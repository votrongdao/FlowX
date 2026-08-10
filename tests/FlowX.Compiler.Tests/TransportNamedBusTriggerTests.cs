using System.Linq;
using FlowX.Compiler.Emit;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// Whether a transport-named bus attribute becomes a registration, and whether the registration
/// carries the family the flow declared.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Three attributes, one binding path, and that is
/// <a href="../../docs/adr/ADR-0004-universal-trigger-model.md">ADR-0004</a> as code rather than
/// as a claim.</strong> <c>FlowPlanGenerator</c> selects a bus subscription on kind and shape —
/// <c>Kind == "Bus"</c> with a topic and a group — and never on which attribute was written. So
/// <c>[KafkaTrigger]</c>, <c>[RabbitMqTrigger]</c> and <c>[ServiceBusTrigger]</c> reach the same
/// emitter through the same predicate, and adding a fourth broker's attribute is a shape in
/// <c>TriggerReader</c> and nothing else.
/// </para>
/// <para>
/// <strong>What the transport is <em>for</em> is the refusal.</strong> It reaches the generated
/// registration, <c>FlowBusCatalog</c> compares it against the <c>IBusConsumer</c> the host
/// registered, and a mismatch is a host that will not start. A value that reached the manifest and
/// not the registration would leave that check comparing nothing — which is why this file asserts
/// the generated source and not only the published document.
/// </para>
/// </remarks>
public sealed class TransportNamedBusTriggerTests
{
    /// <summary>Stands in for the hosting assembly the user's project would reference.</summary>
    private const string HostingStub = """
        namespace FlowX.Hosting
        {
            public static class FlowBusSubscriptionRegistration
            {
            }
        }
        """;

    [Theory]
    [InlineData("KafkaTrigger", "kafka")]
    [InlineData("RabbitMqTrigger", "rabbitmq")]
    [InlineData("ServiceBusTrigger", "azure-servicebus")]
    public void ADeclaredTransportReachesTheRegistrationAndNotOnlyTheManifest(
        string attribute, string transport)
    {
        var generated = SubscriptionsIn(RunOn(Consuming(attribute), HostingStub));

        generated.ShouldNotBeNull("a bus trigger with a topic and a group binds.");
        generated.ShouldContain("AddFlowXSubscriptions");
        generated.ShouldContain("\"order.placed\"");
        generated.ShouldContain("\"pricing\"");
        generated.ShouldContain("global::Sample.PriceOrderFlow.Plan");

        generated.ShouldContain(
            "\"" + transport + "\"",
            customMessage:
                "FlowBusCatalog refuses a host whose consumer answers another family, and it can " +
                "only do that if the declared family reaches the registration.");
    }

    /// <summary>
    /// The transport-neutral declaration registers with none, which is a different statement and
    /// not a missing one.
    /// </summary>
    [Fact]
    public void ABusTriggerNamingNoTransportRegistersNull()
    {
        var generated = SubscriptionsIn(RunOn(Consuming("BusTrigger"), HostingStub));

        generated.ShouldNotBeNull();
        generated.ShouldContain("null");

        generated.ShouldNotContain(
            "\"rabbitmq\"",
            customMessage: "\"whatever bus the host wired\" must not acquire a broker by accident.");
    }

    /// <summary>
    /// Every transport-named attribute binds through the same predicate, so none of them is a
    /// declaration nothing serves.
    /// </summary>
    /// <remarks>
    /// The failure this guards is the one this repository keeps finding: an attribute that
    /// compiles, reaches the manifest and produces no registration at all — a subscription the
    /// application declared and nothing consumes.
    /// </remarks>
    [Theory]
    [InlineData("KafkaTrigger")]
    [InlineData("RabbitMqTrigger")]
    [InlineData("ServiceBusTrigger")]
    public void NoTransportNamedAttributeIsDeclaredAndInert(string attribute)
    {
        SubscriptionsIn(RunOn(Consuming(attribute), HostingStub)).ShouldNotBeNull(
            $"[{attribute}] reaches the manifest; a registration is what makes it run.");
    }

    /// <summary>
    /// A subscription whose flow takes its own contract registers the decode with the
    /// registration, not a raw delivery.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>This is what makes the analyzer relaxation safe.</strong> <c>FLOWX1051</c> lets a
    /// bus trigger name a decoder instead of forcing the flow to be declared over the delivery.
    /// If the emitter then ignored <c>Decode</c>, the scan would seed a delivery into a flow
    /// expecting its own contract and the first step would find nothing under the key it binds —
    /// a compile-time relaxation paid for at run time, which is the worst trade this repository
    /// makes.
    /// </para>
    /// <para>
    /// The generated starter is asserted rather than the decoder's name alone, because the name
    /// appearing in a comment would satisfy a weaker check. What has to be there is the call:
    /// decode, then run the plan on what the decode produced.
    /// </para>
    /// </remarks>
    [Fact]
    public void ASubscriptionNamingADecoderRegistersTheDecodeAndNotTheDelivery()
    {
        var generated = SubscriptionsIn(RunOn(Decoding, HostingStub));

        generated.ShouldNotBeNull(
            "A flow whose trigger names a decoder is consumable — that is what the decoder is "
            + "for. No registration means the delivery reaches nothing.");

        generated.ShouldContain(
            "new global::Sample.ReadOrder().Decode(message)",
            customMessage: "The registration must call the decoder the attribute named.");

        generated.ShouldContain(
            "decoded.Value",
            customMessage:
                "The plan must run on what the decoder produced. Running it on the delivery is " +
                "the defect this whole path exists to prevent.");

        generated.ShouldContain(
            "decoded.IsFailure",
            customMessage:
                "A body that does not parse is a poison message and must become a refusal the " +
                "scan can dead-letter, never an exception out of a delegate.");
    }

    /// <summary>A flow taking its own contract, reached through a named decoder.</summary>
    private const string Decoding = """
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record PlaceOrder(string Sku);
        public sealed record Priced(string Id);

        public sealed class ReadOrder : ITriggerDecoder<BusMessage, PlaceOrder>
        {
            public Result<PlaceOrder> Decode(BusMessage payload) => Result.Ok(new PlaceOrder(payload.Type));
        }

        [Capability("orders.price", Version = "1.0.0", Authorization = Authorization.Internal)]
        public sealed class Price : ICapability<PlaceOrder, Priced>
        {
            public ValueTask<Result<Priced>> ExecuteAsync(
                PlaceOrder input, CapabilityContext ctx, CancellationToken ct) =>
                ValueTask.FromResult(Result.Ok(new Priced(input.Sku)));
        }

        [Flow("orders.price", Version = "1.0.0", Profile = ExecutionProfile.Durable)]
        [BusTrigger("order.placed", Group = "pricing", Decode = typeof(ReadOrder))]
        public sealed partial class PriceOrderFlow : Flow<PlaceOrder, Priced>
        {
            protected override void Define(IFlowBuilder<PlaceOrder, Priced> flow) =>
                flow.Step<Price>().Return(ctx => ctx.Get<Priced>());
        }
        """;

    private static string Consuming(string attribute) => $$"""
        using System.Threading;
        using System.Threading.Tasks;
        using FlowX;

        namespace Sample;

        public sealed record Priced(string Id);

        [Capability("orders.price", Version = "1.0.0", Authorization = Authorization.Internal)]
        public sealed class Price : ICapability<BusMessage, Priced>
        {
            public ValueTask<Result<Priced>> ExecuteAsync(
                BusMessage input, CapabilityContext ctx, CancellationToken ct) =>
                ValueTask.FromResult(Result.Ok(new Priced("p")));
        }

        [Flow("orders.price", Version = "1.0.0", Profile = ExecutionProfile.Durable)]
        [{{attribute}}("order.placed", Group = "pricing")]
        public sealed partial class PriceOrderFlow : Flow<BusMessage, Priced>
        {
            protected override void Define(IFlowBuilder<BusMessage, Priced> flow) =>
                flow.Step<Price>().Return(ctx => ctx.Get<Priced>());
        }
        """;

    private static GeneratorRun RunOn(params string[] sources) => GeneratorHarness.Run(
        GeneratorHarness.CompilationOf(
            [.. sources.Select((source, i) => ($"/src/File{i}.cs", source))]));

    private static string? SubscriptionsIn(GeneratorRun run) => run.Sources
        .Where(static s => s.HintName == BusEmitter.FileName)
        .Select(static s => s.Source)
        .FirstOrDefault();
}

using System.Collections.Generic;
using FlowX.Compiler.Model;

namespace FlowX.Compiler.Emit;

/// <summary>
/// Turns each flow's <c>[BusTrigger]</c> or <c>[KafkaTrigger]</c> into the subscription
/// registration a composition root would otherwise write by hand — or, before this existed, could
/// not write at all.
/// </summary>
/// <remarks>
/// <para>
/// <strong>The arrangement <see cref="EndpointEmitter"/> and <see cref="ScheduleEmitter"/>
/// have.</strong> The emitted file is C# in the <em>user's</em> assembly, calling
/// <c>FlowX.Hosting.FlowBusSubscriptionRegistration.Add</c> by name; this project links against
/// nothing, and the only thing the generator knows about the host is that string, which
/// <see cref="FlowPlanGenerator"/> looks up in the compilation before asking for any of this.
/// </para>
/// <para>
/// <strong>The address is copied off the <see cref="TriggerModel"/> the manifest published,</strong>
/// for <see cref="ScheduleEmitter"/>'s reason and with the same consequence if it were not: the
/// topic and the group are two of the five values every node derives a delivery's instance id
/// from
/// (<a href="https://github.com/votrongdao/FlowX/blob/master/docs/adr/ADR-0035-a-delivery-names-the-instance-it-starts.md">ADR-0035</a>),
/// so two copies would not be a documentation defect but a message that starts a flow twice.
/// </para>
/// <para>
/// <strong>The declared transport is emitted even though nothing here consumes it.</strong> It is
/// checked at registration against the <c>IBusConsumer</c> the host wired, so a
/// <c>[KafkaTrigger]</c> on a Redis-only host is a pod that never becomes ready rather than a
/// subscription served by a broker the manifest does not name.
/// </para>
/// <para>
/// Pure, like <see cref="FlowEmitter"/>: a model in, a string out, no Roslyn.
/// </para>
/// </remarks>
public static class BusEmitter
{
    /// <summary>The file the emitted registrations are written to.</summary>
    public const string FileName = "FlowXSubscriptions.g.cs";

    private const string Provider = "global::System.IServiceProvider";

    private const string Dispatcher = "global::FlowX.Runtime.IStepDispatcher";

    /// <summary>Emits the subscription registrations for every bus-triggered flow.</summary>
    /// <param name="assemblyName">The compilation's assembly name, which names the class.</param>
    /// <param name="subscriptions">The subscriptions to register, in the order they should be added.</param>
    public static string Emit(string assemblyName, IReadOnlyList<BusSubscriptionModel> subscriptions)
    {
        if (subscriptions is null)
        {
            throw new System.ArgumentNullException(nameof(subscriptions));
        }

        var writer = new SourceWriter();

        writer.Line(FlowEmitter.Header.TrimEnd('\n'));
        writer.Line("#nullable enable");
        writer.Line();
        writer.Line("namespace FlowX.Generated");
        writer.OpenBrace();

        EmitClass(writer, ClassNameFor(assemblyName), subscriptions);

        writer.CloseBrace();

        return writer.ToString();
    }

    /// <summary>
    /// The class name for one assembly's subscriptions, e.g. <c>EcommerceSubscriptions</c>.
    /// </summary>
    /// <remarks>
    /// Named from the assembly rather than fixed, for <see cref="EndpointEmitter.ClassNameFor"/>'s
    /// reason: a solution with two flow libraries would otherwise emit two
    /// <c>FlowX.Generated.FlowXSubscriptions</c>, and the application referencing both would get
    /// an ambiguous <c>services.AddFlowXSubscriptions()</c> with no way to disambiguate it.
    /// </remarks>
    /// <param name="assemblyName">The compilation's assembly name.</param>
    public static string ClassNameFor(string assemblyName)
    {
        var identifier = new System.Text.StringBuilder();

        foreach (var character in assemblyName ?? string.Empty)
        {
            if (char.IsLetterOrDigit(character) || character == '_')
            {
                identifier.Append(character);
            }
        }

        if (identifier.Length == 0 || char.IsDigit(identifier[0]))
        {
            return "FlowXSubscriptions";
        }

        return identifier.Append("Subscriptions").ToString();
    }

    private static void EmitClass(
        SourceWriter writer, string className, IReadOnlyList<BusSubscriptionModel> subscriptions)
    {
        writer.Line("/// <summary>The bus subscriptions this application's flows declare.</summary>");
        writer.Line("/// <remarks>");
        writer.Line("/// One method per bus trigger, generated from the same reading of the attribute");
        writer.Line("/// that produced the <c>triggers</c> block of <c>flowx.manifest.json</c>. The topic");
        writer.Line("/// and the group are therefore the manifest's own — there is no second copy of them");
        writer.Line("/// to drift, and both are terms every node derives a delivery's instance id from.");
        writer.Line("/// </remarks>");
        writer.Line("public static class " + className);
        writer.OpenBrace();

        EmitAddAll(writer, subscriptions);

        foreach (var subscription in subscriptions)
        {
            writer.Line();
            EmitSubscription(writer, subscription);
        }

        writer.CloseBrace();
    }

    /// <summary>
    /// Emits <c>AddFlowXSubscriptions</c>: every bus-triggered flow in this application, in one
    /// call.
    /// </summary>
    /// <remarks>
    /// Taken on the built <c>IServiceProvider</c> rather than on <c>IServiceCollection</c>, for
    /// <see cref="ScheduleEmitter"/>'s reason: a registration needs the flow's generated
    /// dispatcher, and that is resolved from the container.
    /// </remarks>
    private static void EmitAddAll(SourceWriter writer, IReadOnlyList<BusSubscriptionModel> subscriptions)
    {
        writer.Line("/// <summary>Registers every flow in this application that declares a bus trigger.</summary>");
        writer.Line("/// <param name=\"services\">The built container.</param>");
        writer.Line("public static " + Provider + " AddFlowXSubscriptions(");
        writer.Line("    this " + Provider + " services)");
        writer.OpenBrace();

        foreach (var subscription in subscriptions)
        {
            writer.Line(subscription.MethodName + "(services);");
        }

        writer.Line();
        writer.Line("return services;");
        writer.CloseBrace();
    }

    private static void EmitSubscription(SourceWriter writer, BusSubscriptionModel subscription)
    {
        var flow = "global::" + subscription.FlowTypeName;

        writer.Line(
            "/// <summary><c>" + Escape(subscription.Topic) + "</c> as <c>" +
            Escape(subscription.Group) + "</c> — runs <c>" + subscription.FlowId + "</c>.</summary>");
        writer.Line("/// <param name=\"services\">The built container.</param>");
        writer.Line("public static " + Provider + " " + subscription.MethodName + "(");
        writer.Line("    this " + Provider + " services) =>");
        writer.Line("    global::FlowX.Hosting.FlowBusSubscriptionRegistration.Add(");
        writer.Line("        services,");
        writer.Line("        " + flow + ".Plan,");
        writer.Line("        static provider => (" + Dispatcher + ")global::Microsoft.Extensions.DependencyInjection");
        writer.Line("            .ServiceProviderServiceExtensions");
        writer.Line("            .GetRequiredService<" + flow + ".Dispatcher>(provider),");
        writer.Line("        " + Quote(subscription.Topic) + ",");
        writer.Line("        " + Quote(subscription.Group) + ",");
        writer.Line(
            "        " +
            (subscription.Transport is null ? "null" : Quote(subscription.Transport)) +
            (subscription.DecoderTypeName is null ? ");" : ","));

        if (subscription.DecoderTypeName is null)
        {
            return;
        }

        // The decode closes over both concrete types here and nowhere else. FlowBusScan is not
        // generic over a flow's input, and FlowHost.RunAsync<TIn> seeds the context under TIn's
        // *static* type — so a delegate handing the value over as object would file the input
        // under a key no step looks up. Generated code is the one place both types are known.
        //
        // A decoder that refuses is a poison message: the result is a rejection carrying its
        // Error, which the scan's own disposition rules dead-letter with a reason an operator
        // can read (ADR-0038). It is never an exception and never a silent acknowledgement.
        writer.Line("        static (host, registration, invocation, message, instanceId, ct) =>");
        writer.Line("        {");
        writer.Line(
            "            var decoded = new global::" + subscription.DecoderTypeName +
            "().Decode(message);");
        writer.Line();
        writer.Line("            return decoded.IsFailure");
        writer.Line(
            "                ? new global::System.Threading.Tasks.ValueTask<global::FlowX.Runtime.FlowExecutionResult>(");
        writer.Line(
            "                    global::FlowX.Runtime.FlowExecutionResult.Rejected(decoded.Error))");
        writer.Line("                : host.RunAsync(");
        writer.Line("                    registration.Flow.Plan,");
        writer.Line("                    registration.Flow.Dispatcher,");
        writer.Line("                    invocation,");
        writer.Line("                    decoded.Value,");
        writer.Line("                    instanceId,");
        writer.Line("                    ct);");
        writer.Line("        });");
    }

    private static string Quote(string value) =>
        "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>Makes a value safe to sit inside an XML doc comment.</summary>
    private static string Escape(string value) => value
        .Replace("&", "&amp;")
        .Replace("<", "&lt;")
        .Replace(">", "&gt;");
}

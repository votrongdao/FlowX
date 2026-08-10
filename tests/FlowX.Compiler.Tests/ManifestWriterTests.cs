using System;
using System.Linq;
using System.Text.Json;
using FlowX.Compiler.Emit;
using FlowX.Compiler.Model;
using Shouldly;
using Xunit;

namespace FlowX.Compiler.Tests;

/// <summary>
/// The manifest is the artifact everything else derives from — OpenAPI, the agent tool
/// surface, <c>flowx graph</c>, impact analysis, <c>flowx diff</c>. These tests defend
/// the two properties that make it usable at all: it validates, and it is byte-stable.
/// </summary>
public sealed class ManifestWriterTests
{
    private static string Write(params FlowModel[] flows) =>
        ManifestWriter.Write("Sample.App", "1.0.0", flows);

    private static JsonDocument Parse(string manifest) => JsonDocument.Parse(manifest);

    [Fact]
    public void EmitsWellFormedJson()
    {
        using var document = Parse(Write(Models.PlaceOrder()));

        document.RootElement.ValueKind.ShouldBe(JsonValueKind.Object);
    }

    [Fact]
    public void CarriesEveryFieldTheSchemaRequiresAtTheTopLevel()
    {
        using var document = Parse(Write(Models.PlaceOrder()));
        var root = document.RootElement;

        foreach (var required in new[] { "schemaVersion", "application", "flows", "capabilities" })
        {
            root.TryGetProperty(required, out _).ShouldBeTrue(
                $"'{required}' is required by schemas/flowx.manifest.schema.json.");
        }

        root.GetProperty("schemaVersion").GetString().ShouldBe("0.1.0");
        root.GetProperty("application").GetProperty("name").GetString().ShouldBe("Sample.App");
    }

    [Fact]
    public void EachCapabilityCarriesEveryFieldTheSchemaRequires()
    {
        using var document = Parse(Write(Models.PlaceOrder()));

        foreach (var capability in document.RootElement.GetProperty("capabilities").EnumerateArray())
        {
            foreach (var required in new[] { "id", "version", "input", "output", "authorization", "idempotent" })
            {
                capability.TryGetProperty(required, out _).ShouldBeTrue(
                    $"Capability entry is missing required field '{required}'.");
            }

            capability.GetProperty("authorization").TryGetProperty("mode", out _).ShouldBeTrue();
        }
    }

    [Fact]
    public void FlowIdsAndCapabilityIdsMatchTheSchemasIdentityPattern()
    {
        using var document = Parse(Write(Models.PlaceOrder(), Models.Minimal()));
        var pattern = new System.Text.RegularExpressions.Regex(@"^[a-z][a-z0-9_]*(\.[a-z][a-z0-9_]*)+$");

        foreach (var flow in document.RootElement.GetProperty("flows").EnumerateArray())
        {
            pattern.IsMatch(flow.GetProperty("id").GetString()!).ShouldBeTrue(
                $"Flow id '{flow.GetProperty("id").GetString()}' does not match the schema's identity pattern.");
        }

        foreach (var capability in document.RootElement.GetProperty("capabilities").EnumerateArray())
        {
            pattern.IsMatch(capability.GetProperty("id").GetString()!).ShouldBeTrue();
        }
    }

    [Fact]
    public void IsByteIdenticalAcrossRuns()
    {
        Write(Models.PlaceOrder()).ShouldBe(Write(Models.PlaceOrder()),
            "Two builds of identical source must produce identical bytes, or `flowx diff` " +
            "reports changes nobody made and people stop reading it.");
    }

    [Fact]
    public void IsIndependentOfTheOrderFlowsAreDiscoveredIn()
    {
        var forwards = Write(Models.PlaceOrder(), Models.Minimal());
        var backwards = Write(Models.Minimal(), Models.PlaceOrder());

        backwards.ShouldBe(forwards,
            "Roslyn does not promise a stable discovery order, so the writer sorts. " +
            "Without that, the manifest would differ between builds for no reason.");
    }

    [Fact]
    public void ContainsNoTimestampOrCommitHash()
    {
        var manifest = Write(Models.PlaceOrder());

        manifest.ShouldNotContainText("builtAt",
            "A build timestamp makes every manifest differ, which destroys `flowx diff`. " +
            "It belongs in the manifest, injected at publish time by the CLI — not baked " +
            "in by the generator.");
        manifest.ShouldNotContainText("commit", "Same reason as builtAt.");
    }

    /// <summary>
    /// <c>ManifestContainsNoSecrets</c>, over every field the writer can emit.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The manifest says a capability accepts a <c>CaptureRequest</c>; it must never say
    /// what was in one. That is what makes the file safe to publish, feed to an agent, or
    /// attach to a build.
    /// </para>
    /// <para>
    /// The document under test carries triggers and an error catalogue as well as steps,
    /// because a guard that only ever sees the fields that existed when it was written
    /// stops guarding the moment a field is added. An error's <em>message</em> is the field
    /// most likely to carry a value — it routinely interpolates one — and the writer never
    /// receives it at all.
    /// </para>
    /// </remarks>
    [Fact]
    public void ContainsStructureButNoValues()
    {
        var manifest = ManifestWriter.Write(
            "Sample.App",
            "1.0.0",
            [
                Models.PlaceOrder(), Models.Conditional(), Models.Switching(),
                Models.Parallel(), Models.Iterating(), Models.Composing(),
            ],
            null,
            [Models.Triggers()],
            Models.ErrorCatalogues());

        foreach (var forbidden in new[]
        {
            "password", "secret", "apikey", "api_key", "token",
            "connectionstring", "bearer", "private_key",
        })
        {
            manifest.ToUpperInvariant().Contains(forbidden.ToUpperInvariant(), StringComparison.Ordinal)
                .ShouldBeFalse($"The manifest contains '{forbidden}'. It describes structure, never values.");
        }

        // Every composite shape is in the document above, because each one carries
        // something the manifest has to refuse: a predicate, a selector and its case
        // values, a merge argument, a collection selector and a concurrency bound, and a
        // sub-flow's input mapping. A guard that saw only the linear flow would pass
        // against a writer that published them all.
        foreach (var expression in new[]
        {
            "ctx =>", "RiskScore", "Channel.Retail", "MergeStrategy",
            "ValidatedOrder", "MaxDegreeOfParallelism", "ContinueOnError",
            "OrderId", "FulfilOrderFlow",
        })
        {
            manifest.Contains(expression, StringComparison.Ordinal).ShouldBeFalse(
                $"The manifest contains '{expression}'. A predicate, a case value, a merge " +
                "argument, an iterated collection, an iteration's bound and a sub-flow's " +
                "input mapping are all things the author wrote about their own data, their " +
                "own tuning or their own class names; the manifest publishes that the flow " +
                "branches, iterates and composes, never on what.");
        }
    }

    /// <summary>
    /// A loop publishes its body as the same <c>branches</c> array every other shape uses.
    /// </summary>
    /// <remarks>
    /// One entry, because a loop has one block. How many times it runs is data, and the
    /// manifest carries none — so <c>kind</c> saying <c>ForEach</c> is the whole of what
    /// distinguishes it from a step that runs once.
    /// </remarks>
    [Fact]
    public void AnIterationPublishesItsBodyAndNothingAboutTheCollection()
    {
        using var document = Parse(Write(Models.Iterating()));

        var loop = document.RootElement.GetProperty("flows")[0].GetProperty("steps")[1];

        loop.GetProperty("kind").GetString().ShouldBe("ForEach");
        loop.GetProperty("id").GetInt32().ShouldBe(1);
        loop.GetProperty("branches").GetArrayLength().ShouldBe(1);
        loop.GetProperty("branches")[0].GetArrayLength().ShouldBe(2);
        loop.GetProperty("branches")[0][0].GetProperty("capability").GetString()
            .ShouldBe("inventory.reserve@1.0.0");
        loop.GetProperty("branches")[0][0].GetProperty("compensation").GetString()
            .ShouldBe("inventory.release@1.0.0",
                "A per-element compensation is published exactly as any other is: the loop " +
                "changes how often it runs, not what it is.");

        loop.TryGetProperty("merge", out _).ShouldBeFalse(
            "A loop has nothing to merge, and borrowing the fork's field would invite a " +
            "consumer to read a join rule that does not exist.");
    }

    /// <summary>
    /// A composition publishes the child's identity and how it relates to the parent, and
    /// nothing else.
    /// </summary>
    /// <remarks>
    /// <para>
    /// No <c>branches</c>, unlike every other composite shape. The child's steps are in the
    /// child's own entry, which is where a change to them belongs: inlining them would make
    /// the parent's document grow with the child's and turn one edit to a shared flow into
    /// a diff in every flow that composes it.
    /// </para>
    /// <para>
    /// No <c>capability</c> either. A composition invokes a flow, and the child's
    /// capabilities are the child's — a parent claiming them would misreport who does what,
    /// which is exactly what a reviewer reads this document to find out.
    /// </para>
    /// </remarks>
    [Fact]
    public void ACompositionPublishesTheChildsIdentityAndTheModeAndNothingElse()
    {
        using var document = Parse(Write(Models.Composing()));

        var composition = document.RootElement.GetProperty("flows")[0].GetProperty("steps")[1];

        composition.GetProperty("kind").GetString().ShouldBe("SubFlow");
        composition.GetProperty("id").GetInt32().ShouldBe(1);
        composition.GetProperty("flow").GetString().ShouldBe("order.fulfil");
        composition.GetProperty("mode").GetString().ShouldBe("Inline");

        composition.TryGetProperty("branches", out _).ShouldBeFalse(
            "The child's steps belong to the child's entry.");
        composition.TryGetProperty("capability", out _).ShouldBeFalse(
            "A composition invokes a flow, not a capability.");
        composition.TryGetProperty("merge", out _).ShouldBeFalse();
    }

    [Fact]
    public void ADetachedCompositionSaysSo()
    {
        // The mode changes what the flow *means* — whether the parent waits, whether the
        // child's failure is the parent's, whose deadline applies — so a reader who cannot
        // see it cannot review the composition at all.
        using var document = Parse(Write(Models.Composing("Detached")));

        document.RootElement.GetProperty("flows")[0].GetProperty("steps")[1]
            .GetProperty("mode").GetString().ShouldBe("Detached");
    }

    [Fact]
    public void AComposedFlowsCapabilitiesDoNotReachTheParentsCatalogue()
    {
        // The mirror of ACapabilityInvokedOnlyInsideABranchStillReachesTheCapabilityList,
        // and the opposite answer for a reason: a branch's steps are this flow's, and a
        // child's are not. `flowx diff` reads this list to decide what a change to a
        // capability affects; a parent that claimed its child's would report an impact it
        // does not have.
        using var document = Parse(Write(Models.Composing()));

        var ids = document.RootElement.GetProperty("capabilities")
            .EnumerateArray()
            .Select(c => c.GetProperty("id").GetString())
            .ToArray();

        ids.ShouldBe(["order.validate", "payment.capture"]);
    }

    [Fact]
    public void ACapabilityInvokedOnlyInsideALoopStillReachesTheCapabilityList()
    {
        // The failure this forbids is silent: a capability the manifest does not list is
        // invisible to `flowx diff`, to generated OpenAPI and to anything auditing what an
        // application can do.
        using var document = Parse(Write(Models.Iterating()));

        document.RootElement.GetProperty("capabilities")
            .EnumerateArray()
            .Select(c => c.GetProperty("id").GetString())
            .ShouldContain("payment.capture");
    }

    // ------------------------------------------------------------------ triggers

    private static string WriteWithTriggers(params TriggerModel[] triggers) => ManifestWriter.Write(
        "Sample.App", "1.0.0", [Models.PlaceOrder()], null,
        [new FlowTriggersModel("order.place", triggers)]);

    [Fact]
    public void TriggersAreSortedRatherThanLeftInDeclarationOrder()
    {
        var http = new TriggerModel("Http", method: "POST", route: "/api/v1/orders");
        var bus = new TriggerModel("Bus", transport: "kafka", topic: "orders.requested");

        WriteWithTriggers(http, bus).ShouldBe(WriteWithTriggers(bus, http),
            "Roslyn promises no attribute order, so a flow with two triggers would otherwise " +
            "differ between builds of identical source.");
    }

    /// <summary>Omitted rather than emitted empty — and unlike <c>errors</c>, on purpose.</summary>
    /// <remarks>
    /// An empty array would read as "this flow cannot be started from outside the process",
    /// which the compiler cannot know: nothing yet turns a trigger attribute into a
    /// registration, so a flow with no attribute may still be serving a hand-written route.
    /// The positive statement is sound; the negative one is not.
    /// </remarks>
    [Fact]
    public void AFlowWithNoDeclaredTriggerCarriesNoTriggersArray()
    {
        using var document = Parse(Write(Models.PlaceOrder()));

        document.RootElement.GetProperty("flows")[0].TryGetProperty("triggers", out _).ShouldBeFalse();
    }

    [Fact]
    public void ATriggerOmitsTheFieldsItsKindDoesNotHave()
    {
        using var document = Parse(WriteWithTriggers(new TriggerModel("Schedule", cron: "0 2 * * *", timeZone: "UTC")));

        var trigger = document.RootElement.GetProperty("flows")[0].GetProperty("triggers")[0];

        trigger.GetProperty("cron").GetString().ShouldBe("0 2 * * *");
        trigger.TryGetProperty("route", out _).ShouldBeFalse();
        trigger.TryGetProperty("idempotent", out _).ShouldBeFalse(
            "A schedule has no notion of an idempotency key, and writing `false` would claim it does.");
    }

    // -------------------------------------------------------------------- errors

    /// <summary>
    /// Three states, three renderings.
    /// </summary>
    /// <remarks>
    /// A capability that returns no declared error publishes an empty array, and one whose
    /// failure paths could not all be resolved publishes nothing. Rendering both as
    /// <c>[]</c> is exactly the ambiguity this field exists to remove: a consumer reading
    /// an empty catalogue is entitled to conclude the capability never fails with a code,
    /// and would be wrong if that were also what an unreadable capability produced.
    /// </remarks>
    [Fact]
    public void AnIncompleteCatalogueIsWithheldWhileAnEmptyOneIsPublished()
    {
        var complete = ManifestWriter.Write(
            "Sample.App", "1.0.0", [Models.PlaceOrder()], null, null,
            [new CapabilityErrorCatalogue("order.validate", "1.0.0", [], isComplete: true)]);

        var incomplete = ManifestWriter.Write(
            "Sample.App", "1.0.0", [Models.PlaceOrder()], null, null,
            [new CapabilityErrorCatalogue("order.validate", "1.0.0", [], isComplete: false)]);

        using var published = Parse(complete);
        using var withheld = Parse(incomplete);

        Capability(published, "order.validate").GetProperty("errors").GetArrayLength().ShouldBe(0);
        Capability(withheld, "order.validate").TryGetProperty("errors", out _).ShouldBeFalse();
    }

    [Fact]
    public void ErrorsAreSortedByCodeAndDeduplicated()
    {
        var manifest = ManifestWriter.Write(
            "Sample.App", "1.0.0", [Models.PlaceOrder()], null, null,
            [
                new CapabilityErrorCatalogue(
                    "order.validate",
                    "1.0.0",
                    [
                        new CapabilityErrorModel("order.too_many", "Validation"),
                        new CapabilityErrorModel("order.invalid_quantity", "Validation"),
                        new CapabilityErrorModel("order.too_many", "Validation"),
                    ],
                    isComplete: true),
            ]);

        using var document = Parse(manifest);

        Capability(document, "order.validate").GetProperty("errors").EnumerateArray()
            .Select(e => e.GetProperty("code").GetString())
            .ShouldBe(["order.invalid_quantity", "order.too_many"]);
    }

    [Fact]
    public void ACapabilityWithNoCatalogueAtAllCarriesNoErrorsArray()
    {
        using var document = Parse(Write(Models.PlaceOrder()));

        Capability(document, "order.validate").TryGetProperty("errors", out _).ShouldBeFalse(
            "Nothing was read about this capability, and an empty array would claim otherwise.");
    }

    private static JsonElement Capability(JsonDocument manifest, string id) =>
        manifest.RootElement.GetProperty("capabilities").EnumerateArray()
            .Single(c => c.GetProperty("id").GetString() == id);

    [Fact]
    public void DeduplicatesACapabilityUsedByTwoFlows()
    {
        var first = new FlowModel(
            "order.place", "1.0.0", "Ephemeral", null, "Sample", "A", "In", "Out",
            [Models.Validate(0)]);

        var second = new FlowModel(
            "order.cancel", "1.0.0", "Ephemeral", null, "Sample", "B", "In", "Out",
            [Models.Validate(0)]);

        using var document = Parse(Write(first, second));

        document.RootElement.GetProperty("capabilities").GetArrayLength().ShouldBe(1,
            "One capability invoked by two flows is one manifest entry.");
        document.RootElement.GetProperty("flows").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public void KeepsTwoVersionsOfTheSameCapabilityApart()
    {
        var v1 = StepModel.Capability(0, "T", "payment.capture", "1.0.0", false);
        var v2 = StepModel.Capability(0, "T", "payment.capture", "2.0.0", false);

        var flow1 = new FlowModel("a.one", "1.0.0", "Ephemeral", null, "S", "A", "In", "Out", [v1]);
        var flow2 = new FlowModel("a.two", "1.0.0", "Ephemeral", null, "S", "B", "In", "Out", [v2]);

        using var document = Parse(Write(flow1, flow2));

        document.RootElement.GetProperty("capabilities").GetArrayLength().ShouldBe(2,
            "Deduplication is by id AND version — a contract change is a different entry.");
    }

    [Fact]
    public void RecordsCompensationAgainstTheStepItUndoes()
    {
        using var document = Parse(Write(Models.PlaceOrder()));

        var steps = document.RootElement.GetProperty("flows")[0].GetProperty("steps");
        var compensable = steps.EnumerateArray().Single(s => s.TryGetProperty("compensation", out _));

        compensable.GetProperty("id").GetInt32().ShouldBe(1);
        compensable.GetProperty("compensation").GetString().ShouldBe("inventory.release@1.0.0");
    }

    [Fact]
    public void ListsTheEventsAFlowEmits()
    {
        using var document = Parse(Write(Models.PlaceOrder()));

        document.RootElement.GetProperty("flows")[0].GetProperty("emits")
            .EnumerateArray().Select(e => e.GetString()).ShouldBe(["order.placed"]);

        document.RootElement.GetProperty("events")[0].GetProperty("type").GetString()
            .ShouldBe("order.placed");
    }

    [Fact]
    public void NamesTheFlowsThatEmitAnEvent()
    {
        // Absence has to mean "nothing emits this", which it cannot while the field is
        // declared in the schema and written by nothing: a reader then cannot tell an
        // unproduced event from a compiler that never looked (ADR-0017 F1).
        using var document = Parse(Write(Models.PlaceOrder()));

        document.RootElement.GetProperty("events")[0].GetProperty("producedBy")
            .EnumerateArray().Select(f => f.GetString()).ShouldBe(["order.place"]);
    }

    [Fact]
    public void EscapesCharactersThatWouldBreakTheJson()
    {
        var awkward = new FlowModel(
            "a.b", "1.0.0", "Ephemeral", null, "N", "T",
            "Sample.Generic<Sample.Item>", "Out",
            [StepModel.Capability(0, @"Ns.With\Backslash", "a.b", "1.0.0", true)],
            declarationLocation: @"C:\src\Flows\Order.cs:12");

        // Parsing is the assertion: a broken escape produces invalid JSON.
        using var document = Parse(Write(awkward));

        document.RootElement.GetProperty("flows")[0].GetProperty("source").GetString()
            .ShouldBe(@"C:\src\Flows\Order.cs:12");
    }

    [Fact]
    public void HandlesAnApplicationWithNoFlows()
    {
        using var document = Parse(ManifestWriter.Write("Empty.App", "1.0.0", []));

        document.RootElement.GetProperty("flows").GetArrayLength().ShouldBe(0);
        document.RootElement.GetProperty("capabilities").GetArrayLength().ShouldBe(0);
    }

    [Fact]
    public void RejectsANullFlowList()
        => Should.Throw<ArgumentNullException>(() => ManifestWriter.Write("A", "1.0.0", null!));

    [Fact]
    public void WritesSensitiveContractMembers()
    {

        using var document = Parse(Write(new FlowModel(
            flowId: "payment.take",
            version: "1.0.0",
            profile: "Ephemeral",
            deadline: null,
            containingNamespace: "Sample",
            typeName: "PaymentFlow",
            inputTypeName: "Sample.Payment",
            outputTypeName: "Sample.Receipt",
            steps: [StepModel.Capability(0, "Take", "payment.take", "1.0.0", isIdempotent: true)],
            sensitiveInputMembers: ["CardToken", "Cvv"],
            sensitiveOutputMembers: [])));

        var input = document.RootElement.GetProperty("flows")[0].GetProperty("input");

        input.GetProperty("sensitive").EnumerateArray()
            .Select(e => e.GetString())
            .ShouldBe(["CardToken", "Cvv"]);

        // Omitted, not empty: a contract with no secrets must serialise exactly as it did
        // before the field existed, or every flow gains a diff.
        document.RootElement.GetProperty("flows")[0].GetProperty("output")
            .TryGetProperty("sensitive", out _).ShouldBeFalse();
    }

    [Fact]
    public void SensitiveMembersSurviveJsonEscaping()
    {
        using var document = Parse(Write(new FlowModel(
            flowId: "payment.take",
            version: "1.0.0",
            profile: "Ephemeral",
            deadline: null,
            containingNamespace: "Sample",
            typeName: "PaymentFlow",
            inputTypeName: "Sample.Payment",
            outputTypeName: "Sample.Receipt",
            steps: [StepModel.Capability(0, "Take", "payment.take", "1.0.0", isIdempotent: true)],
            sensitiveInputMembers: ["Odd\"Name"])));

        document.RootElement.GetProperty("flows")[0]
            .GetProperty("input").GetProperty("sensitive")[0].GetString()
            .ShouldBe("Odd\"Name");
    }

    [Fact]
    public void AConditionalIsPublishedNestedWithItsThenBlockFirst()
    {
        using var document = Parse(Write(Models.Conditional()));

        var condition = document.RootElement.GetProperty("flows")[0].GetProperty("steps")[1];

        condition.GetProperty("kind").GetString().ShouldBe("Condition");
        condition.GetProperty("id").GetInt32().ShouldBe(1);

        var branches = condition.GetProperty("branches");

        branches.GetArrayLength().ShouldBe(2, "The `then` block first, the alternative second.");
        branches[0][0].GetProperty("capability").GetString().ShouldBe("inventory.reserve@1.0.0");
        branches[1][0].GetProperty("capability").GetString().ShouldBe("payment.capture@2.1.0");
    }

    [Fact]
    public void TheJumpThatClosesAThenBlockIsNotPublished()
    {
        // It exists only because the compiled plan is one flat array. Publishing it would
        // invite a consumer to draw an edge that is not part of the declared design — so
        // ids across a conditional are deliberately not contiguous, and index 3 is absent.
        using var document = Parse(Write(Models.Conditional()));

        var steps = document.RootElement.GetProperty("flows")[0].GetProperty("steps");

        var ids = steps.EnumerateArray()
            .SelectMany(step => step.TryGetProperty("branches", out var branches)
                ? branches.EnumerateArray().SelectMany(block => block.EnumerateArray()).Prepend(step)
                : [step])
            .Select(step => step.GetProperty("id").GetInt32())
            .OrderBy(id => id)
            .ToList();

        ids.ShouldBe([0, 1, 2, 4, 5]);
    }

    [Fact]
    public void AConditionalHasNoCapabilityOrEventOfItsOwn()
    {
        using var document = Parse(Write(Models.Conditional()));
        var condition = document.RootElement.GetProperty("flows")[0].GetProperty("steps")[1];

        condition.TryGetProperty("capability", out _).ShouldBeFalse();
        condition.TryGetProperty("event", out _).ShouldBeFalse();
    }

    [Fact]
    public void ACapabilityInvokedOnlyInsideABranchStillReachesTheCapabilityList()
    {
        // Otherwise the manifest would describe an application missing whichever
        // capabilities happen to sit behind a condition — and impact analysis, the agent
        // tool surface and `flowx diff` all read that list.
        using var document = Parse(Write(Models.Conditional()));

        document.RootElement.GetProperty("capabilities").EnumerateArray()
            .Select(c => c.GetProperty("id").GetString())
            .ShouldContain("payment.capture");
    }
}

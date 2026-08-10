namespace FlowX.Compiler.Model;

/// <summary>
/// One flow's bus trigger, reduced to everything the subscription registration needs and nothing
/// else.
/// </summary>
/// <remarks>
/// <para>
/// <strong><see cref="Topic"/>, <see cref="Group"/> and <see cref="Transport"/> are copied off
/// the <see cref="TriggerModel"/> the manifest publishes, not read a second time.</strong> That
/// is the arrangement <see cref="HttpEndpointModel"/> has with a route and
/// <see cref="ScheduleModel"/> has with an expression, and it matters here for
/// <see cref="ScheduleModel"/>'s exact reason: the topic and the group are two of the five values
/// every node derives a delivery's instance id from
/// (<a href="../../../docs/adr/ADR-0035-a-delivery-names-the-instance-it-starts.md">ADR-0035</a>),
/// so a registration carrying a different string from the manifest's would not merely mislead a
/// reader — it would make one message start two flows, one per copy of the address.
/// </para>
/// <para>
/// <strong>Nothing is read from the attribute directly, unlike <see cref="ScheduleModel"/>'s
/// <c>MissedFire</c>.</strong> <c>KafkaTriggerAttribute</c>'s <c>MaxInFlight</c> and
/// <c>DeadLetter</c> are the two candidates, and both are tuning that <c>FlowXOptions</c> now
/// owns
/// (<a href="../../../docs/adr/ADR-0039-a-bus-subscription-publishes-no-new-manifest-field.md">ADR-0039</a>).
/// A subscription is therefore <em>entirely</em> what the manifest published, which is the
/// cleanest form of the copy rule any of the three bound kinds has.
/// </para>
/// </remarks>
public sealed class BusSubscriptionModel
{
    /// <summary>Creates a model of one flow's subscription.</summary>
    /// <param name="flowId">The flow's business id, e.g. <c>pricing.reprice</c>.</param>
    /// <param name="flowTypeName">The flow's fully qualified type name.</param>
    /// <param name="methodName">The C# name of the generated extension method.</param>
    /// <param name="topic">The topic, exactly as the manifest states it.</param>
    /// <param name="group">The consumer group, exactly as the manifest states it.</param>
    /// <param name="transport">The broker family, or null when the attribute names none.</param>
    /// <param name="decoderTypeName">
    /// The type translating a delivery into the flow's input, or null when the flow takes the
    /// delivery itself.
    /// </param>
    /// <param name="inputTypeName">The flow's input contract, which the decoder must produce.</param>
    public BusSubscriptionModel(
        string flowId,
        string flowTypeName,
        string methodName,
        string topic,
        string group,
        string? transport,
        string? decoderTypeName = null,
        string? inputTypeName = null)
    {
        FlowId = flowId;
        FlowTypeName = flowTypeName;
        MethodName = methodName;
        Topic = topic;
        Group = group;
        Transport = transport;
        DecoderTypeName = decoderTypeName;
        InputTypeName = inputTypeName;
    }

    /// <summary>The flow's business id.</summary>
    public string FlowId { get; }

    /// <summary>The flow's fully qualified type name.</summary>
    public string FlowTypeName { get; }

    /// <summary>The C# name of the generated extension method, e.g. <c>AddRepriceFlowSubscription</c>.</summary>
    public string MethodName { get; }

    /// <summary>The topic, exactly as the manifest states it.</summary>
    public string Topic { get; }

    /// <summary>The consumer group, exactly as the manifest states it.</summary>
    public string Group { get; }

    /// <summary>The broker family, or null when the declaration names none.</summary>
    public string? Transport { get; }

    /// <summary>The decoder's fully qualified type, or null when the flow takes the delivery.</summary>
    public string? DecoderTypeName { get; }

    /// <summary>The flow's input contract, or null when no decoder is named.</summary>
    public string? InputTypeName { get; }
}

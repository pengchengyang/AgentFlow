// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="BroadcastTopic.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;

namespace BroadcastKit.Contracts;

/// <summary>
/// Shared topic names used by the broadcast demo. Only topics with a real publisher and
/// subscriber belong here; remove unused topics together with their demo.
/// </summary>
public static class BroadcastTopics
{
    /// <summary>GUI -> Node.Broadcast.Demo. Payload: <see cref="BroadcastTopicMessage"/>.</summary>
    public const string GuiToNode = "broadcast.demo.gui-to-node";

    /// <summary>Node.Broadcast.Demo -> GUI. Payload: <see cref="BroadcastTopicMessage"/>.</summary>
    public const string NodeToGui = "broadcast.demo.node-to-gui";
}

/// <summary>
/// Shared broadcast envelope carried inside <c>BroadcastKit.TopicMessage.Payload</c>.
/// <see cref="Payload"/> and <see cref="Sample"/> are peers: either one may be sent alone,
/// or both together. TopicBus itself stays generic and does not reference Sample.
/// </summary>
public sealed record BroadcastTopicMessage
{
    public BroadcastTopicMessage(string? payload = null, Sample? sample = null)
    {
        Payload = payload;
        Sample = sample;
    }

    public string? Payload { get; init; }
    public Sample? Sample { get; init; }

    public static BroadcastTopicMessage FromPayload(string payload) => new(payload: payload);
    public static BroadcastTopicMessage FromSample(Sample sample) => new(sample: sample);
    public static BroadcastTopicMessage FromBoth(string payload, Sample sample) => new(payload, sample);

    public bool IsEmpty => Payload is null && Sample is null;
}

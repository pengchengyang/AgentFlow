namespace AgentFlow.Broadcast;

/// <summary>A broadcast message sent through <see cref="BroadcastHub"/>.</summary>
public sealed record BroadcastMessage(
    string Topic,
    object? Payload,
    object? Sender,
    DateTimeOffset Timestamp);

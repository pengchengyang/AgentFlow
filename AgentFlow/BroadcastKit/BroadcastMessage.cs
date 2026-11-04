// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="BroadcastMessage.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace BroadcastKit;

/// <summary>
/// A single broadcast/publish message. <see cref="MessageId"/> identifies the message (dedupe /
/// correlation); <see cref="TargetId"/> identifies the intended subscriber and is null for a
/// broadcast.
/// </summary>
public sealed record BroadcastMessage
{
    public required Guid MessageId { get; init; }
    public required Guid SenderId { get; init; }

    /// <summary>null = broadcast; otherwise only the subscriber with this id receives it.</summary>
    public Guid? TargetId { get; init; }

    public required string Kind { get; init; }
    public object? Payload { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public Guid? CorrelationId { get; init; }
    public long Sequence { get; init; }

    public bool IsFor(Guid subscriberId) =>
        TargetId is null || TargetId == subscriberId;
}

/// <summary>Well-known kinds used by BroadcastKit hosts. Callers may use their own strings.</summary>
public static class BroadcastKinds
{
    public const string Event = "event";
    public const string Command = "command";
    public const string Ack = "ack";
    public const string Log = "log";
    public const string Progress = "progress";
    public const string State = "state";
}

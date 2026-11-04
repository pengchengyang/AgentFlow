// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="TopicErrors.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace BroadcastKit;

public enum TopicErrorKind
{
    HandlerFailed = 0,
    MessageDropped = 1,
    QueueFull = 2,
    SubscriptionStopped = 3
}

public sealed class TopicErrorEventArgs : EventArgs
{
    public required TopicErrorKind Kind { get; init; }
    public Guid? SubscriptionId { get; init; }
    public string? Topic { get; init; }
    public TopicMessage? Message { get; init; }
    public Exception? Exception { get; init; }
    public string? Detail { get; init; }
}

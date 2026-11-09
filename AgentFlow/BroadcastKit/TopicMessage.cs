// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="TopicMessage.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace BroadcastKit;

/// <summary>
/// A topic-addressed, process-local message. Callers only need the topic string; there is no
/// target id and no sender identity (both are intentionally absent from routing).
/// </summary>
public sealed record TopicMessage
{
    public required Guid MessageId { get; init; }
    public required string Topic { get; init; }
    public object? Payload { get; init; }
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;
    public Guid? CorrelationId { get; init; }
    public string? ReplyTo { get; init; }
    public long Sequence { get; init; }
}

/// <summary>Minimal topic bus contract. Hosts and plugins may share this type.</summary>
public interface ITopicBus
{
    /// <summary>Subscribe to one exact topic. The returned handle removes the subscription.</summary>
    ITopicSubscription Subscribe(string topic, Func<TopicMessage, Task> handler);

    /// <summary>Publish to every subscriber of the topic. No subscriber means a no-op.</summary>
    Task PublishAsync(
        string topic,
        object? payload = null,
        Guid? correlationId = null,
        CancellationToken ct = default);

    /// <summary>
    /// Publish a request and wait for the first correlated reply on <paramref name="replyTopic"/>.
    /// A temporary reply subscription is active before the request is published.
    /// </summary>
    Task<TopicMessage> RequestAsync(
        string requestTopic,
        string replyTopic,
        object? payload = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default);

    /// <summary>Reply to a request using its <see cref="TopicMessage.ReplyTo"/> and correlation id.</summary>
    Task ReplyAsync(
        TopicMessage request,
        object? payload = null,
        CancellationToken ct = default);
}

/// <summary>Handle returned by <see cref="ITopicBus.Subscribe"/>.</summary>
public interface ITopicSubscription : IDisposable
{
    Guid Id { get; }
    string Topic { get; }
    bool IsActive { get; }
}

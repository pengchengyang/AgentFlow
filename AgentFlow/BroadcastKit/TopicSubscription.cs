// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="TopicSubscription.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace BroadcastKit;

public sealed class TopicSubscription : ITopicSubscription
{
    private readonly TopicBus _owner;
    private readonly Guid _id;
    private readonly string _topic;
    private readonly long _generation;
    private int _disposed;

    internal TopicSubscription(TopicBus owner, Guid id, string topic, long generation)
    {
        _owner = owner;
        _id = id;
        _topic = topic;
        _generation = generation;
    }

    public Guid Id => _id;
    public string Topic => _topic;

    public bool IsActive =>
        Volatile.Read(ref _disposed) == 0 &&
        _owner.IsRegistered(_topic, _id, _generation);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _owner.UnsubscribeCore(_topic, _id, _generation);
    }
}

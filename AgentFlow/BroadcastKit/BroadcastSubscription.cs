// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="BroadcastSubscription.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace BroadcastKit;

/// <summary>Handle returned by <see cref="BroadcastBus.Register"/>; disposing it unregisters.</summary>
public interface IBroadcastSubscription : IDisposable
{
    Guid Id { get; }
    bool IsActive { get; }
}

public sealed class BroadcastSubscription : IBroadcastSubscription
{
    private readonly BroadcastBus _owner;
    private readonly Guid _id;
    private readonly long _generation;
    private int _disposed;

    internal BroadcastSubscription(BroadcastBus owner, Guid id, long generation)
    {
        _owner = owner;
        _id = id;
        _generation = generation;
    }

    public Guid Id => _id;

    public bool IsActive =>
        Volatile.Read(ref _disposed) == 0 &&
        _owner.IsRegistered(_id, _generation);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
            return;

        _owner.UnregisterCore(_id, _generation);
    }
}

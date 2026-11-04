// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="BroadcastOptions.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace BroadcastKit;

/// <summary>Lifecycle state of a <see cref="BroadcastBus"/>.</summary>
public enum BroadcastState
{
    Created = 0,
    Running = 1,
    Disposing = 2,
    Disposed = 3
}

/// <summary>What to do when a subscriber's bounded queue is full.</summary>
public enum QueueFullMode
{
    /// <summary>Drop the oldest queued message and keep the latest (default for state/telemetry).</summary>
    DropOldest = 0,

    /// <summary>Drop the incoming message.</summary>
    DropNewest = 1,

    /// <summary>Wait for space; can block the sender (avoid for UI telemetry).</summary>
    Block = 2
}

/// <summary>Configuration for a <see cref="BroadcastBus"/> instance.</summary>
public sealed record BroadcastOptions
{
    /// <summary>Capacity of each subscriber queue; must be greater than zero.</summary>
    public int SubscriberCapacity { get; init; } = 1024;

    /// <summary>Queue-full policy.</summary>
    public QueueFullMode QueueFullMode { get; init; } = QueueFullMode.DropOldest;

    /// <summary>Throw when a targeted message has no registered subscriber.</summary>
    public bool ThrowOnUnregisteredTarget { get; init; } = true;

    /// <summary>Allow registering a second handler under the same id; the old one is replaced.</summary>
    public bool AllowDuplicateRegistration { get; init; }

    /// <summary>Allow a message whose sender and target are the same id.</summary>
    public bool AllowSelfTarget { get; init; } = true;

    /// <summary>Stop the subscription after its handler throws, instead of only reporting the error.</summary>
    public bool StopSubscriptionOnHandlerError { get; init; }

    /// <summary>Diagnostic name.</summary>
    public string Name { get; init; } = "BroadcastKit";

    public static BroadcastOptions Default { get; } = new();
}

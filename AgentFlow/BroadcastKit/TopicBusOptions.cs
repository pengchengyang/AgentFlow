// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="TopicBusOptions.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace BroadcastKit;

/// <summary>Configuration for a <see cref="TopicBus"/>.</summary>
public sealed record TopicBusOptions
{
    /// <summary>Capacity of each subscriber queue; must be greater than zero.</summary>
    public int SubscriberCapacity { get; init; } = 1024;

    /// <summary>Queue-full policy for each subscriber.</summary>
    public QueueFullMode QueueFullMode { get; init; } = QueueFullMode.DropOldest;

    /// <summary>Stop a subscription when its handler throws.</summary>
    public bool StopSubscriptionOnHandlerError { get; init; }

    /// <summary>Diagnostic name.</summary>
    public string Name { get; init; } = "TopicBus";

    public static TopicBusOptions Default { get; } = new();
}

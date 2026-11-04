// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="BroadcastErrors.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace BroadcastKit;

public enum BroadcastErrorKind
{
    HandlerFailed = 0,
    MessageDropped = 1,
    QueueFull = 2,
    TargetNotRegistered = 3,
    SubscriptionStopped = 4
}

public sealed class BroadcastErrorEventArgs : EventArgs
{
    public required BroadcastErrorKind Kind { get; init; }
    public Guid? SubscriberId { get; init; }
    public BroadcastMessage? Message { get; init; }
    public Exception? Exception { get; init; }
    public string? Detail { get; init; }
}

public class BroadcastException : Exception
{
    public BroadcastException(string message) : base(message) { }
    public BroadcastException(string message, Exception inner) : base(message, inner) { }
}

public sealed class BroadcastNotInitializedException : BroadcastException
{
    public BroadcastNotInitializedException()
        : base("BroadcastBus is not initialized. Call Initialize first.") { }
}

public sealed class BroadcastDisposedException : BroadcastException
{
    public BroadcastDisposedException()
        : base("BroadcastBus has been disposed.") { }
}

public sealed class DuplicateRegistrationException : BroadcastException
{
    public Guid SubscriberId { get; }

    public DuplicateRegistrationException(Guid subscriberId)
        : base($"Subscriber '{subscriberId}' is already registered.")
    {
        SubscriberId = subscriberId;
    }
}

public sealed class TargetNotRegisteredException : BroadcastException
{
    public Guid TargetId { get; }

    public TargetNotRegisteredException(Guid targetId)
        : base($"Target subscriber '{targetId}' is not registered.")
    {
        TargetId = targetId;
    }
}

public sealed class SelfTargetNotAllowedException : BroadcastException
{
    public SelfTargetNotAllowedException(Guid id)
        : base($"Sender and target are the same subscriber '{id}'.") { }
}

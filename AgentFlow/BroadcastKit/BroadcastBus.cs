// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="BroadcastBus.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.Concurrent;
using System.Threading.Channels;

namespace BroadcastKit;

/// <summary>
/// Process-wide in-memory broadcast/publish bus. Handlers are registered by a <see cref="Guid"/>
/// and every registration owns a bounded queue drained by a single pump task, so a slow or
/// throwing handler cannot block a sender or another subscriber.
/// </summary>
public sealed class BroadcastBus : IAsyncDisposable
{
    /// <summary>Process-wide singleton used by hosts. Tests may create their own instance.</summary>
    public static BroadcastBus Instance { get; } = new();

    private readonly ConcurrentDictionary<Guid, Subscriber> _subscribers = new();
    private readonly ConcurrentDictionary<long, Task> _pumps = new();
    private readonly object _registrationGate = new();
    private readonly object _stateGate = new();

    private BroadcastOptions _options = BroadcastOptions.Default;
    private int _state = (int)BroadcastState.Created;
    private long _generation;
    private long _sequence;
    private int _disposeStarted;

    public BroadcastBus() { }

    public BroadcastState State => (BroadcastState)Volatile.Read(ref _state);
    public BroadcastOptions Options => _options;
    public int SubscriberCount => _subscribers.Count;
    public IReadOnlyCollection<Guid> SubscriberIds => _subscribers.Keys.ToArray();

    /// <summary>Raised for handler failures / dropped messages. Handler exceptions are never rethrown to senders.</summary>
    public event EventHandler<BroadcastErrorEventArgs>? Error;

    /// <summary>Initialize once. Repeated calls are no-ops; a disposed bus cannot be reused.</summary>
    public void Initialize(BroadcastOptions? options = null)
    {
        var candidate = options ?? BroadcastOptions.Default;
        ValidateOptions(candidate);

        lock (_stateGate)
        {
            switch (State)
            {
                case BroadcastState.Running:
                    return;
                case BroadcastState.Disposing:
                case BroadcastState.Disposed:
                    throw new BroadcastDisposedException();
            }

            _options = candidate;
            Volatile.Write(ref _state, (int)BroadcastState.Running);
        }
    }

    public bool IsRegistered(Guid id) => _subscribers.ContainsKey(id);

    internal bool IsRegistered(Guid id, long generation) =>
        _subscribers.TryGetValue(id, out var subscriber) &&
        subscriber.Generation == generation;

    /// <summary>Register a handler under an id (the routing address).</summary>
    public IBroadcastSubscription Register(Guid id, Func<BroadcastMessage, Task> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        EnsureRunning();

        Subscriber subscriber;
        Subscriber? replaced = null;

        lock (_registrationGate)
        {
            if (_subscribers.TryGetValue(id, out var existing))
            {
                if (!_options.AllowDuplicateRegistration)
                    throw new DuplicateRegistrationException(id);

                _subscribers.TryRemove(id, out _);
                replaced = existing;
            }

            var generation = Interlocked.Increment(ref _generation);
            var queue = Channel.CreateBounded<BroadcastMessage>(
                new BoundedChannelOptions(_options.SubscriberCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false
                });

            subscriber = new Subscriber
            {
                Id = id,
                Generation = generation,
                Handler = handler,
                Queue = queue,
                Cancellation = new CancellationTokenSource()
            };

            _subscribers[id] = subscriber;
            subscriber.PumpTask = Task.Run(() => PumpAsync(subscriber));
            _pumps[generation] = subscriber.PumpTask;
        }

        if (replaced is not null)
            Stop(replaced);

        return new BroadcastSubscription(this, id, subscriber.Generation);
    }

    /// <summary>Remove a registration by id. Idempotent.</summary>
    public bool Unregister(Guid id)
    {
        Subscriber? subscriber;
        lock (_registrationGate)
        {
            if (!_subscribers.TryRemove(id, out subscriber))
                return false;
        }

        Stop(subscriber);
        return true;
    }

    /// <summary>Remove a registration and wait until its pump task exits.</summary>
    public async Task<bool> UnregisterAsync(Guid id)
    {
        Subscriber? subscriber;
        lock (_registrationGate)
        {
            if (!_subscribers.TryRemove(id, out subscriber))
                return false;
        }

        if (subscriber is null)
            return false;

        Stop(subscriber);
        await AwaitPumpAsync(subscriber).ConfigureAwait(false);
        return true;
    }

    /// <summary>Create a message with a fresh id, sequence and timestamp.</summary>
    public BroadcastMessage CreateMessage(
        Guid senderId,
        Guid? targetId,
        string kind,
        object? payload = null,
        Guid? correlationId = null)
    {
        EnsureRunning();
        var sequence = Interlocked.Increment(ref _sequence);
        return new BroadcastMessage
        {
            MessageId = Guid.NewGuid(),
            SenderId = senderId,
            TargetId = targetId,
            Kind = kind,
            Payload = payload,
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = correlationId,
            Sequence = sequence
        };
    }

    /// <summary>Send a targeted message (the message must have a non-null target).</summary>
    public Task SendAsync(BroadcastMessage message, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        EnsureRunning();

        var targetId = message.TargetId
            ?? throw new ArgumentException(
                "TargetId is required for SendAsync; use BroadcastAsync for broadcasts.", nameof(message));

        if (!_options.AllowSelfTarget && targetId == message.SenderId)
            throw new SelfTargetNotAllowedException(targetId);

        if (!_subscribers.TryGetValue(targetId, out var subscriber))
        {
            if (_options.ThrowOnUnregisteredTarget)
                throw new TargetNotRegisteredException(targetId);

            Report(BroadcastErrorKind.TargetNotRegistered, null, message,
                null, $"No subscriber registered for {targetId}.");
            return Task.CompletedTask;
        }

        return EnqueueAsync(subscriber, message, ct);
    }

    /// <summary>Convenience overload: build and send a targeted message.</summary>
    public Task SendAsync(
        Guid senderId,
        Guid targetId,
        string kind,
        object? payload = null,
        Guid? correlationId = null,
        CancellationToken ct = default)
        => SendAsync(CreateMessage(senderId, targetId, kind, payload, correlationId), ct);

    /// <summary>Send to one id only when it is registered; returns false instead of throwing.</summary>
    public async Task<bool> TrySendAsync(
        Guid senderId,
        Guid targetId,
        string kind,
        object? payload = null,
        Guid? correlationId = null,
        CancellationToken ct = default)
    {
        EnsureRunning();
        if (!_options.AllowSelfTarget && targetId == senderId)
            return false;

        if (!_subscribers.TryGetValue(targetId, out var subscriber))
            return false;

        return await EnqueueAsync(
            subscriber,
            CreateMessage(senderId, targetId, kind, payload, correlationId),
            ct).ConfigureAwait(false);
    }

    /// <summary>Broadcast to every active subscriber except the sender (or <paramref name="exceptId"/>).</summary>
    public async Task BroadcastAsync(
        BroadcastMessage message,
        Guid? exceptId = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        EnsureRunning();

        var normalized = message.TargetId is null ? message : message with { TargetId = null };
        var excluded = exceptId ?? normalized.SenderId;

        foreach (var entry in _subscribers.ToArray())
        {
            if (entry.Key == excluded)
                continue;

            var subscriber = entry.Value;
            if (!subscriber.Active)
                continue;

            try
            {
                await EnqueueAsync(subscriber, normalized, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Report(BroadcastErrorKind.HandlerFailed, subscriber, normalized, ex,
                    "Broadcast enqueue failed.");
            }
        }
    }

    /// <summary>Convenience overload: build and broadcast a message.</summary>
    public Task BroadcastAsync(
        Guid senderId,
        string kind,
        object? payload = null,
        Guid? exceptId = null,
        Guid? correlationId = null,
        CancellationToken ct = default)
        => BroadcastAsync(
            CreateMessage(senderId, null, kind, payload, correlationId),
            exceptId,
            ct);

    /// <summary>Stop every subscription, drain pump tasks and mark the bus disposed.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) == 1)
            return;

        Subscriber[] subscribers;
        lock (_stateGate)
        {
            if (State == BroadcastState.Disposed)
                return;

            Volatile.Write(ref _state, (int)BroadcastState.Disposing);
        }

        lock (_registrationGate)
        {
            subscribers = _subscribers.Values.ToArray();
            _subscribers.Clear();
        }

        foreach (var subscriber in subscribers)
            Stop(subscriber);

        var tasks = new HashSet<Task>(_pumps.Values);
        foreach (var subscriber in subscribers)
            tasks.Add(subscriber.PumpTask);

        try
        {
            await Task.WhenAll(tasks).ConfigureAwait(false);
        }
        catch
        {
            // Pumps already catch their own exceptions; ignore stragglers.
        }

        lock (_stateGate)
        {
            Volatile.Write(ref _state, (int)BroadcastState.Disposed);
        }
    }

    internal void UnregisterCore(Guid id, long generation)
    {
        Subscriber? subscriber;
        lock (_registrationGate)
        {
            if (!_subscribers.TryGetValue(id, out subscriber) ||
                subscriber.Generation != generation)
                return;

            _subscribers.TryRemove(id, out _);
        }

        if (subscriber is not null)
            Stop(subscriber);
    }

    private void EnsureRunning()
    {
        switch (State)
        {
            case BroadcastState.Running:
                return;
            case BroadcastState.Created:
                throw new BroadcastNotInitializedException();
            default:
                throw new BroadcastDisposedException();
        }
    }

    private static void ValidateOptions(BroadcastOptions options)
    {
        if (options.SubscriberCapacity <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(options), options.SubscriberCapacity,
                "SubscriberCapacity must be greater than zero.");

        if (!Enum.IsDefined(options.QueueFullMode))
            throw new ArgumentOutOfRangeException(
                nameof(options), options.QueueFullMode, "Unknown QueueFullMode.");
    }

    private async Task<bool> EnqueueAsync(
        Subscriber subscriber,
        BroadcastMessage message,
        CancellationToken ct)
    {
        if (!subscriber.Active)
        {
            Report(BroadcastErrorKind.SubscriptionStopped, subscriber, message, null,
                "Subscription is no longer active.");
            return false;
        }

        switch (_options.QueueFullMode)
        {
            case QueueFullMode.Block:
                try
                {
                    await subscriber.Queue.Writer.WriteAsync(message, ct).ConfigureAwait(false);
                    return true;
                }
                catch (ChannelClosedException)
                {
                    Report(BroadcastErrorKind.SubscriptionStopped, subscriber, message, null,
                        "Queue was completed before the message could be enqueued.");
                    return false;
                }

            case QueueFullMode.DropNewest:
                if (subscriber.Queue.Writer.TryWrite(message))
                    return true;

                Report(BroadcastErrorKind.QueueFull, subscriber, message, null,
                    "Queue is full; incoming message dropped.");
                return false;

            case QueueFullMode.DropOldest:
            default:
                if (subscriber.Queue.Writer.TryWrite(message))
                    return true;

                var dropped = 0;
                var written = false;
                while (dropped < 64)
                {
                    if (!subscriber.Queue.Reader.TryRead(out _))
                        break;

                    dropped++;
                    if (subscriber.Queue.Writer.TryWrite(message))
                    {
                        written = true;
                        break;
                    }
                }

                if (written)
                {
                    Report(BroadcastErrorKind.MessageDropped, subscriber, message, null,
                        $"Queue was full; dropped {dropped} oldest message(s).");
                    return true;
                }

                Report(BroadcastErrorKind.QueueFull, subscriber, message, null,
                    "Queue is full; incoming message dropped.");
                return false;
        }
    }

    private async Task PumpAsync(Subscriber subscriber)
    {
        try
        {
            await foreach (var message in
                subscriber.Queue.Reader.ReadAllAsync(subscriber.Cancellation.Token)
                    .ConfigureAwait(false))
            {
                if (!subscriber.Active)
                    break;

                try
                {
                    await subscriber.Handler(message).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (subscriber.Cancellation.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Report(BroadcastErrorKind.HandlerFailed, subscriber, message, ex,
                        "Subscriber handler threw.");

                    if (_options.StopSubscriptionOnHandlerError)
                    {
                        subscriber.Active = false;
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Normal stop.
        }
        catch (Exception ex)
        {
            Report(BroadcastErrorKind.HandlerFailed, subscriber, null, ex,
                "Pump failed.");
        }
        finally
        {
            subscriber.Active = false;
            try { subscriber.Queue.Writer.TryComplete(); } catch { /* already completed */ }
            _pumps.TryRemove(subscriber.Generation, out _);

            if (_subscribers.TryGetValue(subscriber.Id, out var current) &&
                current.Generation == subscriber.Generation)
            {
                _subscribers.TryRemove(subscriber.Id, out _);
            }

            try { subscriber.Cancellation.Dispose(); } catch { /* already disposed */ }
        }
    }

    private async Task AwaitPumpAsync(Subscriber subscriber)
    {
        try
        {
            await subscriber.PumpTask.ConfigureAwait(false);
        }
        catch
        {
            // Pumps catch themselves; never surface a stop error to callers.
        }
    }

    private static void Stop(Subscriber subscriber)
    {
        subscriber.Active = false;
        try { subscriber.Cancellation.Cancel(); } catch { /* already disposed */ }
        try { subscriber.Queue.Writer.TryComplete(); } catch { /* already completed */ }
    }

    private void Report(
        BroadcastErrorKind kind,
        Subscriber? subscriber,
        BroadcastMessage? message,
        Exception? exception,
        string? detail)
    {
        try
        {
            Error?.Invoke(this, new BroadcastErrorEventArgs
            {
                Kind = kind,
                SubscriberId = subscriber?.Id,
                Message = message,
                Exception = exception,
                Detail = detail
            });
        }
        catch
        {
            // A failing error observer must not break the pump or sender.
        }
    }
}

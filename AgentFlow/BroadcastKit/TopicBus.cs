// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="TopicBus.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.Concurrent;
using System.Threading.Channels;

namespace BroadcastKit;

/// <summary>
/// Process-wide, topic-addressed publish/subscribe bus. Routing is by exact topic string only;
/// there is no node id, target id or sender identity in the API. Every subscriber gets its own
/// bounded queue and pump, so a slow or throwing handler cannot block another subscriber.
/// </summary>
public sealed class TopicBus : ITopicBus, IAsyncDisposable
{
    private static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(5);

    /// <summary>Process-wide default instance; shared by hosts/plugins that load this assembly together.</summary>
    public static TopicBus Instance { get; } = new();

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, TopicSubscriber>> _topics =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<Guid, PendingRequest> _pendingRequests = new();
    private readonly ConcurrentDictionary<long, Task> _pumps = new();
    private readonly object _gate = new();
    private readonly object _stateGate = new();

    private TopicBusOptions _options = TopicBusOptions.Default;
    private int _state = (int)BroadcastState.Created;
    private long _generation;
    private long _sequence;
    private int _disposeStarted;

    public TopicBus() { }

    public BroadcastState State => (BroadcastState)Volatile.Read(ref _state);
    public TopicBusOptions Options => _options;
    public int SubscriberCount => _topics.Values.Sum(topic => topic.Count);
    public int PendingRequestCount => _pendingRequests.Count;
    public IReadOnlyCollection<string> Topics => _topics.Keys.ToArray();

    /// <summary>Raised for handler failures or dropped messages; sender callers never see handler errors.</summary>
    public event EventHandler<TopicErrorEventArgs>? Error;

    /// <summary>Initialize explicitly, or let the first Subscribe/Publish auto-initialize with defaults.</summary>
    public void Initialize(TopicBusOptions? options = null)
    {
        var candidate = options ?? TopicBusOptions.Default;
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

    public bool IsSubscribed(string topic) =>
        _topics.TryGetValue(topic, out var subscribers) && subscribers.Count > 0;

    internal bool IsRegistered(string topic, Guid id, long generation) =>
        _topics.TryGetValue(topic, out var subscribers) &&
        subscribers.TryGetValue(id, out var subscriber) &&
        subscriber.Generation == generation;

    /// <summary>Subscribe to one exact topic.</summary>
    public ITopicSubscription Subscribe(string topic, Func<TopicMessage, Task> handler)
    {
        ValidateTopic(topic);
        ArgumentNullException.ThrowIfNull(handler);
        EnsureRunning();

        TopicSubscriber subscriber;
        lock (_gate)
        {
            if (State != BroadcastState.Running)
                throw new BroadcastDisposedException();

            var generation = Interlocked.Increment(ref _generation);
            var queue = Channel.CreateBounded<TopicMessage>(
                new BoundedChannelOptions(_options.SubscriberCapacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = false
                });

            subscriber = new TopicSubscriber
            {
                Id = Guid.NewGuid(),
                Topic = topic,
                Generation = generation,
                Handler = handler,
                Queue = queue,
                Cancellation = new CancellationTokenSource()
            };

            var topicSubscribers = _topics.GetOrAdd(
                topic,
                _ => new ConcurrentDictionary<Guid, TopicSubscriber>());
            topicSubscribers[subscriber.Id] = subscriber;

            subscriber.PumpTask = Task.Run(() => PumpAsync(subscriber));
            _pumps[generation] = subscriber.PumpTask;
        }

        return new TopicSubscription(this, subscriber.Id, topic, subscriber.Generation);
    }

    /// <summary>Publish to every subscriber of the topic. No subscriber is a no-op.</summary>
    public async Task PublishAsync(
        string topic,
        object? payload = null,
        Guid? correlationId = null,
        CancellationToken ct = default)
    {
        await PublishCoreAsync(topic, payload, correlationId, replyTo: null, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Publish a request and wait for the first correlated reply on <paramref name="replyTopic"/>.
    /// The temporary reply subscription is registered before the request is published, so a
    /// fast responder cannot race ahead of the requester.
    /// </summary>
    public async Task<TopicMessage> RequestAsync(
        string requestTopic,
        string replyTopic,
        object? payload = null,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        ValidateTopic(requestTopic);
        ValidateTopic(replyTopic);

        var effectiveTimeout = timeout ?? DefaultRequestTimeout;
        if (effectiveTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(timeout),
                effectiveTimeout,
                "Request timeout must be greater than zero.");
        }

        ct.ThrowIfCancellationRequested();
        EnsureRunning();

        var correlationId = Guid.NewGuid();
        var completion = new TaskCompletionSource<TopicMessage>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingRequest
        {
            ReplyTopic = replyTopic,
            Completion = completion
        };

        if (!_pendingRequests.TryAdd(correlationId, pending))
            throw new InvalidOperationException("Unable to register a pending request.");

        ITopicSubscription? subscription = null;
        try
        {
            subscription = Subscribe(replyTopic, message =>
            {
                if (!_pendingRequests.TryGetValue(correlationId, out var current))
                    return Task.CompletedTask;

                if (message.CorrelationId != correlationId)
                    return Task.CompletedTask;

                current.Completion.TrySetResult(message);
                return Task.CompletedTask;
            });

            await PublishCoreAsync(requestTopic, payload, correlationId, replyTopic, ct)
                .ConfigureAwait(false);

            return await completion.Task
                .WaitAsync(effectiveTimeout, ct)
                .ConfigureAwait(false);
        }
        catch (TimeoutException ex)
        {
            throw new TimeoutException(
                $"Request on topic '{requestTopic}' did not receive a reply on '{replyTopic}' " +
                $"within {effectiveTimeout.TotalMilliseconds:0} ms.",
                ex);
        }
        finally
        {
            subscription?.Dispose();
            _pendingRequests.TryRemove(correlationId, out _);
        }
    }

    /// <summary>Reply to a request using its reply topic and correlation id.</summary>
    public async Task ReplyAsync(
        TopicMessage request,
        object? payload = null,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.CorrelationId is not { } correlationId)
        {
            throw new ArgumentException(
                "The request message must have a correlation id.",
                nameof(request));
        }

        if (string.IsNullOrWhiteSpace(request.ReplyTo))
            throw new ArgumentException("The request message must have a reply topic.", nameof(request));

        await PublishCoreAsync(request.ReplyTo, payload, correlationId, replyTo: null, ct)
            .ConfigureAwait(false);
    }

    private async Task PublishCoreAsync(
        string topic,
        object? payload,
        Guid? correlationId,
        string? replyTo,
        CancellationToken ct)
    {
        ValidateTopic(topic);
        EnsureRunning();

        if (!_topics.TryGetValue(topic, out var topicSubscribers))
            return;

        var subscribers = topicSubscribers.Values.ToArray();
        if (subscribers.Length == 0)
            return;

        var message = new TopicMessage
        {
            MessageId = Guid.NewGuid(),
            Topic = topic,
            Payload = payload,
            Timestamp = DateTimeOffset.UtcNow,
            CorrelationId = correlationId,
            ReplyTo = replyTo,
            Sequence = Interlocked.Increment(ref _sequence)
        };

        foreach (var subscriber in subscribers)
        {
            if (!subscriber.Active)
                continue;

            try
            {
                await EnqueueAsync(subscriber, message, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Report(TopicErrorKind.HandlerFailed, subscriber, message, ex,
                    "Topic publish enqueue failed.");
            }
        }
    }

    /// <summary>Stop every subscription and await its pump.</summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeStarted, 1) == 1)
            return;

        TopicSubscriber[] subscribers;
        lock (_stateGate)
        {
            if (State == BroadcastState.Disposed)
                return;

            Volatile.Write(ref _state, (int)BroadcastState.Disposing);
        }

        foreach (var pending in _pendingRequests.Values)
        {
            pending.Completion.TrySetException(
                new BroadcastDisposedException());
        }

        lock (_gate)
        {
            subscribers = _topics.Values.SelectMany(topic => topic.Values).ToArray();
            _topics.Clear();
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
            // Pumps catch their own failures; ignore stragglers.
        }

        lock (_stateGate)
        {
            Volatile.Write(ref _state, (int)BroadcastState.Disposed);
        }
    }

    internal void UnsubscribeCore(string topic, Guid id, long generation)
    {
        TopicSubscriber? subscriber = null;

        lock (_gate)
        {
            if (!_topics.TryGetValue(topic, out var topicSubscribers))
                return;

            if (!topicSubscribers.TryGetValue(id, out subscriber) ||
                subscriber.Generation != generation)
                return;

            topicSubscribers.TryRemove(id, out _);
            if (topicSubscribers.IsEmpty)
                _topics.TryRemove(topic, out _);
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
                Initialize();
                return;
            default:
                throw new BroadcastDisposedException();
        }
    }

    private static void ValidateOptions(TopicBusOptions options)
    {
        if (options.SubscriberCapacity <= 0)
            throw new ArgumentOutOfRangeException(
                nameof(options), options.SubscriberCapacity,
                "SubscriberCapacity must be greater than zero.");

        if (!Enum.IsDefined(options.QueueFullMode))
            throw new ArgumentOutOfRangeException(
                nameof(options), options.QueueFullMode, "Unknown QueueFullMode.");
    }

    private static void ValidateTopic(string topic) =>
        ArgumentException.ThrowIfNullOrWhiteSpace(topic);

    private async Task EnqueueAsync(
        TopicSubscriber subscriber,
        TopicMessage message,
        CancellationToken ct)
    {
        if (!subscriber.Active)
        {
            Report(TopicErrorKind.SubscriptionStopped, subscriber, message, null,
                "Subscription is no longer active.");
            return;
        }

        switch (_options.QueueFullMode)
        {
            case QueueFullMode.Block:
                try
                {
                    await subscriber.Queue.Writer.WriteAsync(message, ct).ConfigureAwait(false);
                }
                catch (ChannelClosedException)
                {
                    Report(TopicErrorKind.SubscriptionStopped, subscriber, message, null,
                        "Queue was completed before the message could be enqueued.");
                }

                return;

            case QueueFullMode.DropNewest:
                if (!subscriber.Queue.Writer.TryWrite(message))
                {
                    Report(TopicErrorKind.QueueFull, subscriber, message, null,
                        "Queue is full; incoming message dropped.");
                }

                return;

            case QueueFullMode.DropOldest:
            default:
                if (subscriber.Queue.Writer.TryWrite(message))
                    return;

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
                    Report(TopicErrorKind.MessageDropped, subscriber, message, null,
                        $"Queue was full; dropped {dropped} oldest message(s).");
                }
                else
                {
                    Report(TopicErrorKind.QueueFull, subscriber, message, null,
                        "Queue is full; incoming message dropped.");
                }

                return;
        }
    }

    private async Task PumpAsync(TopicSubscriber subscriber)
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
                    Report(TopicErrorKind.HandlerFailed, subscriber, message, ex,
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
            Report(TopicErrorKind.HandlerFailed, subscriber, null, ex, "Pump failed.");
        }
        finally
        {
            subscriber.Active = false;
            try { subscriber.Queue.Writer.TryComplete(); } catch { /* already completed */ }
            _pumps.TryRemove(subscriber.Generation, out _);

            lock (_gate)
            {
                if (_topics.TryGetValue(subscriber.Topic, out var topicSubscribers) &&
                    topicSubscribers.TryGetValue(subscriber.Id, out var current) &&
                    current.Generation == subscriber.Generation)
                {
                    topicSubscribers.TryRemove(subscriber.Id, out _);
                    if (topicSubscribers.IsEmpty)
                        _topics.TryRemove(subscriber.Topic, out _);
                }
            }

            try { subscriber.Cancellation.Dispose(); } catch { /* already disposed */ }
        }
    }

    private static void Stop(TopicSubscriber subscriber)
    {
        subscriber.Active = false;
        try { subscriber.Cancellation.Cancel(); } catch { /* already disposed */ }
        try { subscriber.Queue.Writer.TryComplete(); } catch { /* already completed */ }
    }

    private void Report(
        TopicErrorKind kind,
        TopicSubscriber? subscriber,
        TopicMessage? message,
        Exception? exception,
        string? detail)
    {
        try
        {
            Error?.Invoke(this, new TopicErrorEventArgs
            {
                Kind = kind,
                SubscriptionId = subscriber?.Id,
                Topic = subscriber?.Topic ?? message?.Topic,
                Message = message,
                Exception = exception,
                Detail = detail
            });
        }
        catch
        {
            // A failing observer must not break the bus.
        }
    }

    private sealed class TopicSubscriber
    {
        public required Guid Id { get; init; }
        public required string Topic { get; init; }
        public required long Generation { get; init; }
        public required Func<TopicMessage, Task> Handler { get; init; }
        public required Channel<TopicMessage> Queue { get; init; }
        public required CancellationTokenSource Cancellation { get; init; }

        public Task PumpTask { get; set; } = Task.CompletedTask;
        public volatile bool Active = true;
    }

    private sealed class PendingRequest
    {
        public required string ReplyTopic { get; init; }
        public required TaskCompletionSource<TopicMessage> Completion { get; init; }
    }
}

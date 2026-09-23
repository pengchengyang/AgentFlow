// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="BroadcastHub.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.Concurrent;
using System.Reflection;

namespace AgentFlow.Broadcast;

/// <summary>
/// In-process broadcast hub used to connect nodes and external GUI/dashboard
/// components. Subscribers register either reflectively via
/// <see cref="BroadcastHandlerAttribute"/> or programmatically via
/// <see cref="Subscribe(string, Action{BroadcastMessage})"/>.
/// </summary>
public sealed class BroadcastHub
{
    public static BroadcastHub Instance { get; } = new();

    private readonly ConcurrentDictionary<string, List<HandlerEntry>> _handlers = new();
    private readonly object _lock = new();

    private BroadcastHub()
    {
    }

    /// <summary>
    /// Scan all instance methods of <paramref name="subscriber"/> and register every method
    /// marked with <see cref="BroadcastHandlerAttribute"/>. Private methods are supported.
    /// </summary>
    public void Register(object subscriber)
    {
        ArgumentNullException.ThrowIfNull(subscriber);

        var methods = subscriber.GetType().GetMethods(
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);

        lock (_lock)
        {
            foreach (var method in methods)
            {
                foreach (var attr in method.GetCustomAttributes<BroadcastHandlerAttribute>(inherit: true))
                {
                    if (!IsValidHandler(method))
                        continue;

                    var list = _handlers.GetOrAdd(attr.Topic, _ => new List<HandlerEntry>());
                    var entry = new HandlerEntry(subscriber, method);
                    if (!list.Contains(entry))
                        list.Add(entry);
                }
            }
        }
    }

    /// <summary>Remove all reflectively registered handlers owned by <paramref name="subscriber"/>.</summary>
    public void Unregister(object subscriber)
    {
        if (subscriber is null)
            return;

        lock (_lock)
        {
            foreach (var list in _handlers.Values)
                list.RemoveAll(e => e.IsMethodHandler && ReferenceEquals(e.Subscriber, subscriber));
        }
    }

    /// <summary>Register a plain delegate handler (no reflection needed).</summary>
    public IDisposable Subscribe(string topic, Action<BroadcastMessage> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);

        lock (_lock)
        {
            var list = _handlers.GetOrAdd(topic, _ => new List<HandlerEntry>());
            var entry = new HandlerEntry(handler);
            list.Add(entry);
            return new Subscription(() => Unsubscribe(topic, entry));
        }
    }

    /// <summary>Broadcast a message to every handler registered for <paramref name="topic"/>.</summary>
    public void Publish(string topic, object? payload = null, object? sender = null)
    {
        if (!_handlers.TryGetValue(topic, out var list))
            return;

        HandlerEntry[] snapshot;
        lock (_lock)
            snapshot = list.ToArray();

        var message = new BroadcastMessage(topic, payload, sender, DateTimeOffset.Now);
        foreach (var handler in snapshot)
        {
            try
            {
                handler.Invoke(message);
            }
            catch
            {
                // Subscriber failures must not break broadcast delivery.
            }
        }
    }

    private void Unsubscribe(string topic, HandlerEntry entry)
    {
        lock (_lock)
        {
            if (_handlers.TryGetValue(topic, out var list))
                list.Remove(entry);
        }
    }

    private static bool IsValidHandler(MethodInfo method)
    {
        var parameters = method.GetParameters();
        if (parameters.Length == 0)
            return true;
        if (parameters.Length != 1)
            return false;

        var p = parameters[0].ParameterType;
        return p == typeof(object)
            || p == typeof(BroadcastMessage)
            || p.IsAssignableFrom(typeof(BroadcastMessage));
    }

    private sealed class HandlerEntry : IEquatable<HandlerEntry>
    {
        public object? Subscriber { get; }
        public MethodInfo? Method { get; }
        public Action<BroadcastMessage>? Delegate { get; }

        public bool IsMethodHandler => Method is not null;

        public HandlerEntry(object subscriber, MethodInfo method)
        {
            Subscriber = subscriber;
            Method = method;
        }

        public HandlerEntry(Action<BroadcastMessage> handler)
        {
            Delegate = handler;
        }

        public void Invoke(BroadcastMessage message)
        {
            if (Delegate is not null)
            {
                Delegate(message);
                return;
            }

            if (Method is null || Subscriber is null)
                return;

            var parameters = Method.GetParameters();
            if (parameters.Length == 0)
            {
                Method.Invoke(Subscriber, null);
            }
            else
            {
                Method.Invoke(Subscriber, new object?[] { message });
            }
        }

        public bool Equals(HandlerEntry? other)
        {
            if (other is null)
                return false;

            if (Delegate is not null || other.Delegate is not null)
                return ReferenceEquals(Delegate, other.Delegate);

            return ReferenceEquals(Subscriber, other.Subscriber)
                && ReferenceEquals(Method, other.Method);
        }

        public override bool Equals(object? obj) => Equals(obj as HandlerEntry);

        public override int GetHashCode() => HashCode.Combine(Subscriber, Method, Delegate);
    }

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        private Action? _onDispose = onDispose;

        public void Dispose() => Interlocked.Exchange(ref _onDispose, null)?.Invoke();
    }
}

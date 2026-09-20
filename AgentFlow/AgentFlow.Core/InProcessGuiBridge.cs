using System.Collections.Concurrent;
using AgentFlow.Contracts;

namespace AgentFlow.Core;

/// <summary>
/// In-process GuiBridge implementation: node Publish -> subscribers receive the message.
/// Business GUIs (dashboards) subscribe to topics; a headless CLI may subscribe to nothing.
/// </summary>
public sealed class InProcessGuiBridge : IGuiBridge
{
    private readonly ConcurrentDictionary<string, List<Action<GuiMessage>>> _handlers = new();
    private readonly object _lock = new();

    public void Publish(string topic, object? payload)
    {
        var msg = new GuiMessage(topic, payload, DateTimeOffset.Now);
        if (_handlers.TryGetValue(topic, out var list))
        {
            Action<GuiMessage>[] snapshot;
            lock (_lock) snapshot = list.ToArray();
            foreach (var h in snapshot)
            {
                try { h(msg); } catch { /* Subscriber failures must not break node execution */ }
            }
        }
    }

    public IDisposable Subscribe(string topic, Action<GuiMessage> handler)
    {
        var list = _handlers.GetOrAdd(topic, _ => new List<Action<GuiMessage>>());
        lock (_lock) list.Add(handler);
        return new Subscription(() => { lock (_lock) list.Remove(handler); });
    }

    private sealed class Subscription(Action onDispose) : IDisposable
    {
        public void Dispose() => onDispose();
    }
}

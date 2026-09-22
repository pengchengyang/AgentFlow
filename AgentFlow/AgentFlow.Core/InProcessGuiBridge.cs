using AgentFlow.Broadcast;
using AgentFlow.Contracts;

namespace AgentFlow.Core;

/// <summary>
/// In-process GuiBridge implementation that forwards every node publish to the reusable
/// <see cref="BroadcastHub"/>. Business GUIs/dashboards can either use this bridge directly
/// or subscribe through the independent broadcast DLL.
/// </summary>
public sealed class InProcessGuiBridge : IGuiBridge
{
    public void Publish(string topic, object? payload)
        => BroadcastHub.Instance.Publish(topic, payload);

    public IDisposable Subscribe(string topic, Action<GuiMessage> handler)
        => BroadcastHub.Instance.Subscribe(topic, msg =>
            handler(new GuiMessage(msg.Topic, msg.Payload, msg.Timestamp)));
}

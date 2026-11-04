// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="Broadcast.Demo.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
using BroadcastKit;
using BroadcastKit.Contracts;

namespace AgentFlow.Services;

/// <summary>
/// GUI side of the targetless topic demo. It subscribes to messages from
/// <c>Node.Broadcast.Demo</c> and replies on the GUI topic. Payload and Sample are independent
/// fields; callers may send either one or both.
/// </summary>
public sealed class BroadcastDemo : IDisposable
{
    private readonly Action<string> _log;
    private readonly ITopicSubscription _fromNodeSubscription;

    public BroadcastDemo(Action<string> log)
    {
        _log = log;
        _fromNodeSubscription = TopicBus.Instance.Subscribe(
            BroadcastTopics.NodeToGui,
            OnNodeMessage);
    }

    /// <summary>Send only the string payload to the node.</summary>
    public Task SendPayloadAsync(string payload) =>
        PublishAsync(BroadcastTopicMessage.FromPayload(payload));

    /// <summary>Send only the typed sample to the node.</summary>
    public Task SendSampleAsync(Sample sample) =>
        PublishAsync(BroadcastTopicMessage.FromSample(sample));

    /// <summary>Send payload and sample together to the node.</summary>
    public Task SendBothAsync(string payload, Sample sample) =>
        PublishAsync(BroadcastTopicMessage.FromBoth(payload, sample));

    public void Dispose() => _fromNodeSubscription.Dispose();

    private async Task OnNodeMessage(TopicMessage message)
    {
        try
        {
            if (message.Payload is not BroadcastTopicMessage demo)
            {
                _log($"[node -> gui] ignored non-demo payload: {message.Payload}");
                return;
            }

            if (demo.IsEmpty)
            {
                _log("[node -> gui] received an empty demo message.");
                return;
            }

            _log($"[node -> gui] {Describe(demo)}");

            // Reply with both fields so the node can verify Sample and Payload independently.
            var sample = StringSample.CreateSample();
            sample.SetValue("gui-sample", "Broadcast.Demo");
            await SendBothAsync("gui payload", sample);
        }
        catch (Exception ex)
        {
            _log($"[node -> gui] failed: {ex.Message}");
        }
    }

    private static Task PublishAsync(BroadcastTopicMessage message) =>
        TopicBus.Instance.PublishAsync(BroadcastTopics.GuiToNode, message);

    private static string Describe(BroadcastTopicMessage message)
    {
        var payload = message.Payload is null
            ? "payload=(none)"
            : $"payload={message.Payload}";
        var sample = message.Sample is null
            ? "sample=(none)"
            : $"sample={message.Sample.Type} key={message.Sample.Key} value={message.Sample.GetValue(message.Sample.Type)}";
        return $"{payload} {sample}";
    }
}

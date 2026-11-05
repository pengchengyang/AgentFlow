// -----------------------------------------------------------------------
// <copyright company="Your Company" file="BroadcastDemoNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
using BroadcastKit;
using BroadcastKit.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.Broadcast.Demo;

/// <summary>
/// Minimal node-side demo for the generic TopicBus. It sends one initial message containing
/// both Payload and Sample, then receives GUI replies on the GUI topic. No node id, target id
/// or host broadcast facade is involved.
/// </summary>
public sealed class BroadcastDemoNode : BaseNode
{
    private ITopicSubscription? _fromGuiSubscription;

    public BroadcastDemoNode()
    {
        Uuid = "9C4F2A61-7B3D-4E58-9A1C-2D6F8E0B5C77";
        AddParam();
        AddPins();
    }

    public override string Name => "Broadcast Demo";
    public override string DisplayName { get; set; } = "Broadcast Demo";
    public override string Category => "Demo";

    public override void AddPins() { }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters) { }

    public override Task Initialize(INodeContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    public override async Task Run(INodeContext context, CancellationToken cancellationToken = default)
    {
        _fromGuiSubscription?.Dispose();
        _fromGuiSubscription = TopicBus.Instance.Subscribe(
            BroadcastTopics.GuiToNode,
            message => OnGuiMessage(context, message));
        Running = true;

        var sample = StringSample.CreateSample();
        sample.SetValue("node-sample", "Broadcast.Demo");

        try
        {
            await TopicBus.Instance.PublishAsync(
                BroadcastTopics.NodeToGui,
                BroadcastTopicMessage.FromBoth("node payload", sample),
                ct: cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            context.Logger.LogDebug(ex, "BroadcastDemoNode failed to publish its initial message.");
        }
    }

    public override Task Stop(INodeContext context, CancellationToken cancellationToken = default)
    {
        Running = false;
        _fromGuiSubscription?.Dispose();
        _fromGuiSubscription = null;
        return Task.CompletedTask;
    }

    private static Task OnGuiMessage(INodeContext context, TopicMessage message)
    {
        if (message.Payload is not BroadcastTopicMessage demo)
        {
            context.Logger.LogInformation(
                "BroadcastDemoNode ignored non-demo payload on {Topic}: {Payload}",
                message.Topic, message.Payload);
            return Task.CompletedTask;
        }

        context.Logger.LogInformation(
            "BroadcastDemoNode received {Topic}: payload={Payload} sample={Sample}",
            message.Topic,
            demo.Payload ?? "(none)",
            demo.Sample is null
                ? "(none)"
                : $"{demo.Sample.Type} key={demo.Sample.Key} value={demo.Sample.GetValue(demo.Sample.Type)}");

        return Task.CompletedTask;
    }
}

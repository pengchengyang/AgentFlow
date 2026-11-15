// -----------------------------------------------------------------------
// <copyright company="Your Company" file="AutoStopNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
using AgentFlow.Shared.RTS.Broadcasts;
using BroadcastKit;
using BroadcastKit.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.AutoStop;

/// <summary>
/// Workflow-control node that accepts one exact exit message and broadcasts its JSON payload.
/// </summary>
public sealed class AutoStopNode : BaseNode
{
    private const string ExitMessage = ";{\"Request\":\"Exit\"};";
    private const string ExitPayload = "{\"Request\":\"Exit\"}";

    private readonly BasePin _stopPin =
        new("input_string_stop", DataType.STRING, PinDirection.Input, required: false);

    private ILogger? _logger;
    private ITopicBus? _topicBus;
    private IWorkflowStopRequestSink? _stopRequestSink;

    public AutoStopNode()
    {
        Uuid = "C60BAA9B-C0AB-4817-8683-F1646BC863AE";
        AddParam();
        AddPins();
    }

    public override string Name => "Auto Stop";
    public override string DisplayName { get; set; } = "Auto Stop";
    public override string Category => "Control";

    public override void AddPins()
    {
        AddInputPin(_stopPin);
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        _topicBus = ResolveTopicBus(context);
        _stopRequestSink = context.GetService<IWorkflowStopRequestSink>();
        return Task.CompletedTask;
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        _topicBus = ResolveTopicBus(context);
        _stopRequestSink = context.GetService<IWorkflowStopRequestSink>();
        Running = true;
        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        _topicBus = null;
        _stopRequestSink = null;
        _logger = null;
        return Task.CompletedTask;
    }

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (!ReferenceEquals(pin, _stopPin))
            return;

        if (value is not StringSample input ||
            input.StringValue is not { } inputText)
        {
            _logger?.LogError(
                "AutoStop ignored invalid input on {Pin}: expected a non-empty StringSample.",
                pin.Name);
            return;
        }

        if (!string.Equals(inputText, ExitMessage, StringComparison.Ordinal))
            return;

        _logger?.LogInformation(
            "AutoStop received stop message on {Pin}: {Message}",
            pin.Name,
            inputText);

        _ = BroadcastExitAsync();
    }

    private async Task BroadcastExitAsync()
    {
        var topicBus = _topicBus ?? TopicBus.Instance;
        var stopRequestSink = _stopRequestSink;
        try
        {
            var sample = StringSample.CreateSample();
            sample.SetValue(ExitPayload);

            await topicBus.PublishAsync(
                RTSBroadcastSubscriptions.AutoStopExit,
                BroadcastTopicMessage.FromSample(sample),
                ct: CancellationToken.None).ConfigureAwait(false);

            _logger?.LogTrace(
                "AutoStop broadcast the exit signal on {Topic}.",
                RTSBroadcastSubscriptions.AutoStopExit);

            if (stopRequestSink is not null)
            {
                if (!stopRequestSink.RequestStop(Name, ExitMessage))
                    _logger?.LogWarning("AutoStop stop request was not accepted by the current workflow host.");
            }
            else
            {
                _logger?.LogWarning(
                    "AutoStop has no workflow stop sink in this host; only the exit broadcast was sent.");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "AutoStop failed to broadcast the exit signal on {Topic}.",
                RTSBroadcastSubscriptions.AutoStopExit);
        }
    }

    private static ITopicBus ResolveTopicBus(INodeContext context) =>
        context.GetService<ITopicBus>() ?? TopicBus.Instance;

    /// <summary>
    /// Final application-exit hook. Runtime resources were already released by Stop.
    /// </summary>
    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;
}

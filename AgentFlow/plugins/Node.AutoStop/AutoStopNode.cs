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
        return Task.CompletedTask;
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        Running = true;
        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        _logger = null;
        return Task.CompletedTask;
    }

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (ReferenceEquals(pin, _stopPin) &&
            value is StringSample input &&
            string.Equals(input.StringValue, ExitMessage, StringComparison.Ordinal))
        {
            _logger?.LogInformation(
                "AutoStop received stop message on {Pin}: {Message}",
                pin.Name,
                input.StringValue);

            _ = BroadcastExitAsync();
        }
    }

    private async Task BroadcastExitAsync()
    {
        try
        {
            var sample = StringSample.CreateSample();
            sample.SetValue(ExitPayload);

            await TopicBus.Instance.PublishAsync(
                RTSBroadcastSubscriptions.AutoStopExit,
                BroadcastTopicMessage.FromSample(sample),
                ct: CancellationToken.None).ConfigureAwait(false);

            _logger?.LogTrace(
                "AutoStop broadcast the exit signal on {Topic}.",
                RTSBroadcastSubscriptions.AutoStopExit);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "AutoStop failed to broadcast the exit signal on {Topic}.",
                RTSBroadcastSubscriptions.AutoStopExit);
        }
    }

    /// <summary>
    /// Final application-exit hook. Runtime resources were already released by Stop.
    /// </summary>
    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;
}

// -----------------------------------------------------------------------
// <copyright company="Your Company" file="AutoStopNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;
using AgentFlow.Contracts;
using AgentFlow.Shared.RTS.CFG.Broadcasts;
using BroadcastKit;
using BroadcastKit.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.AutoStop;

/// <summary>
/// Workflow-control node that accepts semicolon-framed JSON, triggers only when the top-level
/// TYPE is DISCONNECT, broadcasts the stop payload, and requests a normal workflow stop from the host.
/// </summary>
public sealed class AutoStopNode : BaseNode
{
    private const string StopRequestFragment = "{\"Request\":\"Stop\"}";
    private const string DisconnectMessageType = "DISCONNECT";

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
            input.StringValue is not { } inputText ||
            string.IsNullOrWhiteSpace(inputText))
        {
            _logger?.LogWarning(
                "AutoStop ignored invalid input on {Pin}: expected a non-empty StringSample.",
                pin.Name);
            return;
        }

        if (!TryGetDisconnectMessageType(inputText, out var messageType, out var parseError))
        {
            _logger?.LogWarning(
                "AutoStop ignored input on {Pin} because it could not be parsed as a JSON object with string property TYPE: {Error}",
                pin.Name,
                parseError);
            return;
        }

        if (!string.Equals(messageType, DisconnectMessageType, StringComparison.Ordinal))
        {
            _logger?.LogWarning(
                "AutoStop ignored input on {Pin} because TYPE is {Type}; expected {ExpectedType}.",
                pin.Name,
                messageType,
                DisconnectMessageType);
            return;
        }

        _logger?.LogInformation(
            "AutoStop received DISCONNECT message on {Pin}: {Message}",
            pin.Name,
            inputText);

        _ = BroadcastStopRequestAsync(inputText);
    }

    private static bool TryGetDisconnectMessageType(
        string inputText,
        out string messageType,
        out string parseError)
    {
        messageType = string.Empty;
        parseError = string.Empty;

        var jsonText = inputText.Trim().Trim(';').Trim();
        try
        {
            using var document = JsonDocument.Parse(jsonText);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                parseError = "The input root must be a JSON object.";
                return false;
            }

            if (!document.RootElement.TryGetProperty("TYPE", out var typeElement) ||
                typeElement.ValueKind != JsonValueKind.String)
            {
                parseError = "The input object must contain a string property named TYPE.";
                return false;
            }

            messageType = typeElement.GetString() ?? string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            parseError = ex.Message;
            return false;
        }
    }

    private async Task BroadcastStopRequestAsync(string triggerMessage)
    {
        var topicBus = _topicBus ?? TopicBus.Instance;
        var stopRequestSink = _stopRequestSink;
        try
        {
            var sample = StringSample.CreateSample();
            sample.SetValue(StopRequestFragment);

            await topicBus.PublishAsync(
                CFGBroadcastSubscriptions.CFGAutoStopExit,
                BroadcastTopicMessage.FromSample(sample),
                ct: CancellationToken.None).ConfigureAwait(false);

            _logger?.LogTrace(
                "AutoStop broadcast the stop request on {Topic}.",
                CFGBroadcastSubscriptions.CFGAutoStopExit);

            if (stopRequestSink is not null)
            {
                if (!stopRequestSink.RequestStop(Name, triggerMessage))
                    _logger?.LogWarning("AutoStop stop request was not accepted by the current workflow host.");
            }
            else
            {
                _logger?.LogWarning(
                    "AutoStop has no workflow stop sink in this host; only the stop broadcast was sent.");
            }
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "AutoStop failed to broadcast the stop request on {Topic}.",
                CFGBroadcastSubscriptions.CFGAutoStopExit);
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

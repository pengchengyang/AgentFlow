// -----------------------------------------------------------------------
// <copyright company="Your Company" file="AutoStopNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
using BroadcastKit;
using BroadcastKit.Contracts;
using AgentFlow.Shared.FlashForgeHmi.Broadcasts;
using Microsoft.Extensions.Logging;

namespace Node.AutoStop;

/// <summary>
/// Workflow control node that receives semicolon-terminated JSON groups, validates them,
/// and requests a workflow stop through <see cref="IWorkflowStopRequestSink"/>.
/// </summary>
public sealed class AutoStopNode : BaseNode
{
    private const int MaxPendingCharacters = 64 * 1024;

    private readonly BasePin _stopPin =
        new("input_string_stop", DataType.STRING, PinDirection.Input, required: false);

    private readonly object _inputGate = new();

    private INodeContext? _context;
    private ILogger? _logger;
    private string _pendingInput = string.Empty;

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
        _context = context;
        _logger = context.Logger;
        lock (_inputGate)
            _pendingInput = string.Empty;

        return Task.CompletedTask;
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        _context = context;
        _logger = context.Logger;
        Running = true;
        lock (_inputGate)
            _pendingInput = string.Empty;

        _logger?.LogTrace(
            "AutoStop is waiting for semicolon-terminated SLOT/FSN/IMEI JSON groups.");
        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        _logger = null;
        _context = null;
        lock (_inputGate)
            _pendingInput = string.Empty;

        return Task.CompletedTask;
    }

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (ReferenceEquals(pin, _stopPin))
        {
            if (value is not StringSample input ||
                input.StringValue is not { } inputText)
            {
                _logger?.LogError(
                    "AutoStop ignored invalid input on {Pin}: expected StringSample with non-null StringValue, got {Type}.",
                    pin.Name,
                    value?.Type.ToString() ?? "null");
                return;
            }

            lock (_inputGate)
            {
                _pendingInput += inputText;
                if (_pendingInput.Length > MaxPendingCharacters)
                {
                    _logger?.LogError(
                        "AutoStop input exceeded the {MaxCharacters}-character buffer limit and was cleared.",
                        MaxPendingCharacters);
                    _pendingInput = string.Empty;
                    return;
                }

                while (true)
                {
                    var separatorIndex = _pendingInput.IndexOf(';');
                    if (separatorIndex < 0)
                        break;

                    var segment = _pendingInput[..separatorIndex].Trim();
                    _pendingInput = _pendingInput[(separatorIndex + 1)..];

                    if (segment.Length == 0)
                        continue;

                    ProcessCompletedSegment(segment, pin.Context);
                }
            }
        }
    }

    private void ProcessCompletedSegment(string segment, INodeContext? pinContext)
    {
        if (!JsonKeyValueParser.TryParseStringValues(segment, out var values))
        {
            _logger?.LogError(
                "AutoStop ignored an invalid JSON group: {Payload}",
                segment);
            return;
        }

        if (!TryGetRequiredValue(values, "SLOT", out var slotText) ||
            !TryGetRequiredValue(values, "FSN", out var fsn) ||
            !TryGetRequiredValue(values, "IMEI", out var imei))
        {
            _logger?.LogError(
                "AutoStop ignored a JSON group because SLOT, FSN, and IMEI are required: {Payload}",
                segment);
            return;
        }

        if (!int.TryParse(slotText, out var slot) || slot is < 1 or > 10)
        {
            _logger?.LogError(
                "AutoStop ignored an invalid SLOT value {Slot}: {Payload}",
                slotText,
                segment);
            return;
        }

        _logger?.LogTrace(
            "AutoStop accepted SLOT {Slot}, FSN {Fsn}, IMEI {Imei} and requested a workflow stop.",
            slot,
            fsn,
            imei);

        _ = BroadcastLauncherStopAsync();
        RequestWorkflowStop(segment, pinContext);
    }

    private async Task BroadcastLauncherStopAsync()
    {
        try
        {
            var sample = StringSample.CreateSample();
            sample.SetValue("Stop");

            await TopicBus.Instance.PublishAsync(
                FlashForgeHmiBroadcastSubscriptions.LauncherStop,
                BroadcastTopicMessage.FromSample(sample),
                ct: CancellationToken.None).ConfigureAwait(false);

            _logger?.LogTrace(
                "AutoStop broadcast launcher stop signal on {Topic}.",
                FlashForgeHmiBroadcastSubscriptions.LauncherStop);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "AutoStop failed to broadcast launcher stop signal on {Topic}.",
                FlashForgeHmiBroadcastSubscriptions.LauncherStop);
        }
    }

    private void RequestWorkflowStop(string triggerMessage, INodeContext? pinContext)
    {
        var stopSink = (_context ?? pinContext)?.GetService<IWorkflowStopRequestSink>();
        if (stopSink is null)
        {
            _logger?.LogWarning(
                "AutoStop parsed a valid group, but no IWorkflowStopRequestSink service is registered.");
            return;
        }

        if (!stopSink.RequestStop(Name, triggerMessage))
            _logger?.LogTrace("AutoStop stop request was not accepted by the host.");
    }

    private static bool TryGetRequiredValue(
        IReadOnlyDictionary<string, string> values,
        string key,
        out string value)
    {
        if (values.TryGetValue(key, out var found) && !string.IsNullOrWhiteSpace(found))
        {
            value = found;
            return true;
        }

        value = string.Empty;
        return false;
    }

    /// <summary>
    /// Final application-exit hook. Runtime resources were already released by Stop; this node
    /// currently has no additional process-exit cleanup.
    /// </summary>
    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;
}

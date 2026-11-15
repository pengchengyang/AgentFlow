// -----------------------------------------------------------------------
// <copyright company="Your Company" file="AutoStartNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.AutoStart;

/// <summary>
/// Workflow control node that receives semicolon-prefixed and semicolon-terminated JSON groups and emits each
/// validated JSON group through the output selected by its SLOT value. The actual workflow-start action
/// is supplied by the host through <see cref="IWorkflowStartRequestSink"/>.
/// </summary>
public sealed class AutoStartNode : BaseNode
{
    private const int MaxPendingCharacters = 64 * 1024;

    private readonly BasePin _startPin =
        new("input_string_start", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _readyAPin =
        new("output_string_readyA", DataType.STRING, PinDirection.Output);

    private readonly BasePin _readyBPin =
        new("output_string_readyB", DataType.STRING, PinDirection.Output);

    private readonly BasePin _readyCPin =
        new("output_string_readyC", DataType.STRING, PinDirection.Output);

    private readonly BasePin _readyDPin =
        new("output_string_readyD", DataType.STRING, PinDirection.Output);

    private readonly BasePin _readyFPin =
        new("output_string_readyF", DataType.STRING, PinDirection.Output);

    private readonly BasePin[] _readyPins;
    private readonly object _inputGate = new();

    private INodeContext? _context;
    private ILogger? _logger;
    private string _pendingInput = string.Empty;
    private bool _batchStartReceived;

    public AutoStartNode()
    {
        _readyPins = new[]
        {
            _readyAPin,
            _readyBPin,
            _readyCPin,
            _readyDPin,
            _readyFPin,
        };

        Uuid = "DC836F0E-BAB4-4D53-8BD3-4C9E1B6AD7DB";
        AddParam();
        AddPins();
    }

    public override string Name => "Auto Start";
    public override string DisplayName { get; set; } = "Auto Start";
    public override string Category => "Control";

    public override void AddPins()
    {
        AddInputPin(_startPin);
        foreach (var readyPin in _readyPins)
            AddOutputPin(readyPin);
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        _context = context;
        _logger = context.Logger;
        lock (_inputGate)
        {
            _pendingInput = string.Empty;
            _batchStartReceived = false;
        }

        return Task.CompletedTask;
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        _context = context;
        _logger = context.Logger;
        Running = true;
        lock (_inputGate)
        {
            _pendingInput = string.Empty;
            _batchStartReceived = false;
        }

        _logger?.LogTrace(
            "AutoStart is waiting for semicolon-prefixed batches containing semicolon-terminated SLOT/FSN/IMEI JSON groups.");
        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        _logger = null;
        _context = null;
        lock (_inputGate)
        {
            _pendingInput = string.Empty;
            _batchStartReceived = false;
        }

        return Task.CompletedTask;
    }

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (ReferenceEquals(pin, _startPin))
        {
            if (!Running)
                return;

            if (value is not StringSample input ||
                input.StringValue is not { } inputText)
            {
                _logger?.LogError(
                    "AutoStart ignored invalid input on {Pin}: expected StringSample with non-null StringValue, got {Type}.",
                    pin.Name,
                    value?.Type.ToString() ?? "null");
                return;
            }

            lock (_inputGate)
            {
                if (!_batchStartReceived)
                {
                    var candidate = inputText.TrimStart();
                    if (candidate.Length == 0)
                        return;

                    if (candidate[0] != ';')
                    {
                        _logger?.LogError(
                            "AutoStart rejected input because each batch must begin with ';': {Payload}",
                            inputText);
                        _pendingInput = string.Empty;
                        _batchStartReceived = false;
                        return;
                    }

                    _batchStartReceived = true;
                    inputText = candidate[1..];
                }

                _pendingInput += inputText;
                if (_pendingInput.Length > MaxPendingCharacters)
                {
                    _logger?.LogError(
                        "AutoStart input exceeded the {MaxCharacters}-character buffer limit and was cleared.",
                        MaxPendingCharacters);
                    _pendingInput = string.Empty;
                    _batchStartReceived = false;
                    return;
                }

                var processedSegment = false;
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
                    processedSegment = true;
                }

                if (processedSegment && string.IsNullOrWhiteSpace(_pendingInput))
                {
                    _pendingInput = string.Empty;
                    _batchStartReceived = false;
                }
            }
        }
    }

    private void ProcessCompletedSegment(string segment, INodeContext? pinContext)
    {
        if (!JsonKeyValueParser.TryParseStringValues(segment, out var values))
        {
            _logger?.LogError(
                "AutoStart ignored an invalid JSON group: {Payload}",
                segment);
            return;
        }

        if (!TryGetRequiredValue(values, "SLOT", out var slotText) ||
            !TryGetRequiredValue(values, "FSN", out var fsn) ||
            !TryGetRequiredValue(values, "IMEI", out var imei))
        {
            _logger?.LogError(
                "AutoStart ignored a JSON group because SLOT, FSN, and IMEI are required: {Payload}",
                segment);
            return;
        }

        if (!int.TryParse(slotText, out var slot) || slot is < 1 or > 10)
        {
            _logger?.LogError(
                "AutoStart ignored an invalid SLOT value {Slot}: {Payload}",
                slotText,
                segment);
            return;
        }

        var readyPin = GetReadyPin(slot);
        if (readyPin is null)
        {
            _logger?.LogError("AutoStart could not map SLOT {Slot} to a ready output pin.", slot);
            return;
        }

        var readySample = StringSample.CreateSample();
        readySample.StringValue = segment;
        readyPin.SendSampleWithDispatchLog(
            readySample,
            $"SLOT {slot} ready payload, FSN {fsn}, IMEI {imei}",
            LogLevel.Information);

        RequestWorkflowStart(segment, pinContext);
    }

    private void RequestWorkflowStart(string triggerMessage, INodeContext? pinContext)
    {
        var startSink = (_context ?? pinContext)?.GetService<IWorkflowStartRequestSink>();
        if (startSink is null)
        {
            _logger?.LogWarning(
                "AutoStart parsed a valid group, but no IWorkflowStartRequestSink service is registered.");
            return;
        }

        if (!startSink.RequestStart(Name, triggerMessage))
            _logger?.LogTrace("AutoStart start request was not accepted by the host.");
    }

    private BasePin? GetReadyPin(int slot) => slot switch
    {
        1 or 6 => _readyAPin,
        2 or 7 => _readyBPin,
        3 or 8 => _readyCPin,
        4 or 9 => _readyDPin,
        5 or 10 => _readyFPin,
        _ => null,
    };

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



// -----------------------------------------------------------------------
// <copyright company="Your Company" file="AutoStartNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.AutoStart;

/// <summary>
/// Workflow control node that receives a JSON object containing a MESSAGES array, validates each
/// business message, emits each individual message through the output selected by its SLOT value,
/// and forwards a workflow-start request to the host. The actual workflow-start action is supplied
/// by the host through <see cref="IWorkflowStartRequestSink"/>.
/// </summary>
public sealed class AutoStartNode : BaseNode
{
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

    private INodeContext? _context;
    private ILogger? _logger;

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
        return Task.CompletedTask;
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        _context = context;
        _logger = context.Logger;
        Running = true;
        _logger?.LogTrace(
            "AutoStart is waiting for JSON objects containing a MESSAGES array of SLOT/FSN/IMEI business messages.");
        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        _logger = null;
        _context = null;
        return Task.CompletedTask;
    }

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (!ReferenceEquals(pin, _startPin))
            return;

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

        ProcessInput(inputText, pin.Context);
    }

    private void ProcessInput(string inputText, INodeContext? pinContext)
    {
        if (string.IsNullOrWhiteSpace(inputText))
        {
            _logger?.LogError("AutoStart ignored empty input.");
            return;
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(inputText);
        }
        catch (JsonException ex)
        {
            _logger?.LogError(
                ex,
                "AutoStart ignored invalid JSON input: {Payload}",
                inputText);
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                _logger?.LogError(
                    "AutoStart rejected input because the top-level JSON value must be an object: {Payload}",
                    inputText);
                return;
            }

            if (!TryGetPropertyIgnoreCase(root, "MESSAGES", out var messages))
            {
                _logger?.LogError(
                    "AutoStart rejected input because the top-level MESSAGES property is missing: {Payload}",
                    inputText);
                return;
            }

            if (messages.ValueKind != JsonValueKind.Array)
            {
                _logger?.LogError(
                    "AutoStart rejected input because MESSAGES must be a JSON array: {Payload}",
                    inputText);
                return;
            }

            if (messages.GetArrayLength() == 0)
            {
                _logger?.LogWarning("AutoStart received an empty MESSAGES array and produced no output.");
                return;
            }

            var index = 0;
            foreach (var message in messages.EnumerateArray())
            {
                ProcessMessage(message, index, pinContext);
                index++;
            }
        }
    }

    private void ProcessMessage(JsonElement message, int index, INodeContext? pinContext)
    {
        if (message.ValueKind != JsonValueKind.Object)
        {
            _logger?.LogError(
                "AutoStart ignored MESSAGES item {Index} because it must be a JSON object: {Payload}",
                index,
                message.GetRawText());
            return;
        }

        if (!TryGetRequiredStringProperty(message, "SLOT", out var slotText) ||
            !TryGetRequiredStringProperty(message, "FSN", out var fsn) ||
            !TryGetRequiredStringProperty(message, "IMEI", out var imei))
        {
            _logger?.LogError(
                "AutoStart ignored MESSAGES item {Index} because non-empty string values for SLOT, FSN, and IMEI are required: {Payload}",
                index,
                message.GetRawText());
            return;
        }

        if (!int.TryParse(slotText, out var slot) || slot is < 1 or > 10)
        {
            _logger?.LogError(
                "AutoStart ignored MESSAGES item {Index} with invalid SLOT value {Slot}: {Payload}",
                index,
                slotText,
                message.GetRawText());
            return;
        }

        var readyPin = GetReadyPin(slot);
        if (readyPin is null)
        {
            _logger?.LogError(
                "AutoStart could not map SLOT {Slot} from MESSAGES item {Index} to a ready output pin.",
                slot,
                index);
            return;
        }

        var compactMessage = JsonSerializer.Serialize(message);
        var readySample = StringSample.CreateSample();
        readySample.StringValue = compactMessage;
        readyPin.SendSampleWithDispatchLog(
            readySample,
            $"SLOT {slot} ready payload, FSN {fsn}, IMEI {imei}",
            LogLevel.Information);

        RequestWorkflowStart(compactMessage, pinContext);
    }

    private void RequestWorkflowStart(string triggerMessage, INodeContext? pinContext)
    {
        var startSink = (_context ?? pinContext)?.GetService<IWorkflowStartRequestSink>();
        if (startSink is null)
        {
            _logger?.LogWarning(
                "AutoStart parsed a valid business message, but no IWorkflowStartRequestSink service is registered.");
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

    private static bool TryGetRequiredStringProperty(
        JsonElement element,
        string key,
        out string value)
    {
        if (TryGetPropertyIgnoreCase(element, key, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            var found = property.GetString();
            if (!string.IsNullOrWhiteSpace(found))
            {
                value = found;
                return true;
            }
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetPropertyIgnoreCase(
        JsonElement element,
        string propertyName,
        out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
            {
                value = property.Value;
                return true;
            }
        }

        value = default;
        return false;
    }

    /// <summary>
    /// Final application-exit hook. Runtime resources were already released by Stop; this node
    /// currently has no additional process-exit cleanup.
    /// </summary>
    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;
}

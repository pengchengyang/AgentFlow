// -----------------------------------------------------------------------
// <copyright company="Your Company" file="MessagesDispatcherNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;
using AgentFlow.Contracts;
using AgentFlow.Shared.RTS.CFG.Broadcasts;
using BroadcastKit;
using BroadcastKit.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.MessagesDispatcher;

/// <summary>
/// Validates a JSON object containing a fixed five-record MESSAGES array, dispatches each
/// STATUS=TEST record to its SLOT-specific output pin, and requests one workflow start when
/// the batch contains test data.
/// </summary>
public sealed class MessagesDispatcherNode : BaseNode
{
    private readonly BasePin _messagesInputPin =
        new("in_string_messages", DataType.STRING, PinDirection.Input, required: false);

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
    private ITopicBus? _topicBus;
    private ITopicSubscription? _launcherStartSubscription;

    public MessagesDispatcherNode()
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

    public override string Name => "Messages Dispatcher";
    public override string DisplayName { get; set; } = "Messages Dispatcher";
    public override string Category => "C";

    public override void AddPins()
    {
        AddInputPin(_messagesInputPin);
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
        _topicBus = ResolveTopicBus(context);

        DisposeSubscriptions();
        _launcherStartSubscription = _topicBus.Subscribe(
            CFGBroadcastSubscriptions.CFGLauncherStart,
            OnLauncherStartBroadcastAsync);

        _logger?.LogTrace(
            "Messages Dispatcher subscribed to launcher start broadcasts on {StartTopic} during initialization.",
            CFGBroadcastSubscriptions.CFGLauncherStart);
        return Task.CompletedTask;
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        _context = context;
        _logger = context.Logger;
        Running = true;
        _logger?.LogTrace(
            "Messages Dispatcher is waiting for JSON objects containing a fixed five-record MESSAGES array with SLOT/FSN/IMEI/STATUS values.");
        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        DisposeSubscriptions();
        _topicBus = null;
        _logger = null;
        _context = null;
        return Task.CompletedTask;
    }

    private Task OnLauncherStartBroadcastAsync(TopicMessage message)
    {
        try
        {
            if (!string.Equals(
                    message.Topic,
                    CFGBroadcastSubscriptions.CFGLauncherStart,
                    StringComparison.Ordinal))
            {
                _logger?.LogWarning(
                    "Messages Dispatcher ignored broadcast from unexpected topic {Topic}.",
                    message.Topic);
                return Task.CompletedTask;
            }

            if (message.Payload is not BroadcastTopicMessage
                {
                    Sample: StringSample { StringValue: { } payload }
                })
            {
                _logger?.LogWarning(
                    "Messages Dispatcher ignored launcher start broadcast because the payload did not contain a StringSample with a non-null value.");
                return Task.CompletedTask;
            }

            if (string.Equals(payload, CFGLauncherStartSignals.CFGRequest, StringComparison.Ordinal))
            {
                _logger?.LogTrace(
                    "Messages Dispatcher ignored its own launcher run request broadcast on {Topic}.",
                    message.Topic);
                return Task.CompletedTask;
            }

            _logger?.LogInformation(
                "Messages Dispatcher received launcher start broadcast on {Topic}: {Payload}",
                message.Topic,
                payload);

            ProcessInput(payload, _context);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "Messages Dispatcher failed to process launcher start broadcast on {Topic}: {Message}",
                message.Topic,
                ex.Message);
        }

        return Task.CompletedTask;
    }

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (ReferenceEquals(pin, _messagesInputPin))
        {
            if (!Running)
                return;

            if (value is not StringSample input ||
                input.StringValue is not { } inputText)
            {
                _logger?.LogError(
                    "Messages Dispatcher ignored invalid input on {Pin}: expected StringSample with non-null StringValue, got {Type}.",
                    pin.Name,
                    value?.Type.ToString() ?? "null");
                return;
            }

            ProcessInput(inputText, pin.Context);
        }
    }

    private void ProcessInput(string inputText, INodeContext? pinContext)
    {
        if (string.IsNullOrWhiteSpace(inputText))
        {
            _logger?.LogError("Messages Dispatcher ignored empty input.");
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
                "Messages Dispatcher ignored invalid JSON input: {Payload}",
                inputText);
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                _logger?.LogError(
                    "Messages Dispatcher rejected input because the top-level JSON value must be an object: {Payload}",
                    inputText);
                return;
            }

            if (!TryGetPropertyIgnoreCase(root, "MESSAGES", out var messages))
            {
                _logger?.LogError(
                    "Messages Dispatcher rejected input because the top-level MESSAGES property is missing: {Payload}",
                    inputText);
                return;
            }

            if (messages.ValueKind != JsonValueKind.Array)
            {
                _logger?.LogError(
                    "Messages Dispatcher rejected input because MESSAGES must be a JSON array: {Payload}",
                    inputText);
                return;
            }

            if (messages.GetArrayLength() != 5)
            {
                _logger?.LogError(
                    "Messages Dispatcher rejected input because MESSAGES must contain exactly five records: {Payload}",
                    inputText);
                return;
            }

            var parsedMessages = new List<(JsonElement Element, int Slot, string Fsn, string Imei, string Status)>();
            var index = 0;
            foreach (var message in messages.EnumerateArray())
            {
                if (!TryReadMessage(message, index, out var parsed, out var messageError))
                {
                    _logger?.LogError(
                        "Messages Dispatcher rejected MESSAGES item {Index}: {Error}. Payload: {Payload}",
                        index,
                        messageError,
                        message.GetRawText());
                    return;
                }

                parsedMessages.Add(parsed);
                index++;
            }

            if (!TryValidateSlotSet(parsedMessages, out var slotSetError))
            {
                _logger?.LogError(
                    "Messages Dispatcher rejected MESSAGES slot set: {Error}.",
                    slotSetError);
                return;
            }

            var hasTestMessage = false;
            for (var messageIndex = 0; messageIndex < parsedMessages.Count; messageIndex++)
            {
                var item = parsedMessages[messageIndex];
                if (string.Equals(item.Status, "IDLE", StringComparison.OrdinalIgnoreCase))
                {
                    _logger?.LogWarning(
                        "Messages Dispatcher skipped SLOT {Slot} MESSAGES item {Index} because STATUS=IDLE; no output was dispatched.",
                        item.Slot,
                        messageIndex);
                    continue;
                }

                hasTestMessage = true;
                DispatchTestMessage(item.Element, messageIndex, item.Slot, item.Fsn, item.Imei, item.Status);
            }

            if (hasTestMessage)
                RequestWorkflowStart(inputText, pinContext);
        }
    }

    private void DispatchTestMessage(
        JsonElement message,
        int index,
        int slot,
        string fsn,
        string imei,
        string status)
    {
        var readyPin = GetReadyPin(slot);
        if (readyPin is null)
        {
            _logger?.LogError(
                "Messages Dispatcher could not map SLOT {Slot} from MESSAGES item {Index} to a ready output pin.",
                slot,
                index);
            return;
        }

        var compactMessage = JsonSerializer.Serialize(message);
        var readySample = StringSample.CreateSample();
        readySample.StringValue = compactMessage;
        readyPin.SendSampleWithDispatchLog(
            readySample,
            $"SLOT {slot} ready payload, FSN {fsn}, IMEI {imei}, STATUS {status}",
            LogLevel.Information);
    }

    private static bool TryReadMessage(
        JsonElement message,
        int index,
        out (JsonElement Element, int Slot, string Fsn, string Imei, string Status) parsed,
        out string? error)
    {
        parsed = default;
        error = null;

        if (message.ValueKind != JsonValueKind.Object)
        {
            error = "each MESSAGES item must be a JSON object";
            return false;
        }

        if (!TryGetRequiredStringProperty(message, "SLOT", out var slotText) ||
            !int.TryParse(slotText, out var slot) ||
            slot is < 1 or > 10)
        {
            error = "SLOT must be an integer from 1 through 10";
            return false;
        }

        if (!TryGetStringProperty(message, "FSN", out var fsn))
        {
            error = "FSN must be a string";
            return false;
        }

        if (!TryGetStringProperty(message, "IMEI", out var imei))
        {
            error = "IMEI must be a string";
            return false;
        }

        if (string.IsNullOrEmpty(fsn) != string.IsNullOrEmpty(imei))
        {
            error = "FSN and IMEI must both be empty or both be non-empty";
            return false;
        }

        if (!TryGetStringProperty(message, "STATUS", out var status) ||
            (!string.Equals(status, "IDLE", StringComparison.OrdinalIgnoreCase) &&
             !string.Equals(status, "TEST", StringComparison.OrdinalIgnoreCase)))
        {
            error = "STATUS must be IDLE or TEST";
            return false;
        }

        if (string.IsNullOrEmpty(fsn) &&
            !string.Equals(status, "IDLE", StringComparison.OrdinalIgnoreCase))
        {
            error = "empty FSN/IMEI requires STATUS IDLE";
            return false;
        }

        parsed = (message, slot, fsn, imei, status.Trim().ToUpperInvariant());
        return true;
    }

    private static bool TryValidateSlotSet(
        IReadOnlyList<(JsonElement Element, int Slot, string Fsn, string Imei, string Status)> messages,
        out string? error)
    {
        error = null;
        if (messages.Count == 0)
        {
            error = "MESSAGES must contain exactly five records";
            return false;
        }

        var expectedStartSlot = messages[0].Slot <= 5 ? 1 : 6;
        var seenSlots = new bool[5];
        foreach (var message in messages)
        {
            if (message.Slot < expectedStartSlot || message.Slot > expectedStartSlot + 4)
            {
                error = $"SLOT {message.Slot} is outside the {expectedStartSlot}-{expectedStartSlot + 4} group";
                return false;
            }

            var slotIndex = message.Slot - expectedStartSlot;
            if (seenSlots[slotIndex])
            {
                error = $"MESSAGES contains SLOT {message.Slot} more than once";
                return false;
            }

            seenSlots[slotIndex] = true;
        }

        for (var index = 0; index < seenSlots.Length; index++)
        {
            if (!seenSlots[index])
            {
                error = $"MESSAGES must contain SLOT {expectedStartSlot + index} exactly once";
                return false;
            }
        }

        return true;
    }

    private void RequestWorkflowStart(string triggerMessage, INodeContext? pinContext)
    {
        var startSink = (_context ?? pinContext)?.GetService<IWorkflowStartRequestSink>();
        if (startSink is null)
        {
            _logger?.LogWarning(
                "Messages Dispatcher parsed a valid TEST business batch, but no IWorkflowStartRequestSink service is registered.");
            return;
        }

        if (!startSink.RequestStart(Name, triggerMessage))
            _logger?.LogTrace("Messages Dispatcher start request was not accepted by the host.");
    }

    private static ITopicBus ResolveTopicBus(INodeContext context) =>
        context.GetService<ITopicBus>() ?? TopicBus.Instance;

    private BasePin? GetReadyPin(int slot) => slot switch
    {
        1 or 6 => _readyAPin,
        2 or 7 => _readyBPin,
        3 or 8 => _readyCPin,
        4 or 9 => _readyDPin,
        5 or 10 => _readyFPin,
        _ => null,
    };

    private static bool TryGetStringProperty(
        JsonElement element,
        string key,
        out string value)
    {
        if (TryGetPropertyIgnoreCase(element, key, out var property) &&
            property.ValueKind == JsonValueKind.String)
        {
            value = property.GetString() ?? string.Empty;
            return true;
        }

        value = string.Empty;
        return false;
    }

    private static bool TryGetRequiredStringProperty(
        JsonElement element,
        string key,
        out string value)
    {
        if (TryGetStringProperty(element, key, out value) &&
            !string.IsNullOrWhiteSpace(value))
            return true;

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

    private void DisposeSubscriptions()
    {
        _launcherStartSubscription?.Dispose();
        _launcherStartSubscription = null;
    }

    /// <summary>Final application-exit hook; runtime resources were already released by Stop.</summary>
    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        DisposeSubscriptions();
        _topicBus = null;
        _logger = null;
        _context = null;
        return Task.CompletedTask;
    }
}

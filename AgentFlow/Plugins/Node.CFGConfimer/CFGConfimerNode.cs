// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="CFGConfimerNode.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.CFGConfimer;

/// <summary>
/// Confirms that a complete CFG protocol envelope targets this local GUID and location.
/// The location is derived from the SLOT values in the complete UserSettings JSON received on
/// in_str_user_settings. BUSINESS envelopes emit only the validated MESSAGES wrapper; other
/// TYPE values emit the complete original envelope. Any validation failure logs an error and
/// stops the node.
/// </summary>
public sealed class CFGConfimerNode : BaseNode
{
    private const string RunType = "RUN";
    private const string BusinessType = "BUSINESS";
    private const string SlotKey = "SLOT";
    private static readonly int[] Cfg1Slots = { 1, 2, 3, 4, 5 };
    private static readonly int[] Cfg2Slots = { 6, 7, 8, 9, 10 };

    private readonly object _stateGate = new();
    private ILogger? _logger;
    private string _localGuid = string.Empty;
    private string _localLocation = string.Empty;
    private DateTimeOffset? _lastBusinessTimestampUtc;

    public CFGConfimerNode()
    {
        Uuid = "90C3A453-044E-45BB-82B4-90499E07693D";
        AddParam();
        AddPins();
    }

    public override string Name => "CFG Confimer";
    public override string DisplayName { get; set; } = "CFG Confimer";
    public override string Category => "C";

    public BasePin LocalGuidInputPin { get; } =
        new("in_string_local_guid", DataType.STRING, PinDirection.Input, required: false);

    public BasePin UserSettingsInputPin { get; } =
        new("in_str_user_settings", DataType.STRING, PinDirection.Input, required: false);

    public BasePin MessagesInputPin { get; } =
        new("in_string_messages", DataType.STRING, PinDirection.Input, required: false);

    public BasePin MessagesOutputPin { get; } =
        new("out_string_messages", DataType.STRING, PinDirection.Output);

    public BasePin OthersOutputPin { get; } =
        new("out_string_others", DataType.STRING, PinDirection.Output);

    public override void AddPins()
    {
        AddInputPin(LocalGuidInputPin);
        AddInputPin(UserSettingsInputPin);
        AddInputPin(MessagesInputPin);
        AddOutputPin(MessagesOutputPin);
        AddOutputPin(OthersOutputPin);
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        // This node has no custom parameters; LocalLocation is derived from in_str_user_settings.
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;

        lock (_stateGate)
        {
            _localGuid = string.Empty;
            _localLocation = string.Empty;
            _lastBusinessTimestampUtc = null;
        }

        context.Logger.LogInformation(
            "CFG Confimer initialized; waiting for in_str_user_settings to derive LocalLocation and in_string_local_guid.");
        return Task.CompletedTask;
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        Running = true;
        context.Logger.LogInformation(
            "CFG Confimer is running; BUSINESS messages route to output_string_messages and other TYPE values route to output_string_others.");
        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        lock (_stateGate)
            _lastBusinessTimestampUtc = null;
        SetIdle();
        context.Logger.LogInformation("CFG Confimer stopped.");
        return Task.CompletedTask;
    }

    public override Task Shutdown(INodeContext context, CancellationToken ct = default) =>
        Stop(context, ct);

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (ReferenceEquals(pin, LocalGuidInputPin))
        {
            HandleLocalGuid(value);
            return;
        }

        if (ReferenceEquals(pin, UserSettingsInputPin))
        {
            HandleUserSettingsInput(value);
            return;
        }

        if (ReferenceEquals(pin, MessagesInputPin))
            HandleMessagesInput(value);
    }

    private void HandleLocalGuid(Sample? value)
    {
        if (value is not StringSample text || text.StringValue is null)
        {
            _logger?.LogError("CFG Confimer ignored input_string_local_guid: expected StringSample.");
            return;
        }

        if (!Guid.TryParse(text.StringValue.Trim(), out var parsedGuid))
        {
            _logger?.LogError(
                "CFG Confimer rejected local GUID {Guid}: invalid GUID format.",
                text.StringValue);
            return;
        }

        var normalized = parsedGuid.ToString("D").ToLowerInvariant();
        lock (_stateGate)
        {
            if (!string.Equals(_localGuid, normalized, StringComparison.Ordinal))
                _lastBusinessTimestampUtc = null;
            _localGuid = normalized;
        }

        _logger?.LogInformation("CFG Confimer accepted local GUID {Guid}.", normalized);
    }

    private void HandleUserSettingsInput(Sample? value)
    {
        if (value is not StringSample text || text.StringValue is null)
        {
            RejectSettingsAndStop(
                "CFG Confimer ignored in_str_user_settings: expected StringSample.");
            return;
        }

        if (!TryParseObject(text.StringValue, out var settings, out var error))
        {
            RejectSettingsAndStop(
                "CFG Confimer rejected in_str_user_settings: {Error}.",
                error);
            return;
        }

        if (!TryGetStringValues(settings, SlotKey, out var slotValues, out error))
        {
            RejectSettingsAndStop(
                "CFG Confimer rejected in_str_user_settings: {Error}.",
                error);
            return;
        }

        if (!TryResolveLocationFromSlots(slotValues, out var location, out error))
        {
            RejectSettingsAndStop(
                "CFG Confimer rejected in_str_user_settings SLOT values: {Error}.",
                error);
            return;
        }

        var changed = false;
        lock (_stateGate)
        {
            changed = !string.Equals(_localLocation, location, StringComparison.Ordinal);
            _localLocation = location;
            if (changed)
                _lastBusinessTimestampUtc = null;
        }

        _logger?.LogInformation(
            "CFG Confimer derived LocalLocation={Location} from in_str_user_settings; changed={Changed}.",
            location,
            changed);
    }

    private void RejectSettingsAndStop(string message, params object?[] args)
    {
        lock (_stateGate)
        {
            _localLocation = string.Empty;
            _lastBusinessTimestampUtc = null;
        }

        RejectAndStop(message, args);
    }

    private void HandleMessagesInput(Sample? value)
    {
        if (value is not StringSample text || text.StringValue is null)
        {
            RejectAndStop("CFG Confimer ignored input_string_messages: expected StringSample.");
            return;
        }

        string localGuid;
        string localLocation;
        lock (_stateGate)
        {
            localGuid = _localGuid;
            localLocation = _localLocation;
        }

        if (string.IsNullOrWhiteSpace(localGuid))
        {
            RejectAndStop(
                "CFG Confimer rejected message because input_string_local_guid has not received a valid GUID yet.");
            return;
        }

        if (string.IsNullOrWhiteSpace(localLocation))
        {
            RejectAndStop(
                "CFG Confimer rejected message because in_str_user_settings has not supplied a valid SLOT-derived location yet.");
            return;
        }

        if (!TryParseObject(text.StringValue, out var envelope, out var error))
        {
            RejectAndStop("CFG Confimer rejected message: {Error}.", error);
            return;
        }

        if (!TryGetString(envelope, "GUID", out var guidText) ||
            !Guid.TryParse(guidText.Trim(), out var targetGuid))
        {
            RejectAndStop("CFG Confimer rejected message: GUID is missing or invalid.");
            return;
        }

        var targetGuidNormalized = targetGuid.ToString("D").ToLowerInvariant();
        if (!string.Equals(targetGuidNormalized, localGuid, StringComparison.Ordinal))
        {
            RejectAndStop(
                "CFG Confimer rejected message for GUID {TargetGuid}; local GUID is {LocalGuid}.",
                targetGuidNormalized,
                localGuid);
            return;
        }

        if (!TryGetString(envelope, "TYPE", out var type) || string.IsNullOrWhiteSpace(type))
        {
            RejectAndStop("CFG Confimer rejected message: TYPE is missing.");
            return;
        }

        if (!TryGetString(envelope, "LOCATION", out var location) ||
            !IsLocation(location))
        {
            RejectAndStop("CFG Confimer rejected message: LOCATION must be CFG-1 or CFG-2.");
            return;
        }

        if (!string.Equals(NormalizeLocation(location), localLocation, StringComparison.Ordinal))
        {
            RejectAndStop(
                "CFG Confimer rejected message LOCATION {Location}; local LOCATION is {LocalLocation}.",
                location,
                localLocation);
            return;
        }

        if (string.Equals(type.Trim(), BusinessType, StringComparison.OrdinalIgnoreCase))
        {
            ProcessBusinessMessage(envelope, localGuid, localLocation);
        }
        else
        {
            ProcessOtherMessage(envelope, type.Trim());
        }
    }

    private void ProcessBusinessMessage(JsonObject envelope, string localGuid, string localLocation)
    {
        if (!TryValidateProtocolFields(envelope, out var data, out var timestampUtc, out var error))
        {
            RejectAndStop("CFG Confimer rejected BUSINESS message: {Error}.", error);
            return;
        }

        if (!TryValidateBusinessData(data, localLocation, out error))
        {
            RejectAndStop("CFG Confimer rejected BUSINESS payload: {Error}.", error);
            return;
        }

        if (Get(data, "MESSAGES") is not JsonArray messages)
        {
            RejectAndStop("CFG Confimer rejected BUSINESS payload: DATA.MESSAGES must be a JSON array.");
            return;
        }

        lock (_stateGate)
        {
            if (_lastBusinessTimestampUtc is { } lastTimestampUtc &&
                timestampUtc <= lastTimestampUtc)
            {
                RejectAndStop(
                    "CFG Confimer rejected TIMESTAMP {Timestamp}; it must be newer than the last successfully dispatched BUSINESS timestamp {LastTimestamp}.",
                    timestampUtc,
                    lastTimestampUtc);
                return;
            }

            var messagesEnvelope = new JsonObject
            {
                ["MESSAGES"] = messages.DeepClone()
            };
            var json = JsonSerializer.Serialize(messagesEnvelope);
            var sample = StringSample.CreateSample();
            sample.SetValue(json, MessagesOutputPin.Name);
            var sent = MessagesOutputPin.SendSampleWithDispatchLog(
                sample,
                $"confirmed BUSINESS MESSAGES for {localGuid}",
                LogLevel.Information);

            if (sent)
            {
                _lastBusinessTimestampUtc = timestampUtc;
            }
            else
            {
                _logger?.LogWarning(
                    "CFG Confimer did not cache TIMESTAMP {Timestamp} because output_string_messages was not connected.",
                    timestampUtc);
            }
        }
    }

    private void ProcessOtherMessage(JsonObject envelope, string type)
    {
        if (string.Equals(type, RunType, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogInformation(
                "CFG Confimer received RUN startup control for LOCATION {Location}; forwarding the original envelope.",
                Get(envelope, "LOCATION")?.ToJsonString() ?? "<unknown>");
        }

        var json = JsonSerializer.Serialize(envelope);
        var sample = StringSample.CreateSample();
        sample.SetValue(json, OthersOutputPin.Name);
        var sent = OthersOutputPin.SendSampleWithDispatchLog(
            sample,
            $"original TYPE {type} envelope",
            LogLevel.Information);

        if (!sent)
        {
            _logger?.LogTrace(
                "CFG Confimer did not send TYPE {Type} because output_string_others was not connected.",
                type);
        }
    }

    private static bool TryValidateBusinessData(
        JsonObject data,
        string location,
        out string? error)
    {
        error = null;
        var expectedStartSlot = string.Equals(NormalizeLocation(location), "CFG-1", StringComparison.Ordinal)
            ? 1
            : 6;
        var expectedEndSlot = expectedStartSlot + 4;

        if (Get(data, "MESSAGES") is not JsonArray messages || messages.Count != 5)
        {
            error = "DATA.MESSAGES must contain exactly five records";
            return false;
        }

        var seenSlots = new bool[5];
        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index] is not JsonObject message)
            {
                error = $"DATA.MESSAGES[{index}] must be a JSON object";
                return false;
            }

            if (!TryGetString(message, "SLOT", out var slotText) ||
                !int.TryParse(slotText, out var slot) ||
                slot < expectedStartSlot ||
                slot > expectedEndSlot)
            {
                error = $"DATA.MESSAGES[{index}].SLOT must be an integer from {expectedStartSlot} through {expectedEndSlot}";
                return false;
            }

            var slotIndex = slot - expectedStartSlot;
            if (seenSlots[slotIndex])
            {
                error = $"DATA.MESSAGES contains duplicate SLOT {slot}";
                return false;
            }
            seenSlots[slotIndex] = true;

            if (!TryGetString(message, "FSN", out var fsn))
            {
                error = $"DATA.MESSAGES[{index}].FSN must be a string";
                return false;
            }
            if (!TryGetString(message, "IMEI", out var imei))
            {
                error = $"DATA.MESSAGES[{index}].IMEI must be a string";
                return false;
            }

            if (string.IsNullOrEmpty(fsn) != string.IsNullOrEmpty(imei))
            {
                error = $"DATA.MESSAGES[{index}] must set both FSN and IMEI or leave both empty";
                return false;
            }

            if (!TryGetString(message, "STATUS", out var statusText))
            {
                error = $"DATA.MESSAGES[{index}].STATUS must be IDLE or TEST";
                return false;
            }

            var status = statusText.Trim().ToUpperInvariant();
            if (!string.Equals(status, "IDLE", StringComparison.Ordinal) &&
                !string.Equals(status, "TEST", StringComparison.Ordinal))
            {
                error = $"DATA.MESSAGES[{index}].STATUS must be IDLE or TEST";
                return false;
            }

            if (string.IsNullOrEmpty(fsn) && !string.Equals(status, "IDLE", StringComparison.Ordinal))
            {
                error = $"DATA.MESSAGES[{index}] with empty FSN/IMEI must use STATUS IDLE";
                return false;
            }
        }

        for (var index = 0; index < seenSlots.Length; index++)
        {
            if (!seenSlots[index])
            {
                error = $"DATA.MESSAGES must contain SLOT {expectedStartSlot + index} exactly once";
                return false;
            }
        }

        return true;
    }

    private void RejectAndStop(string message, params object?[] args)
    {
        _logger?.LogError(message, args);
        Running = false;
    }

    private static bool TryParseObject(string json, out JsonObject envelope, out string? error)
    {
        envelope = new JsonObject();
        error = null;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject parsed)
            {
                error = "message must be a JSON object";
                return false;
            }

            envelope = parsed;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"invalid JSON: {ex.Message}";
            return false;
        }
    }

    private static bool TryValidateProtocolFields(
        JsonObject envelope,
        out JsonObject data,
        out DateTimeOffset timestampUtc,
        out string? error)
    {
        data = new JsonObject();
        timestampUtc = default;
        error = null;

        if (!TryGetString(envelope, "TIMESTAMP", out var timestamp) ||
            string.IsNullOrWhiteSpace(timestamp) ||
            !DateTimeOffset.TryParse(timestamp, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsedTimestamp))
        {
            error = "TIMESTAMP must be a valid timestamp";
            return false;
        }

        if (Get(envelope, "DATA") is not JsonObject parsedData)
        {
            error = "DATA must be a JSON object";
            return false;
        }

        timestampUtc = parsedTimestamp.ToUniversalTime();
        data = parsedData;
        return true;
    }

    private static bool TryGetStringValues(
        JsonObject obj,
        string key,
        out IReadOnlyList<string> values,
        out string? error)
    {
        values = Array.Empty<string>();
        error = null;

        var node = Get(obj, key);
        if (node is null)
        {
            error = $"{key} is required";
            return false;
        }

        if (node is JsonArray array)
        {
            var parsedValues = new List<string>(array.Count);
            foreach (var item in array)
            {
                if (item is not JsonValue value ||
                    !value.TryGetValue<string>(out var text) ||
                    text is null)
                {
                    error = $"{key} must contain only strings";
                    return false;
                }

                parsedValues.Add(text);
            }

            values = parsedValues;
            return true;
        }

        if (node is JsonValue single &&
            single.TryGetValue<string>(out var singleText) &&
            singleText is not null)
        {
            values = new[] { singleText };
            return true;
        }

        error = $"{key} must be a string or an array of strings";
        return false;
    }

    private static bool TryResolveLocationFromSlots(
        IReadOnlyList<string> slotValues,
        out string location,
        out string? error)
    {
        location = string.Empty;
        error = null;
        var slots = new HashSet<int>();

        foreach (var rawValue in slotValues)
        {
            foreach (var token in rawValue.Split(
                         ',',
                         StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot) ||
                    slot is < 1 or > 10)
                {
                    error = $"SLOT value {token} must be an integer from 1 through 10";
                    return false;
                }

                if (!slots.Add(slot))
                {
                    error = $"SLOT {slot} is duplicated";
                    return false;
                }
            }
        }

        if (slots.Count == Cfg1Slots.Length && Cfg1Slots.All(slots.Contains))
        {
            location = "CFG-1";
            return true;
        }

        if (slots.Count == Cfg2Slots.Length && Cfg2Slots.All(slots.Contains))
        {
            location = "CFG-2";
            return true;
        }

        if (slots.Count == 0)
        {
            error = "SLOT must contain 1,2,3,4,5 or 6,7,8,9,10";
            return false;
        }

        var hasCfg1Slots = slots.Any(slot => slot is >= 1 and <= 5);
        var hasCfg2Slots = slots.Any(slot => slot is >= 6 and <= 10);
        if (hasCfg1Slots && hasCfg2Slots)
        {
            error = "SLOT mixes the CFG-1 and CFG-2 slot ranges";
            return false;
        }

        error = "SLOT must contain exactly 1,2,3,4,5 or 6,7,8,9,10";
        return false;
    }

    private static bool IsLocation(string value) =>
        string.Equals(NormalizeLocation(value), "CFG-1", StringComparison.Ordinal) ||
        string.Equals(NormalizeLocation(value), "CFG-2", StringComparison.Ordinal);

    private static string NormalizeLocation(string value) =>
        value.Trim().ToUpperInvariant();

    private static JsonNode? Get(JsonObject obj, string key)
    {
        if (obj.TryGetPropertyValue(key, out var exact))
            return exact;

        foreach (var pair in obj)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
                return pair.Value;
        }

        return null;
    }

    private static bool TryGetString(JsonObject obj, string key, out string value)
    {
        value = string.Empty;
        if (Get(obj, key) is JsonValue node &&
            node.TryGetValue<string>(out var text) &&
            text is not null)
        {
            value = text;
            return true;
        }

        return false;
    }

}

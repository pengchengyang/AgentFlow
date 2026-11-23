// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="CFGSHandlerNode.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.CFGSHandler;

/// <summary>
/// Builds the CFG-1 and CFG-2 startup and BUSINESS envelopes from SLOT/FSN/IMEI batches received
/// from the upper workflow. Launch and stop pin behavior remains reserved for a later implementation.
/// </summary>
public sealed class CFGSHandlerNode : BaseNode
{
    private const string RunType = "RUN";
    private const string RunAckType = "RUN_ACK";
    private const string BusinessType = "BUSINESS";
    private const string ProtocolVersion = "1.0";
    private const string Cfg1Location = "CFG-1";
    private const string Cfg2Location = "CFG-2";
    private const string IdleStatus = "IDLE";
    private const string TestStatus = "TEST";

    private readonly BasePin _rawFromCfgInputPin =
        new("in_str_raw_from_cfg", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _fromUpperInputPin =
        new("in_str_from_upper", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _userSettingsInputPin =
        new("in_str_user_settings", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _runReturnInt64InputPin =
        new("int64_run_ret", DataType.INT64, PinDirection.Input, required: false);

    private readonly BasePin _exitReturnInt64InputPin =
        new("int64_exit_ret", DataType.INT64, PinDirection.Input, required: false);

    private readonly BasePin _runReturnStringInputPin =
        new("str_run_ret", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _stopReturnStringInputPin =
        new("str_stop_ret", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _stopReturnInt64InputPin =
        new("int64_stop_ret", DataType.INT64, PinDirection.Input, required: false);

    private readonly BasePin _exitReturnStringInputPin =
        new("str_exit_ret", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _cmdRunCfgOutputPin =
        new("cmd_run_cfg", DataType.STRING, PinDirection.Output);

    private readonly BasePin _cmdStopCfgOutputPin =
        new("cmd_stop_cfg", DataType.STRING, PinDirection.Output);

    private readonly BasePin _cmdExitCfgOutputPin =
        new("cmd_exit_cfg", DataType.STRING, PinDirection.Output);

    private readonly BasePin _cmdSureExitCfgOutputPin =
        new("cmd_sure_exit_cfg", DataType.STRING, PinDirection.Output);

    private readonly BasePin _messageOutputPin =
        new("output_string_message", DataType.STRING, PinDirection.Output);

    private ILogger? _logger;
    private string? _cfg1Guid;
    private string? _cfg2Guid;
    private long _cfg1Sequence;
    private long _cfg2Sequence;
    private bool _cfg1RunSent;
    private bool _cfg2RunSent;
    private bool _cfg1RunAckReceived;
    private bool _cfg2RunAckReceived;

    private sealed record SlotIdentity(int Slot, string Fsn, string Imei)
    {
        public bool HasCompleteIdentity =>
            !string.IsNullOrWhiteSpace(Fsn) && !string.IsNullOrWhiteSpace(Imei);
    }

    public CFGSHandlerNode()
    {
        Uuid = "CEC9E7C3-7F5D-48BD-BA1D-CCBDF22E7820";
        AddParam();
        AddPins();
    }

    public override string Name => "CFGS Handler";
    public override string DisplayName { get; set; } = "CFGS Handler";
    public override string Category => "C";

    public override void AddPins()
    {
        AddInputPin(_rawFromCfgInputPin);
        AddInputPin(_fromUpperInputPin);
        AddInputPin(_userSettingsInputPin);
        AddInputPin(_runReturnInt64InputPin);
        AddInputPin(_exitReturnInt64InputPin);
        AddInputPin(_runReturnStringInputPin);
        AddInputPin(_exitReturnStringInputPin);
        AddInputPin(_stopReturnStringInputPin);
        AddInputPin(_stopReturnInt64InputPin);
        AddOutputPin(_cmdRunCfgOutputPin);
        AddOutputPin(_cmdStopCfgOutputPin);
        AddOutputPin(_cmdExitCfgOutputPin);
        AddOutputPin(_cmdSureExitCfgOutputPin);
        AddOutputPin(_messageOutputPin);
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        _cfg1Guid = null;
        _cfg2Guid = null;
        _cfg1Sequence = 0;
        _cfg2Sequence = 0;
        _cfg1RunSent = false;
        _cfg2RunSent = false;
        _cfg1RunAckReceived = false;
        _cfg2RunAckReceived = false;
        return Task.CompletedTask;
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        Running = true;
        return Task.CompletedTask;
    }

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (ReferenceEquals(pin, _fromUpperInputPin))
        {
            HandleUpperMessages(value);
            return;
        }

        if (ReferenceEquals(pin, _userSettingsInputPin))
        {
            HandleUserSettings(value);
            return;
        }

        if (ReferenceEquals(pin, _rawFromCfgInputPin))
            HandleCfgMessage(value);
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        _cfg1Guid = null;
        _cfg2Guid = null;
        _cfg1Sequence = 0;
        _cfg2Sequence = 0;
        _cfg1RunSent = false;
        _cfg2RunSent = false;
        _cfg1RunAckReceived = false;
        _cfg2RunAckReceived = false;
        return Task.CompletedTask;
    }

    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;

    private void HandleUserSettings(Sample? value)
    {
        if (value is not StringSample text || string.IsNullOrWhiteSpace(text.StringValue))
        {
            Fail("CFGS Handler rejected user settings: expected a non-empty STRING payload.");
            return;
        }

        if (!TryParseGuids(text.StringValue, out var cfg1Guid, out var cfg2Guid, out var error))
        {
            Fail("CFGS Handler rejected user settings: {Error}.", error);
            return;
        }

        _cfg1Guid = cfg1Guid;
        _cfg2Guid = cfg2Guid;
        _cfg1RunSent = false;
        _cfg2RunSent = false;
        _cfg1RunAckReceived = false;
        _cfg2RunAckReceived = false;
        _logger?.LogInformation(
            "CFGS Handler cached CFG GUIDs: CFG-1={Cfg1Guid}, CFG-2={Cfg2Guid}.",
            _cfg1Guid,
            _cfg2Guid);
    }

    private void HandleUpperMessages(Sample? value)
    {
        if (value is not StringSample text || string.IsNullOrWhiteSpace(text.StringValue))
        {
            Fail("CFGS Handler rejected upper message: expected a non-empty STRING payload.");
            return;
        }

        // TODO: This parser assumes the upper workflow sends SLOT/FSN/IMEI batches in the agreed
        // MESSAGES JSON array. The transport and producer-side validation are intentionally outside
        // this node until the upstream contract is finalized.
        if (!TryParseSlotIdentities(
                text.StringValue,
                out var slots,
                out var stoppedByDuplicate,
                out var error))
        {
            if (stoppedByDuplicate)
                Running = false;
            else
                Fail("CFGS Handler rejected upper message: {Error}.", error);
            return;
        }

        if (string.IsNullOrWhiteSpace(_cfg1Guid) || string.IsNullOrWhiteSpace(_cfg2Guid))
        {
            Fail(
                "CFGS Handler cannot build CFGS messages because user settings have not supplied both GUIDs. CFG-1={Cfg1Guid}, CFG-2={Cfg2Guid}.",
                _cfg1Guid ?? "<missing>",
                _cfg2Guid ?? "<missing>");
            return;
        }

        var cfg1Messages = BuildMessages(slots, 1, out var cfg1HasTestData);
        var cfg2Messages = BuildMessages(slots, 6, out var cfg2HasTestData);
        if (!cfg1HasTestData && !cfg2HasTestData)
        {
            _logger?.LogTrace("CFGS Handler skipped both envelopes because all slots are IDLE.");
            return;
        }

        EnsureRunSent();

        var timestamp = FormatTimestamp(DateTimeOffset.UtcNow);
        if (cfg1HasTestData)
        {
            var envelope = CreateBusinessEnvelope(
                _cfg1Guid,
                Cfg1Location,
                ++_cfg1Sequence,
                timestamp,
                cfg1Messages);
            SendEnvelope(envelope, Cfg1Location, BusinessType);
        }

        if (cfg2HasTestData)
        {
            var envelope = CreateBusinessEnvelope(
                _cfg2Guid,
                Cfg2Location,
                ++_cfg2Sequence,
                timestamp,
                cfg2Messages);
            SendEnvelope(envelope, Cfg2Location, BusinessType);
        }
    }

    private void EnsureRunSent()
    {
        if (!_cfg1RunSent)
            _cfg1RunSent = SendRun(_cfg1Guid!, Cfg1Location, ref _cfg1Sequence);
        if (!_cfg2RunSent)
            _cfg2RunSent = SendRun(_cfg2Guid!, Cfg2Location, ref _cfg2Sequence);
    }

    private bool SendRun(string guid, string location, ref long sequence)
    {
        var envelope = CreateControlEnvelope(
            guid,
            location,
            RunType,
            ++sequence,
            FormatTimestamp(DateTimeOffset.UtcNow));
        return SendEnvelope(envelope, location, RunType);
    }

    private bool SendEnvelope(JsonObject envelope, string location, string type)
    {
        var json = JsonSerializer.Serialize(envelope);
        var sample = StringSample.CreateSample();
        sample.SetValue(json, _messageOutputPin.Name);
        var sent = _messageOutputPin.SendSampleWithDispatchLog(
            sample,
            $"CFGS {location} {type} envelope",
            LogLevel.Information);

        if (!sent)
        {
            _logger?.LogWarning(
                "CFGS Handler could not send the {Location} {Type} envelope because {Pin} is not connected.",
                location,
                type,
                _messageOutputPin.Name);
        }

        return sent;
    }

    private JsonArray BuildMessages(
        IReadOnlyDictionary<int, SlotIdentity> slots,
        int startSlot,
        out bool hasTestData)
    {
        var messages = new JsonArray();
        hasTestData = false;
        for (var slot = startSlot; slot < startSlot + 5; slot++)
        {
            var hasIdentity =
                slots.TryGetValue(slot, out var identity) &&
                identity.HasCompleteIdentity;
            if (hasIdentity)
                hasTestData = true;

            messages.Add(new JsonObject
            {
                ["SLOT"] = slot.ToString(CultureInfo.InvariantCulture),
                ["FSN"] = hasIdentity ? identity!.Fsn : string.Empty,
                ["IMEI"] = hasIdentity ? identity!.Imei : string.Empty,
                ["STATUS"] = hasIdentity ? TestStatus : IdleStatus,
            });
        }

        return messages;
    }

    private static JsonObject CreateBusinessEnvelope(
        string guid,
        string location,
        long sequence,
        string timestamp,
        JsonArray messages)
        => new()
        {
            ["GUID"] = guid,
            ["LOCATION"] = location,
            ["TYPE"] = BusinessType,
            ["VERSION"] = ProtocolVersion,
            ["SEQ"] = sequence,
            ["TIMESTAMP"] = timestamp,
            ["DATA"] = new JsonObject
            {
                ["MESSAGES"] = messages,
            },
        };

    private static JsonObject CreateControlEnvelope(
        string guid,
        string location,
        string type,
        long sequence,
        string timestamp)
        => new()
        {
            ["GUID"] = guid,
            ["LOCATION"] = location,
            ["TYPE"] = type,
            ["VERSION"] = ProtocolVersion,
            ["SEQ"] = sequence,
            ["TIMESTAMP"] = timestamp,
            ["DATA"] = new JsonObject(),
        };

    private void HandleCfgMessage(Sample? value)
    {
        if (value is not StringSample text || string.IsNullOrWhiteSpace(text.StringValue))
        {
            Fail("CFGS Handler rejected CFG message: expected a non-empty STRING payload.");
            return;
        }

        JsonObject envelope;
        try
        {
            if (JsonNode.Parse(text.StringValue) is not JsonObject parsed)
            {
                Fail("CFGS Handler rejected CFG message: payload must be a JSON object.");
                return;
            }

            envelope = parsed;
        }
        catch (JsonException ex)
        {
            Fail("CFGS Handler rejected CFG message: invalid JSON: {Message}", ex.Message);
            return;
        }

        if (!TryGetRequiredString(envelope, "TYPE", out var type))
        {
            Fail("CFGS Handler rejected CFG message: TYPE is required.");
            return;
        }

        if (!string.Equals(type, RunAckType, StringComparison.OrdinalIgnoreCase))
        {
            _logger?.LogTrace(
                "CFGS Handler ignored CFG message TYPE {Type}; only RUN_ACK is consumed.",
                type);
            return;
        }

        if (!TryGetRequiredString(envelope, "GUID", out var guidText) ||
            !Guid.TryParse(guidText, out var guid))
        {
            Fail("CFGS Handler rejected RUN_ACK: GUID is invalid.");
            return;
        }

        if (!TryGetRequiredString(envelope, "LOCATION", out var location))
        {
            Fail("CFGS Handler rejected RUN_ACK: LOCATION is required.");
            return;
        }

        var normalizedGuid = guid.ToString("D").ToUpperInvariant();
        var normalizedLocation = location.Trim().ToUpperInvariant();
        if ((normalizedLocation != Cfg1Location ||
             !string.Equals(normalizedGuid, _cfg1Guid, StringComparison.Ordinal)) &&
            (normalizedLocation != Cfg2Location ||
             !string.Equals(normalizedGuid, _cfg2Guid, StringComparison.Ordinal)))
        {
            Fail(
                "CFGS Handler rejected RUN_ACK for unexpected GUID/LOCATION: {Guid}/{Location}.",
                normalizedGuid,
                normalizedLocation);
            return;
        }

        if (!TryGetProperty(envelope, "DATA", out var dataNode) || dataNode is not JsonObject data ||
            !TryGetRequiredString(data, "RESULT", out var result))
        {
            Fail("CFGS Handler rejected RUN_ACK: DATA.RESULT is required.");
            return;
        }

        var state = string.Empty;
        TryGetOptionalString(data, "STATE", out state);

        if (!string.Equals(result.Trim(), "OK", StringComparison.OrdinalIgnoreCase))
        {
            Fail(
                "CFGS Handler received RUN_ACK with failure result for {Location}: RESULT={Result}, STATE={State}.",
                normalizedLocation,
                result,
                state);
            return;
        }

        if (normalizedLocation == Cfg1Location)
        {
            _cfg1RunAckReceived = true;
        }
        else
        {
            _cfg2RunAckReceived = true;
        }

        _logger?.LogInformation(
            "CFGS Handler received RUN_ACK for {Location}; RESULT={Result}, STATE={State}, CFG-1 acknowledged={Cfg1Acknowledged}, CFG-2 acknowledged={Cfg2Acknowledged}.",
            normalizedLocation,
            result,
            state,
            _cfg1RunAckReceived,
            _cfg2RunAckReceived);
    }

    private bool TryParseSlotIdentities(
        string json,
        out IReadOnlyDictionary<int, SlotIdentity> slots,
        out bool stoppedByDuplicate,
        out string? error)
    {
        slots = new Dictionary<int, SlotIdentity>();
        stoppedByDuplicate = false;
        error = null;

        JsonObject root;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject parsed)
            {
                error = "message must be a JSON object";
                return false;
            }

            root = parsed;
        }
        catch (JsonException ex)
        {
            error = $"invalid JSON: {ex.Message}";
            return false;
        }

        if (!TryGetProperty(root, "MESSAGES", out var messagesNode) ||
            messagesNode is not JsonArray messages ||
            messages.Count is < 1 or > 10)
        {
            error = "MESSAGES must be a JSON array containing between one and ten records";
            return false;
        }

        var parsedSlots = new Dictionary<int, SlotIdentity>();
        for (var index = 0; index < messages.Count; index++)
        {
            if (messages[index] is not JsonObject message)
            {
                error = $"MESSAGES[{index}] must be a JSON object";
                return false;
            }

            if (!TryGetRequiredString(message, "SLOT", out var slotText) ||
                !int.TryParse(slotText, NumberStyles.Integer, CultureInfo.InvariantCulture, out var slot) ||
                slot is < 1 or > 10)
            {
                error = $"MESSAGES[{index}].SLOT must be an integer string from 1 through 10";
                return false;
            }

            if (!TryGetOptionalString(message, "FSN", out var fsn) ||
                !TryGetOptionalString(message, "IMEI", out var imei))
            {
                error = $"MESSAGES[{index}].FSN and IMEI must be strings when present";
                return false;
            }

            fsn = fsn.Trim();
            imei = imei.Trim();
            if (string.IsNullOrEmpty(fsn) ^ string.IsNullOrEmpty(imei))
            {
                _logger?.LogWarning(
                    "CFGS Handler treated MESSAGES[{Index}] SLOT {Slot} as IDLE because only one of FSN or IMEI was supplied.",
                    index,
                    slot);
                fsn = string.Empty;
                imei = string.Empty;
            }

            if (parsedSlots.ContainsKey(slot))
            {
                _logger?.LogWarning(
                    "CFGS Handler found duplicate SLOT {Slot} in MESSAGES; the last value was retained and the node will stop.",
                    slot);
                stoppedByDuplicate = true;
            }

            parsedSlots[slot] = new SlotIdentity(slot, fsn, imei);
        }

        slots = parsedSlots;
        return !stoppedByDuplicate;
    }

    private static bool TryParseGuids(
        string json,
        out string cfg1Guid,
        out string cfg2Guid,
        out string? error)
    {
        cfg1Guid = string.Empty;
        cfg2Guid = string.Empty;
        error = null;

        JsonObject root;
        try
        {
            if (JsonNode.Parse(json) is not JsonObject parsed)
            {
                error = "settings must be a JSON object";
                return false;
            }

            root = parsed;
        }
        catch (JsonException ex)
        {
            error = $"invalid settings JSON: {ex.Message}";
            return false;
        }

        if (!TryGetProperty(root, "GUID", out var guidNode) || guidNode is not JsonObject guidObject)
        {
            error = "GUID must be a JSON object containing CFG-1 and CFG-2";
            return false;
        }

        if (!TryGetValidGuid(guidObject, Cfg1Location, out cfg1Guid))
        {
            error = "GUID.CFG-1 must contain a valid GUID";
            return false;
        }

        if (!TryGetValidGuid(guidObject, Cfg2Location, out cfg2Guid))
        {
            error = "GUID.CFG-2 must contain a valid GUID";
            return false;
        }

        return true;
    }

    private static bool TryGetValidGuid(JsonObject obj, string key, out string guid)
    {
        guid = string.Empty;
        if (!TryGetRequiredString(obj, key, out var rawGuid) ||
            !Guid.TryParse(rawGuid.Trim(), out var parsedGuid))
        {
            return false;
        }

        guid = parsedGuid.ToString("D").ToUpperInvariant();
        return true;
    }

    private static bool TryGetRequiredString(JsonObject obj, string key, out string value)
    {
        value = string.Empty;
        if (!TryGetProperty(obj, key, out var node) ||
            node is not JsonValue jsonValue ||
            !jsonValue.TryGetValue<string>(out var parsed) ||
            string.IsNullOrWhiteSpace(parsed))
        {
            return false;
        }

        value = parsed;
        return true;
    }

    private static bool TryGetOptionalString(JsonObject obj, string key, out string value)
    {
        value = string.Empty;
        if (!TryGetProperty(obj, key, out var node) || node is null)
            return true;

        if (node is not JsonValue jsonValue || !jsonValue.TryGetValue<string>(out var parsed))
            return false;

        value = parsed;
        return true;
    }

    private static bool TryGetProperty(JsonObject obj, string key, out JsonNode? node)
    {
        foreach (var pair in obj)
        {
            if (string.Equals(pair.Key, key, StringComparison.OrdinalIgnoreCase))
            {
                node = pair.Value;
                return true;
            }
        }

        node = null;
        return false;
    }

    private static string FormatTimestamp(DateTimeOffset timestamp) =>
        timestamp.UtcDateTime.ToString(
            "yyyy-MM-dd'T'HH:mm:ss.ffffff'Z'",
            CultureInfo.InvariantCulture);

    private void Fail(string message, params object?[] args)
    {
        Running = false;
        _logger?.LogError(message, args);
    }
}
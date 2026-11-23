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

    private const int Cfg2ExitVerificationAttempts = 3;
    private const int Cfg2ExitVerificationDelayMilliseconds = 750;

    private enum ExitPhase
    {
        None,
        WaitingInitialQuery,
        WaitingKill,
        WaitingVerification,
    }

    private readonly BasePin _messageInputPin =
        new("input_str_message", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _fromUpperInputPin =
        new("in_str_from_upper", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _userSettingsInputPin =
        new("in_str_user_settings", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _startReturnStringInputPin1 =
        new("str_start_ret1", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _startReturnInt64InputPin1 =
        new("int64_start_ret1", DataType.INT64, PinDirection.Input, required: false);

    private readonly BasePin _sureExitReturnStringInputPin1 =
        new("str_sure_exit1", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _sureExitReturnInt64InputPin1 =
        new("int64_sure_exit1", DataType.INT64, PinDirection.Input, required: false);

    private readonly BasePin _cmdStartCfgOutputPin1 =
        new("cmd_start_cfg1", DataType.STRING, PinDirection.Output);

    private readonly BasePin _cmdSureExitCfgOutputPin1 =
        new("cmd_sure_exit_cfg1", DataType.STRING, PinDirection.Output);

    private readonly BasePin _startReturnStringInputPin2 =
        new("str_start_ret2", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _startReturnInt64InputPin2 =
        new("int64_start_ret2", DataType.INT64, PinDirection.Input, required: false);

    private readonly BasePin _sureExitReturnStringInputPin2 =
        new("str_sure_exit2", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _sureExitReturnInt64InputPin2 =
        new("int64_sure_exit2", DataType.INT64, PinDirection.Input, required: false);

    private readonly BasePin _cmdStartCfgOutputPin2 =
        new("cmd_start_cfg2", DataType.STRING, PinDirection.Output);

    private readonly BasePin _cmdSureExitCfgOutputPin2 =
        new("cmd_sure_exit_cfg2", DataType.STRING, PinDirection.Output);

    private readonly BasePin _messageOutputPin =
        new("output_string_message", DataType.STRING, PinDirection.Output);

    private ILogger? _logger;
    private IReadOnlyDictionary<string, IReadOnlyList<string>> _userSettings =
        new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
    private string? _cfg1StartCommand;
    private string? _cfg2StartCommand;
    private string? _cfg1Guid;
    private string? _cfg2Guid;
    private long _cfg1Sequence;
    private long _cfg2Sequence;
    private bool _cfg1RunSent;
    private bool _cfg2RunSent;
    private bool _cfg1RunAckReceived;
    private bool _cfg2RunAckReceived;
    private ExitPhase _cfg1ExitPhase;
    private long _cfg1ExitPid;
    private string _cfg1ExitOutput = string.Empty;
    private ExitPhase _cfg2ExitPhase;
    private long _cfg2ExitPid;
    private string _cfg2ExitOutput = string.Empty;
    private int _cfg2ExitVerificationAttempt;
    private CancellationTokenSource? _cfg2ExitRetryCts;

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
        AddInputPin(_messageInputPin);
        AddInputPin(_fromUpperInputPin);
        AddInputPin(_userSettingsInputPin);
        AddInputPin(_startReturnStringInputPin1);
        AddInputPin(_startReturnInt64InputPin1);
        AddInputPin(_sureExitReturnStringInputPin1);
        AddInputPin(_sureExitReturnInt64InputPin1);
        AddOutputPin(_cmdStartCfgOutputPin1);
        AddOutputPin(_cmdSureExitCfgOutputPin1);
        AddInputPin(_startReturnStringInputPin2);
        AddInputPin(_startReturnInt64InputPin2);
        AddInputPin(_sureExitReturnStringInputPin2);
        AddInputPin(_sureExitReturnInt64InputPin2);
        AddOutputPin(_cmdStartCfgOutputPin2);
        AddOutputPin(_cmdSureExitCfgOutputPin2);
        AddOutputPin(_messageOutputPin);
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        _userSettings = new Dictionary<string, IReadOnlyList<string>>(
            StringComparer.OrdinalIgnoreCase);
        _cfg1StartCommand = null;
        _cfg2StartCommand = null;
        _cfg1Guid = null;
        _cfg2Guid = null;
        _cfg1Sequence = 0;
        _cfg2Sequence = 0;
        _cfg1RunSent = false;
        _cfg2RunSent = false;
        _cfg1RunAckReceived = false;
        _cfg2RunAckReceived = false;
        _cfg1ExitPhase = ExitPhase.None;
        _cfg1ExitPid = 0;
        _cfg1ExitOutput = string.Empty;
        _cfg2ExitPhase = ExitPhase.None;
        _cfg2ExitPid = 0;
        _cfg2ExitOutput = string.Empty;
        _cfg2ExitVerificationAttempt = 0;
        CancelCfg2ExitRetry();
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

        if (ReferenceEquals(pin, _sureExitReturnStringInputPin1))
        {
            HandleCfg1ExitString(value);
            return;
        }

        if (ReferenceEquals(pin, _sureExitReturnInt64InputPin1))
        {
            HandleCfg1ExitExitCode(value);
            return;
        }

        if (ReferenceEquals(pin, _sureExitReturnStringInputPin2))
        {
            HandleCfg2ExitString(value);
            return;
        }

        if (ReferenceEquals(pin, _sureExitReturnInt64InputPin2))
        {
            HandleCfg2ExitExitCode(value);
            return;
        }

        if (ReferenceEquals(pin, _messageInputPin))
            HandleCfgMessage(value);
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        _userSettings = new Dictionary<string, IReadOnlyList<string>>(
            StringComparer.OrdinalIgnoreCase);
        _cfg1StartCommand = null;
        _cfg2StartCommand = null;
        _cfg1Guid = null;
        _cfg2Guid = null;
        _cfg1Sequence = 0;
        _cfg2Sequence = 0;
        _cfg1RunSent = false;
        _cfg2RunSent = false;
        _cfg1RunAckReceived = false;
        _cfg2RunAckReceived = false;
        _cfg1ExitPhase = ExitPhase.None;
        _cfg1ExitPid = 0;
        _cfg1ExitOutput = string.Empty;
        _cfg2ExitPhase = ExitPhase.None;
        _cfg2ExitPid = 0;
        _cfg2ExitOutput = string.Empty;
        _cfg2ExitVerificationAttempt = 0;
        CancelCfg2ExitRetry();
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

        if (!TryParseUserSettings(
                text.StringValue,
                out var settings,
                out var cfg1Guid,
                out var cfg2Guid,
                out var error))
        {
            Fail("CFGS Handler rejected user settings: {Error}.", error);
            return;
        }

        if (!CFGSCommandBuilder.TryBuildStartCommands(
                settings,
                out var cfg1StartCommand,
                out var cfg2StartCommand,
                out error))
        {
            Fail("CFGS Handler could not prepare startup commands: {Error}.", error);
            return;
        }

        _userSettings = settings;
        _cfg1StartCommand = cfg1StartCommand;
        _cfg2StartCommand = cfg2StartCommand;
        _cfg1Guid = cfg1Guid;
        _cfg2Guid = cfg2Guid;
        _cfg1RunSent = false;
        _cfg2RunSent = false;
        _cfg1RunAckReceived = false;
        _cfg2RunAckReceived = false;
        _logger?.LogInformation(
            "CFGS Handler cached {KeyCount} user setting key(s): CFG-1={Cfg1Guid}, CFG-2={Cfg2Guid}.",
            _userSettings.Count,
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

        if (!CFGSCommandBuilder.TryParseUpperMessages(
                text.StringValue,
                out var items,
                out var error))
        {
            Fail("CFGS Handler rejected upper message: {Error}.", error);
            return;
        }

        if (!CFGSCommandBuilder.TryBuildGroupMessages(
                items,
                Cfg1Location,
                out var cfg1Messages,
                out var cfg1HasTestData,
                out error) ||
            !CFGSCommandBuilder.TryBuildGroupMessages(
                items,
                Cfg2Location,
                out var cfg2Messages,
                out var cfg2HasTestData,
                out error))
        {
            Fail("CFGS Handler could not build CFGS business messages: {Error}.", error);
            return;
        }

        if (!cfg1HasTestData && !cfg2HasTestData)
        {
            _logger?.LogTrace("CFGS Handler skipped both envelopes because all slots are IDLE.");
            return;
        }

        var timestamp = FormatTimestamp(DateTimeOffset.UtcNow);
        if (cfg1HasTestData)
        {
            if (string.IsNullOrWhiteSpace(_cfg1Guid))
            {
                Fail("CFGS Handler cannot build CFG-1 messages because GUID.CFG-1 is missing.");
                return;
            }

            EnsureRunSent(Cfg1Location);
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
            if (string.IsNullOrWhiteSpace(_cfg2Guid))
            {
                Fail("CFGS Handler cannot build CFG-2 messages because GUID.CFG-2 is missing.");
                return;
            }

            EnsureRunSent(Cfg2Location);
            var envelope = CreateBusinessEnvelope(
                _cfg2Guid,
                Cfg2Location,
                ++_cfg2Sequence,
                timestamp,
                cfg2Messages);
            SendEnvelope(envelope, Cfg2Location, BusinessType);
        }
    }

    private void EnsureRunSent(string location)
    {
        if (string.Equals(location, Cfg1Location, StringComparison.Ordinal))
        {
            if (!_cfg1RunSent)
                _cfg1RunSent = SendRun(_cfg1Guid!, Cfg1Location, ref _cfg1Sequence);
            return;
        }

        if (string.Equals(location, Cfg2Location, StringComparison.Ordinal))
        {
            if (!_cfg2RunSent)
                _cfg2RunSent = SendRun(_cfg2Guid!, Cfg2Location, ref _cfg2Sequence);
        }
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

    private static bool TryParseUserSettings(
        string json,
        out IReadOnlyDictionary<string, IReadOnlyList<string>> settings,
        out string cfg1Guid,
        out string cfg2Guid,
        out string? error)
    {
        settings = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
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

        var parsedSettings = new Dictionary<string, IReadOnlyList<string>>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var pair in root)
        {
            if (pair.Value is not JsonArray values)
            {
                error = $"settings value for '{pair.Key}' must be a JSON array of strings";
                return false;
            }

            var parsedValues = new List<string>(values.Count);
            foreach (var item in values)
            {
                if (item is not JsonValue value ||
                    !value.TryGetValue<string>(out var text) ||
                    text is null)
                {
                    error = $"settings value for '{pair.Key}' must contain only strings";
                    return false;
                }

                parsedValues.Add(text);
            }

            parsedSettings[pair.Key] = parsedValues;
        }

        if (!TryGetValidGuid(parsedSettings, "GUID.CFG-1", out cfg1Guid))
        {
            error = "GUID.CFG-1 must contain a valid GUID";
            return false;
        }

        if (!TryGetValidGuid(parsedSettings, "GUID.CFG-2", out cfg2Guid))
        {
            error = "GUID.CFG-2 must contain a valid GUID";
            return false;
        }

        settings = parsedSettings;
        return true;
    }

    private static bool TryGetValidGuid(
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings,
        string key,
        out string guid)
    {
        guid = string.Empty;
        if (!settings.TryGetValue(key, out var values))
            return false;

        var rawGuid = values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (rawGuid is null || !Guid.TryParse(rawGuid.Trim(), out var parsedGuid))
            return false;

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

    private bool SendStartCommand(string location)
    {
        string? command;
        BasePin outputPin;
        if (string.Equals(location, CFGSCommandBuilder.Cfg1, StringComparison.Ordinal))
        {
            command = _cfg1StartCommand;
            outputPin = _cmdStartCfgOutputPin1;
        }
        else if (string.Equals(location, CFGSCommandBuilder.Cfg2, StringComparison.Ordinal))
        {
            command = _cfg2StartCommand;
            outputPin = _cmdStartCfgOutputPin2;
        }
        else
        {
            _logger?.LogError(
                "CFGS Handler cannot send a startup command for unknown LOCATION {Location}.",
                location);
            return false;
        }

        return SendCommand(outputPin, command, $"{location} startup command");
    }

    private bool SendSureExitCommand(string location, long pid)
    {
        if (string.Equals(location, CFGSCommandBuilder.Cfg1, StringComparison.Ordinal))
            return StartCfg1ExitSequence(pid);

        if (string.Equals(location, CFGSCommandBuilder.Cfg2, StringComparison.Ordinal))
            return StartCfg2ExitSequence(pid);

        _logger?.LogError(
            "CFGS Handler cannot send an exit command for unknown LOCATION {Location}.",
            location);
        return false;
    }

    private bool StartCfg1ExitSequence(long pid)
    {
        if (_cfg1ExitPhase != ExitPhase.None)
        {
            _logger?.LogError(
                "CFGS Handler rejected a new CFG-1 exit sequence because phase {Phase} is active.",
                _cfg1ExitPhase);
            return false;
        }

        if (!CFGSCommandBuilder.TryBuildCfg1PidQueryCommand(
                pid,
                out var command,
                out var error))
        {
            _logger?.LogError("CFGS Handler could not prepare the CFG-1 PID query: {Error}.", error);
            return false;
        }

        _cfg1ExitPid = pid;
        _cfg1ExitOutput = string.Empty;
        _cfg1ExitPhase = ExitPhase.WaitingInitialQuery;
        if (!SendCommand(
                _cmdSureExitCfgOutputPin1,
                command,
                $"CFG-1 initial PID query for PID {pid}"))
        {
            _cfg1ExitPhase = ExitPhase.None;
            return false;
        }

        _logger?.LogInformation(
            "CFGS Handler started the CFG-1 exit sequence for PID {Pid}.",
            pid);
        return true;
    }

    private void HandleCfg1ExitString(Sample? value)
    {
        if (value is not StringSample text)
        {
            _logger?.LogError(
                "CFGS Handler ignored CFG-1 exit output: expected StringSample, got {Type}.",
                value?.Type.ToString() ?? "null");
            return;
        }

        if (_cfg1ExitPhase == ExitPhase.None)
        {
            _logger?.LogWarning(
                "CFGS Handler ignored CFG-1 exit output because no exit sequence is active.");
            return;
        }

        _cfg1ExitOutput = text.StringValue ?? string.Empty;
    }

    private void HandleCfg1ExitExitCode(Sample? value)
    {
        if (value is not Int64Sample { Int64Value: { } exitCode })
        {
            _logger?.LogError(
                "CFGS Handler ignored CFG-1 exit code: expected Int64Sample, got {Type}.",
                value?.Type.ToString() ?? "null");
            return;
        }

        if (_cfg1ExitPhase == ExitPhase.None)
        {
            _logger?.LogWarning(
                "CFGS Handler ignored CFG-1 exit code {ExitCode} because no exit sequence is active.",
                exitCode);
            return;
        }

        var output = _cfg1ExitOutput;
        _cfg1ExitOutput = string.Empty;
        ProcessCfg1ExitStep(exitCode, output);
    }

    private void ProcessCfg1ExitStep(long exitCode, string output)
    {
        switch (_cfg1ExitPhase)
        {
            case ExitPhase.WaitingInitialQuery:
                ProcessCfg1InitialQuery(exitCode, output);
                return;

            case ExitPhase.WaitingKill:
                ProcessCfg1KillResult(exitCode, output);
                return;

            case ExitPhase.WaitingVerification:
                ProcessCfg1VerificationQuery(exitCode, output);
                return;

            default:
                _logger?.LogWarning(
                    "CFGS Handler ignored CFG-1 exit result because no exit phase is active.");
                return;
        }
    }

    private void ProcessCfg1InitialQuery(long exitCode, string output)
    {
        if (!CFGSCommandBuilder.TryClassifyTasklistOutput(
                _cfg1ExitPid,
                output,
                out var result,
                out var error))
        {
            _cfg1ExitPhase = ExitPhase.None;
            Fail(
                "CFGS Handler could not classify the initial CFG-1 PID query for PID {Pid}: {Error}. ExitCode={ExitCode}.",
                _cfg1ExitPid,
                error,
                exitCode);
            return;
        }

        if (result == PidLookupResult.NotFound)
        {
            _cfg1ExitPhase = ExitPhase.None;
            _logger?.LogInformation(
                "CFGS Handler confirmed that CFG-1 PID {Pid} is not running; no kill was required.",
                _cfg1ExitPid);
            return;
        }

        if (!CFGSCommandBuilder.TryBuildCfg1KillProcessTreeCommand(
                _cfg1ExitPid,
                out var command,
                out var commandError))
        {
            _cfg1ExitPhase = ExitPhase.None;
            Fail(
                "CFGS Handler could not prepare the CFG-1 taskkill command: {Error}.",
                commandError);
            return;
        }

        _cfg1ExitPhase = ExitPhase.WaitingKill;
        if (!SendCommand(
                _cmdSureExitCfgOutputPin1,
                command,
                $"CFG-1 taskkill for PID {_cfg1ExitPid}"))
        {
            _cfg1ExitPhase = ExitPhase.None;
            Fail(
                "CFGS Handler could not send the CFG-1 taskkill command for PID {Pid}.",
                _cfg1ExitPid);
        }
    }

    private void ProcessCfg1KillResult(long exitCode, string output)
    {
        _logger?.LogInformation(
            "CFGS Handler finished the CFG-1 taskkill for PID {Pid}; ExitCode={ExitCode}, OutputLength={OutputLength}.",
            _cfg1ExitPid,
            exitCode,
            output.Length);

        if (!CFGSCommandBuilder.TryBuildCfg1PidQueryCommand(
                _cfg1ExitPid,
                out var command,
                out var error))
        {
            _cfg1ExitPhase = ExitPhase.None;
            Fail(
                "CFGS Handler could not prepare the CFG-1 verification PID query: {Error}.",
                error);
            return;
        }

        _cfg1ExitPhase = ExitPhase.WaitingVerification;
        if (!SendCommand(
                _cmdSureExitCfgOutputPin1,
                command,
                $"CFG-1 verification PID query for PID {_cfg1ExitPid}"))
        {
            _cfg1ExitPhase = ExitPhase.None;
            Fail(
                "CFGS Handler could not send the CFG-1 verification PID query for PID {Pid}.",
                _cfg1ExitPid);
        }
    }

    private void ProcessCfg1VerificationQuery(long exitCode, string output)
    {
        if (!CFGSCommandBuilder.TryClassifyTasklistOutput(
                _cfg1ExitPid,
                output,
                out var result,
                out var error))
        {
            _cfg1ExitPhase = ExitPhase.None;
            Fail(
                "CFGS Handler could not classify the verification CFG-1 PID query for PID {Pid}: {Error}. ExitCode={ExitCode}.",
                _cfg1ExitPid,
                error,
                exitCode);
            return;
        }

        _cfg1ExitPhase = ExitPhase.None;
        if (result == PidLookupResult.NotFound)
        {
            _logger?.LogInformation(
                "CFGS Handler verified that CFG-1 PID {Pid} has exited.",
                _cfg1ExitPid);
            return;
        }

        Fail(
            "CFGS Handler verification found CFG-1 PID {Pid} still running after taskkill. ExitCode={ExitCode}.",
            _cfg1ExitPid,
            exitCode);
    }

    private bool StartCfg2ExitSequence(long pid)
    {
        if (_cfg2ExitPhase != ExitPhase.None)
        {
            _logger?.LogError(
                "CFGS Handler rejected a new CFG-2 exit sequence because phase {Phase} is active.",
                _cfg2ExitPhase);
            return false;
        }
        CancelCfg2ExitRetry();

        if (!CFGSCommandBuilder.TryBuildCfg2PidQueryCommand(
                _userSettings,
                pid,
                out var command,
                out var error))
        {
            _logger?.LogError("CFGS Handler could not prepare the CFG-2 PID query: {Error}.", error);
            return false;
        }

        _cfg2ExitPid = pid;
        _cfg2ExitOutput = string.Empty;
        _cfg2ExitVerificationAttempt = 0;
        _cfg2ExitPhase = ExitPhase.WaitingInitialQuery;
        if (!SendCommand(
                _cmdSureExitCfgOutputPin2,
                command,
                $"CFG-2 initial PID query for PID {pid}"))
        {
            _cfg2ExitPhase = ExitPhase.None;
            return false;
        }

        _logger?.LogInformation(
            "CFGS Handler started the CFG-2 exit sequence for PID {Pid}.",
            pid);
        return true;
    }

    private void HandleCfg2ExitString(Sample? value)
    {
        if (value is not StringSample text)
        {
            _logger?.LogError(
                "CFGS Handler ignored CFG-2 exit output: expected StringSample, got {Type}.",
                value?.Type.ToString() ?? "null");
            return;
        }

        if (_cfg2ExitPhase == ExitPhase.None)
        {
            _logger?.LogWarning(
                "CFGS Handler ignored CFG-2 exit output because no exit sequence is active.");
            return;
        }

        _cfg2ExitOutput = text.StringValue ?? string.Empty;
    }

    private void HandleCfg2ExitExitCode(Sample? value)
    {
        if (value is not Int64Sample { Int64Value: { } exitCode })
        {
            _logger?.LogError(
                "CFGS Handler ignored CFG-2 exit code: expected Int64Sample, got {Type}.",
                value?.Type.ToString() ?? "null");
            return;
        }

        if (_cfg2ExitPhase == ExitPhase.None)
        {
            _logger?.LogWarning(
                "CFGS Handler ignored CFG-2 exit code {ExitCode} because no exit sequence is active.",
                exitCode);
            return;
        }

        var output = _cfg2ExitOutput;
        _cfg2ExitOutput = string.Empty;
        ProcessCfg2ExitStep(exitCode, output);
    }

    private void ProcessCfg2ExitStep(long exitCode, string output)
    {
        switch (_cfg2ExitPhase)
        {
            case ExitPhase.WaitingInitialQuery:
                ProcessCfg2InitialQuery(exitCode, output);
                return;

            case ExitPhase.WaitingKill:
                ProcessCfg2KillResult(exitCode, output);
                return;

            case ExitPhase.WaitingVerification:
                ProcessCfg2VerificationQuery(exitCode, output);
                return;

            default:
                _logger?.LogWarning(
                    "CFGS Handler ignored CFG-2 exit result because no exit phase is active.");
                return;
        }
    }

    private void ProcessCfg2InitialQuery(long exitCode, string output)
    {
        if (!CFGSCommandBuilder.TryClassifyTasklistOutput(
                _cfg2ExitPid,
                output,
                out var result,
                out var error))
        {
            _cfg2ExitPhase = ExitPhase.None;
            Fail(
                "CFGS Handler could not classify the initial CFG-2 PID query for PID {Pid}: {Error}. ExitCode={ExitCode}.",
                _cfg2ExitPid,
                error,
                exitCode);
            return;
        }

        if (result == PidLookupResult.NotFound)
        {
            _cfg2ExitPhase = ExitPhase.None;
            _logger?.LogInformation(
                "CFGS Handler confirmed that CFG-2 PID {Pid} is not running; no kill was required.",
                _cfg2ExitPid);
            return;
        }

        if (!CFGSCommandBuilder.TryBuildCfg2KillProcessTreeCommand(
                _userSettings,
                _cfg2ExitPid,
                out var command,
                out var commandError))
        {
            _cfg2ExitPhase = ExitPhase.None;
            Fail(
                "CFGS Handler could not prepare the CFG-2 taskkill command: {Error}.",
                commandError);
            return;
        }

        _cfg2ExitPhase = ExitPhase.WaitingKill;
        if (!SendCommand(
                _cmdSureExitCfgOutputPin2,
                command,
                $"CFG-2 taskkill for PID {_cfg2ExitPid}"))
        {
            _cfg2ExitPhase = ExitPhase.None;
            Fail(
                "CFGS Handler could not send the CFG-2 taskkill command for PID {Pid}.",
                _cfg2ExitPid);
        }
    }

    private void ProcessCfg2KillResult(long exitCode, string output)
    {
        _logger?.LogInformation(
            "CFGS Handler finished the CFG-2 taskkill for PID {Pid}; ExitCode={ExitCode}, OutputLength={OutputLength}. Verification will retry up to {Attempts} times.",
            _cfg2ExitPid,
            exitCode,
            output.Length,
            Cfg2ExitVerificationAttempts);

        _cfg2ExitVerificationAttempt = 0;
        SendCfg2VerificationQuery();
    }

    private bool SendCfg2VerificationQuery()
    {
        if (_cfg2ExitPhase == ExitPhase.None)
        {
            _logger?.LogError(
                "CFGS Handler could not send the CFG-2 verification query because the exit sequence is not active.");
            return false;
        }

        _cfg2ExitVerificationAttempt++;
        if (!CFGSCommandBuilder.TryBuildCfg2PidQueryCommand(
                _userSettings,
                _cfg2ExitPid,
                out var command,
                out var error))
        {
            EndCfg2ExitSequence();
            Fail(
                "CFGS Handler could not prepare the CFG-2 verification query: {Error}.",
                error);
            return false;
        }

        _cfg2ExitPhase = ExitPhase.WaitingVerification;
        if (!SendCommand(
                _cmdSureExitCfgOutputPin2,
                command,
                $"CFG-2 verification PID query {_cfg2ExitVerificationAttempt}/{Cfg2ExitVerificationAttempts} for PID {_cfg2ExitPid}"))
        {
            EndCfg2ExitSequence();
            Fail(
                "CFGS Handler could not send the CFG-2 verification query for PID {Pid}.",
                _cfg2ExitPid);
            return false;
        }

        return true;
    }

    private void ProcessCfg2VerificationQuery(long exitCode, string output)
    {
        if (!CFGSCommandBuilder.TryClassifyTasklistOutput(
                _cfg2ExitPid,
                output,
                out var result,
                out var error))
        {
            EndCfg2ExitSequence();
            Fail(
                "CFGS Handler could not classify the CFG-2 verification query for PID {Pid}: {Error}. ExitCode={ExitCode}.",
                _cfg2ExitPid,
                error,
                exitCode);
            return;
        }

        if (result == PidLookupResult.NotFound)
        {
            EndCfg2ExitSequence();
            _logger?.LogInformation(
                "CFGS Handler verified that CFG-2 PID {Pid} has exited after {Attempt}/{Attempts} verification attempt(s).",
                _cfg2ExitPid,
                _cfg2ExitVerificationAttempt,
                Cfg2ExitVerificationAttempts);
            return;
        }

        if (_cfg2ExitVerificationAttempt < Cfg2ExitVerificationAttempts)
        {
            _logger?.LogInformation(
                "CFGS Handler found CFG-2 PID {Pid} still present on verification attempt {Attempt}/{Attempts}; retrying in {DelayMilliseconds} ms.",
                _cfg2ExitPid,
                _cfg2ExitVerificationAttempt,
                Cfg2ExitVerificationAttempts,
                Cfg2ExitVerificationDelayMilliseconds);
            ScheduleCfg2ExitVerificationRetry();
            return;
        }

        EndCfg2ExitSequence();
        Fail(
            "CFGS Handler verification still found CFG-2 PID {Pid} after {Attempts} attempts. ExitCode={ExitCode}.",
            _cfg2ExitPid,
            Cfg2ExitVerificationAttempts,
            exitCode);
    }

    private void EndCfg2ExitSequence()
    {
        _cfg2ExitPhase = ExitPhase.None;
        _cfg2ExitOutput = string.Empty;
        CancelCfg2ExitRetry();
    }

    private void ScheduleCfg2ExitVerificationRetry()
    {
        CancelCfg2ExitRetry();
        var cts = new CancellationTokenSource();
        _cfg2ExitRetryCts = cts;
        var expectedPid = _cfg2ExitPid;
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(
                    Cfg2ExitVerificationDelayMilliseconds,
                    cts.Token).ConfigureAwait(false);
                if (!Running ||
                    _cfg2ExitPhase != ExitPhase.WaitingVerification ||
                    _cfg2ExitPid != expectedPid)
                {
                    return;
                }

                SendCfg2VerificationQuery();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger?.LogError(
                    ex,
                    "CFGS Handler failed while scheduling the CFG-2 verification retry for PID {Pid}.",
                    expectedPid);
            }
        });
    }

    private void CancelCfg2ExitRetry()
    {
        var cts = _cfg2ExitRetryCts;
        _cfg2ExitRetryCts = null;
        if (cts is null)
            return;

        cts.Cancel();
        cts.Dispose();
    }

    private bool SendCommand(BasePin outputPin, string? command, string operation)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            _logger?.LogError(
                "CFGS Handler could not send {Operation} because the command has not been prepared.",
                operation);
            return false;
        }

        var sample = StringSample.CreateSample();
        sample.SetValue(command, outputPin.Name);
        var sent = outputPin.SendSampleWithDispatchLog(
            sample,
            operation,
            LogLevel.Information);

        if (!sent)
        {
            _logger?.LogWarning(
                "CFGS Handler could not send {Operation} because output pin {Pin} is not connected.",
                operation,
                outputPin.Name);
        }

        return sent;
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

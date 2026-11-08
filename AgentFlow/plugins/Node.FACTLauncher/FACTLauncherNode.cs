// -----------------------------------------------------------------------
// <copyright company="Your Company" file="FACTLauncherNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;
using AgentFlow.Contracts;
using BroadcastKit;
using BroadcastKit.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.FACTLauncher;

/// <summary>
/// Default skeleton for a FACT launch node. It declares the input / output pins and
/// the endpoint configuration expected by the future business implementation.
/// </summary>
public sealed class FACTLauncherNode : BaseNode
{
    private const string SettingsDirectory = @"C:\AC710\UserSettings";
    private const string SettingsFilePath = @"C:\AC710\UserSettings\UserSettings.ini";
    private const string DefaultDisplayName = "FACT Launcher";
    private const string DefaultPosition = "A";
    private static readonly string[] PositionOptions = { "A", "B", "C", "D", "F" };

    private readonly BasePin _slotFsnImeiInputPin =
        new("str_slotfsnimei_run", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _retInputPin =
        new("input_int64_ret", DataType.INT64, PinDirection.Input, required: false);

    private readonly BasePin _outputPin =
        new("output_string_host", DataType.STRING, PinDirection.Output);

    private readonly BasePin _cmdOutputPin =
        new("output_string_cmd", DataType.STRING, PinDirection.Output);

    private readonly object _inputGate = new();
    private ILogger? _logger;
    private string _displayName = DefaultDisplayName;
    private string _position = DefaultPosition;
    private string? _lastInput;
    private string? _fsn;
    private string? _imei;
    private string? _factCommand;
    private string? _ip;
    private string? _slot;
    private string? _porthub;
    private int? _fsnImeiSlot;

    public FACTLauncherNode()
    {
        Uuid = "1617370B-3D5A-4685-85E6-A9BFE294E44D";
        AddParam();
        AddPins();
        RefreshPosition();
    }

    public override string Name => "FACT Launcher";

    public override string DisplayName
    {
        get => string.IsNullOrWhiteSpace(_position)
            ? _displayName
            : $"{_displayName} {_position}";
        set => _displayName = StripPositionSuffix(value);
    }

    public override string Category => "Data";

    public override void AddPins()
    {
        AddInputPin(_slotFsnImeiInputPin);
        AddInputPin(_retInputPin);
        AddOutputPin(_outputPin);
        AddOutputPin(_cmdOutputPin);
    }

    protected override void AddParam()
    {
        base.AddParam();
        AddParameter(new NodeParameter(
            "Endpoint", typeof(string), "", isEditable: true,
            group: "Connection", zone: "FACTLauncherZone"));
        AddParameter(new NodeParameter(
            "TimeoutSeconds", typeof(int), 30, isEditable: true,
            group: "Connection", zone: "FACTLauncherZone"));
        AddParameter(new NodeParameter(
            "FactKey", typeof(string), "", isEditable: true,
            group: "Request", zone: "FACTLauncherZone"));
        AddParameter(new NodeParameter(
            "Position", typeof(string), DefaultPosition,
            isEditable: true, group: "General", zone: "FACTLauncherZone",
            editor: ParameterEditorKind.SingleSelect,
            options: PositionOptions));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("Endpoint", out var endpoint))
            Param("Endpoint").Value = ParameterValues.GetString(endpoint, "") ?? "";
        if (parameters.TryGetValue("TimeoutSeconds", out var timeout))
            Param("TimeoutSeconds").Value = ParameterValues.GetInt(timeout, 30);
        if (parameters.TryGetValue("FactKey", out var factKey))
            Param("FactKey").Value = ParameterValues.GetString(factKey, "") ?? "";
        if (parameters.TryGetValue("Position", out var position))
            Param("Position").Value = ParameterValues.GetString(position, DefaultPosition) ?? DefaultPosition;

        RefreshPosition();
    }

    public override async Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        RefreshPosition();
        lock (_inputGate)
        {
            _fsn = null;
            _imei = null;
            _factCommand = null;
            _ip = null;
            _slot = null;
            _porthub = null;
            _fsnImeiSlot = null;
        }

        await EnsureSettingsLoadedAsync().ConfigureAwait(false);
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        Running = true;
        _logger = context.Logger;
        RefreshPosition();
        _logger?.LogTrace(
            "FACTLauncher {DisplayName} is running with endpoint {Endpoint}.",
            DisplayName,
            Param("Endpoint").Value);

        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        _logger = null;
        lock (_inputGate)
        {
            _fsn = null;
            _imei = null;
            _factCommand = null;
            _ip = null;
            _slot = null;
            _porthub = null;
            _fsnImeiSlot = null;
        }

        return Task.CompletedTask;
    }

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (ReferenceEquals(pin, _slotFsnImeiInputPin))
        {
            if (value is not StringSample input ||
                input.StringValue is not { } inputText)
            {
                _logger?.LogError(
                    "FACTLauncher ignored invalid input on {Pin}: expected StringSample with non-null StringValue, got {Type}.",
                    pin.Name,
                    value?.Type.ToString() ?? "null");
                return;
            }

            lock (_inputGate)
                _lastInput = inputText;

            _logger?.LogTrace("FACTLauncher received FSN/IMEI input for FACT key {FactKey}: {Input}",
                Param("FactKey").Value, inputText);

            if (ProcessRunInput(inputText) && HasCompleteCachedSettings())
                SendCommand();
        }
    }

    private bool HasCompleteCachedSettings()
    {
        lock (_inputGate)
        {
            return HasCompleteCachedSettingsCore();
        }
    }

    private bool HasCompleteCachedSettingsCore()
    {
        return !string.IsNullOrWhiteSpace(_factCommand) &&
               !string.IsNullOrWhiteSpace(_ip) &&
               !string.IsNullOrWhiteSpace(_slot) &&
               !string.IsNullOrWhiteSpace(_porthub);
    }

    private async Task EnsureSettingsLoadedAsync()
    {
        _logger?.LogInformation(
            "FACTLauncher is loading user settings during initialization from {FilePath}: CMD, IP, SLOT, PORTHUB.",
            SettingsFilePath);

        var parser = new UserSettingsParser(_logger, SettingsDirectory, SettingsFilePath);
        if (!parser.TryLoad(out var settings))
        {
            const string message =
                "FACTLauncher initialization failed because UserSettings.ini could not be loaded.";
            _logger?.LogError(
                "{Message} FilePath={FilePath}; command will not be sent.",
                message,
                SettingsFilePath);
            await BroadcastErrorAsync("LauncherSettingsLoadFailed", message).ConfigureAwait(false);
            throw new InvalidOperationException(message);
        }

        var factCommand = JsonKeyValueParser.GetSingleValue(settings, "CMD");
        var ip = JsonKeyValueParser.GetSingleValue(settings, "IP");
        var slot = JsonKeyValueParser.GetSingleValue(settings, "SLOT");
        var portHub = JsonKeyValueParser.GetSingleValue(settings, "PORTHUB");

        lock (_inputGate)
        {
            _factCommand = factCommand;
            _ip = ip;
            _slot = slot;
            _porthub = portHub;
        }

        if (!HasCompleteCachedSettings())
        {
            const string message =
                "FACTLauncher initialization failed because CMD, IP, SLOT, or PORTHUB is missing from UserSettings.ini.";
            _logger?.LogError(
                "{Message} FilePath={FilePath}; command will not be sent.",
                message,
                SettingsFilePath);
            await BroadcastErrorAsync("LauncherSettingsIncomplete", message).ConfigureAwait(false);
            throw new InvalidOperationException(message);
        }

        _logger?.LogInformation(
            "FACTLauncher loaded user settings from {FilePath}: CMD={Command}, IP={Ip}, SLOT={Slot}, PORTHUB={PortHub}.",
            SettingsFilePath,
            factCommand ?? "<missing>",
            ip ?? "<missing>",
            slot ?? "<missing>",
            portHub ?? "<missing>");
    }

    private async Task BroadcastErrorAsync(string errorCode, string message)
    {
        try
        {
            var payload = JsonSerializer.Serialize(new FactLauncherErrorBroadcast(
                Name,
                DisplayName,
                _position,
                errorCode,
                message,
                DateTimeOffset.UtcNow));

            await TopicBus.Instance.PublishAsync(
                BroadcastTopics.FactLauncherError,
                BroadcastTopicMessage.FromPayload(payload),
                ct: CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "FACTLauncher failed to broadcast initialization error {ErrorCode}.",
                errorCode);
        }
    }

    private sealed record FactLauncherErrorBroadcast(
        string NodeName,
        string DisplayName,
        string Position,
        string ErrorCode,
        string Message,
        DateTimeOffset Timestamp);

    private void SendCommand()
    {
        string factCommand;
        string fsn;
        string imei;
        string slotText;
        int fsnImeiSlot;

        lock (_inputGate)
        {
            if (string.IsNullOrWhiteSpace(_fsn) ||
                string.IsNullOrWhiteSpace(_imei) ||
                _fsnImeiSlot is null ||
                !HasCompleteCachedSettingsCore())
            {
                return;
            }

            factCommand = _factCommand!;
            fsn = _fsn;
            imei = _imei;
            slotText = _slot!;
            fsnImeiSlot = _fsnImeiSlot.Value;
        }

        if (!int.TryParse(slotText.Trim(), out var settingsSlot))
        {
            _logger?.LogWarning(
                "FACTLauncher cannot send command because settings SLOT {Slot} is invalid.",
                slotText);
            return;
        }

        if (fsnImeiSlot != settingsSlot)
        {
            _logger?.LogWarning(
                "FACTLauncher ignored FSN/IMEI input for SLOT {InputSlot}; settings SLOT is {SettingsSlot}.",
                fsnImeiSlot,
                settingsSlot);
            return;
        }

        var command = $"{factCommand} /F{fsn} /I{imei}";

        try
        {
            var output = StringSample.CreateSample();
            output.StringValue = command;
            if (_cmdOutputPin.SendSample(output))
            {
                _logger?.LogTrace(
                    "FACTLauncher sent command through {Pin}: {Command}",
                    _cmdOutputPin.Name,
                    command);
            }
            else
            {
                _logger?.LogWarning(
                    "FACTLauncher could not send command through {Pin}.",
                    _cmdOutputPin.Name);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "FACTLauncher failed to send command through {Pin}.",
                _cmdOutputPin.Name);
        }
    }

    private bool ProcessRunInput(string inputText)
    {
        // TODO: Confirm the production FSN/IMEI JSON schema; this parser currently
        // expects an object whose property values are scalar strings.
        if (!JsonKeyValueParser.TryParseStringValues(inputText, out var values))
        {
            _logger?.LogError("FACTLauncher ignored FSN/IMEI input because the JSON payload is invalid.");
            return false;
        }

        if (!TryGetRequiredString(values, "SLOT", out var slotText) ||
            !TryGetRequiredString(values, "FSN", out var fsn) ||
            !TryGetRequiredString(values, "IMEI", out var imei))
        {
            _logger?.LogError(
                "FACTLauncher ignored FSN/IMEI input because SLOT, FSN, and IMEI are required.");
            return false;
        }

        if (!int.TryParse(slotText.Trim(), out var slot) || slot is < 1 or > 10)
        {
            _logger?.LogError("FACTLauncher ignored FSN/IMEI input with invalid SLOT {Slot}.", slotText);
            return false;
        }

        if (!TryGetLauncherGroup(out var launcherGroup))
        {
            _logger?.LogError(
                "FACTLauncher DisplayName {DisplayName} must end with A, B, C, D, or F.",
                DisplayName);
            return false;
        }

        var expectedGroup = GetGroupForSlot(slot);
        if (expectedGroup != launcherGroup)
        {
            _logger?.LogWarning(
                "FACTLauncher {DisplayName} ignored SLOT {Slot}; expected group {ExpectedGroup}.",
                DisplayName,
                slot,
                expectedGroup);
            return false;
        }

        lock (_inputGate)
        {
            _fsn = fsn;
            _imei = imei;
            _fsnImeiSlot = slot;
        }

        _logger?.LogTrace(
            "FACTLauncher stored FSN {Fsn} and IMEI {Imei} for SLOT {Slot}.",
            fsn,
            imei,
            slot);
        return true;
    }

    private bool TryGetLauncherGroup(out char group)
    {
        group = default;
        if (_position is { Length: 1 })
        {
            var value = char.ToUpperInvariant(_position[0]);
            if (value is 'A' or 'B' or 'C' or 'D' or 'F')
            {
                group = value;
                return true;
            }
        }

        return false;
    }

    private static char GetGroupForSlot(int slot) => slot switch
    {
        1 or 6 => 'A',
        2 or 7 => 'B',
        3 or 8 => 'C',
        4 or 9 => 'D',
        5 or 10 => 'F',
        _ => throw new ArgumentOutOfRangeException(nameof(slot)),
    };

    private static bool TryGetRequiredString(
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

    private void RefreshPosition()
    {
        var rawValue = ParameterValues.GetString(Param("Position").Value, DefaultPosition)?.Trim();
        var candidate = rawValue?.ToUpperInvariant();
        _position = Array.IndexOf(PositionOptions, candidate) >= 0
            ? candidate!
            : DefaultPosition;
        Param("Position").Value = _position;
        RefreshBuiltInParameters();
    }

    private static string StripPositionSuffix(string? value)
    {
        var displayName = value?.Trim() ?? "";
        foreach (var position in PositionOptions)
        {
            var suffix = $" {position}";
            if (displayName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                return displayName[..^suffix.Length].TrimEnd();
        }

        return displayName;
    }

    private NodeParameter Param(string name) => NodeParameters.First(p => p.Name == name);
    /// <summary>
    /// Final application-exit hook. Runtime resources were already released by Stop; this node
    /// currently has no additional process-exit cleanup.
    /// </summary>
    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;
}


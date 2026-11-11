// -----------------------------------------------------------------------
// <copyright company="Your Company" file="FACTLauncherNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;
using AgentFlow.Contracts;
using BroadcastKit;
using BroadcastKit.Contracts;
using AgentFlow.Shared.FlashForgeHmi.Broadcasts;
using Microsoft.Extensions.Logging;

namespace Node.FACTLauncher;

/// <summary>
/// FACT launcher node for SLOT-aware FSN/IMEI capture and user-settings coordination.
/// </summary>
public sealed class FACTLauncherNode : BaseNode
{
    private const string DefaultDisplayName = "FACT Launcher";
    private const string DefaultPosition = "A";
    private static readonly string[] PositionOptions = { "A", "B", "C", "D", "F" };

    private readonly BasePin _slotFsnImeiInputPin =
        new("str_slotfsnimei_run", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _settingsInputPin =
        new("input_string_settings", DataType.STRING, PinDirection.Input, required: false);

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
        AddInputPin(_settingsInputPin);
        AddInputPin(_retInputPin);
        AddOutputPin(_outputPin);
        AddOutputPin(_cmdOutputPin);
    }

    protected override void AddParam()
    {
        base.AddParam();
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
        if (parameters.TryGetValue("TimeoutSeconds", out var timeout))
            Param("TimeoutSeconds").Value = ParameterValues.GetInt(timeout, 30);
        if (parameters.TryGetValue("FactKey", out var factKey))
            Param("FactKey").Value = ParameterValues.GetString(factKey, "") ?? "";
        if (parameters.TryGetValue("Position", out var position))
            Param("Position").Value = ParameterValues.GetString(position, DefaultPosition) ?? DefaultPosition;

        RefreshPosition();
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
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

        return Task.CompletedTask;
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        Running = true;
        _logger = context.Logger;
        RefreshPosition();
        _logger?.LogTrace(
            "FACTLauncher {DisplayName} is ready to run and waits for user settings on {Pin}.",
            DisplayName,
            _settingsInputPin.Name);

        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
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

        _logger = null;
        return Task.CompletedTask;
    }

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (ReferenceEquals(pin, _settingsInputPin))
        {
            HandleSettingsInput(value);
            return;
        }

        if (ReferenceEquals(pin, _retInputPin))
        {
            HandleRetInput(value);
            return;
        }

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

            if (!ProcessRunInput(inputText))
                return;

            if (HasCompleteCachedSettings())
            {
                SendCommand();
            }
            else
            {
                int? cachedSlot;
                lock (_inputGate)
                    cachedSlot = _fsnImeiSlot;

                _logger?.LogTrace(
                    "FACTLauncher cached FSN/IMEI data for SLOT {Slot} and waits for user settings on {Pin}.",
                    cachedSlot,
                    _settingsInputPin.Name);
            }
        }
    }

    private void HandleRetInput(Sample? value)
    {
        if (value is not Int64Sample { Int64Value: { } ret })
        {
            _logger?.LogWarning(
                "FACTLauncher ignored invalid input on {Pin}: expected Int64Sample with non-null Int64Value, got {Type}.",
                _retInputPin.Name,
                value?.Type.ToString() ?? "null");
            return;
        }

        try
        {
            var json = JsonSerializer.Serialize(new { RET = ret });
            var output = StringSample.CreateSample();
            output.SetValue(json, _outputPin.Name);
            _outputPin.SendSampleWithDispatchLog(
                output,
                $"RET {ret}",
                LogLevel.Information);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "FACTLauncher failed to convert or publish RET value {Ret}: {Message}",
                ret,
                ex.Message);
        }
    }

    private void HandleSettingsInput(Sample? value)
    {
        if (value is not StringSample { StringValue: { } inputText })
        {
            _logger?.LogWarning(
                "FACTLauncher ignored invalid user-settings input on {Pin}: expected StringSample with non-null StringValue.",
                _settingsInputPin.Name);
            return;
        }

        if (!JsonKeyValueParser.TryParse(inputText, out var settings))
        {
            _logger?.LogWarning(
                "FACTLauncher ignored input on {Pin}: expected a JSON object with string or string-array values.",
                _settingsInputPin.Name);
            return;
        }

        var factCommand = JsonKeyValueParser.GetSingleValue(settings, "CMD");
        var ip = JsonKeyValueParser.GetSingleValue(settings, "IP");
        var slotText = JsonKeyValueParser.GetSingleValue(settings, "SLOT");
        var portHub = JsonKeyValueParser.GetSingleValue(settings, "PORTHUB");

        if (string.IsNullOrWhiteSpace(factCommand) ||
            string.IsNullOrWhiteSpace(ip) ||
            string.IsNullOrWhiteSpace(portHub))
        {
            _logger?.LogWarning(
                "FACTLauncher ignored incomplete user settings; CMD, IP, and PORTHUB are required.");
            return;
        }

        if (string.IsNullOrWhiteSpace(slotText))
        {
            _logger?.LogError(
                "FACTLauncher rejected user settings because SLOT is required.");
            return;
        }

        if (!TryValidateSlotForPosition(slotText, out var settingsSlot))
            return;

        lock (_inputGate)
        {
            _factCommand = factCommand;
            _ip = ip;
            _slot = settingsSlot.ToString();
            _porthub = portHub;
        }

        _logger?.LogInformation(
            "FACTLauncher received user settings: CMD={Command}, IP={Ip}, SLOT={Slot}, PORTHUB={PortHub}.",
            factCommand,
            ip,
            settingsSlot,
            portHub);

        _ = BroadcastLauncherSettingsAsync();
        SendCommand();
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

    private async Task BroadcastLauncherSettingsAsync()
    {
        string? factCommand;
        string? ip;
        string? slot;
        string? portHub;

        lock (_inputGate)
        {
            factCommand = _factCommand;
            ip = _ip;
            slot = _slot;
            portHub = _porthub;
        }

        var samples = new List<Sample>
        {
            CreateStringSample("CMD", factCommand),
            CreateStringSample("IP", ip),
            CreateStringSample("SLOT", slot),
            CreateStringSample("PORTHUB", portHub),
        };

        try
        {
            await TopicBus.Instance.PublishAsync(
                FlashForgeHmiBroadcastSubscriptions.LauncherSettings,
                BroadcastTopicMessage.FromSamples(samples),
                ct: CancellationToken.None).ConfigureAwait(false);

            _logger?.LogTrace(
                "FACTLauncher {DisplayName} broadcast module information with {Count} ordered samples.",
                DisplayName,
                samples.Count);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "FACTLauncher {DisplayName} failed to broadcast module information.",
                DisplayName);
        }
    }

    private static StringSample CreateStringSample(string key, string? value)
    {
        var sample = StringSample.CreateSample();
        sample.SetValue(value ?? string.Empty, key);
        return sample;
    }

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

        if (!TryValidateSlotForPosition(slotText, out var settingsSlot))
            return;

        if (fsnImeiSlot != settingsSlot)
        {
            _logger?.LogError(
                "FACTLauncher rejected command because FSN/IMEI SLOT {InputSlot} does not match settings SLOT {SettingsSlot}.",
                fsnImeiSlot,
                settingsSlot);
            return;
        }

        var command = $"{factCommand} /F{fsn} /I{imei}";

        try
        {
            var output = StringSample.CreateSample();
            output.StringValue = command;
            _cmdOutputPin.SendSampleWithDispatchLog(
                output,
                "FACT launch command",
                LogLevel.Information);
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

        if (!TryValidateSlotForPosition(slotText, out var slot))
            return false;

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

    private bool TryValidateSlotForPosition(string slotText, out int slot)
    {
        slot = 0;

        if (!int.TryParse(slotText.Trim(), out var parsedSlot) || parsedSlot is < 1 or > 10)
        {
            _logger?.LogError(
                "FACTLauncher rejected SLOT {Slot}; expected an integer from 1 to 10.",
                slotText);
            return false;
        }

        if (!TryGetLauncherGroup(out var launcherGroup))
        {
            _logger?.LogError(
                "FACTLauncher DisplayName {DisplayName} must end with A, B, C, D, or F.",
                DisplayName);
            return false;
        }

        var expectedGroup = GetGroupForSlot(parsedSlot);
        if (expectedGroup != launcherGroup)
        {
            _logger?.LogError(
                "FACTLauncher rejected SLOT {Slot}; SLOT maps to position {ExpectedGroup}, but this node is position {ActualGroup}.",
                parsedSlot,
                expectedGroup,
                launcherGroup);
            return false;
        }

        slot = parsedSlot;
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

    /// <summary>Final application-exit hook; this node owns no external resources.</summary>
    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;
}


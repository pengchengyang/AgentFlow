// -----------------------------------------------------------------------
// <copyright company="Your Company" file="FACTLauncherNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.FACTLauncher;

/// <summary>
/// Default skeleton for a FACT launch node. It declares the input / output pins and
/// the endpoint configuration expected by the future business implementation.
/// </summary>
public sealed class FACTLauncherNode : BaseNode
{
    private readonly BasePin _runInputPin =
        new("input_string_run", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _settingsInputPin =
        new("input_string_settings", DataType.STRING, PinDirection.Input, required: false);


    private readonly BasePin _retInputPin =
        new("input_int64_ret", DataType.INT64, PinDirection.Input, required: false);

    private readonly BasePin _outputPin =
        new("output_string_host", DataType.STRING, PinDirection.Output);

    private readonly BasePin _settingsOutputPin =
        new("output_string_settings", DataType.STRING, PinDirection.Output);


    private readonly BasePin _cmdOutputPin =
        new("output_string_cmd", DataType.STRING, PinDirection.Output);

    private readonly object _inputGate = new();
    private ILogger? _logger;
    private string? _lastInput;
    private string? _lastInputSettings;
    private string? _fsn;
    private string? _imei;
    private string? _factCommand;
    private string? _ip;
    private string? _slot;
    private string? _porthub;

    public FACTLauncherNode()
    {
        Uuid = "1617370B-3D5A-4685-85E6-A9BFE294E44D";
        AddParam();
        AddPins();
    }

    public override string Name => "FACT Launcher";
    public override string DisplayName { get; set; } = "FACT Launcher";
    public override string Category => "Data";

    public override void AddPins()
    {
        AddInputPin(_runInputPin);
        AddInputPin(_settingsInputPin);
        AddInputPin(_retInputPin);
        AddOutputPin(_outputPin);
        AddOutputPin(_settingsOutputPin);
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
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("Endpoint", out var endpoint))
            Param("Endpoint").Value = ParameterValues.GetString(endpoint, "") ?? "";
        if (parameters.TryGetValue("TimeoutSeconds", out var timeout))
            Param("TimeoutSeconds").Value = ParameterValues.GetInt(timeout, 30);
        if (parameters.TryGetValue("FactKey", out var factKey))
            Param("FactKey").Value = ParameterValues.GetString(factKey, "") ?? "";
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        lock (_inputGate)
        {
            _fsn = null;
            _imei = null;
            _factCommand = null;
            _ip = null;
            _slot = null;
            _porthub = null;
        }

        return Task.CompletedTask;
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        Running = true;
        _logger = context.Logger;
        _logger?.LogTrace("FACTLauncher is running with endpoint {Endpoint}.", Param("Endpoint").Value);

        lock (_inputGate)
        {
            _fsn = null;
            _imei = null;
            _factCommand = null;
            _ip = null;
            _slot = null;
            _porthub = null;
        }

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
        }

        return Task.CompletedTask;
    }

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (value is not StringSample input || input.StringValue is not { } inputText)
            return;


        if (ReferenceEquals(pin, _settingsInputPin))
        {
            lock (_inputGate)
                _lastInputSettings = inputText;

            if (JsonKeyValueParser.TryParse(inputText, out var settings))
            {
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

                _logger?.LogTrace(
                    "FACTLauncher cached settings CMD={Command}, IP={Ip}, SLOT={Slot}, PORTHUB={PortHub}.",
                    factCommand ?? "<missing>",
                    ip ?? "<missing>",
                    slot ?? "<missing>",
                    portHub ?? "<missing>");
            }
            else
            {
                _logger?.LogError(
                    "FACTLauncher could not parse the JSON key/value payload.");
            }

            return;
        }

        if (!ReferenceEquals(pin, _runInputPin))
            return;

        lock (_inputGate)
            _lastInput = inputText;

        _logger?.LogTrace("FACTLauncher received run input for FACT key {FactKey}: {Input}",
            Param("FactKey").Value, inputText);

        if (ProcessRunInput(inputText))
            RequestUserSettings();
    }

    private void RequestUserSettings()
    {
        const string GetUserSettings = "get user settings";

        try
        {
            var request = StringSample.CreateSample();
            request.StringValue = GetUserSettings;
            if (_settingsOutputPin.SendSample(request))
            {
                _logger?.LogTrace(
                    "FACTLauncher sent {Request} through {Pin}.",
                    GetUserSettings,
                    _settingsOutputPin.Name);
            }
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(
                ex,
                "FACTLauncher failed to send {Request} through {Pin}.",
                GetUserSettings,
                _settingsOutputPin.Name);
        }
    }
    private bool ProcessRunInput(string inputText)
    {
        if (!JsonKeyValueParser.TryParseStringValues(inputText, out var values))
        {
            _logger?.LogError("FACTLauncher ignored run input because the JSON payload is invalid.");
            return false;
        }

        if (!TryGetRequiredString(values, "SLOT", out var slotText) ||
            !TryGetRequiredString(values, "FSN", out var fsn) ||
            !TryGetRequiredString(values, "IMEI", out var imei))
        {
            _logger?.LogError(
                "FACTLauncher ignored run input because SLOT, FSN, and IMEI are required.");
            return false;
        }

        if (!int.TryParse(slotText.Trim(), out var slot) || slot is < 1 or > 10)
        {
            _logger?.LogError("FACTLauncher ignored run input with invalid SLOT {Slot}.", slotText);
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
        var displayName = DisplayName?.Trim();
        if (string.IsNullOrWhiteSpace(displayName) ||
            !displayName.StartsWith("FACT Launcher", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var suffix = char.ToUpperInvariant(displayName[^1]);
        if (suffix is 'A' or 'B' or 'C' or 'D' or 'F')
        {
            group = suffix;
            return true;
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

    private NodeParameter Param(string name) => NodeParameters.First(p => p.Name == name);
}


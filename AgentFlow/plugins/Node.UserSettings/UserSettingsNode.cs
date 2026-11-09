// -----------------------------------------------------------------------
// <copyright company="Your Company" file="UserSettingsNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.UserSettings;

/// <summary>
/// Publishes user settings from a fixed INI path as JSON whenever the ready input pin
/// receives a string sample.
/// </summary>
public sealed class UserSettingsNode : BaseNode
{
    private readonly BasePin _readyInputPin =
        new("input_string_ready", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _slotAOutputPin =
        new("output_string_slotA", DataType.STRING, PinDirection.Output);

    private readonly BasePin _slotBOutputPin =
        new("output_string_slotB", DataType.STRING, PinDirection.Output);

    private readonly BasePin _slotCOutputPin =
        new("output_string_slotC", DataType.STRING, PinDirection.Output);

    private readonly BasePin _slotDOutputPin =
        new("output_string_slotD", DataType.STRING, PinDirection.Output);

    private readonly BasePin _slotFOutputPin =
        new("output_string_slotF", DataType.STRING, PinDirection.Output);

    private readonly BasePin _endpointOutputPin =
        new("output_string_endpoint", DataType.STRING, PinDirection.Output);

    private readonly BasePin[] _slotOutputPins;

    private readonly record struct SlotAssignment(int Slot, string PortHub);

    private const string SettingsDirectory = @"C:\AC710\UserSettings";
    private const string SettingsFilePath = @"C:\AC710\UserSettings\UserSettings.ini";

    private ILogger? _logger;

    public UserSettingsNode()
    {
        _slotOutputPins = new[]
        {
            _slotAOutputPin,
            _slotBOutputPin,
            _slotCOutputPin,
            _slotDOutputPin,
            _slotFOutputPin,
        };

        Uuid = "686AEC05-C62F-4D63-9C92-389E7B5B80C0";
        AddParam();
        AddPins();
    }

    public override string Name => "User Settings";
    public override string DisplayName { get; set; } = "User Settings";
    public override string Category => "Configuration";

    public override void AddPins()
    {
        AddInputPin(_readyInputPin);
        foreach (var outputPin in _slotOutputPins)
            AddOutputPin(outputPin);
        AddOutputPin(_endpointOutputPin);
    }

    protected override void AddParam()
    {
        base.AddParam();
        AddParameter(new NodeParameter(
            "Profile", typeof(string), "default", isEditable: true,
            group: "Profile", zone: "UserSettingsZone"));
        AddParameter(new NodeParameter(
            "SettingsJson", typeof(string), "{}", isEditable: true,
            group: "Settings", zone: "UserSettingsZone"));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("Profile", out var profile))
            Param("Profile").Value = ParameterValues.GetString(profile, "default") ?? "default";
        if (parameters.TryGetValue("SettingsJson", out var settings))
            Param("SettingsJson").Value = ParameterValues.GetString(settings, "{}") ?? "{}";
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        return Task.CompletedTask;
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;

        var parser = new UserSettingsParser(_logger, SettingsDirectory, SettingsFilePath);
        if (!parser.TryLoad(out var settings))
        {
            Running = false;
            throw new InvalidOperationException(
                $"UserSettings could not load the required settings file '{SettingsFilePath}'.");
        }

        Running = true;
        PublishEndpoint(settings);
        PublishSettingsBySlot(settings);
        _logger?.LogTrace(
            "UserSettings is running with profile {Profile} and loaded settings from {FilePath}.",
            Param("Profile").Value,
            SettingsFilePath);
        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        _logger = null;
        return Task.CompletedTask;
    }

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (!ReferenceEquals(pin, _readyInputPin) ||
            value is not StringSample { StringValue: { } inputText })
        {
            return;
        }

        _logger?.LogTrace(
            "UserSettings received readiness input through {Pin} for profile {Profile}: {Input}. " +
            "Settings are published only during Run.",
            pin.Name,
            Param("Profile").Value,
            inputText);
    }

    private void PublishEndpoint(
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings)
    {
        var endpoint = GetCommaSeparatedValues(settings, "IP")
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        if (endpoint is null)
        {
            _logger?.LogWarning(
                "UserSettings cannot publish an endpoint because the IP section is missing or empty.");
            return;
        }

        if (_endpointOutputPin.TargetCount == 0)
        {
            _logger?.LogWarning(
                "UserSettings cannot publish endpoint {Endpoint} because output pin {Pin} is not connected.",
                endpoint,
                _endpointOutputPin.Name);
            return;
        }

        var sample = StringSample.CreateSample();
        sample.SetValue(endpoint, _endpointOutputPin.Name);
        if (_endpointOutputPin.SendSample(sample))
        {
            _logger?.LogTrace(
                "UserSettings published endpoint {Endpoint} through output pin {Pin}.",
                endpoint,
                _endpointOutputPin.Name);
        }
    }

    private void PublishSettingsBySlot(
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings)
    {
        if (!TryGetSlotAssignments(settings, out var assignments))
            return;

        var connectedOutputCount = 0;
        for (var index = 0; index < _slotOutputPins.Length; index++)
        {
            if (assignments[index] is not { } assignment)
                continue;

            var outputPin = _slotOutputPins[index];
            if (outputPin.TargetCount == 0)
            {
                _logger?.LogWarning(
                    "UserSettings cannot publish SLOT {Slot} and PORTHUB {PortHub} because output pin {Pin} is not connected.",
                    assignment.Slot,
                    assignment.PortHub,
                    outputPin.Name);
                continue;
            }

            connectedOutputCount++;
            var json = SerializeSettingsForSlot(settings, assignment);
            var output = StringSample.CreateSample();
            output.SetValue(json, outputPin.Name);
            if (outputPin.SendSample(output))
            {
                _logger?.LogTrace(
                    "Published user settings JSON for SLOT {Slot} and PORTHUB {PortHub} through output pin {Pin}.",
                    assignment.Slot,
                    assignment.PortHub,
                    outputPin.Name);
            }
        }

        if (connectedOutputCount == 0)
        {
            _logger?.LogWarning(
                "UserSettings found valid SLOT values but no matching SLOT output pin is connected; settings JSON was not sent.");
        }
    }

    private bool TryGetSlotAssignments(
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings,
        out SlotAssignment?[] assignments)
    {
        assignments = new SlotAssignment?[_slotOutputPins.Length];

        var slotValues = GetCommaSeparatedValues(settings, "SLOT");
        if (slotValues.Count == 0)
        {
            _logger?.LogWarning("UserSettings cannot publish settings because the SLOT section is missing.");
            return false;
        }

        var portHubValues = GetCommaSeparatedValues(settings, "PORTHUB");
        if (portHubValues.Count == 0)
        {
            _logger?.LogWarning("UserSettings cannot publish settings because the PORTHUB section is missing.");
            return false;
        }

        var assignedCount = 0;
        for (var index = 0; index < slotValues.Count; index++)
        {
            var part = slotValues[index];
            if (!int.TryParse(part, out var slot) || slot is < 1 or > 10)
            {
                _logger?.LogWarning(
                    "UserSettings ignored invalid SLOT value {Slot}; expected an integer from 1 to 10.",
                    part);
                continue;
            }

            if (index >= portHubValues.Count)
            {
                _logger?.LogWarning(
                    "UserSettings cannot publish SLOT {Slot} because the matching PORTHUB value is missing.",
                    slot);
                continue;
            }

            var portHub = portHubValues[index];
            if (string.IsNullOrWhiteSpace(portHub))
            {
                _logger?.LogWarning(
                    "UserSettings cannot publish SLOT {Slot} because the matching PORTHUB value is empty.",
                    slot);
                continue;
            }

            var outputIndex = (slot - 1) % _slotOutputPins.Length;
            if (assignments[outputIndex] is { } existingAssignment)
            {
                _logger?.LogWarning(
                    "UserSettings ignored duplicate SLOT group value {Slot}; {OutputPin} is already assigned to SLOT {ExistingSlot}.",
                    slot,
                    _slotOutputPins[outputIndex].Name,
                    existingAssignment.Slot);
                continue;
            }

            assignments[outputIndex] = new SlotAssignment(slot, portHub);
            assignedCount++;
        }

        if (portHubValues.Count > slotValues.Count)
        {
            _logger?.LogWarning(
                "UserSettings ignored {Count} extra PORTHUB value(s) that do not have matching SLOT entries.",
                portHubValues.Count - slotValues.Count);
        }

        if (assignedCount == 0)
        {
            _logger?.LogWarning("UserSettings cannot publish settings because no valid SLOT/PORTHUB pair was found.");
            return false;
        }

        return true;
    }

    private static IReadOnlyList<string> GetCommaSeparatedValues(
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings,
        string key)
    {
        if (!settings.TryGetValue(key, out var sectionValues) || sectionValues.Count == 0)
            return Array.Empty<string>();

        var values = new List<string>();
        foreach (var rawValue in sectionValues)
        {
            foreach (var rawPart in rawValue.Split(','))
                values.Add(rawPart.Trim());
        }

        return values;
    }

    private static string SerializeSettingsForSlot(
        IReadOnlyDictionary<string, IReadOnlyList<string>> settings,
        SlotAssignment assignment)
    {
        var payload = settings.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToArray(),
            StringComparer.OrdinalIgnoreCase);
        payload["SLOT"] = new[] { assignment.Slot.ToString() };
        payload["PORTHUB"] = new[] { assignment.PortHub };
        return JsonSerializer.Serialize(payload);
    }

    private NodeParameter Param(string name) => NodeParameters.First(p => p.Name == name);

    /// <summary>Final application-exit hook; this node owns no external resources.</summary>
    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;
}



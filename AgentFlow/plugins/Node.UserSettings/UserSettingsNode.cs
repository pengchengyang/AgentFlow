// -----------------------------------------------------------------------
// <copyright company="Your Company" file="UserSettingsNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.UserSettings;

/// <summary>
/// Receives workflow strings, handles the get user settings command, and can forward
/// ordinary input values unchanged. User settings are loaded from a fixed INI path defined
/// by this node and returned as a JSON string through the output pin.
/// </summary>
public sealed class UserSettingsNode : BaseNode
{
    private readonly BasePin _normalInputPin =
        new("input_string_trigger", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _jsonOutputPin =
        new("output_string_json", DataType.STRING, PinDirection.Output);

    private const string GetUserSettingsCommand = "get user settings";
    private const string SettingsDirectory = @"C:\AC710\UserSettings";
    private const string SettingsFilePath = @"C:\AC710\UserSettings\UserSettings.ini";

    private readonly object _settingsGate = new();
    private ILogger? _logger;
    private string? _lastInput;

    public UserSettingsNode()
    {
        Uuid = "686AEC05-C62F-4D63-9C92-389E7B5B80C0";
        AddParam();
        AddPins();
    }

    public override string Name => "User Settings";
    public override string DisplayName { get; set; } = "User Settings";
    public override string Category => "Configuration";

    public override void AddPins()
    {
        AddInputPin(_normalInputPin);
        AddOutputPin(_jsonOutputPin);
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
        AddParameter(new NodeParameter(
            "PassThrough", typeof(bool), true, isEditable: true,
            group: "Behavior", zone: "UserSettingsZone"));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("Profile", out var profile))
            Param("Profile").Value = ParameterValues.GetString(profile, "default") ?? "default";
        if (parameters.TryGetValue("SettingsJson", out var settings))
            Param("SettingsJson").Value = ParameterValues.GetString(settings, "{}") ?? "{}";
        if (parameters.TryGetValue("PassThrough", out var passThrough))
            Param("PassThrough").Value = ParameterValues.GetBool(passThrough, true);
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        return Task.CompletedTask;
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        Running = true;
        _logger?.LogTrace("UserSettings is running with profile {Profile}.", Param("Profile").Value);
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
        if (!ReferenceEquals(pin, _normalInputPin) ||
            value is not StringSample input || input.StringValue is not { } inputText)
        {
            return;
        }

        lock (_settingsGate)
            _lastInput = inputText;

        _logger?.LogTrace(
            "UserSettings received input through {Pin} for profile {Profile}: {Input}",
            pin.Name,
            Param("Profile").Value,
            inputText);

        if (string.Equals(inputText.Trim(), GetUserSettingsCommand, StringComparison.OrdinalIgnoreCase))
        {
            HandleGetUserSettingsRequest();
            return;
        }


        if (ParameterValues.GetBool(Param("PassThrough").Value, true))
        {
            var output = StringSample.CreateSample();
            output.SetValue(inputText, _jsonOutputPin.Name);
            _jsonOutputPin.SendSample(output);
        }

        // TODO: Apply SettingsJson transformations before forwarding the output.
    }

    private void HandleGetUserSettingsRequest()
    {
        var parser = new UserSettingsParser(_logger, SettingsDirectory, SettingsFilePath);
        if (!parser.TryLoadAsJson(out var json))
        {
            _logger?.LogError("Failed to handle get user settings request.");
            return;
        }

        var output = StringSample.CreateSample();
        output.SetValue(json, _jsonOutputPin.Name);
        if (_jsonOutputPin.SendSample(output))
        {
            _logger?.LogTrace(
                "Sent UserSettings JSON response through output pin {Pin}.",
                _jsonOutputPin.Name);
        }
    }


    private NodeParameter Param(string name) => NodeParameters.First(p => p.Name == name);
}



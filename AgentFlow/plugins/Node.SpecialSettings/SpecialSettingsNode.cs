// -----------------------------------------------------------------------
// <copyright company="Your Company" file="SpecialSettingsNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.SpecialSettings;

/// <summary>
/// Default skeleton for applying profile-specific settings to a workflow value.
/// The current implementation safely forwards the incoming string so the node can be
/// wired and exercised before the business-specific transformation is added.
/// </summary>
public sealed class SpecialSettingsNode : BaseNode
{
    private readonly BasePin _inputPin =
        new("input", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _outputPin =
        new("output", DataType.STRING, PinDirection.Output);

    private readonly object _settingsGate = new();
    private ILogger? _logger;
    private string? _lastInput;

    public SpecialSettingsNode()
    {
        Uuid = "686AEC05-C62F-4D63-9C92-389E7B5B80C0";
        AddParam();
        AddPins();
    }

    public override string Name => "Special Settings";
    public override string DisplayName { get; set; } = "Special Settings";
    public override string Category => "Configuration";

    public override void AddPins()
    {
        AddInputPin(_inputPin);
        AddOutputPin(_outputPin);
    }

    protected override void AddParam()
    {
        base.AddParam();
        AddParameter(new NodeParameter(
            "Profile", typeof(string), "default", isEditable: true,
            group: "Profile", zone: "SpecialSettingsZone"));
        AddParameter(new NodeParameter(
            "SettingsJson", typeof(string), "{}", isEditable: true,
            group: "Settings", zone: "SpecialSettingsZone"));
        AddParameter(new NodeParameter(
            "PassThrough", typeof(bool), true, isEditable: true,
            group: "Behavior", zone: "SpecialSettingsZone"));
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
        _logger?.LogTrace("SpecialSettings is running with profile {Profile}.", Param("Profile").Value);
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
        if (!ReferenceEquals(pin, _inputPin) || value is not StringSample input ||
            input.StringValue is not { } inputText)
        {
            return;
        }

        lock (_settingsGate)
            _lastInput = inputText;

        _logger?.LogTrace("SpecialSettings received input for profile {Profile}: {Input}",
            Param("Profile").Value, inputText);

        if (ParameterValues.GetBool(Param("PassThrough").Value, true))
        {
            var output = StringSample.CreateSample();
            output.SetValue(inputText, "output");
            _outputPin.SendSample(output);
        }

        // TODO: Apply SettingsJson transformations before forwarding the output.
    }

    private NodeParameter Param(string name) => NodeParameters.First(p => p.Name == name);
}
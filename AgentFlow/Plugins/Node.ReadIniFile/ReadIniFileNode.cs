// -----------------------------------------------------------------------
// <copyright company="Your Company" file="ReadIniFileNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Text;
using System.Text.Json;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.ReadIniFile;

/// <summary>
/// Reads an INI file and publishes its parsed contents as compact JSON. A runtime path received
/// on <c>input_string_path</c> takes priority over the configured <c>IniPath</c> parameter.
/// </summary>
public sealed class ReadIniFileNode : BaseNode
{
    private const string DefaultIniPath = "";

    private readonly BasePin _pathInputPin =
        new("input_string_path", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _jsonOutputPin =
        new("output_string_sample", DataType.STRING, PinDirection.Output);

    private readonly object _loadGate = new();

    private ILogger? _logger;

    public ReadIniFileNode()
    {
        Uuid = "25F10629-0BD8-4FDB-83A5-C7A07EFB4652";
        AddParam();
        AddPins();
    }

    public override string Name => "Read INI File";
    public override string DisplayName { get; set; } = "Read INI File";
    public override string Category => "C";

    public override void AddPins()
    {
        AddInputPin(_pathInputPin);
        AddOutputPin(_jsonOutputPin);
    }

    protected override void AddParam()
    {
        base.AddParam();
        AddParameter(new NodeParameter(
            "IniPath",
            typeof(string),
            DefaultIniPath,
            isEditable: true,
            group: "General",
            zone: "ReadIniFileZone"));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("IniPath", out var configuredPath))
        {
            Param("IniPath").Value =
                ParameterValues.GetString(configuredPath, DefaultIniPath) ?? DefaultIniPath;
        }
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        return Task.CompletedTask;
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        Running = true;

        var inputPath = GetCurrentInputPath();
        LoadAndPublish(inputPath);
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
        if (ReferenceEquals(pin, _pathInputPin))
        {
            if (value is not StringSample { StringValue: { } inputPath } ||
                string.IsNullOrWhiteSpace(inputPath))
            {
                RejectAndStop(
                    "Read INI File ignored invalid input on {Pin}: expected StringSample with a non-empty INI path.",
                    pin.Name);
                return;
            }

            if (!Running)
            {
                _logger?.LogTrace(
                    "Read INI File cached input path while the node is not running: {InputPath}",
                    inputPath);
                return;
            }

            LoadAndPublish(inputPath);
        }
    }

    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        return Task.CompletedTask;
    }

    private void LoadAndPublish(string? runtimePath)
    {
        lock (_loadGate)
        {
            try
            {
                var fullPath = ResolveIniPath(runtimePath);
                var content = ReadAllText(fullPath);

                if (!IniFileParser.TryParse(content, out var parsed, out var error))
                {
                    RejectAndStop(
                        "Read INI File rejected {FilePath}: {Error}",
                        fullPath,
                        error);
                    return;
                }

                foreach (var warning in parsed.Warnings)
                {
                    _logger?.LogWarning(
                        "Read INI File parsed {FilePath} with warning: {Warning}",
                        fullPath,
                        warning);
                }

                if (parsed.ValueCount == 0)
                {
                    _logger?.LogWarning(
                        "Read INI File found no values in {FilePath}; publishing an empty JSON object.",
                        fullPath);
                }

                foreach (var pair in parsed.Values)
                {
                    _logger?.LogTrace(
                        "Read INI File parsed {Key} with {ValueCount} value(s) from {FilePath}.",
                        pair.Key,
                        pair.Value.Count,
                        fullPath);
                }

                var json = JsonSerializer.Serialize(parsed.Values);
                var sample = StringSample.CreateSample();
                sample.SetValue(json, _jsonOutputPin.Name);

                var sent = _jsonOutputPin.SendSampleWithDispatchLog(
                    sample,
                    $"parsed INI JSON from {fullPath}",
                    LogLevel.Information);

                _logger?.LogInformation(
                    "Read INI File parsed {SectionCount} section(s), {ValueCount} value(s), and {KeyCount} output key(s) from {FilePath}; sent={Sent}.",
                    parsed.SectionCount,
                    parsed.ValueCount,
                    parsed.Values.Count,
                    fullPath,
                    sent);
            }
            catch (Exception ex)
            {
                RejectAndStop(
                    ex,
                    "Read INI File failed to load {Path}: {Message}",
                    runtimePath ?? Param("IniPath").Value,
                    ex.Message);
            }
        }
    }

    private string ResolveIniPath(string? runtimePath)
    {
        var inputPath = string.IsNullOrWhiteSpace(runtimePath)
            ? ParameterValues.GetString(Param("IniPath").Value) ?? DefaultIniPath
            : runtimePath;

        inputPath = TrimMatchingQuotes(inputPath.Trim());
        if (inputPath.Length == 0)
            throw new InvalidOperationException(
                "Read INI File requires a non-empty input_string_path value or IniPath parameter.");

        var fullPath = Path.GetFullPath(inputPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("INI file was not found.", fullPath);

        return fullPath;
    }

    private string? GetCurrentInputPath()
    {
        return _pathInputPin.Value is StringSample input &&
               !string.IsNullOrWhiteSpace(input.StringValue)
            ? input.StringValue
            : null;
    }

    private static string ReadAllText(string fullPath)
    {
        using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static string TrimMatchingQuotes(string value)
    {
        if (value.Length >= 2 &&
            ((value[0] == '"' && value[^1] == '"') ||
             (value[0] == '\'' && value[^1] == '\'')))
        {
            return value[1..^1].Trim();
        }

        return value;
    }

    private void RejectAndStop(string message, params object?[] args)
    {
        _logger?.LogError(message, args);
        Running = false;
    }

    private void RejectAndStop(Exception exception, string message, params object?[] args)
    {
        _logger?.LogError(exception, message, args);
        Running = false;
    }

    private NodeParameter Param(string name) => NodeParameters.First(p => p.Name == name);
}
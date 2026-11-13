// -----------------------------------------------------------------------
// <copyright company="Your Company" file="PriFileParserNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using AgentFlow.Contracts;
using BroadcastKit;
using BroadcastKit.Contracts;
using AgentFlow.Shared.RTS.Broadcasts;
using Microsoft.Extensions.Logging;

namespace Node.PriFileParser;

/// <summary>
/// Reads a PRI configuration file and publishes its non-comment key/value pairs as JSON.
/// Lines beginning with '#' are comments. The first '=' separates each key from its value.
/// </summary>
public sealed class PriFileParserNode : BaseNode
{
    private readonly BasePin _pathInputPin =
        new("input_string_path", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _jsonOutputPin =
        new("output_string_json", DataType.STRING, PinDirection.Output);

    private ILogger? _logger;

    public PriFileParserNode()
    {
        Uuid = "ACAF52A4-3815-4793-ABD0-98D1FDA24BC9";
        AddParam();
        AddPins();
    }

    public override string Name => "PRI File Parser";
    public override string DisplayName { get; set; } = "PRI File Parser";
    public override string Category => "Parsing";

    public override void AddPins()
    {
        AddInputPin(_pathInputPin);
        AddOutputPin(_jsonOutputPin);
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        // This parser has no configurable parameters.
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
        _logger?.LogTrace("PRI File Parser is running.");
        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        _logger?.LogTrace("PRI File Parser stopped.");
        _logger = null;
        return Task.CompletedTask;
    }

    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (ReferenceEquals(pin, _pathInputPin))
        {
            if (value is not StringSample { StringValue: { } inputPath } ||
                string.IsNullOrWhiteSpace(inputPath))
            {
                _logger?.LogWarning(
                    "PRI File Parser ignored invalid input on {Pin}: expected StringSample with a non-empty file path.",
                    pin.Name);
                return;
            }

            try
            {
                var parsed = ParseFile(inputPath.Trim());
                _ = BroadcastPriFileSettingsAsync(parsed.Settings);
                PublishOutput(parsed.Json);
            }
            catch (Exception ex)
            {
                _logger?.LogError(
                    ex,
                    "PRI File Parser failed for input {InputPath}: {Message}",
                    inputPath,
                    ex.Message);
            }
        }
    }

    private static readonly Regex PriPathOptionPattern = new(
        @"(?i)(?:^|\s)/P(?::)?\s*(?<path>""[^""]+""|'[^']+'|\S+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private PriFileParseResult ParseFile(string inputPath)
    {
        var fullPath = ResolveInputPath(inputPath);

        var settings = new Dictionary<string, string>(StringComparer.Ordinal);
        using var stream = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);

        var lineNumber = 0;
        while (reader.ReadLine() is { } line)
        {
            lineNumber++;
            var trimmed = line.Trim();

            if (trimmed.Length == 0 || trimmed.StartsWith('#'))
                continue;

            var separatorIndex = trimmed.IndexOf('=');
            if (separatorIndex < 0)
                continue;

            var key = trimmed[..separatorIndex].Trim();
            var value = trimmed[(separatorIndex + 1)..].Trim();
            if (key.Length == 0)
            {
                _logger?.LogWarning(
                    "PRI File Parser ignored line {LineNumber} in {FilePath}: the key is empty.",
                    lineNumber,
                    fullPath);
                continue;
            }

            if (settings.TryGetValue(key, out var previousValue))
            {
                _logger?.LogTrace(
                    "PRI File Parser replaced duplicate key {Key} from {LineNumber} in {FilePath}; previous value was {PreviousValue}.",
                    key,
                    lineNumber,
                    fullPath,
                    previousValue);
            }

            settings[key] = value;
        }

        if (settings.Count == 0)
        {
            _logger?.LogWarning(
                "PRI File Parser found no valid key/value pairs in {FilePath}.",
                fullPath);
        }

        foreach (var pair in settings)
        {
            _logger?.LogTrace(
                "PRI File Parser parsed key {Key} with value {Value} from {FilePath}.",
                pair.Key,
                pair.Value,
                fullPath);
        }

        _logger?.LogInformation(
            "PRI File Parser read {PairCount} key/value pair(s) from {FilePath}.",
            settings.Count,
            fullPath);

        var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        return new PriFileParseResult(settings, json);
    }

    private string ResolveInputPath(string input)
    {
        var inputText = input.Trim();
        var matches = PriPathOptionPattern.Matches(inputText);
        if (matches.Count > 1)
            throw new InvalidOperationException(
                "PRI File Parser rejected input because it contains multiple /P path options.");

        var candidate = matches.Count == 1
            ? matches[0].Groups["path"].Value
            : inputText;
        candidate = TrimMatchingQuotes(candidate.Trim());
        if (candidate.Length == 0)
            throw new ArgumentException("PRI File Parser requires a non-empty /P file path.", nameof(input));

        var fullPath = Path.GetFullPath(candidate);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("PRI file was not found.", fullPath);

        if (matches.Count == 1)
        {
            _logger?.LogTrace(
                "PRI File Parser resolved /P path {FilePath} from input {Input}.",
                fullPath,
                inputText);
        }

        return fullPath;
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
    private async Task BroadcastPriFileSettingsAsync(
        IReadOnlyDictionary<string, string> settings)
    {
        var samplesByKey = new Dictionary<string, Sample>(StringComparer.Ordinal);
        foreach (var pair in settings)
        {
            var sample = StringSample.CreateSample();
            sample.SetValue(pair.Value, pair.Key);
            samplesByKey[pair.Key] = sample;
        }

        try
        {
            await TopicBus.Instance.PublishAsync(
                RTSBroadcastSubscriptions.PriFileSettings,
                BroadcastTopicMessage.FromSampleDictionary(samplesByKey),
                ct: CancellationToken.None).ConfigureAwait(false);

            _logger?.LogInformation(
                "PRI File Parser broadcast {PairCount} setting(s) on {Topic}.",
                samplesByKey.Count,
                RTSBroadcastSubscriptions.PriFileSettings);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "PRI File Parser failed to broadcast settings on {Topic}: {Message}",
                RTSBroadcastSubscriptions.PriFileSettings,
                ex.Message);
        }
    }

    private sealed record PriFileParseResult(
        IReadOnlyDictionary<string, string> Settings,
        string Json);

    private void PublishOutput(string json)
    {
        if (_jsonOutputPin.TargetCount == 0)
        {
            _logger?.LogWarning(
                "PRI File Parser generated JSON, but output pin {Pin} is not connected.",
                _jsonOutputPin.Name);
            return;
        }

        var sample = StringSample.CreateSample();
        sample.SetValue(json, _jsonOutputPin.Name);
        _jsonOutputPin.SendSampleWithDispatchLog(
            sample,
            "PRI file JSON",
            LogLevel.Information);
    }
}

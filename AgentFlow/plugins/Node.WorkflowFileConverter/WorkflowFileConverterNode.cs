// -----------------------------------------------------------------------
// <copyright company="Your Company" file="WorkflowFileConverterNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.WorkflowFileConverter;

/// <summary>
/// Converts encrypted .rw workflow files to readable JSON and back. Inputs and outputs are file
/// paths; conversion writes a sibling file with the same base name in the same directory. Output pins publish only after conversion, and only when connected, using the full absolute path.
/// </summary>
public sealed class WorkflowFileConverterNode : BaseNode
{
    private readonly BasePin _rwInputPin =
        new("input_string_rw", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _jsonInputPin =
        new("input_string_json", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _jsonOutputPin =
        new("output_string_json", DataType.STRING, PinDirection.Output);

    private readonly BasePin _rwOutputPin =
        new("output_string_rw", DataType.STRING, PinDirection.Output);

    private ILogger? _logger;

    public WorkflowFileConverterNode()
    {
        Uuid = "C42659A2-9B4D-4647-BB50-6439B642C6D2";
        AddParam();
        AddPins();
    }

    public override string Name => "Workflow File Converter";
    public override string DisplayName { get; set; } = "Workflow File Converter";
    public override string Category => "Workflow Tools";

    public override void AddPins()
    {
        AddInputPin(_rwInputPin);
        AddInputPin(_jsonInputPin);
        AddOutputPin(_jsonOutputPin);
        AddOutputPin(_rwOutputPin);
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        // This converter has no configurable parameters.
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
        _logger?.LogTrace("Workflow File Converter is running.");
        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        _logger = null;
        return Task.CompletedTask;
    }

    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;

    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (value is not StringSample { StringValue: { } inputPath } ||
            string.IsNullOrWhiteSpace(inputPath))
        {
            return;
        }

        try
        {
            if (ReferenceEquals(pin, _rwInputPin))
                ConvertRwToJson(inputPath.Trim());
            else if (ReferenceEquals(pin, _jsonInputPin))
                ConvertJsonToRw(inputPath.Trim());
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "Workflow File Converter failed for input {InputPath}: {Message}",
                inputPath,
                ex.Message);
        }
    }

    private void ConvertRwToJson(string rwPath)
    {
        var fullRwPath = Path.GetFullPath(rwPath);
        EnsureFile(fullRwPath, WorkflowFileCrypto.RwExtension);
        var json = WorkflowFileCrypto.ReadWorkflowJsonFile(fullRwPath);
        ValidateJson(json, fullRwPath);

        var jsonPath = Path.GetFullPath(Path.ChangeExtension(fullRwPath, ".json"));
        WriteAtomicText(jsonPath, json);
        _logger?.LogInformation(
            "Workflow File Converter decrypted {RwPath} to {JsonPath}.",
            fullRwPath,
            jsonPath);
        PublishPath(_jsonOutputPin, jsonPath, "decrypted JSON path");
    }

    private void ConvertJsonToRw(string jsonPath)
    {
        var fullJsonPath = Path.GetFullPath(jsonPath);
        EnsureFile(fullJsonPath, ".json");
        var json = File.ReadAllText(fullJsonPath);
        ValidateJson(json, fullJsonPath);

        var rwPath = Path.GetFullPath(WorkflowFileCrypto.NormalizeWorkflowPath(fullJsonPath));
        var encrypted = WorkflowFileCrypto.Encrypt(json);
        var temp = rwPath + ".tmp";
        File.WriteAllBytes(temp, encrypted);
        File.Move(temp, rwPath, overwrite: true);
        _logger?.LogInformation(
            "Workflow File Converter encrypted {JsonPath} to {RwPath}.",
            fullJsonPath,
            rwPath);
        PublishPath(_rwOutputPin, rwPath, "encrypted RW path");
    }

    private void PublishPath(BasePin outputPin, string path, string operation)
    {
        if (outputPin.TargetCount == 0)
        {
            _logger?.LogWarning(
                "Workflow File Converter generated {Path}, but output pin {Pin} is not connected.",
                path,
                outputPin.Name);
            return;
        }

        var sample = StringSample.CreateSample();
        sample.SetValue(path, outputPin.Name);
        outputPin.SendSampleWithDispatchLog(sample, operation);
    }

    private static void EnsureFile(string path, string expectedExtension)
    {
        if (!File.Exists(path))
            throw new FileNotFoundException("Workflow file not found.", path);
        if (!Path.GetExtension(path).Equals(expectedExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Expected a '{expectedExtension}' workflow file, got '{path}'.");
    }

    private static void ValidateJson(string json, string path)
    {
        try
        {
            using var _ = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"Workflow JSON is invalid: '{path}'.", ex);
        }
    }

    private static void WriteAtomicText(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text);
        File.Move(temp, path, overwrite: true);
    }
}

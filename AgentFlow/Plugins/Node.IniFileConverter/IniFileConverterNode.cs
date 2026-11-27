// -----------------------------------------------------------------------
// <copyright company="Your Company" file="IniFileConverterNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Text;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.IniFileConverter;

/// <summary>
/// Converts encrypted .rwi settings files to readable .ini text files and back, using the
/// same AES-GCM format as .rw workflow files (WorkflowFileCrypto with a shared key and
/// magic; only the extension differs). Inputs and outputs are file paths; conversion
/// writes a sibling file with the same base name in the same directory, atomically
/// replacing an existing destination. Output pins publish only after conversion, and
/// only when connected, using the full absolute path.
/// </summary>
public sealed class IniFileConverterNode : BaseNode
{
    private const string RwiExtension = ".rwi";
    private const string IniExtension = ".ini";

    private readonly BasePin _rwiInputPin =
        new("input_path_rwi", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _iniInputPin =
        new("input_path_ini", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _iniOutputPin =
        new("output_path_ini", DataType.STRING, PinDirection.Output);

    private readonly BasePin _rwiOutputPin =
        new("output_path_rwi", DataType.STRING, PinDirection.Output);

    private ILogger? _logger;

    public IniFileConverterNode()
    {
        Uuid = "2BDF5291-88D8-4665-8AA8-F8081EB49EF7";
        AddParam();
        AddPins();
    }

    public override string Name => "Ini File Converter";
    public override string DisplayName { get; set; } = "Ini File Converter";
    public override string Category => "C";

    public override void AddPins()
    {
        AddInputPin(_rwiInputPin);
        AddInputPin(_iniInputPin);
        AddOutputPin(_iniOutputPin);
        AddOutputPin(_rwiOutputPin);
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
        _logger?.LogTrace("Ini File Converter is running.");
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
            if (ReferenceEquals(pin, _rwiInputPin))
                ConvertRwiToIni(inputPath.Trim());
            else if (ReferenceEquals(pin, _iniInputPin))
                ConvertIniToRwi(inputPath.Trim());
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "Ini File Converter failed for input {InputPath}: {Message}",
                inputPath,
                ex.Message);
        }
    }

    private void ConvertRwiToIni(string rwiPath)
    {
        var fullRwiPath = Path.GetFullPath(rwiPath);
        EnsureFile(fullRwiPath, RwiExtension);
        var ini = WorkflowFileCrypto.Decrypt(File.ReadAllBytes(fullRwiPath));
        ValidateIni(ini, fullRwiPath);

        var iniPath = Path.GetFullPath(Path.ChangeExtension(fullRwiPath, IniExtension));
        WriteAtomicText(iniPath, ini);
        _logger?.LogInformation(
            "Ini File Converter decrypted {RwiPath} to {IniPath}.",
            fullRwiPath,
            iniPath);
        PublishPath(_iniOutputPin, iniPath, "decrypted INI path");
    }

    private void ConvertIniToRwi(string iniPath)
    {
        var fullIniPath = Path.GetFullPath(iniPath);
        EnsureFile(fullIniPath, IniExtension);
        var ini = File.ReadAllText(fullIniPath, Encoding.UTF8);
        ValidateIni(ini, fullIniPath);

        var rwiPath = Path.GetFullPath(Path.ChangeExtension(fullIniPath, RwiExtension));
        var encrypted = WorkflowFileCrypto.Encrypt(ini);
        WriteAtomicBytes(rwiPath, encrypted);
        _logger?.LogInformation(
            "Ini File Converter encrypted {IniPath} to {RwiPath}.",
            fullIniPath,
            rwiPath);
        PublishPath(_rwiOutputPin, rwiPath, "encrypted RWI path");
    }

    private void PublishPath(BasePin outputPin, string path, string operation)
    {
        if (outputPin.TargetCount == 0)
        {
            _logger?.LogWarning(
                "Ini File Converter generated {Path}, but output pin {Pin} is not connected.",
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
            throw new FileNotFoundException("Settings file not found.", path);
        if (!Path.GetExtension(path).Equals(expectedExtension, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"Expected a '{expectedExtension}' settings file, got '{path}'.");
    }

    /// <summary>
    /// Lightweight content check: deep INI syntax validation stays with Node.ReadIniFile at
    /// workflow runtime. Rejects empty payloads and payloads without a single section header
    /// or non-comment line, which would otherwise silently encrypt a wrong file.
    /// </summary>
    private static void ValidateIni(string ini, string path)
    {
        if (string.IsNullOrWhiteSpace(ini))
            throw new InvalidDataException($"The settings file is empty: '{path}'.");

        foreach (var rawLine in ini.Split('\n'))
        {
            var line = rawLine.Trim('\r', ' ', '\t');
            if (line.Length == 0 || line.StartsWith('#') || line.StartsWith(';'))
                continue;

            return;
        }

        throw new InvalidDataException(
            $"The settings file has no section header and no content line: '{path}'.");
    }

    private static void WriteAtomicText(string path, string text)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        File.Move(temp, path, overwrite: true);
    }

    private static void WriteAtomicBytes(string path, byte[] payload)
    {
        var temp = path + ".tmp";
        File.WriteAllBytes(temp, payload);
        File.Move(temp, path, overwrite: true);
    }
}

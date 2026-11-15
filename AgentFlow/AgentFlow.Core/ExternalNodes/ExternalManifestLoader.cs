// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="ExternalManifestLoader.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;
using AgentFlow.Contracts;

namespace AgentFlow.Core.ExternalNodes;

/// <summary>Loads and validates plugin.json without starting the Worker.</summary>
public static class ExternalManifestLoader
{
    public const int SupportedManifestVersion = 1;
    public const int SupportedProtocolVersion = 1;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip
    };

    public static IReadOnlyList<ExternalNodeDefinition> LoadPackage(string packageDirectory)
    {
        var root = Path.GetFullPath(packageDirectory);
        var manifestPath = Path.Combine(root, "plugin.json");
        if (!File.Exists(manifestPath))
            throw new ExternalManifestException($"Package manifest not found: {manifestPath}");

        ExternalPackageManifest manifest;
        try
        {
            var json = File.ReadAllText(manifestPath, System.Text.Encoding.UTF8);
            manifest = JsonSerializer.Deserialize<ExternalPackageManifest>(json, JsonOptions)
                ?? throw new ExternalManifestException($"Package manifest is empty: {manifestPath}");
        }
        catch (ExternalManifestException)
        {
            throw;
        }
        catch (Exception ex)
        {
            throw new ExternalManifestException($"Failed to parse package manifest: {manifestPath}", ex);
        }

        ValidateManifest(root, manifest);

        var workerExecutable = ResolveInside(root, manifest.Worker.Executable, "worker.executable");
        var workerWorkingDirectory = ResolveInside(
            root,
            manifest.Worker.WorkingDirectory ?? Path.GetDirectoryName(manifest.Worker.Executable) ?? ".",
            "worker.workingDirectory");

        var metadata = new NodePackageMetadata(
            manifest.PackageId,
            manifest.Version,
            manifest.Metadata?.Publisher,
            manifest.Metadata?.Author,
            manifest.Metadata?.AuthorEmail,
            manifest.Metadata?.Copyright,
            manifest.Metadata?.License,
            manifest.Metadata?.LicenseFile,
            manifest.Metadata?.Homepage,
            manifest.Metadata?.Repository);

        return manifest.NodeTypes.Select(nodeType => new ExternalNodeDefinition(
            root,
            manifest,
            nodeType,
            workerExecutable,
            workerWorkingDirectory,
            metadata,
            CreatePins(nodeType, AgentFlow.Contracts.PinDirection.Input),
            CreatePins(nodeType, AgentFlow.Contracts.PinDirection.Output),
            CreateParameterDefinitions(nodeType))).ToList();
    }

    private static void ValidateManifest(string root, ExternalPackageManifest manifest)
    {
        if (manifest.ManifestVersion != SupportedManifestVersion)
            throw new ExternalManifestException(
                $"Unsupported manifestVersion {manifest.ManifestVersion}; expected {SupportedManifestVersion}.");

        if (string.IsNullOrWhiteSpace(manifest.PackageId) || ContainsPathSeparator(manifest.PackageId))
            throw new ExternalManifestException("packageId must be non-empty and must not contain path separators.");

        if (string.IsNullOrWhiteSpace(manifest.DisplayName))
            throw new ExternalManifestException("displayName is required.");

        if (string.IsNullOrWhiteSpace(manifest.Version))
            throw new ExternalManifestException("version is required.");

        if (manifest.Metadata is { Copyright: { } copyright } && string.IsNullOrWhiteSpace(copyright))
            throw new ExternalManifestException("metadata.copyright must not be blank when provided.");

        if (manifest.Metadata is { License: { } license } && string.IsNullOrWhiteSpace(license))
            throw new ExternalManifestException("metadata.license must not be blank when provided.");

        if (manifest.Metadata?.LicenseFile is { } licenseFile)
        {
            var resolved = ResolveInside(root, licenseFile, "metadata.licenseFile");
            if (!File.Exists(resolved))
                throw new ExternalManifestException($"metadata.licenseFile does not exist: {licenseFile}");
        }

        if (manifest.Worker.ProtocolVersion != SupportedProtocolVersion)
            throw new ExternalManifestException(
                $"Unsupported worker.protocolVersion {manifest.Worker.ProtocolVersion}; expected {SupportedProtocolVersion}.");

        if (string.IsNullOrWhiteSpace(manifest.Worker.Executable))
            throw new ExternalManifestException("worker.executable is required.");

        if (manifest.Worker.StartTimeoutMs <= 0 || manifest.Worker.CommandTimeoutMs <= 0 ||
            manifest.Worker.HeartbeatIntervalMs <= 0 || manifest.Worker.HeartbeatTimeoutMs <= 0)
            throw new ExternalManifestException("Worker timeout values must be greater than zero.");

        if (manifest.Worker.MaxLineBytes <= 0)
            throw new ExternalManifestException("worker.maxLineBytes must be greater than zero.");

        if (manifest.NodeTypes.Count == 0)
            throw new ExternalManifestException("nodeTypes must contain at least one node type.");

        var nodeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var nodeType in manifest.NodeTypes)
        {
            if (string.IsNullOrWhiteSpace(nodeType.NodeType) || ContainsPathSeparator(nodeType.NodeType))
                throw new ExternalManifestException("nodeType must be non-empty and must not contain path separators.");
            if (!nodeTypes.Add(nodeType.NodeType))
                throw new ExternalManifestException($"Duplicate nodeType: {nodeType.NodeType}");
            if (string.IsNullOrWhiteSpace(nodeType.DisplayName))
                throw new ExternalManifestException($"nodeType '{nodeType.NodeType}' is missing displayName.");
            if (string.IsNullOrWhiteSpace(nodeType.Category))
                throw new ExternalManifestException($"nodeType '{nodeType.NodeType}' is missing category.");

            var pinNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pin in nodeType.Pins)
            {
                if (string.IsNullOrWhiteSpace(pin.Name) || !pinNames.Add(pin.Name))
                    throw new ExternalManifestException(
                        $"nodeType '{nodeType.NodeType}' has an empty or duplicate pin name: '{pin.Name}'.");
                _ = ParseDataType(pin.DataType, $"pin '{pin.Name}' dataType");
                _ = ParseDirection(pin.Direction, $"pin '{pin.Name}' direction");
            }

            var parameterNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var parameter in nodeType.Parameters)
            {
                if (string.IsNullOrWhiteSpace(parameter.Name) || !parameterNames.Add(parameter.Name))
                    throw new ExternalManifestException(
                        $"nodeType '{nodeType.NodeType}' has an empty or duplicate parameter name: '{parameter.Name}'.");
                _ = ParseParameterType(parameter.Type, $"parameter '{parameter.Name}' type");
                if (parameter.Editor is { } editor && ParseEditor(editor) is
                    (ParameterEditorKind.SingleSelect or ParameterEditorKind.MultiSelect))
                {
                    if (parameter.Options is not { Count: > 0 })
                        throw new ExternalManifestException(
                            $"parameter '{parameter.Name}' uses a select editor but has no options.");
                    if (parameter.Options.Any(string.IsNullOrWhiteSpace) ||
                        parameter.Options.Distinct(StringComparer.Ordinal).Count() != parameter.Options.Count)
                        throw new ExternalManifestException(
                            $"parameter '{parameter.Name}' has empty or duplicate options.");
                }
            }
        }

        var workerExecutable = ResolveInside(root, manifest.Worker.Executable, "worker.executable");
        if (!File.Exists(workerExecutable))
            throw new ExternalManifestException($"worker.executable does not exist: {manifest.Worker.Executable}");
    }

    private static IReadOnlyList<BasePin> CreatePins(
        ExternalNodeTypeManifest nodeType,
        AgentFlow.Contracts.PinDirection direction)
    {
        return nodeType.Pins
            .Where(pin => ParseDirection(pin.Direction, $"pin '{pin.Name}' direction") == direction)
            .Select(pin => new BasePin(
                pin.Name,
                ParseDataType(pin.DataType, $"pin '{pin.Name}' dataType"),
                direction,
                pin.Required))
            .ToList();
    }

    private static IReadOnlyList<ParameterDefinition> CreateParameterDefinitions(ExternalNodeTypeManifest nodeType)
    {
        return nodeType.Parameters.Select(parameter =>
        {
            var type = ParseParameterType(parameter.Type, $"parameter '{parameter.Name}' type");
            var editor = parameter.Editor is { } value ? ParseEditor(value) : ParameterEditorKind.Auto;
            return new ParameterDefinition(
                parameter.Name,
                type,
                parameter.DisplayName ?? parameter.Name,
                ConvertDefaultValue(parameter.DefaultValue, type, parameter.Name),
                parameter.Description,
                editor,
                parameter.Options);
        }).ToList();
    }

    private static object? ConvertDefaultValue(JsonElement? value, Type type, string parameterName)
    {
        if (value is null || value.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return type == typeof(string) ? "" : Activator.CreateInstance(type);

        var element = value.Value;
        try
        {
            if (type == typeof(string))
                return element.ValueKind == JsonValueKind.String ? element.GetString() ?? "" : element.ToString();
            if (type == typeof(bool))
            {
                if (element.ValueKind == JsonValueKind.True) return true;
                if (element.ValueKind == JsonValueKind.False) return false;
                if (element.ValueKind == JsonValueKind.String && bool.TryParse(element.GetString(), out var b)) return b;
            }
            if (type == typeof(int) && TryGetInt32(element, out var i)) return i;
            if (type == typeof(long) && TryGetInt64(element, out var l)) return l;
            if (type == typeof(float) && TryGetSingle(element, out var f)) return f;
            if (type == typeof(double) && TryGetDouble(element, out var d)) return d;
        }
        catch (Exception ex)
        {
            throw new ExternalManifestException(
                $"parameter '{parameterName}' defaultValue is not valid for type {type.Name}.", ex);
        }

        throw new ExternalManifestException(
            $"parameter '{parameterName}' defaultValue is not valid for type {type.Name}.");
    }

    private static bool TryGetInt32(JsonElement element, out int value)
    {
        value = default;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out value)) return true;
        return element.ValueKind == JsonValueKind.String && int.TryParse(element.GetString(), out value);
    }

    private static bool TryGetInt64(JsonElement element, out long value)
    {
        value = default;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt64(out value)) return true;
        return element.ValueKind == JsonValueKind.String && long.TryParse(element.GetString(), out value);
    }

    private static bool TryGetSingle(JsonElement element, out float value)
    {
        value = default;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetSingle(out value)) return true;
        return element.ValueKind == JsonValueKind.String && float.TryParse(element.GetString(), out value);
    }

    private static bool TryGetDouble(JsonElement element, out double value)
    {
        value = default;
        if (element.ValueKind == JsonValueKind.Number && element.TryGetDouble(out value)) return true;
        return element.ValueKind == JsonValueKind.String && double.TryParse(element.GetString(), out value);
    }

    private static Type ParseParameterType(string value, string label) =>
        value.Trim().ToLowerInvariant() switch
        {
            "string" => typeof(string),
            "bool" or "boolean" => typeof(bool),
            "int32" or "int" => typeof(int),
            "int64" or "long" => typeof(long),
            "float" => typeof(float),
            "double" => typeof(double),
            _ => throw new ExternalManifestException($"Unsupported {label}: {value}")
        };

    private static ParameterEditorKind ParseEditor(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            "auto" => ParameterEditorKind.Auto,
            "text" => ParameterEditorKind.Text,
            "singleselect" or "single-select" or "single_select" => ParameterEditorKind.SingleSelect,
            "multiselect" or "multi-select" or "multi_select" => ParameterEditorKind.MultiSelect,
            _ => throw new ExternalManifestException($"Unsupported parameter editor: {value}")
        };

    private static DataType ParseDataType(string value, string label)
    {
        if (Enum.TryParse<DataType>(value, ignoreCase: true, out var dataType))
            return dataType;
        throw new ExternalManifestException($"Unsupported {label}: {value}");
    }

    private static AgentFlow.Contracts.PinDirection ParseDirection(string value, string label)
    {
        if (value.Equals("input", StringComparison.OrdinalIgnoreCase))
            return AgentFlow.Contracts.PinDirection.Input;
        if (value.Equals("output", StringComparison.OrdinalIgnoreCase))
            return AgentFlow.Contracts.PinDirection.Output;
        throw new ExternalManifestException($"Unsupported {label}: {value}");
    }

    private static string ResolveInside(string packageRoot, string relativePath, string field)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            throw new ExternalManifestException($"{field} must not be empty.");
        var fullRoot = Path.GetFullPath(packageRoot);
        var resolved = Path.GetFullPath(Path.Combine(fullRoot, relativePath));
        var rootWithSeparator = fullRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        if (!resolved.StartsWith(rootWithSeparator, StringComparison.OrdinalIgnoreCase))
            throw new ExternalManifestException($"{field} escapes the Package directory: {relativePath}");
        return resolved;
    }

    private static bool ContainsPathSeparator(string value) =>
        value.Contains(Path.DirectorySeparatorChar) || value.Contains(Path.AltDirectorySeparatorChar);
}

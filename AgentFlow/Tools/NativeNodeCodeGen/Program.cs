using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AgentFlow.NativeNodeCodeGen;

/// <summary>
/// Generates C# BaseNode proxy classes from native node manifests. The Core plugin loader only
/// discovers managed BaseNode types, so every native module must have a generated proxy in the
/// NativeHost assembly.
/// </summary>
internal static class Program
{
    public static int Main(string[] args)
    {
        try
        {
            var workspaceRoot = args.Length > 0
                ? Path.GetFullPath(args[0])
                : Directory.GetCurrentDirectory();
            var manifestRoot = args.Length > 1
                ? Path.GetFullPath(args[1])
                : Path.Combine(workspaceRoot, "Plugins", "Windows", "Src");
            var outputPath = args.Length > 2
                ? Path.GetFullPath(args[2])
                : Path.Combine(
                    workspaceRoot,
                    "Plugins",
                    "Windows",
                    "AgentFlow.Windows.NativeHost",
                    "Generated",
                    "NativeNodeProxies.g.cs");

            if (!Directory.Exists(manifestRoot))
                throw new DirectoryNotFoundException($"Native manifest directory was not found: {manifestRoot}");

            var manifests = Directory
                .EnumerateFiles(manifestRoot, "manifest.json", SearchOption.AllDirectories)
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}win-x64{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase))
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .Select(LoadManifest)
                .ToList();

            if (manifests.Count == 0)
                throw new InvalidOperationException($"No native node manifests were found under {manifestRoot}.");

            ValidateManifests(manifests);
            var source = Generate(manifests);
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);

            var existing = File.Exists(outputPath) ? File.ReadAllText(outputPath) : null;
            if (!string.Equals(existing, source, StringComparison.Ordinal))
            {
                File.WriteAllText(outputPath, source, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                Console.WriteLine($"Generated {manifests.Count} native node proxy class(es): {outputPath}");
            }
            else
            {
                Console.WriteLine($"Native node proxies are up to date: {outputPath}");
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine(ex);
            return 1;
        }
    }

    private static NativeNodeManifest LoadManifest(string path)
    {
        using var stream = File.OpenRead(path);
        var manifest = JsonSerializer.Deserialize<NativeNodeManifest>(stream, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true
        });

        if (manifest is null)
            throw new InvalidOperationException($"Native manifest was empty: {path}");

        manifest.SourcePath = path;
        return manifest;
    }

    private static void ValidateManifests(IReadOnlyList<NativeNodeManifest> manifests)
    {
        var nodeTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var modules = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var manifest in manifests)
        {
            Require(manifest.ModuleId, "moduleId", manifest);
            Require(manifest.NodeType, "nodeType", manifest);
            Require(manifest.ProxyClassName, "proxyClassName", manifest);
            Require(manifest.DisplayName, "displayName", manifest);
            Require(manifest.NativeBinary, "nativeBinary", manifest);
            Require(manifest.Uuid, "uuid", manifest);

            if (manifest.AbiVersion <= 0)
                throw new InvalidOperationException($"Native manifest '{manifest.SourcePath}' has an invalid abiVersion.");
            if (!string.Equals(manifest.Platform, "windows", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Native manifest '{manifest.SourcePath}' must declare platform='windows'.");
            if (manifest.Category != "W")
                throw new InvalidOperationException($"Native manifest '{manifest.SourcePath}' must use category W.");
            if (!nodeTypes.Add(manifest.NodeType!))
                throw new InvalidOperationException($"Duplicate native node type '{manifest.NodeType}'.");
            if (!modules.Add(manifest.ModuleId!))
                throw new InvalidOperationException($"Duplicate native module id '{manifest.ModuleId}'.");

            foreach (var pin in manifest.Pins)
            {
                Require(pin.Name, "pins.name", manifest);
                if (pin.Direction is not ("Input" or "Output"))
                    throw new InvalidOperationException($"Native manifest '{manifest.SourcePath}' has invalid pin direction '{pin.Direction}'.");
                if (pin.DataType is not ("STRING" or "INT32" or "INT64" or "BOOL" or "FLOAT" or "DOUBLE" or "BIN"))
                    throw new InvalidOperationException($"Native manifest '{manifest.SourcePath}' has invalid pin dataType '{pin.DataType}'.");
            }
        }
    }

    private static void Require(string? value, string field, NativeNodeManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new InvalidOperationException($"Native manifest '{manifest.SourcePath}' is missing required field '{field}'.");
    }

    private static string Generate(IReadOnlyList<NativeNodeManifest> manifests)
    {
        var builder = new StringBuilder();
        builder.AppendLine("// <auto-generated />");
        builder.AppendLine("#nullable enable");
        builder.AppendLine("// Generated by Tools/NativeNodeCodeGen from Plugins/Windows/Src/**/manifest.json.");
        builder.AppendLine("using AgentFlow.Contracts;");
        builder.AppendLine();
        builder.AppendLine("namespace AgentFlow.Windows.NativeHost;");
        builder.AppendLine();

        foreach (var manifest in manifests)
        {
            var className = manifest.ProxyClassName!;
            builder.Append("public sealed class ").Append(className).AppendLine(" : WindowsNativeNodeBase");
            builder.AppendLine("{");
            builder.Append("    public ").Append(className).AppendLine("()");
            builder.AppendLine("    {");
            builder.Append("        Uuid = ").Append(ToLiteral(manifest.Uuid!)).AppendLine(";");
            builder.AppendLine("        AddParam();");
            builder.AppendLine("        AddPins();");
            builder.AppendLine("    }");
            builder.AppendLine();
            builder.Append("    public override string Name => ").Append(ToLiteral(manifest.NodeType!)).AppendLine(";");
            builder.Append("    public override string DisplayName { get; set; } = ").Append(ToLiteral(manifest.DisplayName!)).AppendLine(";");
            builder.Append("    public override string Category => ").Append(ToLiteral(manifest.Category!)).AppendLine(";");
            builder.AppendLine();
            builder.Append("    protected override string ModuleId => ").Append(ToLiteral(manifest.ModuleId!)).AppendLine(";");
            builder.Append("    protected override string NativeBinaryFileName => ").Append(ToLiteral(manifest.NativeBinary!)).AppendLine(";");
            builder.Append("    protected override int ExpectedAbiVersion => ").Append(manifest.AbiVersion).AppendLine(";");
            builder.AppendLine();
            builder.AppendLine("    protected override IReadOnlyList<NativePinDefinition> GeneratedPins { get; } = new NativePinDefinition[]");
            builder.AppendLine("    {");

            foreach (var pin in manifest.Pins)
            {
                builder.Append("        new(")
                    .Append(ToLiteral(pin.Name!)).Append(", DataType.")
                    .Append(pin.DataType).Append(", PinDirection.")
                    .Append(pin.Direction).Append(", Required: ")
                    .Append(pin.Required ? "true" : "false").AppendLine("),");
            }

            builder.AppendLine("    };");
            builder.AppendLine();
            builder.AppendLine("    public override void Configure(IReadOnlyDictionary<string, object?> parameters)");
            builder.AppendLine("    {");
            builder.AppendLine("        // Native nodes currently expose only the standard General parameters.");
            builder.AppendLine("    }");
            builder.AppendLine("}");
            builder.AppendLine();
        }

        return builder.ToString();
    }

    private static string ToLiteral(string value) => JsonSerializer.Serialize(value);

    private sealed class NativeNodeManifest
    {
        [JsonPropertyName("moduleId")]
        public string? ModuleId { get; set; }

        [JsonPropertyName("nodeType")]
        public string? NodeType { get; set; }

        [JsonPropertyName("proxyClassName")]
        public string? ProxyClassName { get; set; }

        [JsonPropertyName("displayName")]
        public string? DisplayName { get; set; }

        [JsonPropertyName("category")]
        public string? Category { get; set; }

        [JsonPropertyName("platform")]
        public string? Platform { get; set; }

        [JsonPropertyName("abiVersion")]
        public int AbiVersion { get; set; }

        [JsonPropertyName("uuid")]
        public string? Uuid { get; set; }

        [JsonPropertyName("nativeBinary")]
        public string? NativeBinary { get; set; }

        [JsonPropertyName("pins")]
        public List<NativePinManifest> Pins { get; set; } = new();

        [JsonIgnore]
        public string SourcePath { get; set; } = string.Empty;
    }

    private sealed class NativePinManifest
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("direction")]
        public string? Direction { get; set; }

        [JsonPropertyName("dataType")]
        public string? DataType { get; set; }

        [JsonPropertyName("required")]
        public bool Required { get; set; }
    }
}
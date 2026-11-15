// -----------------------------------------------------------------------
// <copyright company="Your Company" file="FactFlatFileParserNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.FACTFlatFileParser;

/// <summary>
/// Parses a FACT XML log file and publishes both the original XML text and a normalized JSON
/// representation, including the legacy FlatFile row as indexed fields.
/// </summary>
public sealed class FactFlatFileParserNode : BaseNode
{
    private const string ExpectedRootName = "FactLogInfo";

    private readonly BasePin _pathInputPin =
        new("input_string_path", DataType.STRING, PinDirection.Input, required: false);

    private readonly BasePin _rawXmlOutputPin =
        new("output_string_raw_xml", DataType.STRING, PinDirection.Output);

    private readonly BasePin _jsonOutputPin =
        new("output_string_json", DataType.STRING, PinDirection.Output);

    private ILogger? _logger;

    public FactFlatFileParserNode()
    {
        Uuid = "167802E5-B5E7-4A35-BEF4-710D4A7C4FDC";
        AddParam();
        AddPins();
    }

    public override string Name => "FACT Flat File Parser";
    public override string DisplayName { get; set; } = "FACT Flat File Parser";
    public override string Category => "Parsing";

    public override void AddPins()
    {
        AddInputPin(_pathInputPin);
        AddOutputPin(_rawXmlOutputPin);
        AddOutputPin(_jsonOutputPin);
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        // This parser currently has no configurable parameters.
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
        _logger?.LogTrace("FACT Flat File Parser is running.");
        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        _logger?.LogTrace("FACT Flat File Parser stopped.");
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
                return;
            }

            try
            {
                ParseAndPublish(inputPath.Trim());
            }
            catch (Exception ex)
            {
                _logger?.LogError(
                    ex,
                    "FACT Flat File Parser failed for input {InputPath}: {Message}",
                    inputPath,
                    ex.Message);
            }
        }
    }

    private void ParseAndPublish(string inputPath)
    {
        var fullPath = Path.GetFullPath(inputPath);
        if (!File.Exists(fullPath))
            throw new FileNotFoundException("FACT XML file was not found.", fullPath);
        if (!Path.GetExtension(fullPath).Equals(".xml", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"Expected an XML file path, got '{fullPath}'.");

        var rawXml = ReadXmlText(fullPath);
        var document = ParseXml(rawXml);
        var root = document.Root
            ?? throw new InvalidDataException($"XML file '{fullPath}' has no root element.");
        if (!root.Name.LocalName.Equals(ExpectedRootName, StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Expected XML root '{ExpectedRootName}', got '{root.Name.LocalName}' in '{fullPath}'.");
        }

        var flatFile = root.Descendants()
            .FirstOrDefault(element => element.Name.LocalName == "FlatFile")
            ?.Value ?? string.Empty;
        var json = BuildJson(fullPath, root, flatFile);

        _logger?.LogInformation(
            "FACT Flat File Parser parsed {Path}: root={Root}, xmlLength={XmlLength}, flatFileFields={FieldCount}.",
            fullPath,
            root.Name.LocalName,
            rawXml.Length,
            CountFlatFileFields(flatFile));

        PublishOutput(_rawXmlOutputPin, rawXml, "raw XML");
        PublishOutput(_jsonOutputPin, json, "normalized FACT JSON");
    }

    private static string ReadXmlText(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true);
        return reader.ReadToEnd();
    }

    private static XDocument ParseXml(string rawXml)
    {
        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreComments = false
        };

        using var textReader = new StringReader(rawXml);
        using var xmlReader = XmlReader.Create(textReader, settings);
        return XDocument.Load(xmlReader, LoadOptions.PreserveWhitespace);
    }

    private static string BuildJson(string fullPath, XElement root, string flatFile)
    {
        var json = new JsonObject
        {
            ["schemaVersion"] = "1.0",
            ["sourceFile"] = fullPath,
            ["root"] = root.Name.LocalName,
            ["xml"] = ElementValueToJson(root),
            ["flatFile"] = BuildFlatFileJson(flatFile)
        };

        return json.ToJsonString(new JsonSerializerOptions
        {
            WriteIndented = true
        });
    }

    private static JsonObject BuildFlatFileJson(string flatFile)
    {
        var fields = flatFile.Split(',');
        var fieldArray = new JsonArray();
        for (var index = 0; index < fields.Length; index++)
        {
            fieldArray.Add(new JsonObject
            {
                ["index"] = index,
                ["value"] = fields[index]
            });
        }

        return new JsonObject
        {
            ["raw"] = flatFile,
            ["fieldCount"] = fields.Length,
            ["fields"] = fieldArray
        };
    }

    private static int CountFlatFileFields(string flatFile) => flatFile.Split(',').Length;

    private static JsonNode? ElementValueToJson(XElement element)
    {
        var attributes = element.Attributes()
            .Where(attribute => !attribute.IsNamespaceDeclaration)
            .ToList();
        var children = element.Elements().ToList();

        if (attributes.Count == 0 && children.Count == 0)
            return JsonValue.Create(element.Value.Trim());

        var result = new JsonObject();
        foreach (var attribute in attributes)
            result[attribute.Name.LocalName] = attribute.Value;

        var childGroups = new Dictionary<string, List<XElement>>(StringComparer.Ordinal);
        var childOrder = new List<string>();
        foreach (var child in children)
        {
            var name = child.Name.LocalName;
            if (!childGroups.TryGetValue(name, out var group))
            {
                group = new List<XElement>();
                childGroups[name] = group;
                childOrder.Add(name);
            }

            group.Add(child);
        }

        foreach (var name in childOrder)
        {
            var group = childGroups[name];
            if (group.Count == 1)
            {
                result[name] = ElementValueToJson(group[0]);
                continue;
            }

            var array = new JsonArray();
            foreach (var child in group)
                array.Add(ElementValueToJson(child));
            result[name] = array;
        }

        var directText = string.Concat(
                element.Nodes()
                    .OfType<XText>()
                    .Select(text => text.Value))
            .Trim();
        if (directText.Length > 0)
            result["value"] = directText;

        return result;
    }

    private void PublishOutput(BasePin outputPin, string value, string operation)
    {
        if (outputPin.TargetCount == 0)
        {
            _logger?.LogWarning(
                "FACT Flat File Parser generated {Operation}, but output pin {Pin} is not connected.",
                operation,
                outputPin.Name);
            return;
        }

        try
        {
            var sample = StringSample.CreateSample();
            sample.SetValue(value, outputPin.Name);
            outputPin.SendSampleWithDispatchLog(sample, operation, LogLevel.Information);
        }
        catch (Exception ex)
        {
            _logger?.LogError(
                ex,
                "FACT Flat File Parser could not publish {Operation} through {Pin}: {Message}",
                operation,
                outputPin.Name,
                ex.Message);
        }
    }
}
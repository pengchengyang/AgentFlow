// -----------------------------------------------------------------------
// <copyright company="Your Company" file="ReceiverNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Globalization;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.Receiver;

/// <summary>
/// Sink node with a single input. Every sample received on <c>input</c> (including null) is
/// written to the node logger at Information level; the node never forwards or stores data.
/// </summary>
public sealed class ReceiverNode : BaseNode
{
    private static readonly string[] DataTypeOptions =
    {
        "string", "int32", "int64", "float", "double", "binary", "bool"
    };

    private readonly BasePin _inPin =
        new("input", DataType.STRING, PinDirection.Input, required: false);

    private ILogger? _logger;

    public ReceiverNode()
    {
        Uuid = "2A0DD4D4-8261-464B-AFA3-F453B5E1A5FA";
        AddParam();
        AddPins();
        ApplyDataType(Param("DataType").Value);
    }

    public override string Name => "Receiver";
    public override string DisplayName { get; set; } = "Receiver";
    public override string Category => "General";

    public override void AddPins()
    {
        AddInputPin(_inPin);
    }

    protected override void AddParam()
    {
        base.AddParam();
        AddParameter(new NodeParameter(
            "DataType", typeof(string), "string",
            isEditable: true, group: "General", zone: "ReceiverZone",
            editor: ParameterEditorKind.SingleSelect,
            options: DataTypeOptions));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("DataType", out var dt) && dt is not null)
            Param("DataType").Value = ParameterValues.GetString(dt, "string") ?? "string";

        ApplyDataType(Param("DataType").Value);
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
    {
        _logger = context.Logger;
        return Task.CompletedTask;
    }

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        Running = true;
        return Task.CompletedTask;
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        return Task.CompletedTask;
    }

    /// <summary>Log every received value at Information level; no output is produced.</summary>
    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (ReferenceEquals(pin, _inPin))
        {
            _logger?.LogInformation(
                "Receiver {Node} received on {Pin}: {Type} {Value}",
                DisplayName,
                pin.Name,
                value?.Type.ToString() ?? "null",
                FormatValue(value));

        }
    }

    private NodeParameter Param(string name) => NodeParameters.First(p => p.Name == name);

    private void ApplyDataType(object? value)
    {
        _inPin.DataType = ParseDataType(value);
    }

    private static DataType ParseDataType(object? value)
    {
        return (ParameterValues.GetString(value, "string") ?? "").Trim().ToLowerInvariant() switch
        {
            "string" => DataType.STRING,
            "int32" => DataType.INT32,
            "int64" => DataType.INT64,
            "float" => DataType.FLOAT,
            "double" => DataType.DOUBLE,
            "binary" => DataType.BIN,
            "bool" => DataType.BOOL,
            _ => DataType.STRING,
        };
    }

    private static string FormatValue(Sample? sample)
    {
        if (sample is null)
            return "(null)";

        var value = sample.GetValue(sample.Type);
        if (value is null)
            return "(null)";

        if (sample.Type == DataType.BIN && value is byte[] bytes)
        {
            return bytes.Length == 0
                ? "(empty)"
                : string.Join(' ', bytes.Select(b => b.ToString("X2", CultureInfo.InvariantCulture)));
        }

        return Convert.ToString(value, CultureInfo.InvariantCulture) ?? "(null)";
    }
    /// <summary>
    /// Final application-exit hook. Runtime resources were already released by Stop; this node
    /// currently has no additional process-exit cleanup.
    /// </summary>
    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;
}

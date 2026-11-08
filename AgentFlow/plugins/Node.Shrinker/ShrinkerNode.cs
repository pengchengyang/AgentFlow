// -----------------------------------------------------------------------
// <copyright company="Your Company" file="ShrinkerNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Node.Shrinker;

/// <summary>
/// Ten-input, one-output fan-in node. The selected data type is applied to every port by
/// <see cref="Configure"/>. Every non-null sample that arrives on any input is forwarded to
/// the single output; the node does not wait for a particular input and does not merge values.
/// </summary>
public sealed class ShrinkerNode : BaseNode
{
    private static readonly string[] DataTypeOptions =
    {
        "string", "int32", "int64", "float", "double", "binary", "bool"
    };

    private readonly BasePin[] _inPins =
    {
        new("input1", DataType.STRING, PinDirection.Input, required: false),
        new("input2", DataType.STRING, PinDirection.Input, required: false),
        new("input3", DataType.STRING, PinDirection.Input, required: false),
        new("input4", DataType.STRING, PinDirection.Input, required: false),
        new("input5", DataType.STRING, PinDirection.Input, required: false),
        new("input6", DataType.STRING, PinDirection.Input, required: false),
        new("input7", DataType.STRING, PinDirection.Input, required: false),
        new("input8", DataType.STRING, PinDirection.Input, required: false),
        new("input9", DataType.STRING, PinDirection.Input, required: false),
        new("input10", DataType.STRING, PinDirection.Input, required: false),
    };

    private readonly BasePin _outPin =
        new("output", DataType.STRING, PinDirection.Output);

    public ShrinkerNode()
    {
        Uuid = "9D6EE2B4-730E-4FCE-BC72-55FDB8D8B029";
        AddParam();
        AddPins();
        ApplyDataType(Param("DataType").Value);
    }

    public override string Name => "Shrinker";
    public override string DisplayName { get; set; } = "Shrinker";
    public override string Category => "General";

    public override void AddPins()
    {
        foreach (var pin in _inPins)
            AddInputPin(pin);
        AddOutputPin(_outPin);
    }

    protected override void AddParam()
    {
        base.AddParam();
        AddParameter(new NodeParameter(
            "DataType", typeof(string), "string",
            isEditable: true, group: "General", zone: "ShrinkerZone",
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
        => Task.CompletedTask;

    /// <summary>
    /// Data-driven startup: every arriving input sample is handled by <see cref="ReceiveSample"/>.
    /// Returning here marks the node as started and keeps it registered until the workflow stops.
    /// </summary>
    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        Running = true;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Forward every non-null sample arriving on any of the ten inputs to the single output pin.
    /// </summary>
    public override void ReceiveSample(BasePin pin, Sample? value)
    {
        if (_inPins.Contains(pin))
        {
            if (value is null)
            {
                pin.Context?.Logger.LogError("Shrinker ignored a null sample on {Pin}.", pin.Name);
                return;
            }

            _outPin.SendSample(value);
        }
    }

    public override Task Stop(INodeContext context, CancellationToken ct = default)
    {
        Running = false;
        return Task.CompletedTask;
    }

    private NodeParameter Param(string name) => NodeParameters.First(p => p.Name == name);

    private void ApplyDataType(object? value)
    {
        var type = ParseDataType(value);
        foreach (var pin in InputPins)
            pin.DataType = type;
        foreach (var pin in OutputPins)
            pin.DataType = type;
    }

    private static DataType ParseDataType(object? value)
    {
        return (ParameterValues.GetString(value) ?? "").Trim().ToLowerInvariant() switch
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
    /// <summary>
    /// Final application-exit hook. Runtime resources were already released by Stop; this node
    /// currently has no additional process-exit cleanup.
    /// </summary>
    public override Task Shutdown(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;
}

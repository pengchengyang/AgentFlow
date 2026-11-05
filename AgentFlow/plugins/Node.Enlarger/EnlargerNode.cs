// -----------------------------------------------------------------------
// <copyright company="Your Company" file="EnlargerNode.cs">
//     Copyright (c) Your Company. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;

namespace Node.Enlarger;

/// <summary>
/// One-input, five-output fan-out node. The selected data type is applied to every port by
/// <see cref="Configure"/>; the actual fan-out behavior is intentionally left as a TODO until
/// the runtime semantics are defined.
/// </summary>
public sealed class EnlargerNode : BaseNode
{
    private static readonly string[] DataTypeOptions =
    {
        "int32", "int64", "float", "double", "binary", "bool"
    };

    private readonly BasePin _inPin =
        new("input", DataType.INT32, PinDirection.Input, required: false);

    private readonly BasePin[] _outPins =
    {
        new("output1", DataType.INT32, PinDirection.Output),
        new("output2", DataType.INT32, PinDirection.Output),
        new("output3", DataType.INT32, PinDirection.Output),
        new("output4", DataType.INT32, PinDirection.Output),
        new("output5", DataType.INT32, PinDirection.Output),
    };

    public EnlargerNode()
    {
        Uuid = "9FEEEE59-908A-46C3-BC01-DF36D44AE6EF";
        AddParam();
        AddPins();
        ApplyDataType(Param("DataType").Value);
    }

    public override string Name => "Enlarger";
    public override string DisplayName { get; set; } = "Enlarger";
    public override string Category => "General";

    public override void AddPins()
    {
        AddInputPin(_inPin);
        foreach (var pin in _outPins)
            AddOutputPin(pin);
    }

    protected override void AddParam()
    {
        base.AddParam();
        AddParameter(new NodeParameter(
            "DataType", typeof(string), "int32",
            isEditable: true, group: "General", zone: "EnlargerZone",
            editor: ParameterEditorKind.SingleSelect,
            options: DataTypeOptions));
    }

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("DataType", out var dt) && dt is not null)
            Param("DataType").Value = ParameterValues.GetString(dt, "int32") ?? "int32";

        ApplyDataType(Param("DataType").Value);
    }

    public override Task Initialize(INodeContext context, CancellationToken ct = default)
        => Task.CompletedTask;

    public override Task Run(INodeContext context, CancellationToken ct = default)
    {
        // The node is operational and waits for incoming samples.
        Running = true;
        return Task.CompletedTask;
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
            "int32" => DataType.INT32,
            "int64" => DataType.INT64,
            "float" => DataType.FLOAT,
            "double" => DataType.DOUBLE,
            "binary" => DataType.BIN,
            "bool" => DataType.BOOL,
            _ => DataType.INT32,
        };
    }
}

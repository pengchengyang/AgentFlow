using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Nodes.Amplifier;

/// <summary>
/// Amplifier node: one double input, four double outputs. Every output emits the
/// input scaled by the "Gain" parameter (default 4x), so it acts as a multi-way
/// amplifier / fan-out stage.
/// </summary>
[Node("nodes.amplifier", "Amplifier", "Math")]
public sealed class AmplifierNode : BaseNode
{
    private double _gain = 4.0;

    public override string TypeId => "nodes.amplifier";
    public override string DisplayName => "Amplifier";

    public override IReadOnlyList<PinDefinition> InputPins { get; } =
    [
        new("In", typeof(double), PinDirection.Input)
    ];

    public override IReadOnlyList<PinDefinition> OutputPins { get; } =
    [
        new("Out1", typeof(double), PinDirection.Output),
        new("Out2", typeof(double), PinDirection.Output),
        new("Out3", typeof(double), PinDirection.Output),
        new("Out4", typeof(double), PinDirection.Output)
    ];

    public override IReadOnlyList<ParameterDefinition> Parameters =>
    [
        new("Gain", typeof(double), "Gain", 4.0, "Amplification factor applied to the input")
    ];

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("Gain", out var v) && v is not null)
            _gain = Convert.ToDouble(v);
    }

    public override Task ExecuteAsync(INodeContext context, CancellationToken ct = default)
    {
        var input = context.GetInput<double>("In");
        var amplified = input * _gain;
        for (var i = 1; i <= 4; i++)
            context.SetOutput($"Out{i}", amplified);
        context.Logger.LogInformation("Amplifier: {Input} * {Gain} = {Amplified}", input, _gain, amplified);
        return Task.CompletedTask;
    }
}

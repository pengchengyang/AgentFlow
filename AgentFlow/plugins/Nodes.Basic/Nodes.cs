using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Nodes.Basic;

/// <summary>Number source node: outputs a constant (editable in the property panel).</summary>
[Node("basic.number", "Number", "Input")]
public sealed class NumberSourceNode : INode
{
    private double _value;

    public string TypeId => "basic.number";
    public string DisplayName => "Number";

    public IReadOnlyList<PinDefinition> Pins { get; } =
    [
        new("Value", typeof(double), PinDirection.Output)
    ];

    public IReadOnlyList<ParameterDefinition> Parameters { get; } =
    [
        new("Value", typeof(double), "Value", 1.0, "The constant number to output")
    ];

    public void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("Value", out var v) && v is not null)
            _value = Convert.ToDouble(v);
    }

    public Task ExecuteAsync(INodeContext context, CancellationToken ct = default)
    {
        context.Logger.LogInformation("Output number {Value}", _value);
        context.SetOutput("Value", _value);
        return Task.CompletedTask;
    }
}

/// <summary>Add node: A + B.</summary>
[Node("basic.add", "Add", "Logic")]
public sealed class AddNode : INode
{
    public string TypeId => "basic.add";
    public string DisplayName => "Add";

    public IReadOnlyList<PinDefinition> Pins { get; } =
    [
        new("A", typeof(double), PinDirection.Input),
        new("B", typeof(double), PinDirection.Input),
        new("Sum", typeof(double), PinDirection.Output)
    ];

    public IReadOnlyList<ParameterDefinition> Parameters { get; } = [];

    public void Configure(IReadOnlyDictionary<string, object?> parameters) { }

    public Task ExecuteAsync(INodeContext context, CancellationToken ct = default)
    {
        var a = context.GetInput<double>("A");
        var b = context.GetInput<double>("B");
        var sum = a + b;
        context.Logger.LogInformation("{A} + {B} = {Sum}", a, b, sum);
        context.SetOutput("Sum", sum);
        return Task.CompletedTask;
    }
}

/// <summary>Print node: logs the value and publishes it to the external GUI.</summary>
[Node("basic.print", "Print / Publish", "Output")]
public sealed class PrintNode : INode
{
    private string _topic = "result";

    public string TypeId => "basic.print";
    public string DisplayName => "Print / Publish";

    public IReadOnlyList<PinDefinition> Pins { get; } =
    [
        new("Value", typeof(object), PinDirection.Input)
    ];

    public IReadOnlyList<ParameterDefinition> Parameters { get; } =
    [
        new("Topic", typeof(string), "Topic", "result", "GuiBridge topic (external GUIs subscribe by topic)")
    ];

    public void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("Topic", out var v) && v is string s && !string.IsNullOrWhiteSpace(s))
            _topic = s;
    }

    public Task ExecuteAsync(INodeContext context, CancellationToken ct = default)
    {
        var value = context.GetInput<object>("Value");
        context.Logger.LogInformation("Print: {Value}", value);
        // Publish to the business GUI (e.g. a yield dashboard).
        context.Gui.Publish(_topic, value);
        return Task.CompletedTask;
    }
}

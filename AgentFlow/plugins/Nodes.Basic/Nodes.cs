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

/// <summary>Multiply node: A * B.</summary>
[Node("basic.multiply", "Multiply", "Logic")]
public sealed class MultiplyNode : INode
{
    public string TypeId => "basic.multiply";
    public string DisplayName => "Multiply";

    public IReadOnlyList<PinDefinition> Pins { get; } =
    [
        new("A", typeof(double), PinDirection.Input),
        new("B", typeof(double), PinDirection.Input),
        new("Product", typeof(double), PinDirection.Output)
    ];

    public IReadOnlyList<ParameterDefinition> Parameters { get; } = [];

    public void Configure(IReadOnlyDictionary<string, object?> parameters) { }

    public Task ExecuteAsync(INodeContext context, CancellationToken ct = default)
    {
        var a = context.GetInput<double>("A");
        var b = context.GetInput<double>("B");
        var product = a * b;
        context.Logger.LogInformation("{A} * {B} = {Product}", a, b, product);
        context.SetOutput("Product", product);
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

/// <summary>
/// Coupler (ALC-inspired): forwards the first non-null input to the output.
/// Useful for joining several optional data sources into one downstream pipe.
/// </summary>
[Node("basic.coupler", "Coupler", "Flow")]
public sealed class CouplerNode : INode
{
    public string TypeId => "basic.coupler";
    public string DisplayName => "Coupler";

    public IReadOnlyList<PinDefinition> Pins { get; } =
    [
        new("In1", typeof(object), PinDirection.Input, false),
        new("In2", typeof(object), PinDirection.Input, false),
        new("In3", typeof(object), PinDirection.Input, false),
        new("In4", typeof(object), PinDirection.Input, false),
        new("Out", typeof(object), PinDirection.Output)
    ];

    public IReadOnlyList<ParameterDefinition> Parameters { get; } = [];

    public void Configure(IReadOnlyDictionary<string, object?> parameters) { }

    public Task ExecuteAsync(INodeContext context, CancellationToken ct = default)
    {
        foreach (var name in new[] { "In1", "In2", "In3", "In4" })
        {
            if (context.GetInput<object>(name) is { } value)
            {
                context.Logger.LogInformation("Coupler forwarded {Pin}: {Value}", name, value);
                context.SetOutput("Out", value);
                return Task.CompletedTask;
            }
        }

        context.Logger.LogInformation("Coupler: no connected input has a value; nothing to forward");
        return Task.CompletedTask;
    }
}

/// <summary>
/// Multiplexer (ALC-inspired): selects one input based on the "Selected" parameter.
/// </summary>
[Node("basic.multiplexer", "Multiplexer", "Flow")]
public sealed class MultiplexerNode : INode
{
    private int _selected;

    public string TypeId => "basic.multiplexer";
    public string DisplayName => "Multiplexer";

    public IReadOnlyList<PinDefinition> Pins { get; } =
    [
        new("In0", typeof(object), PinDirection.Input, false),
        new("In1", typeof(object), PinDirection.Input, false),
        new("In2", typeof(object), PinDirection.Input, false),
        new("In3", typeof(object), PinDirection.Input, false),
        new("Out", typeof(object), PinDirection.Output)
    ];

    public IReadOnlyList<ParameterDefinition> Parameters { get; } =
    [
        new("Selected", typeof(int), "Selected Index", 0, "Which input (0-based) to route to the output")
    ];

    public void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("Selected", out var v) && v is not null)
            _selected = Convert.ToInt32(v);
    }

    public Task ExecuteAsync(INodeContext context, CancellationToken ct = default)
    {
        var pinName = $"In{Math.Clamp(_selected, 0, 3)}";
        var value = context.GetInput<object>(pinName);
        context.Logger.LogInformation("Multiplexer selected {Pin}: {Value}", pinName, value);
        context.SetOutput("Out", value);
        return Task.CompletedTask;
    }
}

/// <summary>
/// Delay (ALC-inspired "Periods/flow timing" idea): pauses for a configurable
/// number of milliseconds and then passes the input value through unchanged.
/// </summary>
[Node("basic.delay", "Delay", "Flow")]
public sealed class DelayNode : INode
{
    private int _delayMs = 100;

    public string TypeId => "basic.delay";
    public string DisplayName => "Delay";

    public IReadOnlyList<PinDefinition> Pins { get; } =
    [
        new("In", typeof(object), PinDirection.Input),
        new("Out", typeof(object), PinDirection.Output)
    ];

    public IReadOnlyList<ParameterDefinition> Parameters { get; } =
    [
        new("DelayMs", typeof(int), "Delay (ms)", 100, "Milliseconds to wait before forwarding")
    ];

    public void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("DelayMs", out var v) && v is not null)
            _delayMs = Math.Max(0, Convert.ToInt32(v));
    }

    public async Task ExecuteAsync(INodeContext context, CancellationToken ct = default)
    {
        var value = context.GetInput<object>("In");
        context.Logger.LogInformation("Delay {DelayMs} ms...", _delayMs);
        await Task.Delay(_delayMs, ct);
        context.SetOutput("Out", value);
    }
}

/// <summary>
/// Device lifecycle demo (ALC-inspired): shows how a node can own resources that
/// need Initialize / Start / Stop hooks. In a real plugin this is where you would
/// open a camera, serial port, socket, or DAQ board.
/// </summary>
[Node("basic.device", "Device (lifecycle)", "Flow")]
public sealed class DeviceLifecycleNode : INode, ILifecycleNode
{
    private string _deviceName = "device-1";
    private bool _enabled = true;

    public string TypeId => "basic.device";
    public string DisplayName => "Device (lifecycle)";

    public IReadOnlyList<PinDefinition> Pins { get; } =
    [
        new("In", typeof(object), PinDirection.Input, false),
        new("Out", typeof(object), PinDirection.Output)
    ];

    public IReadOnlyList<ParameterDefinition> Parameters { get; } =
    [
        new("DeviceName", typeof(string), "Device Name", "device-1", "Name of the device/resource"),
        new("Enabled", typeof(bool), "Enabled", true, "Whether the device participates in the workflow")
    ];

    public void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("DeviceName", out var name) && name is string s && !string.IsNullOrWhiteSpace(s))
            _deviceName = s;
        if (parameters.TryGetValue("Enabled", out var en) && en is not null)
            _enabled = Convert.ToBoolean(en);
    }

    public Task InitializeAsync(INodeContext context, CancellationToken ct = default)
    {
        context.Logger.LogInformation("Device '{Device}' initializing", _deviceName);
        return Task.CompletedTask;
    }

    public Task StartAsync(INodeContext context, CancellationToken ct = default)
    {
        context.Logger.LogInformation("Device '{Device}' started (enabled={Enabled})", _deviceName, _enabled);
        return Task.CompletedTask;
    }

    public Task StopAsync(INodeContext context, CancellationToken ct = default)
    {
        context.Logger.LogInformation("Device '{Device}' stopped", _deviceName);
        return Task.CompletedTask;
    }

    public Task ExecuteAsync(INodeContext context, CancellationToken ct = default)
    {
        var value = context.GetInput<object>("In");
        context.Logger.LogInformation("Device '{Device}' step -> {Value}", _deviceName, value);
        if (_enabled)
            context.SetOutput("Out", value);
        return Task.CompletedTask;
    }
}

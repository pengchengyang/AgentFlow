using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Nodes.Strings;

/// <summary>
/// (a) String source: a single OUTPUT pin that emits a (configurable) string.
/// </summary>
[Node("strings.source", "String Source", "Input")]
public sealed class StringSourceNode : INode
{
    private string _text = "Hello, AgentFlow!";

    public string TypeId => "strings.source";
    public string DisplayName => "String Source";

    public IReadOnlyList<PinDefinition> Pins { get; } =
    [
        new("Output", typeof(string), PinDirection.Output)
    ];

    public IReadOnlyList<ParameterDefinition> Parameters { get; } =
    [
        new("Text", typeof(string), "Text", "Hello, AgentFlow!", "The string emitted on the output pin")
    ];

    public void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("Text", out var v) && v is not null)
            _text = v.ToString() ?? string.Empty;
    }

    public Task ExecuteAsync(INodeContext context, CancellationToken ct = default)
    {
        context.Logger.LogInformation("String Source emitted: {Text}", _text);
        // SetOutput -> output pin Send -> every connected input pin Receive.
        context.SetOutput("Output", _text);
        return Task.CompletedTask;
    }
}

/// <summary>
/// (b) String sink: a single INPUT pin. Whatever string it receives is written to the log panel.
/// </summary>
[Node("strings.sink", "String Sink / Log", "Output")]
public sealed class StringSinkNode : INode
{
    public string TypeId => "strings.sink";
    public string DisplayName => "String Sink / Log";

    public IReadOnlyList<PinDefinition> Pins { get; } =
    [
        new("Input", typeof(string), PinDirection.Input)
    ];

    public IReadOnlyList<ParameterDefinition> Parameters { get; } = [];

    public void Configure(IReadOnlyDictionary<string, object?> parameters) { }

    public Task ExecuteAsync(INodeContext context, CancellationToken ct = default)
    {
        var value = context.GetInput<string>("Input") ?? string.Empty;
        context.Logger.LogInformation("String Sink received: {Value}", value);
        return Task.CompletedTask;
    }
}

/// <summary>
/// (c) String upper-case: one INPUT pin (string) and one OUTPUT pin (string).
/// Upper-cases every letter of the incoming string and emits it on the output.
/// </summary>
[Node("strings.to-upper", "String To Upper", "Logic")]
public sealed class StringToUpperNode : INode
{
    public string TypeId => "strings.to-upper";
    public string DisplayName => "String To Upper";

    public IReadOnlyList<PinDefinition> Pins { get; } =
    [
        new("Input", typeof(string), PinDirection.Input),
        new("Output", typeof(string), PinDirection.Output)
    ];

    public IReadOnlyList<ParameterDefinition> Parameters { get; } = [];

    public void Configure(IReadOnlyDictionary<string, object?> parameters) { }

    public Task ExecuteAsync(INodeContext context, CancellationToken ct = default)
    {
        var value = context.GetInput<string>("Input") ?? string.Empty;
        var upper = value.ToUpperInvariant();
        context.Logger.LogInformation("String To Upper: {Before} -> {After}", value, upper);
        context.SetOutput("Output", upper);
        return Task.CompletedTask;
    }
}

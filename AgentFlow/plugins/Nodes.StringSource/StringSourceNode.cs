using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Nodes.StringSource;

/// <summary>
/// String source node: a single OUTPUT pin that emits a (configurable) string.
/// </summary>
[Node("nodes.string-source", "String Source", "Input")]
public sealed class StringSourceNode : BaseNode
{
    private string _text = "Hello, AgentFlow!";

    public override string TypeId => "nodes.string-source";
    public override string DisplayName => "String Source";

    public override IReadOnlyList<PinDefinition> InputPins { get; } = [];
    public override IReadOnlyList<PinDefinition> OutputPins { get; } =
    [
        new("Output", typeof(string), PinDirection.Output)
    ];

    public override IReadOnlyList<ParameterDefinition> Parameters =>
    [
        new("Text", typeof(string), "Text", "Hello, AgentFlow!", "The string emitted on the output pin")
    ];

    public override void Configure(IReadOnlyDictionary<string, object?> parameters)
    {
        if (parameters.TryGetValue("Text", out var v) && v is not null)
            _text = v.ToString() ?? string.Empty;
    }

    public override Task ExecuteAsync(INodeContext context, CancellationToken ct = default)
    {
        context.Logger.LogInformation("String Source emitted: {Text}", _text);
        // SetOutput -> output pin Send -> every connected input pin Receive.
        context.SetOutput("Output", _text);
        return Task.CompletedTask;
    }
}

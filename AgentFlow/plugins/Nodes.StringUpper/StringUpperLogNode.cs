using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace Nodes.StringUpper;

/// <summary>
/// String to-upper logger: a single INPUT pin. Whatever string it receives is
/// converted to upper case and written to the log.
/// </summary>
[Node("nodes.string-upper", "String To Upper / Log", "Logic")]
public sealed class StringUpperLogNode : BaseNode
{
    public override string TypeId => "nodes.string-upper";
    public override string DisplayName => "String To Upper / Log";

    public override IReadOnlyList<PinDefinition> InputPins { get; } =
    [
        new("Input", typeof(string), PinDirection.Input)
    ];
    public override IReadOnlyList<PinDefinition> OutputPins { get; } = [];

    public override void Configure(IReadOnlyDictionary<string, object?> parameters) { }

    public override Task ExecuteAsync(INodeContext context, CancellationToken ct = default)
    {
        var value = context.GetInput<string>("Input") ?? string.Empty;
        var upper = value.ToUpperInvariant();
        context.Logger.LogInformation("String To Upper (log): {Value}", upper);
        return Task.CompletedTask;
    }
}

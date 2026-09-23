using System.Reflection;

namespace AgentFlow.Contracts;

/// <summary>
/// Base class for every AgentFlow node. A node declares its pins by calling
/// <see cref="AddInputPin"/> / <see cref="AddOutputPin"/> (the pin sets are stored in
/// mutable lists, so a node can append pins as needed), and provides <see cref="TypeId"/>,
/// <see cref="DisplayName"/>, <see cref="Configure"/> and <see cref="ExecuteAsync"/>.
/// Optional lifecycle hooks <see cref="Initialize"/>, <see cref="Run"/> and
/// <see cref="Stop"/> default to no-ops.
/// </summary>
public abstract class BaseNode
{
    private readonly List<BasePin> _inputPins = new();
    private readonly List<BasePin> _outputPins = new();
    private string? _category;

    /// <summary>Node type id (must match NodeAttribute.TypeId).</summary>
    public abstract string TypeId { get; }

    /// <summary>Unique id of this node instance. Assigned/managed by AgentFlow.Core.</summary>
    public int InstanceId { get; set; }

    /// <summary>Display name in the UI.</summary>
    public abstract string DisplayName { get; }

    /// <summary>
    /// Node category (used by the palette grouping and accent color).
    /// Read from the <see cref="NodeAttribute"/> on the concrete node type.
    /// </summary>
    public virtual string Category => _category ??= GetType().GetCustomAttribute<NodeAttribute>()?.Category ?? "General";

    /// <summary>All input pin definitions (the left-hand ports), append via <see cref="AddInputPin"/>.</summary>
    public IReadOnlyList<BasePin> InputPins => _inputPins;

    /// <summary>All output pin definitions (the right-hand ports), append via <see cref="AddOutputPin"/>.</summary>
    public IReadOnlyList<BasePin> OutputPins => _outputPins;

    /// <summary>Append an input pin to the input list.</summary>
    public void AddInputPin(BasePin pin) => _inputPins.Add(pin);

    /// <summary>Append an output pin to the output list.</summary>
    public void AddOutputPin(BasePin pin) => _outputPins.Add(pin);

    /// <summary>Parameter declarations (used to auto-generate the property panel).</summary>
    public virtual IReadOnlyList<ParameterDefinition> Parameters => Array.Empty<ParameterDefinition>();

    /// <summary>
    /// Called by an input pin when data arrives from an upstream output pin.
    /// The default implementation is a no-op for nodes that only read inputs
    /// inside <see cref="ExecuteAsync"/>.
    /// </summary>
    public virtual void Receive(INodeContext context, BasePin pin, object? value) { }

    /// <summary>Apply parameters (from JSON deserialization / property panel).</summary>
    public abstract void Configure(IReadOnlyDictionary<string, object?> parameters);

    /// <summary>Execute the node logic.</summary>
    public abstract Task ExecuteAsync(INodeContext context, CancellationToken cancellationToken = default);

    /// <summary>Optional one-time setup before a run starts. Default no-op.</summary>
    public virtual Task Initialize(INodeContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>Optional run body; runs after <see cref="Initialize"/>. Default no-op.</summary>
    public virtual Task Run(INodeContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;

    /// <summary>Optional teardown after a run ends (or is cancelled). Default no-op.</summary>
    public virtual Task Stop(INodeContext context, CancellationToken cancellationToken = default)
        => Task.CompletedTask;
}

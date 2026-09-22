namespace AgentFlow.Contracts;

/// <summary>
/// Node interface. Every concrete node dll implements this.
/// A node = execution logic + pins (input/output ports) + parameter declarations.
/// The pin set of a node is fixed at development time, so pins are exposed as
/// read-only input/output lists (no dynamic add/remove).
/// </summary>
public interface INode
{
    /// <summary>Node type id (must match NodeAttribute.TypeId).</summary>
    string TypeId { get; }

    /// <summary>
    /// Unique id of this node <b>instance</b>. Unlike <see cref="TypeId"/>, which identifies
    /// the node <i>type</i> statically (and matches <see cref="NodeAttribute.TypeId"/>), the
    /// instance id is an <see cref="int"/> that identifies a single runtime node instance and
    /// can be changed at runtime. Because instances are added and removed dynamically,
    /// AgentFlow.Core (see <c>PluginLoader</c>) reuses freed ids so the set stays compact.
    /// </summary>
    int InstanceId { get; set; }

    /// <summary>Display name in the UI.</summary>
    string DisplayName { get; }

    /// <summary>All input pin definitions (the left-hand ports).</summary>
    IReadOnlyList<PinDefinition> InputPins { get; }

    /// <summary>All output pin definitions (the right-hand ports).</summary>
    IReadOnlyList<PinDefinition> OutputPins { get; }

    /// <summary>Parameter declarations (used to auto-generate the property panel).</summary>
    IReadOnlyList<ParameterDefinition> Parameters { get; }

    /// <summary>Apply parameters (from JSON deserialization / property panel).</summary>
    void Configure(IReadOnlyDictionary<string, object?> parameters);

    /// <summary>
    /// Called by an input pin when data arrives from an upstream output pin.
    /// <paramref name="pin"/> is the input pin definition that owns the received value,
    /// so a node with several inputs can tell which one fired and react immediately.
    /// The default implementation is a no-op for nodes that only read inputs
    /// inside <see cref="ExecuteAsync"/>.
    /// </summary>
    void Receive(INodeContext context, PinDefinition pin, object? value) { }

    /// <summary>Execute the node logic.</summary>
    Task ExecuteAsync(INodeContext context, CancellationToken cancellationToken = default);
}

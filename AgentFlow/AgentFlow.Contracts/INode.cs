namespace AgentFlow.Contracts;

/// <summary>
/// Node interface. Every concrete node dll implements this.
/// A node = execution logic + pins (input/output ports) + parameter declarations.
/// </summary>
public interface INode
{
    /// <summary>Node type id (must match NodeAttribute.TypeId).</summary>
    string TypeId { get; }

    /// <summary>Display name in the UI.</summary>
    string DisplayName { get; }

    /// <summary>All pin definitions (used for connection validation and UI visualization).</summary>
    IReadOnlyList<PinDefinition> Pins { get; }

    /// <summary>Parameter declarations (used to auto-generate the property panel).</summary>
    IReadOnlyList<ParameterDefinition> Parameters { get; }

    /// <summary>Apply parameters (from JSON deserialization / property panel).</summary>
    void Configure(IReadOnlyDictionary<string, object?> parameters);

    /// <summary>Execute the node logic.</summary>
    Task ExecuteAsync(INodeContext context, CancellationToken cancellationToken = default);
}

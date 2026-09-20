using AgentFlow.Contracts;

namespace AgentFlow.Core;

/// <summary>Node metadata discovered via reflection (used by the palette and property panel).</summary>
public sealed record NodeDescriptor(
    string TypeId,
    string DisplayName,
    string Category,
    Type NodeType,
    IReadOnlyList<PinDefinition> Pins,
    IReadOnlyList<ParameterDefinition> Parameters);

/// <summary>Registry mapping TypeId to node types.</summary>
public sealed class NodeRegistry
{
    private readonly Dictionary<string, NodeDescriptor> _nodes = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<NodeDescriptor> Nodes => _nodes.Values;

    public void Register(NodeDescriptor descriptor) => _nodes[descriptor.TypeId] = descriptor;

    public NodeDescriptor Get(string typeId) =>
        _nodes.TryGetValue(typeId, out var d)
            ? d
            : throw new KeyNotFoundException($"Unregistered node type: {typeId}");

    public bool Contains(string typeId) => _nodes.ContainsKey(typeId);

    /// <summary>Create a node instance.</summary>
    public INode CreateInstance(string typeId) =>
        (INode)(Activator.CreateInstance(Get(typeId).NodeType)
            ?? throw new InvalidOperationException($"Cannot instantiate node: {typeId}"));
}

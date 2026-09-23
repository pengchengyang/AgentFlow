using AgentFlow.Contracts;

namespace AgentFlow.Core;

/// <summary>UI-facing pin metadata (keeps the editor independent from the contracts assembly).</summary>
public enum PinDirection
{
    Input,
    Output
}

/// <summary>UI-facing pin descriptor, mirroring the contract pin metadata.</summary>
public sealed record PinDescriptor(
    string Name,
    Type DataType,
    PinDirection Direction,
    bool Required = true);

/// <summary>UI-facing parameter descriptor, mirroring the contract parameter metadata.</summary>
public sealed record ParameterDescriptor(
    string Name,
    Type DataType,
    string DisplayName,
    object? DefaultValue = null,
    string? Description = null);

/// <summary>Node metadata discovered via reflection (used by the palette and property panel).</summary>
public sealed class NodeDescriptor
{
    public string TypeId { get; }
    public string DisplayName { get; }
    public string Category { get; }
    public Type NodeType { get; }

    /// <summary>UI-facing input pins (left-hand ports), no dependency on <see cref="AgentFlow.Contracts"/>.</summary>
    public IReadOnlyList<PinDescriptor> InputPins { get; }

    /// <summary>UI-facing output pins (right-hand ports), no dependency on <see cref="AgentFlow.Contracts"/>.</summary>
    public IReadOnlyList<PinDescriptor> OutputPins { get; }

    /// <summary>UI-facing pins in declaration order (inputs then outputs).</summary>
    public IReadOnlyList<PinDescriptor> Pins { get; }

    /// <summary>UI-facing parameters (no dependency on <see cref="AgentFlow.Contracts"/>).</summary>
    public IReadOnlyList<ParameterDescriptor> Parameters { get; }

    /// <summary>Original contract pin definitions used to build runtime pins with their hooks.</summary>
    internal IReadOnlyList<BasePin> RuntimePins { get; }

    public NodeDescriptor(
        string typeId,
        string displayName,
        string category,
        Type nodeType,
        IReadOnlyList<BasePin> inputPins,
        IReadOnlyList<BasePin> outputPins,
        IReadOnlyList<ParameterDefinition> parameters)
    {
        TypeId = typeId;
        DisplayName = displayName;
        Category = category;
        NodeType = nodeType;

        var runtime = inputPins.Concat(outputPins).ToList();
        RuntimePins = runtime;
        InputPins = inputPins
            .Select(p => new PinDescriptor(p.Name, p.DataType, (AgentFlow.Core.PinDirection)p.Direction, p.Required))
            .ToList();
        OutputPins = outputPins
            .Select(p => new PinDescriptor(p.Name, p.DataType, (AgentFlow.Core.PinDirection)p.Direction, p.Required))
            .ToList();
        Pins = runtime
            .Select(p => new PinDescriptor(p.Name, p.DataType, (AgentFlow.Core.PinDirection)p.Direction, p.Required))
            .ToList();
        Parameters = parameters
            .Select(p => new ParameterDescriptor(p.Name, p.DataType, p.DisplayName, p.DefaultValue, p.Description))
            .ToList();
    }
}

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
    public BaseNode CreateInstance(string typeId) =>
        (BaseNode)(Activator.CreateInstance(Get(typeId).NodeType)
            ?? throw new InvalidOperationException($"Cannot instantiate node: {typeId}"));
}

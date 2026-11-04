// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeRegistry.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

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
    DataType DataType,
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
    public string Name { get; }
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

    public NodeDescriptor(
        string name,
        string displayName,
        string category,
        Type nodeType,
        IReadOnlyList<BasePin> inputPins,
        IReadOnlyList<BasePin> outputPins,
        IReadOnlyList<ParameterDefinition> parameters)
    {
        Name = name;
        DisplayName = displayName;
        Category = category;
        NodeType = nodeType;

        var runtime = inputPins.Concat(outputPins).ToList();
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

/// <summary>Registry mapping Name (node type id) to node types.</summary>
public sealed class NodeRegistry
{
    private readonly Dictionary<string, NodeDescriptor> _nodes = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<NodeDescriptor> Nodes => _nodes.Values;

    public void Register(NodeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        if (!_nodes.TryAdd(descriptor.Name, descriptor))
            throw new InvalidOperationException(
                $"Duplicate node type '{descriptor.Name}'. Each node type name must be globally unique.");
    }

    public NodeDescriptor Get(string name) =>
        _nodes.TryGetValue(name, out var d)
            ? d
            : throw new KeyNotFoundException($"Unregistered node type: {name}");

    public bool Contains(string name) => _nodes.ContainsKey(name);

    /// <summary>Remove every registered type (used when unloading plugins).</summary>
    public void Clear() => _nodes.Clear();

    /// <summary>
    /// Create a node instance and make sure it owns every pin declared by its descriptor. The
    /// plugin loader does this defensively for GUI-created instances; headless callers (CLI /
    /// WorkflowEngine) go through the registry directly and must get the same pin set.
    /// </summary>
    public BaseNode CreateInstance(string name)
    {
        var descriptor = Get(name);
        var node = (BaseNode)(Activator.CreateInstance(descriptor.NodeType)
            ?? throw new InvalidOperationException($"Cannot instantiate node: {name}"));
        if (!string.Equals(node.Name, descriptor.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Node type '{descriptor.Name}' instantiated an object whose Name is '{node.Name}'.");

        foreach (var pin in descriptor.InputPins)
        {
            if (!node.InputPins.Any(p => p.Name == pin.Name))
                node.AddInputPin(new BasePin(
                    pin.Name, pin.DataType,
                    (AgentFlow.Contracts.PinDirection)pin.Direction, pin.Required));
        }
        foreach (var pin in descriptor.OutputPins)
        {
            if (!node.OutputPins.Any(p => p.Name == pin.Name))
                node.AddOutputPin(new BasePin(
                    pin.Name, pin.DataType,
                    (AgentFlow.Contracts.PinDirection)pin.Direction, pin.Required));
        }
        foreach (var pin in node.InputPins) pin.Owner = node;
        foreach (var pin in node.OutputPins) pin.Owner = node;
        return node;
    }
}



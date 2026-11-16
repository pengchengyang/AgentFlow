// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeRegistry.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
using AgentFlow.Core.ExternalNodes;
using BroadcastKit;
using Microsoft.Extensions.Logging;

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
    string? Description = null,
    ParameterEditorKind Editor = ParameterEditorKind.Auto,
    IReadOnlyList<string>? Options = null);

/// <summary>Package-level metadata used by the palette and node property panel.</summary>
public sealed record NodePackageMetadata(
    string PackageId,
    string Version,
    string? Publisher = null,
    string? Author = null,
    string? AuthorEmail = null,
    string? Copyright = null,
    string? License = null,
    string? LicenseFile = null,
    string? Homepage = null,
    string? Repository = null);
/// <summary>Node metadata discovered via reflection (used by the palette and property panel).</summary>
public sealed class NodeDescriptor
{
    public string Name { get; }
    public string DisplayName { get; }
    public string Category { get; }
    public Type NodeType { get; }

    /// <summary>Optional factory used by dynamic node types such as external workers.</summary>
    public Func<BaseNode>? Factory { get; }

    /// <summary>Package metadata for external node types; null for internal C# plugins.</summary>
    public NodePackageMetadata? PackageMetadata { get; }

    /// <summary>Validated external package definition; null for internal C# plugins.</summary>
    public ExternalNodeDefinition? ExternalDefinition { get; }

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
        IReadOnlyList<ParameterDefinition> parameters,
        Func<BaseNode>? factory = null,
        NodePackageMetadata? packageMetadata = null,
        ExternalNodeDefinition? externalDefinition = null)
    {
        Name = name;
        DisplayName = displayName;
        Category = category;
        NodeType = nodeType;
        Factory = factory;
        PackageMetadata = packageMetadata;
        ExternalDefinition = externalDefinition;

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
            .Select(p => new ParameterDescriptor(
                p.Name, p.DataType, p.DisplayName, p.DefaultValue, p.Description,
                p.Editor, p.Options))
            .ToList();
    }
}

/// <summary>Registry mapping Name (node type id) to node types.</summary>
public sealed class NodeRegistry
{
    private readonly Dictionary<string, NodeDescriptor> _nodes = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyCollection<NodeDescriptor> Nodes => _nodes.Values;

    /// <summary>Create a node from its registered factory, or by reflection when no factory is provided.</summary>
    public BaseNode CreateInstance(NodeDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        var node = descriptor.Factory is { } factory
            ? factory()
            : (BaseNode)(Activator.CreateInstance(descriptor.NodeType)
                ?? throw new InvalidOperationException($"Cannot instantiate node: {descriptor.Name}"));
        if (!string.Equals(node.Name, descriptor.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Node type '{descriptor.Name}' instantiated an object whose Name is '{node.Name}'.");
        return node;
    }

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

    /// <summary>
    /// Build a per-workflow registry view. Internal node descriptors are reused, but external
    /// descriptors get a fresh <see cref="ExternalNodeRuntime"/> so each concurrent workflow owns
    /// independent Worker processes and node-instance routing.
    /// </summary>
    public NodeRegistryScope CreateWorkflowScope(ILogger logger, ITopicBus? topicBus = null)
    {
        ArgumentNullException.ThrowIfNull(logger);
        var scoped = new NodeRegistry();
        ExternalNodeRuntime? runtime = null;

        foreach (var descriptor in _nodes.Values)
        {
            if (descriptor.ExternalDefinition is not { } definition)
            {
                scoped.Register(descriptor);
                continue;
            }

            runtime ??= new ExternalNodeRuntime(logger, topicBus);
            var capturedRuntime = runtime;
            scoped.Register(new NodeDescriptor(
                descriptor.Name,
                descriptor.DisplayName,
                descriptor.Category,
                descriptor.NodeType,
                definition.InputPins,
                definition.OutputPins,
                definition.Parameters,
                () => new ExternalNodeProxy(definition, capturedRuntime),
                descriptor.PackageMetadata,
                definition));
        }

        return new NodeRegistryScope(scoped, runtime);
    }

    /// <summary>Remove every registered type (used when unloading plugins).</summary>
    public void Clear() => _nodes.Clear();

    /// <summary>
    /// Replace the registry contents only after a complete staged load has finished. The
    /// caller must supply a duplicate-free set so a failed plugin scan cannot expose a
    /// half-populated node catalog.
    /// </summary>
    public void ReplaceWith(IEnumerable<NodeDescriptor> descriptors)
    {
        ArgumentNullException.ThrowIfNull(descriptors);
        var staged = new Dictionary<string, NodeDescriptor>(StringComparer.OrdinalIgnoreCase);
        foreach (var descriptor in descriptors)
        {
            ArgumentNullException.ThrowIfNull(descriptor);
            if (!staged.TryAdd(descriptor.Name, descriptor))
                throw new InvalidOperationException(
                    $"Duplicate node type '{descriptor.Name}' in staged plugin registry.");
        }

        _nodes.Clear();
        foreach (var pair in staged)
            _nodes.Add(pair.Key, pair.Value);
    }

    /// <summary>
    /// Create a node instance and make sure it owns every pin declared by its descriptor. The
    /// plugin loader does this defensively for GUI-created instances; headless callers (CLI /
    /// WorkflowEngine) go through the registry directly and must get the same pin set.
    /// </summary>
    public BaseNode CreateInstance(string name)
    {
        var descriptor = Get(name);
        var node = CreateInstance(descriptor);
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

/// <summary>Owns a per-workflow node registry and its isolated external Worker runtime.</summary>
public sealed class NodeRegistryScope : IAsyncDisposable
{
    private ExternalNodeRuntime? _runtime;

    internal NodeRegistryScope(NodeRegistry registry, ExternalNodeRuntime? runtime)
    {
        Registry = registry;
        _runtime = runtime;
    }

    public NodeRegistry Registry { get; }

    public async ValueTask DisposeAsync()
    {
        var runtime = Interlocked.Exchange(ref _runtime, null);
        if (runtime is not null)
            await runtime.DisposeAsync().ConfigureAwait(false);
    }
}

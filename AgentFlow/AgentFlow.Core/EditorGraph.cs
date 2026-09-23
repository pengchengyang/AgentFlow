// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="EditorGraph.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
using ContractPinDirection = AgentFlow.Contracts.PinDirection;
using Microsoft.Extensions.Logging;

namespace AgentFlow.Core;

/// <summary>
/// An editor-time graph node: holds a <see cref="BaseNode"/> instance and all of
/// its runtime pins (inputs / outputs). Wiring is maintained directly on
/// <see cref="RuntimeOutputPin.Targets"/> — the output pin keeps references to the
/// connected input pins, and data flows through Send → Receive.
/// </summary>
public sealed class EditorNode
{
    public string Id { get; }
    public string TypeId { get; }
    public string DisplayName { get; }
    public string Category { get; }

    /// <summary>User-editable instance name (persisted to JSON).</summary>
    public string? Name { get; set; }

    /// <summary>Start-up priority, mirroring ALC's "Startup Priority".</summary>
    public int Priority { get; set; }

    public double X { get; set; }
    public double Y { get; set; }

    /// <summary>The runtime node instance (a contract implementation).</summary>
    public BaseNode RuntimeNode { get; }

    /// <summary>Current parameter values owned by the editor graph (persisted to JSON).</summary>
    public Dictionary<string, object?> Parameters { get; set; } = new();

    /// <summary>All input pins, indexed by name.</summary>
    public IReadOnlyDictionary<string, RuntimeInputPin> Inputs { get; }

    /// <summary>All output pins, indexed by name.</summary>
    public IReadOnlyDictionary<string, RuntimeOutputPin> Outputs { get; }

    public EditorNode(
        string id,
        NodeDescriptor descriptor,
        BaseNode runtimeNode,
        double x,
        double y,
        string? name = null,
        int priority = 0,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        Id = id;
        TypeId = descriptor.TypeId;
        DisplayName = descriptor.DisplayName;
        Category = descriptor.Category;
        Name = name;
        Priority = priority;
        X = x;
        Y = y;
        RuntimeNode = runtimeNode;
        Parameters = parameters is null ? new() : new Dictionary<string, object?>(parameters);

        var inputs = new Dictionary<string, RuntimeInputPin>(StringComparer.Ordinal);
        var outputs = new Dictionary<string, RuntimeOutputPin>(StringComparer.Ordinal);
        foreach (var pin in descriptor.RuntimePins)
        {
            if (pin.Direction == ContractPinDirection.Input)
                inputs[pin.Name] = new RuntimeInputPin(pin, runtimeNode);
            else
                outputs[pin.Name] = new RuntimeOutputPin(pin, runtimeNode);
        }
        Inputs = inputs;
        Outputs = outputs;
    }
}

/// <summary>An editor-time connection: a wire from an output pin to an input pin.</summary>
public sealed class EditorConnection
{
    public EditorNode FromNode { get; }
    public string FromPin { get; }
    public EditorNode ToNode { get; }
    public string ToPin { get; }

    public EditorConnection(EditorNode fromNode, string fromPin, EditorNode toNode, string toPin)
    {
        FromNode = fromNode;
        FromPin = fromPin;
        ToNode = toNode;
        ToPin = toPin;
    }

    public RuntimeOutputPin SourcePin => FromNode.Outputs[FromPin];
    public RuntimeInputPin TargetPin => ToNode.Inputs[ToPin];
}

/// <summary>
/// Editor-time graph manager: the GUI layer only calls methods here to add/remove
/// nodes, wires, save and load; it does not manage pin references itself. Once a
/// connection is established, the output pin directly holds references to the input
/// pins, and Send/Receive data flows through those references at runtime.
/// </summary>
public sealed class EditorGraph
{
    private readonly NodeRegistry _registry;
    private readonly PluginLoader _pluginLoader;
    private readonly ILogger _logger;
    private readonly IGuiBridge _gui;

    public List<EditorNode> Nodes { get; } = new();
    public List<EditorConnection> Connections { get; } = new();

    /// <summary>Raised on connect / disconnect / node add-remove / property change so the GUI can refresh visuals such as IsConnected.</summary>
    public event Action? GraphChanged;

    public EditorGraph(NodeRegistry registry, ILoggerFactory loggerFactory, PluginLoader pluginLoader, IGuiBridge gui)
    {
        _registry = registry;
        _pluginLoader = pluginLoader;
        _logger = loggerFactory.CreateLogger(nameof(EditorGraph));
        _gui = gui;
    }

    /// <summary>Create an editor node instance from a node type. <paramref name="id"/> is preserved so IDs stay stable on load.</summary>
    public EditorNode AddNode(
        BaseNode instance,
        double x,
        double y,
        IReadOnlyDictionary<string, object?>? parameters = null,
        string? id = null,
        string? name = null,
        int priority = 0)
    {
        var descriptor = _registry.Get(instance.TypeId);
        if (parameters is not null && parameters.Count > 0)
            instance.Configure(parameters);

        var node = new EditorNode(
            id ?? Guid.NewGuid().ToString("N")[..8],
            descriptor,
            instance,
            x,
            y,
            name,
            priority,
            parameters);
        Nodes.Add(node);
        _logger.LogInformation("EditorGraph: added node {Id} ({TypeId})", node.Id, node.TypeId);
        GraphChanged?.Invoke();
        return node;
    }

    /// <summary>Establish a connection: the output pin saves a reference to the input pin.</summary>
    public void Connect(EditorNode fromNode, string fromPin, EditorNode toNode, string toPin)
    {
        if (!fromNode.Outputs.TryGetValue(fromPin, out var output))
            throw new InvalidOperationException($"Node {fromNode.TypeId} has no output pin: {fromPin}");
        if (!toNode.Inputs.TryGetValue(toPin, out var input))
            throw new InvalidOperationException($"Node {toNode.TypeId} has no input pin: {toPin}");

        if (Connections.Any(c =>
                ReferenceEquals(c.ToNode, toNode) && c.ToPin == toPin))
            throw new InvalidOperationException($"Input pin {toNode.TypeId}.{toPin} is already connected.");

        // Type check + reference (RuntimeOutputPin.Connect validates the type and adds to _targets)
        output.Connect(input);

        Connections.Add(new EditorConnection(fromNode, fromPin, toNode, toPin));
        _logger.LogDebug("EditorGraph: wired {From}.{FromPin} -> {To}.{ToPin}",
            fromNode.Id, fromPin, toNode.Id, toPin);
        GraphChanged?.Invoke();
    }

    /// <summary>Remove a connection: the output pin drops its reference to the input pin.</summary>
    public void Disconnect(EditorConnection conn)
    {
        var output = conn.SourcePin;
        var input = conn.TargetPin;

        output.Disconnect(input);

        Connections.Remove(conn);
        _logger.LogDebug("EditorGraph: unwired {From}.{FromPin} -> {To}.{ToPin}",
            conn.FromNode.Id, conn.FromPin, conn.ToNode.Id, conn.ToPin);
        GraphChanged?.Invoke();
    }

    /// <summary>Remove a node and all of its wires.</summary>
    public void RemoveNode(EditorNode node)
    {
        var related = Connections
            .Where(c => ReferenceEquals(c.FromNode, node) || ReferenceEquals(c.ToNode, node))
            .ToList();
        foreach (var c in related)
            Disconnect(c);

        Nodes.Remove(node);
        _pluginLoader.DeleteNodeInstance(node.RuntimeNode);
        _logger.LogInformation("EditorGraph: removed node {Id}", node.Id);
        GraphChanged?.Invoke();
    }

    /// <summary>Sync editor-node properties (name / priority / position / parameters) so the UI can call this before save.</summary>
    public void UpdateNode(
        EditorNode node,
        string? name,
        int priority,
        double x,
        double y,
        IReadOnlyDictionary<string, object?>? parameters = null)
    {
        node.Name = name;
        node.Priority = priority;
        node.X = x;
        node.Y = y;
        node.Parameters = parameters is null ? new() : new Dictionary<string, object?>(parameters);
        GraphChanged?.Invoke();
    }

    /// <summary>Export the editor graph to a serializable / executable workflow graph.</summary>
    public WorkflowGraph ToWorkflowGraph()
    {
        var graph = new WorkflowGraph();
        foreach (var node in Nodes)
        {
            graph.Nodes.Add(new NodeSpec
            {
                Id = node.Id,
                TypeId = node.TypeId,
                Name = node.Name,
                Priority = node.Priority,
                X = node.X,
                Y = node.Y,
                Parameters = new Dictionary<string, object?>(node.Parameters)
            });
        }

        foreach (var c in Connections)
        {
            graph.Connections.Add(new ConnectionSpec
            {
                FromNode = c.FromNode.Id,
                FromPin = c.FromPin,
                ToNode = c.ToNode.Id,
                ToPin = c.ToPin
            });
        }

        return graph;
    }

    /// <summary>Clear the whole graph (call before loading a new workflow).</summary>
    public void Clear()
    {
        foreach (var conn in Connections.ToList())
            conn.SourcePin.Disconnect(conn.TargetPin);

        Connections.Clear();
        Nodes.Clear();
        // Let the PluginLoader container be the single source of node instances: clear them here too.
        _pluginLoader.ClearInstances();
        GraphChanged?.Invoke();
    }

    /// <summary>
    /// Execute an editor node once and push data downstream along the existing wires
    /// (the context-menu "Send"). Uses the node's wired runtime pins:
    /// <see cref="INodeContext.GetInput{T}"/> reads the latest value of an input pin,
    /// and <see cref="INodeContext.SetOutput"/> calls the output pin's Send to push
    /// data to downstream input pins.
    /// </summary>
    public async Task SendAsync(EditorNode node, CancellationToken ct = default)
    {
        if (node is null) throw new ArgumentNullException(nameof(node));
        var ctx = new EditorNodeContext(node, _logger, _gui);
        await node.RuntimeNode.ExecuteAsync(ctx, ct);
        _logger.LogInformation("EditorGraph: sent node {Id} ({TypeId})", node.Id, node.TypeId);
    }

    /// <summary>The node context used by editor-time Send: bound to this node's wired runtime pins.</summary>
    private sealed class EditorNodeContext : INodeContext
    {
        private readonly EditorNode _node;
        public ILogger Logger { get; }
        public IGuiBridge Gui { get; }

        public EditorNodeContext(EditorNode node, ILogger logger, IGuiBridge gui)
        {
            _node = node;
            Logger = logger;
            Gui = gui;
        }

        public T? GetInput<T>(string pinName) =>
            _node.Inputs.TryGetValue(pinName, out var pin) && pin.Value is T t ? t : default;

        public void SetOutput(string pinName, object? value)
        {
            if (_node.Outputs.TryGetValue(pinName, out var pin))
                pin.Send(value);
        }
    }
}

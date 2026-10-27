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
    public string Name { get; }
    public string Category { get; }

    /// <summary>User-editable display name (persisted to JSON).</summary>
    public string? DisplayName { get; set; }

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
        Name = descriptor.Name;
        Category = descriptor.Category;
        DisplayName = name;
        Priority = priority;
        X = x;
        Y = y;
        RuntimeNode = runtimeNode;
        Parameters = parameters is null ? new() : new Dictionary<string, object?>(parameters);

        var inputs = new Dictionary<string, RuntimeInputPin>(StringComparer.Ordinal);
        var outputs = new Dictionary<string, RuntimeOutputPin>(StringComparer.Ordinal);

        // Use the node instance's own pins (created by its constructor) rather than the
        // shared probe pins, so every node instance keeps unique pin ids/values.
        foreach (var pin in runtimeNode.InputPins)
            inputs[pin.Name] = new RuntimeInputPin(pin, runtimeNode);
        foreach (var pin in runtimeNode.OutputPins)
            outputs[pin.Name] = new RuntimeOutputPin(pin, runtimeNode);

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

    /// <summary>This document's pin-id allocator: ids only need to be unique inside this document.</summary>
    private readonly PinIdAllocator _pinIds = new();

    public List<EditorNode> Nodes { get; } = new();
    public List<EditorConnection> Connections { get; } = new();

    /// <summary>Raised on connect / disconnect / node add-remove / property change so the GUI can refresh visuals such as IsConnected.</summary>
    public event Action? GraphChanged;

    public EditorGraph(NodeRegistry registry, ILoggerFactory loggerFactory, PluginLoader pluginLoader)
    {
        _registry = registry;
        _pluginLoader = pluginLoader;
        _logger = loggerFactory.CreateLogger(nameof(EditorGraph));
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
        var descriptor = _registry.Get(instance.Name);
        if (parameters is not null && parameters.Count > 0)
            instance.Configure(parameters);
        if (!string.IsNullOrEmpty(name))
            instance.DisplayName = name;

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
        _logger.LogTrace("EditorGraph: added node {Id} ({Name})", node.Id, node.Name);
        GraphChanged?.Invoke();
        return node;
    }

    /// <summary>Establish a connection: the output pin saves a reference to the input pin.</summary>
    public void Connect(EditorNode fromNode, string fromPin, EditorNode toNode, string toPin)
    {
        if (!fromNode.Outputs.TryGetValue(fromPin, out var output))
            throw new InvalidOperationException($"Node {fromNode.Name} has no output pin: {fromPin}");
        if (!toNode.Inputs.TryGetValue(toPin, out var input))
            throw new InvalidOperationException($"Node {toNode.Name} has no input pin: {toPin}");

        // One-to-one rule: if either pin is already connected, simply don't connect (no exception).
        if (Connections.Any(c =>
                ReferenceEquals(c.ToNode, toNode) && c.ToPin == toPin))
            return;
        if (Connections.Any(c =>
                ReferenceEquals(c.FromNode, fromNode) && c.FromPin == fromPin))
            return;

        // Type check + reference (RuntimeOutputPin.Connect validates the type and adds to _targets)
        output.Connect(input);

        Connections.Add(new EditorConnection(fromNode, fromPin, toNode, toPin));
        _logger.LogTrace("EditorGraph: wired {From}.{FromPin} -> {To}.{ToPin}",
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
        _logger.LogTrace("EditorGraph: unwired {From}.{FromPin} -> {To}.{ToPin}",
            conn.FromNode.Id, conn.FromPin, conn.ToNode.Id, conn.ToPin);
        GraphChanged?.Invoke();
    }

    /// <summary>
    /// Look up a runtime pin by its instance id (used when restoring connections from a
    /// saved pin-id based connection section).
    /// </summary>
    public (EditorNode Node, string PinName, bool IsOutput)? FindPinById(int pinId)
    {
        foreach (var node in Nodes)
        {
            foreach (var kv in node.Outputs)
                if (kv.Value.Id == pinId) return (node, kv.Key, true);
            foreach (var kv in node.Inputs)
                if (kv.Value.Id == pinId) return (node, kv.Key, false);
        }
        return null;
    }

    /// <summary>
    /// Establish a connection from saved pin instance ids. The output pin's ConnectedPin
    /// and the input pin's ConnectedPin are both set by <see cref="RuntimeOutputPin.Connect"/>.
    /// </summary>
    public bool TryConnectByPinId(int fromPinId, int toPinId)
    {
        var from = FindPinById(fromPinId);
        var to = FindPinById(toPinId);
        if (from is null || to is null) return false;
        if (!from.Value.IsOutput || to.Value.IsOutput) return false;

        Connect(from.Value.Node, from.Value.PinName, to.Value.Node, to.Value.PinName);
        return true;
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
        _logger.LogTrace("EditorGraph: removed node {Id}", node.Id);
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
        node.DisplayName = name;
        if (node.RuntimeNode is not null)
            node.RuntimeNode.DisplayName = name ?? string.Empty;
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
                Name = node.Name,
                DisplayName = node.RuntimeNode?.DisplayName ?? node.DisplayName,
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
        // A cleared graph is a new document: forget the pin ids handed out for the previous one.
        // The next save observes the ids the newly loaded document actually carries, so allocation
        // restarts from that document's ids instead of keeping on growing in this session.
        _pinIds.Reset();
        // Let the PluginLoader container be the single source of node instances: clear them here too.
        _pluginLoader.ClearInstances();
        GraphChanged?.Invoke();
        _logger.LogTrace("EditorGraph: cleared graph");
    }

    /// <summary>
    /// Assign the persistent pin ids this document will write to disk.
    /// <para>
    /// Pin ids are only needed for persistence (they link connections to pins in the saved JSON), so
    /// they are allocated lazily here — once per save, for pins that do not have an id yet — instead
    /// of at pin construction inside a process-wide pool. Ids restored from an existing document are
    /// merely observed, which keeps them stable across saves and makes loading tolerant of sparse ids.
    /// </para>
    /// </summary>
    public void EnsurePinIds()
    {
        // The pin definition owns the persisted id (logic.pins[] is serialized from the definitions),
        // while runtime pins mirror it so the connection endpoints written into the document match.
        IEnumerable<BasePin> Definitions()
        {
            foreach (var node in Nodes)
            {
                if (node.RuntimeNode is null) continue;
                foreach (var pin in node.RuntimeNode.InputPins) yield return pin;
                foreach (var pin in node.RuntimeNode.OutputPins) yield return pin;
            }
        }

        // Pass 1: take the highest already known id into account (restored documents, previous saves).
        foreach (var pin in Definitions())
            if (pin.Id != 0) _pinIds.Observe(pin.Id);

        // Pass 2: give a fresh id to every pin created since the last save.
        foreach (var pin in Definitions())
            if (pin.Id == 0) pin.AssignId(_pinIds.Allocate());

        // Keep each runtime pin in sync with the definition it wraps.
        foreach (var node in Nodes)
        {
            foreach (var pin in node.Inputs.Values) pin.AssignId(pin.Definition.Id);
            foreach (var pin in node.Outputs.Values) pin.AssignId(pin.Definition.Id);
        }
    }

    /// <summary>
    /// Build an <see cref="INodeContext"/> bound to the editor node that owns the given
    /// runtime instance. Used by <see cref="NodeInstanceManager"/> so UI commands run the
    /// actual on-canvas node instances through the same wired runtime pins.
    /// </summary>
    public INodeContext CreateContext(BaseNode runtimeNode)
    {
        var node = Nodes.FirstOrDefault(n => ReferenceEquals(n.RuntimeNode, runtimeNode))
            ?? throw new InvalidOperationException($"No editor node for instance #{runtimeNode.InstanceId}.");
        var ctx = new EditorNodeContext(node, _logger);
        foreach (var pin in node.Inputs.Values)
            pin.Context = ctx;
        foreach (var pin in node.Outputs.Values)
            pin.Context = ctx;
        return ctx;
    }

    /// <summary>The node context bound to an editor node's wired runtime pins (used by <see cref="CreateContext"/>).</summary>
    private sealed class EditorNodeContext : INodeContext
    {
        private readonly EditorNode _node;
        public ILogger Logger { get; }

        public EditorNodeContext(EditorNode node, ILogger logger)
        {
            _node = node;
            Logger = logger;
        }

        public T? GetInput<T>(string pinName) =>
            _node.Inputs.TryGetValue(pinName, out var pin) && pin.Value is T t ? t : default;

        public void SetOutput(string pinName, Sample? value)
        {
            if (_node.Outputs.TryGetValue(pinName, out var pin))
                pin.SendSample(value);
        }

        public T? GetService<T>()
        {
            // 临时:直接返回本地 Ollama 客户端
            if (typeof(T) == typeof(ILLMClient))
            {
                var client = new OllamaLlmClient();
                return (T)(object)client;
            }
            // 临时:返回一个空的工具提供者,先让模型能跑起来
            if (typeof(T) == typeof(IAgentToolProvider))
            {
                var provider = new EmptyToolProvider();
                return (T)(object)provider;
            }
            return default;
        }
    }

    /// <summary>空的工具提供者,先返回空列表,后面再接真实工具。</summary>
    private sealed class EmptyToolProvider : IAgentToolProvider
    {
        public IReadOnlyList<IAgentTool> Tools => Array.Empty<IAgentTool>();
        public IAgentTool? Find(string name) => null;
    }
}






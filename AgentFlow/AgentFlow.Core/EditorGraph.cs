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
/// An editor-time graph node: holds a <see cref="BaseNode"/> instance and exposes its own
/// contract pins (<see cref="BasePin"/>, the single pin type used for both declaration and
/// runtime wiring). Wiring is maintained directly on the output pin's target snapshot, and
/// data flows through <c>SendSample</c> → <c>ReceiveSample</c>.
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
    public IReadOnlyDictionary<string, BasePin> Inputs { get; }

    /// <summary>All output pins, indexed by name.</summary>
    public IReadOnlyDictionary<string, BasePin> Outputs { get; }

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

        var inputs = new Dictionary<string, BasePin>(StringComparer.Ordinal);
        var outputs = new Dictionary<string, BasePin>(StringComparer.Ordinal);

        // The node instance's own pins are the single pin type: the editor wires these objects
        // directly (no runtime wrapper), so a plugin that calls BasePin.SendSample reaches the
        // downstream node through the same object the engine uses.
        foreach (var pin in runtimeNode.InputPins)
            inputs[pin.Name] = pin;
        foreach (var pin in runtimeNode.OutputPins)
            outputs[pin.Name] = pin;

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

    public BasePin SourcePin => FromNode.Outputs[FromPin];
    public BasePin TargetPin => ToNode.Inputs[ToPin];
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

    /// <summary>Host-provided services resolved by node contexts (for example a broadcast facade).</summary>
    private NodeServiceRegistry? _services;

    /// <summary>This document's pin-id allocator: ids only need to be unique inside this document.</summary>
    private readonly PinIdAllocator _pinIds = new();

    /// <summary>
    /// Host services available to node contexts. Set it once before running nodes; contexts created
    /// later resolve the services through <see cref="INodeContext.GetService{T}"/>.
    /// </summary>
    public NodeServiceRegistry? Services
    {
        get => _services;
        set => _services = value;
    }

    public List<EditorNode> Nodes { get; } = new();
    public List<EditorConnection> Connections { get; } = new();

    /// <summary>Raised on connect / disconnect / node add-remove / property change so the GUI can refresh visuals such as IsConnected.</summary>
    /// <summary>Raised before a node's plugin instance is deleted/clear, so hosts can release node-scoped services.</summary>
    public event Action<BaseNode>? NodeRemoving;

    /// <summary>Raised whenever the graph structure or node state changes.</summary>
    public event Action? GraphChanged;

    public EditorGraph(NodeRegistry registry, ILoggerFactory loggerFactory, PluginLoader pluginLoader)
    {
        _registry = registry;
        _pluginLoader = pluginLoader;
        _logger = loggerFactory.CreateLogger(nameof(EditorGraph));
    }

    /// <summary>Create an editor node instance from a node type. <paramref name="id"/> is preserved so IDs stay stable on load.
    /// <paramref name="assignPinIds"/> is false while a document is still being loaded: assigning ids
    /// before every node has restored its saved ids could collide, so the loader calls
    /// <see cref="EnsurePinIds"/> once after all nodes exist.</summary>
    public EditorNode AddNode(
        BaseNode instance,
        double x,
        double y,
        IReadOnlyDictionary<string, object?>? parameters = null,
        string? id = null,
        string? name = null,
        int priority = 0,
        bool assignPinIds = true)
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
        // Assign pin ids as soon as the node exists (not only before a save): the pin id is the
        // identity of the single pin type at runtime, and an unsaved document must not leave every
        // pin on id 0 (which would make id-based lookups ambiguous). While loading a document the
        // loader disables this and calls EnsurePinIds once after every node restored its ids.
        if (assignPinIds)
            EnsurePinIds();
        _logger.LogTrace("EditorGraph: added node {Id} ({Name})", node.Id, node.Name);
        GraphChanged?.Invoke();
        return node;
    }

    /// <summary>Establish a connection: the output pin saves a reference to the input pin.</summary>
    public bool Connect(EditorNode fromNode, string fromPin, EditorNode toNode, string toPin)
    {
        if (!fromNode.Outputs.TryGetValue(fromPin, out var output))
            throw new InvalidOperationException($"Node {fromNode.Name} has no output pin: {fromPin}");
        if (!toNode.Inputs.TryGetValue(toPin, out var input))
            throw new InvalidOperationException($"Node {toNode.Name} has no input pin: {toPin}");

        // One-to-one rule: if either pin is already connected, simply don't connect (no exception).
        if (Connections.Any(c =>
                ReferenceEquals(c.ToNode, toNode) && c.ToPin == toPin))
            return false;
        if (Connections.Any(c =>
                ReferenceEquals(c.FromNode, fromNode) && c.FromPin == fromPin))
            return false;

        // Type check + reference (BasePin.ConnectTo validates the type and stores the target).
        try
        {
            output.ConnectTo(input);
        }
        catch (PinWiringException)
        {
            // The visual connection list and the core pin state can disagree (e.g. a rejected
            // restore); report "not connected" instead of throwing out of a bool API.
            return false;
        }
        if (!ReferenceEquals(input.ConnectedPin, output))
            return false;

        Connections.Add(new EditorConnection(fromNode, fromPin, toNode, toPin));
        _logger.LogTrace("EditorGraph: wired {From}.{FromPin} -> {To}.{ToPin}",
            fromNode.Id, fromPin, toNode.Id, toPin);
        GraphChanged?.Invoke();
        return true;
    }

    /// <summary>Remove a connection: the output pin drops its reference to the input pin.</summary>
    public void Disconnect(EditorConnection conn)
    {
        var output = conn.SourcePin;
        var input = conn.TargetPin;

        output.DisconnectFrom(input);

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
    /// and the input pin's ConnectedPin are both set by <see cref="BasePin.ConnectTo(BasePin)"/>.
    /// </summary>
    public bool TryConnectByPinId(int fromPinId, int toPinId)
    {
        var from = FindPinById(fromPinId);
        var to = FindPinById(toPinId);
        if (from is null || to is null) return false;
        if (!from.Value.IsOutput || to.Value.IsOutput) return false;

        return Connect(from.Value.Node, from.Value.PinName, to.Value.Node, to.Value.PinName);
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
        NodeRemoving?.Invoke(node.RuntimeNode);
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
            // The runtime instance is the source of truth for identity fields. Include the
            // built-in General parameters the engine/validation expects (InstanceId, DependsOn,
            // DisplayName, Name, Uuid); BaseNode.Parameters intentionally filters them out.
            var parameters = new Dictionary<string, object?>(node.Parameters);
            if (node.RuntimeNode is { } runtime)
            {
                parameters["Name"] = runtime.Name;
                parameters["Uuid"] = runtime.Uuid;
                parameters["InstanceId"] = runtime.InstanceId;
                parameters["DisplayName"] = runtime.DisplayName ?? "";
                parameters["DependsOn"] = runtime.DependsOn;
            }

            graph.Nodes.Add(new NodeSpec
            {
                Id = node.Id,
                Name = node.Name,
                DisplayName = node.RuntimeNode?.DisplayName ?? node.DisplayName,
                Priority = node.Priority,
                X = node.X,
                Y = node.Y,
                Parameters = parameters
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
            conn.SourcePin.DisconnectFrom(conn.TargetPin);

        foreach (var node in Nodes.ToList())
            NodeRemoving?.Invoke(node.RuntimeNode);

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
    /// Pin ids are needed for persistence (they link connections to pins in the saved JSON) and
    /// are also now the identity of the single pin type used at runtime, so they are assigned
    /// when a node is added to the graph as well as right before saving. Pins that already carry
    /// an id (restored documents / previous saves) are merely observed, which keeps them stable.
    /// </para>
    /// </summary>
    public void EnsurePinIds()
    {
        // The node instance's own pins (the only pin type) own the persisted id.
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
        // A duplicate saved id would make FindPinById ambiguous, so reject it explicitly.
        var seen = new HashSet<int>();
        foreach (var pin in Definitions())
        {
            if (pin.Id == 0) continue;
            if (!seen.Add(pin.Id))
                throw new InvalidOperationException(
                    $"Duplicate pin id {pin.Id} in the workflow document; every pin id must be unique.");
            _pinIds.Observe(pin.Id);
        }

        // Pass 2: give a fresh id to every pin created since the last assignment.
        foreach (var pin in Definitions())
            if (pin.Id == 0) pin.AssignId(_pinIds.Allocate());
    }

    /// <summary>Build an <see cref="INodeContext"/> bound to the editor node that owns the given
    /// runtime instance. Used by <see cref="NodeInstanceManager"/> so UI commands run the
    /// actual on-canvas node instances through the same wired pins.</summary>
    public INodeContext CreateContext(BaseNode runtimeNode)
    {
        var node = Nodes.FirstOrDefault(n => ReferenceEquals(n.RuntimeNode, runtimeNode))
            ?? throw new InvalidOperationException($"No editor node for instance #{runtimeNode.InstanceId}.");
        var ctx = new EditorNodeContext(node, _logger, _services);
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
        private readonly NodeServiceRegistry? _services;
        public ILogger Logger { get; }

        public EditorNodeContext(EditorNode node, ILogger logger, NodeServiceRegistry? services)
        {
            _node = node;
            _services = services;
            Logger = logger;
        }

        public T? GetInput<T>(string pinName) =>
            _node.Inputs.TryGetValue(pinName, out var pin) && pin.Value is T t ? t : default;

        public bool HasInput(string pinName) =>
            _node.Inputs.TryGetValue(pinName, out var pin) && pin.HasValue;

        public bool TryGetInput<T>(string pinName, out T? value)
        {
            if (_node.Inputs.TryGetValue(pinName, out var pin) && pin.Value is T typed)
            {
                value = typed;
                return true;
            }

            value = default;
            return false;
        }

        public Task<Sample?> WaitForInputAsync(
            string pinName,
            TimeSpan? timeout = null,
            CancellationToken cancellationToken = default) =>
            _node.Inputs.TryGetValue(pinName, out var pin)
                ? pin.WaitForValueAsync(timeout, cancellationToken)
                : throw new InvalidOperationException($"Node '{_node.Id}' has no input pin: {pinName}");

        public void SetOutput(string pinName, Sample? value)
        {
            if (_node.Outputs.TryGetValue(pinName, out var pin))
                pin.SendSample(value);
        }

        public T? GetService<T>()
        {
            // 临时:本地 Ollama 工厂。节点通过它按自己的 Model/Endpoint 参数创建客户端,
            // 直接取 ILLMClient 时退化为默认模型/端点(保持旧行为)。
            if (typeof(T) == typeof(ILLMClientFactory))
            {
                return (T)(object)LlmFactory;
            }
            if (typeof(T) == typeof(ILLMClient))
            {
                return (T)(object)LlmFactory.Create();
            }
            // 临时:返回一个空的工具提供者,先让模型能跑起来
            if (typeof(T) == typeof(IAgentToolProvider))
            {
                return (T)(object)EmptyAgentToolProvider.Instance;
            }
            var service = _services?.Get<T>(_node.RuntimeNode);
            return service is T typed ? typed : default;
        }

        private static readonly OllamaClientFactory LlmFactory = new();
    }
}








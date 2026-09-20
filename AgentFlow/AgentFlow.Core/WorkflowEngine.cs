using System.Text.Json;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace AgentFlow.Core;

/// <summary>
/// Workflow execution engine:
/// 1. Instantiate nodes and their runtime pins.
/// 2. Wire the graph: each output pin holds references to its connected input pins (Connect).
/// 3. Execute in topological order; SetOutput -> output pin.Send -> each input pin.Receive.
/// Has no UI dependency and can run headless in the CLI.
/// </summary>
public sealed class WorkflowEngine
{
    private readonly NodeRegistry _registry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly IGuiBridge _guiBridge;

    public WorkflowEngine(NodeRegistry registry, ILoggerFactory loggerFactory, IGuiBridge guiBridge)
    {
        _registry = registry;
        _loggerFactory = loggerFactory;
        _guiBridge = guiBridge;
        _logger = loggerFactory.CreateLogger(nameof(WorkflowEngine));
    }

    public async Task RunAsync(WorkflowGraph graph, CancellationToken ct = default)
    {
        // 1. Instantiate all nodes and runtime pins, build execution contexts.
        var instances = new Dictionary<string, INode>();
        var contexts = new Dictionary<string, NodeContext>();

        foreach (var spec in graph.Nodes)
        {
            var node = _registry.CreateInstance(spec.TypeId);
            node.Configure(NormalizeParameters(spec.Parameters));
            instances[spec.Id] = node;
            contexts[spec.Id] = new NodeContext(
                spec.Id, node,
                _loggerFactory.CreateLogger($"Node:{spec.Id}"), _guiBridge);
            _logger.LogInformation("Instantiated node {Id} ({TypeId})", spec.Id, spec.TypeId);
        }

        // 2. Wire the graph: output.Connect(input) -- output pins hold input pin references.
        foreach (var conn in graph.Connections)
        {
            var output = contexts[conn.FromNode].GetOutputPin(conn.FromPin);
            var input = contexts[conn.ToNode].GetInputPin(conn.ToPin);
            output.Connect(input);
            _logger.LogDebug("Wired: {From}.{FromPin} -> {To}.{ToPin}",
                conn.FromNode, conn.FromPin, conn.ToNode, conn.ToPin);
        }

        // 3. Topological sort (Kahn).
        var order = TopologicalSort(graph);
        _logger.LogInformation("Execution order: {Order}", string.Join(" -> ", order));

        // 4. Execute in order; SetOutput pushes data directly along pin references.
        foreach (var nodeId in order)
        {
            ct.ThrowIfCancellationRequested();
            var node = instances[nodeId];
            var ctx = contexts[nodeId];

            _logger.LogInformation("--- Executing node {Id} ---", nodeId);
            try
            {
                await node.ExecuteAsync(ctx, ct);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Node {Id} failed", nodeId);
                throw;
            }
        }

        _logger.LogInformation("Workflow completed");
    }

    /// <summary>JSON-deserialized parameter values are JsonElements; convert to native types.</summary>
    private static IReadOnlyDictionary<string, object?> NormalizeParameters(
        IReadOnlyDictionary<string, object?> parameters) =>
        parameters.ToDictionary(kv => kv.Key, kv => Normalize(kv.Value));

    private static object? Normalize(object? value) =>
        value is JsonElement je
            ? je.ValueKind switch
            {
                JsonValueKind.String => je.GetString(),
                JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => je.ToString()
            }
            : value;

    private static List<string> TopologicalSort(WorkflowGraph graph)
    {
        var indegree = graph.Nodes.ToDictionary(n => n.Id, _ => 0);
        foreach (var c in graph.Connections)
            indegree[c.ToNode]++;

        var queue = new Queue<string>(indegree.Where(kv => kv.Value == 0).Select(kv => kv.Key));
        var order = new List<string>();

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            order.Add(id);
            foreach (var c in graph.Connections.Where(c => c.FromNode == id))
                if (--indegree[c.ToNode] == 0)
                    queue.Enqueue(c.ToNode);
        }

        if (order.Count != graph.Nodes.Count)
            throw new InvalidOperationException("The workflow contains a cycle; cannot topologically sort");

        return order;
    }

    /// <summary>
    /// Node execution context: owns all runtime pins of one node.
    /// SetOutput -> output pin.Send (pushes to downstream Receive);
    /// GetInput -> reads the latest value received by the input pin.
    /// </summary>
    private sealed class NodeContext : INodeContext
    {
        private readonly string _nodeId;
        private readonly Dictionary<string, RuntimeInputPin> _inputs = new();
        private readonly Dictionary<string, RuntimeOutputPin> _outputs = new();

        public ILogger Logger { get; }
        public IGuiBridge Gui { get; }

        public NodeContext(string nodeId, INode node, ILogger logger, IGuiBridge gui)
        {
            _nodeId = nodeId;
            Logger = logger;
            Gui = gui;

            foreach (var pin in node.Pins)
            {
                if (pin.Direction == PinDirection.Input)
                    _inputs[pin.Name] = new RuntimeInputPin(pin);
                else
                    _outputs[pin.Name] = new RuntimeOutputPin(pin);
            }
        }

        public RuntimeInputPin GetInputPin(string name) =>
            _inputs.TryGetValue(name, out var p)
                ? p
                : throw new InvalidOperationException($"Node {_nodeId} has no input pin: {name}");

        public RuntimeOutputPin GetOutputPin(string name) =>
            _outputs.TryGetValue(name, out var p)
                ? p
                : throw new InvalidOperationException($"Node {_nodeId} has no output pin: {name}");

        public T? GetInput<T>(string pinName) =>
            _inputs.TryGetValue(pinName, out var pin) && pin.Value is T t ? t : default;

        public void SetOutput(string pinName, object? value)
        {
            var pin = GetOutputPin(pinName);
            Logger.LogDebug("Pin {Node}.{Pin} sent {Value} ({Count} downstream)",
                _nodeId, pinName, value, pin.Targets.Count);
            pin.Send(value);   // The output pin calls Receive on every connected input pin.
        }
    }
}

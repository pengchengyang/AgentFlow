using System.Text.Json;
using AgentFlow.Contracts;
using ContractPinDirection = AgentFlow.Contracts.PinDirection;
using Microsoft.Extensions.Logging;

namespace AgentFlow.Core;

/// <summary>
/// Workflow execution engine:
/// 1. Validate the graph (types, pins, required inputs, cycles, priority tie-breaks).
/// 2. Instantiate nodes and their runtime pins.
/// 3. Wire the graph: each output pin holds references to its connected input pins (Connect).
/// 4. Run the optional node lifecycle (Initialize / Start / Execute / Stop) borrowed from
///    ALC's filter-manager start-priority pattern.
/// 5. Execute in topological order; SetOutput -> output pin.Send -> each input pin.Receive.
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
        var errors = WorkflowValidation.Validate(_registry, graph);
        if (errors.Count > 0)
        {
            var joined = string.Join(Environment.NewLine, errors);
            _logger.LogError("Workflow validation failed:\n{Errors}", joined);
            throw new InvalidOperationException($"Workflow validation failed:{Environment.NewLine}{joined}");
        }

        // 1. Instantiate all nodes and runtime pins, build execution contexts.
        var instances = new Dictionary<string, INode>();
        var contexts = new Dictionary<string, NodeContext>();
        var lifecycle = new Dictionary<string, ILifecycleNode>();

        foreach (var spec in graph.Nodes)
        {
            var node = _registry.CreateInstance(spec.TypeId);
            node.Configure(NormalizeParameters(spec.Parameters));
            instances[spec.Id] = node;
            contexts[spec.Id] = new NodeContext(
                spec.Id, node,
                _loggerFactory.CreateLogger($"Node:{spec.Id}"), _guiBridge);
            if (node is ILifecycleNode lc)
                lifecycle[spec.Id] = lc;
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

        // 3. Topological sort (Kahn) with startup-priority tie-break (ALC-inspired).
        var order = WorkflowValidation.TopologicalSort(graph);
        _logger.LogInformation("Execution order: {Order}", string.Join(" -> ", order));

        // 4. Execute lifecycle + graph body in one try/finally so StopAsync always runs
        //    (reverse priority order, ALC stop pattern) even when Start/Execute throws.
        try
        {
            // 4a. Initialize lifecycle nodes before the first execute (e.g. open devices).
            foreach (var nodeId in order)
            {
                if (lifecycle.TryGetValue(nodeId, out var lc))
                {
                    ct.ThrowIfCancellationRequested();
                    _logger.LogInformation("Initializing node {Id}", nodeId);
                    await lc.InitializeAsync(contexts[nodeId], ct);
                }
            }

            // 4b. Start lifecycle nodes in the same priority order (ALC start priority).
            foreach (var nodeId in order)
            {
                if (lifecycle.TryGetValue(nodeId, out var lc))
                {
                    ct.ThrowIfCancellationRequested();
                    _logger.LogInformation("Starting node {Id}", nodeId);
                    await lc.StartAsync(contexts[nodeId], ct);
                }
            }

            // 4c+5. Execute in order; SetOutput pushes data directly along pin references.
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
        finally
        {
            // Stop lifecycle nodes in reverse priority order (ALC stop pattern).
            foreach (var nodeId in Enumerable.Reverse(order))
            {
                if (lifecycle.TryGetValue(nodeId, out var lc))
                {
                    try
                    {
                        _logger.LogInformation("Stopping node {Id}", nodeId);
                        await lc.StopAsync(contexts[nodeId], ct);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Node {Id} failed to stop", nodeId);
                    }
                }
            }
        }
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

            foreach (var pin in node.InputPins)
                _inputs[pin.Name] = new RuntimeInputPin(pin, node, this);

            foreach (var pin in node.OutputPins)
                _outputs[pin.Name] = new RuntimeOutputPin(pin, node);
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






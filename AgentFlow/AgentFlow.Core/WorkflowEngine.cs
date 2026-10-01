// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="WorkflowEngine.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using System.Text.Json;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

namespace AgentFlow.Core;

/// <summary>
/// Workflow execution engine:
/// 1. Validate the graph (types, pins, required inputs, cycles, priority tie-breaks).
/// 2. Instantiate nodes and their runtime pins.
/// 3. Wire the graph: each output pin holds references to its connected input pins (Connect).
/// 4. Execute in topological order through <see cref="NodeInstanceManager"/> — the same
///    unified lifecycle mechanism used by the AgentFlow GUI (initialize, run, always stop).
/// Has no UI dependency and can run headless in the CLI.
/// </summary>
public sealed class WorkflowEngine
{
    private readonly NodeRegistry _registry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly IGuiBridge _guiBridge;
    private WorkflowRun? _prepared;
    private WorkflowGraph? _preparedGraph;

    public WorkflowEngine(NodeRegistry registry, ILoggerFactory loggerFactory, IGuiBridge guiBridge)
    {
        _registry = registry;
        _loggerFactory = loggerFactory;
        _guiBridge = guiBridge;
        _logger = loggerFactory.CreateLogger(nameof(WorkflowEngine));
    }

    /// <summary>Initialize every node in the workflow (uses <see cref="NodeInstanceManager"/>).</summary>
    public async Task InitializeAsync(WorkflowGraph graph, CancellationToken ct = default)
    {
        var run = Prepare(graph);
        await run.Manager.InitializeAsync(run.GetContext, ct);
    }

    /// <summary>Run every node in the workflow once, in execution order (uses <see cref="NodeInstanceManager"/>).</summary>
    public async Task RunAsync(WorkflowGraph graph, CancellationToken ct = default)
    {
        var run = Prepare(graph);
        await run.Manager.RunAsync(run.GetContext, ct);
    }

    /// <summary>
    /// Start a persistent workflow: initialize every node, then run them (uses
    /// <see cref="NodeInstanceManager"/>). Does NOT stop automatically; the caller (GUI Stop
    /// button or CLI Ctrl+C) is responsible for calling <see cref="StopAsync"/>.
    /// </summary>
    public async Task StartAsync(WorkflowGraph graph, CancellationToken ct = default)
    {
        var run = Prepare(graph);
        await run.Manager.StartAsync(run.GetContext, ct);
    }


    /// <summary>Stop every node in the workflow, reverse order (uses <see cref="NodeInstanceManager"/>).</summary>
    public async Task StopAsync(WorkflowGraph graph, CancellationToken ct = default)
    {
        var run = Prepare(graph);
        await run.Manager.StopAsync(run.GetContext, ct);
    }

    /// <summary>Returns the prepared run for the given graph, preparing it once per graph instance.</summary>
    private WorkflowRun Prepare(WorkflowGraph graph)
    {
        if (_prepared is not null && ReferenceEquals(_preparedGraph, graph))
            return _prepared;

        _prepared = BuildRun(graph);
        _preparedGraph = graph;
        return _prepared;
    }

    private WorkflowRun BuildRun(WorkflowGraph graph)
    {
        var errors = WorkflowValidation.Validate(_registry, graph);
        if (errors.Count > 0)
        {
            var joined = string.Join(Environment.NewLine, errors);
            _logger.LogError("Workflow validation failed:\n{Errors}", joined);
            throw new InvalidOperationException($"Workflow validation failed:{Environment.NewLine}{joined}");
        }

        // 1. Instantiate all nodes and runtime pins, build execution contexts.
        var instances = new Dictionary<string, BaseNode>();
        var contexts = new Dictionary<string, NodeContext>();

        foreach (var spec in graph.Nodes)
        {
            var node = _registry.CreateInstance(spec.FunctionName);
            node.Configure(NormalizeParameters(spec.Parameters));
            instances[spec.Id] = node;
            contexts[spec.Id] = new NodeContext(
                spec.Id, node,
                _loggerFactory.CreateLogger($"Node:{spec.Id}"), _guiBridge);
            _logger.LogInformation("Instantiated node {Id} ({FunctionName})", spec.Id, spec.FunctionName);
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

        // 4. Build the same unified NodeInstanceManager used by the AgentFlow GUI.
        var orderedInstances = order.Select(id => instances[id]).ToList();
        var manager = new NodeInstanceManager(orderedInstances);
        var nodeIds = instances.ToDictionary(kv => kv.Value, kv => kv.Key);
        return new WorkflowRun(manager, node => contexts[nodeIds[node]]);
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

    /// <summary>Prepared graph state: the manager plus the context factory bound to its instances.</summary>
    private sealed class WorkflowRun
    {
        public WorkflowRun(NodeInstanceManager manager, Func<BaseNode, INodeContext> contextFactory)
        {
            Manager = manager;
            GetContext = contextFactory;
        }

        public NodeInstanceManager Manager { get; }

        public Func<BaseNode, INodeContext> GetContext { get; }
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

        public NodeContext(string nodeId, BaseNode node, ILogger logger, IGuiBridge gui)
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






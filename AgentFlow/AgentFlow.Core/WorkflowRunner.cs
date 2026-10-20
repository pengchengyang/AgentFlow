// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="WorkflowRunner.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------
using System.Text.Json;
using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;
namespace AgentFlow.Core;
/// <summary>
/// The single unified workflow executor, used by every host (headless CLI / Web / Desktop GUI).
/// <para>
/// It supports two ownership modes so the exact same lifecycle mechanism drives all hosts:
/// <list type="bullet">
/// <item><b>Owned</b> (<see cref="CreateOwned"/>): the runner builds the node/pin instances itself
/// from a <see cref="WorkflowGraph"/> (the JSON produced by the editor), runs them, and disposes
/// them when it is no longer needed. Used by headless / CLI / Web hosts where the runner owns
/// the instances for the duration of one workflow run.</item>
/// <item><b>Attached</b> (<see cref="CreateAttached"/>): the runner executes an externally-owned
/// set of instances (for example the on-canvas nodes of the Desktop GUI) through a caller-supplied
/// context factory, without rebuilding or disposing them, so UI bindings stay intact.</item>
/// </list>
/// </para>
/// <para>
/// An owned runner allocates a fresh <see cref="NodeIdPool"/> / <see cref="PinIdPool"/> for the
/// duration of its construction, so every workflow's ids restart from the minimum and concurrent
/// runs never share or collide. Attached runners never allocate ids.
/// </para>
/// The lifecycle (Initialize → Run → Stop in reverse) is the same in both modes.
/// </summary>
public sealed class WorkflowRunner : IDisposable
{
    private readonly ILogger _logger;
    private readonly Func<IReadOnlyList<BaseNode>> _instancesProvider;
    private readonly Func<BaseNode, INodeContext> _contextFactory;
    private readonly IReadOnlyList<BaseNode>? _fixedInstances;
    private readonly bool _ownsInstances;
    private readonly NodeIdPool? _nodePool;
    private readonly PinIdPool? _pinPool;

    private WorkflowRunner(
        IReadOnlyList<BaseNode>? fixedInstances,
        Func<IReadOnlyList<BaseNode>>? instancesProvider,
        Func<BaseNode, INodeContext> contextFactory,
        bool ownsInstances,
        NodeIdPool? nodePool,
        PinIdPool? pinPool,
        ILogger logger)
    {
        _fixedInstances = fixedInstances;
        _instancesProvider = instancesProvider!;
        _contextFactory = contextFactory;
        _ownsInstances = ownsInstances;
        _nodePool = nodePool;
        _pinPool = pinPool;
        _logger = logger;
    }

    /// <summary>
    /// Build a runner that owns its instances: it instantiates the nodes and their runtime pins
    /// from the graph (same as the editor-persisted JSON), wires the connections, then runs them.
    /// Each owned run draws ids from its own fresh pools (restarting from the minimum).
    /// Calling <see cref="Dispose"/> tears the owned instances down and returns their pin ids.
    /// </summary>
    public static WorkflowRunner CreateOwned(NodeRegistry registry, WorkflowGraph graph, ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(graph);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var logger = loggerFactory.CreateLogger(nameof(WorkflowRunner));
        var nodePool = new NodeIdPool();
        var pinPool = new PinIdPool();

        // Install the per-run pools before instantiating nodes/pins so every id this workflow
        // allocates comes from its own pool (and concurrent runs stay isolated).
        List<BaseNode> instances;
        Dictionary<BaseNode, INodeContext> contexts;
        using (PoolScope.Enter(nodePool, pinPool))
        {
            (instances, contexts) = BuildOwned(registry, graph, loggerFactory, logger);
        }

        return new WorkflowRunner(
            instances,
            instancesProvider: null,
            contextFactory: node => contexts[node],
            ownsInstances: true,
            nodePool,
            pinPool,
            logger);
    }

    /// <summary>
    /// Build a runner that executes an externally-owned instance set (e.g. the Desktop canvas
    /// nodes). The instance list is resolved lazily through <paramref name="instancesProvider"/> so
    /// nodes added / removed on the canvas are picked up on each call. Nothing is created or
    /// disposed by the runner.
    /// </summary>
    public static WorkflowRunner CreateAttached(
        Func<IReadOnlyList<BaseNode>> instancesProvider,
        Func<BaseNode, INodeContext> contextFactory,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(instancesProvider);
        ArgumentNullException.ThrowIfNull(contextFactory);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        var logger = loggerFactory.CreateLogger(nameof(WorkflowRunner));
        return new WorkflowRunner(
            fixedInstances: null,
            instancesProvider,
            contextFactory,
            ownsInstances: false,
            nodePool: null,
            pinPool: null,
            logger);
    }

    /// <summary>Every node instance currently managed by this runner (owned or attached).</summary>
    public IReadOnlyList<BaseNode> Instances => _fixedInstances ?? _instancesProvider();

    /// <summary>
    /// Whether the whole workflow is running: true only while every managed instance reports
    /// <see cref="BaseNode.Running"/> == true. Derived purely from instance state.
    /// </summary>
    public bool IsRunning => Instances.Count > 0 && Instances.All(n => n.Running);

    /// <summary>Initialize every node instance, in execution (load) order.</summary>
    public async Task InitializeAsync(CancellationToken ct = default)
    {
        foreach (var node in Instances)
        {
            ct.ThrowIfCancellationRequested();
            await node.Initialize(_contextFactory(node), ct);
        }
    }

    /// <summary>Run every node instance once, in execution order.</summary>
    public async Task RunAsync(CancellationToken ct = default)
    {
        foreach (var node in Instances)
        {
            ct.ThrowIfCancellationRequested();
            await node.Run(_contextFactory(node), ct);
        }
    }

    /// <summary>
    /// Start a persistent workflow: initialize every instance, then run them. This does NOT stop
    /// automatically; the caller (GUI Stop button or CLI Ctrl+C) is responsible for calling
    /// <see cref="StopAsync"/> so long-running nodes can keep executing in the background.
    /// </summary>
    public async Task StartAsync(CancellationToken ct = default)
    {
        await InitializeAsync(ct);
        await RunAsync(ct);
    }

    /// <summary>Stop every node instance, in reverse execution order.</summary>
    public async Task StopAsync(CancellationToken ct = default)
    {
        foreach (var node in Instances.Reverse())
        {
            ct.ThrowIfCancellationRequested();
            await node.Stop(_contextFactory(node), ct);
        }
    }

    /// <summary>
    /// Tear down an <b>owned</b> runner: return the pin ids it allocated to its own pool so they can
    /// be reused by later runs. Has no effect on attached runners (their instances belong to the
    /// caller, typically the UI, and must not be released here).
    /// </summary>
    public void Dispose()
    {
        if (!_ownsInstances || _fixedInstances is null)
            return;

        // Re-enter the run's pools so ReleaseId returns ids to the correct (per-run) pool.
        using (PoolScope.Enter(_nodePool, _pinPool))
        {
            foreach (var node in _fixedInstances)
            {
                foreach (var pin in node.InputPins)
                    pin.ReleaseId();
                foreach (var pin in node.OutputPins)
                    pin.ReleaseId();
            }
        }
    }

    // ------------------- Owned-mode construction -------------------

    /// <summary>
    /// Instantiate, configure and wire an owned run from the graph, returning the instances in
    /// topological (execution) order together with their bound contexts. Callers must have the
    /// per-run <see cref="PoolScope"/> active so created pins draw from the run's own pool.
    /// </summary>
    private static (List<BaseNode> Instances, Dictionary<BaseNode, INodeContext> Contexts) BuildOwned(
        NodeRegistry registry, WorkflowGraph graph, ILoggerFactory loggerFactory, ILogger logger)
    {
        var errors = WorkflowValidation.Validate(registry, graph);
        if (errors.Count > 0)
        {
            var joined = string.Join(Environment.NewLine, errors);
            logger.LogError("Workflow validation failed:\n{Errors}", joined);
            throw new InvalidOperationException($"Workflow validation failed:{Environment.NewLine}{joined}");
        }

        var instances = new Dictionary<string, BaseNode>();
        var contexts = new Dictionary<string, NodeContext>();

        foreach (var spec in graph.Nodes)
        {
            var node = registry.CreateInstance(spec.Name);
            node.Configure(NormalizeParameters(spec.Parameters));
            instances[spec.Id] = node;
            contexts[spec.Id] = new NodeContext(spec.Id, node, loggerFactory.CreateLogger($"Node:{spec.Id}"));
            logger.LogTrace("Instantiated node {Id} ({Name})", spec.Id, spec.Name);
        }

        foreach (var conn in graph.Connections)
        {
            var output = contexts[conn.FromNode].GetOutputPin(conn.FromPin);
            var input = contexts[conn.ToNode].GetInputPin(conn.ToPin);
            output.Connect(input);
            logger.LogTrace("Wired: {From}.{FromPin} -> {To}.{ToPin}",
                conn.FromNode, conn.FromPin, conn.ToNode, conn.ToPin);
        }

        var order = WorkflowValidation.TopologicalSort(graph);
        logger.LogTrace("Execution order: {Order}", string.Join(" -> ", order));

        var orderedInstances = order.Select(id => instances[id]).ToList();
        var nodeIds = instances.ToDictionary(kv => kv.Value, kv => kv.Key);
        var nodeContexts = orderedInstances.ToDictionary(n => n, n => (INodeContext)contexts[nodeIds[n]]);
        return (orderedInstances, nodeContexts);
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
    /// Node execution context (owned mode): owns all runtime pins of one node.
    /// SetOutput -> output pin.Send (pushes to downstream Receive);
    /// GetInput -> reads the latest value received by the input pin.
    /// </summary>
    private sealed class NodeContext : INodeContext
    {
        private readonly string _nodeId;
        private readonly Dictionary<string, RuntimeInputPin> _inputs = new();
        private readonly Dictionary<string, RuntimeOutputPin> _outputs = new();

        public ILogger Logger { get; }

        public NodeContext(string nodeId, BaseNode node, ILogger logger)
        {
            _nodeId = nodeId;
            Logger = logger;

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

        public void SetOutput(string pinName, Sample? value)
        {
            var pin = GetOutputPin(pinName);
            Logger.LogTrace("Pin {Node}.{Pin} sent {Value} ({Count} downstream)",
                _nodeId, pinName, value, pin.Targets.Count);
            pin.SendSample(value);   // The output pin calls Receive on every connected input pin.
        }

        public T? GetService<T>() => default;
    }
}

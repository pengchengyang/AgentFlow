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
    private readonly NodeServiceRegistry? _services;
    private WorkflowRun? _prepared;
    private WorkflowGraph? _preparedGraph;

    /// <summary>Whether any node in the prepared run has failed (FailFast stop or Isolate).</summary>
    public bool HasFailedNodes => _prepared?.Manager.Instances.Any(n => n.Failed) ?? false;

    /// <summary>Whether any node is still connecting/running (or failed but not yet reset).</summary>
    public bool IsActive => _prepared?.Manager.Instances.Any(n => n.State != NodeState.Idle) ?? false;

    public WorkflowEngine(
        NodeRegistry registry,
        ILoggerFactory loggerFactory,
        NodeServiceRegistry? services = null)
    {
        _registry = registry;
        _loggerFactory = loggerFactory;
        _services = services;
        _logger = loggerFactory.CreateLogger(nameof(WorkflowEngine));
    }

    /// <summary>Initialize every node in the workflow (uses <see cref="NodeInstanceManager"/>).</summary>
    public async Task InitializeAsync(WorkflowGraph graph, CancellationToken ct = default)
    {
        var run = Prepare(graph);
        await run.Manager.InitializeAsync(run.GetContext, ct);
    }

    /// <summary>
    /// Run every node in the workflow once to completion (initialize first, then run) and always
    /// stop afterwards so the nodes release their resources. Uses <see cref="NodeInstanceManager"/>.
    /// </summary>
    public async Task RunAsync(WorkflowGraph graph, CancellationToken ct = default)
    {
        var run = Prepare(graph);
        try
        {
            await run.Manager.InitializeAsync(run.GetContext, ct);
            await run.Manager.RunAsync(run.GetContext, ct);
        }
        finally
        {
            await run.Manager.StopAsync(run.GetContext, CancellationToken.None);
        }
    }

    /// <summary>
    /// Execute one complete headless lifecycle: initialize, run to completion, stop, then invoke
    /// the final shutdown callback. CLI hosts use this because the process executes one workflow
    /// pass and exits instead of remaining resident like the desktop editor.
    /// </summary>
    public async Task RunOnceAsync(WorkflowGraph graph, CancellationToken ct = default)
    {
        _logger.LogTrace("WorkflowEngine: run once ({NodeCount} nodes).", graph.Nodes.Count);
        var run = Prepare(graph);
        try
        {
            await run.Manager.InitializeAsync(run.GetContext, ct).ConfigureAwait(false);
            await run.Manager.RunAsync(run.GetContext, ct).ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await run.Manager.StopAsync(run.GetContext, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            finally
            {
                await run.Manager.ShutdownAsync(run.GetContext, CancellationToken.None)
                    .ConfigureAwait(false);
            }
        }
    }
    /// <summary>
    /// Start a persistent workflow: initialize every node, then run them (uses
    /// <see cref="NodeInstanceManager"/>). Does NOT stop automatically; the caller (desktop host Stop
    /// button or another persistent host) is responsible for calling <see cref="StopAsync"/>.
    /// </summary>
    public async Task StartAsync(WorkflowGraph graph, CancellationToken ct = default)
    {
        _logger.LogTrace("WorkflowEngine: start ({NodeCount} nodes).", graph.Nodes.Count);
        var run = Prepare(graph);
        await run.Manager.StartAsync(run.GetContext, ct);
    }


    /// <summary>Stop every node in the workflow, reverse order (uses <see cref="NodeInstanceManager"/>).</summary>
    public async Task StopAsync(WorkflowGraph graph, CancellationToken ct = default)
    {
        _logger.LogTrace("WorkflowEngine: stop.");
        WorkflowRun run;
        try
        {
            run = Prepare(graph);
        }
        catch (Exception ex)
        {
            // The graph failed validation / wiring / registration before a prepared run existed,
            // so there is nothing to stop. Do not replay the same error from the CLI's finally block.
            _logger.LogDebug("Stop skipped for an unprepared workflow: {Message}", ex.Message);
            return;
        }

        await run.Manager.StopAsync(run.GetContext, ct);
    }

    /// <summary>
    /// Invoke every node's final application-exit callback after normal Stop cleanup. The CLI
    /// calls this immediately before the process terminates; it is not part of workflow restart.
    /// </summary>
    public async Task ShutdownAsync(WorkflowGraph graph, CancellationToken ct = default)
    {
        _logger.LogTrace("WorkflowEngine: shutdown.");
        WorkflowRun run;
        try
        {
            run = Prepare(graph);
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Shutdown skipped for an unprepared workflow: {Message}", ex.Message);
            return;
        }

        await run.Manager.ShutdownAsync(run.GetContext, ct);
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
            var node = _registry.CreateInstance(spec.Name);
            node.Configure(ParameterConversion.Normalize(spec.Parameters));
            // Restore the identity fields the same way the GUI does (the built-in parameters are
            // carried inside spec.Parameters), so DependsOn ordering works in headless mode too.
            var instanceId = WorkflowValidation.ToInt(spec.Parameters.GetValueOrDefault("InstanceId"));
            if (instanceId > 0) node.InstanceId = instanceId;
            node.DependsOn = WorkflowValidation.ToInt(spec.Parameters.GetValueOrDefault("DependsOn"));
            instances[spec.Id] = node;
            contexts[spec.Id] = new NodeContext(
                spec.Id, node,
                _loggerFactory.CreateLogger($"Node:{spec.Id}"),
                _services);
            _logger.LogTrace("Instantiated node {Id} ({Name})", spec.Id, spec.Name);
        }

        // 2. Wire the graph: output.ConnectTo(input) -- output pins keep a snapshot of targets.
        foreach (var conn in graph.Connections)
        {
            var output = contexts[conn.FromNode].GetOutputPin(conn.FromPin);
            var input = contexts[conn.ToNode].GetInputPin(conn.ToPin);
            output.ConnectTo(input);
            _logger.LogTrace("Wired: {From}.{FromPin} -> {To}.{ToPin}",
                conn.FromNode, conn.FromPin, conn.ToNode, conn.ToPin);
        }

        // 3. Start order: the wire topological order (with Priority tie-break) is the base, and the
        // same DependsOn ordering the GUI uses (via PluginLoader) is applied on top. A DependsOn
        // edge therefore always wins, while otherwise-independent nodes keep their priority/wiring
        // order — previously headless mode ignored DependsOn entirely.
        var wireOrder = WorkflowValidation.TopologicalSort(graph);
        var topoInstances = wireOrder.Select(id => instances[id]).ToList();
        var order = WorkflowOrdering.SortByDependencies(topoInstances);
        _logger.LogTrace("Execution order: {Order}",
            string.Join(" -> ", order.Select(n => n.InstanceId)));

        // 4. Build the same unified NodeInstanceManager used by the AgentFlow GUI.
        var manager = new NodeInstanceManager(order, _loggerFactory.CreateLogger(nameof(NodeInstanceManager)));
        var nodeIds = instances.ToDictionary(kv => kv.Value, kv => kv.Key);
        return new WorkflowRun(manager, node => contexts[nodeIds[node]]);
    }

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
    /// Node execution context: owns the node's pins for the duration of one run.
    /// SetOutput -> output pin.Send (pushes to downstream Receive);
    /// GetInput -> reads the latest value received by the input pin.
    /// </summary>
    private sealed class NodeContext : INodeContext
    {
        private readonly string _nodeId;
        private readonly BaseNode _node;
        private readonly NodeServiceRegistry? _services;
        private readonly Dictionary<string, BasePin> _inputs = new();
        private readonly Dictionary<string, BasePin> _outputs = new();

        public ILogger Logger { get; }

        public NodeContext(
            string nodeId,
            BaseNode node,
            ILogger logger,
            NodeServiceRegistry? services)
        {
            _nodeId = nodeId;
            _node = node;
            _services = services;
            Logger = logger;

            foreach (var pin in node.InputPins)
            {
                pin.Context = this;
                _inputs[pin.Name] = pin;
            }

            foreach (var pin in node.OutputPins)
            {
                pin.Context = this;
                _outputs[pin.Name] = pin;
            }
        }

        public BasePin GetInputPin(string name) =>
            _inputs.TryGetValue(name, out var p)
                ? p
                : throw new InvalidOperationException($"Node {_nodeId} has no input pin: {name}");

        public BasePin GetOutputPin(string name) =>
            _outputs.TryGetValue(name, out var p)
                ? p
                : throw new InvalidOperationException($"Node {_nodeId} has no output pin: {name}");

        public T? GetInput<T>(string pinName) =>
            _inputs.TryGetValue(pinName, out var pin) && pin.Value is T t ? t : default;

        public bool HasInput(string pinName) =>
            _inputs.TryGetValue(pinName, out var pin) && pin.HasValue;

        public bool TryGetInput<T>(string pinName, out T? value)
        {
            if (_inputs.TryGetValue(pinName, out var pin) && pin.Value is T typed)
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
            GetInputPin(pinName).WaitForValueAsync(timeout, cancellationToken);

        public void SetOutput(string pinName, Sample? value)
        {
            var pin = GetOutputPin(pinName);
            if (pin.SendSample(value))
            {
                Logger.LogTrace("Pin {Node}.{Pin} sent {Value} ({Count} downstream)",
                    _nodeId, pinName, value, pin.TargetCount);
            }
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
            if (typeof(T) == typeof(IAgentToolProvider))
            {
                return (T)(object)EmptyAgentToolProvider.Instance;
            }
            var service = _services?.Get<T>(_node);
            return service is T typed ? typed : default;
        }

        private static readonly OllamaClientFactory LlmFactory = new();
    }
}









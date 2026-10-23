// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeInstanceManager.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;

namespace AgentFlow.Core;

/// <summary>
/// Lifecycle manager for a set of node instances.
/// <para>
/// Two usage modes:
/// <list type="bullet">
/// <item>Created with <see cref="PluginLoader"/>: manages every instance the loader currently tracks
/// (used by the AgentFlow GUI on-canvas nodes).</item>
/// <item>Created with an explicit instance list: runs exactly those instances in the given order
/// (used by <see cref="WorkflowEngine"/> for CLI / headless workflow execution).</item>
/// </list>
/// </para>
/// Provides bulk lifecycle operations: initialize, run, stop, and running-state lookup.
/// </summary>
public sealed class NodeInstanceManager
{
    private readonly PluginLoader? _pluginLoader;
    private readonly IReadOnlyList<BaseNode>? _instances;

    public NodeInstanceManager(PluginLoader pluginLoader)
    {
        _pluginLoader = pluginLoader ?? throw new ArgumentNullException(nameof(pluginLoader));
    }

    public NodeInstanceManager(IEnumerable<BaseNode> instances)
    {
        _instances = (instances ?? throw new ArgumentNullException(nameof(instances))).ToList();
    }

    /// <summary>All node instances currently managed by this manager.</summary>
    public IReadOnlyList<BaseNode> Instances => _instances ?? _pluginLoader!.Instances;

    /// <summary>Initialize every node instance, in load order.</summary>
    public async Task InitializeAsync(Func<BaseNode, INodeContext> contextFactory, CancellationToken ct = default)
    {
        foreach (var node in Instances)
            await node.Initialize(contextFactory(node), ct);
    }

    /// <summary>Run every node instance once, in load order.</summary>
    public async Task RunAsync(Func<BaseNode, INodeContext> contextFactory, CancellationToken ct = default)
    {
        foreach (var node in Instances)
        {
            ct.ThrowIfCancellationRequested();
            await node.Run(contextFactory(node), ct);
        }
    }

    /// <summary>
    /// Start a persistent workflow: initialize every instance, then run them. This does NOT stop
    /// automatically; the caller (GUI Stop button or CLI Ctrl+C) is responsible for calling
    /// <see cref="StopAsync"/> so long-running nodes can keep executing in the background.
    /// </summary>
    public async Task StartAsync(Func<BaseNode, INodeContext> contextFactory, CancellationToken ct = default)
    {
        await InitializeAsync(contextFactory, ct);
        await RunAsync(contextFactory, ct);
    }

    /// <summary>Stop every node instance, in reverse load order.</summary>
    public async Task StopAsync(Func<BaseNode, INodeContext> contextFactory, CancellationToken ct = default)
    {
        foreach (var node in Instances.Reverse())
            await node.Stop(contextFactory(node), ct);
    }
    /// <summary>Find a managed instance by its compact instance id, or null when absent.</summary>
    public BaseNode? FindInstance(int instanceId) =>
        Instances.FirstOrDefault(n => n.InstanceId == instanceId);

    /// <summary>
    /// Snapshot the commands a node exposes for the editor context menu. CanExecute is evaluated
    /// on every call so dynamic enabled/disabled state stays fresh.
    /// </summary>
    public IReadOnlyList<NodeCommandInfo> GetNodeCommands(int instanceId)
    {
        var node = FindInstance(instanceId);
        if (node is null) return Array.Empty<NodeCommandInfo>();
        return node.NodeCommands
            .OrderBy(c => c.Order)
            .Select(c => new NodeCommandInfo(
                c.Id, c.Title, c.IconKey, c.Group, c.Order,
                c.CanExecute?.Invoke() ?? true, c.RequiresConfirmation))
            .ToList();
    }

    /// <summary>
    /// Execute one node command through the Core layer. The host supplies the context factory
    /// (normally the editor graph's CreateContext) so commands use the same wired pins/logging
    /// as run/stop.
    /// </summary>
    public async Task ExecuteNodeCommandAsync(
        int instanceId,
        string commandId,
        Func<BaseNode, INodeContext> contextFactory,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        var node = FindInstance(instanceId)
            ?? throw new InvalidOperationException($"Node instance #{instanceId} was not found.");
        var context = new NodeCommandContext
        {
            Node = node,
            NodeContext = contextFactory(node),
            CancellationToken = ct,
        };
        await node.ExecuteCommandAsync(commandId, context, ct).ConfigureAwait(false);
    }
    /// <summary>Whether a node is currently executing.</summary>
    public bool IsNodeRunning(BaseNode node) => node.Running;

    /// <summary>
    /// Whether the whole workflow is running: true only while every managed node instance
    /// reports <see cref="BaseNode.Running"/> == true, and false once all of them have stopped.
    /// Derived from instance state — no separate workflow-level flag is stored.
    /// </summary>
    public bool IsRunning => Instances.Count > 0 && Instances.All(n => n.Running);
}


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

    /// <summary>Stop every node instance, in reverse load order.</summary>
    public async Task StopAsync(Func<BaseNode, INodeContext> contextFactory, CancellationToken ct = default)
    {
        foreach (var node in Instances.Reverse())
            await node.Stop(contextFactory(node), ct);
    }

    /// <summary>
    /// Run a complete workflow pass: initialize all instances, run them, then always stop
    /// them (reverse order) even when a node throws or the run is cancelled. This is the
    /// unified lifecycle used by both the AgentFlow GUI and the CLI engine.
    /// </summary>
    public async Task RunWorkflowAsync(Func<BaseNode, INodeContext> contextFactory, CancellationToken ct = default)
    {
        try
        {
            await InitializeAsync(contextFactory, ct);
            await RunAsync(contextFactory, ct);
        }
        finally
        {
            await StopAsync(contextFactory, CancellationToken.None);
        }
    }

    /// <summary>Whether a node is currently executing.</summary>
    public bool IsRunning(BaseNode node) => node.Running;
}

// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeInstanceManager.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;

namespace AgentFlow.Core;

/// <summary>
/// Minimal manager for all node instances currently loaded by <see cref="PluginLoader"/>.
/// Provides bulk lifecycle operations: initialize, run, stop, and running-state lookup.
/// </summary>
public sealed class NodeInstanceManager
{
    private readonly PluginLoader _pluginLoader;

    public NodeInstanceManager(PluginLoader pluginLoader)
    {
        _pluginLoader = pluginLoader ?? throw new ArgumentNullException(nameof(pluginLoader));
    }

    /// <summary>All node instances currently managed by <see cref="PluginLoader"/>.</summary>
    public IReadOnlyList<BaseNode> Instances => _pluginLoader.Instances;

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

    /// <summary>Whether a node is currently executing.</summary>
    public bool IsRunning(BaseNode node) => node.Running;
}
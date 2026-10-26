// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeInstanceManager.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;

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

    // Background runs launched by Launch/StartAsync, awaited by StopAsync / RunAsync.
    private readonly List<Task> _runTasks = new();
    private readonly object _runTasksGate = new();

    // One token source per start session: it links the caller's token and is cancelled both by the
    // Stop button and by the automatic stop that a failing node triggers.
    private CancellationTokenSource? _runCts;
    private int _autoStopRequested;

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
            await node.Initialize(contextFactory(node), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Launch every node instance concurrently, without waiting for them to finish. Each node is
    /// first marked <see cref="NodeState.Connecting"/> (so the editor can show "starting"), then its
    /// <see cref="BaseNode.Run"/> executes on the thread pool. Because the nodes start concurrently,
    /// a node that has to wait for a resource (e.g. a socket client retrying its connection) no
    /// longer blocks the startup of the nodes after it — the previous sequential loop did.
    /// A node whose <see cref="BaseNode.Run"/> returns normally counts as running (until the workflow
    /// is stopped), even if the node already finished its work and set <see cref="BaseNode.Running"/>
    /// back to false. A node that throws is marked <see cref="NodeState.Failed"/>, logged, and the
    /// whole workflow is stopped automatically so the other nodes release their resources.
    /// </summary>
    public void Launch(Func<BaseNode, INodeContext> contextFactory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ct.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _autoStopRequested, 0);
        _runCts?.Dispose();
        _runCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var token = _runCts.Token;
        foreach (var node in Instances)
        {
            // The token also covers the automatic stop: when an earlier node failed while this loop
            // was still launching nodes, the remaining ones are simply not started.
            if (token.IsCancellationRequested) break;
            var context = contextFactory(node);
            node.SetConnecting();
            var run = RunNodeAsync(node, context, token, contextFactory);
            lock (_runTasksGate) _runTasks.Add(run);
        }
    }

    /// <summary>
    /// Run every node instance concurrently and wait until all of them have returned (headless /
    /// finite-workflow mode). Throws when a node failed, so a CLI run reports a non-zero outcome.
    /// </summary>
    public async Task RunAsync(Func<BaseNode, INodeContext> contextFactory, CancellationToken ct = default)
    {
        Launch(contextFactory, ct);
        Task[] tasks;
        lock (_runTasksGate)
        {
            tasks = _runTasks.ToArray();
            _runTasks.Clear();
        }
        await Task.WhenAll(tasks).ConfigureAwait(false);
        if (Instances.FirstOrDefault(n => n.Failed) is { } failed)
            throw new InvalidOperationException(
                $"Node '{failed.DisplayName}' failed to run (state: {failed.State}).");
    }

    /// <summary>
    /// Start a persistent workflow: initialize every instance, then launch them concurrently.
    /// This does NOT wait for the nodes to finish; the workflow stays "running" until
    /// <see cref="StopAsync"/> (GUI Stop button, CLI Ctrl+C, or the automatic stop on failure).
    /// When initialization fails, every instance is marked <see cref="NodeState.Failed"/> so the
    /// editor shows a failed start instead of silently doing nothing.
    /// </summary>
    public async Task StartAsync(Func<BaseNode, INodeContext> contextFactory, CancellationToken ct = default)
    {
        try
        {
            await InitializeAsync(contextFactory, ct).ConfigureAwait(false);
        }
        catch
        {
            foreach (var node in Instances) node.SetFailed();
            throw;
        }
        Launch(contextFactory, ct);
    }

    /// <summary>
    /// Stop every node instance (reverse load order), wait for the launched runs to unwind, then
    /// reset every node's lifecycle state to <see cref="NodeState.Idle"/>. Waiting happens BEFORE
    /// the reset so a node that unwinds badly during teardown (e.g. it was disposed mid-connect)
    /// cannot leave a stale Failed / Running state behind in the editor.
    /// <para>
    /// <paramref name="preserveFailure"/> keeps the <see cref="NodeState.Failed"/> flag of the node(s)
    /// that failed — used by the automatic stop, so the editor still reports the failed start after
    /// the resources have been released. A user-initiated stop clears it.
    /// </para>
    /// </summary>
    public async Task StopAsync(
        Func<BaseNode, INodeContext> contextFactory,
        CancellationToken ct = default,
        bool preserveFailure = false)
    {
        // Cancel the run session first so nodes blocked on their run token (connect retries, waits)
        // observe the stop instead of continuing to run.
        _runCts?.Cancel();
        try
        {
            foreach (var node in Instances.Reverse())
                await node.Stop(contextFactory(node), ct).ConfigureAwait(false);
        }
        finally
        {
            Task[] tasks;
            lock (_runTasksGate)
            {
                tasks = _runTasks.ToArray();
                _runTasks.Clear();
            }
            if (tasks.Length > 0)
            {
                try { await Task.WhenAll(tasks).ConfigureAwait(false); }
                catch { /* node failures were already marked + logged by RunNodeAsync */ }
            }

            foreach (var node in Instances)
            {
                if (preserveFailure && node.Failed)
                {
                    node.SetFailed();   // keep it failed, but not running
                    continue;
                }
                node.ResetState();
            }

            Interlocked.Exchange(ref _autoStopRequested, 0);
            Interlocked.Exchange(ref _runCts, null)?.Dispose();
        }
    }

    /// <summary>
    /// Run one node and converge its lifecycle state:
    /// <list type="bullet">
    /// <item><see cref="BaseNode.Run"/> returned normally → <see cref="NodeState.Running"/>: the node
    /// initialized and started without errors, so it counts as running until the workflow is stopped
    /// (also for one-shot nodes that already set <see cref="BaseNode.Running"/> back to false).</item>
    /// <item>cancelled / stopped → idle (the stop reset owns the final state);</item>
    /// <item>threw → <see cref="NodeState.Failed"/> + logged, and the whole workflow is stopped
    /// automatically so the other nodes release their resources.</item>
    /// </list>
    /// Never throws, so one bad node cannot take the whole workflow (or the editor) down.
    /// </summary>
    private async Task RunNodeAsync(
        BaseNode node,
        INodeContext context,
        CancellationToken ct,
        Func<BaseNode, INodeContext> contextFactory)
    {
        try
        {
            await node.Run(context, ct).ConfigureAwait(false);
            node.SetRunning();
        }
        catch (OperationCanceledException)
        {
            node.SetIdle();
        }
        catch (Exception ex) when (ct.IsCancellationRequested)
        {
            // Teardown race (the node was disposed while it was still connecting, ...): not a failure.
            node.SetIdle();
            context.Logger.LogDebug(ex, "Node '{Node}' ended while stopping.", node.Name);
        }
        catch (Exception ex)
        {
            node.SetFailed();
            context.Logger.LogError(ex, "Node '{Node}' failed to start: {Message}", node.Name, ex.Message);
            context.Logger.LogWarning(
                "Node '{Node}' failed; stopping the workflow so every node releases its resources.",
                node.Name);
            RequestAutoStop(contextFactory);
        }
    }

    /// <summary>
    /// Stop the whole workflow after a node failed. Runs on its own task because this is requested
    /// from inside one of the run tasks that <see cref="StopAsync"/> has to await.
    /// </summary>
    private void RequestAutoStop(Func<BaseNode, INodeContext> contextFactory)
    {
        if (Interlocked.Exchange(ref _autoStopRequested, 1) == 1)
            return;

        _runCts?.Cancel();
        _ = Task.Run(async () =>
        {
            try
            {
                await StopAsync(contextFactory, CancellationToken.None, preserveFailure: true)
                    .ConfigureAwait(false);
            }
            catch
            {
                // Nothing left to report: the failure itself was already logged by RunNodeAsync.
            }
        });
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
    /// Whether the whole workflow is running: true only while every managed node instance is in
    /// <see cref="NodeState.Running"/> (initialized and started without errors, not stopped yet),
    /// false while any of them is still connecting, has failed, or has been stopped.
    /// </summary>
    public bool IsRunning => Instances.Count > 0 && Instances.All(n => n.State == NodeState.Running);

    /// <summary>Whether at least one managed node is still starting up (connecting) and none failed yet.</summary>
    public bool IsStarting => Instances.Any(n => n.Connecting);

    /// <summary>Whether at least one managed node failed to start / run.</summary>
    public bool IsFailed => Instances.Any(n => n.Failed);
}


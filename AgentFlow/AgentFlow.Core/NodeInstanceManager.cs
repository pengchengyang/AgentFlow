// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeInstanceManager.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private readonly ILogger _logger;

    // Serializes every lifecycle transition (initialize + launch, run-to-completion, stop) so the
    // automatic stop triggered by a failing node can never interleave with the user's Stop/Run.
    // Without it, two StopAsync bodies would stop the same plugins concurrently (Stop re-entry),
    // dispose each other's token source, and race over the final node states.
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);

    // Hard limits per phase: a plugin that ignores its cancellation token must never be able to
    // hang the host (and thereby the editor's Stop button or the CLI's Ctrl+C handler) forever.
    // Public so a host can tune them for a slow plugin without changing the defaults.

    /// <summary>Hard limit for one node's initialization (default 30s).</summary>
    public static TimeSpan InitializeTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Hard limit for one node's Stop (default 5s).</summary>
    public static TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Hard limit for waiting on the launched runs during stop / run-to-completion (default 30s).</summary>
    public static TimeSpan RunTimeout { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Interval used by the Core health monitor to verify that every node which reported a
    /// successful start continues to report <see cref="BaseNode.Running"/> = true (default 1s).
    /// </summary>
    public static TimeSpan RunningHealthCheckInterval { get; set; } = TimeSpan.FromSeconds(1);

    // Background runs launched by Launch, awaited by StopAsync / RunAsync. The node is kept so a
    // run that ignores cancellation can be named in the timeout error message.
    private readonly List<(BaseNode Node, Task Run)> _runTasks = new();
    private readonly object _runTasksGate = new();

    // One token source per start session: it links the caller's token and is cancelled both by the
    // Stop button and by the automatic stop that a failing node triggers.
    private CancellationTokenSource? _runCts;
    private int _autoStopRequested;
    private Task? _healthMonitorTask;
    // Instances that already completed a start; used to reject a second start of a non-reusable node.
    private readonly HashSet<BaseNode> _everStarted = new();

    public NodeInstanceManager(PluginLoader pluginLoader, ILogger? logger = null)
    {
        _pluginLoader = pluginLoader ?? throw new ArgumentNullException(nameof(pluginLoader));
        _logger = logger ?? NullLogger.Instance;
    }

    public NodeInstanceManager(IEnumerable<BaseNode> instances, ILogger? logger = null)
    {
        _instances = (instances ?? throw new ArgumentNullException(nameof(instances))).ToList();
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>All node instances currently managed by this manager.</summary>
    public IReadOnlyList<BaseNode> Instances => _instances ?? _pluginLoader!.Instances;

    /// <summary>Reject a second start of a non-reusable instance (the host cannot rebuild it).</summary>
    private void EnsureReusableNodes()
    {
        foreach (var node in Instances)
        {
            if (_everStarted.Contains(node) && !node.IsReusable)
                throw new InvalidOperationException(
                    $"Node '{NodeDisplayName(node)}' (#{node.InstanceId}) is not reusable; " +
                    "recreate the node instance before starting the workflow again.");
        }
    }

    private static string NodeDisplayName(BaseNode node) =>
        string.IsNullOrWhiteSpace(node.DisplayName) ? node.Name : node.DisplayName!;

    /// <summary>Clear per-run pin values of every managed node (wiring is preserved).</summary>
    private void ClearRuntimeValues()
    {
        foreach (var node in Instances)
        {
            foreach (var pin in node.InputPins)
                pin.ClearValue();
            foreach (var pin in node.OutputPins)
                pin.ClearValue();
        }
    }

    /// <summary>
    /// Stop a previous Start/Run session on this manager. Caller holds the lifecycle gate. A manager
    /// can be reused by the GUI; without this a second start would create a new token while the old
    /// runs are still alive and would also leave their tasks in <see cref="_runTasks"/>.
    /// </summary>
    private async Task StopPreviousRunAsync(
        Func<BaseNode, INodeContext> contextFactory,
        CancellationToken ct)
    {
        if (_runCts is null && _runTasks.Count == 0)
            return;

        _logger.LogWarning("A previous run is still active; stopping it before starting a new session.");
        // Do not let an already-cancelled caller token interrupt cleanup of the previous session;
        // the new session's cancellation has nothing to do with releasing the old resources.
        await StopCoreAsync(contextFactory, CancellationToken.None, preserveFailure: false)
            .ConfigureAwait(false);
    }

    /// <summary>Initialize every node instance, in load order.</summary>
    public async Task InitializeAsync(Func<BaseNode, INodeContext> contextFactory, CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopPreviousRunAsync(contextFactory, ct).ConfigureAwait(false);
            EnsureReusableNodes();
            ClearRuntimeValues();
            var initialized = new List<BaseNode>();
            BaseNode? failedNode = null;
            try
            {
                foreach (var node in Instances)
                {
                    failedNode = node;
                    await node.Initialize(contextFactory(node), ct).ConfigureAwait(false);
                    initialized.Add(node);
                    failedNode = null;
                }
            }
            catch
            {
                var nodesToStop = new List<BaseNode>(initialized);
                if (failedNode is not null) nodesToStop.Add(failedNode);
                await StopNodesBestEffortAsync(nodesToStop.AsEnumerable().Reverse(), contextFactory,
                    CancellationToken.None).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Launch every node instance concurrently, without waiting for them to finish. Each node is
    /// first marked <see cref="NodeState.Connecting"/> (so the editor can show "starting"), then its
    /// <see cref="BaseNode.Run"/> executes on the thread pool. Because the nodes start concurrently,
    /// a node that has to wait for a resource (e.g. a socket client retrying its connection) no
    /// longer blocks the startup of the nodes after it — the previous sequential loop did.
    /// A node must set <see cref="BaseNode.Running"/> = true before <see cref="BaseNode.Run"/> returns
    /// normally; otherwise the run is treated as a failed start. Once a node is running, the Core
    /// health monitor continuously verifies that it keeps reporting Running = true. A node that
    /// throws or stops reporting Running = true is marked <see cref="NodeState.Failed"/>, logged, and
    /// the whole workflow is stopped automatically so the other nodes release their resources.
    /// <para>
    /// Callers must hold <see cref="_lifecycleGate"/>: only <see cref="StartAsync"/> and
    /// <see cref="RunAsync"/> call it, so a launch can never start a new token source while a stop
    /// is disposing the old one.
    /// </para>
    /// </summary>
    private void Launch(Func<BaseNode, INodeContext> contextFactory, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(contextFactory);
        ct.ThrowIfCancellationRequested();
        Interlocked.Exchange(ref _autoStopRequested, 0);
        // Callers hold the lifecycle gate and Start/RunAsync stop the previous session first, so no
        // live run can still be using the old token. Cancel before dispose anyway as a safety net.
        _runCts?.Cancel();
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
            lock (_runTasksGate) _runTasks.Add((node, run));
        }

        _healthMonitorTask = MonitorRunningNodesAsync(contextFactory, token);
    }

    /// <summary>
    /// Run every node instance concurrently and wait until all of them have returned (headless /
    /// finite-workflow mode). Throws when a node failed or the run watchdog expired, so a CLI run
    /// reports a non-zero outcome.
    /// </summary>
    public async Task RunAsync(Func<BaseNode, INodeContext> contextFactory, CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            // A second Run on the same manager must not inherit the previous session's token or
            // run tasks. Stop it first (serialized by the lifecycle gate) before creating a new one.
            await StopPreviousRunAsync(contextFactory, ct).ConfigureAwait(false);
            EnsureReusableNodes();
            try
            {
                Launch(contextFactory, ct);
                foreach (var node in Instances)
                    _everStarted.Add(node);
            }
            catch
            {
                // Launch is now after successful initialization in StartAsync, but RunAsync is
                // also public: a context-factory / token failure must release whatever launched.
                await StopCoreAsync(contextFactory, CancellationToken.None, preserveFailure: false)
                    .ConfigureAwait(false);
                throw;
            }
            var runs = TakeRunTasks();
            var completed = await WaitForRunsAsync(runs, RunTimeout).ConfigureAwait(false);

            if (Instances.FirstOrDefault(n => n.Failed) is { } failed)
                throw new InvalidOperationException(
                    $"Node '{failed.DisplayName}' failed to run (state: {failed.State}).");

            if (!completed)
                throw new InvalidOperationException(
                    $"Node(s) did not finish within {RunTimeout}; the run watchdog stopped waiting.");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Start a persistent workflow: initialize every instance, then launch them concurrently.
    /// This does NOT wait for the nodes to finish; the workflow stays "running" until
    /// <see cref="StopAsync"/> (GUI Stop button, CLI Ctrl+C, or the automatic stop on failure).
    /// When initialization fails, the nodes that already initialized are stopped again (best
    /// effort, as required by the <see cref="BaseNode.Stop"/> contract for a failed start), the
    /// failing node is marked <see cref="NodeState.Failed"/>, and the original exception is
    /// rethrown so the caller still sees the failure.
    /// </summary>
    public async Task StartAsync(Func<BaseNode, INodeContext> contextFactory, CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            // Starting a second session on the same manager must first stop the previous runs so
            // Launch can never dispose a token source that is still in use and two runs cannot
            // execute concurrently.
            await StopPreviousRunAsync(contextFactory, ct).ConfigureAwait(false);

            // A reused node must not see the previous run's last input sample: HasInput/WaitForInput
            // would otherwise return stale data immediately. Wiring is left untouched.
            ClearRuntimeValues();

            // A node that cannot rebuild its resources cannot be started twice on the same instance.
            // The editor/CLI must recreate such a node before the next run; failing loudly is better
            // than silently reusing a disposed socket / completed channel.
            EnsureReusableNodes();

            // Remember which nodes initialized successfully: they already acquired resources (ports,
            // sockets, files) and must be torn down again when a later node fails to initialize.
            var initialized = new List<BaseNode>();
            BaseNode? failedNode = null;
            try
            {
                foreach (var node in Instances)
                {
                    failedNode = node;
                    // Bound each initialization: a plugin that hangs in Initialize (e.g. an
                    // unreachable dependency with no timeout of its own) would otherwise block the
                    // whole start, and the user's Stop behind this gate, forever.
                    var init = node.Initialize(contextFactory(node), ct);
                    if (await Task.WhenAny(init, Task.Delay(InitializeTimeout, CancellationToken.None))
                            .ConfigureAwait(false) != init)
                        throw new TimeoutException(
                            $"Node '{node.DisplayName}' did not initialize within {InitializeTimeout}.");
                    await init.ConfigureAwait(false);
                    initialized.Add(node);
                    failedNode = null;
                }
            }
            catch
            {
                // Stop everything that was initialized (plus the failing node, which may have acquired
                // part of its resources before throwing), in reverse order. Stop failures are logged by
                // the helper and never hide the original initialization error.
                var nodesToStop = new List<BaseNode>(initialized);
                if (failedNode is not null) nodesToStop.Add(failedNode);
                await StopNodesBestEffortAsync(nodesToStop.AsEnumerable().Reverse(), contextFactory,
                    CancellationToken.None).ConfigureAwait(false);

                foreach (var node in Instances)
                {
                    if (ReferenceEquals(node, failedNode))
                        node.SetFailed();
                    else
                        node.ResetState();
                }

                if (failedNode is not null)
                    _logger.LogError("Node '{Node}' (#{InstanceId}) failed to initialize; the workflow was not started.",
                        failedNode.Name, failedNode.InstanceId);

                throw;
            }
            try
            {
                Launch(contextFactory, ct);
                foreach (var node in Instances)
                    _everStarted.Add(node);
            }
            catch
            {
                // Initialization succeeded for every node, so a launch failure (context factory,
                // cancellation, dictionary lookup) must stop them again before propagating.
                await StopCoreAsync(contextFactory, CancellationToken.None, preserveFailure: false)
                    .ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// Stop every node instance (reverse load order), wait for the launched runs to unwind, then
    /// reset every node's lifecycle state to <see cref="NodeState.Idle"/>. Waiting happens BEFORE
    /// the reset so a node that unwinds badly during teardown (e.g. it was disposed mid-connect)
    /// cannot leave a stale Failed / Running state behind in the editor.
    /// <para>
    /// A node whose <see cref="BaseNode.Stop"/> throws (or whose context cannot be created) is
    /// logged at error level and skipped; the remaining nodes are still stopped, so one bad plugin
    /// cannot leave every other node's sockets/ports open. This method never throws.
    /// </para>
    /// <para>
    /// Every phase is bounded by a hard timeout: each node's Stop (<see cref="StopTimeout"/>) and the
    /// wait for the run tasks (<see cref="RunTimeout"/>). A node that ignores cancellation is logged
    /// by name at error level and the stop still completes, so the editor's Stop button and the
    /// CLI's Ctrl+C handler can never hang forever.
    /// </para>
    /// <para>
    /// The whole stop runs under <see cref="_lifecycleGate"/>, so the automatic stop started by a
    /// failing node, a user-initiated stop, and the next Run are serialized: plugins are never
    /// stopped twice concurrently and the node states cannot be reset by two stops interleaving.
    /// </para>
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
        await _lifecycleGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(contextFactory, ct, preserveFailure).ConfigureAwait(false);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>
    /// The actual stop, executed while <see cref="_lifecycleGate"/> is held. Cancels this session's
    /// token, stops the node instances best-effort, waits for the runs (bounded), then converges the
    /// node states. The token source is owned by this method for the whole stop and only disposed
    /// after the runs have been awaited, so a later Launch can never dispose a token that is still
    /// in use.
    /// </summary>
    private async Task StopCoreAsync(
        Func<BaseNode, INodeContext> contextFactory,
        CancellationToken ct,
        bool preserveFailure)
    {
        // Cancel the run session first so nodes blocked on their run token (connect retries, waits)
        // observe the stop instead of continuing to run. The health monitor observes the same token
        // and exits without treating a normal stop as a node failure.
        var cts = _runCts;
        var healthMonitor = _healthMonitorTask;
        _healthMonitorTask = null;
        cts?.Cancel();
        try
        {
            await StopNodesBestEffortAsync(Instances.Reverse(), contextFactory, ct).ConfigureAwait(false);
        }
        finally
        {
            var runs = TakeRunTasks();
            var completed = true;
            if (runs.Count > 0)
                completed = await WaitForRunsAsync(runs, RunTimeout).ConfigureAwait(false);

            if (healthMonitor is not null)
            {
                try
                {
                    await healthMonitor.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected when the run session is stopped or fails.
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "The Core node health monitor terminated unexpectedly.");
                }
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
            if (ReferenceEquals(_runCts, cts)) _runCts = null;
            if (completed || cts is null)
            {
                cts?.Dispose();
            }
            else
            {
                // An orphan run is still unwinding and may register on the token; disposing the
                // source now can turn that into ObjectDisposedException. Leave it alive (bounded
                // leak) rather than corrupting the teardown of a misbehaving plugin.
                _logger.LogWarning(
                    "A run task did not stop within {Timeout}; its cancellation token source is left alive until the process exits.",
                    RunTimeout);
            }
        }
    }

    /// <summary>
    /// Stop the given nodes in the given order, isolating per-node failures: a node whose
    /// <see cref="BaseNode.Stop"/> throws, or one that does not return within
    /// <see cref="StopTimeout"/>, is logged at error level and the loop continues with the next
    /// node, so a single broken plugin cannot strand the resources of all the others.
    /// Returns the number of nodes that failed to stop. Never throws.
    /// </summary>
    private async Task<int> StopNodesBestEffortAsync(
        IEnumerable<BaseNode> nodes,
        Func<BaseNode, INodeContext> contextFactory,
        CancellationToken ct)
    {
        var failedCount = 0;
        foreach (var node in nodes)
        {
            try
            {
                var stop = node.Stop(contextFactory(node), ct);
                // Per-node hard limit: a plugin that blocks in Stop (e.g. closing a socket that
                // never completes) must not stall the remaining nodes and the host.
                var finished = await Task.WhenAny(stop, Task.Delay(StopTimeout, CancellationToken.None))
                    .ConfigureAwait(false) == stop;
                if (!finished)
                {
                    failedCount++;
                    ObserveOrphaned(stop);
                    _logger.LogError(
                        "Node '{Node}' (#{InstanceId}) did not stop within {Timeout}; continuing with the remaining nodes.",
                        node.Name, node.InstanceId, StopTimeout);
                    continue;
                }

                await stop.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                failedCount++;
                _logger.LogError(ex,
                    "Node '{Node}' (#{InstanceId}) failed to stop: {Message}. Continuing with the remaining nodes.",
                    node.Name, node.InstanceId, ex.Message);
            }
        }

        if (failedCount > 0)
        {
            _logger.LogError(
                "{FailedCount} node(s) failed to stop; their resources may still be held. Restart the process if the failure persists.",
                failedCount);
        }

        return failedCount;
    }

    /// <summary>
    /// Await the launched run tasks with a hard limit. A node that ignores its cancellation token
    /// must not hang the stop (or a finite run) forever; when the limit expires the still-running
    /// nodes are logged by name and the caller continues.
    /// Returns true when every run finished within the limit, false when the watchdog expired.
    /// </summary>
    private async Task<bool> WaitForRunsAsync(
        IReadOnlyList<(BaseNode Node, Task Run)> runs,
        TimeSpan timeout)
    {
        if (runs.Count == 0) return true;

        var all = Task.WhenAll(runs.Select(r => r.Run));
        if (await Task.WhenAny(all, Task.Delay(timeout, CancellationToken.None)).ConfigureAwait(false) == all)
        {
            try { await all.ConfigureAwait(false); }
            catch { /* node failures were already marked + logged by RunNodeAsync */ }
            return true;
        }

        var stuck = runs.Where(r => !r.Run.IsCompleted).Select(r => r.Node.Name);
        _logger.LogError(
            "Run watchdog: node(s) {Nodes} did not finish within {Timeout}; they may still be running and holding resources.",
            string.Join(", ", stuck), timeout);
        return false;
    }

    /// <summary>Take (and clear) the launched run tasks, so each stop/run session awaits its own set.</summary>
    private List<(BaseNode Node, Task Run)> TakeRunTasks()
    {
        lock (_runTasksGate)
        {
            var runs = _runTasks.ToList();
            _runTasks.Clear();
            return runs;
        }
    }

    /// <summary>
    /// Mark the exception of an abandoned stop task as observed, so a timed-out Stop that later
    /// faults cannot surface as an unobserved exception; the timeout itself was already logged.
    /// </summary>
    private static void ObserveOrphaned(Task task)
    {
        _ = task.ContinueWith(t => _ = t.Exception, CancellationToken.None,
            TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    /// <summary>
    /// Run one node and converge its lifecycle state:
    /// <list type="bullet">
    /// <item><see cref="BaseNode.Run"/> returned normally with <see cref="BaseNode.Running"/> = true →
    /// <see cref="NodeState.Running"/>: the node became operational and remains running until the
    /// workflow is stopped.</item>
    /// <item><see cref="BaseNode.Run"/> returned normally while <see cref="BaseNode.Running"/> was
    /// false → <see cref="NodeState.Failed"/>: a successful Run contract requires the node to
    /// explicitly report that it is operational.</item>
    /// <item>stopped by the workflow (its token was cancelled) → idle (the stop reset owns the final
    /// state);</item>
    /// <item>cancelled any other way (an internal timeout, CancelAfter, a library deadline) →
    /// <see cref="NodeState.Failed"/> + logged, because the workflow is still running and the node
    /// silently never did its work;</item>
    /// <item>threw → <see cref="NodeState.Failed"/> + logged. The node's <see cref="BaseNode.ErrorPolicy"/>
    /// decides what happens next: <see cref="NodeFailureMode.FailFast"/> stops the whole workflow,
    /// <see cref="NodeFailureMode.Isolate"/> leaves the other nodes running, and
    /// <see cref="NodeFailureMode.Retry"/> retries before failing fast. A <see cref="PinWiringException"/>
    /// is logged separately (graph/configuration error rather than a business failure).</item>
    /// </list>
    /// Never throws, so one bad node cannot take the whole workflow (or the editor) down.
    /// </summary>
    private async Task RunNodeAsync(
        BaseNode node,
        INodeContext context,
        CancellationToken ct,
        Func<BaseNode, INodeContext> contextFactory)
    {
        var policy = node.ErrorPolicy;
        var attempt = 0;

        while (true)
        {
            try
            {
                await node.Run(context, ct).ConfigureAwait(false);

                if (ct.IsCancellationRequested)
                {
                    node.SetIdle();
                    return;
                }

                if (!node.Running)
                {
                    throw new InvalidOperationException(
                        $"Node '{node.Name}' returned from Run without setting Running = true.");
                }

                node.SetRunning();
                return;
            }
            catch (OperationCanceledException oce) when (!ct.IsCancellationRequested)
            {
                // The workflow token was NOT cancelled, so the cancellation came from inside the node
                // (HttpClient timeout, CancelAfter, a library-level deadline...). Treating it as
                // "stopped" would leave the node Idle and the UI/CLI reporting success even though the
                // node never finished its work; report it as the failure it is.
                if (policy.Mode == NodeFailureMode.Retry && attempt < policy.MaxRetries)
                {
                    attempt++;
                    if (await DelayRetryAsync(node, policy, attempt, oce, context, ct).ConfigureAwait(false))
                        continue;
                }

                if (ct.IsCancellationRequested)
                {
                    node.SetIdle();
                    return;
                }

                node.SetFailed();
                context.Logger.LogError(oce,
                    "Node '{Node}' was cancelled without the workflow being stopped (likely a timeout): {Message}",
                    node.Name, oce.Message);
                HandleFailure(node, policy, context, contextFactory);
                return;
            }
            catch (OperationCanceledException)
            {
                // The workflow is being stopped: expected teardown, the stop reset owns the state.
                node.SetIdle();
                return;
            }
            catch (Exception ex) when (ct.IsCancellationRequested)
            {
                // Teardown race (the node was disposed while it was still connecting, ...): not a failure.
                node.SetIdle();
                context.Logger.LogDebug(ex, "Node '{Node}' ended while stopping.", node.Name);
                return;
            }
            catch (PinWiringException ex)
            {
                node.SetFailed();
                context.Logger.LogError(ex,
                    "Pin wiring error in node '{Node}': {Message}. Check the pin types and the graph wiring.",
                    node.Name, ex.Message);
                HandleFailure(node, policy, context, contextFactory);
                return;
            }
            catch (Exception ex)
            {
                if (policy.Mode == NodeFailureMode.Retry && attempt < policy.MaxRetries)
                {
                    attempt++;
                    if (await DelayRetryAsync(node, policy, attempt, ex, context, ct).ConfigureAwait(false))
                        continue;
                }

                if (ct.IsCancellationRequested)
                {
                    node.SetIdle();
                    return;
                }

                node.SetFailed();
                context.Logger.LogError(ex, "Node '{Node}' failed to start: {Message}", node.Name, ex.Message);
                HandleFailure(node, policy, context, contextFactory);
                return;
            }
        }
    }

    /// <summary>
    /// Log a retry, wait for the configured delay and return true when the node should be run again.
    /// Returns false when the workflow was stopped during the delay (the caller then leaves the node idle).
    /// </summary>
    private async Task<bool> DelayRetryAsync(
        BaseNode node,
        NodeErrorPolicy policy,
        int attempt,
        Exception error,
        INodeContext context,
        CancellationToken ct)
    {
        context.Logger.LogWarning(error,
            "Node '{Node}' failed (attempt {Attempt}/{MaxRetries}); retrying in {Delay}.",
            node.Name, attempt, policy.MaxRetries, policy.RetryDelay);

        try
        {
            await Task.Delay(policy.RetryDelay, ct).ConfigureAwait(false);
            return true;
        }
        catch (OperationCanceledException)
        {
            node.SetIdle();
            return false;
        }
    }

    /// <summary>
    /// Finish a node failure. The workflow-level invariant is strict: once any node reaches
    /// <see cref="NodeState.Failed"/>, the whole workflow must stop so its UI and lifecycle state
    /// cannot claim that anything is still running. <see cref="NodeFailureMode.Isolate"/> remains a
    /// diagnostic policy value but no longer overrides this lifecycle guarantee.
    /// </summary>
    private void HandleFailure(
        BaseNode node,
        NodeErrorPolicy policy,
        INodeContext context,
        Func<BaseNode, INodeContext> contextFactory)
    {
        if (policy.Mode == NodeFailureMode.Isolate)
        {
            context.Logger.LogWarning(
                "Node '{Node}' failed under the Isolate policy; the workflow still stops to preserve the global running-state invariant.",
                node.Name);
        }
        else
        {
            context.Logger.LogWarning(
                "Node '{Node}' failed; stopping the workflow so every node releases its resources.",
                node.Name);
        }

        RequestAutoStop(contextFactory);
    }

    /// <summary>
    /// Continuously verify that nodes which successfully started keep reporting Running = true.
    /// Connecting nodes are deliberately ignored during startup. A node that drops out of the
    /// running state is marked failed and triggers the same automatic workflow stop as a thrown
    /// exception, so GUI, CLI, and headless hosts share one lifecycle rule.
    /// </summary>
    private async Task MonitorRunningNodesAsync(
        Func<BaseNode, INodeContext> contextFactory,
        CancellationToken ct)
    {
        if (Instances.Count == 0)
            return;

        using var timer = new PeriodicTimer(RunningHealthCheckInterval);
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                if (ct.IsCancellationRequested)
                    return;

                var inactive = Instances.FirstOrDefault(n =>
                    n.State == NodeState.Running && !n.Running);
                if (inactive is null)
                    continue;

                inactive.SetFailed();
                _logger.LogError(
                    "Node '{Node}' (#{InstanceId}) stopped reporting Running = true while the workflow was active; stopping the workflow.",
                    inactive.Name, inactive.InstanceId);
                RequestAutoStop(contextFactory);
                return;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Expected when the run session is stopped or fails.
        }
    }

    /// <summary>
    /// Stop the whole workflow after a node failed. Runs on its own task because this is requested
    /// from inside one of the run tasks that <see cref="StopAsync"/> has to await. The stop is
    /// serialized by <see cref="_lifecycleGate"/>, so it queues behind — and therefore never races
    /// with — a user-initiated Stop or the next Run.
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
    /// <see cref="NodeState.Running"/> and still reports <see cref="BaseNode.Running"/> = true.
    /// Returns false while any node is connecting, failed, stopped, or no longer operational.
    /// </summary>
    public bool IsRunning => Instances.Count > 0 &&
        Instances.All(n => n.State == NodeState.Running && n.Running);

    /// <summary>Whether at least one managed node is still starting up (connecting) and none failed yet.</summary>
    public bool IsStarting => Instances.Any(n => n.Connecting);

    /// <summary>Whether at least one managed node failed to start / run.</summary>
    public bool IsFailed => Instances.Any(n => n.Failed);
}


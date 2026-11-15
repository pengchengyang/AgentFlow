// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="WorkflowRunSession.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;
using AgentFlow.Core.ExternalNodes;
using BroadcastKit;
using Microsoft.Extensions.Logging;

namespace AgentFlow.Core;

/// <summary>
/// Owns one independent workflow instance: catalog registry scope, graph instance, engine,
/// cancellation and result state. A session never shares node instances with another session.
/// </summary>
internal sealed class WorkflowRunSession : IWorkflowStopRequestSink
{
    private readonly WorkflowRunRequest _request;
    private readonly NodeRegistry _registry;
    private readonly Func<WorkflowRunRequest, ILoggerFactory> _loggerFactoryFactory;
    private readonly Func<WorkflowRunRequest, NodeServiceRegistry?>? _servicesFactory;
    private readonly WorkflowRunMode _runMode;
    private readonly Action<WorkflowRunSessionSnapshot> _onChanged;
    private readonly Action<WorkflowRunResult> _onCompleted;
    private readonly ILogger _fallbackLogger;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _stopCts = new();
    private readonly TaskCompletionSource<WorkflowRunResult> _completion =
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private WorkflowRunState _state = WorkflowRunState.Queued;
    private int _exitCode;
    private DateTimeOffset _startedAt = DateTimeOffset.Now;
    private DateTimeOffset? _finishedAt;
    private string? _error;
    private string? _cleanupError;
    private bool _stopRequested;
    private bool _executionStarted;
    private bool _completed;
    private WorkflowRunResult? _result;

    public WorkflowRunSession(
        WorkflowRunRequest request,
        NodeRegistry registry,
        Func<WorkflowRunRequest, ILoggerFactory> loggerFactoryFactory,
        Func<WorkflowRunRequest, NodeServiceRegistry?>? servicesFactory,
        WorkflowRunMode runMode,
        ILogger fallbackLogger,
        Action<WorkflowRunSessionSnapshot> onChanged,
        Action<WorkflowRunResult> onCompleted)
    {
        _request = request ?? throw new ArgumentNullException(nameof(request));
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _loggerFactoryFactory = loggerFactoryFactory
            ?? throw new ArgumentNullException(nameof(loggerFactoryFactory));
        _servicesFactory = servicesFactory;
        _runMode = runMode;
        _fallbackLogger = fallbackLogger ?? throw new ArgumentNullException(nameof(fallbackLogger));
        _onChanged = onChanged ?? throw new ArgumentNullException(nameof(onChanged));
        _onCompleted = onCompleted ?? throw new ArgumentNullException(nameof(onCompleted));
        _request.Validate();
    }

    public string RunId => _request.RunId;

    public Task<WorkflowRunResult> Completion => _completion.Task;

    internal CancellationToken StopToken => _stopCts.Token;

    internal bool StopRequested
    {
        get
        {
            lock (_gate)
                return _stopRequested;
        }
    }

    /// <summary>Host service exposed to control nodes; stops only this workflow instance.</summary>
    public bool RequestStop(string sourceNodeName, string triggerMessage)
    {
        _fallbackLogger.LogInformation(
            "Workflow instance {RunId} received a stop request from node {NodeName}: {Trigger}.",
            RunId,
            sourceNodeName,
            triggerMessage);
        return RequestStop();
    }

    public WorkflowRunSessionSnapshot GetSnapshot()
    {
        lock (_gate)
            return CreateSnapshotLocked();
    }

    /// <summary>Request only this instance to stop. Safe to call before or during execution.</summary>
    public bool RequestStop()
    {
        WorkflowRunSessionSnapshot? snapshot = null;
        lock (_gate)
        {
            if (_completed || _stopRequested)
                return false;

            _stopRequested = true;
            if (_state is WorkflowRunState.Starting or WorkflowRunState.Running)
            {
                _state = WorkflowRunState.Stopping;
                snapshot = CreateSnapshotLocked();
            }
        }

        NotifyChanged(snapshot);
        try { _stopCts.Cancel(); }
        catch (ObjectDisposedException) { }
        return true;
    }

    /// <summary>Cancel a queued instance without attempting to create a graph or node instances.</summary>
    public WorkflowRunResult CancelQueued()
    {
        RequestStop();
        return Complete(WorkflowRunState.Cancelled, 130, "Cancelled before the instance started.");
    }

    /// <summary>Execute exactly once. Higher layers await <see cref="Completion"/> for the result.</summary>
    public async Task<WorkflowRunResult> ExecuteAsync()
    {
        lock (_gate)
        {
            if (_completed)
                return _result!;
            if (_executionStarted)
                throw new InvalidOperationException($"Workflow run '{RunId}' was already started.");
            _executionStarted = true;
            _startedAt = DateTimeOffset.Now;
        }

        if (StopRequested || _stopCts.IsCancellationRequested)
            return Complete(WorkflowRunState.Cancelled, 130, "Cancelled before the instance started.");

        SetState(WorkflowRunState.Starting);

        ILoggerFactory? instanceLoggerFactory = null;
        ILogger logger = _fallbackLogger;
        NodeRegistryScope? nodeScope = null;
        TopicBus? topicBus = null;
        WorkflowEngine? engine = null;
        WorkflowGraph? graph = null;
        var cleanupErrors = new List<string>();
        var finalState = WorkflowRunState.Failed;
        var finalExitCode = 1;
        string? finalError = "Workflow execution ended without a result.";

        try
        {
            instanceLoggerFactory = _loggerFactoryFactory(_request);
            logger = instanceLoggerFactory.CreateLogger("AgentFlow.RunSession");
            logger.LogInformation(
                "Loading workflow for instance {RunId}: {WorkflowSource} (ordinal {Ordinal})",
                RunId, _request.SourceDescription, _request.Ordinal);

            if (StopRequested || _stopCts.IsCancellationRequested)
            {
                finalState = WorkflowRunState.Cancelled;
                finalExitCode = 130;
                finalError = "Cancelled before the workflow was loaded.";
                goto Finish;
            }

            topicBus = new TopicBus();
            var services = _servicesFactory?.Invoke(_request) ?? new NodeServiceRegistry();
            services.Add<ITopicBus>(topicBus);
            services.Add<IWorkflowStopRequestSink>(this);

            nodeScope = _registry.CreateWorkflowScope(
                instanceLoggerFactory.CreateLogger(nameof(ExternalNodeRuntime)),
                topicBus);

            graph = !string.IsNullOrWhiteSpace(_request.WorkflowPath)
                ? EditorJsonLoader.Load(_request.WorkflowPath!, nodeScope.Registry)
                : EditorJsonLoader.FromJson(_request.WorkflowJson!, nodeScope.Registry);

            if (StopRequested || _stopCts.IsCancellationRequested)
            {
                finalState = WorkflowRunState.Cancelled;
                finalExitCode = 130;
                finalError = "Cancelled before the workflow started.";
                goto Finish;
            }

            engine = new WorkflowEngine(nodeScope.Registry, instanceLoggerFactory, services);
            await engine.InitializeAsync(graph, _stopCts.Token).ConfigureAwait(false);

            if (StopRequested || _stopCts.IsCancellationRequested)
            {
                finalState = WorkflowRunState.Cancelled;
                finalExitCode = 130;
                finalError = "Cancelled before the workflow started.";
                goto Finish;
            }

            var persistent = _runMode == WorkflowRunMode.Persistent
                || (_runMode == WorkflowRunMode.Auto && engine.RequiresPersistentHost);

            SetState(WorkflowRunState.Running);

            if (persistent)
            {
                logger.LogInformation(
                    "Starting persistent workflow instance {RunId}: initialize -> start -> wait for stop -> stop -> shutdown.",
                    RunId);

                await engine.StartAsync(graph, _stopCts.Token).ConfigureAwait(false);
                logger.LogInformation(
                    "Persistent workflow instance {RunId} started; waiting for Stop or Auto Stop.",
                    RunId);

                await WaitForPersistentRunEndAsync(engine, _stopCts.Token).ConfigureAwait(false);

                if (_stopCts.IsCancellationRequested)
                    throw new OperationCanceledException(_stopCts.Token);
                if (engine.HasFailedNodes)
                {
                    throw new InvalidOperationException(
                        "A workflow node failed while the persistent workflow was running.");
                }

                if (_runMode == WorkflowRunMode.Persistent)
                {
                    throw new InvalidOperationException(
                        "Persistent workflow stopped without a stop request or node failure.");
                }

                logger.LogInformation(
                    "Auto workflow instance {RunId} completed because it contains no long-running node.",
                    RunId);
            }
            else
            {
                logger.LogInformation(
                    "Executing finite workflow instance {RunId}: initialize -> run -> stop -> shutdown.",
                    RunId);
                await engine.RunPreparedAsync(graph, _stopCts.Token).ConfigureAwait(false);
                logger.LogInformation("Workflow instance {RunId} completed successfully.", RunId);
            }

            finalState = WorkflowRunState.Succeeded;
            finalExitCode = 0;
            finalError = null;
        }
        catch (OperationCanceledException ex) when (StopRequested || _stopCts.IsCancellationRequested)
        {
            logger.LogInformation("Workflow instance {RunId} cancelled.", RunId);
            finalState = WorkflowRunState.Cancelled;
            finalExitCode = 130;
            finalError = $"Cancelled: {ex.Message}";
        }
        catch (Exception ex) when (StopRequested || _stopCts.IsCancellationRequested)
        {
            logger.LogInformation(ex, "Workflow instance {RunId} ended while stopping.", RunId);
            finalState = WorkflowRunState.Cancelled;
            finalExitCode = 130;
            finalError = $"Cancelled: {ex.Message}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Workflow instance {RunId} failed: {Message}", RunId, ex.Message);
            finalState = WorkflowRunState.Failed;
            finalExitCode = 1;
            finalError = ex.GetType().Name + ": " + ex.Message;
        }
        finally
        {
            async Task CaptureCleanupAsync(string operation, Func<Task> action)
            {
                try
                {
                    await action().ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    var message = $"{operation}: {ex.GetType().Name}: {ex.Message}";
                    cleanupErrors.Add(message);
                    logger.LogError(ex, "Workflow instance {RunId} cleanup failed during {Operation}.", RunId, operation);
                }
            }

            if (engine is not null && graph is not null)
            {
                await CaptureCleanupAsync(
                    "Stop",
                    () => engine.StopAsync(graph, CancellationToken.None)).ConfigureAwait(false);
                await CaptureCleanupAsync(
                    "Shutdown",
                    () => engine.ShutdownAsync(graph, CancellationToken.None)).ConfigureAwait(false);

                if (engine.LastStopFailureCount > 0)
                    cleanupErrors.Add($"Stop reported {engine.LastStopFailureCount} node failure(s).");
                if (engine.LastShutdownFailureCount > 0)
                    cleanupErrors.Add($"Shutdown reported {engine.LastShutdownFailureCount} node failure(s).");
            }

            if (nodeScope is not null)
            {
                await CaptureCleanupAsync(
                    "Run scope disposal",
                    async () => await nodeScope.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
            }

            if (topicBus is not null)
            {
                await CaptureCleanupAsync(
                    "TopicBus disposal",
                    async () => await topicBus.DisposeAsync().ConfigureAwait(false)).ConfigureAwait(false);
            }

            if (instanceLoggerFactory is IDisposable disposable)
            {
                try { disposable.Dispose(); }
                catch (Exception ex)
                {
                    cleanupErrors.Add($"Logger disposal: {ex.GetType().Name}: {ex.Message}");
                    _fallbackLogger.LogDebug(ex,
                        "Workflow run logger disposal failed for {RunId}.", RunId);
                }
            }
        }

Finish:
        var cleanupError = cleanupErrors.Count == 0 ? null : string.Join(" | ", cleanupErrors);
        if (cleanupError is not null && finalState == WorkflowRunState.Succeeded)
        {
            finalState = WorkflowRunState.Failed;
            finalExitCode = 1;
            finalError = "Workflow cleanup failed.";
        }

        return Complete(finalState, finalExitCode, finalError, cleanupError);
    }

    /// <summary>
    /// Persistent workflows remain active until the host asks them to stop, a node fails, or the
    /// node manager auto-stops. The initial grace period lets asynchronous dependency launch tasks
    /// move out of Idle before the first activity check.
    /// </summary>
    private static async Task WaitForPersistentRunEndAsync(
        WorkflowEngine engine,
        CancellationToken cancellationToken)
    {
        for (var attempt = 0; attempt < 20; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (engine.HasFailedNodes || engine.IsActive)
                break;

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (engine.HasFailedNodes || !engine.IsActive)
                return;

            await Task.Delay(100, cancellationToken).ConfigureAwait(false);
        }
    }

    private void SetState(WorkflowRunState state)
    {
        WorkflowRunSessionSnapshot snapshot;
        lock (_gate)
        {
            if (_completed)
                return;
            _state = state;
            snapshot = CreateSnapshotLocked();
        }
        NotifyChanged(snapshot);
    }

    private WorkflowRunResult Complete(
        WorkflowRunState state,
        int exitCode,
        string? error,
        string? cleanupError = null)
    {
        WorkflowRunResult? result;
        WorkflowRunSessionSnapshot? snapshot = null;
        lock (_gate)
        {
            if (_completed)
                return _result!;

            _state = state;
            _exitCode = exitCode;
            _error = error;
            _cleanupError = cleanupError;
            _finishedAt = DateTimeOffset.Now;
            _completed = true;
            result = new WorkflowRunResult(
                RunId,
                _request.SourceDescription,
                _request.Ordinal,
                state,
                exitCode,
                _startedAt,
                _finishedAt.Value,
                error,
                cleanupError);
            _result = result;
            snapshot = CreateSnapshotLocked();
        }

        NotifyChanged(snapshot);
        try { _onCompleted(result); }
        catch (Exception ex) { _fallbackLogger.LogError(ex, "Workflow run completion observer failed for {RunId}.", RunId); }
        _completion.TrySetResult(result);
        return result;
    }

    private WorkflowRunSessionSnapshot CreateSnapshotLocked() =>
        new(RunId,
            _request.SourceDescription,
            _request.Ordinal,
            _request.DisplayName,
            _state,
            _exitCode,
            _startedAt,
            _finishedAt,
            _error,
            _cleanupError);

    private void NotifyChanged(WorkflowRunSessionSnapshot? snapshot)
    {
        if (snapshot is null)
            return;
        try { _onChanged(snapshot); }
        catch (Exception ex) { _fallbackLogger.LogError(ex, "Workflow run state observer failed for {RunId}.", RunId); }
    }
}


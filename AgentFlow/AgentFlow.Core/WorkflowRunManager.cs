// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="WorkflowRunManager.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using Microsoft.Extensions.Logging;

namespace AgentFlow.Core;

/// <summary>
/// Runs multiple workflow instances independently in one process. Every instance receives its
/// own registry scope, graph, node instances, external Worker runtime, logger factory and
/// cancellation source. The manager is shared by CLI and future Desktop hosts.
/// </summary>
public sealed class WorkflowRunManager
{
    private readonly NodeRegistry _registry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Func<WorkflowRunRequest, ILoggerFactory> _runLoggerFactoryFactory;
    private readonly Func<WorkflowRunRequest, NodeServiceRegistry?>? _servicesFactory;
    private readonly WorkflowRunConcurrencyGate? _sharedConcurrencyGate;
    private readonly WorkflowRunOptions _options;
    private readonly ILogger<WorkflowRunManager> _logger;
    private readonly ILogger _sessionLogger;
    private readonly object _gate = new();
    private readonly List<WorkflowRunSession> _sessions = new();
    private readonly Dictionary<string, WorkflowRunSession> _sessionsByRunId =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _running;
    private WorkflowRunOptions? _currentOptions;
    private CancellationTokenSource? _batchCts;
    private TaskCompletionSource<IReadOnlyList<WorkflowRunResult>>? _runCompletion;

    public WorkflowRunManager(
        NodeRegistry registry,
        ILoggerFactory loggerFactory,
        Func<WorkflowRunRequest, ILoggerFactory> runLoggerFactoryFactory,
        WorkflowRunOptions? options = null,
        Func<WorkflowRunRequest, NodeServiceRegistry?>? servicesFactory = null,
        WorkflowRunConcurrencyGate? concurrencyGate = null)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _runLoggerFactoryFactory = runLoggerFactoryFactory
            ?? throw new ArgumentNullException(nameof(runLoggerFactoryFactory));
        _servicesFactory = servicesFactory;
        _sharedConcurrencyGate = concurrencyGate;
        _options = options ?? new WorkflowRunOptions();
        _options.Validate();
        _logger = loggerFactory.CreateLogger<WorkflowRunManager>();
        _sessionLogger = loggerFactory.CreateLogger("AgentFlow.WorkflowRunSession");
    }

    /// <summary>Raised whenever an instance changes lifecycle state. Safe to call from worker threads.</summary>
    public event Action<WorkflowRunSessionSnapshot>? SessionChanged;

    /// <summary>Raised once when all instances in the current batch have completed.</summary>
    public event Action<IReadOnlyList<WorkflowRunResult>>? BatchCompleted;

    /// <summary>Point-in-time snapshots of the current batch in request order.</summary>
    public IReadOnlyList<WorkflowRunSessionSnapshot> Sessions
    {
        get
        {
            lock (_gate)
                return _sessions.Select(s => s.GetSnapshot()).ToList();
        }
    }

    /// <summary>
    /// Execute all requests and return one result per request. One failed instance does not prevent
    /// the remaining instances from completing unless <see cref="WorkflowRunFailurePolicy.CancelBatch"/>
    /// was selected. Cancellation requests stop every instance and still return result records.
    /// </summary>
    public Task<IReadOnlyList<WorkflowRunResult>> RunAsync(
        IReadOnlyList<WorkflowRunRequest> requests,
        CancellationToken cancellationToken = default)
        => RunAsync(requests, _options, cancellationToken);

    /// <summary>Execute one batch with explicit options, allowing each Desktop/CLI batch to choose its policy.</summary>
    public async Task<IReadOnlyList<WorkflowRunResult>> RunAsync(
        IReadOnlyList<WorkflowRunRequest> requests,
        WorkflowRunOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(requests);
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        if (requests.Count == 0)
            return Array.Empty<WorkflowRunResult>();

        var requestList = requests.ToList();
        ValidateRequests(requestList);

        TaskCompletionSource<IReadOnlyList<WorkflowRunResult>> completion;
        WorkflowRunSession[] sessions;
        CancellationTokenSource batchCts;
        lock (_gate)
        {
            if (_running)
                throw new InvalidOperationException("WorkflowRunManager.RunAsync is already active.");

            _running = true;
            _currentOptions = options;
            _sessions.Clear();
            _sessionsByRunId.Clear();
            foreach (var request in requestList)
            {
                var session = new WorkflowRunSession(
                    request,
                    _registry,
                    _runLoggerFactoryFactory,
                    _servicesFactory,
                    options.RunMode,
                    _sessionLogger,
                    OnSessionChanged,
                    OnSessionCompleted);
                _sessions.Add(session);
                _sessionsByRunId.Add(session.RunId, session);
            }

            sessions = _sessions.ToArray();
            batchCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            _batchCts = batchCts;
            completion = new TaskCompletionSource<IReadOnlyList<WorkflowRunResult>>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            _runCompletion = completion;
        }

        using var cancellationRegistration = batchCts.Token.Register(StopSessions);
        WorkflowRunConcurrencyGate? concurrencyGate = _sharedConcurrencyGate;
        if (concurrencyGate is not null)
        {
            // A shared Desktop gate always follows the currently selected global limit.
            concurrencyGate.SetLimit(options.MaxConcurrency);
        }
        else if (options.MaxConcurrency > 0)
        {
            concurrencyGate = new WorkflowRunConcurrencyGate(options.MaxConcurrency);
        }

        try
        {
            _logger.LogInformation(
                "Starting workflow run batch: Instances={Instances}, MaxConcurrency={MaxConcurrency}, FailurePolicy={FailurePolicy}.",
                sessions.Length,
                options.MaxConcurrency == 0 ? "unlimited" : options.MaxConcurrency,
                options.FailurePolicy);

            var tasks = sessions
                .Select(session => Task.Run(
                    () => RunSessionAsync(session, batchCts, concurrencyGate),
                    CancellationToken.None))
                .ToArray();
            var results = await Task.WhenAll(tasks).ConfigureAwait(false);

            completion.TrySetResult(results);
            try { BatchCompleted?.Invoke(results); }
            catch (Exception ex) { _logger.LogError(ex, "Workflow run batch completion observer failed."); }
            return results;
        }
        catch (Exception ex)
        {
            completion.TrySetException(ex);
            _logger.LogError(ex, "Workflow run batch failed before all instance results were produced.");
            throw;
        }
        finally
        {
            lock (_gate)
            {
                if (ReferenceEquals(_batchCts, batchCts))
                    _batchCts = null;
                if (ReferenceEquals(_runCompletion, completion))
                    _runCompletion = null;
                _currentOptions = null;
                _running = false;
            }
            batchCts.Dispose();
        }
    }

    /// <summary>Request the selected instance to stop. Its final result still becomes available.</summary>
    public bool RequestStop(string runId)
    {
        WorkflowRunSession? session;
        lock (_gate)
            _sessionsByRunId.TryGetValue(runId, out session);
        return session?.RequestStop() ?? false;
    }

    /// <summary>Request every instance in the current batch to stop.</summary>
    public void RequestStopAll()
    {
        CancellationTokenSource? batchCts;
        WorkflowRunSession[] sessions;
        lock (_gate)
        {
            batchCts = _batchCts;
            sessions = _sessions.ToArray();
        }

        try { batchCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        foreach (var session in sessions)
            session.RequestStop();
    }

    private void StopSessions()
    {
        WorkflowRunSession[] sessions;
        lock (_gate)
            sessions = _sessions.ToArray();
        foreach (var session in sessions)
            session.RequestStop();
    }

    /// <summary>Request one instance to stop and wait for its Stop/Shutdown result.</summary>
    public async Task<WorkflowRunResult?> StopSessionAsync(string runId)
    {
        WorkflowRunSession? session;
        lock (_gate)
            _sessionsByRunId.TryGetValue(runId, out session);
        if (session is null)
            return null;
        session.RequestStop();
        return await session.Completion.ConfigureAwait(false);
    }

    /// <summary>
    /// Request all instances to stop and wait until every result, including Stop and Shutdown,
    /// has completed. Returns the current batch summary, or an empty list when no batch is active.
    /// </summary>
    public async Task<IReadOnlyList<WorkflowRunResult>> StopAllAsync()
    {
        TaskCompletionSource<IReadOnlyList<WorkflowRunResult>>? completion;
        lock (_gate)
            completion = _runCompletion;
        RequestStopAll();
        return completion is null
            ? Array.Empty<WorkflowRunResult>()
            : await completion.Task.ConfigureAwait(false);
    }

    private async Task<WorkflowRunResult> RunSessionAsync(
        WorkflowRunSession session,
        CancellationTokenSource batchCts,
        WorkflowRunConcurrencyGate? concurrencyGate)
    {
        WorkflowRunConcurrencyGate.Lease? lease = null;
        using var linkedCancellation = CancellationTokenSource.CreateLinkedTokenSource(
            batchCts.Token,
            session.StopToken);
        try
        {
            if (concurrencyGate is not null)
                lease = await concurrencyGate.AcquireAsync(linkedCancellation.Token).ConfigureAwait(false);

            return await session.ExecuteAsync().ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            batchCts.IsCancellationRequested || session.StopRequested)
        {
            return session.CancelQueued();
        }
        finally
        {
            lease?.Dispose();
        }
    }

    private void OnSessionChanged(WorkflowRunSessionSnapshot snapshot)
    {
        try { SessionChanged?.Invoke(snapshot); }
        catch (Exception ex) { _logger.LogError(ex, "Workflow session state observer failed for {RunId}.", snapshot.RunId); }
    }

    private void OnSessionCompleted(WorkflowRunResult result)
    {
        var options = _currentOptions ?? _options;
        if (options.FailurePolicy == WorkflowRunFailurePolicy.CancelBatch
            && result.State == WorkflowRunState.Failed)
        {
            _logger.LogWarning(
                "Workflow instance {RunId} failed; cancelling the remaining batch because FailurePolicy=CancelBatch.",
                result.RunId);
            RequestStopAll();
        }
    }

    private static void ValidateRequests(IReadOnlyList<WorkflowRunRequest> requests)
    {
        var runIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var request in requests)
        {
            request.Validate();
            if (!runIds.Add(request.RunId))
                throw new ArgumentException($"Duplicate RunId '{request.RunId}' in the workflow run batch.");
        }
    }
}





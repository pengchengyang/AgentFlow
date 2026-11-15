// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="RunManagerViewModel.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Collections.ObjectModel;
using System.Globalization;
using AgentFlow.Core;
using AgentFlow.Services;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Hosting.Logging;
using Microsoft.Extensions.Logging;

namespace AgentFlow.ViewModels;

/// <summary>One workflow source included in a Desktop Batch Run.</summary>
public sealed partial class RunBatchWorkflowItemViewModel : ViewModelBase
{
    public const int MinimumInstanceCount = 1;
    public const int MaximumInstanceCount = 50;

    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private string? _filePath;
    [ObservableProperty] private int _instanceCount = 1;
    private string _instanceCountText = "1";
    private bool _suppressInstanceCountTextSync;
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private string _statusText = "IDLE";
    [ObservableProperty] private string? _lastError;

    public string Key { get; } = Guid.NewGuid().ToString("N");
    internal string CurrentRunBatchId { get; set; } = "";
    public string PathText => FilePath ?? "";
    public string InstanceCountText
    {
        get => _instanceCountText;
        set
        {
            var rawText = value ?? "";
            if (!SetProperty(ref _instanceCountText, rawText))
                return;

            // Keep typed text as-is so values outside 1..50 can be committed on Enter or Start.
            // When the typed value is already valid, keep InstanceCount in sync for live summaries.
            if (TryParseInstanceCount(rawText, out var parsed))
            {
                _suppressInstanceCountTextSync = true;
                try
                {
                    InstanceCount = parsed;
                }
                finally
                {
                    _suppressInstanceCountTextSync = false;
                }
            }
        }
    }

    public bool IsFailed => !IsRunning
        && StatusText.StartsWith("FAILED", StringComparison.OrdinalIgnoreCase);
    public bool IsIdle => !IsRunning && !IsFailed;
    public string StatusToolTip => string.IsNullOrWhiteSpace(LastError) ? StatusText : LastError!;

    public AsyncRelayCommand? RunCommand { get; private set; }
    public AsyncRelayCommand? StopCommand { get; private set; }

    public void ConfigureActions(Func<Task> run, Func<Task> stop, Func<bool> canRun)
    {
        RunCommand = new AsyncRelayCommand(run, () => !IsRunning && canRun());
        StopCommand = new AsyncRelayCommand(stop, () => IsRunning);
    }

    public void RefreshRunCanExecute() => RunCommand?.NotifyCanExecuteChanged();

    public void BeginRun(string batchId, int instanceCount)
    {
        CurrentRunBatchId = batchId;
        IsRunning = true;
        StatusText = $"QUEUED 0/{instanceCount}";
        LastError = null;
    }

    public void MarkFailed(string error)
    {
        IsRunning = false;
        StatusText = "FAILED";
        LastError = error;
    }

    partial void OnFilePathChanged(string? value)
    {
        OnPropertyChanged(nameof(PathText));
        if (!string.IsNullOrWhiteSpace(value))
            DisplayName = Path.GetFileName(value);
    }

    /// <summary>
    /// Commit the currently typed instance count. Invalid text becomes 1, values above 50 become
    /// 50, and values below 1 become 1. Called on Enter and again immediately before Start.
    /// </summary>
    public int CommitInstanceCountText()
    {
        var normalized = NormalizeInstanceCount(_instanceCountText);
        if (InstanceCount != normalized)
            InstanceCount = normalized;

        var normalizedText = normalized.ToString(CultureInfo.InvariantCulture);
        if (_instanceCountText != normalizedText)
        {
            _instanceCountText = normalizedText;
            OnPropertyChanged(nameof(InstanceCountText));
        }

        return InstanceCount;
    }

    partial void OnInstanceCountChanged(int value)
    {
        var normalized = Math.Clamp(value, MinimumInstanceCount, MaximumInstanceCount);
        if (value != normalized)
        {
            InstanceCount = normalized;
            return;
        }

        if (_suppressInstanceCountTextSync)
            return;

        var normalizedText = normalized.ToString(CultureInfo.InvariantCulture);
        if (_instanceCountText == normalizedText)
            return;

        _instanceCountText = normalizedText;
        OnPropertyChanged(nameof(InstanceCountText));
    }

    private static int NormalizeInstanceCount(string? value)
    {
        if (!TryParseInstanceCount(value, out var parsed))
            return MinimumInstanceCount;

        return Math.Clamp(parsed, MinimumInstanceCount, MaximumInstanceCount);
    }

    private static bool TryParseInstanceCount(string? value, out int parsed)
    {
        return int.TryParse(
            value?.Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out parsed)
            && parsed is >= MinimumInstanceCount and <= MaximumInstanceCount;
    }

    partial void OnIsRunningChanged(bool value)
    {
        RunCommand?.NotifyCanExecuteChanged();
        StopCommand?.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsIdle));
    }

    partial void OnStatusTextChanged(string value)
    {
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(StatusToolTip));
    }

    partial void OnLastErrorChanged(string? value)
    {
        OnPropertyChanged(nameof(IsFailed));
        OnPropertyChanged(nameof(IsIdle));
        OnPropertyChanged(nameof(StatusToolTip));
    }
}

/// <summary>One workflow instance shown in the Desktop run-management panel.</summary>
public sealed partial class RunSessionItemViewModel : ViewModelBase
{
    [ObservableProperty] private string _runId = "";
    [ObservableProperty] private string _workflowSource = "";
    [ObservableProperty] private string _workflowItemKey = "";
    [ObservableProperty] private string _runBatchId = "";
    [ObservableProperty] private int _ordinal;
    [ObservableProperty] private string _displayName = "";
    [ObservableProperty] private WorkflowRunState _state = WorkflowRunState.Queued;
    [ObservableProperty] private int _exitCode;
    [ObservableProperty] private DateTimeOffset _startedAt;
    [ObservableProperty] private DateTimeOffset? _finishedAt;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private string? _cleanupError;

    public string StateText => State.ToString().ToUpperInvariant();
    public bool IsTerminal => State is WorkflowRunState.Succeeded
        or WorkflowRunState.Failed or WorkflowRunState.Cancelled;

    /// <summary>Run error merged with Stop/Shutdown cleanup error, mirroring the Core result.</summary>
    public string? EffectiveError =>
        string.IsNullOrWhiteSpace(CleanupError)
            ? Error
            : string.IsNullOrWhiteSpace(Error)
                ? CleanupError
                : $"{Error} | Cleanup: {CleanupError}";
    public string DurationText => FinishedAt is { } finished
        ? $"{(finished - StartedAt).TotalSeconds:0.0}s"
        : "";

    public void Apply(WorkflowRunSessionSnapshot snapshot)
    {
        RunId = snapshot.RunId;
        WorkflowSource = snapshot.WorkflowSource;
        Ordinal = snapshot.Ordinal;
        DisplayName = string.IsNullOrWhiteSpace(snapshot.DisplayName)
            ? $"Instance {snapshot.Ordinal}"
            : snapshot.DisplayName!;
        State = snapshot.State;
        ExitCode = snapshot.ExitCode;
        StartedAt = snapshot.StartedAt;
        FinishedAt = snapshot.FinishedAt;
        Error = snapshot.Error;
        CleanupError = snapshot.CleanupError;
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(EffectiveError));
        OnPropertyChanged(nameof(IsTerminal));
        OnPropertyChanged(nameof(DurationText));
    }
}

/// <summary>
/// Desktop adapter for the shared Core <see cref="WorkflowRunManager"/>. Each workflow row owns an
/// independent manager, so a row-level Run/Stop affects only that workflow while other rows can
/// continue running in parallel.
/// </summary>
public sealed partial class RunManagerViewModel : ViewModelBase
{
    private readonly NodeRegistry _registry;
    private readonly ILoggerFactory _loggerFactory;
    private readonly Action<string> _statusSink;
    private readonly Func<bool> _canStartProvider;
    private readonly Action _toggleExpandedSink;
    private readonly RunStartGate _startGate;
    private readonly WorkflowRunConcurrencyGate _concurrencyGate;
    private readonly ILogger _logger;
    private readonly Dictionary<string, WorkflowRunManager> _rowManagers =
        new(StringComparer.Ordinal);

    [ObservableProperty] private int _maxConcurrency;
    [ObservableProperty] private WorkflowRunFailurePolicy _failurePolicy =
        WorkflowRunFailurePolicy.ContinueOthers;
    [ObservableProperty] private RunBatchWorkflowItemViewModel? _selectedWorkflowItem;

    public RunManagerViewModel(
        NodeRegistry registry,
        ILoggerFactory loggerFactory,
        Action<string> statusSink,
        Func<bool> canStartProvider,
        Action toggleExpandedSink,
        RunStartGate startGate)
    {
        _registry = registry;
        _loggerFactory = loggerFactory;
        _statusSink = statusSink;
        _canStartProvider = canStartProvider;
        _toggleExpandedSink = toggleExpandedSink;
        _startGate = startGate;
        _concurrencyGate = new WorkflowRunConcurrencyGate();
        _logger = loggerFactory.CreateLogger<RunManagerViewModel>();

        WorkflowItems.CollectionChanged += (_, _) => NotifyManagerCounters();

        var initialRow = CreateBlankWorkflowItem();
        WorkflowItems.Add(initialRow);
        SelectedWorkflowItem = initialRow;
    }

    public ObservableCollection<RunBatchWorkflowItemViewModel> WorkflowItems { get; } = new();
    public ObservableCollection<RunSessionItemViewModel> Sessions { get; } = new();

    public IReadOnlyList<int> MaxConcurrencyOptions { get; } = new[] { 0, 1, 2, 4, 8 };
    public IReadOnlyList<WorkflowRunFailurePolicy> FailurePolicies { get; } =
        Enum.GetValues<WorkflowRunFailurePolicy>();

    public bool IsBatchActive => WorkflowItems.Any(item => item.IsRunning);
    public bool CanStart => !IsBatchActive
        && _canStartProvider()
        && WorkflowItems.Count > 0
        && WorkflowItems.All(item =>
            !string.IsNullOrWhiteSpace(item.FilePath)
            && item.InstanceCount >= RunBatchWorkflowItemViewModel.MinimumInstanceCount
            && item.InstanceCount <= RunBatchWorkflowItemViewModel.MaximumInstanceCount);
    public bool CanClear => WorkflowItems.Count > 0 && !IsBatchActive;
    public bool CanStopAll => WorkflowItems.Any(item => item.IsRunning);
    public int ActiveCount => Sessions.Count(session => !session.IsTerminal);
    public int CompletedCount => Sessions.Count(session => session.IsTerminal);
    public int PlannedInstanceCount => WorkflowItems.Sum(item => item.InstanceCount);
    public string Summary => $"{ActiveCount} active / {CompletedCount} completed";
    public string BatchDefinitionSummary =>
        $"{WorkflowItems.Count} workflow source(s) / {PlannedInstanceCount} instance(s) planned";

    public void ToggleExpanded() => _toggleExpandedSink();

    /// <summary>
    /// Raised when the user wants to jump to the logs of one Batch Run row. The payload is the
    /// row's RunId prefix (its batch id), which matches every instance of that row.
    /// </summary>
    public event Action<string>? LogFilterRequested;

    /// <summary>Point the log panel at one workflow row. No-op before the row has ever started.</summary>
    public void RequestLogFilter(RunBatchWorkflowItemViewModel item)
    {
        if (string.IsNullOrEmpty(item.CurrentRunBatchId))
        {
            _statusSink($"Workflow row '{item.DisplayName}' has not run yet; no logs to filter.");
            return;
        }

        LogFilterRequested?.Invoke(item.CurrentRunBatchId);
        _statusSink($"Log panel filtered to workflow row '{item.DisplayName}'.");
    }

    public void RefreshCanStart()
    {
        NotifyManagerCounters();
        foreach (var item in WorkflowItems)
            item.RefreshRunCanExecute();
    }

    partial void OnMaxConcurrencyChanged(int value)
    {
        _concurrencyGate.SetLimit(value);
        _logger.LogDebug("Desktop Batch Run global max concurrency set to {MaxConcurrency}.", value);
    }

    partial void OnFailurePolicyChanged(WorkflowRunFailurePolicy value) =>
        _logger.LogDebug("Desktop Batch Run failure policy set to {FailurePolicy}.", value);

    /// <summary>Assign an opened .rw workflow path to a specific Batch Run row.</summary>
    public void SetWorkflowFile(RunBatchWorkflowItemViewModel item, string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !WorkflowItems.Contains(item))
            return;
        if (item.IsRunning)
        {
            _statusSink("Stop this workflow row before changing its workflow file.");
            return;
        }

        var fullPath = Path.GetFullPath(path);
        if (!string.Equals(Path.GetExtension(fullPath), ".rw", StringComparison.OrdinalIgnoreCase))
        {
            ReportFilePickerError($"Only .rw workflow files can be selected: {fullPath}");
            return;
        }

        item.FilePath = fullPath;
        item.DisplayName = Path.GetFileName(fullPath);
        item.LastError = null;
        item.StatusText = "IDLE";
        RemoveSessionsForWorkflowItem(item.Key);
        NotifyManagerCounters();
        SelectedWorkflowItem = item;
        _logger.LogInformation("Selected workflow file for Batch Run row: {WorkflowPath}.", fullPath);
    }

    public void ReportFilePickerError(string message)
    {
        _logger.LogError("Batch Run file picker failed: {Message}", message);
        _statusSink($"Batch Run file selection failed: {message}");
    }

    [RelayCommand]
    private void AddWorkflowRow()
    {
        var item = CreateBlankWorkflowItem();
        WorkflowItems.Add(item);
        SelectedWorkflowItem = item;
        _statusSink("Added a blank workflow row to Batch Run.");
    }

    [RelayCommand]
    private void RemoveBottomWorkflowRow()
    {
        if (WorkflowItems.Count == 0)
            return;

        var item = WorkflowItems[^1];
        if (item.IsRunning)
        {
            _statusSink("Stop the bottom workflow row before removing it from Batch Run.");
            return;
        }

        WorkflowItems.Remove(item);
        _rowManagers.Remove(item.Key);
        RemoveSessionsForWorkflowItem(item.Key);
        SelectedWorkflowItem = WorkflowItems.LastOrDefault();
        NotifyManagerCounters();
        _statusSink("Removed the bottom workflow row from Batch Run.");
    }

    [RelayCommand]
    private async Task StartBatchAsync()
    {
        // Commit any text still pending in the row editors before validating Start All.
        foreach (var item in WorkflowItems)
            item.CommitInstanceCountText();

        if (!_startGate.TryEnter())
        {
            _statusSink("Editor Run or another Batch Run start is already in progress.");
            return;
        }

        try
        {
            if (!CanStart)
            {
                if (WorkflowItems.Count == 0)
                    _statusSink("Batch Run has no workflow rows.");
                else if (IsBatchActive)
                    _statusSink("Stop the currently running Batch Run rows before starting all rows again.");
                else if (!_canStartProvider())
                    _statusSink("The editor workflow is running. Stop it before starting Batch Run.");
                else if (WorkflowItems.Any(item => string.IsNullOrWhiteSpace(item.FilePath)))
                    _statusSink("Select an .rw workflow file for every Batch Run row before starting.");
                else
                    _statusSink("Correct every Batch Run row instance count before starting.");
                return;
            }

            var tasks = WorkflowItems
                .Where(item => !item.IsRunning)
                .Select(item => RunWorkflowRowAsync(item, gateAlreadyHeld: true))
                .ToArray();
            await Task.WhenAll(tasks);
        }
        finally
        {
            _startGate.Exit();
            NotifyManagerCounters();
        }
    }
    [RelayCommand]
    private async Task StopAllAsync()
    {
        _logger.LogInformation("Desktop requested stop for every Batch Run workflow row.");
        var tasks = _rowManagers.Values
            .Select(manager => manager.StopAllAsync())
            .ToArray();
        if (tasks.Length == 0)
            return;

        try
        {
            await Task.WhenAll(tasks);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Desktop Batch Run stop-all request failed.");
            _statusSink($"Stop all failed: {ex.Message}");
        }
    }

    [RelayCommand]
    private void Clear()
    {
        if (IsBatchActive)
        {
            _statusSink("Stop all running workflows before clearing opened workflow paths.");
            return;
        }

        WorkflowItems.Clear();
        _rowManagers.Clear();
        Sessions.Clear();
        SelectedWorkflowItem = null;
        NotifyManagerCounters();
        _statusSink("Cleared all opened workflow paths from Batch Run.");
    }

    public async Task StopAllForShutdownAsync()
    {
        if (!_rowManagers.Values.Any(manager => manager.Sessions.Any(session => !session.IsTerminal)))
            return;
        _logger.LogInformation("Desktop shutdown is stopping every Batch Run workflow row.");
        await Task.WhenAll(_rowManagers.Values.Select(manager => manager.StopAllAsync()));
    }

    private RunBatchWorkflowItemViewModel CreateBlankWorkflowItem()
    {
        var item = new RunBatchWorkflowItemViewModel
        {
            DisplayName = "",
            FilePath = null,
            InstanceCount = 1
        };
        ConfigureWorkflowItem(item);
        return item;
    }

    private void ConfigureWorkflowItem(RunBatchWorkflowItemViewModel item)
    {
        item.ConfigureActions(
            () => RunWorkflowRowAsync(item),
            () => StopWorkflowRowAsync(item),
            _canStartProvider);

        var manager = new WorkflowRunManager(
            _registry,
            _loggerFactory,
            request => AppLog.CreateLoggerFactory(request.RunId),
            servicesFactory: null,
            concurrencyGate: _concurrencyGate);
        manager.SessionChanged += snapshot => OnSessionChanged(item, snapshot);
        _rowManagers[item.Key] = manager;

        item.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(RunBatchWorkflowItemViewModel.FilePath)
                or nameof(RunBatchWorkflowItemViewModel.InstanceCount)
                or nameof(RunBatchWorkflowItemViewModel.IsRunning))
                NotifyManagerCounters();
        };
    }

    private async Task RunWorkflowRowAsync(
        RunBatchWorkflowItemViewModel item,
        bool gateAlreadyHeld = false)
    {
        var ownsGate = !gateAlreadyHeld;
        if (ownsGate && !_startGate.TryEnter())
        {
            _statusSink("Editor Run or another workflow start is already in progress.");
            return;
        }

        try
        {
            // The row Start button is the single-row equivalent of the editor/toolbar Run command.
            // Normalize the editor text first even when the user did not press Enter.
            item.CommitInstanceCountText();

            if (item.IsRunning)
                return;
            if (item.InstanceCount is < RunBatchWorkflowItemViewModel.MinimumInstanceCount
                or > RunBatchWorkflowItemViewModel.MaximumInstanceCount)
            {
                _statusSink(
                    $"Workflow row '{item.DisplayName}' instance count must be between " +
                    $"{RunBatchWorkflowItemViewModel.MinimumInstanceCount} and " +
                    $"{RunBatchWorkflowItemViewModel.MaximumInstanceCount}.");
                return;
            }
            if (!_canStartProvider())
            {
                _statusSink("A workflow is already running in the editor. Stop it before starting Batch Run.");
                return;
            }
            if (item.FilePath is null || !File.Exists(item.FilePath))
            {
                var error = $"Workflow file not found: {item.FilePath ?? "(missing path)"}";
                item.MarkFailed(error);
                _statusSink(error);
                NotifyManagerCounters();
                return;
            }

            var manager = _rowManagers[item.Key];
            RemoveSessionsForWorkflowItem(item.Key);

            var batchId = Guid.NewGuid().ToString("N");
            item.BeginRun(batchId, item.InstanceCount);
            NotifyManagerCounters();

            // A single-row launch only needs the gate until the row has published IsRunning=true.
            // Start All keeps its outer gate until all rows have completed.
            if (ownsGate)
            {
                _startGate.Exit();
                ownsGate = false;
            }

            var requests = new List<WorkflowRunRequest>();
            for (var instanceIndex = 1; instanceIndex <= item.InstanceCount; instanceIndex++)
            {
                var runId = $"run-{batchId}-{instanceIndex:00}";
                var displayName = $"{item.DisplayName} #{instanceIndex}";
                requests.Add(WorkflowRunRequest.FromFile(
                    runId,
                    item.FilePath ?? throw new InvalidOperationException("Workflow file path is unavailable."),
                    instanceIndex,
                    displayName: displayName));
            }

            var options = new WorkflowRunOptions(
                MaxConcurrency,
                FailurePolicy,
                // Keep Batch Start semantics aligned with the toolbar Run command: starting a
                // workflow means leaving it Running until the user stops it, a node fails, or an
                // AutoStop node requests shutdown. Do not let a finite-looking graph complete and
                // tear itself down immediately just because no node overrides RequiresPersistentHost.
                WorkflowRunMode.Persistent);
            _statusSink($"Starting workflow row '{item.DisplayName}' with {requests.Count} instance(s)...");
            _logger.LogInformation(
                "Desktop starting Batch Run row: Workflow={Workflow}, Instances={Instances}, GlobalMaxConcurrency={MaxConcurrency}, FailurePolicy={FailurePolicy}, RunMode={RunMode}.",
                item.PathText,
                requests.Count,
                MaxConcurrency == 0 ? "unlimited" : MaxConcurrency.ToString(),
                FailurePolicy,
                options.RunMode);

            try
            {
                var results = await manager.RunAsync(requests, options);
                ApplyRowResults(item, results);
                var succeeded = results.Count(result => result.State == WorkflowRunState.Succeeded);
                var failed = results.Count(result => result.State == WorkflowRunState.Failed);
                var cancelled = results.Count(result => result.State == WorkflowRunState.Cancelled);
                _statusSink(
                    $"Workflow row '{item.DisplayName}' finished: {succeeded} succeeded, {failed} failed, {cancelled} cancelled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Desktop Batch Run row failed for {Workflow}.", item.PathText);
                item.MarkFailed(ex.Message);
                _statusSink($"Workflow row '{item.DisplayName}' failed: {ex.Message}");
            }
            finally
            {
                if (item.IsRunning)
                    UpdateRowState(item, batchId);
                NotifyManagerCounters();
            }
        }
        finally
        {
            if (ownsGate)
                _startGate.Exit();
        }
    }
    private async Task StopWorkflowRowAsync(RunBatchWorkflowItemViewModel item)
    {
        if (!item.IsRunning)
            return;

        var manager = _rowManagers[item.Key];
        _logger.LogInformation("Desktop requested stop for workflow row {Workflow}.", item.PathText);
        try
        {
            await manager.StopAllAsync();
            UpdateRowState(item, item.CurrentRunBatchId);
        }
        catch (Exception ex)
        {
            item.MarkFailed(ex.Message);
            _logger.LogError(ex, "Desktop stop request failed for workflow row {Workflow}.", item.PathText);
            _statusSink($"Stop failed for '{item.DisplayName}': {ex.Message}");
        }
        finally
        {
            NotifyManagerCounters();
        }
    }

    private void OnSessionChanged(RunBatchWorkflowItemViewModel workflowItem, WorkflowRunSessionSnapshot snapshot)
    {
        var workflowItemKey = workflowItem.Key;
        var runBatchId = workflowItem.CurrentRunBatchId;
        Dispatcher.UIThread.Post(() =>
        {
            var item = Sessions.FirstOrDefault(session =>
                string.Equals(session.RunId, snapshot.RunId, StringComparison.OrdinalIgnoreCase));
            if (item is null)
            {
                item = new RunSessionItemViewModel
                {
                    WorkflowItemKey = workflowItemKey,
                    RunBatchId = runBatchId
                };
                Sessions.Add(item);
            }
            item.Apply(snapshot);
            if (string.Equals(workflowItem.CurrentRunBatchId, runBatchId, StringComparison.Ordinal))
                UpdateRowState(workflowItem, runBatchId);
            NotifyManagerCounters();
        });
    }

    private void ApplyRowResults(
        RunBatchWorkflowItemViewModel item,
        IReadOnlyList<WorkflowRunResult> results)
    {
        var failed = results.Count(result => result.State == WorkflowRunState.Failed);
        var cancelled = results.Count(result => result.State == WorkflowRunState.Cancelled);
        var succeeded = results.Count(result => result.State == WorkflowRunState.Succeeded);
        item.IsRunning = false;
        item.LastError = results
            .Where(result => result.State == WorkflowRunState.Failed
                && !string.IsNullOrWhiteSpace(result.EffectiveError))
            .Select(result => $"{result.RunId}: {result.EffectiveError}")
            .FirstOrDefault();
        item.StatusText = failed > 0
            ? $"FAILED {failed}/{results.Count}"
            : cancelled > 0
                ? $"CANCELLED {cancelled}/{results.Count}"
                : $"COMPLETE {succeeded}/{results.Count}";
    }

    private void UpdateRowState(RunBatchWorkflowItemViewModel item, string runBatchId)
    {
        var current = Sessions
            .Where(session => session.WorkflowItemKey == item.Key
                && session.RunBatchId == runBatchId)
            .ToArray();
        if (current.Length == 0)
            return;

        var active = current.Count(session => !session.IsTerminal);
        var failed = current.Count(session => session.State == WorkflowRunState.Failed);
        var cancelled = current.Count(session => session.State == WorkflowRunState.Cancelled);
        var succeeded = current.Count(session => session.State == WorkflowRunState.Succeeded);
        item.IsRunning = active > 0;
        item.LastError = current
            .Where(session => session.State == WorkflowRunState.Failed
                && !string.IsNullOrWhiteSpace(session.EffectiveError))
            .Select(session => $"{session.RunId}: {session.EffectiveError}")
            .FirstOrDefault();
        item.StatusText = active > 0
            ? $"RUNNING {item.InstanceCount - active}/{item.InstanceCount}"
            : failed > 0
                ? $"FAILED {failed}/{current.Length}"
                : cancelled > 0
                    ? $"CANCELLED {cancelled}/{current.Length}"
                    : $"COMPLETE {succeeded}/{current.Length}";
    }

    private void RemoveSessionsForWorkflowItem(string workflowItemKey)
    {
        for (var index = Sessions.Count - 1; index >= 0; index--)
        {
            if (string.Equals(
                    Sessions[index].WorkflowItemKey,
                    workflowItemKey,
                    StringComparison.Ordinal))
            {
                Sessions.RemoveAt(index);
            }
        }
    }

    private void NotifyManagerCounters()
    {
        OnPropertyChanged(nameof(IsBatchActive));
        OnPropertyChanged(nameof(CanStart));
        OnPropertyChanged(nameof(CanClear));
        OnPropertyChanged(nameof(CanStopAll));
        OnPropertyChanged(nameof(ActiveCount));
        OnPropertyChanged(nameof(CompletedCount));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(PlannedInstanceCount));
        OnPropertyChanged(nameof(BatchDefinitionSummary));
    }
}








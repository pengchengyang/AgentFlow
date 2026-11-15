// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="WorkflowRunModels.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Core;

/// <summary>Lifecycle state of one independently running workflow instance.</summary>
public enum WorkflowRunState
{
    Queued,
    Starting,
    Running,
    Stopping,
    Succeeded,
    Failed,
    Cancelled
}

/// <summary>
/// How one workflow instance reaches completion. RunOnce preserves the CLI one-pass lifecycle;
/// Persistent keeps nodes running until Stop, a stop request, or failure. Auto uses RunOnce for
/// finite workflows and Persistent when any node reports RequiresPersistentHost.
/// </summary>
public enum WorkflowRunMode
{
    RunOnce,
    Auto,
    Persistent
}

/// <summary>How a run manager reacts when one instance fails.</summary>
public enum WorkflowRunFailurePolicy
{
    /// <summary>Keep running the remaining instances and report the failed one at the end.</summary>
    ContinueOthers,

    /// <summary>Request every other instance to stop when one instance fails.</summary>
    CancelBatch
}

/// <summary>
/// One workflow instance requested by a host. Exactly one of <see cref="WorkflowPath"/> and
/// <see cref="WorkflowJson"/> must be supplied.
/// </summary>
public sealed record WorkflowRunRequest(
    string RunId,
    string? WorkflowPath = null,
    string? WorkflowJson = null,
    int Ordinal = 1,
    string? DisplayName = null)
{
    public static WorkflowRunRequest FromFile(
        string runId,
        string workflowPath,
        int ordinal = 1,
        string? displayName = null) =>
        new(runId, WorkflowPath: workflowPath, Ordinal: ordinal, DisplayName: displayName);

    public static WorkflowRunRequest FromJson(
        string runId,
        string workflowJson,
        int ordinal = 1,
        string? displayName = null,
        string? sourceName = null) =>
        new(runId, WorkflowJson: workflowJson, Ordinal: ordinal,
            DisplayName: displayName ?? sourceName);

    public string SourceDescription =>
        !string.IsNullOrWhiteSpace(WorkflowPath) ? WorkflowPath!
        : DisplayName ?? $"inline-json#{Ordinal}";

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(RunId))
            throw new ArgumentException("RunId must be non-empty.", nameof(RunId));
        if (Ordinal <= 0)
            throw new ArgumentOutOfRangeException(nameof(Ordinal), "Ordinal must be greater than zero.");
        if (string.IsNullOrWhiteSpace(WorkflowPath) == string.IsNullOrWhiteSpace(WorkflowJson))
        {
            throw new ArgumentException(
                "Exactly one workflow source must be supplied: WorkflowPath or WorkflowJson.",
                nameof(WorkflowPath));
        }
    }
}

/// <summary>Options applied to one workflow run batch.</summary>
public sealed record WorkflowRunOptions(
    int MaxConcurrency = 0,
    WorkflowRunFailurePolicy FailurePolicy = WorkflowRunFailurePolicy.ContinueOthers,
    WorkflowRunMode RunMode = WorkflowRunMode.RunOnce)
{
    public void Validate()
    {
        if (MaxConcurrency < 0)
            throw new ArgumentOutOfRangeException(nameof(MaxConcurrency),
                "MaxConcurrency must be zero (unlimited) or a positive number.");
    }
}

/// <summary>Final result for one workflow instance.</summary>
public sealed record WorkflowRunResult(
    string RunId,
    string WorkflowSource,
    int Ordinal,
    WorkflowRunState State,
    int ExitCode,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string? Error,
    string? CleanupError = null)
{
    public string? EffectiveError =>
        string.IsNullOrWhiteSpace(CleanupError)
            ? Error
            : string.IsNullOrWhiteSpace(Error)
                ? CleanupError
                : $"{Error} | Cleanup: {CleanupError}";

    public TimeSpan Duration => FinishedAt - StartedAt;
}

/// <summary>Thread-safe point-in-time view used by hosts to display active runs.</summary>
public sealed record WorkflowRunSessionSnapshot(
    string RunId,
    string WorkflowSource,
    int Ordinal,
    string? DisplayName,
    WorkflowRunState State,
    int ExitCode,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    string? Error,
    string? CleanupError = null)
{
    public string? EffectiveError =>
        string.IsNullOrWhiteSpace(CleanupError)
            ? Error
            : string.IsNullOrWhiteSpace(Error)
                ? CleanupError
                : $"{Error} | Cleanup: {CleanupError}";

    public bool IsTerminal =>
        State is WorkflowRunState.Succeeded
            or WorkflowRunState.Failed
            or WorkflowRunState.Cancelled;
}

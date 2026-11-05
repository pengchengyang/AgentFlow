// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeErrorPolicy.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Contracts;

/// <summary>How a node failure is handled by the workflow.</summary>
public enum NodeFailureMode
{
    /// <summary>Stop the whole workflow (historical default).</summary>
    FailFast,

    /// <summary>
    /// Preserve the failure as node-level diagnostics. The workflow still stops after the failure
    /// so the global running-state invariant remains true.
    /// </summary>
    Isolate,

    /// <summary>Retry a bounded number of times, then fall back to <see cref="FailFast"/>.</summary>
    Retry
}

/// <summary>
/// Per-node failure policy, returned by <see cref="BaseNode.ErrorPolicy"/>.
/// The default (<see cref="FailFast"/>) preserves the historical behaviour exactly.
/// </summary>
public sealed record NodeErrorPolicy
{
    /// <summary>Failure handling mode.</summary>
    public NodeFailureMode Mode { get; init; } = NodeFailureMode.FailFast;

    /// <summary>Maximum number of retries (only used when <see cref="Mode"/> is <see cref="NodeFailureMode.Retry"/>).</summary>
    public int MaxRetries { get; init; }

    /// <summary>Delay between retries.</summary>
    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>The historical fail-fast policy (stop the whole workflow).</summary>
    public static NodeErrorPolicy FailFast { get; } = new();

    /// <summary>
    /// Mark the failing node for diagnosis while retaining the workflow-wide stop guarantee.
    /// </summary>
    public static NodeErrorPolicy Isolate { get; } = new() { Mode = NodeFailureMode.Isolate };

    /// <summary>Retry a few times before failing fast.</summary>
    public static NodeErrorPolicy Retry(int maxRetries, TimeSpan? delay = null) => new()
    {
        Mode = NodeFailureMode.Retry,
        MaxRetries = Math.Max(0, maxRetries),
        RetryDelay = delay ?? TimeSpan.FromSeconds(1)
    };
}

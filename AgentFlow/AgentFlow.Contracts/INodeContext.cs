// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="INodeContext.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using Microsoft.Extensions.Logging;

namespace AgentFlow.Contracts;

/// <summary>
/// Node execution context: read input pins, write output pins, logging.
/// Provided by the workflow engine.
/// </summary>
public interface INodeContext
{
    ILogger Logger { get; }

    /// <summary>Read the value of an input pin (received from an upstream output pin).</summary>
    T? GetInput<T>(string pinName);

    /// <summary>True once at least one value has arrived on the pin since the workflow started.</summary>
    bool HasInput(string pinName);

    /// <summary>
    /// Read an input pin only when a value has actually arrived (distinct from a null/empty sample).
    /// Returns false when nothing has arrived yet.
    /// </summary>
    bool TryGetInput<T>(string pinName, out T? value);

    /// <summary>
    /// Wait until a value arrives on the input pin. Returns the sample, or null when
    /// <paramref name="timeout"/> expires; a cancelled <paramref name="cancellationToken"/> throws
    /// <see cref="OperationCanceledException"/>. Nodes that need an upstream value should await this
    /// instead of treating "no data yet" as an empty value.
    /// </summary>
    Task<Sample?> WaitForInputAsync(
        string pinName,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default);

    /// <summary>Write a value to an output pin (pushed to all connected downstream input pins).</summary>
    void SetOutput(string pinName, Sample? value);

    /// <summary>Resolve a host service (e.g. <see cref="ILLMClient"/>, <see cref="IAgentToolProvider"/>), or null when unavailable.</summary>
    T? GetService<T>();
}



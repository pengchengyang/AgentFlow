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

    /// <summary>Write a value to an output pin (pushed to all connected downstream input pins).</summary>
    void SetOutput(string pinName, Sample? value);

    /// <summary>Resolve a host service (e.g. <see cref="ILLMClient"/>, <see cref="IAgentToolProvider"/>), or null when unavailable.</summary>
    T? GetService<T>();
}



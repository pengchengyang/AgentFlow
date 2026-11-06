// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="IWorkflowStartRequestSink.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Contracts;

/// <summary>
/// Host service used by control nodes to request a workflow start without taking a
/// dependency on a specific GUI framework. Implementations must be thread-safe and
/// dispatch any GUI work to the host's UI thread.
/// </summary>
public interface IWorkflowStartRequestSink
{
    /// <summary>
    /// Request that the host start the workflow.
    /// </summary>
    /// <param name="sourceNodeName">Name of the node that raised the request.</param>
    /// <param name="triggerMessage">The trigger message received by the node.</param>
    /// <returns>True when the host accepted the request; otherwise false.</returns>
    bool RequestStart(string sourceNodeName, string triggerMessage);
}
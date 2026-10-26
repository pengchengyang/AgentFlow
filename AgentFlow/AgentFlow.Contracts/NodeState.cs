// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeState.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Contracts;

/// <summary>
/// Lifecycle state of a node instance. Every node (current and future ones) exposes it through
/// <see cref="BaseNode.State"/> so the editor can show what a node is really doing instead of
/// assuming "started = running".
/// <para>
/// Typical transitions: <see cref="Idle"/> → <see cref="Connecting"/> (startup / connecting /
/// waiting for a resource) → <see cref="Running"/> once the node is operational, and back to
/// <see cref="Idle"/> when it stops. <see cref="Failed"/> marks a start / run that threw; a new
/// start moves the node to <see cref="Connecting"/> again.
/// </para>
/// </summary>
public enum NodeState
{
    /// <summary>Not started: never started, or finished/stopped.</summary>
    Idle,

    /// <summary>
    /// Starting up: initializing, connecting, or waiting for a resource. The node is not ready yet,
    /// so the workflow must not be reported as running while any node is in this state.
    /// </summary>
    Connecting,

    /// <summary>Started and operational (the node has set <see cref="BaseNode.Running"/> to true).</summary>
    Running,

    /// <summary>Startup or execution failed; a new start is required to recover.</summary>
    Failed,
}

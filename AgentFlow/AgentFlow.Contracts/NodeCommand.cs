// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="NodeCommand.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using Microsoft.Extensions.Logging;

namespace AgentFlow.Contracts;

/// <summary>
/// A command a node exposes on its editor right-click menu. Commands are code-defined
/// (never serialized) and carry a stable id, a display title, optional metadata and an
/// asynchronous execution delegate that runs against the node instance.
/// </summary>
public sealed class NodeCommand
{
    /// <summary>Stable identifier used by the UI/core to invoke the command (not a display name).</summary>
    public required string Id { get; init; }

    /// <summary>Display text shown in the context menu.</summary>
    public required string Title { get; init; }

    /// <summary>Optional icon resource key resolved by the UI (Contracts stays UI-agnostic).</summary>
    public string? IconKey { get; init; }

    /// <summary>Optional menu group, reserved for grouping commands of the same node.</summary>
    public string? Group { get; init; }

    /// <summary>Ordering hint; lower values appear first.</summary>
    public int Order { get; init; }

    /// <summary>Executes the command against this node instance.</summary>
    public required Func<NodeCommandContext, CancellationToken, Task> Execute { get; init; }

    /// <summary>Optional predicate evaluated when the menu is built; false disables the item.</summary>
    public Func<bool>? CanExecute { get; init; }

    /// <summary>When true the UI asks for confirmation before invoking the command.</summary>
    public bool RequiresConfirmation { get; init; }
}

/// <summary>Context passed to <see cref="NodeCommand.Execute"/> when the UI invokes a node command.</summary>
public sealed class NodeCommandContext
{
    /// <summary>The node instance the command belongs to.</summary>
    public required BaseNode Node { get; init; }

    /// <summary>The same core node context used by run/stop (logger, inputs, outputs, services).</summary>
    public required INodeContext NodeContext { get; init; }

    /// <summary>Cancellation token supplied by the host.</summary>
    public CancellationToken CancellationToken { get; init; }

    /// <summary>Convenience accessor for the node logger.</summary>
    public ILogger Logger => NodeContext.Logger;
}

// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="EmptyAgentToolProvider.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;

namespace AgentFlow.Core;

/// <summary>
/// Placeholder <see cref="IAgentToolProvider"/> used by both hosts until real tools (log query,
/// knowledge base, ...) are wired in: it exposes no tools so an agent node can still run and answer
/// from its own knowledge. Keeping it in Core makes the Desktop and the CLI behave the same.
/// </summary>
internal sealed class EmptyAgentToolProvider : IAgentToolProvider
{
    public static readonly EmptyAgentToolProvider Instance = new();

    public IReadOnlyList<IAgentTool> Tools => Array.Empty<IAgentTool>();

    public IAgentTool? Find(string name) => null;
}

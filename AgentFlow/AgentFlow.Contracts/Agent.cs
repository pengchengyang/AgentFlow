// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="Agent.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Contracts;

/// <summary>
/// Abstraction over an LLM chat-completion endpoint. Implementations can target a local
/// Ollama/vLLM deployment or a cloud API; the ReAct agent node consumes this interface so
/// the provider can be swapped without touching the node.
/// </summary>
public interface ILLMClient
{
    /// <summary>
    /// Send a system prompt plus the latest user prompt and return the model's text reply.
    /// </summary>
    Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default);
}

/// <summary>
/// A tool the ReAct agent can call during its loop (e.g. query logs, query knowledge base).
/// <see cref="Description"/> is shown to the model so it can decide which tool to use.
/// </summary>
public interface IAgentTool
{
    /// <summary>Stable tool name the model uses to invoke it (e.g. "query_log").</summary>
    string Name { get; }

    /// <summary>Human/model-readable description of what this tool does and its expected input.</summary>
    string Description { get; }

    /// <summary>Run the tool with the given argument text and return an observation string.</summary>
    Task<string> ExecuteAsync(string input, CancellationToken ct = default);
}

/// <summary>
/// Provides the set of tools available to a ReAct agent run. Backed by the host application's
/// service layer (log query, knowledge base retrieval, etc.).
/// </summary>
public interface IAgentToolProvider
{
    /// <summary>All tools the agent may call during this run.</summary>
    IReadOnlyList<IAgentTool> Tools { get; }

    /// <summary>Find a tool by name, or null when not available.</summary>
    IAgentTool? Find(string name);
}

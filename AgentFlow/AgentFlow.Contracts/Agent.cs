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
/// Creates <see cref="ILLMClient"/> instances for a specific model / endpoint. Nodes resolve this
/// service (instead of a fixed <see cref="ILLMClient"/>) so their own <c>Model</c> / <c>Endpoint</c>
/// parameters actually drive the request. The host (Desktop or CLI) decides which provider is used.
/// </summary>
public interface ILLMClientFactory
{
    /// <summary>
    /// Create a client. Any argument left <c>null</c> falls back to the host's configured default
    /// (e.g. the <c>OLLAMA_BASE_URL</c> / <c>OLLAMA_MODEL</c> environment variables).
    /// </summary>
    /// <param name="model">Model tag as understood by the provider, e.g. <c>qwen3-coder:30b</c>.</param>
    /// <param name="baseUrl">Provider endpoint, e.g. <c>http://localhost:11434</c>.</param>
    /// <param name="temperature">Sampling temperature, or null for the provider default.</param>
    /// <param name="timeout">Per-request timeout, or null for the host default.</param>
    ILLMClient Create(
        string? model = null,
        string? baseUrl = null,
        double? temperature = null,
        TimeSpan? timeout = null);
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

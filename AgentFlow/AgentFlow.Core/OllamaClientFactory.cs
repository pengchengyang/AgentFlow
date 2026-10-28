// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="OllamaClientFactory.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

using AgentFlow.Contracts;

namespace AgentFlow.Core;

/// <summary>
/// Host-side <see cref="ILLMClientFactory"/> for a local (or remote) Ollama server.
/// Defaults come from the environment so a machine can be pointed at another daemon without
/// re-publishing the app:
/// <list type="bullet">
/// <item><c>OLLAMA_BASE_URL</c> (or <c>OLLAMA_HOST</c>): endpoint such as <c>http://192.168.1.5:11434</c>.</item>
/// <item><c>OLLAMA_TIMEOUT_SECONDS</c>: per-request timeout for slow (CPU / 30B) models, default 600.</item>
/// </list>
/// The node-level parameters always win over these defaults.
/// </summary>
public sealed class OllamaClientFactory : ILLMClientFactory
{
    /// <summary>Model used when neither the node parameter nor the environment specifies one.</summary>
    public const string DefaultModel = "qwen2.5:3b";

    /// <summary>Endpoint used when neither the node parameter nor the environment specifies one.</summary>
    public const string DefaultBaseUrl = "http://localhost:11434";

    /// <summary>Default per-request timeout; generous because a 30B model on CPU can take minutes.</summary>
    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(600);

    private readonly string _baseUrl;
    private readonly string _model;
    private readonly TimeSpan _timeout;

    public OllamaClientFactory(string? baseUrl = null, string? model = null, TimeSpan? timeout = null)
    {
        _baseUrl = ResolveBaseUrl(baseUrl);
        _model = ResolveModel(model);
        _timeout = ResolveTimeout(timeout);
    }

    /// <inheritdoc />
    public ILLMClient Create(
        string? model = null,
        string? baseUrl = null,
        double? temperature = null,
        TimeSpan? timeout = null)
    {
        return new OllamaLlmClient(
            ResolveBaseUrl(baseUrl ?? _baseUrl),
            ResolveModel(model ?? _model),
            temperature,
            ResolveTimeout(timeout ?? _timeout));
    }

    /// <summary>Normalise an endpoint: add the scheme when missing, map wildcard binds to loopback, trim slashes.</summary>
    public static string ResolveBaseUrl(string? explicitBaseUrl)
    {
        var url = explicitBaseUrl;
        if (string.IsNullOrWhiteSpace(url))
            url = Environment.GetEnvironmentVariable("OLLAMA_BASE_URL");
        if (string.IsNullOrWhiteSpace(url))
            url = Environment.GetEnvironmentVariable("OLLAMA_HOST");
        if (string.IsNullOrWhiteSpace(url)) return DefaultBaseUrl;

        url = url.Trim();
        if (!url.Contains("://", StringComparison.Ordinal))
            url = "http://" + url;
        // OLLAMA_HOST is often configured as 0.0.0.0:11434 (bind address); clients cannot dial that.
        url = url.Replace("//0.0.0.0", "//127.0.0.1", StringComparison.OrdinalIgnoreCase);
        return url.TrimEnd('/');
    }

    /// <summary>The configured model, or <see cref="DefaultModel"/>.</summary>
    public static string ResolveModel(string? model) =>
        string.IsNullOrWhiteSpace(model) ? DefaultModel : model.Trim();

    /// <summary>Explicit timeout, else <c>OLLAMA_TIMEOUT_SECONDS</c>, else <see cref="DefaultTimeout"/>.</summary>
    public static TimeSpan ResolveTimeout(TimeSpan? timeout)
    {
        if (timeout is { } explicitTimeout) return explicitTimeout;
        var raw = Environment.GetEnvironmentVariable("OLLAMA_TIMEOUT_SECONDS");
        return int.TryParse(raw, out var seconds) && seconds > 0
            ? TimeSpan.FromSeconds(seconds)
            : DefaultTimeout;
    }
}

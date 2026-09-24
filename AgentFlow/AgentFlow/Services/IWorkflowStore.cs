// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="IWorkflowStore.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
//     Author: Damon Yang (damon.yang@rollingwireless.com)
// </copyright>
// -----------------------------------------------------------------------

namespace AgentFlow.Services;

/// <summary>
/// Abstraction over where the workflow graph JSON is persisted / loaded.
/// The desktop build uses the local filesystem (<see cref="LocalWorkflowStore"/>);
/// the Web (WASM) build routes through a backend HTTP API (<see cref="HttpWorkflowStore"/>).
/// Keeps the shared UI library independent of the host platform.
/// </summary>
public interface IWorkflowStore
{
    /// <summary>Human-readable description of the store (shown in status text).</summary>
    string Description { get; }

    /// <summary>Load the graph JSON, or null when nothing is stored yet.</summary>
    Task<string?> LoadAsync();

    /// <summary>Persist the graph JSON.</summary>
    Task SaveAsync(string json);
}

/// <summary>Local-file store used by the desktop build.</summary>
public sealed class LocalWorkflowStore : IWorkflowStore
{
    private readonly string _path;

    public LocalWorkflowStore(string? path = null)
        => _path = path ?? Path.Combine(AppContext.BaseDirectory, "workflow.json");

    public string Description => _path;

    public Task<string?> LoadAsync()
        => Task.FromResult(File.Exists(_path) ? File.ReadAllText(_path) : null);

    public Task SaveAsync(string json)
    {
        File.WriteAllText(_path, json);
        return Task.CompletedTask;
    }
}

/// <summary>
/// HTTP store used by the Web (WASM) build: talks to the backend that hosts the
/// same Core engine, so the workflow is saved / loaded server-side.
/// </summary>
public sealed class HttpWorkflowStore : IWorkflowStore
{
    private readonly HttpClient _http;
    private readonly string _endpoint;

    public HttpWorkflowStore(HttpClient http, string endpoint = "/api/workflow")
    {
        _http = http;
        _endpoint = endpoint;
    }

    public string Description => _endpoint;

    public async Task<string?> LoadAsync()
    {
        try
        {
            var response = await _http.GetAsync(_endpoint);
            if (!response.IsSuccessStatusCode)
                return null; // nothing stored yet (e.g. 404)
            return await response.Content.ReadAsStringAsync();
        }
        catch
        {
            return null; // backend not reachable -> start empty
        }
    }

    public async Task SaveAsync(string json)
    {
        using var content = new StringContent(json, System.Text.Encoding.UTF8, "application/json");
        await _http.PostAsync(_endpoint, content);
    }
}

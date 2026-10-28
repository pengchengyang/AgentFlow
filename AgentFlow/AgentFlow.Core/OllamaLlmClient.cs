// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="OllamaLlmClient.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using AgentFlow.Contracts;

namespace AgentFlow.Core;

/// <summary>
/// Ollama 本地大模型客户端,实现 <see cref="ILLMClient"/> 接口。
/// 调用本地 Ollama 服务的原生 API (<c>/api/chat</c>)。
/// 模型名、端点、温度与超时都由调用方显式传入（见 <see cref="OllamaClientFactory"/>），
/// 失败时把 Ollama 返回的错误体一并带出，避免只看到 `404 (Not Found)`。
/// </summary>
public sealed class OllamaLlmClient : ILLMClient
{
    private readonly HttpClient _http;
    private readonly string _model;
    private readonly double? _temperature;

    /// <summary>Model tag this client sends with every request.</summary>
    public string Model => _model;

    /// <summary>Endpoint this client sends every request to.</summary>
    public string Endpoint => _http.BaseAddress?.ToString().TrimEnd('/') ?? "";

    /// <summary>
    /// 创建一个 Ollama 客户端。
    /// </summary>
    /// <param name="baseUrl">Ollama 服务地址;空则用 <c>OLLAMA_BASE_URL</c>/<c>OLLAMA_HOST</c>,再退化为 http://localhost:11434。</param>
    /// <param name="model">模型名;空则退化为 qwen2.5:3b。</param>
    /// <param name="temperature">采样温度;空则使用 Ollama 默认值。</param>
    /// <param name="timeout">单次请求超时;空则用 <c>OLLAMA_TIMEOUT_SECONDS</c> 或 600 秒。</param>
    public OllamaLlmClient(
        string? baseUrl = null,
        string? model = null,
        double? temperature = null,
        TimeSpan? timeout = null)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(OllamaClientFactory.ResolveBaseUrl(baseUrl)),
            Timeout = OllamaClientFactory.ResolveTimeout(timeout),
        };
        _model = OllamaClientFactory.ResolveModel(model);
        _temperature = temperature;
    }

    /// <inheritdoc />
    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var request = new ChatRequest
        {
            Model = _model,
            Stream = false,
            Messages =
            [
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", userPrompt),
            ],
            Options = _temperature is { } temperature ? new ChatOptions { Temperature = temperature } : null,
        };

        var response = await _http.PostAsJsonAsync("api/chat", request, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            // Ollama answers a missing model with 404 + {"error":"model \"x\" not found, ..."}.
            // Surface that text instead of only the status code; it is the difference between
            // "wrong model name on this machine" and "wrong endpoint".
            var detail = await ReadErrorAsync(response, ct).ConfigureAwait(false);
            throw new HttpRequestException(
                $"Ollama request failed: HTTP {(int)response.StatusCode} ({response.ReasonPhrase}) " +
                $"for model '{_model}' at {Endpoint}." + (detail is null ? "" : $" Provider said: {detail}"),
                inner: null,
                statusCode: response.StatusCode);
        }

        var result = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct).ConfigureAwait(false);
        return result?.Message?.Content ?? "";
    }

    /// <summary>Extract Ollama's <c>{"error": "..."}</c> body, or the raw text when it is not JSON.</summary>
    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        string body;
        try { body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return null; }
        if (string.IsNullOrWhiteSpace(body)) return null;

        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.ValueKind == JsonValueKind.Object &&
                doc.RootElement.TryGetProperty("error", out var error) &&
                error.ValueKind == JsonValueKind.String)
                return error.GetString();
        }
        catch (JsonException)
        {
            // Not JSON: fall through and return the raw text (truncated).
        }
        var trimmed = body.Trim();
        return trimmed.Length <= 300 ? trimmed : trimmed[..300] + "…";
    }

    // ===== 请求/响应模型 =====

    private class ChatRequest
    {
        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("stream")]
        public bool Stream { get; set; } = false;

        [JsonPropertyName("messages")]
        public ChatMessage[] Messages { get; set; } = Array.Empty<ChatMessage>();

        [JsonPropertyName("options")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public ChatOptions? Options { get; set; }
    }

    private class ChatOptions
    {
        [JsonPropertyName("temperature")]
        public double Temperature { get; set; }
    }

    private class ChatMessage
    {
        [JsonPropertyName("role")]
        public string Role { get; set; } = "";

        [JsonPropertyName("content")]
        public string Content { get; set; } = "";

        public ChatMessage(string role, string content)
        {
            Role = role;
            Content = content;
        }
    }

    private class ChatResponse
    {
        [JsonPropertyName("message")]
        public ChatMessage? Message { get; set; }
    }
}

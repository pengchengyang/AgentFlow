// -----------------------------------------------------------------------
// <copyright company="Rolling Wireless SARL" file="OllamaLlmClient.cs">
//     Copyright (c) Rolling Wireless SARL. All rights reserved.
// </copyright>
// -----------------------------------------------------------------------

using System.Net.Http;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using AgentFlow.Contracts;

namespace AgentFlow.Core;

/// <summary>
/// Ollama 本地大模型客户端,实现 <see cref="ILLMClient"/> 接口。
/// 调用本地 Ollama 服务的原生 API。
/// </summary>
public sealed class OllamaLlmClient : ILLMClient
{
    private readonly HttpClient _http;
    private readonly string _model;

    /// <summary>
    /// 创建一个 Ollama 客户端。
    /// </summary>
    /// <param name="baseUrl">Ollama 服务地址,默认 http://localhost:11434</param>
    /// <param name="model">模型名,默认 qwen2.5:3b</param>
    public OllamaLlmClient(string baseUrl = "http://localhost:11434", string model = "qwen2.5:3b")
    {
        _http = new HttpClient { BaseAddress = new Uri(baseUrl) };
        _model = model;
    }

    /// <inheritdoc />
    public async Task<string> CompleteAsync(string systemPrompt, string userPrompt, CancellationToken ct = default)
    {
        var request = new ChatRequest
        {
            Model = _model,
            Stream = false,
            Messages = new[]
            {
                new ChatMessage("system", systemPrompt),
                new ChatMessage("user", userPrompt)
            }
        };

        var response = await _http.PostAsJsonAsync("api/chat", request, ct);
        response.EnsureSuccessStatusCode();

        var result = await response.Content.ReadFromJsonAsync<ChatResponse>(cancellationToken: ct);
        return result?.Message?.Content ?? "";
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

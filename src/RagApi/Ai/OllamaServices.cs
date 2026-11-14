using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace RagApi.Ai;

public sealed class OllamaOptions
{
    public string BaseUrl { get; set; } = "http://localhost:11434";
    public string EmbeddingModel { get; set; } = "nomic-embed-text";
    public string ChatModel { get; set; } = "llama3.2:3b";
    public double Temperature { get; set; } = 0.2;
}

/// <summary>Embeddings via POST /api/embed do Ollama (aceita vários textos de uma vez).</summary>
public sealed class OllamaEmbeddingService(HttpClient http, IOptions<OllamaOptions> options) : IEmbeddingService
{
    private readonly OllamaOptions _opt = options.Value;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0) return [];

        var response = await http.PostAsJsonAsync("/api/embed", new { model = _opt.EmbeddingModel, input = texts }, ct);
        await response.EnsureSuccessWithBodyAsync(ct);

        var body = await response.Content.ReadFromJsonAsync<EmbedResponse>(ct)
                   ?? throw new InvalidOperationException("Resposta vazia do Ollama (/api/embed).");

        if (body.Embeddings.Count != texts.Count)
            throw new InvalidOperationException($"Esperava {texts.Count} embeddings, recebi {body.Embeddings.Count}.");

        return body.Embeddings;
    }

    private sealed record EmbedResponse([property: JsonPropertyName("embeddings")] List<float[]> Embeddings);
}

/// <summary>Geração de texto via POST /api/chat do Ollama (sem streaming, para manter simples).</summary>
public sealed class OllamaChatService(HttpClient http, IOptions<OllamaOptions> options) : IChatService
{
    private readonly OllamaOptions _opt = options.Value;

    public async Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
    {
        var request = new
        {
            model = _opt.ChatModel,
            stream = false,
            options = new { temperature = _opt.Temperature },
            messages = messages
                .Select(m => new { role = m.Role, content = m.Content })
                .Prepend(new { role = "system", content = systemPrompt }),
        };

        var response = await http.PostAsJsonAsync("/api/chat", request, ct);
        await response.EnsureSuccessWithBodyAsync(ct);

        var body = await response.Content.ReadFromJsonAsync<ChatResponse>(ct);
        return body?.Message?.Content?.Trim()
               ?? throw new InvalidOperationException("Resposta vazia do Ollama (/api/chat).");
    }

    private sealed record ChatResponse([property: JsonPropertyName("message")] ChatMessage? Message);
    private sealed record ChatMessage([property: JsonPropertyName("content")] string? Content);
}

internal static class HttpResponseExtensions
{
    /// <summary>Como EnsureSuccessStatusCode, mas inclui o corpo do erro (ex.: "model not found").</summary>
    public static async Task EnsureSuccessWithBodyAsync(this HttpResponseMessage response, CancellationToken ct)
    {
        if (response.IsSuccessStatusCode) return;
        var body = await response.Content.ReadAsStringAsync(ct);
        throw new HttpRequestException(
            $"{(int)response.StatusCode} {response.ReasonPhrase} em {response.RequestMessage?.RequestUri}: {body}",
            null, response.StatusCode);
    }
}

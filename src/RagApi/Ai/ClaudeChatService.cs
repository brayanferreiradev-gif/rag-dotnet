using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace RagApi.Ai;

public sealed class ClaudeOptions
{
    public string BaseUrl { get; set; } = "https://api.anthropic.com";

    /// <summary>Nunca no appsettings.json: use dotnet user-secrets ou a variável de ambiente Claude__ApiKey.</summary>
    public string ApiKey { get; set; } = "";

    public string Model { get; set; } = "claude-haiku-5-5";
    public int MaxTokens { get; set; } = 1024;
    public double Temperature { get; set; } = 0.2;
}

/// <summary>
/// Geração de texto com o Claude pela Messages API (POST /v1/messages), sem SDK.
/// Diferenças para o Ollama: o prompt de sistema vai num campo próprio ("system")
/// e as mensagens precisam alternar entre user e assistant, começando por user.
/// </summary>
public sealed class ClaudeChatService(HttpClient http, IOptions<ClaudeOptions> options) : IChatService
{
    private readonly ClaudeOptions _opt = options.Value;

    public async Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_opt.ApiKey))
            throw new InvalidOperationException(
                "Chave da API do Claude não configurada. Rode: dotnet user-secrets set \"Claude:ApiKey\" \"sua-chave\"");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/messages")
        {
            Content = JsonContent.Create(new
            {
                model = _opt.Model,
                max_tokens = _opt.MaxTokens,
                temperature = _opt.Temperature,
                system = systemPrompt,
                messages = Normalize(messages).Select(m => new { role = m.Role, content = m.Content }),
            }),
        };
        request.Headers.Add("x-api-key", _opt.ApiKey);
        request.Headers.Add("anthropic-version", "2023-06-01");

        var response = await http.SendAsync(request, ct);
        await response.EnsureSuccessWithBodyAsync(ct);

        var body = await response.Content.ReadFromJsonAsync<MessagesResponse>(ct);
        var text = string.Concat(body?.Content?.Where(c => c.Type == "text").Select(c => c.Text) ?? []);
        return string.IsNullOrWhiteSpace(text)
            ? throw new InvalidOperationException("Resposta vazia do Claude (/v1/messages).")
            : text.Trim();
    }

    /// <summary>Começa por "user" e junta mensagens seguidas do mesmo papel.</summary>
    private static List<ChatTurn> Normalize(IReadOnlyList<ChatTurn> messages)
    {
        var result = new List<ChatTurn>();
        foreach (var m in messages.SkipWhile(m => m.Role != "user"))
        {
            if (result.Count > 0 && result[^1].Role == m.Role)
                result[^1] = result[^1] with { Content = result[^1].Content + "\n\n" + m.Content };
            else
                result.Add(m);
        }
        return result;
    }

    private sealed record MessagesResponse([property: JsonPropertyName("content")] List<ContentBlock>? Content);

    private sealed record ContentBlock(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string? Text);
}

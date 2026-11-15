using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;

namespace RagApi.Ai;

public sealed class VoyageOptions
{
    public string BaseUrl { get; set; } = "https://api.voyageai.com";

    /// <summary>Nunca no appsettings.json: use dotnet user-secrets ou a variável de ambiente Voyage__ApiKey.</summary>
    public string ApiKey { get; set; } = "";

    /// <summary>voyage-4 é multilíngue (bom para português); voyage-4-lite é mais barato.</summary>
    public string Model { get; set; } = "voyage-4";
}

/// <summary>
/// Embeddings pela API da Voyage AI (POST /v1/embeddings), a parceira recomendada pela
/// Anthropic, já que o Claude não gera embeddings. O input_type ("document" ou "query")
/// faz o modelo otimizar o vetor para cada lado da busca.
/// </summary>
public sealed class VoyageEmbeddingService(HttpClient http, IOptions<VoyageOptions> options) : IEmbeddingService
{
    private readonly VoyageOptions _opt = options.Value;

    public async Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts, EmbeddingInput input = EmbeddingInput.Document, CancellationToken ct = default)
    {
        if (texts.Count == 0) return [];
        if (string.IsNullOrWhiteSpace(_opt.ApiKey))
            throw new InvalidOperationException(
                "Chave da Voyage não configurada. Rode: dotnet user-secrets set \"Voyage:ApiKey\" \"sua-chave\"");

        using var request = new HttpRequestMessage(HttpMethod.Post, "/v1/embeddings")
        {
            Content = JsonContent.Create(new
            {
                model = _opt.Model,
                input = texts,
                input_type = input == EmbeddingInput.Query ? "query" : "document",
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _opt.ApiKey);

        var response = await http.SendAsync(request, ct);
        await response.EnsureSuccessWithBodyAsync(ct);

        var body = await response.Content.ReadFromJsonAsync<EmbeddingsResponse>(ct)
                   ?? throw new InvalidOperationException("Resposta vazia da Voyage (/v1/embeddings).");

        return body.Data.OrderBy(d => d.Index).Select(d => d.Embedding).ToList();
    }

    private sealed record EmbeddingsResponse([property: JsonPropertyName("data")] List<EmbeddingItem> Data);

    private sealed record EmbeddingItem(
        [property: JsonPropertyName("index")] int Index,
        [property: JsonPropertyName("embedding")] float[] Embedding);
}

using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RagApi.Ai;

namespace RagApi.VectorStore;

public sealed class QdrantOptions
{
    public string BaseUrl { get; set; } = "http://localhost:6333";
    public string Collection { get; set; } = "documentos";
}

/// <summary>
/// Banco vetorial Qdrant acessado pela API REST.
/// Cada chunk vira um "point": id + vetor + payload (texto, fonte, índice).
/// </summary>
public sealed class QdrantVectorStore(HttpClient http, IOptions<QdrantOptions> options) : IVectorStore
{
    private readonly string _collection = options.Value.Collection;

    public async Task EnsureCollectionAsync(int vectorSize, CancellationToken ct = default)
    {
        var existing = await http.GetAsync($"/collections/{_collection}", ct);
        if (existing.IsSuccessStatusCode) return;
        if (existing.StatusCode != HttpStatusCode.NotFound)
            await existing.EnsureSuccessWithBodyAsync(ct);

        // Distância cosseno: compara a "direção" dos vetores, o padrão para embeddings de texto.
        var create = await http.PutAsJsonAsync($"/collections/{_collection}",
            new { vectors = new { size = vectorSize, distance = "Cosine" } }, ct);
        await create.EnsureSuccessWithBodyAsync(ct);

        // Índice no campo "source" deixa rápido o delete/filtro por documento.
        var index = await http.PutAsJsonAsync($"/collections/{_collection}/index?wait=true",
            new { field_name = "source", field_schema = "keyword" }, ct);
        await index.EnsureSuccessWithBodyAsync(ct);
    }

    public async Task DeleteBySourceAsync(string source, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync($"/collections/{_collection}/points/delete?wait=true",
            new { filter = new { must = new[] { new { key = "source", match = new { value = source } } } } }, ct);
        await response.EnsureSuccessWithBodyAsync(ct);
    }

    public async Task UpsertAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken ct = default)
    {
        if (chunks.Count == 0) return;

        var points = chunks.Select(c => new
        {
            id = StableId(c.Source, c.ChunkIndex),
            vector = c.Vector,
            payload = new { source = c.Source, chunk_index = c.ChunkIndex, text = c.Text },
        });

        var response = await http.PutAsJsonAsync($"/collections/{_collection}/points?wait=true", new { points }, ct);
        await response.EnsureSuccessWithBodyAsync(ct);
    }

    public async Task<IReadOnlyList<SearchHit>> SearchAsync(float[] queryVector, int limit, CancellationToken ct = default)
    {
        var response = await http.PostAsJsonAsync($"/collections/{_collection}/points/query",
            new { query = queryVector, limit, with_payload = true }, ct);

        // Coleção ainda não criada = nada foi indexado ainda.
        if (response.StatusCode == HttpStatusCode.NotFound) return [];
        await response.EnsureSuccessWithBodyAsync(ct);

        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var hits = new List<SearchHit>();

        foreach (var point in doc.RootElement.GetProperty("result").GetProperty("points").EnumerateArray())
        {
            var payload = point.GetProperty("payload");
            hits.Add(new SearchHit(
                Source: payload.GetProperty("source").GetString() ?? "",
                ChunkIndex: payload.GetProperty("chunk_index").GetInt32(),
                Text: payload.GetProperty("text").GetString() ?? "",
                Score: point.GetProperty("score").GetDouble()));
        }

        return hits;
    }

    /// <summary>
    /// Id determinístico (UUID a partir de fonte + índice): reindexar o mesmo
    /// documento sobrescreve os pontos em vez de duplicar.
    /// </summary>
    private static string StableId(string source, int chunkIndex)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes($"{source}#{chunkIndex}"));
        return new Guid(hash).ToString();
    }
}

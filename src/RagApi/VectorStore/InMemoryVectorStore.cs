using System.Collections.Concurrent;

namespace RagApi.VectorStore;

/// <summary>
/// Banco vetorial em memória, para rodar sem Docker/Qdrant (os dados somem ao reiniciar).
/// Faz exatamente o que o Qdrant faz numa busca simples: compara o vetor da pergunta
/// com todos os vetores guardados usando similaridade de cosseno.
/// </summary>
public sealed class InMemoryVectorStore : IVectorStore
{
    private readonly ConcurrentDictionary<(string Source, int ChunkIndex), ChunkRecord> _points = new();

    public Task EnsureCollectionAsync(int vectorSize, CancellationToken ct = default) => Task.CompletedTask;

    public Task DeleteBySourceAsync(string source, CancellationToken ct = default)
    {
        foreach (var key in _points.Keys.Where(k => k.Source == source))
            _points.TryRemove(key, out _);
        return Task.CompletedTask;
    }

    public Task UpsertAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken ct = default)
    {
        foreach (var chunk in chunks)
            _points[(chunk.Source, chunk.ChunkIndex)] = chunk;
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<SearchHit>> SearchAsync(
        float[] queryVector, int limit, IReadOnlyCollection<string>? sources = null, CancellationToken ct = default)
    {
        IReadOnlyList<SearchHit> hits = _points.Values
            .Where(p => sources is null || sources.Contains(p.Source))
            .Select(p => new SearchHit(p.Source, p.ChunkIndex, p.Text, Cosine(queryVector, p.Vector)))
            .OrderByDescending(h => h.Score)
            .Take(limit)
            .ToList();
        return Task.FromResult(hits);
    }

    /// <summary>cos(θ) = (a·b) / (|a|·|b|). 1 = mesma direção, 0 = sem relação.</summary>
    private static double Cosine(float[] a, float[] b)
    {
        if (a.Length != b.Length) return 0;
        double dot = 0, normA = 0, normB = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            normA += a[i] * a[i];
            normB += b[i] * b[i];
        }
        return normA == 0 || normB == 0 ? 0 : dot / (Math.Sqrt(normA) * Math.Sqrt(normB));
    }
}

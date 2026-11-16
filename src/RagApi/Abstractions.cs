namespace RagApi;

/// <summary>O que está sendo transformado em vetor: alguns modelos tratam documento e pergunta de forma diferente.</summary>
public enum EmbeddingInput { Document, Query }

/// <summary>Transforma texto em vetores (embeddings).</summary>
public interface IEmbeddingService
{
    Task<IReadOnlyList<float[]>> EmbedAsync(
        IReadOnlyList<string> texts, EmbeddingInput input = EmbeddingInput.Document, CancellationToken ct = default);
}

/// <summary>Gera texto a partir de um prompt de sistema e de uma sequência de mensagens.</summary>
public interface IChatService
{
    Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default);
}

/// <summary>Guarda e busca vetores por similaridade.</summary>
public interface IVectorStore
{
    Task EnsureCollectionAsync(int vectorSize, CancellationToken ct = default);
    Task DeleteBySourceAsync(string source, CancellationToken ct = default);
    Task UpsertAsync(IReadOnlyList<ChunkRecord> chunks, CancellationToken ct = default);
    /// <param name="sources">Se informado, busca só nesses documentos (usado para cada agente ver só os do seu departamento).</param>
    Task<IReadOnlyList<SearchHit>> SearchAsync(
        float[] queryVector, int limit, IReadOnlyCollection<string>? sources = null, CancellationToken ct = default);
}

/// <summary>Uma mensagem da conversa. Role: "user" ou "assistant".</summary>
public sealed record ChatTurn(string Role, string Content)
{
    public static ChatTurn User(string content) => new("user", content);
    public static ChatTurn Assistant(string content) => new("assistant", content);
}

/// <summary>Um pedaço de documento já com o seu vetor.</summary>
public sealed record ChunkRecord(string Source, int ChunkIndex, string Text, float[] Vector);

/// <summary>Resultado de uma busca vetorial.</summary>
public sealed record SearchHit(string Source, int ChunkIndex, string Text, double Score);

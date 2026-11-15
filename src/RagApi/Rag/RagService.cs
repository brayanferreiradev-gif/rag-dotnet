using System.Text;
using Microsoft.Extensions.Options;

namespace RagApi.Rag;

public sealed class RagOptions
{
    public string DocsPath { get; set; } = "docs";
    public int ChunkSize { get; set; } = 800;
    public int ChunkOverlap { get; set; } = 150;
    public int TopK { get; set; } = 4;

    /// <summary>Abaixo dessa similaridade o trecho é descartado (evita "contexto" irrelevante).</summary>
    public double MinScore { get; set; } = 0.35;

    /// <summary>Quantos chunks mandar para o modelo de embedding por requisição.</summary>
    public int EmbeddingBatchSize { get; set; } = 16;

    /// <summary>Quantas mensagens anteriores da conversa levar em conta.</summary>
    public int MaxHistoryMessages { get; set; } = 6;
}

public sealed record IngestResult(string Source, int Chunks);
public sealed record SourceRef(int Ref, string Source, int ChunkIndex, double Score, string Preview);

/// <param name="SearchQuery">A pergunta usada na busca vetorial (reescrita quando há histórico).</param>
public sealed record AskResult(string Answer, IReadOnlyList<SourceRef> Sources, string SearchQuery)
{
    /// <summary>Texto completo dos trechos enviados à IA. Fica fora do JSON da API; a avaliação usa para checar fidelidade.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public IReadOnlyList<string> Context { get; init; } = [];
}

/// <summary>
/// Orquestra os dois fluxos do RAG:
///  1. Ingestão: texto → chunks → embeddings → banco vetorial
///  2. Pergunta: pergunta → embedding → busca dos trechos → prompt com contexto → LLM
/// </summary>
public sealed partial class RagService(
    IEmbeddingService embeddings,
    IVectorStore store,
    IChatService chat,
    IOptions<RagOptions> options,
    ILogger<RagService> logger)
{
    private readonly RagOptions _opt = options.Value;

    public const string NotFoundAnswer = "Não encontrei essa informação nos documentos indexados.";

    private const string SystemPrompt = """
        Você é um assistente que responde perguntas usando SOMENTE o contexto fornecido.
        Regras:
        - Se a resposta não estiver no contexto, diga claramente que não encontrou a informação.
        - Não invente fatos, números ou nomes.
        - Cite os trechos usados no formato [1], [2] logo após a frase que eles sustentam.
        - Responda em português, de forma direta.
        """;

    // ---------- 1. Ingestão ----------

    public async Task<IngestResult> IngestAsync(string source, string text, CancellationToken ct = default)
    {
        var chunks = TextChunker.Split(text, _opt.ChunkSize, _opt.ChunkOverlap);
        if (chunks.Count == 0) return new IngestResult(source, 0);

        var vectors = new List<float[]>(chunks.Count);
        foreach (var batch in chunks.Chunk(_opt.EmbeddingBatchSize))
            vectors.AddRange(await embeddings.EmbedAsync(batch, EmbeddingInput.Document, ct));

        // A coleção é criada com o tamanho do vetor que o modelo realmente devolve
        // (768 no nomic-embed-text), então trocar de modelo não exige mudar código.
        await store.EnsureCollectionAsync(vectors[0].Length, ct);

        // Remove a versão antiga do documento antes de gravar a nova.
        await store.DeleteBySourceAsync(source, ct);
        await store.UpsertAsync(
            chunks.Select((c, i) => new ChunkRecord(source, i, c, vectors[i])).ToList(), ct);

        logger.LogInformation("Indexado {Source}: {Chunks} chunks", source, chunks.Count);
        return new IngestResult(source, chunks.Count);
    }

    public async Task<IReadOnlyList<IngestResult>> IngestFolderAsync(CancellationToken ct = default)
    {
        var root = Path.GetFullPath(_opt.DocsPath);
        if (!Directory.Exists(root))
            throw new DirectoryNotFoundException($"Pasta de documentos não encontrada: {root}");

        var files = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(f => f.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
                     || f.EndsWith(".txt", StringComparison.OrdinalIgnoreCase))
            .Order();

        var results = new List<IngestResult>();
        foreach (var file in files)
        {
            var source = Path.GetRelativePath(root, file).Replace('\\', '/');
            results.Add(await IngestAsync(source, await File.ReadAllTextAsync(file, ct), ct));
        }
        return results;
    }

    // ---------- 2. Pergunta ----------

    public async Task<AskResult> AskAsync(
        string question, IReadOnlyList<ChatTurn>? history = null, CancellationToken ct = default)
    {
        // Só as últimas mensagens: o histórico inteiro estouraria o contexto do modelo.
        var recent = (history ?? [])
            .Where(t => t.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(t.Content))
            .TakeLast(_opt.MaxHistoryMessages)
            .ToList();

        // "E no PIX?" sozinho não acha nada no banco vetorial.
        // Com histórico, o LLM primeiro reescreve a pergunta de forma completa.
        var searchQuery = recent.Count == 0 ? question : await CondenseQuestionAsync(question, recent, ct);

        var queryVector = (await embeddings.EmbedAsync([searchQuery], EmbeddingInput.Query, ct))[0];
        var hits = (await store.SearchAsync(queryVector, _opt.TopK, ct))
            .Where(h => h.Score >= _opt.MinScore)
            .ToList();

        // Sem contexto relevante, nem chamamos o LLM: é a defesa mais simples contra alucinação.
        if (hits.Count == 0)
            return new AskResult(NotFoundAnswer, [], searchQuery);

        var messages = recent.Append(ChatTurn.User(BuildUserPrompt(question, hits))).ToList();
        var answer = await chat.CompleteAsync(SystemPrompt, messages, ct);

        var sources = hits.Select((h, i) => new SourceRef(
            i + 1, h.Source, h.ChunkIndex, Math.Round(h.Score, 3), Preview(h.Text))).ToList();

        return new AskResult(answer, sources, searchQuery) { Context = hits.Select(h => h.Text).ToList() };
    }

    private const string CondensePrompt = """
        Sua tarefa é reescrever a última pergunta do usuário para que ela possa ser entendida
        sozinha, sem o histórico. Substitua pronomes e referências ("isso", "e no PIX?", "e ele?")
        pelo assunto a que se referem. Se a pergunta já for completa, devolva-a igual.
        Responda APENAS com a pergunta reescrita, sem explicações e sem aspas.
        """;

    private async Task<string> CondenseQuestionAsync(
        string question, IReadOnlyList<ChatTurn> history, CancellationToken ct)
    {
        var transcript = new StringBuilder("Histórico:\n");
        foreach (var turn in history)
            transcript.Append(turn.Role == "user" ? "Usuário: " : "Assistente: ")
                      .Append(Preview(turn.Content, 400)).Append('\n');
        transcript.Append($"\nÚltima pergunta: {question}");

        var rewritten = (await chat.CompleteAsync(CondensePrompt, [ChatTurn.User(transcript.ToString())], ct))
            .Trim().Trim('"', '“', '”').Trim();

        // Modelos pequenos às vezes "conversam" em vez de reescrever; nesse caso, usa a original.
        if (rewritten.Length == 0 || rewritten.Length > 400) return question;

        logger.LogInformation("Pergunta reescrita: {Original} -> {Rewritten}", question, rewritten);
        return rewritten;
    }

    private static string BuildUserPrompt(string question, IReadOnlyList<SearchHit> hits)
    {
        var sb = new StringBuilder("Contexto:\n\n");
        for (var i = 0; i < hits.Count; i++)
            sb.Append($"[{i + 1}] (fonte: {hits[i].Source})\n{hits[i].Text}\n\n");

        sb.Append($"Pergunta: {question}");
        return sb.ToString();
    }

    /// <summary>Trecho curto para exibição: sem marcações de Markdown (#, >) e quebras de linha.</summary>
    private static string Preview(string text, int max = 160)
    {
        var clean = string.Join(' ', MarkdownNoise().Replace(text, " ")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries));
        return clean.Length <= max ? clean : clean[..max].TrimEnd() + "…";
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"(?:^|\s)[#>*]+\s|\s+", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex MarkdownNoise();
}

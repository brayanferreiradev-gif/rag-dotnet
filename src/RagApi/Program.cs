using RagApi;
using RagApi.Ai;
using RagApi.Rag;
using RagApi.VectorStore;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<OllamaOptions>(builder.Configuration.GetSection("Ollama"));
builder.Services.Configure<QdrantOptions>(builder.Configuration.GetSection("Qdrant"));
builder.Services.Configure<RagOptions>(builder.Configuration.GetSection("Rag"));

var ollamaUrl = new Uri(builder.Configuration["Ollama:BaseUrl"] ?? "http://localhost:11434");
var qdrantUrl = new Uri(builder.Configuration["Qdrant:BaseUrl"] ?? "http://localhost:6333");

// Cada serviço depende só de uma interface: trocar Ollama por OpenAI,
// ou Qdrant por pgvector, é escrever outra implementação e mudar uma linha aqui.
builder.Services.AddHttpClient<IEmbeddingService, OllamaEmbeddingService>(c =>
{
    c.BaseAddress = ollamaUrl;
    c.Timeout = TimeSpan.FromMinutes(2);
});
builder.Services.AddHttpClient<IChatService, OllamaChatService>(c =>
{
    c.BaseAddress = ollamaUrl;
    c.Timeout = TimeSpan.FromMinutes(5); // LLM local na CPU pode demorar
});

// "Qdrant" (padrão) ou "InMemory" (roda sem Docker, os dados somem ao reiniciar).
var vectorStore = builder.Configuration["VectorStore"] ?? "Qdrant";
if (vectorStore.Equals("InMemory", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddSingleton<IVectorStore, InMemoryVectorStore>();
else
    builder.Services.AddHttpClient<IVectorStore, QdrantVectorStore>(c => c.BaseAddress = qdrantUrl);

builder.Services.AddScoped<RagService>();

var app = builder.Build();

// Tela de chat (wwwroot/index.html) servida pela própria API.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new { status = "ok", vectorStore }));

// Indexa um texto enviado no corpo da requisição.
app.MapPost("/ingest", async (IngestRequest req, RagService rag, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Source) || string.IsNullOrWhiteSpace(req.Text))
        return Results.BadRequest(new { error = "Informe 'source' e 'text'." });

    return Results.Ok(await rag.IngestAsync(req.Source.Trim(), req.Text, ct));
});

// Indexa todos os .md e .txt da pasta configurada em Rag:DocsPath.
app.MapPost("/ingest/folder", async (RagService rag, CancellationToken ct) =>
{
    var results = await rag.IngestFolderAsync(ct);
    return Results.Ok(new { files = results.Count, chunks = results.Sum(r => r.Chunks), results });
});

// Faz uma pergunta sobre os documentos. "history" é opcional: as mensagens anteriores
// da conversa, mantidas pelo cliente (a API não guarda estado).
app.MapPost("/ask", async (AskRequest req, RagService rag, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Question))
        return Results.BadRequest(new { error = "Informe 'question'." });

    return Results.Ok(await rag.AskAsync(req.Question.Trim(), req.History, ct));
});

app.Run();

public sealed record IngestRequest(string Source, string Text);
public sealed record AskRequest(string Question, List<ChatTurn>? History = null);

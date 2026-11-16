using RagApi;
using RagApi.Agents;
using RagApi.Eval;
using RagApi.Rag;

var builder = WebApplication.CreateBuilder(args);

// Chaves de API ficam fora do código: dotnet user-secrets (só na sua máquina).
builder.Configuration.AddUserSecrets(typeof(Program).Assembly, optional: true);

var providers = builder.Services.AddRag(builder.Configuration);

var app = builder.Build();

// "dotnet run -- eval": roda a avaliação e sai, sem subir o servidor.
// "dotnet run -- eval --agentes": avalia a equipe de agentes (roteamento, ferramentas, respostas).
if (args.Length > 0 && args[0].Equals("eval", StringComparison.OrdinalIgnoreCase))
    return args.Contains("--agentes")
        ? await AgentEvalRunner.RunAsync(app.Services, providers)
        : await EvalRunner.RunAsync(app.Services, providers, args.Skip(1).ToArray());

// Tela de chat (wwwroot/index.html) servida pela própria API.
app.UseDefaultFiles();
app.UseStaticFiles();

app.MapGet("/health", () => Results.Ok(new
{
    status = "ok",
    llm = $"{providers.Llm} ({providers.LlmModel})",
    embeddings = $"{providers.Embeddings} ({providers.EmbeddingModel})",
    vectorStore = providers.VectorStore,
}));

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

// Pergunta para a equipe de agentes: o roteador escolhe o especialista, que usa ferramentas.
app.MapPost("/agent", async (AskRequest req, AgentRunner agents, CancellationToken ct) =>
{
    if (string.IsNullOrWhiteSpace(req.Question))
        return Results.BadRequest(new { error = "Informe 'question'." });

    return Results.Ok(await agents.RunAsync(req.Question.Trim(), req.History, ct));
});

// Lista as skills carregadas (nome, descrição, documentos e ferramentas de cada agente).
app.MapGet("/agents", (SkillLibrary skills) =>
    skills.Skills.Select(s => new { s.Nome, s.Titulo, s.Descricao, s.Documentos, s.Ferramentas }));

// Chamados abertos pelos agentes nesta execução.
app.MapGet("/chamados", (StoreData data) => data.Chamados);

app.Run();
return 0;

public sealed record IngestRequest(string Source, string Text);
public sealed record AskRequest(string Question, List<ChatTurn>? History = null);

public partial class Program;

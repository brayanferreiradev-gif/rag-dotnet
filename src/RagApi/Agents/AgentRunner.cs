using System.Diagnostics;
using System.Text.Json;
using Microsoft.Extensions.Options;
using RagApi.Rag;

namespace RagApi.Agents;

/// <summary>Um passo de um agente: qual ferramenta chamou, com que argumentos e o que voltou.</summary>
public sealed record AgentStep(int N, string Agente, string Ferramenta, string Args, string Pensamento, string Resultado, long Ms);

/// <summary>O que um agente (trabalhador) produziu para a sua tarefa.</summary>
public sealed record WorkerResult(
    string Agente, string AgenteTitulo, string Tarefa, string Answer,
    IReadOnlyList<AgentStep> Steps, IReadOnlyList<SourceRef> Sources, IReadOnlyList<string> Context, long Ms);

/// <summary>
/// O trabalhador: roda UMA skill numa tarefa, num loop feito à mão (sem framework).
/// O LLM responde sempre em JSON, pedindo uma ferramenta ({"acao": "consultar_estoque", "args": {...}})
/// ou dando a resposta final ({"acao": "responder", "resposta": "..."}). O código executa a ferramenta,
/// devolve o resultado como nova mensagem e repete, até a resposta ou o limite de passos.
/// Funciona com qualquer LLM que siga instruções (Claude, Ollama), sem "tool calling" nativo.
/// Quem decide QUAL skill roda é o Orchestrator.
/// </summary>
public sealed class AgentRunner(
    IEnumerable<IAgentTool> tools,
    IChatService chat,
    IOptions<AgentOptions> options,
    ILogger<AgentRunner> logger)
{
    private static readonly JsonSerializerOptions ReadableJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Dictionary<string, IAgentTool> _tools = tools.ToDictionary(t => t.Name);
    private readonly AgentOptions _opt = options.Value;

    public async Task<WorkerResult> RunSkillAsync(
        Skill skill, string task, IReadOnlyList<ChatTurn> history, CancellationToken ct = default)
    {
        var total = Stopwatch.StartNew();
        var system = BuildSystemPrompt(skill);
        var messages = history.Append(ChatTurn.User(task)).ToList();
        var steps = new List<AgentStep>();
        var sources = new List<SourceRef>();
        var context = new List<string>();

        for (var i = 1; i <= _opt.MaxSteps + 1; i++)
        {
            // Passou do limite: pede a resposta final com o que já tem.
            if (i > _opt.MaxSteps)
                messages.Add(ChatTurn.User("Limite de passos atingido. Responda agora com {\"acao\": \"responder\", ...}."));

            var raw = await chat.CompleteAsync(system, messages, ct);
            var action = AgentAction.Parse(raw);

            if (action is null || action.Acao == "responder" || i > _opt.MaxSteps)
            {
                var answer = action?.Resposta ?? StripJson(raw);
                logger.LogInformation("Agente {Skill}: {Steps} passos", skill.Nome, steps.Count);
                return new WorkerResult(skill.Nome, skill.Titulo, task, answer, steps, sources, context, total.ElapsedMilliseconds);
            }

            var sw = Stopwatch.StartNew();
            string observation;
            if (!skill.Ferramentas.Contains(action.Acao) || !_tools.TryGetValue(action.Acao, out var tool))
            {
                observation = $"A ferramenta \"{action.Acao}\" não está disponível para você. Use: {string.Join(", ", skill.Ferramentas)}.";
            }
            else
            {
                var result = await tool.RunAsync(action.Args, skill, ct);
                observation = result.Observation;
                if (result.Sources is not null) sources.AddRange(result.Sources);
                context.Add(observation);
            }
            sw.Stop();

            steps.Add(new AgentStep(i, skill.Nome, action.Acao,
                action.Args.ValueKind == JsonValueKind.Undefined ? "{}" : JsonSerializer.Serialize(action.Args, ReadableJson),
                action.Pensamento ?? "", Shorten(observation, 400), sw.ElapsedMilliseconds));

            messages.Add(ChatTurn.Assistant(raw));
            messages.Add(ChatTurn.User($"Resultado de {action.Acao}:\n{observation}"));
        }

        throw new InvalidOperationException("Loop do agente terminou sem resposta."); // inalcançável
    }

    // ---------- Prompt do agente ----------

    private string BuildSystemPrompt(Skill skill)
    {
        var toolList = string.Join("\n", skill.Ferramentas
            .Where(_tools.ContainsKey)
            .Select(n => $"- {n}: {_tools[n].Description} Argumentos: {_tools[n].ArgsExample}"));

        return $$"""
            {{skill.Instrucoes}}

            # Ferramentas disponíveis
            {{toolList}}

            # Formato da resposta (obrigatório)
            Responda SEMPRE com um único objeto JSON, sem texto fora dele:
            - Para usar uma ferramenta: {"pensamento": "o que você precisa descobrir", "acao": "<nome da ferramenta>", "args": { ... } }
            - Para responder ao usuário: {"pensamento": "...", "acao": "responder", "resposta": "texto final em português"}

            # Regras
            - Use as ferramentas antes de afirmar fatos sobre a loja, produtos, pedidos ou procedimentos.
            - Use no máximo uma ferramenta por mensagem e espere o resultado.
            - Não invente nada que as ferramentas não trouxeram. Se não encontrar, diga claramente que não encontrou.
            - Ao usar trechos de buscar_documentos, cite-os como [1], [2] na resposta.
            """;
    }

    // ---------- Utilitários ----------

    private static string StripJson(string raw) => raw.Trim().Trim('`').Trim();

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

/// <summary>Uma decisão do agente, lida do JSON que o LLM devolve.</summary>
public sealed record AgentAction(string Acao, JsonElement Args, string? Resposta, string? Pensamento)
{
    public static AgentAction? Parse(string raw)
    {
        var json = ExtractJson(raw);
        if (json is not { } j || !j.TryGetProperty("acao", out var acao)) return null;

        return new AgentAction(
            (acao.GetString() ?? "").Trim().ToLowerInvariant(),
            j.TryGetProperty("args", out var args) ? args.Clone() : default,
            j.TryGetProperty("resposta", out var r) ? r.GetString() : null,
            j.TryGetProperty("pensamento", out var p) ? p.GetString() : null);
    }

    /// <summary>Acha o primeiro objeto JSON completo no texto (o LLM às vezes coloca ```json em volta).</summary>
    public static JsonElement? ExtractJson(string raw)
    {
        var start = raw.IndexOf('{');
        while (start >= 0)
        {
            int depth = 0; var inString = false; var escaped = false;
            for (var i = start; i < raw.Length; i++)
            {
                var c = raw[i];
                if (inString)
                {
                    if (escaped) escaped = false;
                    else if (c == '\\') escaped = true;
                    else if (c == '"') inString = false;
                    continue;
                }
                if (c == '"') inString = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0)
                {
                    try { return JsonDocument.Parse(raw[start..(i + 1)]).RootElement.Clone(); }
                    catch (JsonException) { break; }
                }
            }
            start = raw.IndexOf('{', start + 1);
        }
        return null;
    }
}

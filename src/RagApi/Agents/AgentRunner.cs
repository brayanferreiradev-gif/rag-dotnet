using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RagApi.Rag;

namespace RagApi.Agents;

public sealed record AgentStep(int N, string Ferramenta, string Args, string Pensamento, string Resultado, long Ms);

public sealed record AgentResult(
    string Agente, string AgenteTitulo, string MotivoRoteamento, string Answer,
    IReadOnlyList<AgentStep> Steps, IReadOnlyList<SourceRef> Sources, long Ms)
{
    /// <summary>Tudo o que as ferramentas devolveram (fica fora do JSON da API; a avaliação usa para checar fidelidade).</summary>
    [JsonIgnore] public IReadOnlyList<string> Context { get; init; } = [];
}

/// <summary>
/// Agentes feitos à mão, sem framework:
///  1. Roteador: um LLM lê a descrição de cada skill e escolhe o especialista.
///  2. Loop do agente: o LLM responde sempre em JSON, pedindo uma ferramenta
///     ({"acao": "consultar_estoque", "args": {...}}) ou dando a resposta final
///     ({"acao": "responder", "resposta": "..."}). Nosso código executa a ferramenta,
///     devolve o resultado como uma nova mensagem e repete, até a resposta ou o limite de passos.
/// Funciona com qualquer LLM que siga instruções (Claude, Ollama), sem depender de "tool calling" nativo.
/// </summary>
public sealed class AgentRunner(
    SkillLibrary skills,
    IEnumerable<IAgentTool> tools,
    IChatService chat,
    IOptions<AgentOptions> options,
    IOptions<RagOptions> ragOptions,
    ILogger<AgentRunner> logger)
{
    private static readonly JsonSerializerOptions ReadableJson = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly Dictionary<string, IAgentTool> _tools = tools.ToDictionary(t => t.Name);
    private readonly AgentOptions _opt = options.Value;

    public async Task<AgentResult> RunAsync(string question, IReadOnlyList<ChatTurn>? history = null, CancellationToken ct = default)
    {
        var total = Stopwatch.StartNew();
        var recent = (history ?? [])
            .Where(t => t.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(t.Content))
            .TakeLast(ragOptions.Value.MaxHistoryMessages)
            .ToList();

        var (skill, motivo) = await RouteAsync(question, recent, ct);
        logger.LogInformation("Roteador → {Skill} ({Motivo})", skill.Nome, motivo);

        var system = BuildSystemPrompt(skill);
        var messages = recent.Append(ChatTurn.User(question)).ToList();
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
                return new AgentResult(skill.Nome, skill.Titulo, motivo, answer, steps, Renumber(sources), total.ElapsedMilliseconds)
                    { Context = context };
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

            steps.Add(new AgentStep(i, action.Acao, action.Args.ValueKind == JsonValueKind.Undefined ? "{}" : JsonSerializer.Serialize(action.Args, ReadableJson),
                action.Pensamento ?? "", Shorten(observation, 400), sw.ElapsedMilliseconds));

            messages.Add(ChatTurn.Assistant(raw));
            messages.Add(ChatTurn.User($"Resultado de {action.Acao}:\n{observation}"));
        }

        throw new InvalidOperationException("Loop do agente terminou sem resposta."); // inalcançável
    }

    // ---------- Roteador ----------

    private async Task<(Skill Skill, string Motivo)> RouteAsync(string question, List<ChatTurn> history, CancellationToken ct)
    {
        var catalog = string.Join("\n", skills.Skills.Select(s => $"- {s.Nome}: {s.Descricao}"));
        var system = $$"""
            Você é o roteador de atendimento da Loja Exemplo. Escolha o agente mais adequado para a mensagem do usuário.

            Agentes disponíveis:
            {{catalog}}

            Responda APENAS com JSON: {"agente": "<nome>", "motivo": "frase curta"}
            """;

        var prompt = new StringBuilder();
        foreach (var t in history.TakeLast(4))
            prompt.Append(t.Role == "user" ? "Usuário: " : "Assistente: ").Append(Shorten(t.Content, 300)).Append('\n');
        prompt.Append(history.Count > 0 ? "\nMensagem atual: " : "").Append(question);

        var raw = await chat.CompleteAsync(system, [ChatTurn.User(prompt.ToString())], ct);
        var json = AgentAction.ExtractJson(raw);
        var nome = json?.TryGetProperty("agente", out var a) == true ? a.GetString() : null;
        var motivo = json?.TryGetProperty("motivo", out var m) == true ? m.GetString() ?? "" : "";

        // Se o roteador devolver algo inesperado, procura um nome de skill no texto; senão, SAC.
        var skill = skills.Find(nome)
                    ?? skills.Skills.FirstOrDefault(s => raw.Contains(s.Nome, StringComparison.OrdinalIgnoreCase))
                    ?? skills.Find("sac") ?? skills.Skills[0];
        return (skill, motivo);
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

    private static IReadOnlyList<SourceRef> Renumber(List<SourceRef> sources) =>
        sources
            .GroupBy(s => (s.Source, s.ChunkIndex))
            .Select((g, i) => g.First() with { Ref = i + 1 })
            .ToList();

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

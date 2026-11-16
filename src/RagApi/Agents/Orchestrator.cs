using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RagApi.Rag;

namespace RagApi.Agents;

/// <summary>Uma tarefa do plano: qual agente faz o quê.</summary>
public sealed record PlanTask(string Agente, string AgenteTitulo, string Tarefa);

/// <summary>Resposta final do orquestrador, com o plano e o trabalho de cada agente.</summary>
public sealed record AgentResult(
    string Agente, string AgenteTitulo, string MotivoRoteamento, string Answer,
    IReadOnlyList<PlanTask> Plano, IReadOnlyList<AgentStep> Steps, IReadOnlyList<SourceRef> Sources,
    bool Sintetizado, long Ms)
{
    /// <summary>Tudo o que as ferramentas devolveram (fica fora do JSON da API; a avaliação usa para checar fidelidade).</summary>
    [JsonIgnore] public IReadOnlyList<string> Context { get; init; } = [];

    /// <summary>Agentes que participaram (para a avaliação de roteamento).</summary>
    [JsonIgnore] public IReadOnlyList<string> Agentes => Plano.Select(p => p.Agente).Distinct().ToList();
}

/// <summary>
/// Orquestrador + trabalhadores (orchestrator-workers):
///  1. PLANEJA: um LLM lê a pergunta e a descrição de cada skill e devolve um plano,
///     {"tarefas": [{"agente": "sac", "tarefa": "..."}, {"agente": "estoque", "tarefa": "..."}]}.
///     Pergunta simples = 1 tarefa; pergunta que mistura assuntos = até 3.
///  2. DELEGA: cada tarefa roda no seu agente (AgentRunner), em paralelo.
///  3. SINTETIZA: com mais de um agente, um LLM junta as respostas numa só, sem acrescentar fatos.
/// </summary>
public sealed class Orchestrator(
    SkillLibrary skills,
    IServiceScopeFactory scopes,
    IChatService chat,
    IOptions<RagOptions> ragOptions,
    ILogger<Orchestrator> logger)
{
    private const int MaxTasks = 3;

    public async Task<AgentResult> RunAsync(string question, IReadOnlyList<ChatTurn>? history = null, CancellationToken ct = default)
    {
        var total = Stopwatch.StartNew();
        var recent = (history ?? [])
            .Where(t => t.Role is "user" or "assistant" && !string.IsNullOrWhiteSpace(t.Content))
            .TakeLast(ragOptions.Value.MaxHistoryMessages)
            .ToList();

        // 1. Planeja
        var (plan, motivo) = await PlanAsync(question, recent, ct);
        logger.LogInformation("Plano: {Plano}", string.Join(" | ", plan.Select(p => $"{p.Agente}: {p.Tarefa}")));

        // 2. Delega em paralelo. Cada agente ganha o próprio escopo de DI (ferramentas independentes).
        var workers = await Task.WhenAll(plan.Select(async task =>
        {
            using var scope = scopes.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<AgentRunner>();
            var skill = skills.Find(task.Agente)!;
            // Com 1 tarefa, o agente recebe a pergunta original (e o histórico); com várias, só a sua parte.
            return plan.Count == 1
                ? await runner.RunSkillAsync(skill, question, recent, ct)
                : await runner.RunSkillAsync(skill, task.Tarefa, [], ct);
        }));

        // 3. Sintetiza (só quando há mais de um agente)
        var answer = workers.Length == 1 ? workers[0].Answer : await SynthesizeAsync(question, workers, ct);

        var steps = workers.SelectMany(w => w.Steps).Select((s, i) => s with { N = i + 1 }).ToList();
        var sources = workers.SelectMany(w => w.Sources)
            .GroupBy(s => (s.Source, s.ChunkIndex))
            .Select((g, i) => g.First() with { Ref = i + 1 })
            .ToList();

        return new AgentResult(
            Agente: string.Join("+", workers.Select(w => w.Agente)),
            AgenteTitulo: string.Join(" + ", workers.Select(w => w.AgenteTitulo)),
            MotivoRoteamento: motivo,
            Answer: answer,
            Plano: plan,
            Steps: steps,
            Sources: sources,
            Sintetizado: workers.Length > 1,
            Ms: total.ElapsedMilliseconds)
        {
            Context = workers.SelectMany(w => w.Context).Concat(workers.Length > 1 ? workers.Select(w => w.Answer) : []).ToList(),
        };
    }

    // ---------- 1. Planejamento ----------

    private async Task<(List<PlanTask> Plan, string Motivo)> PlanAsync(
        string question, List<ChatTurn> history, CancellationToken ct)
    {
        var catalog = string.Join("\n", skills.Skills.Select(s => $"- {s.Nome}: {s.Descricao}"));
        var system = $$"""
            Você é o orquestrador do atendimento da Loja Exemplo. Sua função é planejar, não responder.

            Agentes disponíveis:
            {{catalog}}

            Divida a mensagem do usuário em tarefas e escolha um agente para cada uma.
            - Se a mensagem trata de um único assunto, crie UMA tarefa só.
            - Crie mais de uma tarefa (no máximo {{MaxTasks}}) apenas quando a mensagem pede coisas de departamentos diferentes.
            - Cada tarefa deve ser uma instrução completa, que o agente entenda sem ver a mensagem original.

            Responda APENAS com JSON:
            {"motivo": "frase curta", "tarefas": [{"agente": "<nome>", "tarefa": "<instrução>"}]}
            """;

        var prompt = new StringBuilder();
        foreach (var t in history.TakeLast(4))
            prompt.Append(t.Role == "user" ? "Usuário: " : "Assistente: ").Append(Shorten(t.Content, 300)).Append('\n');
        prompt.Append(history.Count > 0 ? "\nMensagem atual: " : "").Append(question);

        var raw = await chat.CompleteAsync(system, [ChatTurn.User(prompt.ToString())], ct);
        var json = AgentAction.ExtractJson(raw);
        var motivo = json?.TryGetProperty("motivo", out var m) == true ? m.GetString() ?? "" : "";

        var plan = new List<PlanTask>();
        if (json?.TryGetProperty("tarefas", out var tarefas) == true && tarefas.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in tarefas.EnumerateArray().Take(MaxTasks))
            {
                var skill = skills.Find(t.TryGetProperty("agente", out var a) ? a.GetString() : null);
                var tarefa = t.TryGetProperty("tarefa", out var tt) ? tt.GetString() ?? "" : "";
                if (skill is not null && !plan.Any(p => p.Agente == skill.Nome))
                    plan.Add(new PlanTask(skill.Nome, skill.Titulo, tarefa.Length > 0 ? tarefa : question));
            }
        }

        // Plano inválido: cai para um agente só, achado pelo nome no texto (ou SAC).
        if (plan.Count == 0)
        {
            var skill = skills.Skills.FirstOrDefault(s => raw.Contains(s.Nome, StringComparison.OrdinalIgnoreCase))
                        ?? skills.Find("sac") ?? skills.Skills[0];
            plan.Add(new PlanTask(skill.Nome, skill.Titulo, question));
        }

        return (plan, motivo);
    }

    // ---------- 3. Síntese ----------

    private async Task<string> SynthesizeAsync(string question, IReadOnlyList<WorkerResult> workers, CancellationToken ct)
    {
        var parts = string.Join("\n\n", workers.Select(w => $"## {w.AgenteTitulo}\nTarefa: {w.Tarefa}\nResposta: {w.Answer}"));
        var system = """
            Você junta as respostas de vários agentes da Loja Exemplo numa resposta única para o usuário.
            - Use SOMENTE as informações das respostas dos agentes. Não acrescente fatos.
            - Responda à pergunta original de forma direta e organizada, em português.
            - Se algum agente não encontrou a informação, diga isso.
            Responda apenas com o texto final, sem JSON.
            """;

        return await chat.CompleteAsync(system,
            [ChatTurn.User($"Pergunta original: {question}\n\nRespostas dos agentes:\n\n{parts}")], ct);
    }

    private static string Shorten(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}

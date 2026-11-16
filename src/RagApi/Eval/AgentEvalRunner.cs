using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using RagApi.Agents;
using RagApi.Rag;

namespace RagApi.Eval;

/// <summary>Uma pergunta do gabarito dos agentes (eval/agentes.json).</summary>
public sealed record AgentEvalQuestion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("pergunta")] string Pergunta,
    [property: JsonPropertyName("agente")] string? Agente,
    [property: JsonPropertyName("ferramenta")] string? Ferramenta,
    [property: JsonPropertyName("esperado")] string? Esperado,
    [property: JsonPropertyName("historico")] List<ChatTurn>? Historico)
{
    public bool ForaDoEscopo => Esperado is null;

    /// <summary>"caixa|sac" aceita qualquer um dos dois.</summary>
    public bool AgenteOk(string agente) =>
        Agente is null || Agente.Split('|').Any(a => a.Equals(agente, StringComparison.OrdinalIgnoreCase));
}

public sealed record AgentEvalItem(
    AgentEvalQuestion Q, string Agente, IReadOnlyList<string> Ferramentas, string Resposta,
    bool RoteamentoOk, bool? FerramentaOk, string Status, bool? Fiel, long Ms, string Motivo);

/// <summary>
/// Avaliação dos agentes: além de "a resposta está certa?", mede se o roteador escolheu o
/// especialista certo e se ele usou a ferramenta certa.
/// Uso: dotnet run -- eval --agentes
/// </summary>
public static class AgentEvalRunner
{
    public static async Task<int> RunAsync(IServiceProvider services, ServiceRegistration.Providers providers)
    {
        var config = services.GetRequiredService<IConfiguration>();
        var path = Path.GetFullPath(config["Eval:AgentQuestionsPath"] ?? "eval/agentes.json");
        var resultsDir = Path.GetFullPath(config["Eval:ResultsPath"] ?? "eval/resultados");
        var questions = JsonSerializer.Deserialize<List<AgentEvalQuestion>>(await File.ReadAllTextAsync(path))
                        ?? throw new InvalidOperationException($"Gabarito vazio: {path}");

        Console.WriteLine($"AGENTES · LLM: {providers.Llm} ({providers.LlmModel}) · Embeddings: {providers.Embeddings} ({providers.EmbeddingModel})");
        Console.WriteLine($"{questions.Count} perguntas\n");

        using var scope = services.CreateScope();
        var rag = scope.ServiceProvider.GetRequiredService<RagService>();
        var agents = scope.ServiceProvider.GetRequiredService<AgentRunner>();
        var judge = scope.ServiceProvider.GetRequiredService<IChatService>();

        Console.Write("Indexando documentos… ");
        var ingested = await rag.IngestFolderAsync();
        Console.WriteLine($"{ingested.Count} arquivos, {ingested.Sum(i => i.Chunks)} trechos.\n");

        var items = new List<AgentEvalItem>();
        for (var i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            Console.Write($"[{i + 1,2}/{questions.Count}] {q.Id,-26} ");
            try
            {
                var item = await EvaluateAsync(q, agents, judge);
                items.Add(item);
                Console.WriteLine($"{item.Agente,-8} {string.Join("+", item.Ferramentas),-32} {item.Status,-18} {item.Ms / 1000.0,5:0.0} s");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"ERRO: {ex.Message}");
                if (EvalRunner.IsSetupError(ex))
                {
                    Console.WriteLine("\nAvaliação interrompida: corrija o erro acima e rode de novo.");
                    return 1;
                }
                items.Add(new AgentEvalItem(q, "", [], "", false, null, "Erro", null, 0, ex.Message));
            }
        }

        var (summary, markdown) = BuildReport(items, providers);
        Directory.CreateDirectory(resultsDir);
        var file = Path.Combine(resultsDir, $"{DateTime.Now:yyyy-MM-dd_HH-mm}_agentes_{providers.Llm}.md".ToLowerInvariant());
        await File.WriteAllTextAsync(file, markdown);

        Console.WriteLine("\n" + summary);
        Console.WriteLine($"Relatório completo: {file}");
        return 0;
    }

    private static async Task<AgentEvalItem> EvaluateAsync(AgentEvalQuestion q, AgentRunner agents, IChatService judge)
    {
        var r = await agents.RunAsync(q.Pergunta, q.Historico);
        var tools = r.Steps.Select(s => s.Ferramenta).ToList();
        var routing = q.AgenteOk(r.Agente);
        bool? toolOk = q.Ferramenta is null ? null : tools.Contains(q.Ferramenta);
        var context = r.Context.Count == 0 ? "(nenhuma ferramenta foi usada)" : string.Join("\n\n---\n\n", r.Context);

        if (q.ForaDoEscopo)
        {
            var v = await EvalRunner.JudgeAsync(judge, $$"""
                A pergunta abaixo NÃO tem resposta nos documentos nem nos sistemas da loja. O certo é o assistente dizer que não encontrou a informação.

                Pergunta: {{q.Pergunta}}

                Resposta do assistente: {{r.Answer}}

                Devolva: {"recusou": true ou false, "motivo": "uma frase curta"}
                """);
            var ok = v.GetBool("recusou") == true;
            return new AgentEvalItem(q, r.Agente, tools, r.Answer, routing, toolOk, ok ? "Recusa correta" : "Inventou resposta",
                null, r.Ms, v.Motivo);
        }

        var verdict = await EvalRunner.JudgeAsync(judge, $$"""
            Pergunta: {{q.Pergunta}}

            Resposta esperada (gabarito): {{q.Esperado}}

            O que as ferramentas devolveram ao assistente:
            {{context}}

            Resposta do assistente: {{r.Answer}}

            Devolva: {"correta": true ou false, "fiel": true ou false, "motivo": "uma frase curta"}
            - "correta": true se a resposta traz a informação principal do gabarito sem contradizê-lo. Detalhes extras corretos não tiram ponto. Números de protocolo podem ser diferentes do exemplo.
            - "fiel": true se TUDO o que a resposta afirma está apoiado no que as ferramentas devolveram.
            """);
        var correta = verdict.GetBool("correta") == true;
        return new AgentEvalItem(q, r.Agente, tools, r.Answer, routing, toolOk, correta ? "Correta" : "Errada",
            verdict.GetBool("fiel"), r.Ms, verdict.Motivo);
    }

    private static (string Summary, string Markdown) BuildReport(List<AgentEvalItem> items, ServiceRegistration.Providers p)
    {
        var valid = items.Where(i => i.Status != "Erro").ToList();
        var answerable = valid.Where(i => !i.Q.ForaDoEscopo).ToList();
        var withTool = valid.Where(i => i.FerramentaOk is not null).ToList();
        var judged = answerable.Where(i => i.Fiel is not null).ToList();
        var outOfScope = valid.Where(i => i.Q.ForaDoEscopo).ToList();

        var rows = new List<(string, string, string)>
        {
            ("Roteamento", Pct(valid.Count(i => i.RoteamentoOk), valid.Count), "o roteador escolheu o agente certo"),
            ("Ferramenta certa", Pct(withTool.Count(i => i.FerramentaOk == true), withTool.Count), "o agente usou a ferramenta esperada"),
            ("Respostas corretas", Pct(answerable.Count(i => i.Status == "Correta"), answerable.Count), "a resposta bate com o gabarito"),
            ("Fidelidade", Pct(judged.Count(i => i.Fiel == true), judged.Count), "não inventou nada além do que as ferramentas trouxeram"),
            ("Recusa correta", Pct(outOfScope.Count(i => i.Status == "Recusa correta"), outOfScope.Count), "disse que não sabe quando não há resposta"),
            ("Passos por pergunta", valid.Count == 0 ? "—" : $"{valid.Average(i => i.Ferramentas.Count):0.0}", "chamadas de ferramenta, em média"),
            ("Tempo por pergunta", valid.Count == 0 ? "—" : $"{valid.Average(i => i.Ms) / 1000:0.0} s", "roteador + passos + resposta"),
        };

        var summary = new StringBuilder("RESULTADO (agentes)\n");
        foreach (var (m, v, _) in rows) summary.AppendLine($"  {m,-20} {v}");

        var md = new StringBuilder("# Avaliação dos agentes\n\n");
        md.AppendLine($"- **Data:** {DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR"))}");
        md.AppendLine($"- **LLM:** {p.Llm} ({p.LlmModel}) · também usado como juiz e roteador");
        md.AppendLine($"- **Embeddings:** {p.Embeddings} ({p.EmbeddingModel}) · **Banco vetorial:** {p.VectorStore}");
        md.AppendLine($"- **Perguntas:** {items.Count}\n");
        md.AppendLine("## Resumo\n\n| Métrica | Resultado | O que mede |\n|---|---|---|");
        foreach (var (m, v, d) in rows) md.AppendLine($"| {m} | **{v}** | {d} |");
        md.AppendLine("\n## Pergunta a pergunta\n");
        md.AppendLine("| Pergunta | Agente | Ferramentas | Resultado | Fiel | Tempo | Observação |\n|---|---|---|---|:-:|--:|---|");
        foreach (var i in items)
        {
            var agente = $"{i.Agente} {(i.RoteamentoOk ? "✅" : "❌")}";
            var ferr = (i.Ferramentas.Count == 0 ? "—" : string.Join(" → ", i.Ferramentas)) +
                       (i.FerramentaOk is null ? "" : i.FerramentaOk == true ? " ✅" : " ❌");
            md.AppendLine($"| {Cell(i.Q.Pergunta)} | {agente} | {ferr} | {i.Status} | {Mark(i.Fiel)} | {i.Ms / 1000.0:0.0} s | {Cell(i.Motivo)} |");
        }
        md.AppendLine("\n## Respostas completas\n");
        foreach (var i in items)
            md.AppendLine($"### {i.Q.Id}\n\n**Pergunta:** {i.Q.Pergunta}  \n**Gabarito:** {i.Q.Esperado ?? "(fora do escopo)"}  \n**Resposta:** {i.Resposta.Replace("\n", " ")}\n");

        return (summary.ToString(), md.ToString());
    }

    private static string Pct(int ok, int total) => total == 0 ? "—" : $"{100.0 * ok / total:0}% ({ok}/{total})";
    private static string Mark(bool? b) => b switch { true => "✅", false => "❌", null => "—" };
    private static string Cell(string s) => Regex.Replace(s.Replace("|", "\\|"), @"\s+", " ");
}

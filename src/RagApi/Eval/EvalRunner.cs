using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using RagApi.Rag;

namespace RagApi.Eval;

/// <summary>Uma pergunta do gabarito (eval/perguntas.json).</summary>
public sealed record EvalQuestion(
    [property: JsonPropertyName("id")] string Id,
    [property: JsonPropertyName("pergunta")] string Pergunta,
    [property: JsonPropertyName("fonte")] string? Fonte,
    [property: JsonPropertyName("esperado")] string? Esperado,
    [property: JsonPropertyName("historico")] List<ChatTurn>? Historico)
{
    /// <summary>Sem fonte = a resposta não está nos documentos; o certo é dizer "não encontrei".</summary>
    public bool Respondivel => Fonte is not null;
}

public sealed record EvalItem(
    EvalQuestion Q, string Resposta, string Buscado, bool? BuscaOk, string Status, bool? Correta, bool? Fiel,
    long Ms, string Motivo);

/// <summary>
/// Fase 4: mede a qualidade do RAG com um gabarito.
///   Busca      = o documento certo veio entre os trechos encontrados?
///   Correta    = a resposta bate com o gabarito? (a própria IA corrige: "LLM como juiz")
///   Fidelidade = tudo o que a resposta afirma está nos trechos? (não inventou)
///   Recusa     = nas perguntas sem resposta nos documentos, ele disse "não encontrei"?
/// Uso: dotnet run -- eval [--sem-juiz]
/// </summary>
public static class EvalRunner
{
    private const string JudgeSystemPrompt = """
        Você é um avaliador rigoroso e imparcial de um assistente que responde perguntas com base em documentos.
        Responda APENAS com um objeto JSON válido, sem texto antes ou depois e sem bloco de código.
        """;

    public static async Task<int> RunAsync(IServiceProvider services, ServiceRegistration.Providers providers, string[] args)
    {
        var useJudge = !args.Contains("--sem-juiz");
        var config = services.GetRequiredService<IConfiguration>();
        var ragOptions = services.GetRequiredService<IOptions<RagOptions>>().Value;

        var questionsPath = Path.GetFullPath(config["Eval:QuestionsPath"] ?? "eval/perguntas.json");
        var resultsDir = Path.GetFullPath(config["Eval:ResultsPath"] ?? "eval/resultados");
        var questions = JsonSerializer.Deserialize<List<EvalQuestion>>(await File.ReadAllTextAsync(questionsPath))
                        ?? throw new InvalidOperationException($"Gabarito vazio: {questionsPath}");

        Console.WriteLine($"LLM: {providers.Llm} ({providers.LlmModel}) · Embeddings: {providers.Embeddings} ({providers.EmbeddingModel}) · Banco: {providers.VectorStore}");
        Console.WriteLine($"{questions.Count} perguntas · TopK {ragOptions.TopK} · MinScore {ragOptions.MinScore}{(useJudge ? "" : " · sem juiz")}\n");

        using var scope = services.CreateScope();
        var rag = scope.ServiceProvider.GetRequiredService<RagService>();
        var judge = scope.ServiceProvider.GetRequiredService<IChatService>();

        Console.Write("Indexando documentos… ");
        var ingested = await rag.IngestFolderAsync();
        Console.WriteLine($"{ingested.Count} arquivos, {ingested.Sum(i => i.Chunks)} trechos.\n");

        var items = new List<EvalItem>();
        for (var i = 0; i < questions.Count; i++)
        {
            var q = questions[i];
            Console.Write($"[{i + 1,2}/{questions.Count}] {q.Id,-24} ");
            try
            {
                var item = await EvaluateAsync(q, rag, judge, useJudge);
                items.Add(item);
                Console.WriteLine($"{item.Status,-22} {item.Ms,6} ms");
            }
            catch (Exception ex)
            {
                items.Add(new EvalItem(q, "", "", null, "Erro", null, null, 0, ex.Message));
                Console.WriteLine($"ERRO: {ex.Message}");

                // Erro de configuração (login, executável, chave) se repete em todas: melhor parar já.
                if (IsSetupError(ex) || items.TakeLast(3).Count(x => x.Status == "Erro") == 3)
                {
                    Console.WriteLine("\nAvaliação interrompida: corrija o erro acima e rode de novo. Nenhum relatório foi gerado.");
                    return 1;
                }
            }
        }

        var report = BuildReport(items, providers, ragOptions, useJudge);
        Directory.CreateDirectory(resultsDir);
        var file = Path.Combine(resultsDir,
            $"{DateTime.Now:yyyy-MM-dd_HH-mm}_{providers.Llm}-{Sanitize(providers.LlmModel)}.md".ToLowerInvariant());
        await File.WriteAllTextAsync(file, report.Markdown);

        Console.WriteLine("\n" + report.Summary);
        Console.WriteLine($"Relatório completo: {file}");
        return items.Any(x => x.Status == "Erro") ? 1 : 0;
    }

    private static async Task<EvalItem> EvaluateAsync(EvalQuestion q, RagService rag, IChatService judge, bool useJudge)
    {
        var sw = Stopwatch.StartNew();
        var result = await rag.AskAsync(q.Pergunta, q.Historico);
        sw.Stop();

        var refused = result.Answer == RagService.NotFoundAnswer;

        if (!q.Respondivel)
        {
            if (refused)
                return new EvalItem(q, result.Answer, result.SearchQuery, null, "Recusa correta", null, null, sw.ElapsedMilliseconds,
                    "Nenhum trecho passou do score mínimo.");

            // Achou algum trecho e chamou a IA: ela precisa ter dito que não sabe.
            if (!useJudge)
                return new EvalItem(q, result.Answer, result.SearchQuery, null, "Respondeu (sem juiz)", null, null, sw.ElapsedMilliseconds, "");

            var verdict = await JudgeAsync(judge, $$"""
                A pergunta abaixo NÃO tem resposta nos documentos. O comportamento certo é o assistente dizer que não encontrou a informação.

                Pergunta: {{q.Pergunta}}

                Resposta do assistente: {{result.Answer}}

                Devolva: {"recusou": true ou false, "motivo": "uma frase curta"}
                "recusou" é true se o assistente deixou claro que não encontrou ou não sabe, sem inventar uma resposta.
                """);
            var ok = verdict.GetBool("recusou") == true;
            return new EvalItem(q, result.Answer, result.SearchQuery, null, ok ? "Recusa correta" : "Inventou resposta",
                null, null, sw.ElapsedMilliseconds, verdict.Motivo);
        }

        var buscaOk = result.Sources.Any(s => s.Source == q.Fonte);

        if (refused)
            return new EvalItem(q, result.Answer, result.SearchQuery, buscaOk, "Recusa indevida", false, null, sw.ElapsedMilliseconds,
                "Disse que não encontrou, mas a resposta está nos documentos.");

        if (!useJudge)
            return new EvalItem(q, result.Answer, result.SearchQuery, buscaOk, "Respondeu (sem juiz)", null, null, sw.ElapsedMilliseconds, "");

        var context = string.Join("\n\n", result.Context.Select((c, i) => $"[{i + 1}] {c}"));
        var v = await JudgeAsync(judge, $$"""
            Pergunta: {{q.Pergunta}}

            Resposta esperada (gabarito): {{q.Esperado}}

            Trechos que o assistente recebeu:
            {{context}}

            Resposta do assistente: {{result.Answer}}

            Devolva: {"correta": true ou false, "fiel": true ou false, "motivo": "uma frase curta"}
            - "correta": true se a resposta traz a informação principal do gabarito sem contradizê-lo. Detalhes extras corretos não tiram ponto.
            - "fiel": true se TUDO o que a resposta afirma está apoiado nos trechos. Qualquer informação inventada torna "fiel" false.
            """);

        var correta = v.GetBool("correta");
        return new EvalItem(q, result.Answer, result.SearchQuery, buscaOk, correta == true ? "Correta" : "Errada",
            correta, v.GetBool("fiel"), sw.ElapsedMilliseconds, v.Motivo);
    }

    private static bool IsSetupError(Exception ex) =>
        Regex.IsMatch(ex.Message, @"log ?in|/login|Não encontrei o Claude Code|não configurada|401|403|connection refused|recusou a conexão",
            RegexOptions.IgnoreCase);

    // ---------- Juiz ----------

    private sealed record Verdict(JsonElement? Root, string Motivo)
    {
        public bool? GetBool(string name) =>
            Root is { } r && r.TryGetProperty(name, out var p) && p.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? p.GetBoolean()
                : null;
    }

    private static async Task<Verdict> JudgeAsync(IChatService judge, string prompt)
    {
        var raw = await judge.CompleteAsync(JudgeSystemPrompt, [ChatTurn.User(prompt)]);

        // Modelos às vezes embrulham o JSON em ```json ... ```: pegamos o primeiro objeto.
        var match = Regex.Match(raw, @"\{.*\}", RegexOptions.Singleline);
        if (!match.Success) return new Verdict(null, "Juiz não devolveu JSON: " + raw);
        try
        {
            var root = JsonDocument.Parse(match.Value).RootElement.Clone();
            var motivo = root.TryGetProperty("motivo", out var m) ? m.GetString() ?? "" : "";
            return new Verdict(root, motivo);
        }
        catch (JsonException)
        {
            return new Verdict(null, "JSON inválido do juiz: " + raw);
        }
    }

    // ---------- Relatório ----------

    private sealed record Report(string Summary, string Markdown);

    private static Report BuildReport(List<EvalItem> items, ServiceRegistration.Providers p, RagOptions o, bool useJudge)
    {
        var answerable = items.Where(i => i.Q.Respondivel).ToList();
        var outOfScope = items.Where(i => !i.Q.Respondivel).ToList();
        var answered = answerable.Where(i => i.Fiel is not null).ToList();
        var timed = items.Where(i => i.Ms > 0).Select(i => i.Ms).OrderBy(x => x).ToList();

        var rows = new List<(string Metrica, string Valor, string Explicacao)>
        {
            ("Busca", Pct(answerable.Count(i => i.BuscaOk == true), answerable.Count),
                "o documento certo veio entre os trechos encontrados"),
        };
        if (useJudge)
        {
            rows.Add(("Respostas corretas", Pct(answerable.Count(i => i.Correta == true), answerable.Count),
                "a resposta bate com o gabarito"));
            rows.Add(("Fidelidade", Pct(answered.Count(i => i.Fiel == true), answered.Count),
                "não inventou nada além dos trechos"));
        }
        rows.Add(("Recusa correta", Pct(outOfScope.Count(i => i.Status == "Recusa correta"), outOfScope.Count),
            "disse \"não encontrei\" quando a resposta não existe"));
        rows.Add(("Recusa indevida", $"{answerable.Count(i => i.Status == "Recusa indevida")} de {answerable.Count}",
            "disse \"não encontrei\" mas a resposta existia"));
        if (timed.Count > 0)
            rows.Add(("Tempo por pergunta", $"{timed.Average() / 1000:0.0} s (máx. {timed[^1] / 1000.0:0.0} s)",
                "busca + IA (+ reescrita, quando há histórico)"));

        var summary = new StringBuilder("RESULTADO\n");
        foreach (var r in rows) summary.AppendLine($"  {r.Metrica,-20} {r.Valor}");

        var md = new StringBuilder();
        md.AppendLine("# Avaliação do RAG").AppendLine();
        md.AppendLine($"- **Data:** {DateTime.Now.ToString("dd/MM/yyyy HH:mm", CultureInfo.GetCultureInfo("pt-BR"))}");
        md.AppendLine($"- **LLM:** {p.Llm} ({p.LlmModel}){(useJudge ? " · também usado como juiz" : "")}");
        md.AppendLine($"- **Embeddings:** {p.Embeddings} ({p.EmbeddingModel})");
        md.AppendLine($"- **Banco vetorial:** {p.VectorStore}");
        md.AppendLine($"- **Parâmetros:** TopK {o.TopK} · MinScore {o.MinScore} · Chunk {o.ChunkSize}/{o.ChunkOverlap}");
        md.AppendLine($"- **Perguntas:** {items.Count} ({answerable.Count} com resposta nos documentos, {outOfScope.Count} sem)").AppendLine();

        md.AppendLine("## Resumo").AppendLine();
        md.AppendLine("| Métrica | Resultado | O que mede |").AppendLine("|---|---|---|");
        foreach (var r in rows) md.AppendLine($"| {r.Metrica} | **{r.Valor}** | {r.Explicacao} |");
        md.AppendLine();

        md.AppendLine("## Pergunta a pergunta").AppendLine();
        md.AppendLine("| Pergunta | Busca | Resultado | Fiel | Tempo | Observação |").AppendLine("|---|:-:|---|:-:|--:|---|");
        foreach (var i in items)
        {
            var pergunta = i.Q.Historico is { Count: > 0 } ? $"{i.Q.Pergunta} _(com histórico)_" : i.Q.Pergunta;
            md.AppendLine($"| {Cell(pergunta)} | {Mark(i.BuscaOk)} | {i.Status} | {Mark(i.Fiel)} | {i.Ms / 1000.0:0.0} s | {Cell(i.Motivo)} |");
        }
        md.AppendLine();

        md.AppendLine("## Respostas completas").AppendLine();
        foreach (var i in items)
        {
            md.AppendLine($"### {i.Q.Id}").AppendLine();
            md.AppendLine($"**Pergunta:** {i.Q.Pergunta}  ");
            if (i.Buscado != i.Q.Pergunta && i.Buscado.Length > 0) md.AppendLine($"**Buscado como:** {i.Buscado}  ");
            if (i.Q.Esperado is not null) md.AppendLine($"**Gabarito:** {i.Q.Esperado}  ");
            md.AppendLine($"**Resposta:** {i.Resposta.Replace("\n", " ")}").AppendLine();
        }

        return new Report(summary.ToString(), md.ToString());
    }

    private static string Pct(int ok, int total) =>
        total == 0 ? "—" : $"{100.0 * ok / total:0}% ({ok}/{total})";

    private static string Mark(bool? b) => b switch { true => "✅", false => "❌", null => "—" };

    private static string Cell(string s) => s.Replace("|", "\\|").Replace("\n", " ");

    private static string Sanitize(string s) => Regex.Replace(s, @"[^\w\-\.]", "-");
}

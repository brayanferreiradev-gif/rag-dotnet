using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using RagApi.Rag;

namespace RagApi.Agents;

/// <summary>Resultado de uma ferramenta: o texto que volta para o LLM e, quando houver, as fontes usadas.</summary>
public sealed record ToolResult(string Observation, IReadOnlyList<SourceRef>? Sources = null);

/// <summary>
/// Uma ferramenta que o agente pode chamar. O LLM vê só o nome, a descrição e o formato
/// dos argumentos; quem executa é o nosso código.
/// </summary>
public interface IAgentTool
{
    string Name { get; }
    string Description { get; }
    string ArgsExample { get; }
    Task<ToolResult> RunAsync(JsonElement args, Skill skill, CancellationToken ct);
}

// ---------- 1. Busca nos documentos (o RAG virou uma ferramenta) ----------

public sealed class BuscarDocumentosTool(RagService rag) : IAgentTool
{
    public string Name => "buscar_documentos";
    public string Description => "Busca trechos nos documentos do seu departamento. Use para regras, prazos e procedimentos.";
    public string ArgsExample => """{"consulta": "prazo para troca de eletrônicos"}""";

    public async Task<ToolResult> RunAsync(JsonElement args, Skill skill, CancellationToken ct)
    {
        var query = args.Str("consulta") ?? args.Str("query") ?? "";
        if (query.Length == 0) return new ToolResult("Erro: informe \"consulta\".");

        // Cada agente só enxerga os documentos do próprio departamento.
        var hits = await rag.RetrieveAsync(query, skill.Documentos.Count > 0 ? skill.Documentos : null, ct);
        if (hits.Count == 0) return new ToolResult("Nenhum trecho relevante encontrado nos documentos do departamento.");

        var sb = new StringBuilder();
        var sources = new List<SourceRef>();
        for (var i = 0; i < hits.Count; i++)
        {
            sb.Append($"[{i + 1}] (fonte: {hits[i].Source})\n{hits[i].Text}\n\n");
            sources.Add(new SourceRef(i + 1, hits[i].Source, hits[i].ChunkIndex, Math.Round(hits[i].Score, 3),
                hits[i].Text.Length <= 160 ? hits[i].Text : hits[i].Text[..160] + "…"));
        }
        return new ToolResult(sb.ToString().Trim(), sources);
    }
}

// ---------- 2. Estoque ----------

public sealed class ConsultarEstoqueTool(StoreData data) : IAgentTool
{
    public string Name => "consultar_estoque";
    public string Description => "Consulta um produto pelo nome ou SKU: preço, quantidade em cada loja e previsão de reposição.";
    public string ArgsExample => """{"produto": "fone bluetooth"}""";

    public Task<ToolResult> RunAsync(JsonElement args, Skill skill, CancellationToken ct)
    {
        var query = args.Str("produto") ?? args.Str("sku") ?? "";
        var found = data.FindProducts(query);
        if (found.Count == 0)
            return Task.FromResult(new ToolResult($"Nenhum produto encontrado para \"{query}\"."));

        var lines = found.Take(3).Select(p =>
        {
            var lojas = string.Join(", ", p.Estoque.Select(e => $"{e.Key}: {e.Value}"));
            var total = p.Estoque.Values.Sum();
            var reposicao = total == 0 && p.PrevisaoReposicao is not null ? $" · previsão de reposição: {p.PrevisaoReposicao}" : "";
            return $"{p.Sku} · {p.Nome} · R$ {p.Preco.ToString("0.00", CultureInfo.GetCultureInfo("pt-BR"))} · " +
                   $"estoque por loja: {lojas} (total {total}){reposicao}";
        });
        return Task.FromResult(new ToolResult(string.Join("\n", lines)));
    }
}

// ---------- 3. Pedidos ----------

public sealed class ConsultarPedidoTool(StoreData data) : IAgentTool
{
    public string Name => "consultar_pedido";
    public string Description => "Consulta um pedido de cliente pelo número: itens, pagamento, status e previsão de entrega.";
    public string ArgsExample => """{"numero": "10234"}""";

    public Task<ToolResult> RunAsync(JsonElement args, Skill skill, CancellationToken ct)
    {
        var numero = new string((args.Str("numero") ?? "").Where(char.IsDigit).ToArray());
        var pedido = data.Pedidos.FirstOrDefault(p => p.Numero == numero);
        return Task.FromResult(new ToolResult(pedido is null
            ? $"Pedido \"{numero}\" não encontrado."
            : pedido.Dados.GetRawText()));
    }
}

// ---------- 4. Chamados (a ferramenta que "faz" algo) ----------

public sealed class AbrirChamadoTool(StoreData data) : IAgentTool
{
    public string Name => "abrir_chamado";
    public string Description => "Registra um chamado (assistência técnica ou suporte) e devolve o número de protocolo.";
    public string ArgsExample => """{"titulo": "Maquininha do caixa 3 com erro", "descricao": "...", "prioridade": "alta"}""";

    public Task<ToolResult> RunAsync(JsonElement args, Skill skill, CancellationToken ct)
    {
        var titulo = args.Str("titulo") ?? "";
        if (titulo.Length == 0) return Task.FromResult(new ToolResult("Erro: informe \"titulo\"."));

        var chamado = data.OpenTicket(skill.Nome, titulo, args.Str("descricao") ?? "", args.Str("prioridade") ?? "normal");
        return Task.FromResult(new ToolResult(
            $"Chamado aberto. Protocolo {chamado.Protocolo} · setor {chamado.Setor} · prioridade {chamado.Prioridade}."));
    }
}

// ---------- Dados fictícios da loja ----------

public sealed record Produto(
    [property: JsonPropertyName("sku")] string Sku,
    [property: JsonPropertyName("nome")] string Nome,
    [property: JsonPropertyName("categoria")] string Categoria,
    [property: JsonPropertyName("preco")] decimal Preco,
    [property: JsonPropertyName("estoque")] Dictionary<string, int> Estoque,
    [property: JsonPropertyName("previsao_reposicao")] string? PrevisaoReposicao = null);

public sealed record Chamado(string Protocolo, string Setor, string Titulo, string Descricao, string Prioridade, DateTime AbertoEm);

/// <summary>
/// "Sistema da loja" de mentira: estoque e pedidos vêm de data/*.json e os chamados ficam
/// em memória. Num projeto real seriam consultas ao ERP; para o agente não faz diferença.
/// </summary>
public sealed class StoreData
{
    public IReadOnlyList<Produto> Produtos { get; }
    public IReadOnlyList<PedidoView> Pedidos { get; }
    private readonly ConcurrentQueue<Chamado> _chamados = new();
    private int _seq;

    public sealed record PedidoView(string Numero, JsonElement Dados);

    public StoreData(IOptions<AgentOptions> options)
    {
        var dir = Path.GetFullPath(options.Value.DataPath);
        Produtos = JsonSerializer.Deserialize<List<Produto>>(File.ReadAllText(Path.Combine(dir, "estoque.json"))) ?? [];
        var pedidos = JsonSerializer.Deserialize<List<JsonElement>>(File.ReadAllText(Path.Combine(dir, "pedidos.json"))) ?? [];
        Pedidos = pedidos.Select(p => new PedidoView(p.GetProperty("numero").GetString() ?? "", p)).ToList();
    }

    public IReadOnlyCollection<Chamado> Chamados => _chamados;

    public Chamado OpenTicket(string setor, string titulo, string descricao, string prioridade)
    {
        var n = Interlocked.Increment(ref _seq);
        var chamado = new Chamado($"CH-{DateTime.Now:yyMMdd}-{n:000}", setor, titulo, descricao, prioridade, DateTime.Now);
        _chamados.Enqueue(chamado);
        return chamado;
    }

    /// <summary>Busca simples por palavras (sem acento, sem caixa): SKU exato ou mais palavras em comum.</summary>
    public IReadOnlyList<Produto> FindProducts(string query)
    {
        var q = Normalize(query);
        var exact = Produtos.Where(p => Normalize(p.Sku) == q).ToList();
        if (exact.Count > 0) return exact;

        var words = q.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length >= 3).ToArray();
        return Produtos
            .Select(p => (p, score: words.Count(w => Normalize(p.Nome + " " + p.Categoria).Contains(w))))
            .Where(x => x.score > 0)
            .OrderByDescending(x => x.score)
            .Select(x => x.p)
            .ToList();
    }

    private static string Normalize(string s)
    {
        var formD = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
        return new string(formD.Where(c => CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark).ToArray());
    }
}

internal static class JsonArgs
{
    public static string? Str(this JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v)
            ? v.ValueKind == JsonValueKind.String ? v.GetString()?.Trim() : v.ToString()
            : null;
}

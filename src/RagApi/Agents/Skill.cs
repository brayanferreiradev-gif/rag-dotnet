using Microsoft.Extensions.Options;

namespace RagApi.Agents;

public sealed class AgentOptions
{
    /// <summary>Pasta com uma skill por arquivo .md (frontmatter + instruções).</summary>
    public string SkillsPath { get; set; } = "skills";

    /// <summary>Pasta com os dados fictícios da loja (estoque.json, pedidos.json).</summary>
    public string DataPath { get; set; } = "data";

    /// <summary>Limite de passos do loop (pensar → ferramenta → observar) antes de forçar a resposta.</summary>
    public int MaxSteps { get; set; } = 4;
}

/// <summary>
/// Uma skill = um agente especialista. Fica num arquivo Markdown, no mesmo espírito das
/// Agent Skills: um cabeçalho dizendo o que ela faz (o roteador lê só isso) e o corpo com
/// as instruções (só o agente escolhido lê). Exemplo:
///
///   ---
///   nome: sac
///   titulo: SAC · Atendimento ao cliente
///   descricao: Trocas, devoluções...
///   documentos: politica-de-trocas.md, garantia-e-assistencia.md
///   ferramentas: buscar_documentos, consultar_pedido
///   ---
///   Você é o agente de SAC...
/// </summary>
public sealed record Skill(
    string Nome, string Titulo, string Descricao,
    IReadOnlyList<string> Documentos, IReadOnlyList<string> Ferramentas, string Instrucoes)
{
    public static Skill Parse(string markdown, string fileName)
    {
        var text = markdown.Replace("\r\n", "\n").TrimStart();
        if (!text.StartsWith("---\n"))
            throw new FormatException($"{fileName}: a skill precisa começar com um cabeçalho entre '---'.");

        var end = text.IndexOf("\n---", 4, StringComparison.Ordinal);
        if (end < 0) throw new FormatException($"{fileName}: cabeçalho sem o '---' de fechamento.");

        var header = text[4..end]
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Split(':', 2))
            .Where(p => p.Length == 2)
            .ToDictionary(p => p[0].Trim().ToLowerInvariant(), p => p[1].Trim());

        string Get(string key) => header.TryGetValue(key, out var v) && v.Length > 0
            ? v
            : throw new FormatException($"{fileName}: campo '{key}' obrigatório no cabeçalho.");

        static List<string> List(string? v) =>
            (v ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

        return new Skill(
            Nome: Get("nome"),
            Titulo: header.GetValueOrDefault("titulo") ?? Get("nome"),
            Descricao: Get("descricao"),
            Documentos: List(header.GetValueOrDefault("documentos")),
            Ferramentas: List(header.GetValueOrDefault("ferramentas")),
            Instrucoes: text[(end + 4)..].Trim());
    }
}

/// <summary>Carrega as skills da pasta uma vez e as mantém em memória.</summary>
public sealed class SkillLibrary
{
    public IReadOnlyList<Skill> Skills { get; }

    public SkillLibrary(IOptions<AgentOptions> options)
    {
        var dir = Path.GetFullPath(options.Value.SkillsPath);
        if (!Directory.Exists(dir))
            throw new DirectoryNotFoundException($"Pasta de skills não encontrada: {dir}");

        Skills = Directory.EnumerateFiles(dir, "*.md")
            .Order()
            .Select(f => Skill.Parse(File.ReadAllText(f), Path.GetFileName(f)))
            .ToList();

        if (Skills.Count == 0) throw new InvalidOperationException($"Nenhuma skill em {dir}");
    }

    public Skill? Find(string? nome) =>
        Skills.FirstOrDefault(s => s.Nome.Equals(nome?.Trim(), StringComparison.OrdinalIgnoreCase));
}

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace RagApi.Ai;

public sealed class ClaudeCodeOptions
{
    /// <summary>
    /// Executável do Claude Code. Com o instalador nativo é "claude".
    /// Se instalou pelo npm no Windows, use "claude.cmd".
    /// </summary>
    public string Command { get; set; } = "claude";

    /// <summary>Apelido do modelo: haiku (rápido), sonnet ou opus.</summary>
    public string Model { get; set; } = "haiku";

    public int TimeoutSeconds { get; set; } = 180;
}

/// <summary>
/// Usa o Claude Code instalado na máquina (modo não interativo, "claude -p") como LLM,
/// com a conta já logada nele, sem chave de API. Pensado para testes locais e para
/// coletar dados de avaliação; para produção, o caminho é a API (ClaudeChatService).
///
/// O Claude Code roda "pelado": sem ferramentas, sem MCP, sem skills e sem salvar sessão,
/// só com o nosso prompt de sistema. Assim ele se comporta como um LLM comum.
/// </summary>
public sealed class ClaudeCodeChatService(IOptions<ClaudeCodeOptions> options, ILogger<ClaudeCodeChatService> logger)
    : IChatService
{
    private readonly ClaudeCodeOptions _opt = options.Value;

    public async Task<string> CompleteAsync(string systemPrompt, IReadOnlyList<ChatTurn> messages, CancellationToken ct = default)
    {
        var psi = new ProcessStartInfo(_opt.Command)
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            StandardInputEncoding = new UTF8Encoding(false),
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            // Pasta neutra: o Claude Code não carrega CLAUDE.md nem configurações do projeto.
            WorkingDirectory = Path.GetTempPath(),
        };
        foreach (var arg in new[]
                 {
                     "-p",
                     "--output-format", "json",
                     "--model", _opt.Model,
                     "--system-prompt", systemPrompt,
                     "--tools", "",
                     "--strict-mcp-config",
                     "--disable-slash-commands",
                     "--no-session-persistence",
                     "--max-turns", "1",
                 })
            psi.ArgumentList.Add(arg);

        using var process = new Process { StartInfo = psi };
        try
        {
            process.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or FileNotFoundException)
        {
            throw new InvalidOperationException(
                $"Não encontrei o Claude Code ('{_opt.Command}'). Instale e faça login com 'claude', " +
                "ou ajuste ClaudeCode:Command (no Windows via npm: claude.cmd).", ex);
        }

        // A conversa vai pelo stdin: evita limite de tamanho e problemas de aspas na linha de comando.
        await process.StandardInput.WriteAsync(RenderConversation(messages));
        process.StandardInput.Close();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(_opt.TimeoutSeconds));

        var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            process.Kill(entireProcessTree: true);
            throw new TimeoutException($"O Claude Code não respondeu em {_opt.TimeoutSeconds}s.");
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        return ParseResult(stdout, stderr, process.ExitCode);
    }

    /// <summary>Uma mensagem só vai como está; com histórico, vira uma transcrição.</summary>
    internal static string RenderConversation(IReadOnlyList<ChatTurn> messages)
    {
        if (messages.Count == 1) return messages[0].Content;

        var sb = new StringBuilder("Conversa até agora:\n\n");
        foreach (var m in messages.Take(messages.Count - 1))
            sb.Append(m.Role == "user" ? "Usuário: " : "Assistente: ").Append(m.Content).Append("\n\n");
        sb.Append("---\n\nMensagem atual do usuário:\n\n").Append(messages[^1].Content);
        return sb.ToString();
    }

    /// <summary>Lê o JSON de "claude -p --output-format json": { "result": "...", "is_error": false, ... }.</summary>
    internal string ParseResult(string stdout, string stderr, int exitCode)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(stdout);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException(
                $"Saída inesperada do Claude Code (código {exitCode}). " +
                $"Você já fez login rodando 'claude' uma vez? Detalhes: {Trim(stderr + " " + stdout)}");
        }

        using (doc)
        {
            var root = doc.RootElement;
            var isError = root.TryGetProperty("is_error", out var e) && e.ValueKind == JsonValueKind.True;
            var result = root.TryGetProperty("result", out var r) ? r.GetString() : null;

            if (isError || string.IsNullOrWhiteSpace(result))
            {
                var detail = Trim(result ?? stdout);
                if (detail.Contains("logged in", StringComparison.OrdinalIgnoreCase) || detail.Contains("/login"))
                    throw new InvalidOperationException(
                        "O Claude Code não está logado. Abra um terminal, rode 'claude', digite /login e entre com sua conta.");
                throw new InvalidOperationException($"O Claude Code retornou erro: {detail}");
            }

            if (root.TryGetProperty("total_cost_usd", out var cost) && cost.ValueKind == JsonValueKind.Number)
                logger.LogDebug("Claude Code: custo equivalente US$ {Cost}", cost.GetDouble());

            return result.Trim();
        }
    }

    private static string Trim(string s) => s.Length <= 400 ? s.Trim() : s[..400].Trim() + "…";
}

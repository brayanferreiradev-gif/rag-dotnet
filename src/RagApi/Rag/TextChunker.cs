using System.Text;
using System.Text.RegularExpressions;

namespace RagApi.Rag;

/// <summary>
/// Quebra um texto em pedaços (chunks) de até ~maxChars, respeitando parágrafos.
/// Cada chunk começa com o final do anterior (overlap) para não perder o contexto
/// de uma ideia que ficou dividida entre dois pedaços.
/// </summary>
public static partial class TextChunker
{
    public static IReadOnlyList<string> Split(string text, int maxChars = 800, int overlapChars = 150)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxChars, 100);
        ArgumentOutOfRangeException.ThrowIfNegative(overlapChars);
        if (overlapChars >= maxChars / 2)
            throw new ArgumentException("overlapChars deve ser menor que metade de maxChars.", nameof(overlapChars));

        var pieces = ParagraphBreak()
            .Split(text.Replace("\r\n", "\n"))
            .Select(p => p.Trim())
            .Where(p => p.Length > 0)
            .SelectMany(p => p.Length <= maxChars ? [p] : HardSplit(p, maxChars));

        var chunks = new List<string>();
        var current = new StringBuilder();
        var currentHasNewContent = false;

        foreach (var piece in pieces)
        {
            if (currentHasNewContent && current.Length + piece.Length > maxChars)
            {
                var done = current.ToString().Trim();
                chunks.Add(done);
                current.Clear();
                currentHasNewContent = false;

                if (overlapChars > 0)
                    current.Append(Tail(done, overlapChars)).Append("\n\n");
            }

            current.Append(piece).Append("\n\n");
            currentHasNewContent = true;
        }

        if (currentHasNewContent)
            chunks.Add(current.ToString().Trim());

        return chunks;
    }

    /// <summary>Divide um parágrafo grande em pedaços, cortando em espaços.</summary>
    private static IEnumerable<string> HardSplit(string paragraph, int maxChars)
    {
        var start = 0;
        while (start < paragraph.Length)
        {
            var length = Math.Min(maxChars, paragraph.Length - start);
            if (start + length < paragraph.Length)
            {
                var lastSpace = paragraph.LastIndexOf(' ', start + length - 1, length);
                if (lastSpace > start) length = lastSpace - start;
            }

            yield return paragraph.Substring(start, length).Trim();
            start += length;
        }
    }

    /// <summary>Últimos ~n caracteres, começando numa palavra inteira.</summary>
    private static string Tail(string text, int n)
    {
        if (text.Length <= n) return text;
        var start = text.Length - n;
        var space = text.IndexOfAny([' ', '\n'], start);
        return (space >= 0 ? text[(space + 1)..] : text[start..]).Trim();
    }

    [GeneratedRegex(@"\n\s*\n")]
    private static partial Regex ParagraphBreak();
}

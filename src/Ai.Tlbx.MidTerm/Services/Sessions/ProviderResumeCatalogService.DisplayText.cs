using System.Text;
using System.Text.RegularExpressions;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed partial class ProviderResumeCatalogService
{
    // Catalog labels are plain text. Keep the real prompt/transcript untouched.
    // Prefer prose over a leading rich table, retaining readable table text as
    // the fallback for table-only prompts and as part of the latest preview.
    private static string? ReadRichDisplayText(string? text, bool preferProse)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        using var lines = new StringReader(text);
        var fallback = new StringBuilder();
        while (lines.ReadLine() is { } rawLine)
        {
            var line = rawLine.Trim();
            if (line.StartsWith("```", StringComparison.Ordinal) ||
                line.StartsWith("~~~", StringComparison.Ordinal) ||
                TableDividerPattern().IsMatch(line)) continue;

            var tableRow = line.StartsWith('|') && line.EndsWith('|');
            line = ImageMarkerPattern().Replace(line, " ");
            line = MarkdownLinkPattern().Replace(line, "$1");
            line = MarkdownMarkPattern().Replace(line, "$1$2$3");
            line = MarkdownLinePrefixPattern().Replace(line, string.Empty).Trim();
            if (tableRow)
            {
                var cells = new StringBuilder();
                var row = line.Trim('|');
                for (var index = 0; index < row.Length; index++)
                {
                    if (row[index] == '\\' && index + 1 < row.Length && row[index + 1] == '|')
                    {
                        cells.Append('|');
                        index++;
                    }
                    else if (row[index] == '|') cells.Append(" · ");
                    else cells.Append(row[index]);
                }
                line = cells.ToString().Trim();
            }
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (preferProse && !tableRow) return line;
            if (fallback.Length > 0) fallback.Append(' ');
            fallback.Append(line);
            // Only the bounded display prefix is needed; keep scanning for a
            // prose title after leading tables without retaining their bodies.
            if (fallback.Length > PreviewLength * 2)
            {
                fallback.Length = PreviewLength * 2;
                if (!preferProse) break;
            }
        }
        return fallback.Length == 0 ? null : fallback.ToString();
    }

    [GeneratedRegex(@"^\|?[\s:|-]*-[\s:|-]*\|?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 1000)]
    private static partial Regex TableDividerPattern();

    [GeneratedRegex(@"!\[[^\]\r\n]*\]\([^\r\n)]*\)|\[Image(?:\s+#?\d+|\s+attachment)?\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 1000)]
    private static partial Regex ImageMarkerPattern();

    [GeneratedRegex(@"\[([^\]\r\n]+)\]\([^\r\n)]*\)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 1000)]
    private static partial Regex MarkdownLinkPattern();

    [GeneratedRegex(@"\*\*([^*\r\n]+)\*\*|~~([^~\r\n]+)~~|`([^`\r\n]+)`", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 1000)]
    private static partial Regex MarkdownMarkPattern();

    [GeneratedRegex(@"^(?:#{1,6}\s+|>\s?|[-+*]\s+|\d+[.)]\s+)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 1000)]
    private static partial Regex MarkdownLinePrefixPattern();
}
using System.Text;
using System.Text.RegularExpressions;
using Ai.Tlbx.MidTerm.Common.Text;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed partial class ProviderResumeCatalogService
{
    // Same escapable set as the browser's rich history renderer (utils/markdown.ts).
    private const string MarkdownEscapable = "\\`*{}[]()#+-.!_|<>~";

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

            var tableRow = line.StartsWith('|', StringComparison.Ordinal) && line.EndsWith('|', StringComparison.Ordinal);
            line = ImageMarkerPattern().Replace(line, " ");
            line = MarkdownLinkPattern().Replace(line, "$1");
            line = MarkdownLinePrefixPattern().Replace(line, string.Empty).Trim();
            line = tableRow ? ReadTableRowText(line) : PlainMarkdownText(line).Trim();
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (preferProse && !tableRow) return line;
            if (fallback.Length > 0) fallback.Append(' ');
            fallback.Append(line);
            // Only the bounded display prefix is needed; keep scanning for a
            // prose title after leading tables without retaining their bodies.
            if (fallback.Length > PreviewLength * 2)
            {
                Utf16Text.Truncate(fallback, PreviewLength * 2);
                if (!preferProse) break;
            }
        }
        return fallback.Length == 0 ? null : fallback.ToString();
    }

    private static string ReadTableRowText(string row)
    {
        // GFM removes a delimiter's escaping backslash before inline parsing, including inside code.
        var cells = new List<string>();
        var cell = new StringBuilder();
        var body = row.Trim('|');
        for (var index = 0; index < body.Length; index++)
        {
            if (body[index] == '\\' && index + 1 < body.Length)
            {
                if (body[index + 1] == '|') cell.Append('|');
                else cell.Append(body, index, 2);
                index++;
            }
            else if (body[index] == '|')
            {
                cells.Add(PlainMarkdownText(cell.ToString(), tableCell: true).Trim());
                cell.Clear();
            }
            else cell.Append(body[index]);
        }
        cells.Add(PlainMarkdownText(cell.ToString(), tableCell: true).Trim());
        return string.Join(" · ", cells).Trim();
    }

    /// <summary>
    /// Removes Markdown escapes and paired strong/strike delimiters. Code spans and raw
    /// Windows drive or UNC paths stay literal, so unescaped backslashes are never lost.
    /// </summary>
    internal static string PlainMarkdownText(string line, bool tableCell = false)
    {
        var output = new StringBuilder(line.Length);
        var strong = new List<int>();
        var strike = new List<int>();
        var index = 0;
        while (index < line.Length)
        {
            var character = line[index];
            if (character == '`')
            {
                var run = CountRun(line, index, '`');
                var close = FindClosingBacktickRun(line, index + run, run);
                if (close >= 0)
                {
                    output.Append(line, index + run, close - index - run);
                    index = close + run;
                }
                else
                {
                    output.Append(line, index, run);
                    index += run;
                }
                continue;
            }

            if (RawPathEnd(line, index) is { } end)
            {
                output.Append(line, index, end - index);
                index = end;
                continue;
            }

            if (character == '\\' && index + 1 < line.Length && MarkdownEscapable.Contains(line[index + 1], StringComparison.Ordinal))
            {
                output.Append(line[index + 1]);
                index += 2;
                continue;
            }

            if (tableCell && character == '<' && TableBreakEnd(line, index) is { } breakEnd)
            {
                output.Append(' ');
                index = breakEnd;
                continue;
            }

            if (character is ('*' or '~') && index + 1 < line.Length && line[index + 1] == character)
            {
                (character == '*' ? strong : strike).Add(output.Length);
                output.Append(character, 2);
                index += 2;
                continue;
            }

            output.Append(character);
            index++;
        }

        var removals = (strong.Count % 2 == 0 ? strong : []).Concat(strike.Count % 2 == 0 ? strike : [])
            .OrderDescending();
        foreach (var position in removals) output.Remove(position, 2);
        return output.ToString();
    }

    private static int? TableBreakEnd(string text, int start)
    {
        if (!text.AsSpan(start).StartsWith("<br", StringComparison.OrdinalIgnoreCase)) return null;
        var end = start + 3;
        while (end < text.Length && text[end] is ' ' or '\t') end++;
        if (end < text.Length && text[end] == '/') end++;
        return end < text.Length && text[end] == '>' ? end + 1 : null;
    }

    private static int CountRun(string text, int start, char character)
    {
        var end = start;
        while (end < text.Length && text[end] == character) end++;
        return end - start;
    }

    private static int FindClosingBacktickRun(string text, int start, int length)
    {
        for (var index = start; index < text.Length;)
        {
            if (text[index] != '`')
            {
                index++;
                continue;
            }

            var run = CountRun(text, index, '`');
            if (run == length) return index;
            index += run;
        }
        return -1;
    }

    private static int? RawPathEnd(string text, int index)
    {
        var previous = index > 0 ? text[index - 1] : ' ';
        if (char.IsLetterOrDigit(previous) || previous is '_' or '\\') return null;
        int start;
        if (index + 3 < text.Length && char.IsAsciiLetter(text[index]) && text[index + 1] == ':' &&
            text[index + 2] == '\\' && text[index + 3] != '\\')
        {
            start = index + 3;
        }
        else if (index + 2 < text.Length && text[index] == '\\' && text[index + 1] == '\\' &&
                 char.IsAsciiLetterOrDigit(text[index + 2]) &&
                 (char.IsWhiteSpace(previous) || previous is '(' or '[' or '<' or '"' or '\''))
        {
            start = index + 2;
        }
        else return null;

        var end = start;
        while (end < text.Length && !char.IsWhiteSpace(text[end]) && text[end] is not ('<' or '>' or '"' or '|' or '?' or '*' or '`')) end++;
        return end > start ? end : null;
    }

    [GeneratedRegex(@"^\|?[\s:|-]*-[\s:|-]*\|?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 1000)]
    private static partial Regex TableDividerPattern();

    [GeneratedRegex(@"!\[[^\]\r\n]*\]\([^\r\n)]*\)|\[Image(?:\s+#?\d+|\s+attachment)?\]", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 1000)]
    private static partial Regex ImageMarkerPattern();

    [GeneratedRegex(@"\[([^\]\r\n]+)\]\([^\r\n)]*\)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 1000)]
    private static partial Regex MarkdownLinkPattern();

    [GeneratedRegex(@"^(?:#{1,6}\s+|>\s?|[-+*]\s+|\d+[.)]\s+)", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking, 1000)]
    private static partial Regex MarkdownLinePrefixPattern();
}

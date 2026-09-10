using System.Text;
using System.Text.RegularExpressions;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public static partial class TerminalOutputSanitizer
{
    [GeneratedRegex(@"\x1B(?:\][^\x07]*(?:\x07|\x1B\\)|\[[0-?]*[ -/]*[@-~]|\([AB012]|[@-Z\\-_])", RegexOptions.None, 1000)]
    private static partial Regex AnsiEscapePattern();

    public static string Decode(ReadOnlySpan<byte> buffer)
    {
        return Encoding.UTF8.GetString(buffer);
    }

    public static string StripEscapeSequences(string text)
    {
        var withoutEscapes = AnsiEscapePattern().Replace(text, "");
        var sb = new StringBuilder(withoutEscapes.Length);
        var currentLineStart = 0;

        for (var i = 0; i < withoutEscapes.Length; i++)
        {
            var c = withoutEscapes[i];

            if (c == '\r')
            {
                if (i + 1 < withoutEscapes.Length && withoutEscapes[i + 1] == '\n')
                {
                    sb.Append('\n');
                    currentLineStart = sb.Length;
                    i++;
                }
                else
                {
                    sb.Length = currentLineStart;
                }

                continue;
            }

            if (c == '\n')
            {
                sb.Append(c);
                currentLineStart = sb.Length;
                continue;
            }

            if (c == '\t')
            {
                sb.Append(c);
                continue;
            }

            if (!char.IsControl(c))
            {
                sb.Append(c);
            }
        }

        return sb.ToString();
    }

    public static string NormalizeLineEndings(string text)
    {
        return text.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n');
    }

    public static string TailLines(string text, int maxLines, out int totalLines, out int returnedLines)
    {
        var normalized = CollapseBlankLineRuns(NormalizeLineEndings(text), maxBlankLines: 1)
            .Trim('\n');
        var lines = normalized.Split('\n');

        totalLines = lines.Length;

        if (maxLines <= 0)
        {
            maxLines = 1;
        }

        var start = Math.Max(0, lines.Length - maxLines);
        returnedLines = lines.Length - start;
        return string.Join('\n', lines[start..]);
    }

    private static string CollapseBlankLineRuns(string text, int maxBlankLines)
    {
        if (string.IsNullOrEmpty(text))
        {
            return text;
        }

        maxBlankLines = Math.Max(0, maxBlankLines);
        var lines = text.Split('\n');
        var sb = new StringBuilder(text.Length);
        var blankRun = 0;

        foreach (var line in lines)
        {
            var isBlank = string.IsNullOrWhiteSpace(line);
            if (isBlank)
            {
                blankRun++;
                if (blankRun > maxBlankLines)
                {
                    continue;
                }
            }
            else
            {
                blankRun = 0;
            }

            if (sb.Length > 0)
            {
                sb.Append('\n');
            }

            sb.Append(line);
        }

        return sb.ToString();
    }

    public static int CountBellEvents(ReadOnlySpan<byte> data)
    {
        var count = 0;
        var inOsc = false;

        for (var i = 0; i < data.Length; i++)
        {
            var current = data[i];

            if (!inOsc &&
                current == 0x1B &&
                i + 1 < data.Length &&
                data[i + 1] == 0x5D)
            {
                inOsc = true;
                i++;
                continue;
            }

            if (inOsc)
            {
                if (current == 0x07)
                {
                    inOsc = false;
                    continue;
                }

                if (current == 0x1B &&
                    i + 1 < data.Length &&
                    data[i + 1] == 0x5C)
                {
                    inOsc = false;
                    i++;
                }

                continue;
            }

            if (current == 0x07)
            {
                count++;
            }
        }

        return count;
    }

}

using System.Text;
using System.Text.RegularExpressions;
using Acornima;
using Acornima.Ast;

namespace Ai.Tlbx.MidTerm.Services.WebPreview;

/// <summary>
/// Keeps application Location access in upstream coordinates. Parse JavaScript rather
/// than replacing text so strings, comments, regexes and property names stay intact.
/// The runtime adapter checks object identity, preserving locally shadowed variables.
/// </summary>
internal static partial class WebPreviewLocationRewriter
{
    internal static string Rewrite(string source)
    {
        if (!source.Contains("location", StringComparison.Ordinal)) return source;

        Node root;
        var parser = new Parser();
        try
        {
            root = parser.ParseModule(source);
        }
        catch (ParseErrorException)
        {
            try { root = parser.ParseScript(source); }
            catch (ParseErrorException) { return source; }
        }

        var ranges = new SortedDictionary<int, int>();
        var pending = new Stack<Node>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node is MemberExpression member && IsLocationObject(member.Object))
            {
                ranges[member.Object.Start] = member.Object.End;
            }
            foreach (var child in node.ChildNodes) pending.Push(child);
        }
        if (ranges.Count == 0) return source;

        var result = new StringBuilder(source.Length + ranges.Count * 65);
        var offset = 0;
        foreach (var (start, end) in ranges)
        {
            if (start < offset) continue;
            result.Append(source, offset, start - offset);
            // Workers and scripts used outside an injected HTML document keep native behavior.
            result.Append("((globalThis.__mtPreviewLocation||((v)=>v))(");
            result.Append(source, start, end - start);
            result.Append("))");
            offset = end;
        }
        return result.Append(source, offset, source.Length - offset).ToString();
    }

    private static bool IsLocationObject(Expression expression) => expression switch
    {
        Identifier { Name: "location" } => true,
        MemberExpression
        {
            Object: Identifier { Name: "window" or "self" or "globalThis" or "document" },
            Computed: false,
            Property: Identifier { Name: "location" }
        } => true,
        MemberExpression
        {
            Object: Identifier { Name: "window" or "self" or "globalThis" or "document" },
            Computed: true,
            Property: StringLiteral { Value: "location" }
        } => true,
        _ => false
    };

    internal static string RewriteInlineScripts(string html) => InlineScriptRegex().Replace(html, match =>
    {
        var attributes = match.Groups[1].Value;
        var type = ScriptTypeRegex().Match(attributes);
        if (type.Success && type.Groups[1].Value.ToLowerInvariant() is not ("module" or "text/javascript" or "application/javascript" or "")) return match.Value;
        return "<script" + attributes + ">" + Rewrite(match.Groups[2].Value) + "</script>";
    });

    [GeneratedRegex(@"<script\b([^>]*)>([\s\S]*?)</script\s*>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex InlineScriptRegex();

    [GeneratedRegex("(?:^|\\s)type\\s*=\\s*[\\\"']?([^\\\"'\\s>]+)", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ScriptTypeRegex();
}

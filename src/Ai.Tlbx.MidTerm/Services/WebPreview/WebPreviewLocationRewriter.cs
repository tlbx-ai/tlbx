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
        if (!source.Contains("location", StringComparison.Ordinal)
            && !source.Contains("baseURI", StringComparison.Ordinal)
            && !source.Contains("documentURI", StringComparison.Ordinal)
            && !source.Contains("URL", StringComparison.Ordinal)) return source;

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

        var ranges = new SortedDictionary<int, (int End, string Adapter)>();
        var pending = new Stack<Node>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node is MemberExpression member && IsLocationObject(member.Object) && CanAdapt(member))
            {
                ranges[member.Object.Start] = (member.Object.End, "__mtPreviewLocation");
            }
            else if (node is MemberExpression metadata && IsDocumentMetadata(metadata))
            {
                ranges[metadata.Object.Start] = (metadata.Object.End, "__mtPreviewDocument");
            }
            foreach (var child in node.ChildNodes) pending.Push(child);
        }
        if (ranges.Count == 0) return source;

        var result = new StringBuilder(source.Length + ranges.Count * 65);
        var offset = 0;
        foreach (var (start, adaptation) in ranges)
        {
            if (start < offset) continue;
            var (end, adapter) = adaptation;
            result.Append(source, offset, start - offset);
            // Workers and scripts used outside an injected HTML document keep native behavior.
            result.Append("((globalThis.").Append(adapter).Append("||((v)=>v))(");
            result.Append(source, start, end - start);
            result.Append("))");
            offset = end;
        }
        return result.Append(source, offset, source.Length - offset).ToString();
    }

    private static bool IsDocumentMetadata(MemberExpression member)
    {
        if (member.Object is MemberExpression { Optional: true } && !member.Optional) return false;
        var property = member.Property switch
        {
            Identifier id when !member.Computed => id.Name,
            StringLiteral literal when member.Computed => literal.Value,
            _ => null
        };
        if (property is not ("baseURI" or "URL" or "documentURI")) return false;
        return member.Object is Identifier { Name: "document" }
            or MemberExpression
            {
                Object: Identifier { Name: "window" or "self" or "globalThis" },
                Computed: false, Property: Identifier { Name: "document" }
            };
    }

    private static bool CanAdapt(MemberExpression member)
    {
        // Wrapping the middle of an optional chain would lose its short-circuit.
        if (member.Object is MemberExpression { Optional: true } && !member.Optional) return false;
        var property = member.Property switch
        {
            Identifier identifier when !member.Computed => identifier.Name,
            StringLiteral literal when member.Computed => literal.Value,
            _ => null
        };
        // Leave other native Location members and dynamic property access untouched.
        return property is "href" or "origin" or "protocol" or "host" or "hostname" or "port"
            or "pathname" or "search" or "hash" or "assign" or "replace" or "reload" or "toString";
    }

    private static bool IsLocationObject(Expression expression) => expression switch
    {
        Identifier { Name: "location" } => true,
        MemberExpression
        {
            Object: Identifier { Name: "window" or "self" or "globalThis" or "document" or "top" or "parent" },
            Computed: false,
            Property: Identifier { Name: "location" }
        } => true,
        MemberExpression
        {
            Object: Identifier { Name: "window" or "self" or "globalThis" or "document" or "top" or "parent" },
            Computed: true,
            Property: StringLiteral { Value: "location" }
        } => true,
        MemberExpression
        {
            Object: MemberExpression
            {
                Object: Identifier { Name: "window" or "self" or "globalThis" },
                Computed: false,
                Property: Identifier { Name: "top" or "parent" }
            },
            Computed: false,
            Property: Identifier { Name: "location" }
        } => true,
        _ => false
    };

    internal static string RewriteInlineScripts(string html) => InlineScriptRegex().Replace(html, match =>
    {
        var attributes = match.Groups[1].Value;
        // This adapter must read the native transport URL, even when another
        // tlbx instance wraps its HTML. Rewriting it would make it wrap itself.
        if (ProxyAdapterAttributeRegex().IsMatch(attributes)) return match.Value;
        var type = ScriptTypeRegex().Match(attributes);
        if (type.Success && type.Groups[1].Value.ToLowerInvariant() is not ("module" or "text/javascript" or "application/javascript" or "")) return match.Value;
        return "<script" + attributes + ">" + Rewrite(match.Groups[2].Value) + "</script>";
    });

    [GeneratedRegex(@"<script\b([^>]*)>([\s\S]*?)</script\s*>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex InlineScriptRegex();

    [GeneratedRegex("(?:^|\\s)type\\s*=\\s*[\\\"']?([^\\\"'\\s>]+)", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ScriptTypeRegex();

    [GeneratedRegex(@"(?:^|\s)data-mt-proxy-route\s*=", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ProxyAdapterAttributeRegex();
}

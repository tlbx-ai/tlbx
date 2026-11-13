using System.Text;
using System.Text.Json;
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
            && !source.Contains("URL", StringComparison.Ordinal)
            && !source.Contains("import", StringComparison.Ordinal)) return source;

        var root = Parse(source);
        if (root is null) return source;

        var ranges = new List<(int Start, int End, string Adapter)>();
        var pending = new Stack<Node>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node is MemberExpression member && IsLocationObject(member.Object) && CanAdapt(member))
            {
                ranges.Add((member.Object.Start, member.Object.End, "__mtPreviewLocation"));
            }
            else if (node is MemberExpression metadata && IsDocumentMetadata(metadata))
            {
                ranges.Add((metadata.Object.Start, metadata.Object.End, "__mtPreviewDocument"));
            }
            else if (node is ImportExpression import)
            {
                // Computed absolute module URLs bypass fetch/XHR interception. Keep
                // their import operation native, adapting only its URL argument.
                ranges.Add((import.Source.Start, import.Source.End, "__mtPreviewImport"));
            }
            foreach (var child in node.ChildNodes) pending.Push(child);
        }
        if (ranges.Count == 0) return source;

        var result = new StringBuilder(source.Length + ranges.Count * 65);
        var insertions = new SortedDictionary<int, List<string>>();
        foreach (var (start, end, adapter) in ranges.OrderBy(r => r.Start).ThenByDescending(r => r.End))
        {
            // Workers and scripts used outside an injected HTML document keep native behavior.
            if (!insertions.TryGetValue(start, out var openings)) insertions[start] = openings = [];
            openings.Add("((globalThis." + adapter + "||((v)=>v))(");
            if (!insertions.TryGetValue(end, out var closings)) insertions[end] = closings = [];
            closings.Add("))");
        }
        var offset = 0;
        foreach (var (position, additions) in insertions)
        {
            result.Append(source, offset, position - offset);
            foreach (var addition in additions) result.Append(addition);
            offset = position;
        }
        return result.Append(source, offset, source.Length - offset).ToString();
    }

    internal static string RewriteModuleSpecifiers(string source, Func<string, string> rewrite)
    {
        if (!source.Contains("import", StringComparison.Ordinal)
            && !source.Contains("export", StringComparison.Ordinal)) return source;
        var root = Parse(source);
        if (root is null) return source;

        var edits = new List<(int Start, int End, string Value)>();
        var pending = new Stack<Node>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            var specifier = node switch
            {
                ImportDeclaration import => import.Source,
                ExportNamedDeclaration export => export.Source,
                ExportAllDeclaration export => export.Source,
                ImportExpression { Source: StringLiteral literal } => literal,
                _ => null
            };
            if (specifier is not null)
            {
                var value = rewrite(specifier.Value);
                if (value != specifier.Value)
                    edits.Add((specifier.Start, specifier.End, JsonSerializer.Serialize(value)));
            }
            foreach (var child in node.ChildNodes) pending.Push(child);
        }
        if (edits.Count == 0) return source;

        // Only source literals change. Import attributes/options, comments and
        // unrelated strings retain their original bytes and evaluation order.
        var result = new StringBuilder(source.Length);
        var offset = 0;
        foreach (var (start, end, value) in edits.OrderBy(edit => edit.Start))
        {
            result.Append(source, offset, start - offset).Append(value);
            offset = end;
        }
        return result.Append(source, offset, source.Length - offset).ToString();
    }

    private static Node? Parse(string source)
    {
        var parser = new Parser();
        try { return parser.ParseModule(source); }
        catch (ParseErrorException)
        {
            try { return parser.ParseScript(source); }
            catch (ParseErrorException) { return null; }
        }
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

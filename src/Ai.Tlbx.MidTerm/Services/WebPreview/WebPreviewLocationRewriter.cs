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
        var pending = new Stack<(Node Node, Node? Parent, bool Mutation)>();
        pending.Push((root, null, false));
        while (pending.TryPop(out var item))
        {
            var (node, parent, mutation) = item;
            // Nested previews can rewrite an already adapted script. Never enter
            // our adapter calls/definitions or wrap their native identity reads.
            if (node is CallExpression call && IsAdapterCall(call)
                || node is AssignmentExpression assignment && IsAdapterReference(assignment.Left)) continue;
            if (!mutation && node is MemberExpression member && IsLocationObject(member.Object) && CanAdapt(member))
            {
                ranges.Add((member.Object.Start, member.Object.End, "__mtPreviewLocation"));
            }
            else if (!mutation && node is Expression value && IsLocationObject(value) && IsLocationValueRead(value, parent))
            {
                ranges.Add((value.Start, value.End, "__mtPreviewLocation"));
            }
            else if (node is MemberExpression metadata && IsDocumentMetadata(metadata))
            {
                ranges.Add((metadata.Object.Start, metadata.Object.End, "__mtPreviewDocument"));
            }
            else if (node is ImportExpression import
                && !(import.Source is CallExpression adapted && IsAdapterCall(adapted)))
            {
                // Computed absolute module URLs bypass fetch/XHR interception. Keep
                // their import operation native, adapting only its URL argument.
                ranges.Add((import.Source.Start, import.Source.End, "__mtPreviewImport"));
            }
            // Delete/update act on a reference, not an adapted value. Carry this
            // exclusion only through its member receiver, not into call arguments,
            // computed keys or functions evaluated while obtaining that reference.
            var mutationTarget = node is UpdateExpression
                || node is UnaryExpression && source.AsSpan(node.Start, node.End - node.Start).StartsWith("delete", StringComparison.Ordinal);
            foreach (var child in node.ChildNodes)
            {
                var childMutation = mutationTarget
                    || mutation && node is MemberExpression receiver && ReferenceEquals(receiver.Object, child)
                    || mutation && node is ChainExpression chain && ReferenceEquals(chain.Expression, child);
                pending.Push((child, node, childMutation));
            }
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
                    edits.Add((specifier.Start, specifier.End, EncodeJavaScriptString(value)));
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

    internal static string RewriteImportMetaUrlReads(string source, string? finalScriptUrl)
    {
        if (!source.Contains("import", StringComparison.Ordinal)
            || !Uri.TryCreate(finalScriptUrl, UriKind.Absolute, out var uri)
            || uri.Scheme is not ("http" or "https")) return source;
        var root = Parse(source);
        if (root is null) return source;

        var reads = new List<MemberExpression>();
        var writes = new HashSet<Node>(ReferenceEqualityComparer.Instance);
        var names = new HashSet<string>(StringComparer.Ordinal);
        var pending = new Stack<Node>();
        pending.Push(root);
        while (pending.TryPop(out var node))
        {
            if (node is Identifier identifier) names.Add(identifier.Name);
            if (node is MemberExpression { Object: MetaProperty { Meta.Name: "import", Property.Name: "meta" } } member
                && (member is { Computed: false, Property: Identifier { Name: "url" } }
                    or { Computed: true, Property: StringLiteral { Value: "url" } })) reads.Add(member);
            switch (node)
            {
                case AssignmentExpression assignment: AddWriteTargets(assignment.Left, writes); break;
                case UpdateExpression update: AddWriteTargets(update.Argument, writes); break;
                case UnaryExpression unary when source.AsSpan(unary.Start, unary.Argument.Start - unary.Start)
                    .TrimStart().StartsWith("delete", StringComparison.Ordinal): AddWriteTargets(unary.Argument, writes); break;
                case ForInStatement forIn: AddWriteTargets(forIn.Left, writes); break;
                case ForOfStatement forOf: AddWriteTargets(forOf.Left, writes); break;
            }
            foreach (var child in node.ChildNodes) pending.Push(child);
        }
        reads.RemoveAll(member => writes.Contains(member));
        if (reads.Count == 0) return source;

        var name = "__tlbxModuleUrl";
        for (var index = 0; names.Contains(name); index++) name = "__tlbxModuleUrl" + index;
        // Capture the original native value before application code can mutate
        // import.meta. Reads evaluate the current property exactly once; writes,
        // deletes, updates and aliased access retain their native object/targets.
        var finalUri = new UriBuilder(uri) { UserName = "", Password = "" }.Uri.AbsoluteUri;
        // Function declarations are initialized during module instantiation: a
        // cycle may call an exported hoisted function before this body runs.
        // Keep the lazy state on that private function, not a const/let in TDZ.
        var prologue = ";\nfunction " + name + "(value){if(" + name + ".captured!==" + name + "){" + name + ".captured=" + name + ";"
            + name + ".original=value;}return value===" + name + ".original&&typeof globalThis.__mtPreviewModuleUrl==='function'"
            + "?globalThis.__mtPreviewModuleUrl(value," + EncodeJavaScriptString(finalUri) + "):value;}\n"
            + name + "(import.meta.url);\n";
        var insertion = source.StartsWith("#!", StringComparison.Ordinal) ? source.IndexOf("\n", StringComparison.Ordinal) + 1 : 0;
        foreach (var statement in root.ChildNodes)
        {
            if (statement is not ExpressionStatement { Expression: StringLiteral }) break;
            insertion = statement.End;
        }
        var result = new StringBuilder(source.Length + prologue.Length);
        result.Append(source, 0, insertion).Append(prologue);
        var offset = insertion;
        foreach (var read in reads.OrderBy(member => member.Start))
        {
            result.Append(source, offset, read.Start - offset).Append(name).Append('(')
                .Append(source, read.Start, read.End - read.Start).Append(')');
            offset = read.End;
        }
        return result.Append(source, offset, source.Length - offset).ToString();
    }

    private static void AddWriteTargets(Node? target, HashSet<Node> writes)
    {
        switch (target)
        {
            case MemberExpression member: writes.Add(member); break;
            case AssignmentPattern assignment: AddWriteTargets(assignment.Left, writes); break;
            case RestElement rest: AddWriteTargets(rest.Argument, writes); break;
            case AssignmentProperty property: AddWriteTargets(property.Value, writes); break;
            case ArrayPattern array:
                foreach (var item in array.Elements) AddWriteTargets(item, writes);
                break;
            case ObjectPattern obj:
                foreach (var item in obj.Properties) AddWriteTargets(item, writes);
                break;
        }
    }

    private static string EncodeJavaScriptString(string value) =>
        "\"" + JsonEncodedText.Encode(value).ToString() + "\"";

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

    private static bool IsLocationValueRead(Expression value, Node? parent) => parent switch
    {
        VariableDeclarator declaration => ReferenceEquals(declaration.Init, value),
        AssignmentExpression assignment => ReferenceEquals(assignment.Right, value),
        AssignmentPattern assignment => ReferenceEquals(assignment.Right, value),
        ReturnStatement statement => ReferenceEquals(statement.Argument, value),
        ThrowStatement statement => ReferenceEquals(statement.Argument, value),
        ArrowFunctionExpression function => ReferenceEquals(function.Body, value),
        ExpressionStatement statement => ReferenceEquals(statement.Expression, value),
        CallExpression call => !ReferenceEquals(call.Callee, value),
        NewExpression call => !ReferenceEquals(call.Callee, value),
        BinaryExpression or ConditionalExpression or SequenceExpression or ArrayExpression
            or TemplateLiteral or AwaitExpression or YieldExpression or SpreadElement => true,
        // A shorthand key needs a different edit shape; do not turn it into an
        // invalid object literal. Pattern properties and binding IDs stay native.
        ObjectProperty property => ReferenceEquals(property.Value, value) && property.Key.Start != value.Start,
        MemberExpression member => member.Computed && ReferenceEquals(member.Property, value),
        ForInStatement loop => ReferenceEquals(loop.Right, value),
        ForOfStatement loop => ReferenceEquals(loop.Right, value),
        _ => false
    };

    private static bool IsAdapterReference(Node node) => node is MemberExpression
    {
        Object: Identifier { Name: "globalThis" or "window" or "self" },
        Computed: false,
        Property: Identifier { Name: "__mtPreviewLocation" or "__mtPreviewDocument" or "__mtPreviewImport" }
    };

    private static bool IsAdapterCall(CallExpression call) => IsAdapterReference(call.Callee)
        || call.Callee is BinaryExpression { Left: var adapter, Right: ArrowFunctionExpression }
            && IsAdapterReference(adapter);

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

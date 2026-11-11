using System.Globalization;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.AgentHost;

public static class AppServerControlToolPresentationProjector
{
    private const int MaxSubjectChars = 2_048;
    private const int MaxCommandChars = 32_768;
    private const int MaxOutcomeChars = 320;
    private const int MaxToolNameChars = 160;
    private const int MaxPaths = 12;
    private const int MaxPathChars = 1_024;

    public static AppServerControlToolPresentation FromCodex(
        string itemType,
        string status,
        string? title,
        JsonElement payload)
    {
        var nestedItem = GetObject(payload, "item");
        var item = nestedItem ?? payload;
        var toolName = FirstString(item, "toolName", "name", "title") ??
                       FirstString(payload, "toolName", "name");
        var category = ResolveCategory(itemType, toolName, null);
        var presentation = Create(category, toolName, status);
        var terminal = status is "completed" or "failed" or "cancelled";
        presentation.Subject = ResolveSubject(category, item, payload, allowSummaryFallback: !terminal);
        presentation.ExitCode = FirstInt32(item, "exitCode") ?? FirstInt32(payload, "exitCode");
        presentation.ResultCount = ResolveResultCount(item, payload);
        presentation.Paths = ExtractPaths(item, payload);
        presentation.Outcome = ResolveOutcome(
            status,
            presentation.ExitCode,
            FirstString(item, "summary") ?? FirstString(payload, "summary") ?? title);

        var output = new BoundedToolOutputAccumulator();
        AppendErrorOutput(item, output);
        AppendErrorOutput(payload, output);
        AppendResultOutput(item, output);
        if (nestedItem is not null)
        {
            AppendResultOutput(payload, output);
        }
        output.ApplyTo(presentation, status);
        return presentation;
    }

    public static AppServerControlToolPresentation FromClaude(
        string toolName,
        string status,
        JsonElement? input)
    {
        var category = ResolveCategory("dynamic_tool_call", toolName, null);
        var presentation = Create(category, toolName, status);
        if (input is { ValueKind: JsonValueKind.Object } inputObject)
        {
            presentation.Subject = ResolveSubject(category, inputObject, inputObject);
            presentation.Paths = ExtractPaths(inputObject, inputObject);
        }
        return presentation;
    }

    public static AppServerControlToolPresentation FromAcp(
        string itemType,
        string status,
        string? title,
        string? kind,
        JsonElement? rawInput)
    {
        var toolName = NormalizeToolName(title);
        var category = ResolveCategory(itemType, toolName, kind);
        var presentation = Create(category, toolName, status);
        if (rawInput is { ValueKind: JsonValueKind.Object } input)
        {
            presentation.Subject = ResolveSubject(category, input, input);
            presentation.Paths = ExtractPaths(input, input);
        }
        return presentation;
    }

    public static AppServerControlToolPresentation FromLegacy(
        string? itemType,
        string status,
        string? title,
        string? detail)
    {
        var category = ResolveCategory(itemType, title, null);
        var presentation = Create(category, NormalizeToolName(title), status);
        if (category != "other")
        {
            presentation.Subject = BoundSingleLine(detail, MaxSubjectChars);
        }
        else
        {
            var output = new BoundedToolOutputAccumulator();
            output.Append(detail);
            output.ApplyTo(presentation, status);
        }
        presentation.Outcome = ResolveOutcome(status, null, null);
        return presentation;
    }

    public static AppServerControlToolPresentation Clone(AppServerControlToolPresentation source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return new AppServerControlToolPresentation
        {
            Category = source.Category,
            Label = source.Label,
            ToolName = source.ToolName,
            Subject = source.Subject,
            Outcome = source.Outcome,
            Evidence = source.Evidence,
            EvidenceKind = source.EvidenceKind,
            ExitCode = source.ExitCode,
            ResultCount = source.ResultCount,
            TotalLineCount = source.TotalLineCount,
            OmittedLineCount = source.OmittedLineCount,
            Paths = (source.Paths ?? []).Take(MaxPaths).Select(static path => Bound(path, MaxPathChars)).ToList()
        };
    }

    public static void Merge(
        AppServerControlToolPresentation target,
        AppServerControlToolPresentation source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        target.Category = Prefer(source.Category, target.Category, "other");
        target.Label = Prefer(source.Label, target.Label, "Used tool");
        target.ToolName = Prefer(source.ToolName, target.ToolName);
        target.Subject = Prefer(source.Subject, target.Subject);
        target.Outcome = Prefer(source.Outcome, target.Outcome);
        target.Evidence = Prefer(source.Evidence, target.Evidence);
        target.EvidenceKind = Prefer(source.EvidenceKind, target.EvidenceKind);
        target.ExitCode = source.ExitCode ?? target.ExitCode;
        target.ResultCount = source.ResultCount ?? target.ResultCount;
        target.TotalLineCount = Math.Max(source.TotalLineCount, target.TotalLineCount);
        target.OmittedLineCount = Math.Max(source.OmittedLineCount, target.OmittedLineCount);
        if (source.Paths is { Count: > 0 })
        {
            target.Paths = source.Paths.Take(MaxPaths).Select(static path => Bound(path, MaxPathChars)).ToList();
        }
    }

    public static void AppendAcpContent(JsonElement? content, BoundedToolOutputAccumulator output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (content is not { ValueKind: JsonValueKind.Array } array)
        {
            return;
        }

        using var blockEnumerator = array.EnumerateArray();
        while (blockEnumerator.MoveNext())
        {
            var block = blockEnumerator.Current;
            var type = GetString(block, "type");
            if (string.Equals(type, "diff", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            AppendTextValue(block, output);
        }
    }

    public static void AppendClaudeResult(
        JsonElement item,
        JsonElement root,
        BoundedToolOutputAccumulator output)
    {
        ArgumentNullException.ThrowIfNull(output);
        if (item.TryGetProperty("content", out var content))
        {
            AppendTextValue(content, output);
        }

        AppendString(root, output, "tool_use_result", "stdout");
        AppendString(root, output, "tool_use_result", "stderr");
        AppendString(root, output, "tool_use_result", "error");
    }

    public static void ApplyOutput(
        AppServerControlToolPresentation presentation,
        BoundedToolOutputAccumulator output,
        string status)
    {
        output.ApplyTo(presentation, status);
        presentation.Outcome = ResolveOutcome(status, presentation.ExitCode, presentation.Outcome);
    }

    public static string ResolveLabel(string category)
    {
        return category switch
        {
            "command" => "Ran command",
            "read" => "Read file",
            "search" => "Searched",
            "edit" => "Changed files",
            "browser" => "Used browser",
            "network" => "Fetched resource",
            "subagent" => "Delegated task",
            _ => "Used tool"
        };
    }

    private static AppServerControlToolPresentation Create(string category, string? toolName, string status)
    {
        return new AppServerControlToolPresentation
        {
            Category = category,
            Label = ResolveLabel(category),
            ToolName = BoundSingleLine(toolName, MaxToolNameChars),
            Outcome = ResolveOutcome(status, null, null)
        };
    }

    private static string ResolveCategory(string? itemType, string? toolName, string? kind)
    {
        var normalizedType = NormalizeKey(itemType);
        var normalizedTool = NormalizeKey(toolName);
        var normalizedKind = NormalizeKey(kind);

        if (normalizedType is "commandexecution" or "commandoutput" or "command" ||
            normalizedKind is "execute" or "command" ||
            normalizedTool is "bash" or "powershell" or "shell")
        {
            return "command";
        }
        if (normalizedKind is "read" || normalizedTool is "read" or "readfile")
        {
            return "read";
        }
        if (normalizedType is "filechange" or "filechangeoutput" ||
            normalizedKind is "edit" or "delete" or "move" or "write" ||
            normalizedTool is "edit" or "write" or "notebookedit" or "applypatch")
        {
            return "edit";
        }
        if (normalizedType is "websearch" || normalizedKind is "search" ||
            normalizedTool.Contains("search", StringComparison.Ordinal) ||
            normalizedTool is "grep" or "glob" or "rg")
        {
            return "search";
        }
        if (normalizedKind is "browser" ||
            normalizedTool.Contains("browser", StringComparison.Ordinal) ||
            normalizedTool.Contains("playwright", StringComparison.Ordinal) ||
            normalizedTool.Contains("chrome", StringComparison.Ordinal))
        {
            return "browser";
        }
        if (normalizedKind is "fetch" or "network" || normalizedTool is "webfetch" or "fetch" or "http")
        {
            return "network";
        }
        if (normalizedKind is "task" or "agent" || normalizedTool is "task" or "agent" or "spawnagent")
        {
            return "subagent";
        }
        return "other";
    }

    private static string? ResolveSubject(
        string category,
        JsonElement primary,
        JsonElement fallback,
        bool allowSummaryFallback = true)
    {
        string? value = category switch
        {
            "command" => FirstNestedString(primary, "command") ??
                         FirstNestedString(primary, "input", "command") ??
                         FirstNestedString(primary, "arguments", "command") ??
                         FirstNestedString(fallback, "command"),
            "read" or "edit" => FirstString(primary, "file_path", "filePath", "path") ??
                                  FirstNestedString(primary, "input", "file_path") ??
                                  FirstNestedString(primary, "input", "path"),
            "search" => FirstString(primary, "query", "pattern", "searchTerm") ??
                        FirstNestedString(primary, "input", "query") ??
                        FirstNestedString(primary, "input", "pattern"),
            "browser" or "network" => FirstString(primary, "url", "uri", "selector") ??
                                        FirstNestedString(primary, "input", "url"),
            _ => FirstString(primary, "summary", "title") ??
                 FirstString(fallback, "summary", "title")
        };
        if (allowSummaryFallback)
        {
            value ??= FirstString(primary, "summary", "title") ?? FirstString(fallback, "summary", "title");
        }
        // Commands are expandable source, not a one-line subject. In particular,
        // PowerShell here-strings often start with only the launcher on line one.
        return category == "command" && !string.IsNullOrWhiteSpace(value)
            ? Bound(value, MaxCommandChars)
            : BoundSingleLine(value, MaxSubjectChars);
    }

    private static List<string> ExtractPaths(JsonElement primary, JsonElement fallback)
    {
        var paths = new List<string>(MaxPaths);
        AddPath(paths, FirstString(primary, "file_path", "filePath", "path"));
        AddPath(paths, FirstNestedString(primary, "input", "file_path"));
        AddPath(paths, FirstNestedString(primary, "input", "path"));
        AddChanges(paths, primary);
        AddChanges(paths, fallback);
        return paths;
    }

    private static void AddChanges(List<string> paths, JsonElement value)
    {
        if (paths.Count >= MaxPaths || !value.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        using var changeEnumerator = changes.EnumerateArray();
        while (changeEnumerator.MoveNext())
        {
            var change = changeEnumerator.Current;
            AddPath(paths, FirstString(change, "path", "file_path", "filePath"));
            if (paths.Count >= MaxPaths)
            {
                break;
            }
        }
    }

    private static void AddPath(List<string> paths, string? path)
    {
        var bounded = BoundSingleLine(path, MaxPathChars);
        if (string.IsNullOrWhiteSpace(bounded) || paths.Contains(bounded, StringComparer.Ordinal))
        {
            return;
        }
        paths.Add(bounded);
    }

    private static int? ResolveResultCount(JsonElement primary, JsonElement fallback)
    {
        return FirstInt32(primary, "resultCount", "matchCount", "totalFiles", "count") ??
               FirstInt32(fallback, "resultCount", "matchCount", "totalFiles", "count");
    }

    private static string? ResolveOutcome(string status, int? exitCode, string? providerOutcome)
    {
        if (exitCode is not null)
        {
            return string.Create(CultureInfo.InvariantCulture, $"Exit code {exitCode.Value}");
        }
        if (!string.IsNullOrWhiteSpace(providerOutcome) &&
            !providerOutcome.Contains("started", StringComparison.OrdinalIgnoreCase) &&
            !providerOutcome.Contains("completed", StringComparison.OrdinalIgnoreCase))
        {
            return BoundSingleLine(providerOutcome, MaxOutcomeChars);
        }
        return status.Trim().ToLowerInvariant() switch
        {
            "failed" => "Failed",
            "cancelled" or "canceled" => "Cancelled",
            _ => null
        };
    }

    private static void AppendErrorOutput(JsonElement value, BoundedToolOutputAccumulator output)
    {
        AppendString(value, output, "error");
        AppendString(value, output, "stderr");
        if (value.ValueKind == JsonValueKind.Object &&
            value.TryGetProperty("error", out var error) &&
            error.ValueKind == JsonValueKind.Object)
        {
            AppendString(error, output, "message");
        }
    }

    private static void AppendResultOutput(JsonElement value, BoundedToolOutputAccumulator output)
    {
        AppendString(value, output, "stdout");
        AppendString(value, output, "output");
        if (value.TryGetProperty("result", out var result))
        {
            AppendTextValue(result, output);
        }
        if (value.TryGetProperty("content", out var content))
        {
            AppendTextValue(content, output);
        }
    }

    private static void AppendTextValue(JsonElement value, BoundedToolOutputAccumulator output)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.String:
                output.Append(value.GetString());
                break;
            case JsonValueKind.Array:
                using (var itemEnumerator = value.EnumerateArray())
                {
                    while (itemEnumerator.MoveNext())
                    {
                        AppendTextValue(itemEnumerator.Current, output);
                    }
                }
                break;
            case JsonValueKind.Object:
                AppendString(value, output, "text");
                AppendString(value, output, "content");
                AppendString(value, output, "stdout");
                AppendString(value, output, "stderr");
                if (value.TryGetProperty("content", out var content) && content.ValueKind is JsonValueKind.Array or JsonValueKind.Object)
                {
                    AppendTextValue(content, output);
                }
                break;
        }
    }

    private static void AppendString(JsonElement value, BoundedToolOutputAccumulator output, params string[] path)
    {
        var text = Traverse(value, path);
        if (text is { ValueKind: JsonValueKind.String })
        {
            output.Append(text.Value.GetString());
        }
    }

    private static string? FirstString(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            var candidate = GetString(value, name);
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }
        return null;
    }

    private static string? FirstNestedString(JsonElement value, params string[] path)
    {
        var candidate = Traverse(value, path);
        if (candidate is { ValueKind: JsonValueKind.String })
        {
            return candidate.Value.GetString();
        }
        if (candidate is { ValueKind: JsonValueKind.Object } objectValue)
        {
            return FirstString(objectValue, "command", "text", "summary");
        }
        return null;
    }

    private static int? FirstInt32(JsonElement value, params string[] names)
    {
        foreach (var name in names)
        {
            if (value.ValueKind == JsonValueKind.Object &&
                value.TryGetProperty(name, out var property) &&
                property.ValueKind == JsonValueKind.Number &&
                property.TryGetInt32(out var result))
            {
                return result;
            }
        }
        return null;
    }

    private static JsonElement? GetObject(JsonElement value, string name)
    {
        return value.ValueKind == JsonValueKind.Object &&
               value.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.Object
            ? property
            : null;
    }

    private static string? GetString(JsonElement value, string name)
    {
        return value.ValueKind == JsonValueKind.Object &&
               value.TryGetProperty(name, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static JsonElement? Traverse(JsonElement value, params string[] path)
    {
        var current = value;
        foreach (var segment in path)
        {
            if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }
        return current;
    }

    private static string NormalizeKey(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var source = value.AsSpan(0, Math.Min(value.Length, 256));
        Span<char> buffer = stackalloc char[source.Length];
        var written = 0;
        foreach (var character in source)
        {
            if (char.IsLetterOrDigit(character))
            {
                buffer[written++] = char.ToLowerInvariant(character);
            }
        }
        return new string(buffer[..written]);
    }

    private static string? NormalizeToolName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }
        var trimmed = value.AsSpan().Trim();
        if (trimmed.Equals("Tool".AsSpan(), StringComparison.Ordinal) ||
            trimmed.Equals("Tool started".AsSpan(), StringComparison.Ordinal) ||
            trimmed.Equals("Tool completed".AsSpan(), StringComparison.Ordinal))
        {
            return null;
        }
        return trimmed.Length <= MaxToolNameChars
            ? trimmed.ToString()
            : string.Concat(trimmed[..(MaxToolNameChars - 1)], "…");
    }

    private static string? BoundSingleLine(string? value, int maxChars)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var span = value.AsSpan().Trim();
        var lineEnd = span.IndexOfAny('\r', '\n');
        if (lineEnd >= 0)
        {
            span = span[..lineEnd].TrimEnd();
        }
        if (span.Length == 0)
        {
            return null;
        }
        return span.Length <= maxChars ? span.ToString() : string.Concat(span[..(maxChars - 1)], "…");
    }

    private static string Bound(string value, int maxChars)
    {
        return value.Length <= maxChars ? value : string.Concat(value.AsSpan(0, maxChars - 1), "…");
    }

    private static string Prefer(string? primary, string? fallback, string defaultValue)
    {
        return !string.IsNullOrWhiteSpace(primary)
            ? primary
            : !string.IsNullOrWhiteSpace(fallback)
                ? fallback
                : defaultValue;
    }

    private static string? Prefer(string? primary, string? fallback)
    {
        return !string.IsNullOrWhiteSpace(primary) ? primary : fallback;
    }
}

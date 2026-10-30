using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>The same validation, result references and partial-result execution for HTTP and caller-native workflows.</summary>
internal sealed class TlbxMcpWorkflowExecutor(IEnumerable<Tool> tools)
{
    private readonly Dictionary<string, Tool> _tools = tools.ToDictionary(tool => tool.Name, StringComparer.Ordinal);
    internal sealed record PreviewScope(string SessionId, string PreviewName);
    internal delegate Task<bool> PreviewRunner(string? target, string? name, string url,
        Func<PreviewScope, CancellationToken, Task<bool>> body, CancellationToken cancellationToken);
    internal sealed record Step(Tool Tool, JsonObject Arguments);

    internal IReadOnlyList<Step> Prepare(JsonObject arguments)
    {
        if (arguments.Any(p => p.Key is not ("steps" or "targetSessionId" or "previewName" or "previewUrl" or "timeout")))
            throw new ArgumentException("Unknown workflow argument.");
        if (arguments["steps"] is not JsonArray steps || steps.Count is < 1 or > 64)
            throw new ArgumentException("steps must contain 1-64 named tool calls.");
        var prepared = new List<Step>();
        foreach (var node in steps)
        {
            if (node is not JsonObject step || step.Any(p => p.Key is not ("tool" or "arguments")))
                throw new ArgumentException("Each step requires tool and optional arguments.");
            var name = step["tool"]?.GetValue<string>() ?? "";
            if (name is "workflow_run" or "browser_with_preview" or "browser_scope_open" or "browser_scope_close" || !_tools.TryGetValue(name, out var tool))
                throw new ArgumentException("Unknown or recursive workflow tool: " + name);
            if (step["arguments"] is not null and not JsonObject) throw new ArgumentException("Step arguments must be an object.");
            var values = step["arguments"]?.DeepClone().AsObject() ?? new JsonObject();
            var schema = tool.InputSchema;
            var browserScoped = schema.TryGetProperty("properties", out var scopeProperties) && scopeProperties.TryGetProperty("previewName", out _);
            if (arguments["previewUrl"] is not null && browserScoped)
                foreach (var field in new[] { "targetSessionId", "previewName" })
                    if (values[field] is not null && arguments[field] is null)
                        throw new ArgumentException("Ephemeral browser steps must inherit " + field + "; specify that scope at the workflow root.");
            foreach (var field in new[] { "targetSessionId", "previewName" })
            {
                if (arguments[field] is not { } scope || !schema.TryGetProperty("properties", out var properties)
                    || !properties.TryGetProperty(field, out _)) continue;
                if (values[field] is { } supplied && !JsonNode.DeepEquals(supplied, scope))
                {
                    if (field == "previewName" || browserScoped)
                        throw new ArgumentException("Browser step cannot override workflow " + field + ".");
                    continue; // Explicit non-browser targets remain explicit across a cross-domain callback.
                }
                values[field] = scope.DeepClone();
            }
            ValidateReferences(values, prepared.Count);
            ValidateShape(values, schema, prepared.Count);
            prepared.Add(new Step(tool, values));
        }
        return prepared;
    }

    private static void ValidateReferences(JsonNode? node, int stepIndex)
    {
        if (node is JsonObject obj)
        {
            if (obj.Count == 1 && obj.ContainsKey("$literal")) return;
            if (obj.ContainsKey("$step"))
            {
                if (obj.Count != 2 || obj["$step"] is not JsonValue step || !step.TryGetValue<int>(out var index)
                    || index < 0 || index >= stepIndex || obj["path"] is not JsonValue path
                    || !path.TryGetValue<string>(out var pointer) || pointer.Length > 0 && !pointer.StartsWith('/'))
                    throw new ArgumentException("Result references require an earlier $step and a JSON Pointer path.");
                return;
            }
            foreach (var property in obj) ValidateReferences(property.Value, stepIndex);
        }
        else if (node is JsonArray array)
            foreach (var item in array) ValidateReferences(item, stepIndex);
    }

    private static void ValidateShape(JsonNode? value, JsonElement schema, int stepIndex)
    {
        if (value is JsonObject literal && literal.Count == 1 && literal.ContainsKey("$literal")) return;
        if (value is JsonObject reference && reference.ContainsKey("$step"))
        {
            if (reference.Count != 2 || reference["path"] is not JsonValue || reference["$step"] is not JsonValue index
                || !index.TryGetValue<int>(out var number) || number < 0 || number >= stepIndex)
                throw new ArgumentException("Result references require an earlier $step and path.");
            return;
        }
        if (schema.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String)
        {
            var valid = type.GetString() switch
            {
                "object" => value is JsonObject, "array" => value is JsonArray,
                "string" => value is JsonValue v && v.TryGetValue<string>(out _),
                "boolean" => value is JsonValue v && v.TryGetValue<bool>(out _),
                "integer" => value is JsonValue v && v.TryGetValue<long>(out _),
                "number" => value is JsonValue v && v.TryGetValue<double>(out _), _ => true
            };
            if (!valid) throw new ArgumentException("Workflow argument type does not match the named tool schema.");
        }
        if (value is JsonObject obj)
        {
            if (schema.TryGetProperty("required", out var required))
            {
                using var names = required.EnumerateArray();
                foreach (var name in names)
                    if (!obj.ContainsKey(name.GetString()!)) throw new ArgumentException("Missing required step argument: " + name.GetString());
            }
            foreach (var pair in obj)
            {
                if (schema.TryGetProperty("properties", out var properties) && properties.TryGetProperty(pair.Key, out var property))
                    ValidateShape(pair.Value, property, stepIndex);
                else if (schema.TryGetProperty("additionalProperties", out var additional) && additional.ValueKind == JsonValueKind.False)
                    throw new ArgumentException("Unknown named tool argument: " + pair.Key);
            }
        }
        if (value is JsonArray array && schema.TryGetProperty("items", out var items))
            foreach (var item in array) ValidateShape(item, items, stepIndex);
    }

    private static JsonNode? Resolve(JsonNode? value, IReadOnlyList<CallToolResult> results)
    {
        if (value is JsonObject literal && literal.Count == 1 && literal.ContainsKey("$literal")) return literal["$literal"]?.DeepClone();
        if (value is JsonObject obj && obj.ContainsKey("$step"))
        {
            var index = obj["$step"]!.GetValue<int>();
            JsonNode? selected = results[index].StructuredContent is { } content ? JsonNode.Parse(content.GetRawText()) : null;
            var path = obj["path"]!.GetValue<string>();
            if (path.Length > 0 && !path.StartsWith('/')) throw new ArgumentException("Result path must be a JSON Pointer.");
            foreach (var segment in path.Split('/').Skip(1))
            {
                var key = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                selected = selected is JsonArray array && int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var item) && item >= 0 && item < array.Count
                    ? array[item] : selected is JsonObject itemObject ? itemObject[key] : null;
                if (selected is null) throw new ArgumentException("Result reference could not be resolved.");
            }
            return selected?.DeepClone();
        }
        if (value is JsonObject map) return new JsonObject(map.Select(p => KeyValuePair.Create(p.Key, Resolve(p.Value, results))));
        if (value is JsonArray list) return new JsonArray(list.Select(p => Resolve(p, results)).ToArray());
        return value?.DeepClone();
    }


    internal async ValueTask<CallToolResult> ExecuteAsync(CallToolRequestParams parameters,
        Func<string, IDictionary<string, JsonElement>, CancellationToken, ValueTask<CallToolResult>> invoke,
        PreviewRunner withPreview, CancellationToken cancellationToken)
    {
        var completed = new List<CallToolResult>();
        var evidence = new JsonArray();
        var contents = new List<ContentBlock>();
        string? failure = null;
        try
        {
            var arguments = new JsonObject(parameters.Arguments?.Select(p => KeyValuePair.Create(p.Key, JsonNode.Parse(p.Value.GetRawText()))) ?? []);
            var prepared = Prepare(arguments); // all names, scopes and literal argument shapes checked before opening
            var seconds = arguments["timeout"]?.GetValue<int>() ?? 180;
            if (seconds is < 1 or > 600) throw new ArgumentException("Workflow timeout must be 1-600 seconds.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
            async Task<bool> Run(CancellationToken ct)
            {
                foreach (var step in prepared)
                {
                    ct.ThrowIfCancellationRequested();
                    var resolved = Resolve(step.Arguments, completed)!.AsObject();
                    ValidateShape(resolved, step.Tool.InputSchema, 0);
                    var serialized = JsonSerializer.SerializeToElement(resolved, McpJsonContext.Default.JsonObject);
                    using var properties = serialized.EnumerateObject();
                    var values = properties.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                    var result = await invoke(step.Tool.Name, values, ct).ConfigureAwait(false);
                    completed.Add(result);
                    evidence.Add((JsonNode)new JsonObject
                    {
                        ["tool"] = step.Tool.Name, ["isError"] = result.IsError == true,
                        ["structuredContent"] = result.StructuredContent is { } data ? JsonNode.Parse(data.GetRawText()) : null,
                        ["contentStart"] = contents.Count + 1, ["contentCount"] = result.Content.Count
                    });
                    contents.AddRange(result.Content);
                    if (result.IsError == true) { failure = "A step failed. Completed steps were not rolled back."; break; }
                }
                return true;
            }
            if (arguments["previewUrl"] is { } url)
            {
                var target = arguments["targetSessionId"]?.GetValue<string>();
                var name = arguments["previewName"]?.GetValue<string>();
                await withPreview(target, name, url.GetValue<string>(), async (scope, token) =>
                {
                    foreach (var step in prepared)
                    {
                        if (!step.Tool.InputSchema.TryGetProperty("properties", out var properties)) continue;
                        var browserScoped = properties.TryGetProperty("previewName", out _);
                        if (browserScoped) step.Arguments["previewName"] = scope.PreviewName;
                        if (properties.TryGetProperty("targetSessionId", out _) && (browserScoped || !step.Arguments.ContainsKey("targetSessionId")))
                            step.Arguments["targetSessionId"] = scope.SessionId;
                    }
                    return await Run(token).ConfigureAwait(false);
                }, timeout.Token).ConfigureAwait(false);
            }
            else await Run(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        { failure = ex.Message; }
        var response = McpApiResult.Structured(new JsonObject
        {
            ["success"] = failure is null, ["completedSteps"] = completed.Count, ["steps"] = evidence,
            ["error"] = failure, ["atomic"] = false
        }, failure is not null);
        foreach (var block in contents) response.Content.Add(block);
        return response;
    }
}


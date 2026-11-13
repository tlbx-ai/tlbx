using System.Runtime.InteropServices;
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
    internal sealed record Step(Tool Tool, JsonObject Arguments, int Group = 0, string? Select = null);

    internal IReadOnlyList<Step> Prepare(JsonObject arguments)
    {
        if (arguments.Any(p => p.Key is not ("steps" or "targetSessionId" or "previewName" or "previewUrl" or "timeout" or "verbose")))
            throw new ArgumentException("Unknown workflow argument.");
        if (arguments["steps"] is not JsonArray steps || steps.Count is < 1 or > 64)
            throw new ArgumentException("steps must contain 1-64 named tool calls.");
        List<Step> prepared = [with(capacity: steps.Count)];
        var group = 0;
        foreach (var node in steps)
        {
            var groupStart = prepared.Count;
            var parallel = node is JsonObject obj ? obj["parallel"] as JsonArray : null;
            if (parallel is not null && (((JsonObject)node!).Count != 1 || parallel.Count is < 1 or > 8))
                throw new ArgumentException("A parallel group contains 1-8 independent calls and no other fields.");
            foreach (var call in parallel ?? new JsonArray(node?.DeepClone()))
                Add(call, groupStart, group);
            group++;
        }
        return prepared;

        void Add(JsonNode? node, int referenceLimit, int group)
        {
            JsonObject step;
            if (node is JsonValue text && text.TryGetValue<string>(out var toolName))
                step = new JsonObject { ["tool"] = toolName };
            else if (node is JsonArray tuple && tuple.Count is 1 or 2 && tuple[0] is JsonValue nameValue
                && nameValue.TryGetValue<string>(out var tupleName))
                step = new JsonObject { ["tool"] = tupleName, ["arguments"] = tuple.Count == 2 ? tuple[1]?.DeepClone() : null };
            else if (node is JsonObject named) step = named;
            else throw new ArgumentException("A call is a tool name, [tool, arguments], or {tool, arguments?, select?}.");
            if (step.Any(p => p.Key is not ("tool" or "arguments" or "select")))
                throw new ArgumentException("Unknown step field.");
            if (prepared.Count >= 64) throw new ArgumentException("A workflow contains at most 64 calls, including parallel groups.");
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
            var select = step["select"]?.GetValue<string>();
            if (select is { Length: > 0 } && !select.StartsWith('/', StringComparison.Ordinal))
                throw new ArgumentException("select must be a JSON Pointer.");
            ValidateReferences(values, referenceLimit);
            ValidateShape(values, schema, referenceLimit);
            prepared.Add(new Step(tool, values, group, select));
        }
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
                    || !path.TryGetValue<string>(out var pointer) || pointer.Length > 0 && !pointer.StartsWith('/', StringComparison.Ordinal))
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
            JsonNode? selected = results[index].StructuredContent is { } content ? JsonNode.Parse(JsonMarshal.GetRawUtf8Value(content)) : null;
            var path = obj["path"]!.GetValue<string>();
            if (path.Length > 0 && !path.StartsWith('/', StringComparison.Ordinal)) throw new ArgumentException("Result path must be a JSON Pointer.");
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


    private static JsonNode? Select(JsonNode? value, string pointer)
    {
        foreach (var segment in pointer.Split('/').Skip(1))
        {
            var key = segment.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
            if (value is JsonObject obj && obj.TryGetPropertyValue(key, out var property)) value = property;
            else if (value is JsonArray array && int.TryParse(key, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index >= 0 && index < array.Count) value = array[index];
            else throw new ArgumentException("Result select pointer could not be resolved: " + pointer);
        }
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
            var arguments = new JsonObject(parameters.Arguments?.Select(p => KeyValuePair.Create(p.Key, JsonNode.Parse(JsonMarshal.GetRawUtf8Value(p.Value)))) ?? []);
            var prepared = Prepare(arguments); // all names, scopes and literal argument shapes checked before opening
            completed.EnsureCapacity(prepared.Count);
            contents.EnsureCapacity(prepared.Count);
            var seconds = arguments["timeout"]?.GetValue<int>() ?? 180;
            if (seconds is < 1 or > 600) throw new ArgumentException("Workflow timeout must be 1-600 seconds.");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(seconds));
            var verbose = arguments["verbose"]?.GetValue<bool>() ?? false;
            async Task<bool> Run(CancellationToken ct)
            {
                foreach (var group in prepared.GroupBy(step => step.Group))
                {
                    ct.ThrowIfCancellationRequested();
                    // Resolve every argument before starting any sibling. References may only
                    // address earlier groups, never racing siblings in this parallel group.
                    var calls = group.Select(step =>
                    {
                        var resolved = Resolve(step.Arguments, completed)!.AsObject();
                        ValidateShape(resolved, step.Tool.InputSchema, 0);
                        var json = JsonSerializer.SerializeToElement(resolved, McpJsonContext.Default.JsonObject);
                        using var properties = json.EnumerateObject();
                        var values = properties.ToDictionary(p => p.Name, p => p.Value, StringComparer.Ordinal);
                        return (Step: step, Values: values);
                    }).ToArray();
                    async Task<CallToolResult> Invoke(Step step, IDictionary<string, JsonElement> values)
                    {
                        try { return await invoke(step.Tool.Name, values, ct).ConfigureAwait(false); }
                        catch (Exception ex)
                        {
                            // A lost response is an unknown outcome, not proof that a mutation failed.
                            return McpApiResult.Error("TLBX_MCP_STEP_OUTCOME_UNKNOWN", ex.Message + " Inspect the outcome before retrying.");
                        }
                    }
                    // All dispatched siblings are observed, even if one fails. No automatic retry,
                    // rollback or early return that could hide an in-flight mutation's outcome.
                    var results = await Task.WhenAll(calls.Select(call => Invoke(call.Step, call.Values))).ConfigureAwait(false);
                    for (var i = 0; i < calls.Length; i++)
                    {
                        var step = calls[i].Step;
                        var result = results[i];
                        completed.Add(result);
                        var data = result.StructuredContent is { } structured ? JsonNode.Parse(JsonMarshal.GetRawUtf8Value(structured)) : null;
                        if (result.IsError != true && step.Select is not null)
                        {
                            try { data = Select(data, step.Select); }
                            catch (ArgumentException ex) { failure ??= ex.Message + " The call already completed; do not replay it."; }
                        }
                        var blocks = verbose || result.StructuredContent is null ? result.Content
                            : result.Content.Where(block => block is not TextContentBlock text
                                || text.Text != result.StructuredContent.Value.GetRawText()).ToList();
                        evidence.Add((JsonNode)new JsonObject
                        {
                            ["tool"] = step.Tool.Name, ["isError"] = result.IsError == true,
                            ["structuredContent"] = data,
                            ["contentStart"] = contents.Count + 1, ["contentCount"] = blocks.Count
                        });
                        contents.AddRange(blocks);
                        if (result.IsError == true) failure ??= "A step failed. Dispatched siblings were observed; completed calls were not rolled back.";
                    }
                    if (failure is not null) break;
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

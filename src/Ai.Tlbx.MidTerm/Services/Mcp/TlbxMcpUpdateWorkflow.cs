using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>Native caller survives the update; the mutating request is always dispatched once.</summary>
internal static class TlbxMcpUpdateWorkflow
{
    internal static Tool ProtocolTool { get; } = new()
    {
        Name = "update_apply",
        Description = "Apply an update once, then wait for a different server instance and the expected version. A lost response or cancellation after dispatch is uncertain, never permission to replay. Source defaults to the update channel; explicit sources may supply expectedVersion for version verification.",
        InputSchema = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["source"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                ["forceFull"] = new JsonObject { ["type"] = "boolean", ["default"] = false },
                ["detached"] = new JsonObject { ["type"] = "boolean", ["const"] = true, ["default"] = true },
                ["expectedVersion"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                ["timeoutSeconds"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 600, ["default"] = 120 }
            }
        }, McpJsonContext.Default.JsonObject),
        Annotations = new ToolAnnotations { ReadOnlyHint = false, DestructiveHint = true, IdempotentHint = false, OpenWorldHint = true }
    };

    internal static async ValueTask<CallToolResult> InvokeAsync(CallToolRequestParams parameters,
        Func<string, IDictionary<string, JsonElement>, CancellationToken, ValueTask<CallToolResult>> invoke,
        CancellationToken cancellationToken, TimeSpan? pollInterval = null)
    {
        var args = parameters.Arguments ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        string? expected;
        string? source;
        var timeout = 120;
        try
        {
            if (args.Keys.Any(key => key is not ("source" or "forceFull" or "detached" or "expectedVersion" or "timeoutSeconds")))
                throw new ArgumentException("Unknown update argument.");
            source = ReadString(args, "source");
            expected = ReadString(args, "expectedVersion");
            if (args.TryGetValue("forceFull", out var force) && force.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("forceFull must be boolean.");
            if (args.TryGetValue("detached", out var detached) && detached.ValueKind != JsonValueKind.True)
                throw new ArgumentException("Native update recovery requires detached=true.");
            if (args.TryGetValue("timeoutSeconds", out var seconds) && (!seconds.TryGetInt32(out timeout) || timeout is < 1 or > 600))
                throw new ArgumentException("timeoutSeconds must be an integer from 1 to 600.");
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
        { return McpApiResult.Error("TLBX_MCP_INVALID_ARGUMENT", ex.Message); }

        var dispatched = false;
        var acknowledged = false;
        string? observedVersion = null;
        string? initialInstance = null;
        string? observedInstance = null;
        string? previousResult = null;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(timeout));
        var ct = deadline.Token;
        CallToolResult Outcome(string code, string message, bool error = true) => McpApiResult.Structured(new JsonObject
        {
            ["errorCode"] = error ? code : null, ["outcome"] = code, ["message"] = message,
            ["dispatched"] = dispatched, ["acknowledged"] = acknowledged,
            ["expectedVersion"] = expected, ["serverVersion"] = observedVersion,
            ["expectedVersionVerified"] = expected is not null && expected == observedVersion,
            ["restartVerified"] = observedInstance is not null && observedInstance != initialInstance,
            ["serverInstanceId"] = observedInstance, ["replaySafe"] = !dispatched
        }, error);
        try
        {
            var context = await invoke("context_get", new Dictionary<string, JsonElement>(StringComparer.Ordinal), ct).ConfigureAwait(false);
            if (context.IsError == true) return Outcome("TLBX_MCP_UPDATE_PREFLIGHT", "Context verification failed; no update was dispatched.");
            initialInstance = String(Data(context), "serverInstanceId");
            if (initialInstance is null) return Outcome("TLBX_MCP_UPDATE_PREFLIGHT", "Server does not expose restart identity; no update was dispatched.");
            var status = await invoke("update_status", new Dictionary<string, JsonElement>(StringComparer.Ordinal), ct).ConfigureAwait(false);
            if (status.IsError == true) return Outcome("TLBX_MCP_UPDATE_PREFLIGHT", "Update discovery failed; no update was dispatched.");
            var data = Data(status);
            previousResult = LastResult(data);
            if (source is null)
            {
                if (String(data, "checkError") is { Length: > 0 } checkError)
                    return Outcome("TLBX_MCP_UPDATE_PREFLIGHT", checkError);
                if (data.TryGetProperty("available", out var available) && available.ValueKind == JsonValueKind.False &&
                    (!args.TryGetValue("forceFull", out var forceFull) || forceFull.ValueKind != JsonValueKind.True))
                    return Outcome("no_update", "No update is available; no update was dispatched.", false);
                expected ??= String(data, "latestVersion");
                if (expected is null) return Outcome("TLBX_MCP_UPDATE_PREFLIGHT", "No expected update version was discovered; no update was dispatched.");
            }
            var apply = args.Where(pair => pair.Key is "source" or "forceFull").ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            using var detachedValue = JsonDocument.Parse("true");
            apply["detached"] = detachedValue.RootElement.Clone();
            ct.ThrowIfCancellationRequested();
            dispatched = true;
            try
            {
                var response = await invoke("update_apply", apply, ct).ConfigureAwait(false);
                if (response.IsError == true) return Outcome("TLBX_MCP_UPDATE_FAILED", "The server explicitly rejected the update: " + Detail(response));
                acknowledged = true;
            }
            catch (Exception ex) when (Recoverable(ex) && !ct.IsCancellationRequested)
            { /* A lost POST response is not failure proof. Reconnect with reads only. */ }

            while (true)
            {
                ct.ThrowIfCancellationRequested();
                try
                {
                    var current = await invoke("context_get", new Dictionary<string, JsonElement>(StringComparer.Ordinal), ct).ConfigureAwait(false);
                    if (current.IsError != true)
                    {
                        var currentData = Data(current);
                        observedVersion = String(currentData, "serverVersion");
                        observedInstance = String(currentData, "serverInstanceId");
                    }
                    var progress = await invoke("update_status", new Dictionary<string, JsonElement>(StringComparer.Ordinal), ct).ConfigureAwait(false);
                    if (progress.IsError != true)
                    {
                        var progressData = Data(progress);
                        var last = LastResult(progressData);
                        if (last is not null && last != previousResult && progressData.TryGetProperty("lastResult", out var result) &&
                            result.TryGetProperty("found", out var found) && found.ValueKind == JsonValueKind.True &&
                            result.TryGetProperty("success", out var success) && success.ValueKind == JsonValueKind.False)
                            return Outcome("TLBX_MCP_UPDATE_FAILED", "The server recorded a new failed update result: " + String(result, "message"));
                        // Read the result before accepting restart/version proof: a failed update can
                        // roll back and restart, including at an explicitly requested same version.
                        if (current.IsError != true && observedInstance is not null && observedInstance != initialInstance &&
                            observedVersion is not null && (expected is null || observedVersion == expected))
                            return Outcome("updated", "A restarted server was verified" + (expected is null ? "; no expected source version was supplied." : " at the expected version."), false);
                    }
                }
                catch (Exception ex) when (Recoverable(ex) && !ct.IsCancellationRequested) { }
                await Task.Delay(pollInterval ?? TimeSpan.FromSeconds(1), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return Outcome(dispatched ? "TLBX_MCP_UPDATE_UNCERTAIN" : "TLBX_MCP_UPDATE_NOT_DISPATCHED",
                dispatched ? "Update was dispatched once; recovery was cancelled or timed out. Its outcome is uncertain. Inspect the server before any explicit retry."
                    : "Update preflight was cancelled or timed out; no update was dispatched.");
        }
        catch (Exception ex) when (Recoverable(ex) || ex is JsonException)
        {
            return Outcome(dispatched ? "TLBX_MCP_UPDATE_UNCERTAIN" : "TLBX_MCP_UPDATE_PREFLIGHT",
                dispatched ? "Update outcome is uncertain. Inspect the server before any explicit retry." : "Update preflight failed; no update was dispatched.");
        }
    }

    private static bool Recoverable(Exception ex) => ex is IOException or HttpRequestException or InvalidOperationException or McpException or OperationCanceledException;
    private static string? ReadString(IDictionary<string, JsonElement> args, string name)
        => !args.TryGetValue(name, out var value) ? null : value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString() : throw new ArgumentException(name + " must be a nonempty string.");
    private static JsonElement Data(CallToolResult result)
    {
        var data = result.StructuredContent ?? throw new JsonException("Missing structured update response.");
        return data.TryGetProperty("data", out var inner) ? inner : data;
    }
    private static string? String(JsonElement data, string name) => data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString() : null;
    private static string? LastResult(JsonElement data) => data.TryGetProperty("lastResult", out var result) && result.ValueKind == JsonValueKind.Object ? result.GetRawText() : null;
    private static string Detail(CallToolResult result) => result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "No diagnostic returned.";
}

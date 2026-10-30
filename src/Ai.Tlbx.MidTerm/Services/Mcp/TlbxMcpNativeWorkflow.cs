using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>Caller-local routing over the same workflow engine; preview lifecycles remain owned by the server.</summary>
internal static class TlbxMcpNativeWorkflow
{
    internal static async ValueTask<CallToolResult> InvokeAsync(CallToolRequestParams parameters, McpClient upstream, CancellationToken cancellationToken)
    {
        var tools = new Dictionary<string, Tool>(StringComparer.Ordinal);
        var cursors = new HashSet<string>(StringComparer.Ordinal);
        string? cursor = null;
        do
        {
            var page = await TlbxMcpStdioCommand.ForwardAsync(upstream, new JsonRpcRequest
            {
                Id = new RequestId(0), Method = "tools/list", Params = cursor is null ? new JsonObject() : new JsonObject { ["cursor"] = cursor }
            }, McpClientJsonContext.Default.ListToolsResult, cancellationToken).ConfigureAwait(false);
            foreach (var tool in page.Tools) tools[tool.Name] = tool;
            cursor = page.NextCursor;
            if (cursor is not null && (!cursors.Add(cursor) || cursors.Count > 128))
                return McpApiResult.Error("TLBX_MCP_WORKFLOW_DISCOVERY", "Upstream tool discovery did not terminate; no workflow step was executed.");
        } while (cursor is not null);
        tools[TlbxMcpIsolatedProcessTool.ProtocolTool.Name] = TlbxMcpIsolatedProcessTool.ProtocolTool;
        tools[TlbxMcpFileCallTool.ToolName] = TlbxMcpFileCallTool.Definition;
        tools[TlbxMcpArtifactSaveTool.ToolName] = TlbxMcpArtifactSaveTool.Definition;
        if (tools.ContainsKey("update_apply")) tools["update_apply"] = TlbxMcpUpdateWorkflow.ProtocolTool;
        var executor = new TlbxMcpWorkflowExecutor(tools.Values);
        ValueTask<CallToolResult> Invoke(string name, IDictionary<string, JsonElement> arguments, CancellationToken ct)
            => InvokeNativeStepAsync(upstream, new CallToolRequestParams { Name = name, Arguments = arguments }, ct);

        async Task<bool> WithPreview(string? target, string? name, string url,
            Func<TlbxMcpWorkflowExecutor.PreviewScope, CancellationToken, Task<bool>> body, CancellationToken ct)
        {
            if (!tools.ContainsKey("browser_scope_open") || !tools.ContainsKey("browser_scope_close"))
                throw new InvalidOperationException("This server does not support actor-bound ephemeral preview scopes; no preview was opened.");
            var values = new JsonObject { ["url"] = url };
            if (target is not null) values["targetSessionId"] = target;
            if (name is not null) values["previewName"] = name;
            var opened = await CallUpstreamAsync(upstream, "browser_scope_open", Arguments(values), ct).ConfigureAwait(false);
            if (opened.IsError == true) throw new InvalidOperationException("Ephemeral preview open failed: " + Detail(opened));
            var data = opened.StructuredContent ?? throw new InvalidOperationException("Preview scope response is missing its lease metadata.");
            var token = data.GetProperty("scopeToken").GetString() ?? throw new InvalidOperationException("Preview scope response is missing its token.");
            var session = data.GetProperty("sessionId").GetString() ?? throw new InvalidOperationException("Preview scope response is missing its target.");
            var preview = data.GetProperty("previewName").GetString() ?? throw new InvalidOperationException("Preview scope response is missing its preview identity.");
            var completed = false;
            var result = false;
            Exception? cleanupFailure = null;
            try { result = await body(new(session, preview), ct).ConfigureAwait(false); completed = true; }
            finally
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(15));
                try
                {
                    var closed = await CallUpstreamAsync(upstream, "browser_scope_close", Arguments(new JsonObject { ["scopeToken"] = token }), cleanup.Token).ConfigureAwait(false);
                    if (closed.IsError == true)
                    {
                        if (completed) cleanupFailure = new InvalidOperationException(Detail(closed));
                        else Console.Error.WriteLine("TLBX_MCP_PREVIEW_CLEANUP: " + Detail(closed));
                    }
                }
                catch (Exception ex) when (!completed)
                {
                    Console.Error.WriteLine("TLBX_MCP_PREVIEW_CLEANUP: " + ex.Message);
                }
                catch (Exception ex) { cleanupFailure = ex; }
            }
            if (cleanupFailure is not null)
                throw new InvalidOperationException("Workflow steps completed but preview cleanup failed. Do not replay completed steps.", cleanupFailure);
            return result;
        }
        return await executor.ExecuteAsync(parameters, Invoke, WithPreview, cancellationToken).ConfigureAwait(false);
    }

    private static Dictionary<string, JsonElement> Arguments(JsonObject value)
    {
        var json = JsonSerializer.SerializeToElement(value, McpJsonContext.Default.JsonObject);
        using var properties = json.EnumerateObject();
        return properties.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);
    }

    internal static ValueTask<CallToolResult> CallUpstreamAsync(McpClient upstream, string name,
        IDictionary<string, JsonElement> arguments, CancellationToken ct)
        => TlbxMcpStdioCommand.ForwardAsync(upstream, new JsonRpcRequest
        {
            Id = new RequestId(0), Method = "tools/call",
            Params = new JsonObject
            {
                ["name"] = name,
                ["arguments"] = new JsonObject(arguments.Select(pair => KeyValuePair.Create(pair.Key, JsonNode.Parse(pair.Value.GetRawText()))))
            }
        }, McpClientJsonContext.Default.CallToolResult, ct);

    internal static ValueTask<CallToolResult> InvokeNativeStepAsync(McpClient upstream, CallToolRequestParams call, CancellationToken ct)
    {
        if (call.Name == "process_run_isolated") return TlbxMcpIsolatedProcessTool.InvokeLocalAsync(call, ct);
        if (call.Name == TlbxMcpFileCallTool.ToolName) return TlbxMcpFileCallTool.InvokeLocalAsync(call,
            (name, values, token) => InvokeNativeStepAsync(upstream, new CallToolRequestParams { Name = name, Arguments = values }, token), ct);
        if (call.Name == TlbxMcpArtifactSaveTool.ToolName) return TlbxMcpArtifactSaveTool.InvokeLocalAsync(call,
            (name, values, token) => CallUpstreamAsync(upstream, name, values, token), ct);
        if (call.Name == "update_apply") return TlbxMcpUpdateWorkflow.InvokeAsync(call,
            (name, values, token) => CallUpstreamAsync(upstream, name, values, token), ct);
        return CallUpstreamAsync(upstream, call.Name, call.Arguments ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal), ct);
    }

    private static string Detail(CallToolResult result) => result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text ?? "No diagnostic was returned.";
}

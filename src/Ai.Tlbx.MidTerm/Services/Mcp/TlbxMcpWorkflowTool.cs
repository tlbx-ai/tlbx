using System.Text.Json;
using System.Text.Json.Nodes;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Sessions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

internal sealed class TlbxMcpWorkflowTool(IEnumerable<McpServerTool> tools) : McpServerTool
{
    private readonly Dictionary<string, McpServerTool> _tools = tools.ToDictionary(tool => tool.ProtocolTool.Name, StringComparer.Ordinal);
    private static readonly Tool Definition = new()
    {
        Name = "workflow_run",
        Description = "Execute 1-64 named MCP tools sequentially, preserving partial results. Optional fresh previewUrl/previewName scope is always closed after execution. Already completed steps remain committed. Arguments are literal JSON; {$step:0,path:'/id'} references an earlier structured result by JSON Pointer. Wrap reserved-key literal objects as {$literal:originalJson}. No retries or recursive workflows.",
        InputSchema = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("steps"),
            ["properties"] = new JsonObject
            {
                ["targetSessionId"] = new JsonObject { ["type"] = "string" },
                ["previewName"] = new JsonObject { ["type"] = "string" },
                ["previewUrl"] = new JsonObject { ["type"] = "string" },
                ["timeout"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 600 },
                ["steps"] = new JsonObject
                {
                    ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 64,
                    ["items"] = new JsonObject
                    {
                        ["type"] = "object", ["required"] = new JsonArray("tool"), ["additionalProperties"] = false,
                        ["properties"] = new JsonObject
                        {
                            ["tool"] = new JsonObject { ["type"] = "string" },
                            ["arguments"] = new JsonObject { ["type"] = "object" }
                        }
                    }
                }
            }
        }, McpJsonContext.Default.JsonObject)
    };

    public override Tool ProtocolTool => Definition;
    public override IReadOnlyList<object> Metadata => [];

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        var services = request.Services!;
        var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext;
        if (http is null || TlbxMcpEndpoints.Authorize(http, services.GetRequiredService<SessionCliContextService>()) is not null)
            return McpApiResult.Error("TLBX_MCP_CONTEXT_INVALID", "A fresh authenticated caller context is required.");
        var executor = new TlbxMcpWorkflowExecutor(_tools.Values.Select(tool => tool.ProtocolTool));
        async ValueTask<CallToolResult> Invoke(string name, IDictionary<string, JsonElement> values, CancellationToken ct)
        {
            var tool = _tools[name];
            var context = new JsonRpcMessageContext { User = request.User, Items = new Dictionary<string, object?>(StringComparer.Ordinal) };
            foreach (var item in request.Items) context.Items[item.Key] = item.Value;
            var message = new JsonRpcRequest { Id = request.JsonRpcRequest.Id, Method = "tools/call", Context = context };
            var child = new RequestContext<CallToolRequestParams>(request.Server, message,
                new CallToolRequestParams { Name = name, Arguments = values })
            { Services = request.Services, MatchedPrimitive = tool };
            return await tool.InvokeAsync(child, ct).ConfigureAwait(false);
        }
        async Task<bool> WithPreview(string? target, string? name, string url,
            Func<TlbxMcpWorkflowExecutor.PreviewScope, CancellationToken, Task<bool>> body, CancellationToken ct)
        {
            target ??= http.Items[TlbxMcpEndpoints.SessionItem] as string;
            if (target is null) throw new ArgumentException("An unbound caller must supply targetSessionId.");
            if (services.GetRequiredService<TtyHostSessionManager>().GetSession(target) is null)
                throw new ArgumentException("The preview target session does not exist.");
            var actor = (string)http.Items[TlbxMcpEndpoints.CallerIdentityItem]!;
            return await services.GetRequiredService<BrowserPreviewOperations>().WithPreviewScopeAsync(actor, target, name, url,
                (scope, token) => body(new(scope.SessionId, scope.PreviewName), token), ct).ConfigureAwait(false);
        }
        return await executor.ExecuteAsync(request.Params!, Invoke, WithPreview, cancellationToken).ConfigureAwait(false);
    }
}

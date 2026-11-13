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
        Description = "Chain up to 64 calls. Call forms: \"sessions_list\", [\"session_set_topic\",{\"topic\":\"task\"}], or {tool,arguments?,select?}. Use {parallel:[calls]} for 1-8 independent concurrent calls. Groups run in order; {$step:0,path:'/id'} references full earlier results by flattened call index. select is a JSON Pointer limiting returned data only. Compact by default; verbose:true retains child text. Failures stop later groups, observe dispatched siblings, never retry or roll back. Optional fresh previewUrl scope is always closed. Wrap reserved literals as {$literal:value}.",
        InputSchema = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("steps"),
            ["properties"] = new JsonObject
            {
                ["targetSessionId"] = new JsonObject { ["type"] = "string" },
                ["previewName"] = new JsonObject { ["type"] = "string" },
                ["previewUrl"] = new JsonObject { ["type"] = "string" },
                ["verbose"] = new JsonObject { ["type"] = "boolean", ["default"] = false },
                ["timeout"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 600 },
                ["steps"] = new JsonObject
                {
                    ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 64,
                    ["items"] = new JsonObject
                    {
                        ["description"] = "Tool name, [tool,arguments], {tool,arguments?,select?}, or {parallel:[calls]}. Parallel calls cannot reference siblings.",
                        ["anyOf"] = new JsonArray(
                            new JsonObject { ["type"] = "string" },
                            new JsonObject { ["type"] = "array", ["minItems"] = 1, ["maxItems"] = 2 },
                            new JsonObject { ["type"] = "object" })
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
            { Services = new StepServices(services, CloneContext(http, ct)), MatchedPrimitive = tool };
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
    private static HttpContext CloneContext(HttpContext source, CancellationToken ct)
    {
        var clone = new DefaultHttpContext { RequestServices = source.RequestServices, User = source.User, RequestAborted = ct };
        foreach (var item in source.Items) clone.Items[item.Key] = item.Value;
        foreach (var header in source.Request.Headers) clone.Request.Headers[header.Key] = header.Value;
        clone.Request.Scheme = source.Request.Scheme;
        clone.Request.Host = source.Request.Host;
        clone.Request.PathBase = source.Request.PathBase;
        clone.Connection.RemoteIpAddress = source.Connection.RemoteIpAddress;
        clone.Connection.LocalIpAddress = source.Connection.LocalIpAddress;
        clone.Connection.RemotePort = source.Connection.RemotePort;
        clone.Connection.LocalPort = source.Connection.LocalPort;
        return clone;
    }

    private sealed class StepServices(IServiceProvider inner, HttpContext context) : IServiceProvider
    {
        private readonly IHttpContextAccessor _accessor = new StepContext { HttpContext = context };
        public object? GetService(Type type) => type == typeof(IHttpContextAccessor) ? _accessor : inner.GetService(type);
    }

    private sealed class StepContext : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

}

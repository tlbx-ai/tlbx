using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>The same catalog exposes the execution boundary explicitly for HTTP clients.</summary>
internal sealed class TlbxMcpClientLocalTool(Tool? definition = null) : McpServerTool
{
    public override Tool ProtocolTool => definition ?? TlbxMcpIsolatedProcessTool.ProtocolTool;
    public override IReadOnlyList<object> Metadata => [];

    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(McpApiResult.Error("TLBX_MCP_CLIENT_LOCAL_REQUIRED",
            ProtocolTool.Name + " operates on the caller machine as its OS user. Use mt mcp --stdio; the HTTP service cannot operate on a remote caller's processes or files."));
    }
}

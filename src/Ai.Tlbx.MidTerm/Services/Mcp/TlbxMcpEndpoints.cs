using System.Net;
using Ai.Tlbx.MidTerm.Services.Sessions;
using ModelContextProtocol.AspNetCore;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>Local experimental MCP surface. CLI authentication and context remain authoritative.</summary>
internal static class TlbxMcpEndpoints
{
    internal const string Path = "/api/mcp";
    internal const string SessionItem = "TlbxMcpSession";

    internal static void AddServices(IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddMcpServer(options => options.ServerInfo = new()
        {
            Name = "TLBX MCP",
            Version = Startup.CliCommands.GetVersion()
        }).WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
          .WithTools(TlbxMcpTool.CreateTools());
    }

    internal static void Map(WebApplication app)
    {
        // Runs after normal tlbx authentication/origin checks; no public endpoint,
        // query-string credential, share-cookie access or remote PID attribution.
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments(Path, StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }

            var error = Authorize(context, app.Services.GetRequiredService<SessionCliContextService>());
            if (error is not null)
            {
                context.Response.StatusCode = error.Value.Status;
                await context.Response.WriteAsync(error.Value.Message, context.RequestAborted);
                return;
            }
            context.Response.Headers.CacheControl = "no-store";
            await next(context);
        });
        app.MapMcp(Path);
    }

    internal static (int Status, string Message)? Authorize(HttpContext context, SessionCliContextService leases)
    {
        if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
            return (403, "TLBX_MCP_LOCAL_ONLY: Run the MCP client on the owning tlbx host.");
        if (context.Request.Headers.Keys.Any(header =>
            header.Equals("Forwarded", StringComparison.OrdinalIgnoreCase)
            || header.Equals("Via", StringComparison.OrdinalIgnoreCase)
            || header.Equals("X-Real-IP", StringComparison.OrdinalIgnoreCase)
            || header.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
            || header.Equals("X-MidTerm-Internal-Proxy", StringComparison.OrdinalIgnoreCase)))
            return (403, "TLBX_MCP_LOCAL_ONLY: Proxied requests cannot establish local caller identity.");
        var values = context.Request.Headers[SessionCliContextService.LeaseHeader];
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
            return (409, "TLBX_MCP_CONTEXT_REQUIRED: Obtain a fresh verified CLI context; no tool was executed.");
        var sessionId = leases.GetLeaseSessionId(values[0]!);
        if (sessionId is null)
            return (409, "TLBX_MCP_CONTEXT_EXPIRED: Obtain a fresh verified CLI context; no tool was executed.");
        context.Items[SessionItem] = sessionId;
        return null;
    }
}

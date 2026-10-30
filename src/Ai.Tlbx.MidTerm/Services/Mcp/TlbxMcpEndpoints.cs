using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Services.Sessions;
using ModelContextProtocol.AspNetCore;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>Authenticated experimental MCP surface with verified caller scope.</summary>
internal static class TlbxMcpEndpoints
{
    internal const string Path = "/api/mcp";
    internal const string SessionItem = "TlbxMcpSession";
    internal const string RuntimeItem = "TlbxMcpRuntime";
    internal const string CallerModeItem = "TlbxMcpCallerMode";
    internal const string CallerIdentityItem = "TlbxMcpCallerIdentity";
    internal const string OperatorHeader = "X-Tlbx-Mcp-Operator";

    internal static void AddServices(IServiceCollection services)
    {
        services.AddHttpContextAccessor();
        services.AddSingleton<TlbxMcpClientContextService>();
        services.AddSingleton<TlbxMcpApiInvoker>();
        services.AddSingleton<TlbxMcpArtifactStore>();
        services.AddSingleton<TlbxMcpTmuxRuntime>();
        services.AddSingleton<Browser.BrowserPreviewOperations>();
        var tools = TlbxMcpTool.CreateTools()
            .Concat(TlbxMcpApiTools.CreateTools())
            .Concat(TlbxMcpTmuxTool.CreateTools())
            .Concat(TlbxMcpArtifactTools.CreateTools())
            .Append(new TlbxMcpClientLocalTool())
            .Append(new TlbxMcpFileCallTool())
            .Concat(TlbxMcpBrowserTools.CreateTools()).ToArray();
        services.AddMcpServer(options =>
        {
            options.ServerInfo = new() { Name = "TLBX MCP", Version = Startup.CliCommands.GetVersion() };
            options.ServerInstructions = "Use context_get to inspect verified self before session-scoped work. Explicit operator mode has no self: name targetSessionId where required. Browser previews belong to their session; select previewName explicitly and never silently claim another owner. Arguments are structured JSON: preserve literal text, newlines, paths and provider options. Mutations execute once; a timeout or lost connection is not permission to replay. The maintained CLI remains available until MCP parity is proven.";
        }).WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
          // Named arguments select the SDK's non-reflective tool-instance overload.
          .WithTools(tools: tools)
          .WithTools(tools: [new TlbxMcpWorkflowTool(tools)])
          .WithListResourcesHandler((request, ct) => request.Services!.GetRequiredService<TlbxMcpArtifactStore>().ListAsync(request, ct))
          .WithReadResourceHandler((request, ct) => request.Services!.GetRequiredService<TlbxMcpArtifactStore>().ReadAsync(request, ct));
    }

    internal static void Map(WebApplication app)
    {
        // Runs after normal tlbx authentication/origin checks; no public endpoint,
        // query-string credential, share-cookie access or remote PID attribution.
        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments(Path, StringComparison.OrdinalIgnoreCase)
                || context.Request.Path.Equals(TlbxMcpClientContextService.ContextPath, StringComparison.OrdinalIgnoreCase))
            {
                await next(context);
                return;
            }

            context.Response.Headers.CacheControl = "no-store";
            var error = Authorize(context, app.Services.GetRequiredService<SessionCliContextService>(), allowDiscovery: true);
            if (error is null && context.Items[CallerModeItem] as string == "discovery")
                error = await ValidateDiscoveryRequestAsync(context);
            if (error is not null)
            {
                context.Response.StatusCode = error.Value.Status;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync(error.Value.Message, context.RequestAborted);
                return;
            }
            await next(context);
        });
        app.MapMcp(Path);
        TlbxMcpClientContextService.MapEndpoint(app);
    }

    internal static (int Status, string Message)? Authorize(HttpContext context, SessionCliContextService leases, bool allowDiscovery = false)
    {
        if (!RequestAccessContext.HasFullUserAccess(context))
            return (401, "TLBX_MCP_AUTH_REQUIRED: Full tlbx authentication is required.");
        context.Items.Remove(SessionItem);
        context.Items.Remove(RuntimeItem);
        context.Items.Remove(CallerIdentityItem);
        context.Items.Remove(CallerModeItem);
        var runtime = context.Request.Headers[TlbxMcpClientContextService.RuntimeHeader];
        var legacy = context.Request.Headers[SessionCliContextService.LeaseHeader];
        var operatorMode = context.Request.Headers[OperatorHeader];
        if (operatorMode.Count != 0)
        {
            if (operatorMode.Count != 1 || operatorMode[0] != "explicit" || runtime.Count != 0 || legacy.Count != 0)
                return (409, "TLBX_MCP_AMBIGUOUS_CONTEXT: Operator mode cannot claim a local process or session binding.");
            // Native owner clients may use the same remote authenticated authority
            // as the CLI. No server-local PID or implicit self is invented.
            var identity = AuthenticatedOperatorIdentity(context);
            if (identity is null) return (401, "TLBX_MCP_AUTH_REQUIRED: Operator mode requires an authenticated credential.");
            context.Items[CallerIdentityItem] = identity;
            context.Items[CallerModeItem] = "operator";
            return null;
        }
        if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote))
            return (403, "TLBX_MCP_LOCAL_ONLY: Run the MCP client on the owning tlbx host.");
        if (context.Request.Headers.Keys.Any(header =>
            header.Equals("Forwarded", StringComparison.OrdinalIgnoreCase)
            || header.Equals("Via", StringComparison.OrdinalIgnoreCase)
            || header.Equals("X-Real-IP", StringComparison.OrdinalIgnoreCase)
            || header.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase)
            || header.Equals("X-MidTerm-Internal-Proxy", StringComparison.OrdinalIgnoreCase)))
            return (403, "TLBX_MCP_LOCAL_ONLY: Proxied requests cannot establish local caller identity.");
        if (runtime.Count != 0)
        {
            if (runtime.Count != 1 || legacy.Count != 0 || string.IsNullOrWhiteSpace(runtime[0]))
                return (409, "TLBX_MCP_AMBIGUOUS_CONTEXT: Supply exactly one runtime credential.");
            var binding = context.RequestServices.GetRequiredService<TlbxMcpClientContextService>().Validate(runtime[0]!);
            if (binding is null) return (409, "TLBX_MCP_CONTEXT_EXPIRED: Re-establish this runtime's verified context; no tool was executed.");
            if (binding.Mode == "discovery")
            {
                if (!allowDiscovery) return (409, "TLBX_MCP_THREAD_PROOF_REQUIRED: Discovery cannot execute tools or access caller resources.");
                context.Items[CallerModeItem] = "discovery";
                return null;
            }
            context.Items[RuntimeItem] = binding;
            context.Items[CallerModeItem] = binding.Mode;
            if (binding.SessionId is not null) context.Items[SessionItem] = binding.SessionId;
            context.Items[CallerIdentityItem] = binding.ActorIdentity;
            return null;
        }
        var values = context.Request.Headers[SessionCliContextService.LeaseHeader];
        if (values.Count != 1 || string.IsNullOrWhiteSpace(values[0]))
            return (409, "TLBX_MCP_CONTEXT_REQUIRED: Obtain a fresh verified CLI context; no tool was executed.");
        var sessionId = leases.GetLeaseSessionId(values[0]!);
        if (sessionId is null)
            return (409, "TLBX_MCP_CONTEXT_EXPIRED: Obtain a fresh verified CLI context; no tool was executed.");
        context.Items[SessionItem] = sessionId;
        context.Items[CallerModeItem] = "cli";
        var actor = leases.GetLeaseActorIdentity(values[0]!);
        if (actor is null) return (409, "TLBX_MCP_CONTEXT_EXPIRED: The verified caller changed during authorization.");
        context.Items[CallerIdentityItem] = actor;
        return null;
    }

    internal static async ValueTask<(int Status, string Message)?> ValidateDiscoveryRequestAsync(HttpContext context)
    {
        const int maximumBytes = 65536;
        if (!HttpMethods.IsPost(context.Request.Method) || !context.Request.HasJsonContentType())
            return (409, "TLBX_MCP_THREAD_PROOF_REQUIRED: Discovery accepts JSON-RPC descriptors only.");
        if (context.Request.ContentLength > maximumBytes || context.Request.Body.CanSeek
            && context.Request.Body.Length - context.Request.Body.Position > maximumBytes)
            return (413, "TLBX_MCP_DISCOVERY_TOO_LARGE: Discovery requests are limited to 64 KiB.");
        context.Request.EnableBuffering(maximumBytes, maximumBytes);
        var position = context.Request.Body.Position;
        try
        {
            using var document = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("jsonrpc", out var version) && version.ValueKind == JsonValueKind.String && version.GetString() == "2.0"
                && root.TryGetProperty("method", out var method) && method.ValueKind == JsonValueKind.String
                && method.GetString() is "server/discover" or "initialize" or "notifications/initialized" or "tools/list" or "prompts/list" or "resources/templates/list")
                return null;
            return (409, "TLBX_MCP_THREAD_PROOF_REQUIRED: This method requires an exact verified caller thread.");
        }
        catch (JsonException) { return (400, "TLBX_MCP_INVALID_JSON: Expected one JSON-RPC descriptor request."); }
        catch (IOException) { return (413, "TLBX_MCP_DISCOVERY_TOO_LARGE: Discovery requests are limited to 64 KiB."); }
        finally { context.Request.Body.Position = position; }
    }

    private static string? AuthenticatedOperatorIdentity(HttpContext context)
    {
        var auth = context.RequestServices.GetService<AuthService>()?.AuthenticateRequestWithContext(context.Request);
        if (auth?.SessionTokenId is { } sessionTokenId) return "operator:cookie:" + sessionTokenId;
        var key = context.Request.Headers["X-API-Key"].ToString();
        if (string.IsNullOrEmpty(key)) key = context.Request.Headers.Authorization.ToString();
        return string.IsNullOrWhiteSpace(key) ? null : "operator:key:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));
    }
}

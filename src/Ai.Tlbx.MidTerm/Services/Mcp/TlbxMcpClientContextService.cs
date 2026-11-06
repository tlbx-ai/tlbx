using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Settings;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>
/// Process-bound MCP credentials, independent of the short-lived CLI proof lease.
/// The signing purpose and live process/root snapshots prevent an old connection
/// from selecting a sibling after a process, session, or provider thread changes.
/// </summary>
internal sealed class TlbxMcpClientContextService
{
    internal const string RuntimeHeader = "X-Tlbx-Mcp-Context";
    internal const string ContextPath = "/api/mcp/context";
    private const string TokenPrefix = "TLBXMCP1";
    private const string DiscoveryPrefix = "TLBXMCP-DISCOVERY1";
    private const int MaximumBootstraps = 2048;
    private static readonly TimeSpan AccessLifetime = TimeSpan.FromMinutes(30);
    private readonly Lock _gate = new();
    private readonly Func<IReadOnlyList<RuntimeRoot>> _roots;
    private readonly Func<int, (int Parent, DateTime Started)?> _process;
    private readonly Func<int, bool?> _shared;
    private readonly Func<byte[]> _key;
    private readonly TimeProvider _clock;
    private readonly Dictionary<string, Bootstrap> _bootstraps = new(StringComparer.Ordinal);
    private readonly Dictionary<int, DateTimeOffset> _revokedBefore = [];

    public TlbxMcpClientContextService(TtyHostSessionManager sessions,
        SessionAppServerControlHostRuntimeService hosts, SettingsService settings)
        : this(() => ReadRoots(sessions, hosts), SessionProcessContext.ReadProcess,
            () => Encoding.UTF8.GetBytes(settings.Load().SessionSecret ?? throw new InvalidOperationException("tlbx authentication is not initialized.")),
            TimeProvider.System, SessionProcessContext.IsSharedCodexBackend)
    {
        sessions.OnSessionClosed += RevokeSession;
    }

    internal TlbxMcpClientContextService(Func<IReadOnlyList<RuntimeRoot>> roots,
        Func<int, (int Parent, DateTime Started)?> process, Func<byte[]> key, TimeProvider clock,
        Func<int, bool?>? shared = null)
    {
        _roots = roots; _process = process; _key = key; _clock = clock;
        _shared = shared ?? (_ => false);
    }

    internal sealed record RuntimeRoot(string SessionId, int ProcessId, int ShellPid, string Kind, string ThreadIdentity = "");
    internal sealed record Binding(string? SessionId, int ProcessId, string Mode, DateTimeOffset ExpiresAt, string ActorIdentity);
    private sealed record Bootstrap(RuntimeRoot Root, DateTime Started, bool Detached, DateTimeOffset ExpiresAt);

    private static IReadOnlyList<RuntimeRoot> ReadRoots(TtyHostSessionManager sessions,
        SessionAppServerControlHostRuntimeService hosts)
    {
        var result = sessions.GetSessionList(includeHidden: true).Sessions
            .Where(s => s.IsRunning && !s.AppServerControlOnly && s.Pid > 0)
            .Select(s => new RuntimeRoot(s.Id, s.ForegroundPid is > 0 ? s.ForegroundPid.Value : s.Pid,
                s.Pid, "terminal")).ToList();
        foreach (var host in hosts.GetMcpProcessContextRoots())
            if (!string.IsNullOrWhiteSpace(host.ScopeIdentity)) result.Add(new RuntimeRoot(host.SessionId, host.ProcessId, 0, "controller", host.ScopeIdentity));
        return result;
    }

    /// <summary>
    /// Called only by trusted runtime creation/configuration code, never a client
    /// supplied session-id endpoint. Detached clients require this secret to be
    /// injected into the exact provider thread's MCP configuration.
    /// </summary>
    internal string IssueBootstrap(string sessionId, int runtimeRootPid, bool allowDetachedClient = false)
    {
        lock (_gate)
        {
            Prune();
            var roots = _roots().Where(r => r.SessionId == sessionId && r.ProcessId == runtimeRootPid).ToArray();
            if (roots.Length != 1 || _process(runtimeRootPid) is not { } process)
                throw new InvalidOperationException("TLBX_MCP_RUNTIME_UNAVAILABLE: A unique live runtime root is required.");
            if (_bootstraps.Count >= MaximumBootstraps)
                throw new InvalidOperationException("TLBX_MCP_CONTEXT_LIMIT: Too many pending runtime bootstraps.");
            var token = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            _bootstraps.Add(token, new Bootstrap(roots[0], process.Started, allowDetachedClient, _clock.GetUtcNow().AddMinutes(2)));
            return token;
        }
    }

    /// <summary>
    /// A trusted provider-only launch authority, delivered in an owner-only file.
    /// It permits the exact provider runtime to restart its MCP child, and survives
    /// a web restart. Canonical process/session/scope changes revoke it immediately.
    /// It must never be written to provider configuration or inherited environment.
    /// </summary>
    internal string IssueLaunchCapability(string sessionId, int runtimeRootPid)
    {
        lock (_gate)
        {
            var roots = _roots().Where(r => r.SessionId == sessionId && r.ProcessId == runtimeRootPid &&
                r.Kind == "controller" && !string.IsNullOrWhiteSpace(r.ThreadIdentity)).ToArray();
            if (roots.Length != 1 || _process(runtimeRootPid) is not { } process)
                throw Failure("RUNTIME_UNAVAILABLE", "A unique canonical provider runtime scope is required.");
            var claims = new McpRuntimeClaims { Mode = "launch", Root = roots[0], RootStartedTicks = process.Started.Ticks,
                IssuedAt = _clock.GetUtcNow(), Nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32)) };
            var data = Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(claims, McpClientJsonContext.Default.McpRuntimeClaims))
                .TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var signed = "TLBXMCP-LAUNCH1." + data;
            return signed + "." + Convert.ToHexStringLower(HMACSHA256.HashData(_key(), Encoding.UTF8.GetBytes("tlbx/mcp/launch/v1\0" + signed)));
        }
    }

    private RuntimeRoot? ReadLaunchCapability(string token)
    {
        if (token.Length is < 70 or > 16384) return null;
        var pieces = token.Split('.');
        if (pieces.Length != 3 || pieces[0] != "TLBXMCP-LAUNCH1" || pieces[2].Length != 64) return null;
        try
        {
            var expected = HMACSHA256.HashData(_key(), Encoding.UTF8.GetBytes("tlbx/mcp/launch/v1\0" + pieces[0] + "." + pieces[1]));
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(pieces[2]), expected)) return null;
            var data = pieces[1].Replace('-', '+').Replace('_', '/');
            var claims = JsonSerializer.Deserialize(Convert.FromBase64String(data.PadRight((data.Length + 3) / 4 * 4, '=')), McpClientJsonContext.Default.McpRuntimeClaims);
            if (claims?.Mode != "launch" || claims.Root is not { Kind: "controller" } root || string.IsNullOrWhiteSpace(root.ThreadIdentity) ||
                !RootIsCurrent(root, new DateTime(claims.RootStartedTicks)) ||
                (_revokedBefore.TryGetValue(root.ProcessId, out var revoked) && claims.IssuedAt <= revoked)) return null;
            return root;
        }
        catch (Exception error) when (error is FormatException or JsonException or ArgumentException) { return null; }
    }

    internal McpContextResponse Bind(int processId, string? bootstrapToken = null)
    {
        if (processId <= 0 || _process(processId) is not { } caller)
            throw Failure("PROCESS_UNAVAILABLE", "The MCP stdio process is not live.");
        lock (_gate)
        {
            Prune();
            RuntimeRoot? root;
            if (bootstrapToken?.StartsWith("TLBXMCP-LAUNCH1.", StringComparison.Ordinal) == true)
            {
                root = ReadLaunchCapability(bootstrapToken) ?? throw Failure("LAUNCH_REJECTED", "The private runtime launch authority was revoked or changed scope.");
            }
            else if (bootstrapToken is not null)
            {
                if (!_bootstraps.Remove(bootstrapToken, out var bootstrap) ||
                    bootstrap.ExpiresAt <= _clock.GetUtcNow() || !RootIsCurrent(bootstrap.Root, bootstrap.Started) ||
                    (!bootstrap.Detached && !IsDescendant(processId, bootstrap.Root.ProcessId)))
                    throw Failure("BOOTSTRAP_REJECTED", "The one-use runtime bootstrap is invalid, expired, or belongs to another process tree.");
                root = bootstrap.Root;
            }
            else
            {
                root = FindDirectRoot(processId);
                if (root is null)
                {
                    var discovery = FindSharedRoot(processId);
                    if (discovery is null) throw Failure("CONTEXT_UNBOUND", "No unique runtime ancestry or verified shared backend. Inherited session IDs and cwd are not evidence.");
                    var backend = _process(discovery.ProcessId)!.Value;
                    return Create(new McpRuntimeClaims { ProcessId = processId, StartedTicks = caller.Started.Ticks,
                        Mode = "discovery", Root = discovery, RootStartedTicks = backend.Started.Ticks });
                }
            }
            var anchor = _process(root.ProcessId) ?? throw Failure("RUNTIME_UNAVAILABLE", "The owning runtime exited.");
            return Create(new McpRuntimeClaims { ProcessId = processId, StartedTicks = caller.Started.Ticks,
                Mode = "runtime", Root = root, RootStartedTicks = anchor.Started.Ticks });
        }
    }

    internal Binding? Validate(string token)
    {
        // Access never accepts an expired credential. Only the separate identity
        // preflight may renew a signed credential after idle/sleep or web restart.
        var claims = Read(token, allowExpired: false);
        return claims is null ? null : ToBinding(claims);
    }

    internal McpContextResponse Renew(string token, int processId)
    {
        var claims = Read(token, allowExpired: true);
        if (claims is null || claims.ProcessId != processId)
            throw Failure("CONTEXT_REVOKED", "The runtime binding changed or its signature is no longer valid; no application operation was executed.");
        return Create(claims);
    }

    internal void RevokeSession(string sessionId)
    {
        lock (_gate)
        {
            foreach (var key in _bootstraps.Where(p => p.Value.Root.SessionId == sessionId).Select(p => p.Key).ToArray())
                _bootstraps.Remove(key);
            foreach (var root in _roots().Where(r => r.SessionId == sessionId)) RevokeRuntime(root.ProcessId);
        }
    }

    internal void RevokeRuntime(int processId)
    {
        lock (_gate) _revokedBefore[processId] = _clock.GetUtcNow();
    }

    private McpContextResponse Create(McpRuntimeClaims claims)
    {
        claims.IssuedAt = _clock.GetUtcNow();
        claims.ExpiresAt = claims.IssuedAt + AccessLifetime;
        claims.Nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(claims, McpClientJsonContext.Default.McpRuntimeClaims);
        var data = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var signed = (claims.Mode == "discovery" ? DiscoveryPrefix : TokenPrefix) + "." + data;
        var signature = HMACSHA256.HashData(_key(), Encoding.UTF8.GetBytes((claims.Mode == "discovery" ? "tlbx/mcp/discovery/v1\0" : "tlbx/mcp/runtime/v1\0") + signed));
        return new McpContextResponse { SessionId = claims.Mode == "discovery" ? null : claims.Root?.SessionId, Mode = claims.Mode,
            ContextToken = signed + "." + Convert.ToHexStringLower(signature), ExpiresAt = claims.ExpiresAt };
    }

    private McpRuntimeClaims? Read(string token, bool allowExpired)
    {
        if (token.Length is < 70 or > 16384) return null;
        var pieces = token.Split('.');
        if (pieces.Length != 3 || pieces[0] is not (TokenPrefix or DiscoveryPrefix) || pieces[2].Length != 64) return null;
        try
        {
            var signature = Convert.FromHexString(pieces[2]);
            var expected = HMACSHA256.HashData(_key(), Encoding.UTF8.GetBytes((pieces[0] == DiscoveryPrefix ? "tlbx/mcp/discovery/v1\0" : "tlbx/mcp/runtime/v1\0") + pieces[0] + "." + pieces[1]));
            if (!CryptographicOperations.FixedTimeEquals(signature, expected)) return null;
            var base64 = pieces[1].Replace('-', '+').Replace('_', '/');
            var claims = JsonSerializer.Deserialize(Convert.FromBase64String(base64.PadRight((base64.Length + 3) / 4 * 4, '=')),
                McpClientJsonContext.Default.McpRuntimeClaims);
            if (claims is null || (!allowExpired && claims.ExpiresAt <= _clock.GetUtcNow()) ||
                _process(claims.ProcessId) is not { } caller || caller.Started.Ticks != claims.StartedTicks) return null;
            lock (_gate)
            {
                if (_revokedBefore.TryGetValue(claims.Root?.ProcessId ?? claims.ProcessId, out var revoked) && claims.IssuedAt <= revoked) return null;
                if (claims.Root is null) return null;
                if (claims.Mode == "discovery")
                {
                    if (pieces[0] != DiscoveryPrefix || claims.Root.Kind != "discovery" ||
                        _process(claims.Root.ProcessId) is not { } backend || backend.Started.Ticks != claims.RootStartedTicks ||
                        _shared(claims.Root.ProcessId) != true || !IsDescendant(claims.ProcessId, claims.Root.ProcessId)) return null;
                }
                else if (claims.Mode != "runtime" || pieces[0] != TokenPrefix ||
                    !RootIsCurrent(claims.Root, new DateTime(claims.RootStartedTicks))) return null;
            }
            return claims;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException) { return null; }
    }

    private static Binding ToBinding(McpRuntimeClaims claims)
    {
        if (claims.Mode == "discovery") return new Binding(null, claims.ProcessId, "discovery", claims.ExpiresAt, "");
        // Resource ownership follows the canonical runtime, not a replaceable
        // stdio child. Hash the complete snapshot so true scope changes isolate it.
        var identity = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
            new McpRuntimeClaims { Root = claims.Root, RootStartedTicks = claims.RootStartedTicks }, McpClientJsonContext.Default.McpRuntimeClaims)));
        return new Binding(claims.Root?.SessionId, claims.ProcessId, claims.Mode, claims.ExpiresAt, "runtime:" + identity);
    }

    private RuntimeRoot? FindSharedRoot(int processId)
    {
        var seen = new HashSet<int>();
        DateTime? childStarted = null;
        while (processId > 0 && seen.Count < 128 && seen.Add(processId))
        {
            if (_process(processId) is not { } process || (childStarted is not null && process.Started > childStarted)) return null;
            var shared = _shared(processId);
            if (shared == true) return new RuntimeRoot("", processId, 0, "discovery");
            if (shared is null) return null;
            childStarted = process.Started; processId = process.Parent;
        }
        return null;
    }

    internal async Task<McpContextResponse> ResolveNativeThreadAsync(McpContextRequest request, SessionCliContextService cli, CancellationToken ct)
    {
        var discovery = request.ContextToken is null ? null : Validate(request.ContextToken);
        if (discovery?.Mode != "discovery" || discovery.ProcessId != request.ProcessId ||
            !Guid.TryParseExact(request.NativeThreadId, "D", out var thread))
            throw Failure("NATIVE_THREAD_REQUIRED", "A verified shared-backend process and canonical native thread metadata are required.");
        return await SessionCallerIdentity.RunAsync(request.ProcessId, async () =>
        {
            var canonical = await CodexCliIdentityService.ResolveRootThreadAsync(thread.ToString(), request.CodexHome, ct).ConfigureAwait(false);
            if (canonical.RootThreadId is not { } root) throw Failure("THREAD_UNVERIFIED", canonical.Diagnostic);
            var resolution = await cli.ResolveAsync(root, thread.ToString(), request.ProcessId, request.Fresh,
                async (proof, token) => (await CodexCliIdentityService.EmitProofAsync(root, request.CodexHome, proof, token).ConfigureAwait(false)).Submitted, ct).ConfigureAwait(false);
            if (!SessionCliContextService.IsResolution(resolution)) throw new InvalidOperationException(resolution);
            var parts = resolution.Split(':');
            if (request.ExpectedSessionId is not null && parts[0] != request.ExpectedSessionId)
                throw Failure("CONTEXT_CHANGED", "The exact native thread now resolves to another session. No application operation was executed.");
            return new McpContextResponse { ContextToken = parts[1], SessionId = parts[0], Mode = "cli-thread", ExpiresAt = _clock.GetUtcNow().AddMinutes(2) };
        }).ConfigureAwait(false);
    }

    private RuntimeRoot? FindDirectRoot(int processId)
    {
        var roots = _roots();
        // Dedicated host roots are canonical owners even when their provider uses
        // an app-server child. Unmanaged/shared TUI backends are never traversed.
        return WalkRoot(processId, roots.Where(r => r.Kind == "controller").ToArray(), stopAtShared: false)
            ?? WalkRoot(processId, roots.Where(r => r.Kind == "terminal").ToArray(), stopAtShared: true);
    }

    private RuntimeRoot? WalkRoot(int pid, RuntimeRoot[] roots, bool stopAtShared)
    {
        var seen = new HashSet<int>();
        DateTime? childStarted = null;
        while (pid > 0 && seen.Count < 128 && seen.Add(pid))
        {
            if (_process(pid) is not { } process || (childStarted is not null && process.Started > childStarted) ||
                (stopAtShared && _shared(pid) != false)) return null;
            var matches = roots.Where(r => r.ProcessId == pid).ToArray();
            if (matches.Length > 0) return matches.Length == 1 ? matches[0] : null;
            childStarted = process.Started; pid = process.Parent;
        }
        return null;
    }

    private bool RootIsCurrent(RuntimeRoot expected, DateTime started) =>
        _process(expected.ProcessId) is { } process && process.Started == started &&
        _roots().Count(r => r == expected) == 1;

    private bool IsDescendant(int processId, int parent) => WalkRoot(processId,
        [new RuntimeRoot("bootstrap", parent, 0, "controller")], stopAtShared: false) is not null;

    private void Prune()
    {
        foreach (var key in _bootstraps.Where(p => p.Value.ExpiresAt <= _clock.GetUtcNow()).Select(p => p.Key).ToArray()) _bootstraps.Remove(key);
        foreach (var key in _revokedBefore.Keys.Where(pid => _process(pid) is null).ToArray()) _revokedBefore.Remove(key);
    }

    private static InvalidOperationException Failure(string code, string message) => new("TLBX_MCP_" + code + ": " + message);

    internal static (int Status, string Message)? ValidateLocalRequest(HttpContext context)
    {
        if (!RequestAccessContext.HasFullUserAccess(context)) return (401, "TLBX_MCP_AUTH_REQUIRED: Full tlbx authentication is required.");
        if (context.Connection.RemoteIpAddress is not { } remote || !IPAddress.IsLoopback(remote) ||
            context.Request.Headers.Keys.Any(h => h.Equals("Forwarded", StringComparison.OrdinalIgnoreCase) ||
                h.Equals("Via", StringComparison.OrdinalIgnoreCase) || h.Equals("X-Real-IP", StringComparison.OrdinalIgnoreCase) ||
                h.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase) || h.Equals("X-MidTerm-Internal-Proxy", StringComparison.OrdinalIgnoreCase)))
            return (403, "TLBX_MCP_LOCAL_ONLY: A direct local connection is required.");
        return null;
    }

    internal static void MapEndpoint(WebApplication app)
    {
        app.MapPost(ContextPath, async (HttpContext context) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            if (ValidateLocalRequest(context) is { } denied) return Results.Text(denied.Message, statusCode: denied.Status);
            if (!context.Request.HasJsonContentType()) return Results.Text("TLBX_MCP_JSON_REQUIRED", statusCode: 415);
            try
            {
                var request = await JsonSerializer.DeserializeAsync(context.Request.Body,
                    McpClientJsonContext.Default.McpContextRequest, context.RequestAborted);
                if (request is null) return Results.Text("TLBX_MCP_INVALID_CONTEXT_REQUEST", statusCode: 400);
                var service = app.Services.GetRequiredService<TlbxMcpClientContextService>();
                var response = request.NativeThreadId is not null
                    ? await service.ResolveNativeThreadAsync(request, app.Services.GetRequiredService<SessionCliContextService>(), context.RequestAborted).ConfigureAwait(false)
                    : request.ContextToken is not null ? service.Renew(request.ContextToken, request.ProcessId) : service.Bind(request.ProcessId, request.BootstrapToken);
                return Results.Json(response, McpClientJsonContext.Default.McpContextResponse);
            }
            catch (SessionCallerIdentityException error) { return Results.Text(error.Message, statusCode: 409); }
            catch (JsonException) { return Results.Text("TLBX_MCP_INVALID_CONTEXT_REQUEST", statusCode: 400); }
            catch (InvalidOperationException error) { return Results.Text(error.Message, statusCode: 409); }
        });
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed class McpContextRequest
{
    public int ProcessId { get; set; }
    public string? BootstrapToken { get; set; }
    public string? ContextToken { get; set; }
    public string? NativeThreadId { get; set; }
    public string? ExpectedSessionId { get; set; }
    public string? CodexHome { get; set; }
    public bool Fresh { get; set; }
}

internal sealed class McpContextResponse
{
    public string ContextToken { get; set; } = "";
    public string? SessionId { get; set; }
    public string Mode { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
}

internal sealed class McpRuntimeClaims
{
    public int ProcessId { get; set; }
    public long StartedTicks { get; set; }
    public string Mode { get; set; } = "";
    public TlbxMcpClientContextService.RuntimeRoot? Root { get; set; }
    public long RootStartedTicks { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public string Nonce { get; set; } = "";
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(McpContextRequest))]
[JsonSerializable(typeof(McpContextResponse))]
[JsonSerializable(typeof(McpRuntimeClaims))]
internal sealed partial class McpClientJsonContext : JsonSerializerContext
{
    // SDK contracts include internal property converters (notably CacheScope).
    // Resolve its NativeAOT metadata instead of regenerating incomplete contracts
    // in this assembly, where those converters are inaccessible.
    public JsonTypeInfo<ListToolsResult> ListToolsResult => McpJsonUtilities.DefaultOptions.GetTypeInfo<ListToolsResult>();
    public JsonTypeInfo<CallToolResult> CallToolResult => McpJsonUtilities.DefaultOptions.GetTypeInfo<CallToolResult>();
    public JsonTypeInfo<ListResourcesResult> ListResourcesResult => McpJsonUtilities.DefaultOptions.GetTypeInfo<ListResourcesResult>();
    public JsonTypeInfo<ListResourceTemplatesResult> ListResourceTemplatesResult => McpJsonUtilities.DefaultOptions.GetTypeInfo<ListResourceTemplatesResult>();
    public JsonTypeInfo<ListPromptsResult> ListPromptsResult => McpJsonUtilities.DefaultOptions.GetTypeInfo<ListPromptsResult>();
    public JsonTypeInfo<GetPromptResult> GetPromptResult => McpJsonUtilities.DefaultOptions.GetTypeInfo<GetPromptResult>();
    public JsonTypeInfo<CompleteResult> CompleteResult => McpJsonUtilities.DefaultOptions.GetTypeInfo<CompleteResult>();
    public JsonTypeInfo<ReadResourceResult> ReadResourceResult => McpJsonUtilities.DefaultOptions.GetTypeInfo<ReadResourceResult>();
}

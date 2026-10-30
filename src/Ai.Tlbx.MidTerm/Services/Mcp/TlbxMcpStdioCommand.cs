using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>The native local MCP adapter; no shell, provider flags or global configuration writes.</summary>
internal static class TlbxMcpStdioCommand
{
    internal static int Run(string[] args)
    {
        try
        {
            using var input = Console.OpenStandardInput();
            using var output = Console.OpenStandardOutput();
            return RunAsync(args, input, output, CancellationToken.None).GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or HttpRequestException or IOException or JsonException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(error.Message);
            return 1;
        }
    }

    // Each connection owns its cookie authority and optional TLS pin; sharing a global
    // client would mix credentials and trust across independent MCP runtimes.
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "IDISP014:Use a single instance of HttpClient", Justification = "Connection-specific cookies, certificate authority and identity handlers require isolated clients.")]
    internal static async Task<int> RunAsync(string[] args, Stream input, Stream output, CancellationToken cancellationToken)
    {
        if (args is ["--help"] or ["-h"])
        {
            Console.WriteLine("mt mcp --stdio [--url https://localhost:PORT] [--operator] [--certificate-sha256 HEX] [--credentials-file PRIVATE_JSON]");
            Console.WriteLine("Authentication: private credential file or inherited MT_API_KEY/MT_TOKEN; MT_SESSION_ID is never identity evidence.");
            Console.WriteLine("Shared backends require trusted per-runtime credentials delivered through a private file.");
            return 0;
        }
        var options = McpStdioOptions.Parse(args);
        var cookies = new CookieContainer();
        var credentials = new McpStdioCredentials(options, cookies);
        using var bootstrapHandler = CreateHttpHandler(options, cookies);
        using var bootstrapHttp = new HttpClient(bootstrapHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(15) };
        credentials.Configure(bootstrapHttp);
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var context = options.OperatorMode ? null : new McpStdioRuntimeContext(bootstrapHttp, credentials, options);
        if (context is not null) await context.EnsureAsync(force: true, lifetime.Token).ConfigureAwait(false);
        using var contextHandler = new McpStdioContextHandler(context, credentials, options.OperatorMode)
        {
            InnerHandler = CreateHttpHandler(options, cookies)
        };
        using var http = new HttpClient(contextHandler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        credentials.Configure(http);
        await using var upstreamTransport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Endpoint = options.Endpoint(TlbxMcpEndpoints.Path),
            TransportMode = HttpTransportMode.StreamableHttp,
            MaxReconnectionAttempts = 0
        }, http, ownsHttpClient: false);
        await using var upstream = await McpClient.CreateAsync(upstreamTransport, cancellationToken: lifetime.Token).ConfigureAwait(false);
        var services = new ServiceCollection();
        services.AddLogging();
        var proxy = services.AddMcpServer(server =>
        {
            server.ServerInfo = upstream.ServerInfo;
            server.ServerInstructions = upstream.ServerInstructions;
        }).WithStreamServerTransport(input, output);
        ConfigureForwarding(proxy, upstream, context);
        await using var provider = services.BuildServiceProvider();
        var server = provider.GetRequiredService<McpServer>();
        var renewal = context is null ? Task.CompletedTask : RenewWhileAliveAsync(context, lifetime.Token);
        try { await server.RunAsync(lifetime.Token).ConfigureAwait(false); }
        finally
        {
            await lifetime.CancelAsync().ConfigureAwait(false);
            await renewal.ConfigureAwait(false);
        }
        return 0;
    }

    internal static void ConfigureForwarding(IMcpServerBuilder builder, McpClient upstream, McpStdioRuntimeContext? context = null)
    {
        // Forward one protocol request with its original parameters and metadata.
        // Preserve cursors and opaque URIs instead of flattening/prefetching lists.
        builder.Services.Configure<McpServerOptions>(options => options.Capabilities = new ServerCapabilities
        {
            Tools = upstream.ServerCapabilities.Tools is null ? null : new ToolsCapability(),
            Resources = upstream.ServerCapabilities.Resources is null ? null : new ResourcesCapability(),
            Prompts = upstream.ServerCapabilities.Prompts is null ? null : new PromptsCapability(),
            Completions = upstream.ServerCapabilities.Completions
        });
        if (upstream.ServerCapabilities.Tools is not null)
            builder.WithListToolsHandler(async (request, ct) =>
                {
                    var result = await ForwardAsync(upstream, request.JsonRpcRequest, McpClientJsonContext.Default.ListToolsResult, ct).ConfigureAwait(false);
                    result.Tools = result.Tools.Where(tool => tool.Name is not ("process_run_isolated" or TlbxMcpFileCallTool.ToolName or TlbxMcpArtifactSaveTool.ToolName)).ToList();
                    result.Tools = result.Tools.Select(tool => tool.Name == "update_apply" ? TlbxMcpUpdateWorkflow.ProtocolTool : tool).ToList();
                    if (request.Params?.Cursor is null)
                    {
                        result.Tools.Add(TlbxMcpIsolatedProcessTool.ProtocolTool);
                        result.Tools.Add(TlbxMcpFileCallTool.Definition);
                        result.Tools.Add(TlbxMcpArtifactSaveTool.Definition);
                    }
                    return result;
                })
                .WithCallToolHandler(async (request, ct) =>
                {
                    try
                    {
                        var native = context is null ? null : await context.ResolveNativeCallAsync(request.JsonRpcRequest, request.Params?.Name == "context_repair", ct).ConfigureAwait(false);
                        using var scope = context?.EnterNativeScope(native);
                        if (request.Params?.Name == "process_run_isolated")
                            return await TlbxMcpIsolatedProcessTool.InvokeLocalAsync(request.Params, ct).ConfigureAwait(false);
                        if (request.Params?.Name is TlbxMcpFileCallTool.ToolName or TlbxMcpArtifactSaveTool.ToolName)
                            return await TlbxMcpNativeWorkflow.InvokeNativeStepAsync(upstream, request.Params, ct).ConfigureAwait(false);
                        if (request.Params?.Name == "context_repair" && context is not null)
                            await context.EnsureAsync(force: true, ct).ConfigureAwait(false);
                        if (request.Params?.Name == "workflow_run")
                            return await TlbxMcpNativeWorkflow.InvokeAsync(request.Params, upstream, ct).ConfigureAwait(false);
                        if (request.Params?.Name == "update_apply")
                            return await TlbxMcpUpdateWorkflow.InvokeAsync(request.Params,
                                (name, arguments, token) => TlbxMcpNativeWorkflow.CallUpstreamAsync(upstream, name, arguments, token), ct).ConfigureAwait(false);
                        return await ForwardAsync(upstream, request.JsonRpcRequest, McpClientJsonContext.Default.CallToolResult, ct).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException error) when (
                        error.Message.StartsWith("TLBX_MCP_", StringComparison.Ordinal) ||
                        error.Message.StartsWith("TLBX_CONTEXT_", StringComparison.Ordinal))
                    {
                        // Preserve our actionable, controlled identity errors.
                        // Other exceptions remain SDK-masked and cannot expose
                        // arbitrary provider/configuration text to the model.
                        return new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = error.Message }] };
                    }
                });
        if (upstream.ServerCapabilities.Resources is not null)
        {
            builder.WithListResourcesHandler(async (request, ct) => await WithNativeScopeAsync(context, request.JsonRpcRequest, () => ForwardAsync(upstream, request.JsonRpcRequest, McpClientJsonContext.Default.ListResourcesResult, ct), ct).ConfigureAwait(false))
                .WithListResourceTemplatesHandler(async (request, ct) => await ForwardAsync(upstream, request.JsonRpcRequest, McpClientJsonContext.Default.ListResourceTemplatesResult, ct).ConfigureAwait(false))
                .WithReadResourceHandler(async (request, ct) => await WithNativeScopeAsync(context, request.JsonRpcRequest, () => ForwardAsync(upstream, request.JsonRpcRequest, McpClientJsonContext.Default.ReadResourceResult, ct), ct).ConfigureAwait(false));
        }
        if (upstream.ServerCapabilities.Prompts is not null)
            builder.WithListPromptsHandler(async (request, ct) => await ForwardAsync(upstream, request.JsonRpcRequest, McpClientJsonContext.Default.ListPromptsResult, ct).ConfigureAwait(false))
                .WithGetPromptHandler(async (request, ct) => await WithNativeScopeAsync(context, request.JsonRpcRequest, () => ForwardAsync(upstream, request.JsonRpcRequest, McpClientJsonContext.Default.GetPromptResult, ct), ct).ConfigureAwait(false));
        if (upstream.ServerCapabilities.Completions is not null)
            builder.WithCompleteHandler(async (request, ct) => await WithNativeScopeAsync(context, request.JsonRpcRequest, () => ForwardAsync(upstream, request.JsonRpcRequest, McpClientJsonContext.Default.CompleteResult, ct), ct).ConfigureAwait(false));
    }

    internal static async ValueTask<TResult> ForwardAsync<TResult>(McpClient upstream, JsonRpcRequest incoming, JsonTypeInfo<TResult> resultType, CancellationToken ct)
    {
        var id = "tlbx-proxy-" + Guid.NewGuid().ToString("N");
        try
        {
            var response = await upstream.SendRequestAsync(new JsonRpcRequest { Id = new RequestId(id), Method = incoming.Method,
                Params = incoming.Params?.DeepClone() }, ct).ConfigureAwait(false);
            return response.Result is { } result ? result.Deserialize(resultType)!
                : throw new InvalidOperationException("TLBX_MCP_PROTOCOL: Empty upstream response.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Console.Error.WriteLine("TLBX_MCP_REQUEST_CANCELLED: " + incoming.Method + " upstream=" + id);
            // The SDK's token callback sends cancellation fire-and-forget. Await
            // the actual notification while the connection is alive so a native
            // cancellation does not strand the upstream operation.
            try
            {
                using var delivery = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await upstream.SendMessageAsync(new JsonRpcNotification { Method = NotificationMethods.CancelledNotification,
                    Params = new JsonObject { ["requestId"] = id } }, delivery.Token).ConfigureAwait(false);
            }
            catch (Exception error) when (error is IOException or HttpRequestException or InvalidOperationException or McpException or OperationCanceledException)
            { Console.Error.WriteLine("TLBX_MCP_CANCEL: Upstream cancellation notification could not be delivered."); }
            throw;
        }
    }

    private static async ValueTask<T> WithNativeScopeAsync<T>(McpStdioRuntimeContext? context, JsonRpcRequest request, Func<ValueTask<T>> operation, CancellationToken ct)
    {
        var native = context is null ? null : await context.ResolveNativeCallAsync(request, false, ct).ConfigureAwait(false);
        using var scope = context?.EnterNativeScope(native);
        return await operation().ConfigureAwait(false);
    }

    private static async Task RenewWhileAliveAsync(McpStdioRuntimeContext context, CancellationToken ct)
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromMinutes(5));
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try { await context.EnsureAsync(force: true, ct).ConfigureAwait(false); }
                catch (Exception error) when (error is HttpRequestException or InvalidOperationException or IOException)
                {
                    // Retry only identity preflight on a later tick/request. Application
                    // requests are never replayed, including after a web update.
                    Console.Error.WriteLine("TLBX_MCP_CONTEXT_RENEWAL: " + error.Message);
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }

    internal static HttpClientHandler CreateHttpHandler(McpStdioOptions options, CookieContainer cookies)
    {
        var handler = new HttpClientHandler { CookieContainer = cookies, UseCookies = true, AllowAutoRedirect = false };
        if (options.CertificateSha256 is not null)
        {
            handler.ServerCertificateCustomValidationCallback = (_, certificate, _, errors) =>
                ValidatePinnedCertificate(certificate, errors, options.CertificateSha256);
        }
        return handler;
    }

    internal static bool ValidatePinnedCertificate(X509Certificate2? certificate, SslPolicyErrors errors, string fingerprint) =>
        certificate is not null && (errors & SslPolicyErrors.RemoteCertificateNotAvailable) == 0 &&
        string.Equals(certificate.GetCertHashString(HashAlgorithmName.SHA256), fingerprint, StringComparison.OrdinalIgnoreCase);
}

internal sealed class McpStdioOptions
{
    internal required Uri Origin { get; init; }
    internal required string BaseUrl { get; init; }
    internal string? ApiKey { get; init; }
    internal string? CookieSeed { get; init; }
    internal string? BootstrapToken { get; init; }
    internal string? CertificateSha256 { get; init; }
    internal bool OperatorMode { get; init; }
    internal string? CacheDirectory { get; init; }

    internal static McpStdioOptions Parse(string[] args)
    {
        var files = args.Select((value, index) => (value, index)).Where(item => item.value == "--credentials-file").ToArray();
        if (files.Length > 1 || (files.Length == 1 && files[0].index + 1 >= args.Length))
            throw new ArgumentException("TLBX_MCP_CREDENTIALS: Supply one complete --credentials-file argument.");
        var values = files.Length == 1 ? ReadCredentialsFile(args[files[0].index + 1]) : null;
        string? Value(string name) => values is null ? Environment.GetEnvironmentVariable(name) : values.GetValueOrDefault(name);
        var url = Value("MT_BASE_URL")
            ?? "https://localhost:" + (Environment.GetEnvironmentVariable("MT_PORT") ?? "2000");
        var fingerprint = Value("TLBX_MCP_CERT_SHA256");
        var stdio = false;
        var operatorMode = false;
        for (var index = 0; index < args.Length; index++)
        {
            switch (args[index])
            {
                case "--credentials-file" when index + 1 < args.Length: index++; break;
                case "--stdio": stdio = true; break;
                case "--operator": operatorMode = true; break;
                case "--url" when index + 1 < args.Length: url = args[++index]; break;
                case "--certificate-sha256" when index + 1 < args.Length: fingerprint = args[++index]; break;
                default: throw new ArgumentException("TLBX_MCP_USAGE: Unknown or incomplete argument. Use mt mcp --help.");
            }
        }
        if (!stdio) throw new ArgumentException("TLBX_MCP_USAGE: Select --stdio.");
        var origin = ValidateOrigin(url, operatorMode);
        if (fingerprint is not null)
        {
            fingerprint = fingerprint.Replace(":", "", StringComparison.Ordinal);
            if (fingerprint.Length != 64 || !fingerprint.All(char.IsAsciiHexDigit))
                throw new ArgumentException("TLBX_MCP_CERTIFICATE: Expected a SHA256 certificate fingerprint.");
        }
        if (values is null && Value("TLBX_MCP_BOOTSTRAP")?.StartsWith("TLBXMCP-LAUNCH1.", StringComparison.Ordinal) == true)
            throw new ArgumentException("TLBX_MCP_CREDENTIALS: Runtime launch authority must be supplied through a private credential file.");
        var key = Value("MT_API_KEY");
        var cookie = Value("MT_TOKEN");
        if (string.IsNullOrWhiteSpace(key) && string.IsNullOrWhiteSpace(cookie))
            throw new ArgumentException("TLBX_MCP_AUTH_REQUIRED: Inherit MT_API_KEY or MT_TOKEN from the owning tlbx environment.");
        return new McpStdioOptions
        {
            Origin = origin, BaseUrl = url.TrimEnd('/'), ApiKey = key, CookieSeed = cookie, OperatorMode = operatorMode,
            BootstrapToken = Value("TLBX_MCP_BOOTSTRAP"), CertificateSha256 = fingerprint,
            CacheDirectory = Value("TLBX_CLI_AUTH_CACHE_DIR")
        };
    }

    internal static Uri ValidateOrigin(string url, bool operatorMode = false)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var origin) || origin.Scheme != Uri.UriSchemeHttps ||
            origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0 ||
            (!operatorMode && origin.AbsolutePath != "/") ||
            (!operatorMode && !(origin.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) ||
                (IPAddress.TryParse(origin.Host.Trim('[', ']'), out var ip) && IPAddress.IsLoopback(ip)))))
            throw new ArgumentException("TLBX_MCP_ORIGIN: Use HTTPS without userinfo or query. Automatic runtime binding requires direct loopback without a path; remote or proxy-prefixed owner calls require explicit --operator and explicit targets.");
        return new Uri(origin.AbsoluteUri.TrimEnd('/') + "/");
    }

    internal Uri Endpoint(string path) => new(Origin, path.TrimStart('/'));

    internal static Dictionary<string, string> ReadCredentialsFile(string path)
    {
        if (!Path.IsPathFullyQualified(path) || (File.GetAttributes(path) & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
            throw new ArgumentException("TLBX_MCP_CREDENTIALS: Expected an absolute regular private credential file.");
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length > 65536) throw new ArgumentException("TLBX_MCP_CREDENTIALS: Credential file is too large.");
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var owner = identity.User ?? throw new InvalidOperationException("TLBX_MCP_CREDENTIALS: Cannot identify current user.");
            var security = stream.GetAccessControl();
            var permitted = new HashSet<string>(StringComparer.Ordinal) { owner.Value, "S-1-5-18", "S-1-5-32-544" };
            var unsafeAccess = security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier fileOwner || !permitted.Contains(fileOwner.Value);
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
                unsafeAccess |= rule.AccessControlType == AccessControlType.Allow &&
                    (rule.FileSystemRights & (FileSystemRights.ReadData | FileSystemRights.WriteData | FileSystemRights.AppendData |
                        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | FileSystemRights.Delete)) != 0 &&
                    !permitted.Contains(rule.IdentityReference.Value);
            if (unsafeAccess)
                throw new UnauthorizedAccessException("TLBX_MCP_CREDENTIALS: Credential file must be accessible only by its owner, SYSTEM or administrators.");
        }
        else if ((File.GetUnixFileMode(stream.SafeFileHandle) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
                     UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0)
            throw new UnauthorizedAccessException("TLBX_MCP_CREDENTIALS: Credential file must have owner-only permissions.");
        var values = JsonSerializer.Deserialize(stream, McpClientJsonContext.Default.DictionaryStringString)
            ?? throw new ArgumentException("TLBX_MCP_CREDENTIALS: Empty credential file.");
        string[] allowed = ["MT_BASE_URL", "MT_TOKEN", "MT_API_KEY", "TLBX_MCP_CERT_SHA256", "TLBX_MCP_BOOTSTRAP", "TLBX_CLI_AUTH_CACHE_DIR"];
        if (!values.ContainsKey("MT_BASE_URL") || values.Any(pair => !allowed.Contains(pair.Key, StringComparer.Ordinal) || pair.Value is null || pair.Value.Any(char.IsControl)))
            throw new ArgumentException("TLBX_MCP_CREDENTIALS: Unexpected or malformed credential fields.");
        return values;
    }
}

internal sealed class McpStdioRuntimeContext : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly HttpClient _http;
    private readonly McpStdioCredentials _credentials;
    private readonly McpStdioOptions _options;
    private McpContextResponse? _binding;
    private bool _bootstrapUsed;
    private readonly AsyncLocal<McpNativeCallBinding?> _nativeScope = new();
    internal McpNativeCallBinding? CurrentNativeScope => _nativeScope.Value;

    internal McpStdioRuntimeContext(HttpClient http, McpStdioCredentials credentials, McpStdioOptions options)
    { _http = http; _credentials = credentials; _options = options; }

    public void Dispose() => _gate.Dispose();

    internal async Task<string> EnsureAsync(bool force, CancellationToken ct)
    {
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _credentials.ThrowIfRejected();
            if (!force && _binding is { } binding && binding.ExpiresAt > DateTimeOffset.UtcNow.AddMinutes(1)) return binding.ContextToken;
            var request = new McpContextRequest { ProcessId = Environment.ProcessId, ContextToken = _binding?.ContextToken,
                BootstrapToken = !_bootstrapUsed ? _options.BootstrapToken : null };
            using var response = await SendPreflightAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException("TLBX_MCP_CONTEXT_UNAVAILABLE: Identity preflight returned HTTP " + ((int)response.StatusCode).ToString(CultureInfo.InvariantCulture) + ". No application request was executed.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var next = await JsonSerializer.DeserializeAsync(stream, McpClientJsonContext.Default.McpContextResponse, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("TLBX_MCP_CONTEXT_UNAVAILABLE: Empty identity response.");
            if (string.IsNullOrWhiteSpace(next.ContextToken) || next.Mode is not ("runtime" or "discovery") ||
                (_binding is not null && (next.SessionId != _binding.SessionId || next.Mode != _binding.Mode)))
                throw new InvalidOperationException("TLBX_MCP_CONTEXT_CHANGED: The server changed this connection's calling scope; restart the MCP connection.");
            _bootstrapUsed = true;
            _binding = next;
            return next.ContextToken;
        }
        finally { _gate.Release(); }
    }

    internal static string CallerCodexHome(string? configuredHome) => string.IsNullOrWhiteSpace(configuredHome)
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex") : configuredHome;

    internal async Task<McpNativeCallBinding?> ResolveNativeCallAsync(JsonRpcRequest request, bool fresh, CancellationToken ct)
    {
        await EnsureAsync(force: false, ct).ConfigureAwait(false);
        if (_binding?.Mode != "discovery") return null;
        var meta = request.Params?["_meta"] as JsonObject;
        if (meta?["threadId"] is not JsonValue value || !value.TryGetValue<string>(out var thread) || !Guid.TryParseExact(thread, "D", out _))
            throw new InvalidOperationException("TLBX_MCP_NATIVE_THREAD_REQUIRED: This shared-backend request has no verified native thread metadata. No operation was dispatched.");
        var scoped = await ResolveThreadProofAsync(thread, null, fresh, ct).ConfigureAwait(false);
        return new McpNativeCallBinding(thread, scoped.SessionId!, scoped.ContextToken);
    }

    internal async Task<string> RefreshNativeScopeAsync(McpNativeCallBinding native, CancellationToken ct)
    {
        // Each operation in a long workflow obtains a fresh proof for precisely
        // the original native thread and checks the original target session.
        await EnsureAsync(force: true, ct).ConfigureAwait(false);
        var refreshed = await ResolveThreadProofAsync(native.ThreadId, native.SessionId, false, ct).ConfigureAwait(false);
        return refreshed.ContextToken;
    }

    private async Task<McpContextResponse> ResolveThreadProofAsync(string thread, string? expectedSessionId, bool fresh, CancellationToken ct)
    {
        if (_binding?.Mode != "discovery") throw new InvalidOperationException("TLBX_MCP_CONTEXT_CHANGED: Shared backend calling mode changed.");
        var proof = new McpContextRequest { ProcessId = Environment.ProcessId, ContextToken = _binding.ContextToken,
            NativeThreadId = thread, ExpectedSessionId = expectedSessionId,
            CodexHome = CallerCodexHome(Environment.GetEnvironmentVariable("CODEX_HOME")), Fresh = fresh };
        using var body = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(proof, McpClientJsonContext.Default.McpContextRequest));
        body.Headers.ContentType = new MediaTypeHeaderValue("application/json");
        using var response = await _http.PostAsync(_options.Endpoint(TlbxMcpClientContextService.ContextPath), body, ct).ConfigureAwait(false);
        _credentials.Observe(response);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException("TLBX_MCP_THREAD_UNVERIFIED: " + await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var scoped = await JsonSerializer.DeserializeAsync(stream, McpClientJsonContext.Default.McpContextResponse, ct).ConfigureAwait(false);
        if (scoped?.Mode != "cli-thread" || string.IsNullOrWhiteSpace(scoped.SessionId) || string.IsNullOrWhiteSpace(scoped.ContextToken) ||
            (expectedSessionId is not null && scoped.SessionId != expectedSessionId))
            throw new InvalidOperationException("TLBX_MCP_THREAD_UNVERIFIED: Canonical thread proof changed its session.");
        return scoped;
    }

    internal IDisposable EnterNativeScope(McpNativeCallBinding? value)
    {
        var previous = _nativeScope.Value;
        _nativeScope.Value = value;
        return new NativeScope(() => _nativeScope.Value = previous);
    }
    private sealed class NativeScope(Action restore) : IDisposable { public void Dispose() => restore(); }

    [System.Diagnostics.CodeAnalysis.SuppressMessage("Correctness", "IDISP017:Prefer using", Justification = "Successful/final response ownership transfers to the caller; only discarded retry responses are disposed here.")]
    private async Task<HttpResponseMessage> SendPreflightAsync(McpContextRequest request, CancellationToken ct)
    {
        // Only renew an already established identity. No application request has
        // been dispatched, and renewal cannot change the canonical runtime scope.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var body = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(request, McpClientJsonContext.Default.McpContextRequest));
                body.Headers.ContentType = new MediaTypeHeaderValue("application/json");
                var response = await _http.PostAsync(_options.Endpoint(TlbxMcpClientContextService.ContextPath), body, ct).ConfigureAwait(false);
                _credentials.Observe(response);
                if (_binding is null || attempt >= 4 || response.StatusCode is not (HttpStatusCode.Conflict or HttpStatusCode.ServiceUnavailable)) return response;
                response.Dispose();
            }
            catch (HttpRequestException) when (_binding is not null && attempt < 4) { }
            await Task.Delay(TimeSpan.FromSeconds(1 << attempt), ct).ConfigureAwait(false);
            _credentials.ThrowIfRejected();
        }
    }
}

internal sealed class McpStdioContextHandler(McpStdioRuntimeContext? context, McpStdioCredentials credentials, bool operatorMode = false) : DelegatingHandler
{
    internal const string OperatorHeader = "X-Tlbx-Mcp-Operator";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        credentials.ThrowIfRejected();
        request.Headers.Remove(TlbxMcpClientContextService.RuntimeHeader);
        request.Headers.Remove(OperatorHeader);
        request.Headers.Remove(SessionCliContextService.LeaseHeader);
        if (context?.CurrentNativeScope is { } native)
            request.Headers.Add(SessionCliContextService.LeaseHeader, await context.RefreshNativeScopeAsync(native, cancellationToken).ConfigureAwait(false));
        else if (operatorMode) request.Headers.Add(OperatorHeader, "explicit");
        else
        {
            var token = await (context ?? throw new InvalidOperationException("TLBX_MCP_CONTEXT_REQUIRED: No verified runtime context was configured."))
                .EnsureAsync(force: true, cancellationToken).ConfigureAwait(false);
            request.Headers.Add(TlbxMcpClientContextService.RuntimeHeader, token);
        }
        var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        credentials.Observe(response);
        // Deliberately no retry after dispatch: an operation can have succeeded
        // even when its response was lost or authentication subsequently changed.
        return response;
    }
}

/// <summary>Shares the private, exact-server/exact-seed curl cookie cache with the maintained CLI.</summary>
internal sealed class McpStdioCredentials
{
    private readonly Lock _gate = new();
    private readonly McpStdioOptions _options;
    private readonly CookieContainer _cookies;
    private readonly string? _cache;
    private bool _rejected;

    internal McpStdioCredentials(McpStdioOptions options, CookieContainer cookies)
    {
        _options = options; _cookies = cookies;
        if (!string.IsNullOrWhiteSpace(options.ApiKey)) return;
        var directory = options.CacheDirectory ?? (OperatingSystem.IsWindows()
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "tlbx", "cli-auth")
            : Path.Combine(Environment.GetEnvironmentVariable("XDG_STATE_HOME") ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".local", "state"), "tlbx", "cli-auth"));
        Directory.CreateDirectory(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(options.BaseUrl + "\nmm-session=" + options.CookieSeed)));
        _cache = Path.Combine(directory, key + ".cookies");
        var cached = File.Exists(_cache) ? ReadCookieRecord(File.ReadAllLines(_cache), options.Origin) : null;
        var cookie = File.Exists(_cache) ? cached?.Value : options.CookieSeed;
        if (string.IsNullOrWhiteSpace(cookie)) throw new InvalidOperationException("TLBX_MCP_AUTH_REJECTED: The shared CLI credential cache is empty or expired. Sign in again; the rejected seed will not be replayed.");
        if (cookie.Any(c => char.IsControl(c) || c is ';' or ',' or '"' or '\\'))
            throw new InvalidOperationException("TLBX_MCP_AUTH_REJECTED: The session credential is malformed.");
        _cookies.Add(options.Origin, new Cookie(AuthService.SessionCookieName, cookie, cached?.Path ?? "/") { Secure = true, HttpOnly = true });
    }

    internal void Configure(HttpClient http)
    {
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            if (_options.ApiKey.Any(char.IsControl)) throw new InvalidOperationException("TLBX_MCP_AUTH_REJECTED: The API credential is malformed.");
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
    }

    internal void ThrowIfRejected()
    {
        lock (_gate)
            if (_rejected) throw new InvalidOperationException("TLBX_MCP_AUTH_REJECTED: Authentication was explicitly rejected. No seed credential or application request will be retried.");
    }

    internal void Observe(HttpResponseMessage response)
    {
        lock (_gate)
        {
            if (response.Headers.TryGetValues(AuthService.AuthRequiredHeaderName, out var required) && required.Any(v => v.Equals("true", StringComparison.OrdinalIgnoreCase)))
            {
                _rejected = true;
                if (_cache is not null) TryWriteCache("");
                return;
            }
            if (_rejected || _cache is null || !response.Headers.TryGetValues("Set-Cookie", out var values) ||
                !values.Any(v => v.StartsWith(AuthService.SessionCookieName + "=", StringComparison.OrdinalIgnoreCase))) return;
            var cookie = _cookies.GetCookies(_options.Origin)[AuthService.SessionCookieName];
            if (cookie is null) return;
            var expiry = cookie.Expires == DateTime.MinValue ? "0" : new DateTimeOffset(cookie.Expires.ToUniversalTime()).ToUnixTimeSeconds().ToString(CultureInfo.InvariantCulture);
            TryWriteCache("# Netscape HTTP Cookie File\n#HttpOnly_" + _options.Origin.Host + "\tFALSE\t" + cookie.Path + "\tTRUE\t" + expiry + "\t" + cookie.Name + "\t" + cookie.Value + "\n");
        }
    }

    private void TryWriteCache(string value)
    {
        try { WriteCache(value); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            // The application request may already have succeeded. Losing a cache
            // update must never turn that into a reason to repeat the operation.
            Console.Error.WriteLine("TLBX_MCP_AUTH_CACHE: Cookie state could not be saved; sign in again if needed. Do not replay an application request.");
        }
    }

    private void WriteCache(string value)
    {
        var temporary = _cache + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, value, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.Move(temporary, _cache!, overwrite: true);
        }
        finally { File.Delete(temporary); }
    }

    internal static string? ReadCookie(IEnumerable<string> lines, Uri origin) => ReadCookieRecord(lines, origin)?.Value;

    private static Cookie? ReadCookieRecord(IEnumerable<string> lines, Uri origin)
    {
        Cookie? result = null;
        foreach (var raw in lines)
        {
            var line = raw.StartsWith("#HttpOnly_", StringComparison.Ordinal) ? raw[10..] : raw;
            if (line.StartsWith('#')) continue;
            var parts = line.Split('\t');
            if (parts.Length != 7 || parts[5] != AuthService.SessionCookieName || !CookiePathMatches(origin.AbsolutePath, parts[2]) ||
                !parts[0].TrimStart('.').Equals(origin.Host, StringComparison.OrdinalIgnoreCase) ||
                !long.TryParse(parts[4], CultureInfo.InvariantCulture, out var expiry) ||
                (expiry != 0 && expiry <= DateTimeOffset.UtcNow.ToUnixTimeSeconds())) continue;
            if (result is null || parts[2].Length >= result.Path.Length) result = new Cookie(parts[5], parts[6], parts[2]);
        }
        return result;
    }

    private static bool CookiePathMatches(string requestPath, string cookiePath) => cookiePath.StartsWith('/') &&
        (requestPath == cookiePath || (requestPath.StartsWith(cookiePath, StringComparison.Ordinal) &&
            (cookiePath.EndsWith('/') || requestPath.Length > cookiePath.Length && requestPath[cookiePath.Length] == '/')));
}


internal sealed record McpNativeCallBinding(string ThreadId, string SessionId, string ContextToken);

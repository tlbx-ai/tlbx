using System.Net;
using System.IO.Pipelines;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Net.Security;
using System.Text;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Mcp;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TlbxMcpTransportProtocolTests
{
    private static readonly DateTime Started = new(2026, 10, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly byte[] Key = SHA256.HashData(Encoding.UTF8.GetBytes("mcp-protocol-fixture-purpose"));

    [Fact]
    public void RuntimeCredentialSurvivesWebRestartAndIdleButNotProcessReplacement()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(Started));
        var processes = Processes();
        using var cli = new SessionCliContextService(() => [],
            pid => processes.TryGetValue(pid, out var p) ? p : null, clock);
        var cliLease = cli.CreateHostLease("first001", 100).Split(':')[1];
        var roots = Roots();
        var service = Create(roots, processes, clock);
        var granted = service.Bind(100);
        Assert.Equal("first001", service.Validate(granted.ContextToken)?.SessionId);
        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.NotNull(cli.ValidateLease(cliLease));
        Assert.NotNull(service.Validate(granted.ContextToken));
        var restarted = Create(roots, processes, clock);
        Assert.NotNull(restarted.Validate(granted.ContextToken));
        clock.Advance(TimeSpan.FromHours(4));
        Assert.Null(restarted.Validate(granted.ContextToken));
        var renewed = restarted.Renew(granted.ContextToken, 100);
        Assert.Equal("first001", restarted.Validate(renewed.ContextToken)?.SessionId);
        processes[100] = (11, Started.AddTicks(1));
        Assert.Null(restarted.Validate(renewed.ContextToken));
        Assert.Throws<InvalidOperationException>(() => restarted.Renew(renewed.ContextToken, 100));
    }

    [Fact]
    public void SharedBackendCannotInheritFirstLauncherButOneUseThreadBootstrapCanBind()
    {
        var processes = Processes();
        processes[300] = (31, Started);
        processes[31] = (11, Started);
        processes[301] = (31, Started);
        var service = Create(Roots(), processes, new FakeTimeProvider(new DateTimeOffset(Started)), pid => pid == 31);
        var discovery = service.Bind(300);
        Assert.Equal("discovery", discovery.Mode);
        Assert.Null(discovery.SessionId);
        Assert.Equal("", service.Validate(discovery.ContextToken)!.ActorIdentity);
        var seed = service.IssueBootstrap("first001", 11, allowDetachedClient: true);
        var bound = service.Bind(300, seed);
        Assert.Equal("first001", service.Validate(bound.ContextToken)?.SessionId);
        Assert.Throws<InvalidOperationException>(() => service.Bind(301, seed));
        Assert.Throws<InvalidOperationException>(() => service.Renew(bound.ContextToken, 301));
    }

    [Fact]
    public void PrivateLaunchAuthoritySurvivesChildAndWebRestartButCannotCrossCanonicalScopes()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(Started));
        var roots = Roots();
        roots.Add(new("controller02", 20, 0, "controller", "epoch-one"));
        var processes = Processes();
        processes[20] = (1, Started);
        processes[300] = (31, Started);
        processes[301] = (31, Started.AddTicks(1));
        processes[31] = (1, Started);
        var service = Create(roots, processes, clock, pid => pid == 31);
        var launch = service.IssueLaunchCapability("controller02", 20);
        Assert.Throws<InvalidOperationException>(() => service.IssueLaunchCapability("first001", 11));
        var first = service.Bind(300, launch);
        var actor = service.Validate(first.ContextToken)!.ActorIdentity;
        Assert.Null(service.Validate(launch)); // launch authority is never application authority
        Assert.Throws<InvalidOperationException>(() => service.Bind(301, first.ContextToken));
        processes.Remove(300);
        clock.Advance(TimeSpan.FromHours(8));
        var restarted = Create(roots, processes, clock, pid => pid == 31);
        var second = restarted.Bind(301, launch);
        Assert.Equal(actor, restarted.Validate(second.ContextToken)!.ActorIdentity);
        Assert.Null(restarted.Validate(first.ContextToken));
        roots[^1] = roots[^1] with { ThreadIdentity = "epoch-two" };
        Assert.Null(restarted.Validate(second.ContextToken));
        Assert.Throws<InvalidOperationException>(() => restarted.Bind(301, launch));
        var replacement = restarted.Bind(301, restarted.IssueLaunchCapability("controller02", 20));
        Assert.NotEqual(actor, restarted.Validate(replacement.ContextToken)!.ActorIdentity, StringComparer.Ordinal);
        processes[20] = (1, Started.AddSeconds(1));
        Assert.Null(restarted.Validate(replacement.ContextToken));
        Assert.Throws<InvalidOperationException>(() => restarted.Bind(301, launch));
    }

    [Fact]
    public void ControllerThreadAndSessionChangesRevokeExistingCredentials()
    {
        var roots = Roots();
        roots.Add(new("second02", 20, 0, "controller", "thread-a"));
        var processes = Processes();
        processes[200] = (21, Started);
        processes[21] = (20, Started);
        processes[20] = (1, Started);
        var service = Create(roots, processes, new FakeTimeProvider(new DateTimeOffset(Started)), pid => pid == 21);
        var bound = service.Bind(200);
        Assert.Equal("second02", service.Validate(bound.ContextToken)?.SessionId);
        roots[^1] = roots[^1] with { ThreadIdentity = "thread-b" };
        Assert.Null(service.Validate(bound.ContextToken));
        Assert.Throws<InvalidOperationException>(() => service.Renew(bound.ContextToken, 200));
        var replacement = service.Bind(200);
        roots.RemoveAt(roots.Count - 1);
        Assert.Null(service.Validate(replacement.ContextToken));
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "IDISP014:Use a single instance of HttpClient", Justification = "This fake authority and handler must be isolated per test.")]
    public async Task ExplicitRemoteOperatorSendsNoPidOrRuntimeCredentialAndNeverReplays()
    {
        var options = new McpStdioOptions { Origin = new Uri("https://operator.example"), BaseUrl = "https://operator.example",
            ApiKey = "fixture-credential", OperatorMode = true };
        var credentials = new McpStdioCredentials(options, new CookieContainer());
        using var capture = new CaptureHandler();
        using var handler = new McpStdioContextHandler(null, credentials, operatorMode: true) { InnerHandler = capture };
        using var client = new HttpClient(handler, disposeHandler: false);
        credentials.Configure(client);
        using var request = new HttpRequestMessage(HttpMethod.Post, "https://operator.example/api/mcp");
        request.Headers.Add(TlbxMcpClientContextService.RuntimeHeader, "must-be-removed");
        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(HttpStatusCode.InternalServerError, response.StatusCode);
        Assert.Equal(1, capture.Count);
        Assert.Equal("explicit", capture.Operator);
        Assert.Null(capture.Runtime);
        Assert.Equal("Bearer fixture-credential", capture.Authentication);
        Assert.Equal("/api/mcp", capture.Path);
    }

    [Fact]
    public void TamperedOrOtherInstallationCredentialCannotRenew()
    {
        var processes = Processes();
        var clock = new FakeTimeProvider(new DateTimeOffset(Started));
        var service = Create(Roots(), processes, clock);
        var bound = service.Bind(100);
        var tampered = bound.ContextToken[..^1] + (bound.ContextToken[^1] == '0' ? '1' : '0');
        Assert.Null(service.Validate(tampered));
        var other = new TlbxMcpClientContextService(Roots,
            pid => processes.TryGetValue(pid, out var p) ? p : null, () => SHA256.HashData(Key), clock);
        Assert.Null(other.Validate(bound.ContextToken));
        Assert.Throws<InvalidOperationException>(() => other.Renew(bound.ContextToken, 100));
    }

    [Fact]
    public void LocalBootstrapRejectsShareAnonymousRemoteAndProxyRequests()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback;
        Assert.Equal(401, TlbxMcpClientContextService.ValidateLocalRequest(context)?.Status);
        RequestAccessContext.SetFullUser(context, true);
        Assert.Null(TlbxMcpClientContextService.ValidateLocalRequest(context));
        context.Request.Headers["X-Forwarded-For"] = "127.0.0.1";
        Assert.Equal(403, TlbxMcpClientContextService.ValidateLocalRequest(context)?.Status);
        context.Request.Headers.Clear();
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.0.2.1");
        Assert.Equal(403, TlbxMcpClientContextService.ValidateLocalRequest(context)?.Status);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://localhost:2000")]
    [InlineData("https://user:password@localhost:2000")]
    [InlineData("https://localhost:2000/api/mcp")]
    [InlineData("https://localhost:2000/?token=secret")]
    public void NativeTransportNeverSendsCredentialsToAnotherOrigin(string url) =>
        Assert.Throws<ArgumentException>(() => McpStdioOptions.ValidateOrigin(url));

    [Fact]
    public void RemoteOriginsRequireAnExplicitOperatorAndStillRequireHttps()
    {
        Assert.Equal("operator.example", McpStdioOptions.ValidateOrigin("https://operator.example", operatorMode: true).Host);
        Assert.Throws<ArgumentException>(() => McpStdioOptions.ValidateOrigin("http://operator.example", operatorMode: true));
    }

    [Fact]
    public void ExplicitCertificatePinAlwaysAppliesEvenForOtherwiseTrustedCertificates()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=localhost", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddMinutes(5));
        var pin = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        Assert.True(TlbxMcpStdioCommand.ValidatePinnedCertificate(certificate, SslPolicyErrors.None, pin));
        Assert.True(TlbxMcpStdioCommand.ValidatePinnedCertificate(certificate, SslPolicyErrors.RemoteCertificateChainErrors, pin));
        Assert.False(TlbxMcpStdioCommand.ValidatePinnedCertificate(certificate, SslPolicyErrors.None, new string('0', 64)));
        Assert.True(TlbxMcpStdioCommand.ValidatePinnedCertificate(certificate, SslPolicyErrors.RemoteCertificateNameMismatch, pin));
        Assert.False(TlbxMcpStdioCommand.ValidatePinnedCertificate(certificate, SslPolicyErrors.RemoteCertificateNotAvailable, pin));
    }

    [Fact]
    public void CookieCacheIsExactHostAndExpiryBoundAndHonorsRejectionTombstone()
    {
        var origin = new Uri("https://localhost:2000");
        Assert.Equal("fresh", McpStdioCredentials.ReadCookie([
            "# Netscape HTTP Cookie File", "#HttpOnly_localhost\tFALSE\t/\tTRUE\t4102444800\tmm-session\tfresh"], origin));
        Assert.Null(McpStdioCredentials.ReadCookie([], origin));
        Assert.Null(McpStdioCredentials.ReadCookie(["other-host\tFALSE\t/\tTRUE\t4102444800\tmm-session\twrong"], origin));
        Assert.Null(McpStdioCredentials.ReadCookie(["localhost\tFALSE\t/\tTRUE\t1\tmm-session\texpired"], origin));
        Assert.Null(McpStdioCredentials.ReadCookie(["localhost\tFALSE\t/other\tTRUE\t4102444800\tmm-session\twrong-path"], origin));
    }

    [Fact]
    public void OperatorProxyPrefixAndCookiePathRemainBoundToTheirConfiguredServer()
    {
        var origin = McpStdioOptions.ValidateOrigin("https://operator.example/tlbx", operatorMode: true);
        var options = new McpStdioOptions { Origin = origin, BaseUrl = origin.AbsoluteUri };
        Assert.Equal("/tlbx/api/mcp", options.Endpoint("/api/mcp").AbsolutePath);
        Assert.Equal("scoped", McpStdioCredentials.ReadCookie([
            "operator.example\tFALSE\t/tlbx/\tTRUE\t4102444800\tmm-session\tscoped"], origin));
        Assert.Null(McpStdioCredentials.ReadCookie([
            "operator.example\tFALSE\t/tlbx-sibling/\tTRUE\t4102444800\tmm-session\tsibling"], origin));
    }

    [Fact]
    public void PrivateCredentialFileAcceptsOnlyTheExplicitAuthoritySchema()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tlbx-mcp-reader-test-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows())
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(true, false);
            security.SetOwner(identity.User!);
            security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).Create(security);
        }
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var path = Path.Combine(directory, "credentials.json");
        try
        {
            File.WriteAllText(path, "{\"MT_BASE_URL\":\"https://localhost:2000\",\"MT_TOKEN\":\"fixture-token\"}");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.Equal("fixture-token", McpStdioOptions.ReadCredentialsFile(path)["MT_TOKEN"]);
            var options = McpStdioOptions.Parse(["--stdio", "--credentials-file", path]);
            Assert.Null(options.ApiKey);
            Assert.Equal("fixture-token", options.CookieSeed);
            File.WriteAllText(path, "{\"MT_BASE_URL\":\"https://localhost:2000\",\"MT_SESSION_ID\":\"spoofed\"}");
            Assert.Throws<ArgumentException>(() => McpStdioOptions.ReadCredentialsFile(path));
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.OtherRead);
                Assert.Throws<UnauthorizedAccessException>(() => McpStdioOptions.ReadCredentialsFile(path));
            }
        }
        finally { File.Delete(path); Directory.Delete(directory); }
    }

    [Fact]
    public async Task StdioProxyPreservesResourcePaginationOpaqueUrisBlobBytesAndNativeErrors()
    {
        const string uri = "tlbx-artifact://private/opaque_%2F_A?offset=17&length=53";
        var bytes = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        await WithStreamProxyAsync(builder => builder
            .WithListResourcesHandler((request, _) => ValueTask.FromResult(new ListResourcesResult
            {
                Resources = [new Resource { Name = request.Params?.Cursor ?? "first-page", Uri = uri }],
                NextCursor = request.Params?.Cursor is null ? "cursor:ä/%2F" : null
            }))
            .WithListResourceTemplatesHandler((_, _) => ValueTask.FromResult(new ListResourceTemplatesResult
            { ResourceTemplates = [new ResourceTemplate { Name = "chunk", UriTemplate = "tlbx-artifact://private/{handle}{?offset,length}" }] }))
            .WithReadResourceHandler((request, _) => request.Params!.Uri == uri
                ? ValueTask.FromResult(new ReadResourceResult { Contents = [BlobResourceContents.FromBytes(bytes, uri, "application/octet-stream")] })
                : throw new McpProtocolException("opaque resource denied", McpErrorCode.InvalidParams)), async (client, _) =>
        {
            Assert.NotNull(client.ServerCapabilities.Resources);
            Assert.Null(client.ServerCapabilities.Prompts);
            var first = await client.ListResourcesAsync(new ListResourcesRequestParams(), TestContext.Current.CancellationToken);
            Assert.Equal(uri, first.Resources.Single().Uri);
            Assert.Equal("cursor:ä/%2F", first.NextCursor);
            var second = await client.ListResourcesAsync(new ListResourcesRequestParams { Cursor = first.NextCursor }, TestContext.Current.CancellationToken);
            Assert.Equal(first.NextCursor, second.Resources.Single().Name);
            Assert.Null(second.NextCursor);
            var templates = await client.ListResourceTemplatesAsync(new ListResourceTemplatesRequestParams(), TestContext.Current.CancellationToken);
            Assert.Equal("tlbx-artifact://private/{handle}{?offset,length}", templates.ResourceTemplates.Single().UriTemplate);
            var read = await client.ReadResourceAsync(new ReadResourceRequestParams { Uri = uri }, TestContext.Current.CancellationToken);
            Assert.Equal(bytes, Assert.IsType<BlobResourceContents>(read.Contents.Single()).DecodedData.ToArray());
            var error = await Assert.ThrowsAsync<McpProtocolException>(async () => await client.ReadResourceAsync("tlbx-artifact://private/sibling", cancellationToken: TestContext.Current.CancellationToken));
            Assert.Equal(McpErrorCode.InvalidParams, error.ErrorCode);
            Assert.Contains("opaque resource denied", error.Message, StringComparison.Ordinal);
        });
    }

    [Fact]
    public async Task StdioProxyForwardsPromptsAndCancellationWithoutAdvertisingUnsupportedSubscriptions()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        await WithStreamProxyAsync(builder => builder
            .WithListPromptsHandler((request, _) => ValueTask.FromResult(new ListPromptsResult
            { Prompts = [new Prompt { Name = request.Params?.Cursor ?? "prompt" }], NextCursor = "next-prompt" }))
            .WithGetPromptHandler((request, _) => ValueTask.FromResult(new GetPromptResult { Description = request.Params!.Arguments!["literal"].ToString(), Messages = [] }))
            .WithListResourcesHandler((_, _) => ValueTask.FromResult(new ListResourcesResult { Resources = [] }))
            .WithReadResourceHandler(async (_, ct) =>
            {
                entered.SetResult();
                try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                return new ReadResourceResult { Contents = [] };
            }), async (client, source) =>
        {
            var ct = TestContext.Current.CancellationToken;
            Assert.NotNull(client.ServerCapabilities.Prompts);
            Assert.NotEqual(true, client.ServerCapabilities.Resources!.Subscribe);
            Assert.NotEqual(true, client.ServerCapabilities.Resources.ListChanged);
            var prompts = await client.ListPromptsAsync(new ListPromptsRequestParams { Cursor = "cursor/verbatim" }, ct);
            Assert.Equal("cursor/verbatim", prompts.Prompts.Single().Name);
            Assert.Equal("next-prompt", prompts.NextCursor);
            const string literal = "Grüße '$()\nline2";
            var prompt = await client.GetPromptAsync("prompt", new Dictionary<string, object?>(StringComparer.Ordinal) { ["literal"] = literal }, cancellationToken: ct);
            Assert.Equal(literal, prompt.Description);
            const string resourceUri = "tlbx-artifact://private/opaque%2F";
            var requestId = new RequestId("native-cancel-resource-read");
            var read = client.SendRequestAsync(new JsonRpcRequest { Id = requestId, Method = RequestMethods.ResourcesRead,
                Params = new System.Text.Json.Nodes.JsonObject { ["uri"] = resourceUri } }, ct);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            // Native stdio cancellation is a JSON-RPC notification. Send and await
            // the actual wire message, rather than depending on an SDK client's
            // fire-and-forget CancellationToken callback to finish transmitting it.
            await client.SendNotificationAsync(NotificationMethods.CancelledNotification,
                new CancelledNotificationParams { RequestId = requestId }, cancellationToken: ct);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await read);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        });
    }

    [Fact]
    public async Task StdioToolCallsPreserveNativeMetadataResultsAndAwaitUpstreamCancellationWithoutReplay()
    {
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        const string nativeThread = "242c5edf-9911-4a56-8491-4c5f10b18619";
        const string literal = "Grüße '$()\nsecond line";
        await WithStreamProxyAsync(builder => builder.WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
        { Tools = [new Tool { Name = "fixture_browser_wait", InputSchema = JsonSerializer.SerializeToElement(new { type = "object" }) }] }))
            .WithCallToolHandler(async (request, ct) =>
            {
                Interlocked.Increment(ref calls);
                Assert.Equal(nativeThread, request.JsonRpcRequest.Params!["_meta"]!["threadId"]!.GetValue<string>());
                if (request.Params!.Arguments!["wait"].GetBoolean())
                {
                    entered.SetResult();
                    try { await Task.Delay(Timeout.InfiniteTimeSpan, ct); }
                    catch (OperationCanceledException) { cancelled.TrySetResult(); throw; }
                }
                Assert.Equal(literal, request.Params.Arguments["literal"].GetString());
                return new CallToolResult { Content = [new TextContentBlock { Text = literal }],
                    StructuredContent = JsonSerializer.SerializeToElement(new { literal, untouched = true }) };
            }), async (client, _) =>
        {
            var ct = TestContext.Current.CancellationToken;
            JsonObject Params(bool wait) => new() { ["name"] = "fixture_browser_wait", ["arguments"] = new JsonObject { ["wait"] = wait, ["literal"] = literal },
                ["_meta"] = new JsonObject { ["threadId"] = nativeThread, ["customMarker"] = "original" } };
            var echo = await client.SendRequestAsync(new JsonRpcRequest { Method = RequestMethods.ToolsCall, Params = Params(false) }, ct);
            Assert.Equal(literal, echo.Result!["structuredContent"]!["literal"]!.GetValue<string>());
            Assert.True(echo.Result["structuredContent"]!["untouched"]!.GetValue<bool>());
            var id = new RequestId("native-cancel-browser-wait");
            var wait = client.SendRequestAsync(new JsonRpcRequest { Id = id, Method = RequestMethods.ToolsCall, Params = Params(true) }, ct);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            await client.SendNotificationAsync(NotificationMethods.CancelledNotification, new CancelledNotificationParams { RequestId = id }, cancellationToken: ct);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await wait);
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
            Assert.Equal(2, Volatile.Read(ref calls));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NativeUpdateRecoveryRunsDirectlyAndInsideWorkflowWithoutReplayingApply(bool nested)
    {
        var applied = 0;
        var after = 0;
        Tool Definition(string name) => new() { Name = name, InputSchema = JsonSerializer.SerializeToElement(new { type = "object", properties = new { } }) };
        await WithStreamProxyAsync(builder => builder
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
            {
                Tools = [Definition("context_get"), Definition("update_status"), Definition("update_apply"), Definition("workflow_run"), Definition("fixture_after")]
            }))
            .WithCallToolHandler((request, _) =>
            {
                var name = request.Params!.Name;
                if (name == "context_get") return ValueTask.FromResult(McpApiResult.Structured(new JsonObject
                {
                    ["serverInstanceId"] = applied == 0 ? "before" : "after", ["serverVersion"] = applied == 0 ? "1.0" : "2.0"
                }));
                if (name == "update_status") return ValueTask.FromResult(McpApiResult.Structured(new JsonObject
                {
                    ["data"] = new JsonObject { ["available"] = true, ["latestVersion"] = "2.0" }
                }));
                if (name == "update_apply")
                {
                    Assert.False(request.Params.Arguments!.ContainsKey("expectedVersion"));
                    Assert.True(request.Params.Arguments["detached"].GetBoolean());
                    Interlocked.Increment(ref applied);
                    return ValueTask.FromResult(McpApiResult.Structured(new JsonObject { ["statusCode"] = 202, ["data"] = "scheduled" }));
                }
                Assert.Equal("fixture_after", name);
                Interlocked.Increment(ref after);
                return ValueTask.FromResult(McpApiResult.Structured(new JsonObject { ["done"] = true }));
            }), async (client, _) =>
            {
                var ct = TestContext.Current.CancellationToken;
                var tools = await client.ListToolsAsync(cancellationToken: ct);
                Assert.True(tools.Single(tool => tool.Name == "update_apply").ProtocolTool.InputSchema.GetProperty("properties").TryGetProperty("expectedVersion", out var expectedVersionSchema));
                Assert.Equal("string", expectedVersionSchema.GetProperty("type").GetString());
                CallToolResult result;
                if (nested)
                    result = await client.CallToolAsync("workflow_run", new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["steps"] = new object[]
                        {
                            new { tool = "update_apply", arguments = new { expectedVersion = "2.0", timeoutSeconds = 1 } },
                            new { tool = "fixture_after", arguments = new { } }
                        }
                    }, cancellationToken: ct);
                else
                    result = await client.CallToolAsync("update_apply", new Dictionary<string, object?>(StringComparer.Ordinal)
                    { ["expectedVersion"] = "2.0", ["timeoutSeconds"] = 1 }, cancellationToken: ct);
                Assert.False(result.IsError == true, result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
                Assert.Equal(1, applied);
                Assert.Equal(nested ? 1 : 0, after);
                if (!nested) Assert.True(result.StructuredContent!.Value.GetProperty("restartVerified").GetBoolean());
            });
    }

    private static async Task WithStreamProxyAsync(Action<IMcpServerBuilder> configure, Func<McpClient, McpServer, Task> verify)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var sourceInput = new Pipe(); var sourceOutput = new Pipe();
        await using var sourceRead = sourceInput.Reader.AsStream();
        await using var sourceWrite = sourceOutput.Writer.AsStream();
        await using var upstreamWrite = sourceInput.Writer.AsStream();
        await using var upstreamRead = sourceOutput.Reader.AsStream();
        var sourceServices = new ServiceCollection();
        sourceServices.AddLogging();
        configure(sourceServices.AddMcpServer().WithStreamServerTransport(sourceRead, sourceWrite));
        await using var sourceProvider = sourceServices.BuildServiceProvider();
        var source = sourceProvider.GetRequiredService<McpServer>();
        await using var sourceCancellation = source.RegisterNotificationHandler(NotificationMethods.CancelledNotification, (notification, _) =>
        { Console.Error.WriteLine("MCP_CANCEL_SOURCE " + notification.Params?.ToJsonString()); return ValueTask.CompletedTask; });
        var sourceRun = source.RunAsync(lifetime.Token);
        var proxyInput = new Pipe(); var proxyOutput = new Pipe();
        await using var proxyRead = proxyInput.Reader.AsStream();
        await using var proxyWrite = proxyOutput.Writer.AsStream();
        await using var clientWrite = proxyInput.Writer.AsStream();
        await using var clientRead = proxyOutput.Reader.AsStream();
        Task? proxyRun = null;
        try
        {
            await using var upstream = await McpClient.CreateAsync(new StreamClientTransport(upstreamWrite, upstreamRead), cancellationToken: lifetime.Token);
            var proxyServices = new ServiceCollection();
            proxyServices.AddLogging();
            var builder = proxyServices.AddMcpServer().WithStreamServerTransport(proxyRead, proxyWrite);
            TlbxMcpStdioCommand.ConfigureForwarding(builder, upstream);
            await using var proxyProvider = proxyServices.BuildServiceProvider();
            var proxy = proxyProvider.GetRequiredService<McpServer>();
            await using var proxyCancellation = proxy.RegisterNotificationHandler(NotificationMethods.CancelledNotification, (notification, _) =>
            { Console.Error.WriteLine("MCP_CANCEL_PROXY " + notification.Params?.ToJsonString()); return ValueTask.CompletedTask; });
            proxyRun = proxy.RunAsync(lifetime.Token);
            await using var client = await McpClient.CreateAsync(new StreamClientTransport(clientWrite, clientRead), cancellationToken: lifetime.Token);
            await verify(client, source);
        }
        finally
        {
            await lifetime.CancelAsync();
            try { await sourceRun; } catch (OperationCanceledException) { }
            if (proxyRun is not null) try { await proxyRun; } catch (OperationCanceledException) { }
        }
    }

    [Fact]
    public void DiscoveryCredentialCannotBecomeSelfOrSurviveBackendReplacement()
    {
        var processes = Processes();
        processes[100] = (31, Started);
        processes[31] = (11, Started);
        var clock = new FakeTimeProvider(new DateTimeOffset(Started));
        var service = Create(Roots(), processes, clock, pid => pid == 31);
        var discovery = service.Bind(100);
        Assert.Equal("discovery", service.Validate(discovery.ContextToken)!.Mode);
        Assert.Null(service.Validate(discovery.ContextToken)!.SessionId);
        var restarted = Create(Roots(), processes, clock, pid => pid == 31);
        Assert.Equal("discovery", restarted.Validate(discovery.ContextToken)!.Mode);
        processes[31] = (11, Started.AddTicks(1));
        Assert.Null(restarted.Validate(discovery.ContextToken));
        Assert.Throws<InvalidOperationException>(() => restarted.Renew(discovery.ContextToken, 100));
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "IDISP014:Use a single instance of HttpClient", Justification = "The synthetic identity authority and handler are isolated per test.")]
    public async Task SharedNativeThreadsKeepIndependentPerCallScopesAndRejectUnscopedResources()
    {
        var options = new McpStdioOptions { Origin = new Uri("https://localhost:2000/"), BaseUrl = "https://localhost:2000", ApiKey = "fixture" };
        var credentials = new McpStdioCredentials(options, new CookieContainer());
        using var authority = new NativeIdentityHandler();
        using var http = new HttpClient(authority, disposeHandler: false);
        using var context = new McpStdioRuntimeContext(http, credentials, options);
        var ct = TestContext.Current.CancellationToken;
        await context.EnsureAsync(true, ct);
        var threadA = Guid.NewGuid().ToString();
        var threadB = Guid.NewGuid().ToString();
        async Task Resolve(string thread)
        {
            var request = new JsonRpcRequest { Method = RequestMethods.ToolsCall,
                Params = new JsonObject { ["name"] = "context_get", ["_meta"] = new JsonObject { ["threadId"] = thread, ["customMarker"] = "untouched" } } };
            var binding = await context.ResolveNativeCallAsync(request, false, ct);
            using (context.EnterNativeScope(binding))
            {
                await Task.Yield();
                Assert.Equal(thread, context.CurrentNativeScope!.ContextToken);
                Assert.Equal("untouched", request.Params!["_meta"]!["customMarker"]!.GetValue<string>());
            }
            Assert.Null(context.CurrentNativeScope);
        }
        await Task.WhenAll(Task.Run(() => Resolve(threadA), ct), Task.Run(() => Resolve(threadB), ct));
        Assert.Null(context.CurrentNativeScope);
        Assert.Equal(2, authority.ThreadProofs);
        await Assert.ThrowsAsync<InvalidOperationException>(async () => await context.ResolveNativeCallAsync(new JsonRpcRequest
        { Method = RequestMethods.ResourcesRead, Params = new JsonObject { ["uri"] = "tlbx-artifact://opaque" } }, false, ct));
        Assert.Equal(2, authority.ThreadProofs);
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Reliability", "IDISP014:Use a single instance of HttpClient", Justification = "The synthetic identity authority and handler are isolated per test.")]
    public async Task LongNativeWorkflowRenewsEachOperationForItsImmutableThreadAndRejectsSessionMigration()
    {
        var options = new McpStdioOptions { Origin = new Uri("https://localhost:2000/"), BaseUrl = "https://localhost:2000", ApiKey = "fixture" };
        var credentials = new McpStdioCredentials(options, new CookieContainer());
        using var authority = new NativeIdentityHandler { UniqueExpiredLeases = true };
        using var http = new HttpClient(authority, disposeHandler: false);
        using var context = new McpStdioRuntimeContext(http, credentials, options);
        var ct = TestContext.Current.CancellationToken;
        var thread = Guid.NewGuid().ToString();
        var original = await context.ResolveNativeCallAsync(new JsonRpcRequest { Method = RequestMethods.ToolsCall,
            Params = new JsonObject { ["name"] = "workflow_run", ["_meta"] = new JsonObject { ["threadId"] = thread } } }, false, ct);
        Assert.NotNull(original);
        var firstOperation = await context.RefreshNativeScopeAsync(original, ct);
        var laterOperation = await context.RefreshNativeScopeAsync(original, ct);
        Assert.NotEqual(original.ContextToken, firstOperation, StringComparer.Ordinal);
        Assert.NotEqual(firstOperation, laterOperation, StringComparer.Ordinal);
        Assert.Equal("first001", authority.LastExpectedSession);
        Assert.Equal(thread, original.ThreadId);
        authority.SessionId = "sibling2";
        await Assert.ThrowsAsync<InvalidOperationException>(() => context.RefreshNativeScopeAsync(original, ct));
        Assert.Equal("first001", original.SessionId);
        Assert.Equal(4, authority.ThreadProofs);
    }

    private sealed class NativeIdentityHandler : HttpMessageHandler
    {
        private int _proofs;
        internal int ThreadProofs => Volatile.Read(ref _proofs);
        internal string SessionId { get; set; } = "first001";
        internal bool UniqueExpiredLeases { get; init; }
        internal string? LastExpectedSession { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            await using var stream = await request.Content!.ReadAsStreamAsync(cancellationToken);
            var input = await JsonSerializer.DeserializeAsync(stream, McpClientJsonContext.Default.McpContextRequest, cancellationToken);
            if (input!.NativeThreadId is not null) Interlocked.Increment(ref _proofs);
            LastExpectedSession = input.ExpectedSessionId;
            var output = new McpContextResponse { ContextToken = input.NativeThreadId is null ? "discovery-token" : input.NativeThreadId + (UniqueExpiredLeases ? "-" + ThreadProofs.ToString(System.Globalization.CultureInfo.InvariantCulture) : ""),
                SessionId = input.NativeThreadId is null ? null : SessionId, Mode = input.NativeThreadId is null ? "discovery" : "cli-thread",
                ExpiresAt = UniqueExpiredLeases && input.NativeThreadId is not null ? DateTimeOffset.UtcNow.AddMinutes(-5) : DateTimeOffset.UtcNow.AddMinutes(30) };
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(output, McpClientJsonContext.Default.McpContextResponse)) };
        }
    }

    [Fact]
    public async Task CliActorIdentitySeparatesActualThreadsAndSurvivesTheirToolProcessReplacement()
    {
        var processes = Processes();
        processes[100] = (31, Started); processes[101] = (31, Started); processes[31] = (11, Started);
        using var cli = new SessionCliContextService(() => [new("first001", 10, 11)],
            pid => processes.TryGetValue(pid, out var process) ? process : null, TimeProvider.System, pid => pid == 31);
        var root = Guid.NewGuid().ToString(); var childA = Guid.NewGuid().ToString(); var childB = Guid.NewGuid().ToString();
        async Task<string> Lease(string thread, int processId)
        {
            var response = await cli.ResolveAsync(root, thread, processId, false, (marker, _) =>
            { cli.ObserveOutput("first001", Encoding.UTF8.GetBytes(marker)); return Task.FromResult(true); }, TestContext.Current.CancellationToken);
            Assert.True(SessionCliContextService.IsResolution(response), response);
            return response.Split(':')[1];
        }
        var first = await Lease(childA, 100);
        var replacement = await Lease(childA, 101);
        var sibling = await Lease(childB, 100);
        Assert.Equal(cli.GetLeaseActorIdentity(first), cli.GetLeaseActorIdentity(replacement));
        Assert.NotEqual(cli.GetLeaseActorIdentity(first), cli.GetLeaseActorIdentity(sibling), StringComparer.Ordinal);
        cli.InvalidateInput("first001");
        Assert.Null(cli.GetLeaseActorIdentity(first));
        Assert.Null(cli.GetLeaseActorIdentity(sibling));
    }

    [Fact]
    public void CallerCodexHomeUsesTheNativeCallerProfileAndPassesExplicitPathsThrough()
    {
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex"), McpStdioRuntimeContext.CallerCodexHome(null));
        Assert.Equal("custom-profile-path", McpStdioRuntimeContext.CallerCodexHome("custom-profile-path"));
    }

    [Fact]
    public void ProxyRootCookieRenewalReplacesSeedAndPersistsTheRenewedCredential()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tlbx-mcp-cookie-test-" + Guid.NewGuid().ToString("N"));
        var options = new McpStdioOptions { Origin = new Uri("https://operator.example/prefix/"), BaseUrl = "https://operator.example/prefix",
            CookieSeed = "original-seed", OperatorMode = true, CacheDirectory = directory };
        try
        {
            var cookies = new CookieContainer();
            var credentials = new McpStdioCredentials(options, cookies);
            var endpoint = options.Endpoint("/api/mcp");
            Assert.Equal("/", cookies.GetCookies(endpoint)["mm-session"]!.Path);
            const string renewal = "mm-session=renewed-value; Path=/; Secure; HttpOnly";
            cookies.SetCookies(endpoint, renewal);
            using var response = new HttpResponseMessage(HttpStatusCode.OK);
            response.Headers.Add("Set-Cookie", renewal);
            credentials.Observe(response);
            var applicable = cookies.GetCookies(endpoint).Cast<Cookie>().Where(cookie => cookie.Name == "mm-session").ToArray();
            Assert.Equal("renewed-value", Assert.Single(applicable).Value);
            Assert.Equal("renewed-value", McpStdioCredentials.ReadCookie(File.ReadAllLines(Assert.Single(Directory.GetFiles(directory))), options.Origin));
            var reconnectedCookies = new CookieContainer();
            _ = new McpStdioCredentials(options, reconnectedCookies);
            Assert.Equal("renewed-value", reconnectedCookies.GetCookies(endpoint)["mm-session"]!.Value);
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                foreach (var file in Directory.GetFiles(directory)) File.Delete(file);
                Directory.Delete(directory);
            }
        }
    }

    private static List<TlbxMcpClientContextService.RuntimeRoot> Roots() => [new("first001", 11, 10, "terminal")];
    private static Dictionary<int, (int Parent, DateTime Started)> Processes() => new()
    { [100] = (11, Started), [11] = (10, Started), [10] = (1, Started) };
    private static TlbxMcpClientContextService Create(List<TlbxMcpClientContextService.RuntimeRoot> roots,
        Dictionary<int, (int Parent, DateTime Started)> processes, TimeProvider clock, Func<int, bool?>? shared = null) =>
        new(() => roots, pid => processes.TryGetValue(pid, out var p) ? p : null, () => Key, clock, shared);

    private sealed class CaptureHandler : HttpMessageHandler
    {
        internal int Count { get; private set; }
        internal string? Operator { get; private set; }
        internal string? Runtime { get; private set; }
        internal string? Authentication { get; private set; }
        internal string? Path { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Count++;
            Operator = request.Headers.TryGetValues(McpStdioContextHandler.OperatorHeader, out var op) ? op.Single() : null;
            Runtime = request.Headers.TryGetValues(TlbxMcpClientContextService.RuntimeHeader, out var runtime) ? runtime.Single() : null;
            Authentication = request.Headers.Authorization?.ToString();
            Path = request.RequestUri?.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
        }
    }
}

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Boards;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

[Collection(PathSensitiveEnvironmentCollection.Name)]
public sealed class TlbxCliScriptWriterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "tlbx-cli-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task NativeBoardServeUsesVerifiedTransportAndExactLocalDirectory()
    {
        var helperDirectory = Path.Combine(_tempDir, ".tlbx");
        var program = Path.Combine(_tempDir, "Grüße local program");
        Directory.CreateDirectory(helperDirectory);
        Directory.CreateDirectory(program);
        await File.WriteAllTextAsync(Path.Combine(program, "index.html"), "<button>Local</button>", Xunit.TestContext.Current.CancellationToken);
        TlbxCliScriptWriter.WriteScripts(helperDirectory, 2100, "test-token");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        BoardItemWrite? received = null;
        app.MapGet("/api/sessions/process-context", () => Results.Text("abcdefgh:" + new string('a', 32)));
        app.MapPost("/api/boards/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa/items", async (HttpContext context) =>
        {
            Assert.Equal(new string('a', 32), context.Request.Headers["X-Tlbx-Cli-Context"]);
            received = await context.Request.ReadFromJsonAsync(BoardJsonContext.Default.BoardItemWrite, context.RequestAborted);
            return Results.Text("created");
        });
        await app.StartAsync(CancellationToken.None);
        var names = new[] { "MT_BASE_URL", "MT_SESSION_ID", "CODEX_SESSION_ID", "CODEX_THREAD_ID" };
        var previous = names.ToDictionary(name => name, Environment.GetEnvironmentVariable, StringComparer.Ordinal);
        try
        {
            Environment.SetEnvironmentVariable("MT_BASE_URL", app.Urls.Single());
            Environment.SetEnvironmentVariable("MT_SESSION_ID", "deadbeef");
            Environment.SetEnvironmentVariable("CODEX_SESSION_ID", null);
            Environment.SetEnvironmentVariable("CODEX_THREAD_ID", "01234567-89ab-cdef-0123-456789abcdef");
            var exit = BoardCliCommand.Run(["serve", new string('a', 32), program], _tempDir);
            Assert.Equal(0, exit);
            Assert.NotNull(received);
            Assert.Equal(program, received.Item.LocalDirectory);
            Assert.Equal("browser", received.Item.Kind);
            Assert.Equal(640, received.Item.Width);
        }
        finally
        {
            foreach (var pair in previous) Environment.SetEnvironmentVariable(pair.Key, pair.Value);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BoardCrudPreservesBodiesRevisionsAndVerifiedContext(bool bash)
    {
        if ((bash ? ResolveBashPath() : ResolvePowerShellPath()) is null) return;
        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2100, "test-token");
        await File.WriteAllTextAsync(Path.Combine(_tempDir, "board-body.json"), "{\"title\":\"Grüße $ ` test\"}", Xunit.TestContext.Current.CancellationToken);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var requests = new List<(string Method, string Path, string Body)>();
        app.MapGet("/api/sessions/process-context", () => Results.Text("abcdefgh:" + new string('a', 32)));
        app.MapMethods("/api/boards/{**rest}", ["GET", "POST", "PATCH", "PUT", "DELETE"], async (HttpContext context) =>
        {
            Assert.Equal(new string('a', 32), context.Request.Headers["X-Tlbx-Cli-Context"]);
            using var reader = new StreamReader(context.Request.Body);
            requests.Add((context.Request.Method, context.Request.Path + context.Request.QueryString, await reader.ReadToEndAsync(context.RequestAborted)));
            return Results.Text("ok");
        });
        await app.StartAsync(CancellationToken.None);
        var result = await RunContextShellAsync(bash,
            bash ? """
                export CODEX_THREAD_ID=01234567-89ab-cdef-0123-456789abcdef
                source "$1"
                set -e
                mt_board list
                mt_board get aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
                mt_board create "@$(dirname "$1")/board-body.json"
                mt_board update aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa '{"expectedRevision":1,"zoom":0.5}'
                mt_board add aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa '{"item":{"text":"note"}}'
                mt_board write aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb '{"expectedRevision":2,"item":{"text":"changed"}}'
                mt_board browser aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb '{"command":"exec","value":"document.title"}'
                mt_board remove aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb 3
                mt_board delete aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa 4
                mt_board item aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
                mt_board launches aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
                mt_board viewport aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa '{"panX":20,"panY":30,"zoom":0.5}'
                mt_board launch aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb '{"requestId":"cccccccccccccccccccccccccccccccc","expectedRevision":2}'
                """ : """
                $env:CODEX_THREAD_ID='01234567-89ab-cdef-0123-456789abcdef'
                . $args[0]
                mt_board list
                mt_board get aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
                mt_board create ('@' + (Join-Path (Split-Path $args[0]) 'board-body.json'))
                mt_board update aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa '{"expectedRevision":1,"zoom":0.5}'
                mt_board add aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa '{"item":{"text":"note"}}'
                mt_board write aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb '{"expectedRevision":2,"item":{"text":"changed"}}'
                mt_board browser aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb '{"command":"exec","value":"document.title"}'
                mt_board remove aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb 3
                mt_board delete aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa 4
                mt_board item aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb
                mt_board launches aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa
                mt_board viewport aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa '{"panX":20,"panY":30,"zoom":0.5}'
                mt_board launch aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb '{"requestId":"cccccccccccccccccccccccccccccccc","expectedRevision":2}'
                """,
            app.Urls.Single(), "deadbeef", Path.Combine(_tempDir, bash ? "tlbx_cli.sh" : "tlbx_cli.ps1"));
        Assert.True(result.ExitCode == 0, result.Error + result.Output);
        Assert.Equal(13, requests.Count);
        Assert.Equal(new[] { "GET", "GET", "POST", "PATCH", "POST", "PUT", "POST", "DELETE", "DELETE", "GET", "GET", "PATCH", "POST" }, requests.Select(request => request.Method), StringComparer.Ordinal);
        Assert.Equal("{\"title\":\"Grüße $ ` test\"}", requests[2].Body);
        Assert.Equal("{\"expectedRevision\":2,\"item\":{\"text\":\"changed\"}}", requests[5].Body);
        Assert.EndsWith("/items/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb/browser-command", requests[6].Path, StringComparison.Ordinal);
        Assert.EndsWith("/items/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb?expectedRevision=3", requests[7].Path, StringComparison.Ordinal);
        Assert.EndsWith("/aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa?expectedRevision=4", requests[8].Path, StringComparison.Ordinal);
        Assert.EndsWith("/items/bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb", requests[9].Path, StringComparison.Ordinal);
        Assert.EndsWith("/launches", requests[10].Path, StringComparison.Ordinal);
        Assert.EndsWith("/viewport", requests[11].Path, StringComparison.Ordinal);
        Assert.EndsWith("/launch", requests[12].Path, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CodexPreviewSelectionSurvivesVerifiedRenewalsButNotSessionChanges(bool bash)
    {
        if ((bash ? ResolveBashPath() : ResolvePowerShellPath()) is null) return;
        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2100, "test-token");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var sessionId = "abcdefgh";
        var resolutions = 0;
        app.MapGet("/api/sessions/process-context", () =>
        {
            Interlocked.Increment(ref resolutions);
            return Results.Text(sessionId + ":" + new string('a', 32));
        });
        app.MapPost("/api/browser/open", async (HttpContext context) =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            Assert.Equal("abcdefgh", body.RootElement.GetProperty("sessionId").GetString());
            Assert.Equal("background-test", body.RootElement.GetProperty("previewName").GetString());
            Assert.Equal(new string('a', 32), context.Request.Headers["X-Tlbx-Cli-Context"]);
            sessionId = "ijklmnop";
            return Results.Text("opened");
        });
        await app.StartAsync(CancellationToken.None);
        var result = await RunContextShellAsync(bash,
            bash ? "export CODEX_THREAD_ID=01234567-89ab-cdef-0123-456789abcdef; source \"$1\"; set -e; mt_preview background-test; mt_preview; mt_open https://example.test/; mt_preview"
                : "$env:CODEX_THREAD_ID='01234567-89ab-cdef-0123-456789abcdef'; . $args[0]; mt_preview background-test; mt_preview; mt_open https://example.test/; mt_preview",
            app.Urls.Single(), "deadbeef",
            Path.Combine(_tempDir, bash ? "tlbx_cli.sh" : "tlbx_cli.ps1"));
        Assert.True(result.ExitCode == 0, result.Error + result.Output);
        Assert.Equal(["background-test", "background-test", "opened", "default"],
            result.Output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Assert.True(resolutions >= 4, "Every operation must still verify its calling context.");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FirstPreviewSelectionSurvivesSessionContextResolution(bool bash)
    {
        if ((bash ? ResolveBashPath() : ResolvePowerShellPath()) is null) return;
        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2100, "test-token");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/api/sessions/process-context", () => Results.Text("abcdefgh"));
        await app.StartAsync(CancellationToken.None);
        var result = await RunContextShellAsync(bash,
            bash ? "source \"$1\"; mt_preview background-test"
                : ". $args[0]; mt_preview background-test",
            app.Urls.Single(), "deadbeef",
            Path.Combine(_tempDir, bash ? "tlbx_cli.sh" : "tlbx_cli.ps1"));
        Assert.True(result.ExitCode == 0, result.Error + result.Output);
        Assert.Equal("background-test", result.Output.Trim());
    }

    [Theory]
    [InlineData(false, "")]
    [InlineData(false, "deadbeef")]
    [InlineData(true, "")]
    [InlineData(true, "deadbeef")]
    public async Task LiveProcessContextRepairsMissingOrStaleIdentityBeforeFirstMutation(bool bash, string inheritedId)
    {
        if ((bash ? ResolveBashPath() : ResolvePowerShellPath()) is null) return;
        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2100, "test-token");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var requests = new System.Collections.Concurrent.ConcurrentQueue<string>();
        app.MapGet("/api/sessions/process-context", (int processId) =>
        {
            using var process = Process.GetProcessById(processId);
            requests.Enqueue("resolve");
            return Results.Text("abcdefgh");
        });
        app.MapPost("/api/git/repos", async (HttpContext context) =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            requests.Enqueue(body.RootElement.GetProperty("sessionId").GetString()!);
            return Results.Ok();
        });
        app.MapGet("/api/git/repos", (string sessionId) =>
        {
            requests.Enqueue(sessionId);
            return Results.Ok();
        });
        await app.StartAsync(CancellationToken.None);
        var result = await RunContextShellAsync(bash,
            bash ? "source \"$1\"; set -e; mt_repo add '/repo'; mt_repo --session sibling1 list; test \"$MT_SESSION_ID\" = abcdefgh"
                : ". $args[0]; mt_repo add '/repo'; mt_repo list -SessionId sibling1; if ($env:MT_SESSION_ID -ne 'abcdefgh') { throw 'Wrong identity' }",
            app.Urls.Single(), inheritedId, Path.Combine(_tempDir, bash ? "tlbx_cli.sh" : "tlbx_cli.ps1"));
        Assert.True(result.ExitCode == 0, result.Error + result.Output);
        Assert.Equal(new[] { "resolve", "abcdefgh", "sibling1" }, requests.ToArray());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ContextExport_KeepsGitRequestsOnOwningServerAcrossShells(bool producerBash, bool consumerBash)
    {
        if (ResolveBashPath() is null || ResolvePowerShellPath() is null) return;

        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2100, "test-token");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/{instance}/api/git/repos", (string instance, string sessionId) =>
            instance == "owner" && sessionId == "abcdefgh"
                ? Results.Json(new { sessionId })
                : Results.NotFound("Session not found"));
        await app.StartAsync(CancellationToken.None);
        var server = app.Urls.Single();
        var exports = await RunContextShellAsync(producerBash,
            producerBash ? "source \"$1\"; mt_context \"$2\"" : ". $args[0]; mt_context $args[1]",
            server + "/owner", "abcdefgh",
            Path.Combine(_tempDir, producerBash ? "tlbx_cli.sh" : "tlbx_cli.ps1"),
            consumerBash ? "--bash" : "--pwsh");
        Assert.Equal(0, exports.ExitCode);
        Assert.DoesNotContain("test-token", exports.Output, StringComparison.Ordinal);
        var exportPath = Path.Combine(_tempDir, consumerBash ? "context.sh" : "context.ps1");
        await File.WriteAllTextAsync(exportPath, exports.Output, CancellationToken.None);

        // A daemon/remote shell may inherit another instance's environment and
        // load a helper generated by that instance. The exported tuple must win.
        var result = await RunContextShellAsync(consumerBash,
            consumerBash ? "source \"$1\"; source \"$2\"; mt_repo list" : ". $args[0]; . $args[1]; mt_repo list",
            server + "/other", "deadbeef", exportPath,
            Path.Combine(_tempDir, consumerBash ? "tlbx_cli.sh" : "tlbx_cli.ps1"));
        Assert.True(result.ExitCode == 0, result.Error + result.Output);
        using var json = JsonDocument.Parse(result.Output);
        Assert.Equal("abcdefgh", json.RootElement.GetProperty("sessionId").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Repo_ExplicitSessionOverridesStaleContextWithoutRetargetingOrRetryingFailures(bool bash)
    {
        if ((bash ? ResolveBashPath() : ResolvePowerShellPath()) is null) return;
        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2100, "test-token");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var requests = new System.Collections.Concurrent.ConcurrentQueue<string>();
        app.MapMethods("/api/git/{**path}", ["GET", "POST", "DELETE"], async (HttpContext context) =>
        {
            var sessionId = context.Request.Query["sessionId"].ToString();
            if (context.Request.Method == "POST")
            {
                using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
                sessionId = body.RootElement.GetProperty("sessionId").GetString()!;
            }
            requests.Enqueue(sessionId);
            return sessionId == "abcdefgh" ? Results.Ok() : Results.NotFound("Session not found");
        });
        await app.StartAsync(CancellationToken.None);
        var server = app.Urls.Single();
        var helper = Path.Combine(_tempDir, bash ? "tlbx_cli.sh" : "tlbx_cli.ps1");
        var result = await RunContextShellAsync(bash,
            bash
                ? "source \"$1\"; set -e; mt_repo --session abcdefgh add '/repo with space'; mt_repo --session abcdefgh refresh; mt_repo --session abcdefgh remove '/repo with space'; mt_repo --session abcdefgh list; test \"$MT_SESSION_ID\" = deadbeef"
                : ". $args[0]; mt_repo add '/repo with space' -SessionId abcdefgh; mt_repo refresh -SessionId abcdefgh; mt_repo remove '/repo with space' -SessionId abcdefgh; mt_repo list -SessionId abcdefgh; if ($env:MT_SESSION_ID -ne 'deadbeef') { throw 'Environment changed' }",
            server, "deadbeef", helper);
        Assert.True(result.ExitCode == 0, result.Error + result.Output);
        Assert.Equal(4, requests.Count);
        Assert.All(requests, id => Assert.Equal("abcdefgh", id));

        result = await RunContextShellAsync(bash,
            bash ? "source \"$1\"; mt_repo --session abcdefgh list" : ". $args[0]; mt_repo list -SessionId abcdefgh",
            server, "", helper);
        Assert.True(result.ExitCode == 0, result.Error + result.Output);
        Assert.Equal(5, requests.Count);

        result = await RunContextShellAsync(bash,
            bash ? "source \"$1\"; mt_repo add '/repo with space'" : ". $args[0]; mt_repo add '/repo with space'",
            server, "deadbeef", helper);
        Assert.NotEqual(0, result.ExitCode);
        Assert.Contains("deadbeef", result.Error, StringComparison.Ordinal);
        Assert.Contains(server, result.Error, StringComparison.Ordinal);
        Assert.Contains("mt_sessions", result.Error, StringComparison.Ordinal);
        Assert.Equal(6, requests.Count); // A mutation failure is never replayed against another session.
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task CodexRequiresFullIdentityAndLeaseBeforeMutation(bool bash, bool bound)
    {
        if ((bash ? ResolveBashPath() : ResolvePowerShellPath()) is null) return;
        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2100, "test-token");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        const string identity = "01a10b30-4385-71e0-8a60-6fd26e1cfb01";
        const string lease = "0123456789abcdef0123456789abcdef";
        var mutations = new System.Collections.Concurrent.ConcurrentQueue<string>();
        app.MapGet("/api/sessions/process-context", (HttpContext context, int processId, string codexSessionId, string codexThreadId, string codexHome) =>
        {
            Assert.Equal(identity, codexSessionId);
            Assert.Equal(identity, codexThreadId);
            Assert.True(Path.IsPathFullyQualified(codexHome), codexHome);
            using var caller = Process.GetProcessById(processId);
            return Results.Text(bound ? "abcdefgh:" + lease : "TLBXCTX:" + lease + "\nTLBX_CONTEXT_UNBOUND: No session operation was executed.\nRetry the original command in a separate shell tool call.", statusCode: bound ? 200 : 409);
        });
        app.MapPost("/api/git/repos", async (HttpContext context) =>
        {
            Assert.Equal(lease, context.Request.Headers["X-Tlbx-Cli-Context"].ToString());
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            mutations.Enqueue(body.RootElement.GetProperty("sessionId").GetString()!);
            return Results.Ok();
        });
        await app.StartAsync(CancellationToken.None);
        var command = bash
            ? $"set -e; export CODEX_SESSION_ID={identity} CODEX_THREAD_ID={identity}; source \"$1\"; mt_repo add '/repo'"
            : $"$env:CODEX_SESSION_ID='{identity}'; $env:CODEX_THREAD_ID='{identity}'; . $args[0]; mt_repo add '/repo'";
        var result = await RunContextShellAsync(bash, command, app.Urls.Single(), "deadbeef", Path.Combine(_tempDir, bash ? "tlbx_cli.sh" : "tlbx_cli.ps1"));
        if (bound)
        {
            Assert.True(result.ExitCode == 0, result.Error + result.Output);
            Assert.Equal(new[] { "abcdefgh" }, mutations.ToArray());
        }
        else
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.Empty(mutations);
            Assert.Contains("TLBXCTX:" + lease, result.Output + result.Error, StringComparison.Ordinal);
            Assert.Contains("Retry the original command", result.Output + result.Error, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false, 401)]
    [InlineData(true, 401)]
    [InlineData(false, 403)]
    [InlineData(true, 403)]
    [InlineData(false, 500)]
    [InlineData(true, 500)]
    public async Task CodexContextFailureReportsTransportDetailsWithoutMutation(bool bash, int status)
    {
        if ((bash ? ResolveBashPath() : ResolvePowerShellPath()) is null) return;
        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2100, "test-token");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/api/sessions/process-context", () => Results.StatusCode(status));
        var mutations = 0;
        app.MapPut("/api/sessions/{id}/topic", () => { mutations++; return Results.Ok(); });
        await app.StartAsync(CancellationToken.None);
        const string identity = "01a10b30-4385-71e0-8a60-6fd26e1cfb01";
        var command = bash
            ? $"export CODEX_SESSION_ID={identity}; source \"$1\"; mt_topic diagnostic"
            : $"$env:CODEX_SESSION_ID='{identity}'; . $args[0]; mt_topic diagnostic";
        var result = await RunContextShellAsync(bash, command, app.Urls.Single(), "deadbeef",
            Path.Combine(_tempDir, bash ? "tlbx_cli.sh" : "tlbx_cli.ps1"));
        Assert.NotEqual(0, result.ExitCode);
        var diagnostic = result.Output + result.Error;
        Assert.Contains("TLBX_CONTEXT_UNAVAILABLE", diagnostic, StringComparison.Ordinal);
        Assert.Contains("curl exit 22", diagnostic, StringComparison.Ordinal);
        Assert.Contains(status.ToString(System.Globalization.CultureInfo.InvariantCulture), diagnostic, StringComparison.Ordinal);
        Assert.Contains(app.Urls.Single() + "/api/sessions/process-context", diagnostic, StringComparison.Ordinal);
        Assert.Contains("httpStatus=" + status + ";", diagnostic, StringComparison.Ordinal);
        Assert.Contains("helper=", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("test-token", diagnostic, StringComparison.Ordinal);
        Assert.Equal(0, mutations);
    }

    [Theory]
    [InlineData(false, "bound")]
    [InlineData(true, "bound")]
    [InlineData(false, "UNBOUND")]
    [InlineData(true, "UNBOUND")]
    [InlineData(false, "CHANGED")]
    [InlineData(true, "CHANGED")]
    public async Task ContextRepairReportsVerifiedSuccessOrCompleteActionableFailure(bool bash, string outcome)
    {
        if ((bash ? ResolveBashPath() : ResolvePowerShellPath()) is null) return;
        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2100, "test-token");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        const string identity = "01a10b30-4385-71e0-8a60-6fd26e1cfb01";
        const string token = "0123456789abcdef0123456789abcdef";
        var freshRequests = 0;
        app.MapGet("/api/sessions/process-context", async (bool fresh, int processId) =>
        {
            if (fresh) Interlocked.Increment(ref freshRequests);
            if (outcome == "CHANGED")
            {
                // Exercise the production discovery failure through the generated shell,
                // including its HTTP body, instead of asserting a canned diagnostic.
                var started = new DateTime(2026, 10, 27, 8, 0, 0, DateTimeKind.Utc);
                using var service = new Ai.Tlbx.MidTerm.Services.Sessions.SessionCliContextService(
                    () => [new("first001", 10, 11), new("second02", 20, 21)],
                    _ => (1, started), TimeProvider.System, _ => true);
                var resolution = await service.ResolveAsync(identity, identity, processId, fresh, (marker, _) =>
                {
                    service.ObserveOutput("first001", Encoding.UTF8.GetBytes(marker));
                    service.ObserveInput("first001", userInput: true);
                    return Task.FromResult(true);
                }, CancellationToken.None);
                return Results.Text(resolution, statusCode: 409);
            }
            return Results.Text(outcome == "bound" ? "abcdefgh:" + token
                : (outcome == "UNBOUND" ? "TLBXCTX:" + token + "\n" : "") +
                    $"TLBX_CONTEXT_{outcome}: No session operation was executed.\nReason: terminal-input-changed: session=second02; inputVersion=4->5.\nNext: Retry the original command once in a new shell tool call.",
                statusCode: outcome == "bound" ? 200 : 409);
        });
        await app.StartAsync(CancellationToken.None);
        var command = bash
            ? $"export CODEX_SESSION_ID={identity}; source \"$1\"; mt_context repair"
            : $"$env:CODEX_SESSION_ID='{identity}'; . $args[0]; mt_context repair";
        var helper = Path.Combine(_tempDir, bash ? "tlbx_cli.sh" : "tlbx_cli.ps1");
        var result = await RunContextShellAsync(bash, command, app.Urls.Single(), "deadbeef", helper);
        Assert.Equal(1, freshRequests);
        var diagnostic = result.Output + result.Error;
        TestContext.Current.TestOutputHelper?.WriteLine(diagnostic);
        Assert.DoesNotContain("Usage:", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("test-token", diagnostic, StringComparison.Ordinal);
        if (outcome == "bound")
        {
            Assert.True(result.ExitCode == 0, diagnostic);
            Assert.Contains("repair=verified", result.Output, StringComparison.Ordinal);
            Assert.Contains("sessionId=abcdefgh", result.Output, StringComparison.Ordinal);
            Assert.DoesNotContain(token, diagnostic, StringComparison.Ordinal);
        }
        else
        {
            Assert.NotEqual(0, result.ExitCode);
            Assert.Contains("TLBX_CONTEXT_" + outcome, result.Error, StringComparison.Ordinal);
            Assert.Contains(outcome == "CHANGED" ? "session=first001; inputVersion=1->2" : "session=second02; inputVersion=4->5", result.Error, StringComparison.Ordinal);
            if (outcome == "CHANGED") Assert.Contains("stage=automatic-discovery", result.Error, StringComparison.Ordinal);
            Assert.Contains("httpStatus=409;", result.Error, StringComparison.Ordinal);
            Assert.Contains("helper=", result.Error, StringComparison.Ordinal);
            Assert.Contains("Next:", result.Error, StringComparison.Ordinal);
            if (outcome == "UNBOUND") Assert.Contains("TLBXCTX:" + token, result.Error, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(false, 409)]
    [InlineData(true, 409)]
    [InlineData(false, 503)]
    [InlineData(true, 503)]
    public async Task ApiFailurePreservesServerReasonAndIdentifiesRequestWithoutReplayingMutation(bool bash, int status)
    {
        if ((bash ? ResolveBashPath() : ResolvePowerShellPath()) is null) return;
        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2100, "test-token");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/api/sessions/process-context", () => Results.Text("abcdefgh"));
        var mutations = 0;
        app.MapPut("/api/sessions/{id}/topic", () =>
        {
            Interlocked.Increment(ref mutations);
            return Results.Text("Reason: requested session is unavailable; original topic preserved.", statusCode: status);
        });
        await app.StartAsync(CancellationToken.None);
        var result = await RunContextShellAsync(bash,
            bash ? "source \"$1\"; mt_topic do-not-log-payload" : ". $args[0]; mt_topic do-not-log-payload",
            app.Urls.Single(), "abcdefgh", Path.Combine(_tempDir, bash ? "tlbx_cli.sh" : "tlbx_cli.ps1"));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(1, mutations);
        var diagnostic = result.Output + result.Error;
        Assert.Contains("Reason: requested session is unavailable; original topic preserved.", diagnostic, StringComparison.Ordinal);
        Assert.Contains("TLBX_API_FAILED", result.Error, StringComparison.Ordinal);
        Assert.Contains("endpoint=" + app.Urls.Single() + "/api/sessions/abcdefgh/topic", result.Error, StringComparison.Ordinal);
        Assert.Contains(status.ToString(System.Globalization.CultureInfo.InvariantCulture), diagnostic, StringComparison.Ordinal);
        Assert.Contains("No automatic retry", result.Error, StringComparison.Ordinal);
        Assert.DoesNotContain("do-not-log-payload", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("test-token", diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureRefreshesBothAgentGuidesAndPreservesUserArtifacts()
    {
        var directory = TlbxDirectory.Ensure(_tempDir);
        var guide = File.ReadAllText(Path.Combine(directory, "AGENTS.md"));
        Assert.StartsWith("<!-- tlbx-guidance:", guide, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(directory, "AGENTS.md"), "outdated instructions");
        File.WriteAllText(Path.Combine(directory, "CLAUDE.md"), "different outdated instructions");
        var upload = Path.Combine(directory, "uploads", "keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(upload)!);
        File.WriteAllText(upload, "user upload");
        TlbxDirectory.Ensure(_tempDir);
        Assert.Equal(guide, File.ReadAllText(Path.Combine(directory, "AGENTS.md")));
        Assert.Equal(guide, File.ReadAllText(Path.Combine(directory, "CLAUDE.md")));
        Assert.Equal("user upload", File.ReadAllText(upload));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CodexDoesNotUseLegacyPlainIdOrBypassFailureWithExplicitTarget(bool bash)
    {
        if ((bash ? ResolveBashPath() : ResolvePowerShellPath()) is null) return;
        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2100, "test-token");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var requests = new System.Collections.Concurrent.ConcurrentQueue<string>();
        app.MapGet("/api/sessions/process-context", () => Results.Text("deadbeef"));
        app.MapMethods("/api/git/{**path}", ["GET", "POST"], (HttpContext context) => { requests.Enqueue(context.Request.Path); return Results.Ok(); });
        app.MapGet("/api/sessions", () => Results.Text("global-list-ok"));
        await app.StartAsync(CancellationToken.None);
        var command = bash
            ? "set -e; export CODEX_SESSION_ID=01a10b30-4385-71e0-8a60-6fd26e1cfb01; source \"$1\"; mt_sessions; mt_repo --session sibling1 list"
            : "$env:CODEX_SESSION_ID='01a10b30-4385-71e0-8a60-6fd26e1cfb01'; . $args[0]; mt_sessions; mt_repo list -SessionId sibling1";
        var result = await RunContextShellAsync(bash, command, app.Urls.Single(), "deadbeef", Path.Combine(_tempDir, bash ? "tlbx_cli.sh" : "tlbx_cli.ps1"));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Empty(requests);
        Assert.Contains("global-list-ok", result.Output, StringComparison.Ordinal);
        Assert.Contains("TLBX_CONTEXT_UNAVAILABLE", result.Output + result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ApplyUpdate_AuthenticatesVersionPollAndRecoversAfterRestart(bool bash, bool apiKey)
    {
        if ((bash ? ResolveBashPath() : ResolvePowerShellPath()) is null) return;
        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2100, "test-token");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var polls = 0;
        var applies = 0;
        app.Use(async (context, next) =>
        {
            var authenticated = apiKey
                ? context.Request.Headers.Authorization == "Bearer test-key"
                : context.Request.Cookies["mm-session"] == "test-token";
            if (!authenticated) { context.Response.StatusCode = 401; return; }
            await next(context);
        });
        app.MapGet("/api/update/check", () => Results.Json(new { latestVersion = "10.17.31-dev" }));
        app.MapPost("/api/update/apply", (bool detached) =>
        {
            Assert.True(detached);
            Interlocked.Increment(ref applies);
            return Results.Ok();
        });
        app.MapGet("/api/version", () => Interlocked.Increment(ref polls) == 1
            ? Results.StatusCode(503) : Results.Text("10.17.31-dev"));
        await app.StartAsync(CancellationToken.None);
        var authentication = apiKey ? (bash ? "export MT_API_KEY=test-key; " : "$env:MT_API_KEY='test-key'; ") : "";
        var result = await RunContextShellAsync(bash,
            authentication + (bash ? "source \"$1\"; mt_apply_update" : ". $args[0]; Mt-ApplyUpdate"),
            app.Urls.Single(), "abcdefgh", Path.Combine(_tempDir, bash ? "tlbx_cli.sh" : "tlbx_cli.ps1"));
        Assert.True(result.ExitCode == 0, result.Error + result.Output);
        Assert.Contains("Current version: 10.17.31-dev", result.Output, StringComparison.Ordinal);
        Assert.Equal(1, applies);
        Assert.Equal(2, polls);
    }

    [Theory]
    [InlineData("normal")]
    [InlineData("important")]
    public async Task Notify_SerializesPriorityAndLiteralMessageBeforeSinglePost(string priority)
    {
        if (ResolvePowerShellPath() is null) return;
        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2100, "test-token");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var posts = 0;
        app.MapPost("/api/notifications", async (HttpContext context) =>
        {
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            Assert.Equal(priority, body.RootElement.GetProperty("priority").GetString());
            Assert.Equal("A \"quoted\" message\nwith umlaut ä", body.RootElement.GetProperty("body").GetString());
            Assert.Equal("abcdefgh", body.RootElement.GetProperty("sessionId").GetString());
            Interlocked.Increment(ref posts);
            return Results.Ok();
        });
        await app.StartAsync(CancellationToken.None);
        var result = await RunContextShellAsync(false,
            ". $args[0]; Mt-Notify -Message \"A `\"quoted`\" message`nwith umlaut ä\" -Priority " + priority,
            app.Urls.Single(), "abcdefgh", Path.Combine(_tempDir, "tlbx_cli.ps1"));
        Assert.True(result.ExitCode == 0, result.Error + result.Output);
        Assert.Equal(1, posts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenewedCookieSurvivesANewToolProcessButNeverChangesIdentityOrServer(bool bash)
    {
        if ((bash ? ResolveBashPath() : ResolvePowerShellPath()) is null) return;
        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2100, "seed-token");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var phase = 0;
        var seedRequests = 0;
        app.Use(async (context, next) =>
        {
            var cookie = context.Request.Cookies["mm-session"];
            if (cookie == "seed-token") Interlocked.Increment(ref seedRequests);
            if (phase == 2 || (cookie != "renewed-token" && (phase != 0 || cookie != "seed-token")))
            {
                AuthService.MarkAuthenticationRequired(context.Response);
                context.Response.StatusCode = 401;
                return;
            }
            context.Response.Cookies.Append("mm-session", "renewed-token", new CookieOptions { Path = "/", HttpOnly = true });
            await next(context);
        });
        app.MapGet("/api/sessions/process-context", () => Results.Text("abcdefgh"));
        app.MapGet("/api/sessions", () => Results.Text("authenticated"));
        await app.StartAsync(CancellationToken.None);
        var helper = Path.Combine(_tempDir, bash ? "tlbx_cli.sh" : "tlbx_cli.ps1");
        var command = bash ? "source \"$1\"; mt_sessions" : ". $args[0]; mt_sessions";
        var first = await RunContextShellAsync(bash, command, app.Urls.Single(), "abcdefgh", helper);
        Assert.True(first.ExitCode == 0, first.Error + first.Output);
        Assert.Equal(1, seedRequests);
        phase = 1; // The inherited seed has expired; only its legitimate renewal remains valid.
        var resumed = await RunContextShellAsync(bash, command, app.Urls.Single(), "abcdefgh", helper);
        Assert.True(resumed.ExitCode == 0, resumed.Error + resumed.Output);
        Assert.Contains("authenticated", resumed.Output, StringComparison.Ordinal);
        Assert.Equal(1, seedRequests);
        var anotherWorkspace = Path.Combine(_tempDir, "another-workspace");
        Directory.CreateDirectory(anotherWorkspace);
        TlbxCliScriptWriter.WriteScripts(anotherWorkspace, 2100, "seed-token");
        var moved = await RunContextShellAsync(bash, command, app.Urls.Single(), "abcdefgh",
            Path.Combine(anotherWorkspace, bash ? "tlbx_cli.sh" : "tlbx_cli.ps1"));
        Assert.True(moved.ExitCode == 0, moved.Error + moved.Output);
        Assert.Equal(1, seedRequests);
        var explicitToken = bash ? "export MT_TOKEN=wrong-identity; " : "$env:MT_TOKEN='wrong-identity'; ";
        var wrongIdentity = await RunContextShellAsync(bash, explicitToken + command, app.Urls.Single(), "abcdefgh", helper);
        Assert.NotEqual(0, wrongIdentity.ExitCode);
        Assert.DoesNotContain("renewed-token", wrongIdentity.Error + wrongIdentity.Output, StringComparison.Ordinal);

        var otherBuilder = WebApplication.CreateSlimBuilder();
        otherBuilder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var other = otherBuilder.Build();
        var foreignCookies = 0;
        other.MapGet("/{**path}", (HttpContext context) =>
        {
            if (context.Request.Cookies["mm-session"] != "seed-token") Interlocked.Increment(ref foreignCookies);
            return Results.Text("abcdefgh");
        });
        await other.StartAsync(CancellationToken.None);
        var otherResult = await RunContextShellAsync(bash, command, other.Urls.Single(), "abcdefgh", helper);
        Assert.True(otherResult.ExitCode == 0, otherResult.Error + otherResult.Output);
        Assert.Equal(0, foreignCookies);
        phase = 2; // Logout/revocation must remain a failure, even while a cookie is cached.
        var revoked = await RunContextShellAsync(bash, command, app.Urls.Single(), "abcdefgh", helper);
        Assert.NotEqual(0, revoked.ExitCode);
        Assert.Equal(1, seedRequests); // No fallback to the generated seed after a rejected renewal.
        Assert.DoesNotContain("renewed-token", revoked.Output + revoked.Error, StringComparison.Ordinal);
        phase = 0; // Even if a restarted server accepted old seeds again, this rejected identity stays rejected.
        var afterServerRestart = await RunContextShellAsync(bash, command, app.Urls.Single(), "abcdefgh", helper);
        Assert.NotEqual(0, afterServerRestart.ExitCode);
        Assert.Equal(1, seedRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LoadedHelperRefreshesCodeWithoutRetargetingTheOwningServerOrReplayingCommands(bool bash)
    {
        if ((bash ? ResolveBashPath() : ResolvePowerShellPath()) is null) return;
        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2100, "test-token");
        var helper = Path.Combine(_tempDir, bash ? "tlbx_cli.sh" : "tlbx_cli.ps1");
        var original = File.ReadAllText(helper);
        var oldProbe = bash ? "\nmt_recovery_probe() { printf old; }\n" : "\nfunction script:Mt-RecoveryProbe { 'old' }\n";
        var newProbe = bash ? "\nmt_recovery_probe() { printf renewed; }\n" : "\nfunction script:Mt-RecoveryProbe { 'renewed' }\n";
        File.WriteAllText(helper, original + oldProbe);
        var replacement = helper + ".next";
        File.WriteAllText(replacement, original.Replace(Ai.Tlbx.MidTerm.Startup.CliCommands.GetVersion(), "99.0.0-dev", StringComparison.Ordinal) + newProbe);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var requests = 0;
        app.MapGet("/api/sessions/process-context", () => Results.Text("abcdefgh"));
        app.MapGet("/api/sessions", () => { Interlocked.Increment(ref requests); return Results.Text("owner"); });
        await app.StartAsync(CancellationToken.None);
        var command = bash
            ? "source \"$1\"; mt_recovery_probe; mv -f \"$2\" \"$1\"; mt_sessions; mt_recovery_probe"
            : ". $args[0]; Mt-RecoveryProbe; [IO.File]::Move($args[1],$args[0],$true); mt_sessions; Mt-RecoveryProbe";
        var result = await RunContextShellAsync(bash, command, app.Urls.Single(), "abcdefgh", helper, replacement);
        Assert.True(result.ExitCode == 0, result.Error + result.Output);
        Assert.Contains("old", result.Output, StringComparison.Ordinal);
        Assert.Contains("renewed", result.Output, StringComparison.Ordinal);
        Assert.Equal(1, requests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealSessionCookieRenewalKeepsTheCliAliveBeyondTheOriginalEightDays(bool bash)
    {
        if (!OperatingSystem.IsWindows() || (bash ? ResolveBashPath() : ResolvePowerShellPath()) is null) return;
        Directory.CreateDirectory(_tempDir);
        var settings = new Ai.Tlbx.MidTerm.Settings.SettingsService(Path.Combine(_tempDir, "settings"));
        var configured = settings.Load();
        configured.PasswordHash = "configured";
        settings.Save(configured);
        var clock = new Microsoft.Extensions.Time.Testing.FakeTimeProvider(DateTimeOffset.UtcNow);
        var keys = new Ai.Tlbx.MidTerm.Services.Security.ApiKeyService(settings, clock);
        var auth = new AuthService(settings, keys, clock);
        var original = auth.CreateSessionToken();
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2100, original);
        clock.Advance(TimeSpan.FromDays(7));
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        string? renewed = null;
        app.Use(async (context, next) =>
        {
            var authentication = auth.AuthenticateRequestWithContext(context.Request);
            if (authentication.Method == RequestAuthMethod.None) { AuthService.MarkAuthenticationRequired(context.Response); context.Response.StatusCode = 401; return; }
            renewed = auth.RenewSessionToken(authentication.SessionTokenId!);
            context.Response.Cookies.Append("mm-session", renewed, new CookieOptions { Path = "/", HttpOnly = true });
            await next(context);
        });
        app.MapGet("/api/sessions/process-context", () => Results.Text("abcdefgh"));
        app.MapGet("/api/sessions", () => Results.Text("authenticated"));
        await app.StartAsync(CancellationToken.None);
        var helper = Path.Combine(_tempDir, bash ? "tlbx_cli.sh" : "tlbx_cli.ps1");
        var command = bash ? "source \"$1\"; mt_sessions" : ". $args[0]; mt_sessions";
        var first = await RunContextShellAsync(bash, command, app.Urls.Single(), "abcdefgh", helper);
        Assert.True(first.ExitCode == 0, first.Error + first.Output);
        clock.Advance(TimeSpan.FromDays(2));
        Assert.False(auth.ValidateSessionToken(original));
        var afterExpiry = await RunContextShellAsync(bash, command, app.Urls.Single(), "abcdefgh", helper);
        Assert.True(afterExpiry.ExitCode == 0, afterExpiry.Error + afterExpiry.Output);
        auth.RevokeSessionToken(renewed);
        var afterLogout = await RunContextShellAsync(bash, command, app.Urls.Single(), "abcdefgh", helper);
        Assert.NotEqual(0, afterLogout.ExitCode);
    }

    [Fact]
    public async Task ConcurrentHelperReadersOnlySeeCompletePublishedScripts()
    {
        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2100, "first-token");
        var path = Path.Combine(_tempDir, "tlbx_cli.ps1");
        var first = File.ReadAllText(path);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2100, "second-token");
        var second = File.ReadAllText(path);
        var reader = Task.Run(() =>
        {
            for (var index = 0; index < 80; index++)
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var text = new StreamReader(stream);
                var observed = text.ReadToEnd();
                Assert.True(observed == first || observed == second, "A reader observed an incomplete helper during publication.");
            }
        }, TestContext.Current.CancellationToken);
        for (var index = 0; index < 30; index++)
            TlbxCliScriptWriter.WriteScripts(_tempDir, 2100, index % 2 == 0 ? "first-token" : "second-token");
        await reader;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedHttpResponseCannotEstablishAContextEvenIfItsBodyLooksLikeALease(bool bash)
    {
        if ((bash ? ResolveBashPath() : ResolvePowerShellPath()) is null) return;
        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2100, "test-token");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/api/sessions/process-context", () => Results.Text("abcdefgh:0123456789abcdef0123456789abcdef", statusCode: 409));
        var mutations = 0;
        app.MapPut("/api/sessions/{id}/topic", () => { mutations++; return Results.Ok(); });
        await app.StartAsync(CancellationToken.None);
        var command = bash
            ? "export CODEX_SESSION_ID=01a10b30-4385-71e0-8a60-6fd26e1cfb01; source \"$1\"; mt_topic blocked"
            : "$env:CODEX_SESSION_ID='01a10b30-4385-71e0-8a60-6fd26e1cfb01'; . $args[0]; mt_topic blocked";
        var result = await RunContextShellAsync(bash, command, app.Urls.Single(), "deadbeef", Path.Combine(_tempDir, bash ? "tlbx_cli.sh" : "tlbx_cli.ps1"));
        Assert.NotEqual(0, result.ExitCode);
        Assert.Equal(0, mutations);
        Assert.Contains("httpStatus=409;", result.Output + result.Error, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task IdentityPreflightWaitsThroughAWebRestartBeforeExecutingTheOriginalMutation(bool bash, bool resetConnection)
    {
        if ((bash ? ResolveBashPath() : ResolvePowerShellPath()) is null) return;
        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2100, "test-token");
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        var reads = 0;
        app.MapGet("/api/sessions/process-context", (HttpContext context) =>
        {
            if (Interlocked.Increment(ref reads) == 1)
            {
                if (resetConnection) { context.Abort(); return Results.Empty; }
                return Results.Text("source web is starting", statusCode: 503);
            }
            return Results.Text("abcdefgh:0123456789abcdef0123456789abcdef");
        });
        var mutations = 0;
        app.MapPut("/api/sessions/{id}/topic", () => { mutations++; return Results.Ok(); });
        await app.StartAsync(CancellationToken.None);
        var command = bash
            ? "export CODEX_SESSION_ID=01a10b30-4385-71e0-8a60-6fd26e1cfb01; source \"$1\"; mt_topic healed"
            : "$env:CODEX_SESSION_ID='01a10b30-4385-71e0-8a60-6fd26e1cfb01'; . $args[0]; mt_topic healed";
        var result = await RunContextShellAsync(bash, command, app.Urls.Single(), "deadbeef", Path.Combine(_tempDir, bash ? "tlbx_cli.sh" : "tlbx_cli.ps1"));
        Assert.True(result.ExitCode == 0, result.Error + result.Output);
        Assert.True(reads >= 2);
        Assert.Equal(1, mutations);
        Assert.DoesNotContain("source web is starting", result.Output + result.Error, StringComparison.Ordinal);
    }

    private async Task<(int ExitCode, string Output, string Error)> RunContextShellAsync(
        bool bash, string command, string baseUrl, string sessionId, params string[] arguments)
    {
        var start = new ProcessStartInfo(bash ? ResolveBashPath()! : ResolvePowerShellPath()!)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("MT_", StringComparison.Ordinal)).ToArray())
            start.Environment.Remove(key);
        start.Environment.Remove("CODEX_SESSION_ID");
        start.Environment.Remove("CODEX_THREAD_ID");
        start.Environment.Remove("CODEX_HOME");
        start.Environment["MT_BASE_URL"] = baseUrl;
        start.Environment["MT_SESSION_ID"] = sessionId;
        start.Environment["TLBX_CLI_AUTH_CACHE_DIR"] = Path.Combine(_tempDir, ".auth");
        if (bash)
        {
            start.ArgumentList.Add("-c");
            start.ArgumentList.Add(command);
            start.ArgumentList.Add("context-test");
        }
        else
        {
            var driver = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + ".ps1");
            await File.WriteAllTextAsync(driver, "$ErrorActionPreference = 'Stop'\n" + command, Xunit.TestContext.Current.CancellationToken);
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(driver);
        }
        foreach (var argument in arguments)
            start.ArgumentList.Add(bash && File.Exists(argument) ? ToBashPath(argument) : argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Xunit.TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(Xunit.TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            return (process.ExitCode, await stdout, await stderr);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SourceInstance_BoundAddressRemainsReachableWithoutBaseUrlOverride(bool bash)
    {
        if ((bash ? ResolveBashPath() : ResolvePowerShellPath()) is null) return;
        Directory.CreateDirectory(_tempDir);
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.MapGet("/api/version", () => Results.Text("bound-source"));
        await app.StartAsync(CancellationToken.None);
        var port = new Uri(app.Urls.Single()).Port;
        TlbxDirectory.WriteInstanceCliScripts(_tempDir, port, "test-token", true, "127.0.0.1");
        var helper = Path.Combine(_tempDir, "instances", port.ToString(System.Globalization.CultureInfo.InvariantCulture), bash ? "tlbx_cli.sh" : "tlbx_cli.ps1");
        // Use the generated HTTPS authority, but make the fixture's scheme HTTP.
        // No caller URL override may supply the bound host.
        var command = bash
            ? "unset MT_BASE_URL; source \"$1\"; _MT=${_MT/https:/http:}; _MC \"$_MT/api/version\""
            : "Remove-Item Env:MT_BASE_URL; . $args[0]; $script:_MT=$script:_MT.Replace('https:','http:'); _MC \"$script:_MT/api/version\"";
        var result = await RunContextShellAsync(bash, command, "ignored", "", helper);
        Assert.True(result.ExitCode == 0, result.Error + result.Output);
        Assert.Equal("bound-source", result.Output.Trim());
    }

    [Theory]
    [InlineData("0.0.0.0", "localhost")]
    [InlineData("::", "localhost")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("100.91.240.65", "100.91.240.65")]
    [InlineData("::1", "[::1]")]
    public void CliHost_UsesSpecificBindingOrLoopbackForWildcard(string binding, string expected)
    {
        Assert.Equal(expected, TlbxDirectory.ResolveCliHost(binding));
    }

    [Fact]
    public void SourceInstance_PreservesSupervisorHelpersAndKeepsItsOwnCredentials()
    {
        Directory.CreateDirectory(_tempDir);
        TlbxDirectory.WriteInstanceCliScripts(_tempDir, 2000, "supervisor-token", false);
        var names = new[] { "tlbx_cli.ps1", "tlbx_cli.sh", "tlbx_graphs.ps1", "tlbx_graphs.sh" };
        var originals = names.ToDictionary(name => name, name => File.ReadAllText(Path.Combine(_tempDir, name)), StringComparer.Ordinal);
        TlbxDirectory.WriteInstanceCliScripts(_tempDir, 2100, "source-token", true);
        TlbxDirectory.WriteInstanceCliScripts(_tempDir, 2100, "refreshed-source-token", true);
        foreach (var name in names)
        {
            Assert.Equal(originals[name], File.ReadAllText(Path.Combine(_tempDir, name)));
            var source = File.ReadAllText(Path.Combine(_tempDir, "instances", "2100", name));
            Assert.Contains("2100", source, StringComparison.Ordinal);
            Assert.Contains("refreshed-source-token", source, StringComparison.Ordinal);
            Assert.DoesNotContain("supervisor-token", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WriteScripts_BashBootstrapProducesValidJsonWithSlashCommands()
    {
        var bashPath = ResolveBashPath();
        if (bashPath is null)
        {
            return;
        }

        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2000, "test-token");

        var scriptPath = ToBashPath(Path.Combine(_tempDir, "tlbx_cli.sh"));
        var startInfo = new ProcessStartInfo(bashPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(
            "source \"$1\"; _MJ() { printf '%s' \"$2\"; }; " +
            "mt_bootstrap 'review \"worker\"' 'Q:/repo with space' codex status compact");
        startInfo.ArgumentList.Add("midterm-test");
        startInfo.ArgumentList.Add(scriptPath);

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, error);
        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        Assert.Equal("review \"worker\"", root.GetProperty("name").GetString());
        Assert.Equal("Q:/repo with space", root.GetProperty("workingDirectory").GetString());
        Assert.Equal("codex", root.GetProperty("profile").GetString());
        var slashCommands = root.GetProperty("slashCommands");
        Assert.Equal(2, slashCommands.GetArrayLength());
        Assert.Equal("status", slashCommands[0].GetString());
        Assert.Equal("compact", slashCommands[1].GetString());
    }

    [Fact]
    public void WriteScripts_BashRunIsolatedKeepsChildBytesOutOfCallerAndPreservesArgv()
    {
        var bashPath = ResolveBashPath();
        if (bashPath is null)
        {
            return;
        }

        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2000, "test-token");
        var childPath = Path.Combine(_tempDir, "isolated-child.sh");
        File.WriteAllText(childPath,
            "sleep 0.15\n" +
            "printf 'stdout-sentinel\\n'\n" +
            "printf 'arg1=<%s>\\n' \"$1\"\n" +
            "printf 'arg2=<%s>\\n' \"$2\"\n" +
            "printf 'arg3=<%s>\\n' \"$3\"\n" +
            "if IFS= read -r line; then printf 'stdin=<%s>\\n' \"$line\"; else printf 'stdin=<eof>\\n'; fi\n" +
            "printf 'stderr-sentinel\\n' >&2\n");

        var startInfo = new ProcessStartInfo(bashPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("source \"$1\"; mt_run_isolated \"$2\" \"$3\" \"$4\" \"$5\" \"$6\"");
        startInfo.ArgumentList.Add("midterm-test");
        startInfo.ArgumentList.Add(ToBashPath(Path.Combine(_tempDir, "tlbx_cli.sh")));
        startInfo.ArgumentList.Add(ToBashPath(bashPath));
        startInfo.ArgumentList.Add(ToBashPath(childPath));
        startInfo.ArgumentList.Add("two words");
        startInfo.ArgumentList.Add("quote\"value");
        startInfo.ArgumentList.Add("slash and space\\");

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, error);
        Assert.DoesNotContain("stdout-sentinel", output, StringComparison.Ordinal);
        Assert.DoesNotContain("stderr-sentinel", error, StringComparison.Ordinal);
        using var receipt = JsonDocument.Parse(output);
        var root = receipt.RootElement;
        Assert.True(root.GetProperty("pid").GetInt32() > 0);
        var runId = root.GetProperty("runId").GetString()!;
        var stdoutPath = Path.Combine(_tempDir, "runs", runId, "stdout.log");
        var stderrPath = Path.Combine(_tempDir, "runs", runId, "stderr.log");

        var childOutput = WaitForFileContent(stdoutPath, content => content.Contains("stdin=<eof>", StringComparison.Ordinal));
        var childError = WaitForFileContent(stderrPath, content => content.Contains("stderr-sentinel", StringComparison.Ordinal));
        Assert.Contains("stdout-sentinel", childOutput, StringComparison.Ordinal);
        Assert.Contains("arg1=<two words>", childOutput, StringComparison.Ordinal);
        Assert.Contains("arg2=<quote\"value>", childOutput, StringComparison.Ordinal);
        Assert.Contains("arg3=<slash and space\\>", childOutput, StringComparison.Ordinal);
        Assert.Equal("stderr-sentinel", childError.Trim());
        Assert.EndsWith($"/runs/{runId}/stdout.log", root.GetProperty("stdoutPath").GetString()!.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.EndsWith($"/runs/{runId}/stderr.log", root.GetProperty("stderrPath").GetString()!.Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact]
    public void WriteScripts_PowerShellRunIsolatedKeepsChildBytesOutOfCallerAndPreservesArgv()
    {
        var powershellPath = ResolvePowerShellPath();
        if (powershellPath is null)
        {
            return;
        }

        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2000, "test-token");
        var childPath = Path.Combine(_tempDir, "isolated-child.ps1");
        File.WriteAllText(childPath,
            "param([Parameter(ValueFromRemainingArguments=$true)][string[]]$InputArgs)\n" +
            "Start-Sleep -Milliseconds 150\n" +
            "[Console]::Out.WriteLine('stdout-sentinel')\n" +
            "$InputArgs | ForEach-Object -Begin { $index = 0 } -Process { [Console]::Out.WriteLine(('arg{0}=<{1}>' -f (++$index), $_)) }\n" +
            "[Console]::Out.WriteLine(('stdin-length={0}' -f ([Console]::In.ReadToEnd().Length)))\n" +
            "[Console]::Error.WriteLine('stderr-sentinel')\n");

        var startInfo = new ProcessStartInfo(powershellPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(_tempDir, "tlbx_cli.ps1"));
        startInfo.ArgumentList.Add("mt_run_isolated");
        startInfo.ArgumentList.Add(powershellPath);
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(childPath);
        startInfo.ArgumentList.Add("two words");
        startInfo.ArgumentList.Add("quote\"value");
        startInfo.ArgumentList.Add("slash and space\\");

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, error);
        Assert.DoesNotContain("stdout-sentinel", output, StringComparison.Ordinal);
        Assert.DoesNotContain("stderr-sentinel", error, StringComparison.Ordinal);
        using var receipt = JsonDocument.Parse(output);
        var root = receipt.RootElement;
        Assert.True(root.GetProperty("pid").GetInt32() > 0);
        var runId = root.GetProperty("runId").GetString()!;
        var stdoutPath = root.GetProperty("stdoutPath").GetString()!;
        var stderrPath = root.GetProperty("stderrPath").GetString()!;
        Assert.Equal(Path.Combine(_tempDir, "runs", runId, "stdout.log"), stdoutPath);
        Assert.Equal(Path.Combine(_tempDir, "runs", runId, "stderr.log"), stderrPath);

        var childOutput = WaitForFileContent(stdoutPath, content => content.Contains("stdin-length=0", StringComparison.Ordinal));
        var childError = WaitForFileContent(stderrPath, content => content.Contains("stderr-sentinel", StringComparison.Ordinal));
        Assert.Contains("stdout-sentinel", childOutput, StringComparison.Ordinal);
        Assert.Contains("arg1=<two words>", childOutput, StringComparison.Ordinal);
        Assert.Contains("arg2=<quote\"value>", childOutput, StringComparison.Ordinal);
        Assert.Contains("arg3=<slash and space\\>", childOutput, StringComparison.Ordinal);
        Assert.Equal("stderr-sentinel", childError.Trim());
    }

    [Fact]
    public void WriteScripts_PowerShellRunIsolated_RemovesExpiredCompletedRuns()
    {
        var powershellPath = ResolvePowerShellPath();
        if (powershellPath is null)
        {
            return;
        }

        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2000, "test-token");
        var expiredRun = Path.Combine(_tempDir, "runs", "expired-run");
        Directory.CreateDirectory(expiredRun);
        Directory.SetLastWriteTimeUtc(expiredRun, DateTime.UtcNow.AddDays(-15));

        var startInfo = new ProcessStartInfo(powershellPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(_tempDir, "tlbx_cli.ps1"));
        startInfo.ArgumentList.Add("mt_run_isolated");
        startInfo.ArgumentList.Add(powershellPath);
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("exit 0");

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, error + output);
        Assert.False(Directory.Exists(expiredRun));

        var shell = File.ReadAllText(Path.Combine(_tempDir, "tlbx_cli.sh"));
        var powershell = File.ReadAllText(Path.Combine(_tempDir, "tlbx_cli.ps1"));
        Assert.Contains("max_completed_runs=100", shell, StringComparison.Ordinal);
        Assert.Contains("max_completed_kib=1048576", shell, StringComparison.Ordinal);
        Assert.Contains("$maxCompletedRuns = 100", powershell, StringComparison.Ordinal);
        Assert.Contains("$maxCompletedBytes = 1GB", powershell, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static string? ResolveBashPath()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? [@"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files\Git\usr\bin\bash.exe"]
            : ["/bin/bash", "/usr/bin/bash"];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ResolvePowerShellPath()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? [@"C:\Program Files\PowerShell\7\pwsh.exe"]
            : ["/usr/bin/pwsh", "/usr/local/bin/pwsh"];

        var fixedPath = candidates.FirstOrDefault(File.Exists);
        if (fixedPath is not null)
        {
            return fixedPath;
        }

        var executableName = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.Combine(path, executableName))
            .FirstOrDefault(File.Exists);
    }

    private static string WaitForFileContent(string path, Func<string, bool> completed)
    {
        var timeout = Stopwatch.StartNew();
        string content = string.Empty;
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (File.Exists(path))
            {
                try
                {
                    content = File.ReadAllText(path);
                    if (completed(content))
                    {
                        return content;
                    }
                }
                catch (IOException)
                {
                }
            }

            Thread.Sleep(25);
        }

        Assert.Fail($"Timed out waiting for isolated run artifact '{path}'. Last content: {content}");
        return content;
    }

    private static string ToBashPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return path;
        }

        var normalized = path.Replace('\\', '/');
        return normalized.Length >= 3 && normalized[1] == ':'
            ? $"/{char.ToLowerInvariant(normalized[0])}/{normalized[3..]}"
            : normalized;
    }
}

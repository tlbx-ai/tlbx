using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Browser;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

[Collection(PathSensitiveEnvironmentCollection.Name)]
public sealed partial class TlbxCliScriptWriterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "tlbx-cli-tests", Guid.NewGuid().ToString("N"));

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
        var fallback = File.ReadAllText(Path.Combine(directory, "CLI-FALLBACK.md"));
        Assert.StartsWith("<!-- tlbx-guidance:", guide, StringComparison.Ordinal);
        Assert.Contains("[CLI-FALLBACK.md](CLI-FALLBACK.md)", guide, StringComparison.Ordinal);
        Assert.DoesNotContain("## Tmux Split Panes", guide, StringComparison.Ordinal);
        Assert.Contains("## Tmux Split Panes", fallback, StringComparison.Ordinal);
        File.WriteAllText(Path.Combine(directory, "AGENTS.md"), "outdated instructions");
        File.WriteAllText(Path.Combine(directory, "CLAUDE.md"), "different outdated instructions");
        File.WriteAllText(Path.Combine(directory, "CLI-FALLBACK.md"), "outdated fallback reference");
        var upload = Path.Combine(directory, "uploads", "keep.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(upload)!);
        File.WriteAllText(upload, "user upload");
        TlbxDirectory.Ensure(_tempDir);
        Assert.Equal(guide, File.ReadAllText(Path.Combine(directory, "AGENTS.md")));
        Assert.Equal(guide, File.ReadAllText(Path.Combine(directory, "CLAUDE.md")));
        Assert.Equal(fallback, File.ReadAllText(Path.Combine(directory, "CLI-FALLBACK.md")));
        Assert.Equal("user upload", File.ReadAllText(upload));
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

    private Task<(int ExitCode, string Output, string Error)> RunContextShellAsync(
        bool bash, string command, string baseUrl, string sessionId, params string[] arguments)
        => RunContextShellAsync(bash, command, baseUrl, sessionId, TimeSpan.FromSeconds(30), arguments);

    private async Task<(int ExitCode, string Output, string Error)> RunContextShellAsync(
        bool bash, string command, string baseUrl, string sessionId, TimeSpan timeoutBudget, params string[] arguments)
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
        var driver = Path.Combine(_tempDir, Guid.NewGuid().ToString("N") + (bash ? ".sh" : ".ps1"));
        await File.WriteAllTextAsync(driver, (bash ? "" : "$ErrorActionPreference = 'Stop'\n") + command,
            new UTF8Encoding(false), Xunit.TestContext.Current.CancellationToken);
        if (bash)
        {
            // Git Bash's Windows argv conversion can replace non-ANSI text
            // before the generated helper even runs. Feed exact UTF-8 source.
            start.ArgumentList.Add(ToBashPath(driver));
        }
        else
        {
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-File");
            start.ArgumentList.Add(driver);
        }
        foreach (var argument in arguments)
            start.ArgumentList.Add(bash && File.Exists(argument) ? ToBashPath(argument) : argument);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEndAsync(Xunit.TestContext.Current.CancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(Xunit.TestContext.Current.CancellationToken);
        using var timeout = new CancellationTokenSource(timeoutBudget);
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

    private static string ToBashPath(string path)
    {
        if (!OperatingSystem.IsWindows()) return path;
        var normalized = path.Replace('\\', '/');
        return normalized.Length >= 3 && normalized[1] == ':'
            ? $"/{char.ToLowerInvariant(normalized[0])}/{normalized[3..]}"
            : normalized;
    }
}

using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ai.Tlbx.MidTerm.Services.Mcp;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.AspNetCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TlbxMcpNativeProcessLifetimeTests
{
    [Fact]
    public async Task RealNativeAdapterSavesCompleteArtifactAndExitsBeforeDetachedWorkerAndChildComplete()
    {
        // getcwd resolves ancestor symlinks (notably macOS /var -> /private/var).
        // Start from that same physical directory so exact cwd/state-path checks
        // still verify the adapter's default directory rather than a lexical alias.
        var cwd = Path.Combine(PhysicalDirectory(Path.GetTempPath()), "tlbx-native-lifetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        var gate = Path.Combine(cwd, "release-child");
        var script = Path.Combine(cwd, OperatingSystem.IsWindows() ? "child.ps1" : "child.sh");
        File.WriteAllText(script, OperatingSystem.IsWindows() ? """
            [Console]::WriteLine('child-started')
            [IO.File]::WriteAllLines('context-hints.txt', @($env:MT_BASE_URL,$env:MT_SESSION_ID,$env:MT_PREVIEW_NAME))
            $deadline = [DateTime]::UtcNow.AddSeconds(25)
            while (!(Test-Path -LiteralPath $args[0])) {
                if ([DateTime]::UtcNow -gt $deadline) { exit 23 }
                Start-Sleep -Milliseconds 50
            }
            [Console]::Write('after-native-adapter-exit')
            [Console]::Error.Write('separate-child-stderr')
            """ : """
            printf 'child-started\n'
            printf '%s\n%s\n%s\n' "$MT_BASE_URL" "$MT_SESSION_ID" "$MT_PREVIEW_NAME" > context-hints.txt
            count=0
            while [ ! -f "$1" ]; do
                count=$((count + 1)); [ "$count" -lt 250 ] || exit 23
                sleep 0.1
            done
            printf after-native-adapter-exit
            printf separate-child-stderr >&2
            """, new UTF8Encoding(OperatingSystem.IsWindows()));

        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=localhost", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, critical: true));
        var usages = new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") };
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(usages, critical: true));
        using var generated = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-1), DateTimeOffset.UtcNow.AddHours(1));
        // Schannel requires a persisted CNG key handle for server credentials;
        // the temporary imported key is removed when this certificate is disposed.
        using var certificate = X509CertificateLoader.LoadPkcs12(generated.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.Exportable);
        var apiKey = "isolated-fixture-" + Guid.NewGuid().ToString("N");
        var authenticatedRequests = 0;
        var forwardedCalls = 0;
        var artifactReads = 0;
        var capturedCalls = 0;
        var contextReads = 0;
        var authorityAddress = "";
        var largeOutput = string.Concat(Enumerable.Repeat("Grüße 日本 🦊 '$()'\r\n", 30000));
        const string artifactUri = "tlbx-artifact://capture/native-executable-fixture";
        var artifactBytes = new byte[TlbxMcpArtifactSaveTool.ChunkBytes + 17];
        RandomNumberGenerator.Fill(artifactBytes);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(certificate)));
        builder.Services.AddMcpServer().WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
                { Tools = [.. TlbxMcpArtifactTools.CreateTools().Select(tool => tool.ProtocolTool),
                    .. TlbxMcpTool.CreateTools().Where(tool => tool.ProtocolTool.Name == "context_export").Select(tool => tool.ProtocolTool),
                    new TlbxMcpWorkflowTool([]).ProtocolTool, new Tool
                {
                    Name = "large_result",
                    InputSchema = JsonSerializer.SerializeToElement(new JsonObject { ["type"] = "object" }, McpJsonContext.Default.JsonObject)
                }] }))
            .WithCallToolHandler((call, _) =>
            {
                if (call.Params!.Name == "context_export")
                {
                    Interlocked.Increment(ref contextReads);
                    return ValueTask.FromResult(McpApiResult.Structured(new JsonObject
                    {
                        ["baseUrl"] = authorityAddress, ["sessionId"] = null,
                        ["previewName"] = call.Params.Arguments!["previewName"].GetString()
                    }));
                }
                if (call.Params!.Name == "large_result")
                {
                    Interlocked.Increment(ref capturedCalls);
                    return ValueTask.FromResult(McpApiResult.Structured(new JsonObject { ["value"] = largeOutput }));
                }
                if (call.Params!.Name == "artifacts_list") return ValueTask.FromResult(McpApiResult.Structured(new JsonObject
                {
                    ["resources"] = new JsonArray(new JsonObject { ["uri"] = artifactUri, ["size"] = artifactBytes.Length })
                }));
                if (call.Params.Name == "artifact_read")
                {
                    var args = call.Params.Arguments!;
                    Assert.Equal(artifactUri, args["uri"].GetString());
                    var offset = args["offset"].GetInt32();
                    var count = args["length"].GetInt32();
                    Interlocked.Increment(ref artifactReads);
                    return ValueTask.FromResult(new CallToolResult { Content = [new EmbeddedResourceBlock
                    {
                        Resource = BlobResourceContents.FromBytes(artifactBytes.AsMemory(offset, count),
                            FormattableString.Invariant($"{artifactUri}?offset={offset}&length={count}"), "application/octet-stream")
                    }] });
                }
                Interlocked.Increment(ref forwardedCalls);
                return ValueTask.FromResult(new CallToolResult { IsError = true, Content = [] });
            });
        await using var app = builder.Build();
        app.Use(async (http, next) =>
        {
            if (http.Request.Headers.Authorization != "Bearer " + apiKey ||
                http.Request.Headers[McpStdioContextHandler.OperatorHeader] != "explicit" ||
                http.Request.Headers.ContainsKey(TlbxMcpClientContextService.RuntimeHeader))
            {
                http.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }
            Interlocked.Increment(ref authenticatedRequests);
            await next(http);
        });
        app.MapMcp(TlbxMcpEndpoints.Path);
        using var bounded = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        // Covers two real adapter startups, artifact transfer and detached-worker
        // completion. Keep the whole scenario bounded without a cold-runner race.
        bounded.CancelAfter(TimeSpan.FromMinutes(2));
        var ct = bounded.Token;
        IsolatedRunState? state = null;
        McpClient? client = null;
        var verified = false;
        var stderrPath = Path.Combine(cwd, "native-adapter-stderr.log");
        var stderrLock = new object();
        await app.StartAsync(ct);
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            authorityAddress = address;
            var environment = StdioClientTransportOptions.GetDefaultEnvironmentVariables();
            environment["MT_API_KEY"] = apiKey;
            environment["TLBX_CLI_AUTH_CACHE_DIR"] = Path.Combine(cwd, "auth-cache");
            var nativeBinary = Environment.GetEnvironmentVariable("TLBX_MCP_TEST_NATIVE_BINARY");
            if (nativeBinary is not null)
            {
                Assert.True(Path.IsPathFullyQualified(nativeBinary), "TLBX_MCP_TEST_NATIVE_BINARY must be an absolute path.");
                Assert.True(File.Exists(nativeBinary), "TLBX_MCP_TEST_NATIVE_BINARY must identify an existing executable.");
            }
            string[] adapterArguments = ["mcp", "--stdio", "--operator", "--url", address,
                "--certificate-sha256", certificate.GetCertHashString(HashAlgorithmName.SHA256)];
            // Do not inherit owner runtime credentials, provider homes, or proxy settings.
            var transportOptions = new StdioClientTransportOptions
            {
                Command = nativeBinary ?? "dotnet",
                Arguments = nativeBinary is null ? [Path.Combine(AppContext.BaseDirectory, "mt.dll"), .. adapterArguments] : adapterArguments,
                WorkingDirectory = cwd,
                InheritEnvironmentVariables = false,
                EnvironmentVariables = environment,
                ShutdownTimeout = TimeSpan.FromSeconds(5),
                StandardErrorLines = line => { lock (stderrLock) File.AppendAllText(stderrPath, line + Environment.NewLine); }
            };
            var transport = new StdioClientTransport(transportOptions);
            client = await McpClient.CreateAsync(transport, cancellationToken: ct);
            Assert.Contains(await client.ListToolsAsync(cancellationToken: ct), tool => tool.Name == "process_run_isolated");
            var savedPath = Path.Combine(cwd, "native-original.bin");
            var saved = await client.CallToolAsync("artifact_save", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["uri"] = artifactUri, ["path"] = savedPath
            }, cancellationToken: ct);
            Assert.False(saved.IsError, saved.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
            Assert.Equal(artifactBytes, await File.ReadAllBytesAsync(savedPath, ct));
            Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(artifactBytes)), saved.StructuredContent!.Value.GetProperty("sha256").GetString());
            Assert.Equal(2, artifactReads);
            var outputPath = Path.Combine(cwd, "complete-output.json");
            var captured = await client.CallToolAsync("tool_call_to_file", new Dictionary<string, object?>(StringComparer.Ordinal)
                { ["toolName"] = "large_result", ["path"] = outputPath }, cancellationToken: ct);
            Assert.False(captured.IsError, captured.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
            using (var output = JsonDocument.Parse(await File.ReadAllBytesAsync(outputPath, ct)))
                Assert.Equal(largeOutput, output.RootElement.GetProperty("structuredContent").GetProperty("value").GetString());
            Assert.Equal(1, capturedCalls);
            Assert.True(captured.Content.OfType<TextContentBlock>().Single().Text.Length < 2048);
            var executable = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
                : "/bin/sh";
            string[] argv = OperatingSystem.IsWindows()
                ? ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, gate] : [script, gate];
            var result = await client.CallToolAsync("workflow_run", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["targetSessionId"] = "target01", ["previewName"] = "native-callback",
                ["steps"] = new object[] { new { tool = "process_run_isolated", arguments = new { executable, arguments = argv } } }
            }, cancellationToken: ct);
            Assert.False(result.IsError, result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
            var outcome = result.StructuredContent!.Value.GetProperty("steps")[0].GetProperty("structuredContent");
            Assert.Equal("native-mcp-caller", outcome.GetProperty("executionHost").GetString());
            Assert.Equal(cwd, outcome.GetProperty("workingDirectory").GetString());
            var statePath = outcome.GetProperty("statePath").GetString()!;
            Assert.StartsWith(Path.Combine(cwd, ".tlbx", "runs") + Path.DirectorySeparatorChar, statePath, StringComparison.Ordinal);
            state = TlbxMcpIsolatedProcessTool.ReadState(Path.GetDirectoryName(statePath)!)!;
            Assert.Equal(outcome.GetProperty("pid").GetInt32(), state.TargetPid);
            Assert.Equal(outcome.GetProperty("processStartedUtcTicks").GetInt64(), state.TargetStartedTicks);
            Assert.Null(state.CompletedAt);
            AssertAlive(state.WorkerPid, state.WorkerStartedTicks);
            AssertAlive(state.TargetPid, state.TargetStartedTicks);

            var status = await client.CallToolAsync("process_wait", new Dictionary<string, object?>(StringComparer.Ordinal)
                { ["statePath"] = statePath, ["timeoutSeconds"] = 0 }, cancellationToken: ct);
            Assert.False(status.IsError);
            Assert.False(status.StructuredContent!.Value.GetProperty("completed").GetBoolean());
            Assert.False(status.StructuredContent.Value.GetProperty("timedOut").GetBoolean());

            var completion = client.Completion;
            await client.DisposeAsync();
            client = null;
            var terminal = Assert.IsType<StdioClientCompletionDetails>(await completion.WaitAsync(ct));
            Assert.NotNull(terminal.ProcessId);
            Assert.Equal(0, terminal.ExitCode);
            Assert.NotEqual(state.WorkerPid, terminal.ProcessId);
            Assert.NotEqual(state.TargetPid, terminal.ProcessId);
            File.WriteAllText(Path.Combine(cwd, "native-adapter-completion.log"),
                FormattableString.Invariant($"pid={terminal.ProcessId}; exitCode={terminal.ExitCode}; exitedAt={DateTimeOffset.UtcNow:O}"));
            // The gate is still closed: both exact processes must outlive the actual adapter.
            Assert.Null(TlbxMcpIsolatedProcessTool.ReadState(state.RunDirectory)!.CompletedAt);
            AssertAlive(state.WorkerPid, state.WorkerStartedTicks);
            AssertAlive(state.TargetPid, state.TargetStartedTicks);
            File.WriteAllText(gate, "adapter exited");
            while (true)
            {
                state = TlbxMcpIsolatedProcessTool.ReadState(state.RunDirectory)!;
                if (state.CompletedAt is not null && !File.Exists(Path.Combine(state.RunDirectory, "launch.json"))) break;
                try { await Task.Delay(50, ct); }
                catch (OperationCanceledException error) when (!TestContext.Current.CancellationToken.IsCancellationRequested)
                {
                    var worker = SessionProcessContext.ReadProcess(state.WorkerPid);
                    var target = SessionProcessContext.ReadProcess(state.TargetPid);
                    throw new TimeoutException(FormattableString.Invariant(
                        $"Native worker completion timed out: state={JsonSerializer.Serialize(state, IsolatedProcessJsonContext.Default.IsolatedRunState)}; launchSpecPresent={File.Exists(Path.Combine(state.RunDirectory, "launch.json"))}; gatePresent={File.Exists(gate)}; workerEpochLive={worker?.Started.Ticks == state.WorkerStartedTicks}; targetEpochLive={target?.Started.Ticks == state.TargetStartedTicks}"), error);
                }
            }
            Assert.Null(state.Error);
            Assert.Equal(0, state.ExitCode);
            Assert.Equal("child-started" + Environment.NewLine + "after-native-adapter-exit", File.ReadAllText(outcome.GetProperty("stdoutPath").GetString()!));
            Assert.Equal("separate-child-stderr", File.ReadAllText(outcome.GetProperty("stderrPath").GetString()!));
            Assert.Equal(new[] { address, "target01", "native-callback" }, File.ReadAllLines(Path.Combine(cwd, "context-hints.txt")));
            Assert.Equal(1, contextReads);
            // A fresh native adapter can observe the original run after its
            // launcher disconnected; no application operation is replayed.
            client = await McpClient.CreateAsync(new StdioClientTransport(transportOptions), cancellationToken: ct);
            var resumedWait = await client.CallToolAsync("process_wait", new Dictionary<string, object?>(StringComparer.Ordinal)
                { ["statePath"] = statePath, ["timeoutSeconds"] = 5 }, cancellationToken: ct);
            Assert.False(resumedWait.IsError, resumedWait.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
            Assert.True(resumedWait.StructuredContent!.Value.GetProperty("completed").GetBoolean());
            Assert.Equal(0, resumedWait.StructuredContent.Value.GetProperty("exitCode").GetInt32());
            await client.DisposeAsync();
            client = null;
            Assert.True(authenticatedRequests >= 2);
            Assert.Equal(0, forwardedCalls); // Never dispatch local execution to the HTTPS authority.
            verified = true;
        }
        finally
        {
            if (client is not null) await client.DisposeAsync();
            // Always release our bounded child; preserve durable logs on failure.
            File.WriteAllText(gate, "test cleanup");
            if (state is not null)
            {
                await StopOwnedProcessAsync(state.TargetPid, state.TargetStartedTicks);
                await StopOwnedProcessAsync(state.WorkerPid, state.WorkerStartedTicks);
            }
            await app.StopAsync(CancellationToken.None);
            if (verified) Directory.Delete(cwd, recursive: true);
            else TestContext.Current.SendDiagnosticMessage("Native process lifetime artifacts: " + cwd);
        }
    }

    private static string PhysicalDirectory(string path)
    {
        var current = Path.GetPathRoot(path)!;
        foreach (var name in Path.GetRelativePath(current, path).Split(
            [Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            var entry = new DirectoryInfo(Path.Combine(current, name));
            current = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? entry.FullName;
        }
        return current;
    }

    private static void AssertAlive(int pid, long startedTicks)
    {
        using var process = Process.GetProcessById(pid);
        Assert.Equal(startedTicks, process.StartTime.ToUniversalTime().Ticks);
        Assert.False(process.HasExited);
    }

    private static async Task StopOwnedProcessAsync(int pid, long startedTicks)
    {
        if (pid <= 0 || startedTicks <= 0) return;
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.StartTime.ToUniversalTime().Ticks != startedTicks) return;
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None); }
            catch (TimeoutException)
            {
                if (!process.HasExited && process.StartTime.ToUniversalTime().Ticks == startedTicks)
                    process.Kill();
                await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None);
            }
        }
        catch (ArgumentException) { }
        catch (InvalidOperationException) { }
    }
}

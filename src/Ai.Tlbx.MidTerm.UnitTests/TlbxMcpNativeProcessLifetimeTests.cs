using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json.Nodes;
using Ai.Tlbx.MidTerm.Services.Mcp;
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
        var cwd = Path.Combine(Path.GetTempPath(), "tlbx-native-lifetime-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        var gate = Path.Combine(cwd, "release-child");
        var script = Path.Combine(cwd, OperatingSystem.IsWindows() ? "child.ps1" : "child.sh");
        File.WriteAllText(script, OperatingSystem.IsWindows() ? """
            [Console]::WriteLine('child-started')
            $deadline = [DateTime]::UtcNow.AddSeconds(25)
            while (!(Test-Path -LiteralPath $args[0])) {
                if ([DateTime]::UtcNow -gt $deadline) { exit 23 }
                Start-Sleep -Milliseconds 50
            }
            [Console]::Write('after-native-adapter-exit')
            [Console]::Error.Write('separate-child-stderr')
            """ : """
            printf 'child-started\n'
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
        const string artifactUri = "tlbx-artifact://capture/native-executable-fixture";
        var artifactBytes = new byte[TlbxMcpArtifactSaveTool.ChunkBytes + 17];
        RandomNumberGenerator.Fill(artifactBytes);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0, endpoint => endpoint.UseHttps(certificate)));
        builder.Services.AddMcpServer().WithHttpTransport(options => options.SessionMode = HttpServerSessionMode.Stateless)
            .WithListToolsHandler((_, _) => ValueTask.FromResult(new ListToolsResult
                { Tools = TlbxMcpArtifactTools.CreateTools().Select(tool => tool.ProtocolTool).ToList() }))
            .WithCallToolHandler((call, _) =>
            {
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
        bounded.CancelAfter(TimeSpan.FromSeconds(45));
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
            var transport = new StdioClientTransport(new StdioClientTransportOptions
            {
                Command = nativeBinary ?? "dotnet",
                Arguments = nativeBinary is null ? [Path.Combine(AppContext.BaseDirectory, "mt.dll"), .. adapterArguments] : adapterArguments,
                WorkingDirectory = cwd,
                InheritEnvironmentVariables = false,
                EnvironmentVariables = environment,
                ShutdownTimeout = TimeSpan.FromSeconds(5),
                StandardErrorLines = line => { lock (stderrLock) File.AppendAllText(stderrPath, line + Environment.NewLine); }
            });
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
            var executable = OperatingSystem.IsWindows()
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
                : "/bin/sh";
            string[] argv = OperatingSystem.IsWindows()
                ? ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, gate] : [script, gate];
            var result = await client.CallToolAsync("process_run_isolated", new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["executable"] = executable, ["arguments"] = argv
            }, cancellationToken: ct);
            Assert.False(result.IsError, result.Content.OfType<TextContentBlock>().FirstOrDefault()?.Text);
            var outcome = result.StructuredContent!.Value;
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
                await Task.Delay(50, ct);
            }
            Assert.Null(state.Error);
            Assert.Equal(0, state.ExitCode);
            Assert.Equal("child-started" + Environment.NewLine + "after-native-adapter-exit", File.ReadAllText(outcome.GetProperty("stdoutPath").GetString()!));
            Assert.Equal("separate-child-stderr", File.ReadAllText(outcome.GetProperty("stderrPath").GetString()!));
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

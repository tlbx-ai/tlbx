using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Services.Mcp;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TlbxMcpIsolatedProcessTests
{
    [Fact]
    public async Task AtomicStatePublicationWaitsForTransientWindowsReaderWithoutRelaunching()
    {
        if (!OperatingSystem.IsWindows()) return;
        var run = Path.Combine(Path.GetTempPath(), "tlbx-state-sharing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        try
        {
            var state = new IsolatedRunState { RunDirectory = run, TargetPid = 123 };
            TlbxMcpIsolatedProcessTool.WriteState(state);
            state.TargetPid = 456;
            Task writing;
            using (var reader = new FileStream(Path.Combine(run, "state.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                writing = Task.Run(() => TlbxMcpIsolatedProcessTool.WriteState(state), TestContext.Current.CancellationToken);
                await Task.Delay(100, TestContext.Current.CancellationToken);
                Assert.False(writing.IsCompleted);
            }
            await writing;
            Assert.Equal(456, TlbxMcpIsolatedProcessTool.ReadState(run)!.TargetPid);
        }
        finally { DeleteKnownRun(run); }
    }

    [Fact]
    public async Task FailedOutputDestinationDoesNotAbandonTheChildPipe()
    {
        using var source = new MemoryStream(new byte[256 * 1024]);
        using var destination = new FailedDestination();
        var errors = new List<Exception>();
        await TlbxMcpIsolatedProcessTool.DrainAsync(source, destination, errors.Add);
        Assert.Equal(source.Length, source.Position);
        Assert.IsType<IOException>(Assert.Single(errors));
    }

    private sealed class FailedDestination : MemoryStream
    {
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
            => ValueTask.FromException(new IOException("Simulated full artifact disk."));
    }

    [Fact]
    public async Task RealDetachedWorkerPreservesArgvCwdUserAndSeparateStreamsAfterCallerHandlesClose()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "tlbx isolated test " + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(cwd);
        var script = Path.Combine(cwd, OperatingSystem.IsWindows() ? "probe.ps1" : "probe.sh");
        var literal = "Grüße 'quoted' \"double\" $() trailing\\";
        string executable;
        string[] argv;
        if (OperatingSystem.IsWindows())
        {
            executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");
            File.WriteAllText(script,
                "[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); "
                + "[Console]::WriteLine((Get-Location).Path); [Console]::WriteLine([Environment]::UserName); "
                + "[Console]::WriteLine(($args | ConvertTo-Json -Compress)); "
                + "[Console]::Error.Write('separate-stderr'); "
                + "[Console]::WriteLine('stdin=' + [Console]::In.ReadToEnd().Length); "
                + "Start-Sleep -Milliseconds 350; [Console]::Write('after-caller-handles-closed')", new UTF8Encoding(true));
            argv = ["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script, "", literal];
        }
        else
        {
            executable = "/bin/sh";
            File.WriteAllText(script, "pwd; id -un; printf '[%s]\\n' \"$@\"; printf separate-stderr >&2; text=$(cat); printf 'stdin=%s\\n' \"${#text}\"; sleep 0.35; printf after-caller-handles-closed\n");
            argv = [script, "", literal];
        }
        IsolatedRunState? state = null;
        var verified = false;
        try
        {
            using var caller = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            state = await TlbxMcpIsolatedProcessTool.StartAsync(executable, argv, cwd, caller.Token);
            caller.Cancel(); // Launch completed; ending the MCP caller does not own the background lifetime.
            var waited = await TlbxMcpProcessWaitTool.InvokeLocalAsync(new ModelContextProtocol.Protocol.CallToolRequestParams
            {
                Name = TlbxMcpProcessWaitTool.ToolName,
                Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new
                    { statePath = Path.Combine(state.RunDirectory, "state.json"), timeoutSeconds = 15 }))
            }, TestContext.Current.CancellationToken);
            Assert.False(waited.IsError, JsonSerializer.Serialize(waited.StructuredContent));
            Assert.True(waited.StructuredContent!.Value.GetProperty("completed").GetBoolean());
            Assert.Equal(0, waited.StructuredContent.Value.GetProperty("exitCode").GetInt32());
            Assert.EndsWith("after-caller-handles-closed", File.ReadAllText(Path.Combine(state.RunDirectory, "stdout.log")), StringComparison.Ordinal);
            var deadline = Stopwatch.StartNew();
            while (deadline.Elapsed < TimeSpan.FromSeconds(15))
            {
                var finished = TlbxMcpIsolatedProcessTool.ReadState(state.RunDirectory);
                if (finished?.CompletedAt is not null && !File.Exists(Path.Combine(finished.RunDirectory, "launch.json"))) { state = finished; break; }
                await Task.Delay(50, TestContext.Current.CancellationToken);
            }
            Assert.NotNull(state.CompletedAt);
            Assert.Null(state.Error);
            Assert.Equal(0, state.ExitCode);
            var stdout = File.ReadAllText(Path.Combine(state.RunDirectory, "stdout.log"));
            var stderr = File.ReadAllText(Path.Combine(state.RunDirectory, "stderr.log"));
            Assert.StartsWith(cwd, stdout, StringComparison.Ordinal);
            Assert.Contains(Environment.UserName, stdout, StringComparison.Ordinal);
            Assert.Contains("stdin=0", stdout, StringComparison.Ordinal);
            Assert.EndsWith("after-caller-handles-closed", stdout, StringComparison.Ordinal);
            Assert.Equal("separate-stderr", stderr);
            Assert.DoesNotContain("separate-stderr", stdout, StringComparison.Ordinal);
            if (OperatingSystem.IsWindows())
            {
                var lines = stdout.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
                using var json = JsonDocument.Parse(lines[2]);
                Assert.Equal("", json.RootElement[0].GetString());
                Assert.Equal(literal, json.RootElement[1].GetString());
            }
            else Assert.Contains("[]\n[" + literal + "]", stdout, StringComparison.Ordinal);
            Assert.False(File.Exists(Path.Combine(state.RunDirectory, "launch.json")));
            verified = true;
        }
        finally
        {
            // Keep failures' durable diagnostics; never delete a possibly active run.
            if (verified && state?.CompletedAt is not null) DeleteKnownRun(state.RunDirectory);
            if (verified && state?.CompletedAt is not null)
            {
                File.Delete(script);
                Directory.Delete(Path.Combine(cwd, ".tlbx", "runs"));
                Directory.Delete(Path.Combine(cwd, ".tlbx"));
                Directory.Delete(cwd);
            }
        }
    }

    [Fact]
    public void RetentionProtectsActualProcessStartAndRemovesCompletedExpiredCountAndByteExcess()
    {
        var root = Path.Combine(Path.GetTempPath(), "tlbx-run-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        using var self = Process.GetCurrentProcess();
        var now = DateTimeOffset.UtcNow;
        string Make(int days, bool active = false, bool reusedPid = false)
        {
            var id = now.AddDays(-days).UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
            var path = Path.Combine(root, id);
            Directory.CreateDirectory(path);
            var state = new IsolatedRunState
            {
                RunDirectory = path, CreatedAt = now.AddDays(-days), CompletedAt = now.AddDays(-days),
                TargetPid = active || reusedPid ? self.Id : 0,
                TargetStartedTicks = active ? self.StartTime.ToUniversalTime().Ticks : 1
            };
            File.WriteAllText(Path.Combine(path, "state.json"), JsonSerializer.Serialize(state, IsolatedProcessJsonContext.Default.IsolatedRunState));
            File.WriteAllBytes(Path.Combine(path, "stdout.log"), new byte[32]);
            return path;
        }
        var activeRun = Make(30, active: true);
        var reused = Make(30, reusedPid: true);
        var expired = Make(15);
        var oldest = Make(3);
        var newer = Make(2);
        var newest = Make(1);
        try
        {
            TlbxMcpIsolatedProcessTool.Cleanup(root, now, maxCompleted: 2);
            Assert.True(Directory.Exists(activeRun));
            Assert.False(Directory.Exists(reused));
            Assert.False(Directory.Exists(expired));
            Assert.False(Directory.Exists(oldest));
            Assert.True(Directory.Exists(newer));
            Assert.True(Directory.Exists(newest));
            var newestBytes = Directory.GetFiles(newest).Sum(file => new FileInfo(file).Length);
            TlbxMcpIsolatedProcessTool.Cleanup(root, now, maxBytes: newestBytes);
            Assert.True(Directory.Exists(activeRun));
            Assert.True(Directory.Exists(newest));
            Assert.False(Directory.Exists(newer));
        }
        finally
        {
            foreach (var run in Directory.GetDirectories(root)) DeleteKnownRun(run);
            Directory.Delete(root);
        }
    }

    private static void DeleteKnownRun(string run)
    {
        foreach (var file in Directory.GetFiles(run)) File.Delete(file);
        Directory.Delete(run);
    }
}

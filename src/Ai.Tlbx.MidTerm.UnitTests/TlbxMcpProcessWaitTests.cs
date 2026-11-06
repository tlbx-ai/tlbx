using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Services.Mcp;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TlbxMcpProcessWaitTests
{
    [Fact]
    public async Task StatusTimeoutCancellationAndReusedPidNeverInventCompletionOrStopTheProcess()
    {
        var cwd = Path.Combine(Path.GetTempPath(), "tlbx-wait-" + Guid.NewGuid().ToString("N"));
        var run = Path.Combine(cwd, ".tlbx", "runs", DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(run);
        using var self = Process.GetCurrentProcess();
        var state = new IsolatedRunState
        {
            RunDirectory = run, WorkingDirectory = cwd, WorkerPid = self.Id,
            WorkerStartedTicks = self.StartTime.ToUniversalTime().Ticks,
            TargetPid = self.Id, TargetStartedTicks = self.StartTime.ToUniversalTime().Ticks
        };
        async Task<CallToolResult> Wait(int seconds, CancellationToken token) => await TlbxMcpProcessWaitTool.InvokeLocalAsync(new()
        {
            Name = TlbxMcpProcessWaitTool.ToolName,
            Arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(JsonSerializer.Serialize(new
                { statePath = Path.Combine(run, "state.json"), timeoutSeconds = seconds }))
        }, token);
        try
        {
            TlbxMcpIsolatedProcessTool.WriteState(state);
            var running = await Wait(0, TestContext.Current.CancellationToken);
            Assert.False(running.IsError);
            Assert.False(running.StructuredContent!.Value.GetProperty("completed").GetBoolean());
            var timeout = await Wait(1, TestContext.Current.CancellationToken);
            Assert.True(timeout.IsError);
            Assert.True(timeout.StructuredContent!.Value.GetProperty("timedOut").GetBoolean());
            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();
            Assert.True((await Wait(30, cancelled.Token)).IsError);
            Assert.False(self.HasExited);
            state.WorkerStartedTicks = state.TargetStartedTicks = 1;
            TlbxMcpIsolatedProcessTool.WriteState(state);
            var unknown = await Wait(0, TestContext.Current.CancellationToken);
            Assert.True(unknown.IsError);
            Assert.True(unknown.StructuredContent!.Value.GetProperty("outcomeUnknown").GetBoolean());
            Assert.Equal(JsonValueKind.Null, unknown.StructuredContent.Value.GetProperty("exitCode").ValueKind);
            state.CompletedAt = DateTimeOffset.UtcNow;
            state.ExitCode = 7;
            TlbxMcpIsolatedProcessTool.WriteState(state);
            var failed = await Wait(0, TestContext.Current.CancellationToken);
            Assert.True(failed.IsError);
            Assert.Equal(7, failed.StructuredContent!.Value.GetProperty("exitCode").GetInt32());
        }
        finally { Directory.Delete(cwd, recursive: true); }
    }
}

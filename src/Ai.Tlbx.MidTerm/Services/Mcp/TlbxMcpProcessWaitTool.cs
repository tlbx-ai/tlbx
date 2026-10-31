using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>Observe a caller-local detached run without owning or replaying its lifetime.</summary>
internal static class TlbxMcpProcessWaitTool
{
    internal const string ToolName = "process_wait";
    internal static Tool Definition { get; } = new()
    {
        Name = ToolName,
        Description = "Wait for a process_run_isolated statePath on the native caller machine. timeoutSeconds defaults to 30 (0 reads status, maximum 600). Returns the recorded exit code only after the worker finishes draining both logs. Nonzero exit, unknown outcome and a positive wait timeout are errors, so workflows stop before dependent steps. Cancellation/timeout never kills or restarts the process. Read-only and safe to repeat using the same statePath. Requires native stdio; HTTP never reads service-account files.",
        InputSchema = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("statePath"),
            ["properties"] = new JsonObject
            {
                ["statePath"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                ["timeoutSeconds"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 600, ["default"] = 30 }
            }
        }, McpJsonContext.Default.JsonObject),
        Annotations = new ToolAnnotations { ReadOnlyHint = true, IdempotentHint = true, OpenWorldHint = false }
    };

    internal static async ValueTask<CallToolResult> InvokeLocalAsync(CallToolRequestParams request, CancellationToken ct)
    {
        try
        {
            var args = request.Arguments ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (args.Keys.Any(key => key is not ("statePath" or "timeoutSeconds"))) throw new ArgumentException("Unknown process wait argument.");
            if (!args.TryGetValue("statePath", out var path) || path.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(path.GetString()))
                throw new ArgumentException("statePath must be the path returned by process_run_isolated.");
            var seconds = 30;
            if (args.TryGetValue("timeoutSeconds", out var timeout) &&
                (timeout.ValueKind != JsonValueKind.Number || !timeout.TryGetInt32(out seconds) || seconds is < 0 or > 600))
                throw new ArgumentException("timeoutSeconds must be an integer from 0 to 600.");
            var statePath = Path.GetFullPath(path.GetString()!);
            if (Path.GetFileName(statePath) != "state.json" || new FileInfo(statePath).LinkTarget is not null)
                throw new ArgumentException("statePath must identify a generated state.json file.");
            var run = Path.GetDirectoryName(statePath)!;
            var root = Path.GetDirectoryName(run)!;
            if (Path.GetFileName(root) != "runs" || Path.GetFileName(Path.GetDirectoryName(root)) != ".tlbx")
                throw new ArgumentException("statePath must identify a generated .tlbx/runs directory.");
            var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
            TlbxMcpIsolatedProcessTool.ValidateRun(run, root);
            var clock = Stopwatch.StartNew();
            var inactiveSamples = 0;
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                // Generated state is atomically replaced, never edited in place.
                // Bound reads even if an unrelated local file was supplied.
                IsolatedRunState state;
                using (var file = new FileStream(statePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    if (file.Length is < 1 or > 65536) throw new IOException("Invalid generated process state size.");
                    var bytes = new byte[(int)file.Length];
                    file.ReadExactly(bytes);
                    state = JsonSerializer.Deserialize(bytes, IsolatedProcessJsonContext.Default.IsolatedRunState)
                        ?? throw new IOException("The generated process state is empty.");
                } // Never hold the old Windows destination open across the poll delay.
                if (!string.Equals(Path.GetFullPath(state.RunDirectory), run, comparison) ||
                    !string.Equals(Path.GetFullPath(Path.Combine(state.WorkingDirectory, ".tlbx", "runs")), root, comparison))
                    throw new IOException("Process state does not match its generated directory.");
                var complete = state.CompletedAt is not null;
                var active = state.WorkerStartedTicks > 0 && TlbxMcpIsolatedProcessTool.IsActive(state.WorkerPid, state.WorkerStartedTicks)
                    || state.TargetStartedTicks > 0 && TlbxMcpIsolatedProcessTool.IsActive(state.TargetPid, state.TargetStartedTicks);
                if (active) inactiveSamples = 0;
                if (!complete && !active && inactiveSamples++ == 0)
                {
                    // The worker may publish completion and exit between our
                    // state read and PID check. Read the final generation first.
                    await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
                    continue;
                }
                var expired = clock.Elapsed >= TimeSpan.FromSeconds(seconds);
                if (complete || !active || expired)
                {
                    var unknown = !complete && !active;
                    var timedOut = !complete && active && seconds > 0;
                    return McpApiResult.Structured(new JsonObject
                    {
                        ["statePath"] = statePath, ["runId"] = Path.GetFileName(run), ["pid"] = state.TargetPid,
                        ["processStartedUtcTicks"] = state.TargetStartedTicks,
                        ["completed"] = complete, ["timedOut"] = timedOut, ["outcomeUnknown"] = unknown,
                        ["exitCode"] = complete ? state.ExitCode : null,
                        ["stdoutPath"] = Path.Combine(run, "stdout.log"), ["stderrPath"] = Path.Combine(run, "stderr.log"),
                        ["warning"] = state.Error, ["replaySafe"] = true,
                        ["message"] = unknown ? "Both recorded processes ended without a final outcome. Inspect the logs; do not relaunch automatically."
                            : timedOut ? "The wait timed out. The process was not stopped; repeat process_wait with this statePath."
                            : complete ? "The worker recorded completion. Inspect exitCode and warning." : "The recorded process is still running."
                    }, error: unknown || timedOut || complete && (state.ExitCode != 0 || state.Error is not null));
                }
                await Task.Delay(TimeSpan.FromMilliseconds(100), ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        { return McpApiResult.Error("TLBX_MCP_PROCESS_WAIT_CANCELLED", "Waiting was cancelled. The process was not stopped or restarted; inspect or wait again using the same statePath."); }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or JsonException)
        { return McpApiResult.Error("TLBX_MCP_PROCESS_STATE_UNAVAILABLE", error.Message + " No process was started, stopped or replayed."); }
    }
}

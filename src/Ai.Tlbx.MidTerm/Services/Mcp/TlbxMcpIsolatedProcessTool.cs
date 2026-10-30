using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ModelContextProtocol.Protocol;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>Runs only in the caller's native adapter. The service must never execute this tool.</summary>
internal static class TlbxMcpIsolatedProcessTool
{
    internal static Tool ProtocolTool { get; } = new()
    {
        Name = "process_run_isolated",
        Description = "Start a noninteractive process on the native MCP caller machine as its OS user, with exact argv, detached stdin and separate durable stdout/stderr logs. The child survives MCP connection closure. Defaults to the caller cwd. Requires native stdio; HTTP never launches as the service account.",
        InputSchema = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("executable"),
            ["properties"] = new JsonObject
            {
                ["executable"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                ["arguments"] = new JsonObject { ["type"] = "array", ["items"] = new JsonObject { ["type"] = "string" } },
                ["workingDirectory"] = new JsonObject { ["type"] = "string" }
            }
        }, McpJsonContext.Default.JsonObject)
    };

    internal static async ValueTask<CallToolResult> InvokeLocalAsync(CallToolRequestParams request, CancellationToken ct)
    {
        try
        {
            var args = request.Arguments ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (args.Keys.Any(k => k is not ("executable" or "arguments" or "workingDirectory"))) throw new ArgumentException("Unexpected process argument.");
            if (!args.TryGetValue("executable", out var exe) || exe.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(exe.GetString()))
                throw new ArgumentException("executable is required.");
            var argv = new List<string>();
            if (args.TryGetValue("arguments", out var values))
            {
                if (values.ValueKind != JsonValueKind.Array) throw new ArgumentException("arguments must be an array of strings.");
                using var items = values.EnumerateArray();
                foreach (var item in items)
                    argv.Add(item.ValueKind == JsonValueKind.String ? item.GetString()! : throw new ArgumentException("Every argv item must be a string."));
            }
            var cwd = args.TryGetValue("workingDirectory", out var directory) ? directory.GetString()! : Environment.CurrentDirectory;
            var started = await StartAsync(exe.GetString()!, argv, cwd, ct).ConfigureAwait(false);
            return McpApiResult.Structured(new JsonObject
            {
                ["pid"] = started.TargetPid, ["runId"] = Path.GetFileName(started.RunDirectory),
                ["stdoutPath"] = Path.Combine(started.RunDirectory, "stdout.log"),
                ["stderrPath"] = Path.Combine(started.RunDirectory, "stderr.log"),
                ["workingDirectory"] = started.WorkingDirectory, ["executionHost"] = "native-mcp-caller",
                ["processStartedUtcTicks"] = started.TargetStartedTicks,
                ["statePath"] = Path.Combine(started.RunDirectory, "state.json"), ["warning"] = started.Error
            });
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        { return McpApiResult.Error("TLBX_MCP_PROCESS_START_FAILED", ex.Message + " A confirmed launch is never replayed automatically."); }
    }

    internal static async Task<IsolatedRunState> StartAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(executable) || executable.AsSpan().Contains('\0') || arguments.Any(a => a.AsSpan().Contains('\0')))
            throw new ArgumentException("Executable and argv must not contain NUL; empty argv strings are supported.");
        var cwd = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(cwd)) throw new DirectoryNotFoundException("The caller working directory does not exist.");
        ct.ThrowIfCancellationRequested();
        var root = Path.Combine(cwd, ".tlbx", "runs");
        FileRecoveryStorage.EnsurePrivate(root);
        Cleanup(root);
        var id = DateTime.UtcNow.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        var run = Path.Combine(root, id);
        FileRecoveryStorage.EnsurePrivate(run);
        var spec = new IsolatedRunSpec { Executable = executable, Arguments = arguments.ToArray(), WorkingDirectory = cwd, RunDirectory = run };
        var specPath = Path.Combine(run, "launch.json");
        File.WriteAllText(specPath, JsonSerializer.Serialize(spec, IsolatedProcessJsonContext.Default.IsolatedRunSpec));
        var launch = WorkerStartInfo(specPath, cwd);
        using var worker = Process.Start(launch) ?? throw new InvalidOperationException("Could not start the caller-side isolated worker.");
        worker.StandardInput.Close();
        // No terminal handles are inherited. The worker never writes protocol/console output.
        worker.StandardOutput.Close();
        worker.StandardError.Close();
        // After launch, cancellation cannot undo or authorize replaying this mutation.
        var handshake = Stopwatch.StartNew();
        while (handshake.Elapsed < TimeSpan.FromSeconds(15))
        {
            var state = ReadState(run);
            if (state is { TargetPid: > 0 }) return state;
            if (state?.Error is { } error) throw new InvalidOperationException("Worker failed before confirming launch: " + error);
            if (worker.HasExited) throw new InvalidOperationException("Isolated worker exited before confirming launch. Inspect " + run + " before retrying.");
            await Task.Delay(25, CancellationToken.None).ConfigureAwait(false);
        }
        throw new InvalidOperationException("Launch confirmation timed out. Inspect " + run + " before retrying; the process may already be running.");
    }

    internal static ProcessStartInfo WorkerStartInfo(string specPath, string cwd)
    {
        var assembly = typeof(TlbxMcpIsolatedProcessTool).Assembly;
        var isOwnEntry = Assembly.GetEntryAssembly() == assembly;
        var host = isOwnEntry ? Environment.ProcessPath! : "dotnet";
        var argv = new List<string>();
        if (!isOwnEntry) argv.Add(Path.Combine(AppContext.BaseDirectory, "mt.dll")); // Managed test harness references the executable assembly.
        else if (string.Equals(Path.GetFileNameWithoutExtension(host), "dotnet", StringComparison.OrdinalIgnoreCase))
            argv.Add(Path.GetFullPath(Environment.GetCommandLineArgs()[0]));
        argv.Add("mcp-process-worker"); argv.Add(specPath);
        if (!OperatingSystem.IsWindows()) { argv.Insert(0, host); host = "nohup"; }
        var info = new ProcessStartInfo(host)
        {
            WorkingDirectory = cwd, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        foreach (var arg in argv) info.ArgumentList.Add(arg);
        return info;
    }

    internal static int RunWorker(string[] args)
    {
        if (args.Length != 1) return 2;
        try { return RunWorkerAsync(args[0]).GetAwaiter().GetResult(); }
        catch { return 1; } // Diagnostics belong to the private run state/logs, never inherited MCP streams.
    }

    internal static async Task<int> RunWorkerAsync(string specPath)
    {
        var spec = JsonSerializer.Deserialize(File.ReadAllText(specPath), IsolatedProcessJsonContext.Default.IsolatedRunSpec)
            ?? throw new InvalidOperationException("Missing private launch spec.");
        ValidateRun(spec.RunDirectory, Path.Combine(spec.WorkingDirectory, ".tlbx", "runs"));
        if (!string.Equals(Path.GetFullPath(specPath), Path.Combine(spec.RunDirectory, "launch.json"), PathComparison))
            throw new InvalidOperationException("Invalid launch spec location.");
        FileRecoveryStorage.EnsurePrivate(spec.RunDirectory);
        using var current = Process.GetCurrentProcess();
        var state = new IsolatedRunState
        {
            RunDirectory = spec.RunDirectory, WorkingDirectory = spec.WorkingDirectory,
            WorkerPid = current.Id, WorkerStartedTicks = current.StartTime.ToUniversalTime().Ticks,
            CreatedAt = DateTimeOffset.UtcNow
        };
        WriteState(state);
        // Existing CLI retention recognizes this marker; the worker owns both pipes until the child finishes.
        File.WriteAllText(Path.Combine(spec.RunDirectory, "pid"), current.Id.ToString(CultureInfo.InvariantCulture));
        var failures = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        void Record(Exception error)
        {
            failures.Enqueue(error);
            try { File.AppendAllText(Path.Combine(spec.RunDirectory, "worker-error.log"), error + Environment.NewLine); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        void Publish()
        {
            state.Error = failures.IsEmpty ? null : string.Join(Environment.NewLine, failures.Select(error => error.ToString()));
            try { WriteState(state); }
            catch (IOException ex) { Record(ex); }
            catch (UnauthorizedAccessException ex) { Record(ex); }
        }
        try
        {
            var start = new ProcessStartInfo(spec.Executable)
            {
                WorkingDirectory = spec.WorkingDirectory, UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
            };
            foreach (var arg in spec.Arguments) start.ArgumentList.Add(arg);
            await using var stdout = new FileStream(Path.Combine(spec.RunDirectory, "stdout.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
            await using var stderr = new FileStream(Path.Combine(spec.RunDirectory, "stderr.log"), FileMode.CreateNew, FileAccess.Write, FileShare.Read, 4096, useAsync: true);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("Executable did not start.");
            state.TargetPid = process.Id;
            try { state.TargetStartedTicks = process.StartTime.ToUniversalTime().Ticks; }
            catch (InvalidOperationException ex) { Record(ex); }
            catch (System.ComponentModel.Win32Exception ex) { Record(ex); }
            try { process.StandardInput.Close(); }
            catch (IOException ex) { Record(ex); }
            var output = DrainAsync(process.StandardOutput.BaseStream, stdout, Record);
            var errorOutput = DrainAsync(process.StandardError.BaseStream, stderr, Record);
            Publish(); // Publishing diagnostics must never abandon a launched child's pipes.
            await Task.WhenAll(output, errorOutput, process.WaitForExitAsync()).ConfigureAwait(false);
            state.ExitCode = process.ExitCode;
            state.CompletedAt = DateTimeOffset.UtcNow;
            Publish();
            return failures.IsEmpty ? 0 : 1;
        }
        catch (Exception ex)
        {
            Record(ex); state.CompletedAt = DateTimeOffset.UtcNow; Publish();
            return 1;
        }
        finally { File.Delete(specPath); }
    }

    internal static async Task DrainAsync(Stream source, Stream destination, Action<Exception> failure)
    {
        var buffer = new byte[81920];
        var canWrite = true;
        try
        {
            int count;
            while ((count = await source.ReadAsync(buffer.AsMemory()).ConfigureAwait(false)) > 0)
            {
                if (!canWrite) continue;
                try { await destination.WriteAsync(buffer.AsMemory(0, count)).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure(ex); canWrite = false; }
            }
            if (canWrite) await destination.FlushAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { failure(ex); }
    }

    internal static void WriteState(IsolatedRunState state)
    {
        var path = Path.Combine(state.RunDirectory, "state.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(state, IsolatedProcessJsonContext.Default.IsolatedRunState));
        for (var attempt = 0; ; attempt++)
        {
            try { File.Move(path + ".tmp", path, overwrite: true); break; }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
                && OperatingSystem.IsWindows() && attempt < 16 && (ex.HResult & 0xffff) is 5 or 32)
            {
                // Match the CLI writer's bounded atomic-publication retry. Only metadata is retried, never the process.
                Thread.Sleep(25);
            }
        }
    }

    internal static IsolatedRunState? ReadState(string run)
    {
        try
        {
            // Atomic replacement must remain possible while the adapter polls the old state.
            using var file = new FileStream(Path.Combine(run, "state.json"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return JsonSerializer.Deserialize(file, IsolatedProcessJsonContext.Default.IsolatedRunState);
        }
        catch (IOException) { return null; }
        catch (JsonException) { return null; }
    }

    private static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;

    private static void ValidateRun(string run, string root)
    {
        var name = Path.GetFileName(run);
        if (name.Length != 52 || name[19] != '-' || !Guid.TryParseExact(name[20..], "N", out _)
            || !DateTime.TryParseExact(name[..19], "yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.None, out _)
            || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(run)), Path.GetFullPath(root), PathComparison)
            || new DirectoryInfo(run).LinkTarget is not null)
            throw new InvalidOperationException("Invalid generated run directory.");
    }

    internal static void Cleanup(string root, DateTimeOffset? now = null, int maxCompleted = 100, long maxBytes = 1024L * 1024 * 1024)
    {
        var completed = new List<(string Directory, IsolatedRunState State, long Bytes)>();
        foreach (var run in Directory.EnumerateDirectories(root))
        {
            try
            {
                ValidateRun(run, root);
                var state = ReadState(run);
                if (state is null || IsActive(state.WorkerPid, state.WorkerStartedTicks) || IsActive(state.TargetPid, state.TargetStartedTicks)) continue;
                var files = Directory.GetFiles(run);
                if (files.Any(file => Path.GetFileName(file) is not ("launch.json" or "state.json" or "state.json.tmp" or "stdout.log" or "stderr.log" or "worker-error.log" or "pid"))
                    || files.Any(file => new FileInfo(file).LinkTarget is not null) || Directory.EnumerateDirectories(run).Any()) continue;
                completed.Add((run, state, files.Sum(file => new FileInfo(file).Length)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException) { }
        }
        var count = 0;
        long bytes = 0;
        foreach (var run in completed.OrderByDescending(item => item.State.CompletedAt ?? item.State.CreatedAt))
        {
            count++; bytes += run.Bytes;
            if ((now ?? DateTimeOffset.UtcNow) - (run.State.CompletedAt ?? run.State.CreatedAt) <= TimeSpan.FromDays(14)
                && count <= maxCompleted && bytes <= maxBytes) continue;
            try
            {
                // Exact generated paths only; no recursive delete and no model-supplied targets.
                foreach (var file in Directory.GetFiles(run.Directory)) File.Delete(file);
                Directory.Delete(run.Directory);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool IsActive(int pid, long started)
    {
        if (pid <= 0) return false;
        try { using var process = Process.GetProcessById(pid); return started <= 0 || process.StartTime.ToUniversalTime().Ticks == started; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }
}

internal sealed class IsolatedRunSpec
{
    public string Executable { get; set; } = "";
    public string[] Arguments { get; set; } = [];
    public string WorkingDirectory { get; set; } = "";
    public string RunDirectory { get; set; } = "";
}

internal sealed class IsolatedRunState
{
    public string RunDirectory { get; set; } = "";
    public string WorkingDirectory { get; set; } = "";
    public int WorkerPid { get; set; }
    public long WorkerStartedTicks { get; set; }
    public int TargetPid { get; set; }
    public long TargetStartedTicks { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int? ExitCode { get; set; }
    public string? Error { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(IsolatedRunSpec))]
[JsonSerializable(typeof(IsolatedRunState))]
internal sealed partial class IsolatedProcessJsonContext : JsonSerializerContext;

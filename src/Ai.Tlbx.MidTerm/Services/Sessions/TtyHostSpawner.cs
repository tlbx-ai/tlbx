using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
#if WINDOWS
using System.ComponentModel;
using System.Security.Principal;
using System.Runtime.Versioning;
#endif
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Common.Process;
using Ai.Tlbx.MidTerm.Common.Shells;
using Ai.Tlbx.MidTerm.Models.Update;

using Ai.Tlbx.MidTerm.Services.Updates;
namespace Ai.Tlbx.MidTerm.Services.Sessions;

/// <summary>
/// Spawns mthost processes. Cross-platform with special handling for Windows service mode.
/// </summary>
public static class TtyHostSpawner
{
    private const string MtBinaryPathEnvironmentVariable = "MT_BINARY_PATH";
    internal const string TtyHostPathEnvironmentVariable = "MIDTERM_TTYHOST_PATH";
    private static readonly string TtyHostPath = GetTtyHostPath();
    private static bool _integrityVerified;
    private static readonly object _verifyLock = new();
    private static string? _cachedVersion;
    private static bool _versionChecked;
    private const string MacOsLaunchAgentLabelPrefix = "ai.tlbx.midterm.mthost.";
    private static readonly Regex MacOsPidRegex = new(@"\bpid = (?<pid>\d+)\b", RegexOptions.Compiled, TimeSpan.FromSeconds(1));

    /// <summary>
    /// Gets the expected full path to mthost for this mt installation.
    /// Used to filter discovered processes to only those from this installation.
    /// </summary>
    public static string ExpectedTtyHostPath => TtyHostPath;

    public static void CleanupMacOsGuiLaunchAgent(string sessionId)
    {
#if !WINDOWS
        CleanupMacOsGuiLaunchAgentCore(sessionId);
#else
        _ = sessionId;
#endif
    }

    public static string? GetTtyHostVersion()
    {
        // Return cached version if already checked (version doesn't change at runtime)
        if (_versionChecked)
        {
            return _cachedVersion;
        }

        if (!File.Exists(TtyHostPath))
        {
            _versionChecked = true;
            return null;
        }

        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Windows: read version from PE file metadata (fast, no process spawn)
                var versionInfo = FileVersionInfo.GetVersionInfo(TtyHostPath);
                _cachedVersion = versionInfo.ProductVersion ?? versionInfo.FileVersion;
            }
            else
            {
                // macOS/Linux: PE metadata not available, run mthost --version once
                // Result is cached to avoid spawning process on every health check
                var psi = new ProcessStartInfo
                {
                    FileName = TtyHostPath,
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process is not null)
                {
                    var output = process.StandardOutput.ReadToEnd().Trim();
                    process.WaitForExit(5000);

                    // Output is "mthost 6.7.10" - extract just the version
                    if (!string.IsNullOrEmpty(output))
                    {
                        var parts = output.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                        _cachedVersion = parts.Length >= 2 ? parts[1] : output;
                    }
                }
            }
        }
        catch
        {
            // Ignore errors, just return null
        }

        _versionChecked = true;
        return _cachedVersion;
    }

    /// <summary>
    /// Verifies mthost binary integrity against version.json checksums.
    /// Result is cached after first successful verification.
    /// </summary>
    private static bool VerifyMthostIntegrity()
    {
        // Fast path: already verified this session
        if (_integrityVerified)
        {
            return true;
        }

        lock (_verifyLock)
        {
            if (_integrityVerified)
            {
                return true;
            }

            var installDir = Path.GetDirectoryName(TtyHostPath);
            if (string.IsNullOrEmpty(installDir))
            {
                return true; // Can't verify, allow (dev mode)
            }

            var versionJsonPath = Path.Combine(installDir, "version.json");
            if (!File.Exists(versionJsonPath))
            {
                // No version.json = dev mode or unsigned install, allow
                _integrityVerified = true;
                return true;
            }

            try
            {
                var json = File.ReadAllText(versionJsonPath);
                var manifest = JsonSerializer.Deserialize<VersionManifest>(json, VersionManifestContext.Default.VersionManifest);

                if (manifest?.Checksums is null || manifest.Checksums.Count == 0)
                {
                    // Unsigned release, allow
                    _integrityVerified = true;
                    return true;
                }

                var mthostName = OperatingSystem.IsWindows() ? "mthost.exe" : "mthost";
                if (!manifest.Checksums.TryGetValue(mthostName, out var expectedHash))
                {
                    if (manifest.WebOnly)
                    {
                        Log.Info(() => "TtyHostSpawner: web-only manifest omits mthost checksum; preserving installed host");
                    }
                    else
                    {
                        Log.Warn(() => "TtyHostSpawner: mthost not in version.json checksums");
                    }
                    _integrityVerified = true;
                    return true;
                }

                // Compute actual hash
                using var stream = File.OpenRead(TtyHostPath);
                var actualHash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

                if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
                {
                    Log.Error(() => $"TtyHostSpawner: mthost checksum mismatch! Expected: {expectedHash}, Actual: {actualHash}");
                    return false;
                }

                Log.Info(() => "TtyHostSpawner: mthost integrity verified");
                _integrityVerified = true;
                return true;
            }
            catch (Exception ex)
            {
                Log.Warn(() => $"TtyHostSpawner: Could not verify mthost integrity: {ex.Message}");
                // On error, allow but don't cache
                return true;
            }
        }
    }

    internal static TtyHostSpawnResult SpawnTtyHost(
        string sessionId,
        string? shellType,
        string? workingDirectory,
        int cols,
        int rows,
        string instanceId,
        string ownerToken,
        string? runAsUser,
        string? runAsUserSid,
        int scrollbackBytes,
        IReadOnlyDictionary<string, string?>? environmentOverrides = null,
        int? mtPort = null,
        string? mtToken = null,
        int? paneIndex = null,
        string? tmuxBinDir = null)
    {
        if (!File.Exists(TtyHostPath))
        {
            var message = $"mthost not found at: {TtyHostPath}";
            Log.Error(() => $"TtyHostSpawner: {message}");
            return TtyHostSpawnResult.Failed(message, detail: "MidTerm could not find the tty host binary.");
        }

        if (!VerifyMthostIntegrity())
        {
            const string message = "mthost integrity check failed - refusing to spawn";
            Log.Error(() => $"TtyHostSpawner: {message}");
            return TtyHostSpawnResult.Failed(
                "mthost integrity verification failed.",
                detail: "MidTerm refused to launch an mthost binary whose checksum did not match the installed manifest.");
        }

        var args = BuildArgs(sessionId, shellType, workingDirectory, cols, rows,
            instanceId, ownerToken, scrollbackBytes, mtPort, mtToken, paneIndex, tmuxBinDir);
        var ttyHostEnvironmentOverrides = AddTerminalEnvironmentOverrideKeyMarker(environmentOverrides);

#pragma warning disable CA1416 // Validate platform compatibility (compile-time guard via WINDOWS constant)
#if WINDOWS
        return SpawnWindows(args, runAsUser, runAsUserSid, ttyHostEnvironmentOverrides);
#else
        return SpawnUnix(sessionId, args, runAsUser, ttyHostEnvironmentOverrides);
#endif
#pragma warning restore CA1416
    }

    private static IReadOnlyDictionary<string, string?>? AddTerminalEnvironmentOverrideKeyMarker(
        IReadOnlyDictionary<string, string?>? environmentOverrides)
    {
        if (environmentOverrides is null || environmentOverrides.Count == 0)
        {
            return environmentOverrides;
        }

        var serializedKeys = TerminalEnvironmentOverrides.SerializeOverrideKeys(environmentOverrides.Keys);
        if (string.IsNullOrEmpty(serializedKeys))
        {
            return environmentOverrides;
        }

        var marked = new Dictionary<string, string?>(environmentOverrides, StringComparer.Ordinal)
        {
            [TerminalEnvironmentOverrides.OverrideKeysEnvironmentVariable] = serializedKeys
        };
        return marked;
    }

    internal static bool TryStartRedirectedProcess(
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environmentOverrides,
        IReadOnlyList<string>? pathPrependEntries,
        string? runAsUser,
        string? runAsUserSid,
        out RedirectedProcessHandle? launchedProcess,
        out string? failure)
    {
        launchedProcess = null;
        failure = null;

#pragma warning disable CA1416 // Validate platform compatibility (compile-time guard via WINDOWS constant)
#if WINDOWS
        if (IsRunningAsSystem())
        {
            return TryStartRedirectedProcessAsUserWindows(
                fileName,
                args,
                workingDirectory,
                environmentOverrides,
                pathPrependEntries,
                runAsUser,
                runAsUserSid,
                out launchedProcess,
                out failure);
        }
#else
        if (geteuid() == 0 && !string.IsNullOrWhiteSpace(runAsUser))
        {
            if (!UserValidationService.IsValidUsernameFormat(runAsUser))
            {
                failure = $"Rejected invalid username format: {runAsUser}";
                return false;
            }

            var sudoStartInfo = CreateRedirectedProcessStartInfo("sudo", workingDirectory, environmentOverrides, pathPrependEntries);
            var preservedEnvironmentVariables = new List<string>();
            if (pathPrependEntries is { Count: > 0 })
            {
                preservedEnvironmentVariables.Add("PATH");
            }

            if (environmentOverrides is not null)
            {
                foreach (var key in environmentOverrides.Keys.Where(static key => !string.IsNullOrWhiteSpace(key)))
                {
                    if (!preservedEnvironmentVariables.Contains(key, StringComparer.Ordinal))
                    {
                        preservedEnvironmentVariables.Add(key);
                    }
                }
            }

            if (preservedEnvironmentVariables.Count > 0)
            {
                sudoStartInfo.ArgumentList.Add($"--preserve-env={string.Join(',', preservedEnvironmentVariables)}");
            }

            sudoStartInfo.ArgumentList.Add("-H");
            sudoStartInfo.ArgumentList.Add("-u");
            sudoStartInfo.ArgumentList.Add(runAsUser);
            sudoStartInfo.ArgumentList.Add(fileName);
            foreach (var arg in args)
            {
                sudoStartInfo.ArgumentList.Add(arg);
            }

            return TryStartRedirectedProcessDirect(sudoStartInfo, out launchedProcess, out failure);
        }
#endif
#pragma warning restore CA1416

        var directStartInfo = CreateRedirectedProcessStartInfo(fileName, workingDirectory, environmentOverrides, pathPrependEntries);
        foreach (var arg in args)
        {
            directStartInfo.ArgumentList.Add(arg);
        }

        return TryStartRedirectedProcessDirect(directStartInfo, out launchedProcess, out failure);
    }

    private static string BuildArgs(
        string sessionId, string? shellType, string? workingDirectory, int cols, int rows,
        string instanceId, string ownerToken, int scrollbackBytes,
        int? mtPort, string? mtToken, int? paneIndex, string? tmuxBinDir)
    {
        var args = string.Create(CultureInfo.InvariantCulture, $"--session {sessionId} --cols {cols} --rows {rows} --scrollback {scrollbackBytes} --mt-instance-id {instanceId} --mt-owner-token {ownerToken}");
        if (!string.IsNullOrEmpty(shellType))
        {
            args += $" --shell {shellType}";
        }
        if (!string.IsNullOrEmpty(workingDirectory))
        {
            args += $" --cwd \"{workingDirectory}\"";
        }
        if (mtPort.HasValue)
        {
            args += string.Create(CultureInfo.InvariantCulture, $" --mt-port {mtPort.Value}");
        }
        if (!string.IsNullOrEmpty(mtToken))
        {
            args += $" --mt-token {mtToken}";
        }
        if (paneIndex.HasValue)
        {
            args += string.Create(CultureInfo.InvariantCulture, $" --pane-index {paneIndex.Value}");
        }
        if (!string.IsNullOrEmpty(tmuxBinDir))
        {
            args += $" --tmux-bin-dir \"{tmuxBinDir}\"";
        }
        return args;
    }

    private static ProcessStartInfo CreateRedirectedProcessStartInfo(
        string fileName,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environmentOverrides,
        IReadOnlyList<string>? pathPrependEntries)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        ApplyEnvironmentOverrides(startInfo.Environment, environmentOverrides);
        ApplyPathPrependEntries(startInfo.Environment, pathPrependEntries);
        return startInfo;
    }

    private static bool TryStartRedirectedProcessDirect(
        ProcessStartInfo startInfo,
        out RedirectedProcessHandle? launchedProcess,
        out string? failure)
    {
        return RedirectedProcessHandle.TryStartOwnedDirect(startInfo, out launchedProcess, out failure);
    }

    private static void ApplyEnvironmentOverrides(
        IDictionary<string, string?> environment,
        IReadOnlyDictionary<string, string?>? environmentOverrides)
    {
        if (environmentOverrides is null)
        {
            return;
        }

        foreach (var (key, value) in environmentOverrides)
        {
            if (string.IsNullOrWhiteSpace(key))
            {
                continue;
            }

            if (value is null)
            {
                environment.Remove(key);
            }
            else
            {
                environment[key] = value;
            }
        }
    }

    private static void ApplyPathPrependEntries(
        IDictionary<string, string?> environment,
        IReadOnlyList<string>? pathPrependEntries)
    {
        if (pathPrependEntries is not { Count: > 0 })
        {
            return;
        }

        var existingPath = environment.TryGetValue("PATH", out var currentPath)
            ? currentPath ?? string.Empty
            : Environment.GetEnvironmentVariable("PATH") ?? string.Empty;

        foreach (var directory in pathPrependEntries)
        {
            PrependPath(environment, directory, existingPath);
            existingPath = environment.TryGetValue("PATH", out currentPath)
                ? currentPath ?? string.Empty
                : string.Empty;
        }
    }

    private static void PrependPath(
        IDictionary<string, string?> environment,
        string? directory,
        string existingPath)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
        {
            return;
        }

        var parts = existingPath
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Contains(directory, StringComparer.OrdinalIgnoreCase))
        {
            return;
        }

        environment["PATH"] = string.IsNullOrWhiteSpace(existingPath)
            ? directory
            : directory + Path.PathSeparator + existingPath;
    }

    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP003:Dispose previous before re-assigning", Justification = "RedirectedProcessHandle is the dedicated ownership wrapper for the launched process transport and intentionally transitions ownership state during attach and detach.")]
    [SuppressMessage("IDisposableAnalyzers.Correctness", "IDISP007:Don't dispose injected", Justification = "Ownership of the redirected process and stream handles is explicitly transferred into RedirectedProcessHandle when it is created.")]
    internal sealed class RedirectedProcessHandle : IDisposable
    {
        private readonly Process _process;
        private readonly StreamWriter _input;
        private readonly StreamReader _output;
        private readonly StreamReader _error;
        private bool _processDetached;
        private bool _inputDetached;
        private bool _outputDetached;
        private bool _errorDetached;
        private bool _disposed;

        private RedirectedProcessHandle(ProcessStartInfo startInfo)
        {
            _process = new Process
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            if (!_process.Start())
            {
                throw new InvalidOperationException("Process.Start returned false.");
            }

            ApplyRuntimePriority(_process.Id, "mtagenthost");

#if !WINDOWS
            // Detached redirected helpers are also used for persistent sidecars such as
            // mtagenthost, so they need their own process group to survive mt restarts.
            if (setpgid(_process.Id, 0) != 0)
            {
                var redirectedProcessId = _process.Id;
                var errno = Marshal.GetLastPInvokeError();
                Log.Warn(() => string.Create(
                    CultureInfo.InvariantCulture,
                    $"TtyHostSpawner: setpgid failed for redirected PID {redirectedProcessId} (errno: {errno})"));
            }
#endif

            _input = _process.StandardInput;
            _output = _process.StandardOutput;
            _error = _process.StandardError;
        }

        private RedirectedProcessHandle(
            int processId,
            Microsoft.Win32.SafeHandles.SafeFileHandle stdinSafe,
            Microsoft.Win32.SafeHandles.SafeFileHandle stdoutSafe,
            Microsoft.Win32.SafeHandles.SafeFileHandle stderrSafe)
        {
            _process = Process.GetProcessById(processId);
            _process.EnableRaisingEvents = true;
            ApplyRuntimePriority(processId, "mtagenthost");
            _input = new StreamWriter(new FileStream(stdinSafe, FileAccess.Write), new UTF8Encoding(false)) { AutoFlush = true };
            _output = new StreamReader(new FileStream(stdoutSafe, FileAccess.Read), Encoding.UTF8);
            _error = new StreamReader(new FileStream(stderrSafe, FileAccess.Read), Encoding.UTF8);
        }

        public static bool TryStartOwnedDirect(
            ProcessStartInfo startInfo,
            out RedirectedProcessHandle? launchedProcess,
            out string? failure)
        {
            try
            {
                launchedProcess = new RedirectedProcessHandle(startInfo);
                failure = null;
                return true;
            }
            catch (Exception ex)
            {
                launchedProcess = null;
                failure = ex.Message;
                return false;
            }
        }

        public static RedirectedProcessHandle CreateOwnedFromPipeHandles(
            int processId,
            ref Microsoft.Win32.SafeHandles.SafeFileHandle? stdinSafe,
            ref Microsoft.Win32.SafeHandles.SafeFileHandle? stdoutSafe,
            ref Microsoft.Win32.SafeHandles.SafeFileHandle? stderrSafe)
        {
            var transferredStdin = stdinSafe ?? throw new InvalidOperationException("Redirected process input handle was not created.");
            var transferredStdout = stdoutSafe ?? throw new InvalidOperationException("Redirected process output handle was not created.");
            var transferredStderr = stderrSafe ?? throw new InvalidOperationException("Redirected process error handle was not created.");
            stdinSafe = null;
            stdoutSafe = null;
            stderrSafe = null;

            try
            {
                return new RedirectedProcessHandle(processId, transferredStdin, transferredStdout, transferredStderr);
            }
            catch
            {
                transferredStderr?.Dispose();
                transferredStdout?.Dispose();
                transferredStdin?.Dispose();
                throw;
            }
        }

        public Process Process => !_disposed && !_processDetached ? _process : throw new ObjectDisposedException(nameof(RedirectedProcessHandle));
        public StreamWriter Input => !_disposed && !_inputDetached ? _input : throw new ObjectDisposedException(nameof(RedirectedProcessHandle));
        public StreamReader Output => !_disposed && !_outputDetached ? _output : throw new ObjectDisposedException(nameof(RedirectedProcessHandle));
        public StreamReader Error => !_disposed && !_errorDetached ? _error : throw new ObjectDisposedException(nameof(RedirectedProcessHandle));

        public (Process Process, StreamReader Error) DetachForIpc()
        {
            if (_disposed || _processDetached || _errorDetached)
            {
                throw new ObjectDisposedException(nameof(RedirectedProcessHandle));
            }

            if (!_inputDetached)
            {
                try { _input.Dispose(); } catch { }
                _inputDetached = true;
            }

            if (!_outputDetached)
            {
                try { _output.Dispose(); } catch { }
                _outputDetached = true;
            }

            _processDetached = true;
            return (_process, _error);
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            if (!_inputDetached)
            {
                try { _input.Dispose(); } catch { }
                _inputDetached = true;
            }

            if (!_outputDetached)
            {
                try { _output.Dispose(); } catch { }
                _outputDetached = true;
            }

            if (!_errorDetached)
            {
                try { _error.Dispose(); } catch { }
                _errorDetached = true;
            }

            if (!_processDetached)
            {
                try { _process.Dispose(); } catch { }
                _processDetached = true;
            }
        }
    }

#if !WINDOWS
    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint geteuid();

    [DllImport("libc", EntryPoint = "setpgid")]
    private static extern int setpgid(int pid, int pgid);

    private static void CleanupMacOsGuiLaunchAgentCore(string sessionId)
    {
        if (!OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            var uid = geteuid();
            if (uid == 0)
            {
                return;
            }

            var label = GetMacOsLaunchAgentLabel(sessionId);
            _ = RunProcessSync("launchctl", ["bootout", $"gui/{uid}/{label}"], out _, out _, logFailures: false);

            var tempDir = GetMacOsLaunchAgentTempDirectory();
            File.Delete(Path.Combine(tempDir, $"{label}.plist"));
            File.Delete(Path.Combine(tempDir, $"{label}.stdout.log"));
            File.Delete(Path.Combine(tempDir, $"{label}.stderr.log"));
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"TtyHostSpawner: Failed to cleanup macOS launch agent for session {sessionId}: {ex.Message}");
        }
    }

    private static TtyHostSpawnResult SpawnUnix(
        string sessionId,
        string args,
        string? runAsUser,
        IReadOnlyDictionary<string, string?>? environmentOverrides)
    {
        var processId = 0;

        try
        {
            if (OperatingSystem.IsMacOS() &&
                TrySpawnMacOsViaLaunchAgent(sessionId, args, environmentOverrides, out processId))
            {
                ApplyRuntimePriority(processId, "mthost");
                return TtyHostSpawnResult.Success(processId);
            }

            var ttyHostArgs = ParseUnixArgs(args);
            ProcessStartInfo psi;

            // If running as root and runAsUser is configured, use sudo -u to drop privileges
            var isRoot = geteuid() == 0;

            if (isRoot && !string.IsNullOrEmpty(runAsUser))
            {
                // SECURITY: Defensive re-validation before sudo command
                if (!UserValidationService.IsValidUsernameFormat(runAsUser))
                {
                    Log.Error(() => $"TtyHostSpawner SECURITY: Rejected invalid username format: {runAsUser}");
                    return TtyHostSpawnResult.Failed(
                        $"Refused to spawn mthost for invalid username '{runAsUser}'.",
                        detail: "The configured run-as username did not pass validation.");
                }

                psi = new ProcessStartInfo
                {
                    FileName = "sudo",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false
                };
                psi.ArgumentList.Add("-H");
                psi.ArgumentList.Add("-u");
                psi.ArgumentList.Add(runAsUser);
                if (environmentOverrides is { Count: > 0 })
                {
                    psi.ArgumentList.Add("env");
                    foreach (var (key, value) in environmentOverrides)
                    {
                        if (string.IsNullOrWhiteSpace(key) || value is null)
                        {
                            continue;
                        }

                        psi.ArgumentList.Add($"{key}={value}");
                    }
                }
                psi.ArgumentList.Add(TtyHostPath);
                foreach (var argument in ttyHostArgs)
                {
                    psi.ArgumentList.Add(argument);
                }
                Log.Info(() => $"TtyHostSpawner: Spawning as user: {runAsUser}");
            }
            else
            {
                psi = new ProcessStartInfo
                {
                    FileName = TtyHostPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = false,
                    RedirectStandardOutput = false,
                    RedirectStandardError = false
                };
                foreach (var argument in ttyHostArgs)
                {
                    psi.ArgumentList.Add(argument);
                }
            }

            var mtBinaryPath = GetMidTermBinaryPath();
            if (!string.IsNullOrWhiteSpace(mtBinaryPath))
            {
                psi.Environment[MtBinaryPathEnvironmentVariable] = mtBinaryPath;
            }
            ApplyEnvironmentOverrides(psi.Environment, environmentOverrides);

            using var process = Process.Start(psi);
            if (process is null)
            {
                const string message = "Process.Start returned null";
                Log.Error(() => $"TtyHostSpawner: {message}");
                return TtyHostSpawnResult.Failed(
                    "Failed to launch the mthost process.",
                    detail: "Process.Start returned null.");
            }

            var startedProcessId = process.Id;
            processId = startedProcessId;
            ApplyRuntimePriority(processId, "mthost");

            // Move mthost into its own process group so it survives mt restarts.
            // When launchd kills mt, it sends SIGTERM to mt's process group.
            // Without this, mthosts inherit mt's PGID and die with it.
            if (setpgid(startedProcessId, 0) != 0)
            {
                var errno = Marshal.GetLastPInvokeError();
                Log.Warn(() => string.Create(
                    CultureInfo.InvariantCulture,
                    $"TtyHostSpawner: setpgid failed for PID {startedProcessId} (errno: {errno})"));
            }

            var processIdForLog = startedProcessId;
            if (isRoot && !string.IsNullOrEmpty(runAsUser))
            {
                Log.Info(() => string.Create(CultureInfo.InvariantCulture, $"TtyHostSpawner: Spawned via sudo (PID: {processIdForLog} is sudo, not mthost). Socket discovery will use glob pattern."));
            }
            else
            {
                Log.Info(() => string.Create(CultureInfo.InvariantCulture, $"TtyHostSpawner: Spawned mthost (PID: {processIdForLog})"));
            }
            return TtyHostSpawnResult.Success(processId);
        }
        catch (Exception ex)
        {
            Log.Error(() => $"TtyHostSpawner: Failed to spawn: {ex.Message}");
            return TtyHostSpawnResult.Failed(
                "Failed to launch the mthost process.",
                detail: ex.ToString(),
                exceptionType: ex.GetType().Name);
        }
    }

    private static bool TrySpawnMacOsViaLaunchAgent(
        string sessionId,
        string args,
        IReadOnlyDictionary<string, string?>? environmentOverrides,
        out int processId)
    {
        processId = 0;

        var uid = geteuid();
        if (uid == 0)
        {
            // Root-owned mt on macOS should use existing sudo-based spawn path.
            return false;
        }

        var label = GetMacOsLaunchAgentLabel(sessionId);
        var tempDir = GetMacOsLaunchAgentTempDirectory();
        Directory.CreateDirectory(tempDir);

        var plistPath = Path.Combine(tempDir, $"{label}.plist");
        var stdoutPath = Path.Combine(tempDir, $"{label}.stdout.log");
        var stderrPath = Path.Combine(tempDir, $"{label}.stderr.log");

        var programArguments = new List<string>(ParseUnixArgs(args));
        programArguments.Insert(0, TtyHostPath);

        var plistContent = BuildMacOsLaunchAgentPlist(
            label,
            programArguments,
            stdoutPath,
            stderrPath,
            GetMidTermBinaryPath(),
            environmentOverrides);
        File.WriteAllText(plistPath, plistContent, Encoding.UTF8);

        _ = RunProcessSync("launchctl", ["bootout", $"gui/{uid}/{label}"], out _, out _, logFailures: false);

        if (!RunProcessSync("launchctl", ["bootstrap", $"gui/{uid}", plistPath], out _, out var bootstrapErr, logFailures: false))
        {
            if (!string.IsNullOrWhiteSpace(bootstrapErr))
            {
                Log.Warn(() => $"TtyHostSpawner: macOS GUI bootstrap unavailable for session {sessionId}: {bootstrapErr.Trim()}");
            }
            return false;
        }

        var startedPid = TryGetMacOsLaunchAgentPid(uid, label, timeoutMs: 2000);
        if (startedPid.HasValue)
        {
            processId = startedPid.Value;
            var launchedPid = processId;
            Log.Info(() => string.Create(
                CultureInfo.InvariantCulture,
                $"TtyHostSpawner: Spawned mthost via macOS GUI LaunchAgent (session {sessionId}, PID {launchedPid})"));
        }
        else
        {
            Log.Info(() => $"TtyHostSpawner: Spawned mthost via macOS GUI LaunchAgent (session {sessionId}, PID pending)");
        }

        return true;
    }

    private static int? TryGetMacOsLaunchAgentPid(uint uid, string label, int timeoutMs)
    {
        return PollForValue<int>(
            TimeSpan.FromMilliseconds(timeoutMs),
            TimeSpan.FromMilliseconds(100),
            () =>
            {
                if (RunProcessSync("launchctl", ["print", $"gui/{uid}/{label}"], out var stdout, out _, logFailures: false))
                {
                    var match = MacOsPidRegex.Match(stdout);
                    if (match.Success &&
                        int.TryParse(match.Groups["pid"].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var pid) &&
                        pid > 0)
                    {
                        return pid;
                    }
                }

                return null;
            });
    }

    internal static T? PollForValue<T>(TimeSpan timeout, TimeSpan pollInterval, Func<T?> poll)
        where T : struct
    {
        ArgumentNullException.ThrowIfNull(poll);

        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var value = poll();
            if (value.HasValue)
            {
                return value.Value;
            }

            Thread.Sleep(pollInterval);
        }

        return null;
    }

    private static string GetMacOsLaunchAgentLabel(string sessionId)
    {
        return $"{MacOsLaunchAgentLabelPrefix}{sessionId}";
    }

    private static string GetMacOsLaunchAgentTempDirectory()
    {
        return Path.Combine(Path.GetTempPath(), "midterm-launchagents");
    }

    private static string BuildMacOsLaunchAgentPlist(
        string label,
        IReadOnlyList<string> programArguments,
        string stdoutPath,
        string stderrPath,
        string? mtBinaryPath = null,
        IReadOnlyDictionary<string, string?>? environmentOverrides = null)
    {
        var argsBuilder = new StringBuilder();
        foreach (var argument in programArguments)
        {
            argsBuilder.Append("        <string>");
            argsBuilder.Append(EscapeXml(argument));
            argsBuilder.AppendLine("</string>");
        }

        var pathVar = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathVar))
        {
            pathVar = AiCliCommandLocator.BuildFallbackPath();
        }

        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["PATH"] = pathVar
        };
        if (!string.IsNullOrWhiteSpace(mtBinaryPath))
        {
            environment[MtBinaryPathEnvironmentVariable] = mtBinaryPath;
        }
        ApplyEnvironmentOverrides(environment, environmentOverrides);

        var environmentBuilder = new StringBuilder();
        foreach (var (key, value) in environment)
        {
            if (string.IsNullOrWhiteSpace(key) || value is null)
            {
                continue;
            }

            environmentBuilder.Append("        <key>");
            environmentBuilder.Append(EscapeXml(key));
            environmentBuilder.AppendLine("</key>");
            environmentBuilder.Append("        <string>");
            environmentBuilder.Append(EscapeXml(value));
            environmentBuilder.AppendLine("</string>");
        }

        return $$"""
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>{{EscapeXml(label)}}</string>
    <key>ProgramArguments</key>
    <array>
{{argsBuilder}}    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>AbandonProcessGroup</key>
    <true/>
    <key>StandardOutPath</key>
    <string>{{EscapeXml(stdoutPath)}}</string>
    <key>StandardErrorPath</key>
    <string>{{EscapeXml(stderrPath)}}</string>
    <key>EnvironmentVariables</key>
    <dict>
{{environmentBuilder}}    </dict>
</dict>
</plist>
""";
    }

    private static IReadOnlyList<string> ParseUnixArgs(string args)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;

        foreach (var c in args)
        {
            if (c == '"')
            {
                inQuotes = !inQuotes;
                continue;
            }

            if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (current.Length > 0)
                {
                    parts.Add(current.ToString());
                    current.Clear();
                }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0)
        {
            parts.Add(current.ToString());
        }

        return parts;
    }

    private static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }

    private static bool RunProcessSync(
        string fileName,
        IReadOnlyList<string> arguments,
        out string stdout,
        out string stderr,
        bool logFailures = true)
    {
        stdout = string.Empty;
        stderr = string.Empty;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                }
            };

            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();

            if (!process.WaitForExit(5000))
            {
                try { process.Kill(); } catch { }
                if (logFailures)
                {
                    Log.Warn(() => $"TtyHostSpawner: Command timed out ({fileName})");
                }
                return false;
            }

            stdout = process.StandardOutput.ReadToEnd();
            stderr = process.StandardError.ReadToEnd();

            if (process.ExitCode == 0)
            {
                return true;
            }

            if (logFailures)
            {
                var stderrTrimmed = stderr.Trim();
                var exitCode = process.ExitCode;
                Log.Warn(() => string.Create(
                    CultureInfo.InvariantCulture,
                    $"TtyHostSpawner: Command failed ({fileName}, exit {exitCode}): {stderrTrimmed}"));
            }

            return false;
        }
        catch (Exception ex)
        {
            if (logFailures)
            {
                Log.Warn(() => $"TtyHostSpawner: Command failed ({fileName}): {ex.Message}");
            }
            return false;
        }
    }
#endif

    private static void ApplyRuntimePriority(int processId, string role)
    {
        _ = MidTermProcessPriority.TryApplyToProcessId(
            processId,
            role,
            message => Log.Info(() => message),
            message => Log.Warn(() => message));
    }

#if WINDOWS
    [SupportedOSPlatform("windows")]
    private static TtyHostSpawnResult SpawnWindows(
        string args,
        string? runAsUser,
        string? runAsUserSid,
        IReadOnlyDictionary<string, string?>? environmentOverrides)
    {
        var commandLine = $"\"{TtyHostPath}\" {args}";

        if (IsRunningAsSystem())
        {
            return SpawnAsUser(commandLine, runAsUser, runAsUserSid, environmentOverrides);
        }
        else
        {
            return SpawnDirect(commandLine, environmentOverrides);
        }
    }

    [SupportedOSPlatform("windows")]
    private static TtyHostSpawnResult SpawnDirect(
        string commandLine,
        IReadOnlyDictionary<string, string?>? environmentOverrides)
    {
        var si = new STARTUPINFO();
        si.cb = Marshal.SizeOf<STARTUPINFO>();
        IntPtr environmentBlock = IntPtr.Zero;

        try
        {
            if (environmentOverrides is { Count: > 0 })
            {
                environmentBlock = BuildWindowsEnvironmentBlock(environmentOverrides, pathPrependEntries: null);
            }

            var success = CreateProcess(
                null,
                commandLine,
                IntPtr.Zero,
                IntPtr.Zero,
                false,
                CREATE_NO_WINDOW | (environmentBlock != IntPtr.Zero ? CREATE_UNICODE_ENVIRONMENT : 0),
                environmentBlock == IntPtr.Zero ? IntPtr.Zero : environmentBlock,
                null,
                ref si,
                out var pi);

            if (!success)
            {
                var errorCode = Marshal.GetLastWin32Error();
                var detail = new Win32Exception(errorCode).Message;
                Log.Error(() => string.Create(CultureInfo.InvariantCulture, $"TtyHostSpawner: CreateProcess failed: {errorCode} ({detail})"));
                return TtyHostSpawnResult.Failed(
                    "Windows blocked the mthost process launch.",
                    detail: string.Create(CultureInfo.InvariantCulture, $"CreateProcess failed with Win32 error {errorCode}: {detail}"),
                    exceptionType: nameof(Win32Exception),
                    nativeErrorCode: errorCode);
            }

            var processId = pi.dwProcessId;
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);

            var pid = processId;
            ApplyRuntimePriority(pid, "mthost");
            Log.Info(() => string.Create(CultureInfo.InvariantCulture, $"TtyHostSpawner: Spawned mthost (PID: {pid})"));
            return TtyHostSpawnResult.Success(processId);
        }
        finally
        {
            if (environmentBlock != IntPtr.Zero)
            {
                Marshal.FreeHGlobal(environmentBlock);
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static TtyHostSpawnResult SpawnAsUser(
        string commandLine,
        string? runAsUser,
        string? runAsUserSid,
        IReadOnlyDictionary<string, string?>? environmentOverrides)
    {
        if (!TryGetUserToken(runAsUser, runAsUserSid, out var userToken, out var sessionId))
        {
            return TtyHostSpawnResult.Failed(
                "Failed to acquire a Windows user token for launching mthost.",
                detail: $"Could not acquire a token for '{runAsUser ?? runAsUserSid ?? "active session"}'.");
        }

        try
        {
            if (!CreateEnvironmentBlock(out var envBlock, userToken, false))
            {
                var errorCode = Marshal.GetLastWin32Error();
                var detail = new Win32Exception(errorCode).Message;
                Log.Error(() => string.Create(CultureInfo.InvariantCulture, $"TtyHostSpawner: CreateEnvironmentBlock failed: {errorCode} ({detail})"));
                return TtyHostSpawnResult.Failed(
                    "Failed to prepare the Windows environment block for mthost.",
                    detail: string.Create(CultureInfo.InvariantCulture, $"CreateEnvironmentBlock failed with Win32 error {errorCode}: {detail}"),
                    exceptionType: nameof(Win32Exception),
                    nativeErrorCode: errorCode);
            }

            try
            {
                var mergedEnvironmentBlock = environmentOverrides is { Count: > 0 }
                    ? BuildWindowsEnvironmentBlock(envBlock, environmentOverrides, pathPrependEntries: null)
                    : envBlock;
                var si = new STARTUPINFO();
                si.cb = Marshal.SizeOf<STARTUPINFO>();
                si.lpDesktop = Marshal.StringToHGlobalUni("winsta0\\default");
                si.dwFlags = STARTF_USESHOWWINDOW;
                si.wShowWindow = SW_HIDE;

                try
                {
                    var success = CreateProcessAsUser(
                        userToken,
                        null,
                        commandLine,
                        IntPtr.Zero,
                        IntPtr.Zero,
                        false,
                        CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                        mergedEnvironmentBlock,
                        null,
                        ref si,
                        out var pi);

                    if (!success)
                    {
                        var errorCode = Marshal.GetLastWin32Error();
                        var detail = new Win32Exception(errorCode).Message;
                        Log.Error(() => string.Create(CultureInfo.InvariantCulture, $"TtyHostSpawner: CreateProcessAsUser failed: {errorCode} ({detail})"));
                        return TtyHostSpawnResult.Failed(
                            "Windows blocked the mthost process launch for the target user.",
                            detail: string.Create(CultureInfo.InvariantCulture, $"CreateProcessAsUser failed with Win32 error {errorCode}: {detail}"),
                            exceptionType: nameof(Win32Exception),
                            nativeErrorCode: errorCode);
                    }

                    var processId = pi.dwProcessId;
                    CloseHandle(pi.hThread);
                    CloseHandle(pi.hProcess);

                    var pid = processId;
                    var sess = sessionId;
                    ApplyRuntimePriority(pid, "mthost");
                    Log.Info(() => string.Create(CultureInfo.InvariantCulture, $"TtyHostSpawner: Spawned mthost as user (PID: {pid}, Session: {sess})"));
                    return TtyHostSpawnResult.Success(processId);
                }
                finally
                {
                    if (mergedEnvironmentBlock != envBlock)
                    {
                        Marshal.FreeHGlobal(mergedEnvironmentBlock);
                    }

                    Marshal.FreeHGlobal(si.lpDesktop);
                }
            }
            finally
            {
                DestroyEnvironmentBlock(envBlock);
            }
        }
        finally
        {
            CloseHandle(userToken);
        }
    }

    [SupportedOSPlatform("windows")]
    internal static bool IsRunningAsSystem()
    {
        try
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return identity.IsSystem;
        }
        catch
        {
            return false;
        }
    }

    [SupportedOSPlatform("windows")]
    internal static async Task<(int ExitCode, string Stdout, string Stderr)> RunCommandAsUserAsync(
        string fileName,
        IReadOnlyList<string> args,
        string workingDir,
        string? runAsUser,
        CancellationToken ct,
        uint? preferredSessionId = null,
        string? runAsUserSid = null)
    {
        if (preferredSessionId.HasValue)
        {
            if (WTSQueryUserToken(preferredSessionId.Value, out var preferredToken))
            {
                try
                {
                    return await RunCommandWithTokenAsync(fileName, args, workingDir, preferredToken, ct);
                }
                finally
                {
                    CloseHandle(preferredToken);
                }
            }

            Log.Warn(() => string.Create(CultureInfo.InvariantCulture, $"TtyHostSpawner: Failed to get user token for preferred session {preferredSessionId.Value} (error: {Marshal.GetLastWin32Error()})"));
        }

        if (!TryGetUserToken(runAsUser, runAsUserSid, out var userToken, out _))
        {
            return (-1, "", "Failed to get user token for impersonation");
        }

        try
        {
            return await RunCommandWithTokenAsync(fileName, args, workingDir, userToken, ct);
        }
        finally
        {
            CloseHandle(userToken);
        }
    }

    [SupportedOSPlatform("windows")]
    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunCommandWithTokenAsync(
        string fileName, IReadOnlyList<string> args, string workingDir, IntPtr userToken, CancellationToken ct)
    {
        var sa = new SECURITY_ATTRIBUTES
        {
            bInheritHandle = true
        };
        sa.nLength = Marshal.SizeOf(sa);

        if (!CreatePipe(out var stdoutRead, out var stdoutWrite, ref sa, 0))
            return (-1, "", string.Create(CultureInfo.InvariantCulture, $"CreatePipe stdout failed: {Marshal.GetLastWin32Error()}"));
        if (!CreatePipe(out var stderrRead, out var stderrWrite, ref sa, 0))
        {
            CloseHandle(stdoutRead);
            CloseHandle(stdoutWrite);
            return (-1, "", string.Create(CultureInfo.InvariantCulture, $"CreatePipe stderr failed: {Marshal.GetLastWin32Error()}"));
        }

        SetHandleInformation(stdoutRead, HANDLE_FLAG_INHERIT, 0);
        SetHandleInformation(stderrRead, HANDLE_FLAG_INHERIT, 0);

        if (!CreateEnvironmentBlock(out var envBlock, userToken, false))
        {
            CloseHandle(stdoutRead); CloseHandle(stdoutWrite);
            CloseHandle(stderrRead); CloseHandle(stderrWrite);
            return (-1, "", string.Create(CultureInfo.InvariantCulture, $"CreateEnvironmentBlock failed: {Marshal.GetLastWin32Error()}"));
        }

        try
        {
            var cmdLine = BuildCommandLine(fileName, args);

            var si = new STARTUPINFO();
            si.cb = Marshal.SizeOf<STARTUPINFO>();
            si.lpDesktop = Marshal.StringToHGlobalUni("winsta0\\default");
            si.dwFlags = STARTF_USESHOWWINDOW | STARTF_USESTDHANDLES;
            si.wShowWindow = SW_HIDE;
            si.hStdInput = IntPtr.Zero;
            si.hStdOutput = stdoutWrite;
            si.hStdError = stderrWrite;

            try
            {
                var success = CreateProcessAsUser(
                    userToken,
                    null,
                    cmdLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    true,
                    CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                    envBlock,
                    workingDir,
                    ref si,
                    out var pi);

                if (!success)
                {
                    return (-1, "", string.Create(CultureInfo.InvariantCulture, $"CreateProcessAsUser failed: {Marshal.GetLastWin32Error()}"));
                }

                CloseHandle(pi.hThread);
                CloseHandle(stdoutWrite); stdoutWrite = IntPtr.Zero;
                CloseHandle(stderrWrite); stderrWrite = IntPtr.Zero;

                using var stdoutSafe = new Microsoft.Win32.SafeHandles.SafeFileHandle(stdoutRead, true);
                using var stderrSafe = new Microsoft.Win32.SafeHandles.SafeFileHandle(stderrRead, true);
                stdoutRead = IntPtr.Zero;
                stderrRead = IntPtr.Zero;

                using var stdoutStream = new FileStream(stdoutSafe, FileAccess.Read);
                using var stderrStream = new FileStream(stderrSafe, FileAccess.Read);
                using var stdoutReader = new StreamReader(stdoutStream, Encoding.UTF8);
                using var stderrReader = new StreamReader(stderrStream, Encoding.UTF8);

                var stdoutTask = stdoutReader.ReadToEndAsync(ct);
                var stderrTask = stderrReader.ReadToEndAsync(ct);

                using var processSafe = new Microsoft.Win32.SafeHandles.SafeProcessHandle(pi.hProcess, true);
                using var process = Process.GetProcessById(pi.dwProcessId);
                await process.WaitForExitAsync(ct);

                var stdout = await stdoutTask;
                var stderr = await stderrTask;

                return (process.ExitCode, stdout, stderr);
            }
            finally
            {
                Marshal.FreeHGlobal(si.lpDesktop);
            }
        }
        finally
        {
            DestroyEnvironmentBlock(envBlock);
            if (stdoutRead != IntPtr.Zero) CloseHandle(stdoutRead);
            if (stdoutWrite != IntPtr.Zero) CloseHandle(stdoutWrite);
            if (stderrRead != IntPtr.Zero) CloseHandle(stderrRead);
            if (stderrWrite != IntPtr.Zero) CloseHandle(stderrWrite);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryStartRedirectedProcessAsUserWindows(
        string fileName,
        IReadOnlyList<string> args,
        string workingDirectory,
        IReadOnlyDictionary<string, string?>? environmentOverrides,
        IReadOnlyList<string>? pathPrependEntries,
        string? runAsUser,
        string? runAsUserSid,
        out RedirectedProcessHandle? launchedProcess,
        out string? failure)
    {
        launchedProcess = null;
        failure = null;

        if (!TryGetUserToken(runAsUser, runAsUserSid, out var userToken, out _))
        {
            failure = "Failed to get user token for impersonation";
            return false;
        }

        IntPtr stdinRead = IntPtr.Zero;
        IntPtr stdinWrite = IntPtr.Zero;
        IntPtr stdoutRead = IntPtr.Zero;
        IntPtr stdoutWrite = IntPtr.Zero;
        IntPtr stderrRead = IntPtr.Zero;
        IntPtr stderrWrite = IntPtr.Zero;

        try
        {
            var sa = new SECURITY_ATTRIBUTES
            {
                bInheritHandle = true
            };
            sa.nLength = Marshal.SizeOf(sa);

            if (!CreatePipe(out stdinRead, out stdinWrite, ref sa, 0))
            {
                failure = string.Create(CultureInfo.InvariantCulture, $"CreatePipe stdin failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            if (!CreatePipe(out stdoutRead, out stdoutWrite, ref sa, 0))
            {
                failure = string.Create(CultureInfo.InvariantCulture, $"CreatePipe stdout failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            if (!CreatePipe(out stderrRead, out stderrWrite, ref sa, 0))
            {
                failure = string.Create(CultureInfo.InvariantCulture, $"CreatePipe stderr failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            SetHandleInformation(stdinWrite, HANDLE_FLAG_INHERIT, 0);
            SetHandleInformation(stdoutRead, HANDLE_FLAG_INHERIT, 0);
            SetHandleInformation(stderrRead, HANDLE_FLAG_INHERIT, 0);

            if (!CreateEnvironmentBlock(out var baseEnvironmentBlock, userToken, false))
            {
                failure = string.Create(CultureInfo.InvariantCulture, $"CreateEnvironmentBlock failed: {Marshal.GetLastWin32Error()}");
                return false;
            }

            try
            {
                var mergedEnvironmentBlock = BuildWindowsEnvironmentBlock(
                    baseEnvironmentBlock,
                    environmentOverrides,
                    pathPrependEntries);

                try
                {
                    var commandLine = BuildCommandLine(fileName, args);
                    var si = new STARTUPINFO();
                    si.cb = Marshal.SizeOf<STARTUPINFO>();
                    si.lpDesktop = Marshal.StringToHGlobalUni("winsta0\\default");
                    si.dwFlags = STARTF_USESHOWWINDOW | STARTF_USESTDHANDLES;
                    si.wShowWindow = SW_HIDE;
                    si.hStdInput = stdinRead;
                    si.hStdOutput = stdoutWrite;
                    si.hStdError = stderrWrite;

                    try
                    {
                        var success = CreateProcessAsUser(
                            userToken,
                            null,
                            commandLine,
                            IntPtr.Zero,
                            IntPtr.Zero,
                            true,
                            CREATE_UNICODE_ENVIRONMENT | CREATE_NO_WINDOW,
                            mergedEnvironmentBlock,
                            workingDirectory,
                            ref si,
                            out var pi);

                        if (!success)
                        {
                            failure = string.Create(CultureInfo.InvariantCulture, $"CreateProcessAsUser failed: {Marshal.GetLastWin32Error()}");
                            return false;
                        }

                        CloseHandle(pi.hThread);
                        CloseHandle(stdinRead); stdinRead = IntPtr.Zero;
                        CloseHandle(stdoutWrite); stdoutWrite = IntPtr.Zero;
                        CloseHandle(stderrWrite); stderrWrite = IntPtr.Zero;

                        CloseHandle(pi.hProcess);

                        Microsoft.Win32.SafeHandles.SafeFileHandle? stdinSafe = new(stdinWrite, ownsHandle: true);
                        Microsoft.Win32.SafeHandles.SafeFileHandle? stdoutSafe = new(stdoutRead, ownsHandle: true);
                        Microsoft.Win32.SafeHandles.SafeFileHandle? stderrSafe = new(stderrRead, ownsHandle: true);
                        stdinWrite = IntPtr.Zero;
                        stdoutRead = IntPtr.Zero;
                        stderrRead = IntPtr.Zero;

                        try
                        {
                            launchedProcess = RedirectedProcessHandle.CreateOwnedFromPipeHandles(
                                pi.dwProcessId,
                                ref stdinSafe,
                                ref stdoutSafe,
                                ref stderrSafe);
                        }
                        finally
                        {
                            stderrSafe?.Dispose();
                            stdoutSafe?.Dispose();
                            stdinSafe?.Dispose();
                        }
                        return true;
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(si.lpDesktop);
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(mergedEnvironmentBlock);
                }
            }
            finally
            {
                DestroyEnvironmentBlock(baseEnvironmentBlock);
            }
        }
        finally
        {
            CloseHandle(userToken);
            if (stdinRead != IntPtr.Zero) CloseHandle(stdinRead);
            if (stdinWrite != IntPtr.Zero) CloseHandle(stdinWrite);
            if (stdoutRead != IntPtr.Zero) CloseHandle(stdoutRead);
            if (stdoutWrite != IntPtr.Zero) CloseHandle(stdoutWrite);
            if (stderrRead != IntPtr.Zero) CloseHandle(stderrRead);
            if (stderrWrite != IntPtr.Zero) CloseHandle(stderrWrite);
        }
    }

    [SupportedOSPlatform("windows")]
    private static IntPtr BuildWindowsEnvironmentBlock(
        IReadOnlyDictionary<string, string?>? environmentOverrides,
        IReadOnlyList<string>? pathPrependEntries)
    {
        var environment = Environment.GetEnvironmentVariables()
            .Cast<System.Collections.DictionaryEntry>()
            .Where(static entry => entry.Key is string)
            .ToDictionary(
                static entry => (string)entry.Key,
                static entry => entry.Value?.ToString(),
                StringComparer.OrdinalIgnoreCase);

        ApplyEnvironmentOverrides(environment, environmentOverrides);
        ApplyPathPrependEntries(environment, pathPrependEntries);
        return SerializeWindowsEnvironmentBlock(environment);
    }

    [SupportedOSPlatform("windows")]
    private static IntPtr BuildWindowsEnvironmentBlock(
        IntPtr baseEnvironmentBlock,
        IReadOnlyDictionary<string, string?>? environmentOverrides,
        IReadOnlyList<string>? pathPrependEntries)
    {
        var environment = ReadWindowsEnvironmentBlock(baseEnvironmentBlock);
        ApplyEnvironmentOverrides(environment, environmentOverrides);
        ApplyPathPrependEntries(environment, pathPrependEntries);
        return SerializeWindowsEnvironmentBlock(environment);
    }

    [SupportedOSPlatform("windows")]
    private static IntPtr SerializeWindowsEnvironmentBlock(
        Dictionary<string, string?> environment)
    {
        var entries = environment
            .Where(static pair => !string.IsNullOrWhiteSpace(pair.Key))
            .OrderBy(static pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(static pair => $"{pair.Key}={pair.Value ?? string.Empty}");

        var payload = string.Join('\0', entries) + "\0\0";
        return Marshal.StringToHGlobalUni(payload);
    }
    [SupportedOSPlatform("windows")]
    private static Dictionary<string, string?> ReadWindowsEnvironmentBlock(IntPtr environmentBlock)
    {
        var environment = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        var current = environmentBlock;

        while (true)
        {
            var entry = Marshal.PtrToStringUni(current);
            if (string.IsNullOrEmpty(entry))
            {
                break;
            }

            if (TrySplitEnvironmentEntry(entry, out var key, out var value))
            {
                environment[key] = value;
            }

            current += (entry.Length + 1) * sizeof(char);
        }

        return environment;
    }

    [SupportedOSPlatform("windows")]
    private static bool TrySplitEnvironmentEntry(string entry, out string key, out string value)
    {
        key = string.Empty;
        value = string.Empty;

        if (string.IsNullOrEmpty(entry))
        {
            return false;
        }

        var separatorIndex = entry[0] == '='
            ? entry.IndexOf("=", 1, StringComparison.Ordinal)
            : entry.IndexOf('=', StringComparison.Ordinal);
        if (separatorIndex <= 0)
        {
            return false;
        }

        key = entry[..separatorIndex];
        value = entry[(separatorIndex + 1)..];
        return true;
    }

    [SupportedOSPlatform("windows")]
    private static string BuildCommandLine(string fileName, IReadOnlyList<string> args)
    {
        var sb = new System.Text.StringBuilder();
        sb.Append('"').Append(fileName).Append('"');
        foreach (var arg in args)
        {
            sb.Append(' ');
            if (arg.AsSpan().IndexOf(' ') >= 0 || arg.AsSpan().IndexOf('\"') >= 0)
            {
                sb.Append('"').Append(arg.Replace("\"", "\\\"", StringComparison.Ordinal)).Append('"');
            }
            else
            {
                sb.Append(arg);
            }
        }
        return sb.ToString();
    }

    [SupportedOSPlatform("windows")]
    private static bool TryGetUserToken(string? runAsUser, string? runAsUserSid, out IntPtr userToken, out uint sessionId)
    {
        userToken = IntPtr.Zero;
        sessionId = 0;

        var hasTargetUser = !string.IsNullOrWhiteSpace(runAsUser) || !string.IsNullOrWhiteSpace(runAsUserSid);

        // Fast path when no specific user requested: try active console session
        if (!hasTargetUser)
        {
            var consoleSession = WTSGetActiveConsoleSessionId();
            if (consoleSession != 0xFFFFFFFF && WTSQueryUserToken(consoleSession, out userToken))
            {
                sessionId = consoleSession;
                Log.Info(() => $"TtyHostSpawner: Got user token from console session {consoleSession}");
                return true;
            }
        }

        // Enumerate all sessions to find the right user (or any user as fallback)
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out var pSessionInfo, out var sessionCount))
        {
            var errorCode = Marshal.GetLastWin32Error();
            Log.Error(() => string.Create(CultureInfo.InvariantCulture, $"TtyHostSpawner: WTSEnumerateSessions failed: {errorCode}"));
            return false;
        }

        try
        {
            var sessionInfoSize = Marshal.SizeOf<WTS_SESSION_INFO>();

            for (var i = 0; i < sessionCount; i++)
            {
                var info = Marshal.PtrToStructure<WTS_SESSION_INFO>(pSessionInfo + i * sessionInfoSize);

                if (info.State is not (WTS_CONNECTSTATE_CLASS.WTSActive or WTS_CONNECTSTATE_CLASS.WTSDisconnected))
                {
                    continue;
                }

                if (hasTargetUser)
                {
                    var sessionUser = GetSessionUsername(info.SessionId);
                    if (!TryGetMatchingSessionToken(info.SessionId, sessionUser, runAsUser, runAsUserSid, out userToken))
                    {
                        continue;
                    }

                    sessionId = info.SessionId;
                    Log.Info(() => $"TtyHostSpawner: Got token for user '{runAsUser ?? runAsUserSid}' from session {info.SessionId}");
                    return true;
                }
                else
                {
                    if (WTSQueryUserToken(info.SessionId, out userToken))
                    {
                        sessionId = info.SessionId;
                        Log.Info(() => $"TtyHostSpawner: Got user token from session {info.SessionId} (state: {info.State})");
                        return true;
                    }
                }
            }

            if (hasTargetUser)
            {
                Log.Error(() => $"TtyHostSpawner: User '{runAsUser ?? runAsUserSid}' has no active session — refusing to spawn as different user");
                return false;
            }
        }
        finally
        {
            WTSFreeMemory(pSessionInfo);
        }

        Log.Error(() => "TtyHostSpawner: No session with accessible user token found");
        return false;
    }

    [SupportedOSPlatform("windows")]
    internal static bool IsMatchingWindowsUsername(string? configuredUser, string? sessionUser)
    {
        var normalizedConfiguredUser = Services.Security.SystemUserProvider.NormalizeWindowsUsername(configuredUser);
        var normalizedSessionUser = Services.Security.SystemUserProvider.NormalizeWindowsUsername(sessionUser);

        if (string.IsNullOrWhiteSpace(normalizedConfiguredUser) || string.IsNullOrWhiteSpace(normalizedSessionUser))
        {
            return false;
        }

        return string.Equals(normalizedConfiguredUser, normalizedSessionUser, StringComparison.OrdinalIgnoreCase);
    }

    [SupportedOSPlatform("windows")]
    private static string? GetSessionUsername(uint sessionId)
    {
        if (!WTSQuerySessionInformation(IntPtr.Zero, sessionId, WTS_INFO_CLASS.WTSUserName, out var buffer, out var bytesReturned))
        {
            return null;
        }

        try
        {
            if (bytesReturned <= 2)
            {
                return null;
            }
            return Marshal.PtrToStringUni(buffer);
        }
        finally
        {
            WTSFreeMemory(buffer);
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TryGetMatchingSessionToken(
        uint sessionId,
        string? sessionUser,
        string? runAsUser,
        string? runAsUserSid,
        out IntPtr userToken)
    {
        userToken = IntPtr.Zero;
        if (!WTSQueryUserToken(sessionId, out userToken))
        {
            return false;
        }

        var matched = false;
        try
        {
            matched = !string.IsNullOrWhiteSpace(runAsUserSid)
                ? TokenMatchesWindowsUserSid(userToken, runAsUserSid)
                : IsMatchingWindowsUsername(runAsUser, sessionUser);
            return matched;
        }
        finally
        {
            if (!matched && userToken != IntPtr.Zero)
            {
                CloseHandle(userToken);
                userToken = IntPtr.Zero;
            }
        }
    }

    [SupportedOSPlatform("windows")]
    private static bool TokenMatchesWindowsUserSid(IntPtr userToken, string configuredSid)
    {
        try
        {
            using var identity = new WindowsIdentity(userToken);
            return string.Equals(identity.User?.Value, configuredSid, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }
#endif

    private static string GetTtyHostPath()
    {
        return ResolveTtyHostPath(Environment.ProcessPath, Environment.GetEnvironmentVariable(TtyHostPathEnvironmentVariable));
    }

    internal static string ResolveTtyHostPath(string? currentExePath, string? overridePath = null)
    {
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(overridePath.Trim()));
        }

        var currentExe = currentExePath;
        if (string.IsNullOrEmpty(currentExe))
        {
            return string.Empty;
        }

        var dir = Path.GetDirectoryName(currentExe);
        if (string.IsNullOrEmpty(dir))
        {
            return string.Empty;
        }

        var exeName = OperatingSystem.IsWindows() ? "mthost.exe" : "mthost";

        // Check same directory first (production/published builds)
        var sameDirPath = Path.Combine(dir, exeName);
        if (File.Exists(sameDirPath))
        {
            return sameDirPath;
        }

        // Development fallback: check sibling TtyHost project's output
        var repoRoot = Path.GetFullPath(Path.Combine(dir, "..", "..", "..", ".."));
#if WINDOWS
        var devPath = Path.Combine(repoRoot, "Ai.Tlbx.MidTerm.TtyHost", "bin", "Debug", "net10.0", "win-x64", exeName);
#else
        var rid = OperatingSystem.IsMacOS() ? "osx-arm64" : "linux-x64";
        var devPath = Path.Combine(repoRoot, "Ai.Tlbx.MidTerm.TtyHost", "bin", "Debug", "net10.0", rid, exeName);
#endif
        if (File.Exists(devPath))
        {
            return devPath;
        }

        return sameDirPath;
    }

    private static string? GetMidTermBinaryPath()
    {
        var envPath = Environment.GetEnvironmentVariable(MtBinaryPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            return envPath;
        }

        var exeName = OperatingSystem.IsWindows() ? "mt.exe" : "mt";
        var baseDir = AppContext.BaseDirectory;
        if (!string.IsNullOrWhiteSpace(baseDir))
        {
            var sameDirPath = Path.Combine(baseDir, exeName);
            if (File.Exists(sameDirPath))
            {
                return sameDirPath;
            }
        }

        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe))
        {
            return null;
        }

        if (string.Equals(Path.GetFileName(currentExe), exeName, StringComparison.OrdinalIgnoreCase))
        {
            return currentExe;
        }

        return string.Equals(Path.GetFileNameWithoutExtension(currentExe), "dotnet", StringComparison.OrdinalIgnoreCase)
            ? null
            : currentExe;
    }

#if WINDOWS
    #region P/Invoke

    private const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    private const uint CREATE_NO_WINDOW = 0x08000000;
    private const int STARTF_USESHOWWINDOW = 0x00000001;
    private const int STARTF_USESTDHANDLES = 0x00000100;
    private const short SW_HIDE = 0;
    private const uint HANDLE_FLAG_INHERIT = 0x00000001;

    [DllImport("kernel32.dll")]
    private static extern uint WTSGetActiveConsoleSessionId();

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out IntPtr phToken);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(
        IntPtr hServer,
        uint reserved,
        uint version,
        out IntPtr ppSessionInfo,
        out int pCount);

    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr pMemory);

    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr hServer,
        uint sessionId,
        WTS_INFO_CLASS wtsInfoClass,
        out IntPtr ppBuffer,
        out int pBytesReturned);

    private enum WTS_INFO_CLASS
    {
        WTSUserName = 5
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WTS_SESSION_INFO
    {
        public uint SessionId;
        public IntPtr pWinStationName;
        public WTS_CONNECTSTATE_CLASS State;
    }

    private enum WTS_CONNECTSTATE_CLASS
    {
        WTSActive,
        WTSConnected,
        WTSConnectQuery,
        WTSShadow,
        WTSDisconnected,
        WTSIdle,
        WTSListen,
        WTSReset,
        WTSDown,
        WTSInit
    }

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out IntPtr lpEnvironment, IntPtr hToken, bool bInherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(IntPtr lpEnvironment);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcess(
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUser(
        IntPtr hToken,
        string? lpApplicationName,
        string lpCommandLine,
        IntPtr lpProcessAttributes,
        IntPtr lpThreadAttributes,
        bool bInheritHandles,
        uint dwCreationFlags,
        IntPtr lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFO lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CreatePipe(
        out IntPtr hReadPipe,
        out IntPtr hWritePipe,
        ref SECURITY_ATTRIBUTES lpPipeAttributes,
        uint nSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetHandleInformation(IntPtr hObject, uint dwMask, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved;
        public IntPtr lpDesktop;
        public IntPtr lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    #endregion
#endif
}

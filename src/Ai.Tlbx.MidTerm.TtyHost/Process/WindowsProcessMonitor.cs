#if WINDOWS
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Common.Process;

namespace Ai.Tlbx.MidTerm.TtyHost.Process;

/// <summary>
/// Windows process monitor using simple polling. Monitors shell's direct child only.
/// Optimized: single snapshot per poll cycle, reusable buffers via ArrayPool.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessMonitor : IProcessMonitor
{
    private int _shellPid;
    private int? _currentChildPid;
    private string? _currentCwd;
    private string? _currentChildCwd;
    private ForegroundProcessInfo? _cachedForeground;
    private Timer? _timer;
    private int _polling;
    private bool _disposed;

    // Reusable buffers for ReadProcessMemory (thread-safe via ThreadStatic)
    [ThreadStatic]
    private static byte[]? _pebBuffer;
    [ThreadStatic]
    private static byte[]? _paramsBuffer;

    private static byte[] PebBuffer => _pebBuffer ??= new byte[0x30];
    private static byte[] ParamsBuffer => _paramsBuffer ??= new byte[0x80];

    public event Action<ForegroundProcessInfo>? OnForegroundChanged;

    public void StartMonitoring(int shellPid, int ptyMasterFd = -1)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(WindowsProcessMonitor));

        _shellPid = shellPid;
        var previousTimer = _timer;
        _timer = new Timer(_ => Poll(), null, 0, 500);
        previousTimer?.Dispose();
    }

    public void StopMonitoring()
    {
        _timer?.Dispose();
        _timer = null;
    }

    public ForegroundProcessInfo GetCurrentForeground()
    {
        if (_cachedForeground is not null)
            return _cachedForeground;

        // Initial call before first Poll() - compute fresh (rare case)
        var hSnapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (hSnapshot == IntPtr.Zero || hSnapshot == INVALID_HANDLE_VALUE)
        {
            return new ForegroundProcessInfo
            {
                Pid = _shellPid,
                Name = "shell",
                Cwd = GetShellCwd()
            };
        }

        try
        {
            var child = GetBestDirectChild(_shellPid, hSnapshot);
            if (child is null)
            {
                return new ForegroundProcessInfo
                {
                    Pid = _shellPid,
                    Name = "shell",
                    Cwd = GetShellCwd()
                };
            }

            return new ForegroundProcessInfo
            {
                Pid = child.Value.Pid,
                Name = child.Value.Name ?? "unknown",
                CommandLine = StripExecutablePath(GetProcessCommandLine(child.Value.Pid)),
                Cwd = GetProcessCwd(child.Value.Pid) ?? GetShellCwd()
            };
        }
        finally
        {
            CloseHandle(hSnapshot);
        }
    }

    public string? GetShellCwd() => GetProcessCwd(_shellPid);

    private void Poll()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _polling, 1) == 1) return;

        try
        {
            // Take a single snapshot for this entire poll cycle
            var hSnapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
            if (hSnapshot == IntPtr.Zero || hSnapshot == INVALID_HANDLE_VALUE)
                return;

            try
            {
                var child = GetBestDirectChild(_shellPid, hSnapshot);
                var childPid = child?.Pid;
                var cwd = GetShellCwd();
                var childCwd = childPid.HasValue ? GetProcessCwd(childPid.Value) : null;

                if (childPid != _currentChildPid || cwd != _currentCwd || childCwd != _currentChildCwd)
                {
                    _currentChildPid = childPid;
                    _currentCwd = cwd;
                    _currentChildCwd = childCwd;

                    ForegroundProcessInfo info;
                    if (childPid is null)
                    {
                        info = new ForegroundProcessInfo
                        {
                            Pid = _shellPid,
                            Name = "shell",
                            Cwd = cwd
                        };
                    }
                    else
                    {
                        info = new ForegroundProcessInfo
                        {
                            Pid = childPid.Value,
                            Name = child?.Name ?? "unknown",
                            CommandLine = StripExecutablePath(GetProcessCommandLine(childPid.Value)),
                            Cwd = childCwd ?? cwd
                        };
                    }

                    _cachedForeground = info;
                    OnForegroundChanged?.Invoke(info);
                }
            }
            finally
            {
                CloseHandle(hSnapshot);
            }
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"Process poll error: {ex.Message}");
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private static ForegroundChildCandidate? GetBestDirectChild(int parentPid, IntPtr hSnapshot)
    {
        var candidates = new List<ForegroundChildCandidate>();
        var pe = new PROCESSENTRY32W { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32W>() };
        if (Process32FirstW(hSnapshot, ref pe))
        {
            do
            {
                if ((int)pe.th32ParentProcessID == parentPid)
                {
                    var pid = (int)pe.th32ProcessID;
                    candidates.Add(new ForegroundChildCandidate(
                        pid,
                        NormalizeProcessName(pe.szExeFile),
                        HasVisibleWindow(pid),
                        GetStartedAtUtc(pid)));
                }
            } while (Process32NextW(hSnapshot, ref pe));
        }

        return ForegroundChildSelector.SelectBest(candidates);
    }

    private static string NormalizeProcessName(string processName)
    {
        if (processName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return processName[..^4];
        }

        return processName;
    }

    private static bool HasVisibleWindow(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.MainWindowHandle != IntPtr.Zero;
        }
        catch
        {
            return false;
        }
    }

    private static DateTimeOffset? GetStartedAtUtc(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime();
        }
        catch
        {
            return null;
        }
    }

    private static string? GetProcessCwd(int pid)
    {
        var hProcess = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, (uint)pid);
        if (hProcess == IntPtr.Zero) return null;

        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            if (NtQueryInformationProcess(hProcess, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) != 0)
                return null;
            if (pbi.PebBaseAddress == IntPtr.Zero) return null;

            var pebData = PebBuffer;
            if (!ReadProcessMemory(hProcess, pbi.PebBaseAddress, pebData, 0x30, out _))
                return null;

            var procParamsPtr = checked((IntPtr)BitConverter.ToInt64(pebData, 0x20));
            if (procParamsPtr == IntPtr.Zero) return null;

            var paramsData = ParamsBuffer;
            if (!ReadProcessMemory(hProcess, procParamsPtr, paramsData, 0x50, out _))
                return null;

            var cwdLength = BitConverter.ToUInt16(paramsData, 0x38);
            var cwdBufferPtr = checked((IntPtr)BitConverter.ToInt64(paramsData, 0x38 + 8));
            if (cwdLength == 0 || cwdBufferPtr == IntPtr.Zero) return null;

            var buffer = ArrayPool<byte>.Shared.Rent(cwdLength);
            try
            {
                if (!ReadProcessMemory(hProcess, cwdBufferPtr, buffer, cwdLength, out _))
                    return null;

                var cwd = Encoding.Unicode.GetString(buffer, 0, cwdLength).TrimEnd('\0');
                return cwd.EndsWith('\\') && cwd.Length > 3 ? cwd.TrimEnd('\\') : cwd;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    private static string? GetProcessCommandLine(int pid)
    {
        var hProcess = OpenProcess(PROCESS_QUERY_INFORMATION | PROCESS_VM_READ, false, (uint)pid);
        if (hProcess == IntPtr.Zero) return null;

        try
        {
            var pbi = new PROCESS_BASIC_INFORMATION();
            if (NtQueryInformationProcess(hProcess, 0, ref pbi, Marshal.SizeOf<PROCESS_BASIC_INFORMATION>(), out _) != 0)
                return null;
            if (pbi.PebBaseAddress == IntPtr.Zero) return null;

            var pebData = PebBuffer;
            if (!ReadProcessMemory(hProcess, pbi.PebBaseAddress, pebData, 0x30, out _))
                return null;

            var procParamsPtr = checked((IntPtr)BitConverter.ToInt64(pebData, 0x20));
            if (procParamsPtr == IntPtr.Zero) return null;

            var paramsData = ParamsBuffer;
            if (!ReadProcessMemory(hProcess, procParamsPtr, paramsData, 0x80, out _))
                return null;

            var cmdLength = BitConverter.ToUInt16(paramsData, 0x70);
            var cmdBufferPtr = checked((IntPtr)BitConverter.ToInt64(paramsData, 0x70 + 8));
            if (cmdLength == 0 || cmdBufferPtr == IntPtr.Zero) return null;

            var buffer = ArrayPool<byte>.Shared.Rent(cmdLength);
            try
            {
                if (!ReadProcessMemory(hProcess, cmdBufferPtr, buffer, cmdLength, out _))
                    return null;

                return Encoding.Unicode.GetString(buffer, 0, cmdLength).TrimEnd('\0');
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch
        {
            return null;
        }
        finally
        {
            CloseHandle(hProcess);
        }
    }

    private static string? StripExecutablePath(string? commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine)) return commandLine;

        var cmd = commandLine.AsSpan().Trim();
        if (cmd.IsEmpty) return commandLine;

        int exeEnd;
        ReadOnlySpan<char> exeName;
        if (cmd[0] == '"')
        {
            var closeQuote = cmd.Slice(1).IndexOf('"');
            if (closeQuote < 0) return commandLine;
            var exePath = cmd.Slice(1, closeQuote);
            var lastSlash = exePath.LastIndexOfAny(['\\', '/']);
            exeName = lastSlash >= 0 ? exePath.Slice(lastSlash + 1) : exePath;
            exeEnd = closeQuote + 2;
        }
        else
        {
            exeEnd = cmd.IndexOf(' ');
            var exePath = exeEnd >= 0 ? cmd.Slice(0, exeEnd) : cmd;
            var lastSlash = exePath.LastIndexOfAny(['\\', '/']);
            exeName = lastSlash >= 0 ? exePath.Slice(lastSlash + 1) : exePath;
        }

        if (exeName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            exeName = exeName.Slice(0, exeName.Length - 4);

        var args = exeEnd >= 0 && cmd.Length > exeEnd ? cmd.Slice(exeEnd).TrimStart() : ReadOnlySpan<char>.Empty;
        return args.IsEmpty ? exeName.ToString() : $"{exeName} {args}";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopMonitoring();
    }

    #region Native Interop

    private const uint PROCESS_QUERY_INFORMATION = 0x0400;
    private const uint PROCESS_VM_READ = 0x0010;
    private const uint TH32CS_SNAPPROCESS = 0x00000002;
    private static readonly IntPtr INVALID_HANDLE_VALUE = new(-1);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, uint dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(IntPtr hSnapshot, ref PROCESSENTRY32W lppe);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationProcess(IntPtr processHandle, int processInformationClass,
        ref PROCESS_BASIC_INFORMATION processInformation, int processInformationLength, out int returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadProcessMemory(IntPtr hProcess, IntPtr lpBaseAddress, byte[] lpBuffer,
        int dwSize, out int lpNumberOfBytesRead);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32W
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szExeFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_BASIC_INFORMATION
    {
        public IntPtr Reserved1;
        public IntPtr PebBaseAddress;
        public IntPtr Reserved2_0;
        public IntPtr Reserved2_1;
        public IntPtr UniqueProcessId;
        public IntPtr Reserved3;
    }

    #endregion
}
#endif

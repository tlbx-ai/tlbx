using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

/// <summary>Resolves ownership from live ancestry, never from cwd or a stale environment ID.</summary>
internal static partial class SessionProcessContext
{
    internal static string? Resolve(int processId, IReadOnlyDictionary<int, string> roots,
        Func<int, (int Parent, DateTime Started)?>? readProcess = null)
    {
        readProcess ??= ReadProcess;
        var visited = new HashSet<int>();
        DateTime? childStarted = null;
        while (processId > 0 && visited.Count < 128 && visited.Add(processId))
        {
            var process = readProcess(processId);
            if (process is null || (childStarted is not null && process.Value.Started > childStarted)) return null;
            if (roots.TryGetValue(processId, out var sessionId)) return sessionId;
            childStarted = process.Value.Started;
            processId = process.Value.Parent;
        }
        return null;
    }

    internal static unsafe (int Parent, DateTime Started)? ReadProcess(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            var started = process.StartTime.ToUniversalTime();
            int parent;
            if (OperatingSystem.IsWindows())
            {
                // PROCESS_BASIC_INFORMATION consists of six pointer-sized fields.
                nint* info = stackalloc nint[6];
                if (NtQueryInformationProcess(process.Handle, 0, info, 6 * sizeof(nint), out _) != 0) return null;
                parent = checked((int)info[5]);
            }
            else if (OperatingSystem.IsLinux())
            {
                var stat = File.ReadAllText(string.Create(CultureInfo.InvariantCulture, $"/proc/{pid}/stat"));
                var fields = stat[(stat.LastIndexOf(')') + 2)..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                parent = int.Parse(fields[1], CultureInfo.InvariantCulture);
            }
            else if (OperatingSystem.IsMacOS())
            {
                // PROC_PIDTBSDINFO: flags, status, xstatus, pid, ppid (uint32).
                byte* info = stackalloc byte[136];
                if (ProcPidInfo(pid, 3, 0, info, 136) != 136) return null;
                parent = checked((int)*(uint*)(info + 16));
            }
            else return null;
            return (parent, started);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or OverflowException)
        {
            return null;
        }
    }

    internal static unsafe bool? IsSharedCodexBackend(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!string.Equals(process.ProcessName, "codex", StringComparison.OrdinalIgnoreCase)) return false;
            string? command = null;
            if (OperatingSystem.IsWindows())
            {
                NtQueryInformationProcess(process.Handle, 60, null, 0, out var length);
                if (length <= 0 || length > 131072) return null;
                var buffer = (byte*)NativeMemory.Alloc((nuint)length);
                try
                {
                    if (NtQueryInformationProcess(process.Handle, 60, buffer, length, out _) != 0) return null;
                    var bytes = *(ushort*)buffer;
                    var text = *(nint*)(buffer + IntPtr.Size);
                    if (text < (nint)buffer || text + bytes > (nint)buffer + length) return null;
                    command = Marshal.PtrToStringUni(text, bytes / 2);
                }
                finally { NativeMemory.Free(buffer); }
            }
            else if (OperatingSystem.IsLinux())
                command = File.ReadAllText($"/proc/{pid}/cmdline").Replace('\0', ' ');
            if (string.IsNullOrWhiteSpace(command)) return null;
            return IsSharedCodexCommand(command);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException) { return null; }
    }

    internal static bool IsSharedCodexCommand(string command) =>
        System.Text.RegularExpressions.Regex.IsMatch(command, "(?:^|[\\s\"'])app-server(?:$|[\\s\"'])", System.Text.RegularExpressions.RegexOptions.CultureInvariant) ||
        command.Contains("--managed-daemon", StringComparison.Ordinal);

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQueryInformationProcess(nint process, int infoClass, void* info, int length, out int returned);

    [LibraryImport("libproc", EntryPoint = "proc_pidinfo")]
    private static unsafe partial int ProcPidInfo(int pid, int flavor, ulong arg, void* buffer, int size);
}

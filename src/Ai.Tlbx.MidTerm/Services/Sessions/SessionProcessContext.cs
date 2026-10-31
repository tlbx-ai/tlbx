using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

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
            if (OperatingSystem.IsWindows())
            {
                // Process.Handle requests all access. A service may observe a
                // live user terminal without having those unnecessary rights.
                // Read the epoch and parent through one limited-query handle.
                using var handle = OpenProcess(0x1000, false, pid);
                if (handle.IsInvalid || !GetProcessTimes(handle, out var created, out var exited, out _, out _) || exited != 0) return null;
                nint* info = stackalloc nint[6];
                if (NtQueryInformationProcess(handle, 0, info, 6 * sizeof(nint), out _) != 0 ||
                    *(int*)info != 259 /* STILL_ACTIVE */ || info[4] != pid) return null;
                return (checked((int)info[5]), DateTime.FromFileTimeUtc(created));
            }
            using var process = Process.GetProcessById(pid);
            var started = process.StartTime.ToUniversalTime();
            int parent;
            if (OperatingSystem.IsLinux())
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
                command = ReadWindowsCommandLine(pid);
            }
            else if (OperatingSystem.IsLinux())
            {
                var arguments = File.ReadAllText(string.Create(CultureInfo.InvariantCulture, $"/proc/{pid}/cmdline"));
                return string.IsNullOrWhiteSpace(arguments) ? null : IsSharedCodexArguments(arguments.Split('\0'));
            }
            else if (OperatingSystem.IsMacOS())
            {
                // Use the same KERN_PROCARGS2 layout as MacOSProcessMonitor, preserving argv
                // boundaries and excluding the environment following argc arguments.
                int* argmaxMib = stackalloc int[] { 1, 8 }; // CTL_KERN, KERN_ARGMAX
                var argmax = 0;
                nuint size = sizeof(int);
                if (Sysctl(argmaxMib, 2, &argmax, &size, null, 0) != 0 ||
                    size != sizeof(int) || argmax < 4 || argmax > 16 * 1024 * 1024) return null;
                var buffer = new byte[argmax];
                int* argsMib = stackalloc int[] { 1, 49, pid }; // CTL_KERN, KERN_PROCARGS2
                size = (nuint)buffer.Length;
                fixed (byte* data = buffer)
                    if (Sysctl(argsMib, 3, data, &size, null, 0) != 0 || size > (nuint)buffer.Length) return null;
                var arguments = ParseMacProcessArguments(buffer.AsSpan(0, (int)size));
                return arguments is null ? null : IsSharedCodexArguments(arguments);
            }
            if (string.IsNullOrWhiteSpace(command)) return null;
            return IsSharedCodexCommand(command);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or
            System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException) { return null; }
    }

    internal static bool IsSharedCodexCommand(string command) =>
        IsSharedCodexArguments(SessionForegroundProcessService.Tokenize(command));

    [SupportedOSPlatform("windows")]
    internal static unsafe string? ReadWindowsCommandLine(int pid)
    {
        using var handle = OpenProcess(0x1000, false, pid);
        if (handle.IsInvalid) return null;
        NtQueryInformationProcess(handle, 60, null, 0, out var length);
        if (length <= 0 || length > 131072) return null;
        var buffer = (byte*)NativeMemory.Alloc((nuint)length);
        try
        {
            if (NtQueryInformationProcess(handle, 60, buffer, length, out _) != 0) return null;
            var bytes = *(ushort*)buffer;
            var text = *(nint*)(buffer + IntPtr.Size);
            if (text < (nint)buffer || text + bytes > (nint)buffer + length) return null;
            return Marshal.PtrToStringUni(text, bytes / 2);
        }
        finally { NativeMemory.Free(buffer); }
    }

    internal static bool IsSharedCodexArguments(IEnumerable<string> arguments) =>
        arguments.Skip(1).Any(static argument => argument is "app-server" or "--managed-daemon");

    internal static string[]? ParseMacProcessArguments(ReadOnlySpan<byte> buffer)
    {
        if (buffer.Length < sizeof(int)) return null;
        var argc = System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(buffer);
        if (argc <= 0 || argc > buffer.Length - sizeof(int)) return null;
        var position = sizeof(int);
        var executableEnd = buffer[position..].IndexOf((byte)0);
        if (executableEnd <= 0) return null;
        position += executableEnd + 1;
        while (position < buffer.Length && buffer[position] == 0) position++;
        var arguments = new string[argc];
        for (var i = 0; i < argc; i++)
        {
            var end = buffer[position..].IndexOf((byte)0);
            if (end < 0) return null;
            arguments[i] = System.Text.Encoding.UTF8.GetString(buffer.Slice(position, end));
            position += end + 1;
        }
        return arguments;
    }

    [LibraryImport("ntdll.dll")]
    private static unsafe partial int NtQueryInformationProcess(SafeProcessHandle process, int infoClass, void* info, int length, out int returned);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out long creation, out long exit, out long kernel, out long user);

    [LibraryImport("libproc", EntryPoint = "proc_pidinfo")]
    private static unsafe partial int ProcPidInfo(int pid, int flavor, ulong arg, void* buffer, int size);

    [LibraryImport("libSystem.B.dylib", EntryPoint = "sysctl")]
    private static unsafe partial int Sysctl(int* name, uint nameLength, void* oldValue,
        nuint* oldLength, void* newValue, nuint newLength);
}

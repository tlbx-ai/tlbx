using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;

namespace Ai.Tlbx.MidTerm.Common.Process;

/// <summary>
/// Windows otherwise starts children of AboveNormal/High parents at Normal.
/// A priority-only job applies the selected class at creation to every child
/// and descendant, including shells and processes launched with another token.
/// It has no lifetime, memory, CPU-rate, or kill-on-close limits.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessPriorityJob : IDisposable
{
    private readonly SafeFileHandle _handle;

    public WindowsProcessPriorityJob(string? name = null)
    {
        if (name is null)
        {
            _handle = CreateJobObjectW(IntPtr.Zero, null);
        }
        else
        {
            // Hosts run as the terminal user even when mt runs as LocalSystem.
            // They need query-only handles to keep the group alive across web
            // restarts; only its owner, administrators and SYSTEM may change it.
            if (!ConvertStringSecurityDescriptorToSecurityDescriptorW(
                "D:(A;;GA;;;SY)(A;;GA;;;BA)(A;;GA;;;OW)(A;;0x0004;;;AU)", 1, out var descriptor, out _))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            try
            {
                var attributes = new SecurityAttributes
                {
                    Length = (uint)Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor
                };
                _handle = CreateJobObjectWithSecurity(ref attributes, name);
            }
            finally { LocalFree(descriptor); }
        }
        if (_handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public static SafeFileHandle OpenLifetimeHandle(string name)
    {
        var handle = OpenJobObjectW(4, false, name);
        if (!handle.IsInvalid) return handle;
        var error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new Win32Exception(error);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SecurityAttributes
    {
        public uint Length;
        public IntPtr Descriptor;
        [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ConvertStringSecurityDescriptorToSecurityDescriptorW(string value, uint revision,
        out IntPtr descriptor, out uint size);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    [DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectWithSecurity(ref SecurityAttributes attributes, string name);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle OpenJobObjectW(uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

    public void SetPriority(ProcessPriorityClass priority)
    {
        if (priority == ProcessPriorityClass.High) EnableBasePriorityPrivilege();
        var limits = new BasicLimitInformation { LimitFlags = 0x20, PriorityClass = (uint)priority };
        if (!SetInformationJobObject(_handle, 2, ref limits, (uint)Marshal.SizeOf<BasicLimitInformation>()))
            throw new Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Assign(System.Diagnostics.Process process)
    {
        if (!IsProcessInJob(process.SafeHandle, _handle, out var assigned))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (assigned) return;
        if (!AssignProcessToJobObject(_handle, process.SafeHandle))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        // Hosts can survive a web restart. Adopt their already running children
        // too; job inheritance only covers children created after assignment.
        using var snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
        var children = new Dictionary<int, List<int>>();
        if (Process32FirstW(snapshot, ref entry))
        {
            do
            {
                var parentId = (int)entry.ParentProcessId;
                if (!children.TryGetValue(parentId, out var ids)) children[parentId] = ids = [];
                ids.Add((int)entry.ProcessId);
            } while (Process32NextW(snapshot, ref entry));
        }
        AssignDescendants(process, children);
    }

    private void AssignDescendants(System.Diagnostics.Process parent, Dictionary<int, List<int>> children)
    {
        if (!children.TryGetValue(parent.Id, out var ids)) return;
        foreach (var id in ids)
        {
            try
            {
                using var child = System.Diagnostics.Process.GetProcessById(id);
                // A stale snapshot/PID reuse must never adopt an unrelated process.
                if (parent.HasExited || child.StartTime < parent.StartTime) continue;
                if (!IsProcessInJob(child.SafeHandle, _handle, out var assigned))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                if (!assigned && !AssignProcessToJobObject(_handle, child.SafeHandle))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                AssignDescendants(child, children);
            }
            catch (ArgumentException) { /* Process exited after the snapshot. */ }
            catch (InvalidOperationException) { /* Process exited during adoption. */ }
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nuint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int BasePriority;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExecutableName;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern SafeFileHandle CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32FirstW(SafeFileHandle snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32NextW(SafeFileHandle snapshot, ref ProcessEntry entry);

    public void Dispose() => _handle.Dispose();

    private static void EnableBasePriorityPrivilege()
    {
        using var process = System.Diagnostics.Process.GetCurrentProcess();
        if (!OpenProcessToken(process.SafeHandle, 0x20 | 0x8, out var token))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        using (token)
        {
            if (!LookupPrivilegeValueW(null, "SeIncreaseBasePriorityPrivilege", out var luid))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var privileges = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 2 };
            if (!AdjustTokenPrivileges(token, false, ref privileges, 0, IntPtr.Zero, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var error = Marshal.GetLastWin32Error();
            if (error != 0) throw new Win32Exception(error);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct TokenPrivileges
    {
        public uint Count;
        public Luid Luid;
        public uint Attributes;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Luid
    {
        public uint LowPart;
        public int HighPart;
    }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out Luid luid);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(SafeAccessTokenHandle token,
        [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges privileges,
        uint length, IntPtr previous, IntPtr returnedLength);

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int informationClass,
        ref BasicLimitInformation information, uint length);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, SafeProcessHandle process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(SafeProcessHandle process, SafeFileHandle job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Ai.Tlbx.MidTerm.Common.Logging;
using Microsoft.Win32.SafeHandles;

namespace Ai.Tlbx.MidTerm.Services.Hosting;

/// <summary>Repairs recovery on legacy installs, only for the SCM service hosting this process.</summary>
internal static partial class WindowsServiceRecovery
{
    internal static void EnsureForCurrentProcess(string serviceName)
    {
        if (!OperatingSystem.IsWindows()) return;
        try { Configure(serviceName); }
        catch (Exception ex) { Log.Warn(() => "Could not configure service recovery: " + ex.Message); }
    }

    [SupportedOSPlatform("windows")]
    private static unsafe void Configure(string serviceName)
    {
        using var manager = OpenSCManager(null, null, 1); // SC_MANAGER_CONNECT
        if (manager.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        using var service = OpenService(manager, serviceName, 0x0004 | 0x0002 | 0x0010); // QUERY_STATUS | CHANGE_CONFIG | START
        if (service.IsInvalid) throw new Win32Exception(Marshal.GetLastPInvokeError());
        ServiceStatus status;
        if (QueryServiceStatusEx(service, 0, &status, (uint)sizeof(ServiceStatus), out _) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        // A source/test instance must never reconfigure the installed service.
        if (status.State != 4 || status.ProcessId != (uint)Environment.ProcessId) return;
        var actions = stackalloc ServiceAction[3];
        actions[0] = new() { Type = 1, Delay = 1000 };
        actions[1] = new() { Type = 1, Delay = 5000 };
        actions[2] = new() { Type = 1, Delay = 30000 }; // SCM repeats the last action.
        var recovery = new FailureActions { ResetPeriod = 86400, Count = 3, Actions = actions };
        if (ChangeServiceConfig2(service, 2, &recovery) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        var recoverNonCrashFailure = 1;
        if (ChangeServiceConfig2(service, 4, &recoverNonCrashFailure) == 0)
            throw new Win32Exception(Marshal.GetLastPInvokeError());
        Log.Info(() => "Windows service crash recovery configured (1s, 5s, then 30s). Intentional stops remain stopped.");
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceStatus
    {
        public uint Type, State, AcceptedControls, Win32ExitCode, ServiceExitCode, Checkpoint, WaitHint, ProcessId, Flags;
    }
    [StructLayout(LayoutKind.Sequential)]
    private struct ServiceAction { public uint Type, Delay; }
    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct FailureActions
    {
        public uint ResetPeriod;
        public nint RebootMessage, Command;
        public uint Count;
        public ServiceAction* Actions;
    }

    private sealed class ServiceHandle : SafeHandleZeroOrMinusOneIsInvalid
    {
        public ServiceHandle() : base(true) { }
        protected override bool ReleaseHandle() => CloseServiceHandle(handle) != 0;
    }

    [LibraryImport("advapi32.dll", EntryPoint = "OpenSCManagerW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial ServiceHandle OpenSCManager(string? machine, string? database, uint access);
    [LibraryImport("advapi32.dll", EntryPoint = "OpenServiceW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial ServiceHandle OpenService(ServiceHandle manager, string name, uint access);
    [LibraryImport("advapi32.dll", SetLastError = true)]
    private static unsafe partial int QueryServiceStatusEx(ServiceHandle service, int level, ServiceStatus* status, uint size, out uint needed);
    [LibraryImport("advapi32.dll", EntryPoint = "ChangeServiceConfig2W", SetLastError = true)]
    private static unsafe partial int ChangeServiceConfig2(ServiceHandle service, uint level, void* info);
    [LibraryImport("advapi32.dll")]
    private static partial int CloseServiceHandle(nint handle);
}

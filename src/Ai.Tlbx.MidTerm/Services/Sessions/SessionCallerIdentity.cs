using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

/// <summary>Accesses user-private Codex state with the exact local tool caller's identity.</summary>
internal static partial class SessionCallerIdentity
{
    internal static Task<T> RunAsync<T>(int callerPid, Func<Task<T>> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return OperatingSystem.IsWindows() ? RunWindowsAsync(callerPid, action) : action();
    }

    [SupportedOSPlatform("windows")]
    private static async Task<T> RunWindowsAsync<T>(int callerPid, Func<Task<T>> action)
    {
        var before = SessionProcessContext.ReadProcess(callerPid);
        if (before is null) throw InvalidCaller();
        using var process = OpenProcess(0x1000, false, callerPid); // PROCESS_QUERY_LIMITED_INFORMATION
        if (process.IsInvalid) throw IdentityFailure("OpenProcess", Marshal.GetLastPInvokeError());
        if (!GetProcessTimes(process, out var created, out _, out _, out _))
            throw IdentityFailure("GetProcessTimes", Marshal.GetLastPInvokeError());
        if (created != before.Value.Started.ToFileTimeUtc()) throw InvalidCaller();
        using var token = OpenCallerToken(process);
        if (SessionProcessContext.ReadProcess(callerPid)?.Started != before.Value.Started) throw InvalidCaller();

        Task<T> pending;
        try
        {
            // .NET duplicates the token handle and carries impersonation through awaits.
            // Keep our original handles alive until the entire operation completes.
            pending = WindowsIdentity.RunImpersonatedAsync(token, action);
        }
        catch (SecurityException)
        {
            throw IdentityFailure("RunImpersonatedAsync", Marshal.GetLastPInvokeError());
        }
        var result = await pending.ConfigureAwait(false);
        if (SessionProcessContext.ReadProcess(callerPid)?.Started != before.Value.Started) throw InvalidCaller();
        return result;
    }

    [SupportedOSPlatform("windows")]
    private static SafeAccessTokenHandle OpenCallerToken(SafeProcessHandle process)
    {
        // ImpersonateLoggedOnUser accepts a primary token with QUERY | DUPLICATE.
        if (OpenProcessToken(process, 0x0008 | 0x0002, out var token)) return token;
        var error = Marshal.GetLastPInvokeError();
        token?.Dispose();
        throw IdentityFailure("OpenProcessToken", error);
    }

    private static SessionCallerIdentityException InvalidCaller() =>
        new("INVALID_PROCESS", "The calling tool process ended or its PID was reused.");

    private static SessionCallerIdentityException IdentityFailure(string stage, int error) =>
        new("CALLER_IDENTITY", string.Create(CultureInfo.InvariantCulture, $"{stage} failed (Windows error {error})."));

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint access,
        [MarshalAs(UnmanagedType.Bool)] bool inherit, int pid);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessTimes(SafeProcessHandle process, out long creation,
        out long exit, out long kernel, out long user);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeProcessHandle process, uint access,
        out SafeAccessTokenHandle token);
}

internal sealed class SessionCallerIdentityException(string status, string detail)
    : Exception(detail)
{
    internal string Status { get; } = status;
}

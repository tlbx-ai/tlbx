using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class WindowsSessionProcessContextTests
{
    public static bool IsWindows => OperatingSystem.IsWindows();

    [Fact(Skip = "Requires Windows process access rights.", SkipUnless = nameof(IsWindows))]
    public async Task ProcessEpochAndParentNeedOnlyLimitedQueryRights()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        };
        start.ArgumentList.Add("/d");
        start.ArgumentList.Add("/q");
        using var child = Process.Start(start)!;
        var stdout = child.StandardOutput.ReadToEndAsync(CancellationToken.None);
        var stderr = child.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            var expectedStart = child.StartTime.ToUniversalTime();
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User!.Value;
            // Restrict only this disposable child, preserving our already-open full
            // handle for cleanup. Remove debug privileges from the probe token so
            // elevated CI runners cannot bypass the disposable child's DACL.
            var descriptor = new RawSecurityDescriptor($"D:P(A;;0x101000;;;{sid})");
            var bytes = new byte[descriptor.BinaryLength];
            descriptor.GetBinaryForm(bytes, 0);
            Assert.True(SetKernelObjectSecurity(child.SafeHandle, 4, bytes), Marshal.GetLastPInvokeError().ToString(System.Globalization.CultureInfo.InvariantCulture));
            Assert.True(CreateRestrictedToken(identity.AccessToken, 1, 0, 0, 0, 0, 0, 0, out var restricted),
                Marshal.GetLastPInvokeError().ToString(System.Globalization.CultureInfo.InvariantCulture));
            using (restricted)
            {
                WindowsIdentity.RunImpersonated(restricted, () =>
                {
                    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                    using var fresh = Process.GetProcessById(child.Id);
                    Assert.Throws<Win32Exception>(() => _ = fresh.Handle);
                    var observed = SessionProcessContext.ReadProcess(child.Id);
                    Assert.NotNull(observed);
                    Assert.Equal(Environment.ProcessId, observed.Value.Parent);
                    Assert.Equal(expectedStart, observed.Value.Started);
                    var commandLine = SessionProcessContext.ReadWindowsCommandLine(child.Id);
                    Assert.NotNull(commandLine);
                    Assert.Contains("/d", commandLine, StringComparison.Ordinal);
                    Assert.Contains("/q", commandLine, StringComparison.Ordinal);
                });
            }
        }
        finally
        {
            await child.StandardInput.WriteLineAsync("exit".AsMemory(), CancellationToken.None);
            child.StandardInput.Close();
            try { await child.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
            finally { if (!child.HasExited) child.Kill(); }
            await Task.WhenAll(stdout, stderr);
        }
    }

    // Match the existing token-identity fixtures without enabling unsafe test interop.
#pragma warning disable SYSLIB1054
    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetKernelObjectSecurity(SafeProcessHandle handle, uint information, byte[] descriptor);

    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle token, uint flags,
        uint disabledCount, nint disabled, uint deletedCount, nint deleted,
        uint restrictedCount, nint restricting, out SafeAccessTokenHandle result);
#pragma warning restore SYSLIB1054
}

using System.Diagnostics;
using System.Globalization;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Microsoft.Win32.SafeHandles;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class SessionCallerIdentityTests
{
    [Fact]
    public async Task NativeCallerIdentityFlowsAcrossAwaitAndRestoresOriginalContext()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var original = WindowsIdentity.GetCurrent();
        var expected = original.User!.Value;
        var result = await SessionCallerIdentity.RunAsync(Environment.ProcessId, async () =>
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            using var before = WindowsIdentity.GetCurrent();
            Assert.Equal(expected, before.User!.Value);
            Assert.NotEqual(TokenImpersonationLevel.None, before.ImpersonationLevel);
            await Task.Yield();
            using var after = WindowsIdentity.GetCurrent();
            Assert.Equal(expected, after.User!.Value);
            Assert.NotEqual(TokenImpersonationLevel.None, after.ImpersonationLevel);
            return 42;
        });
        Assert.Equal(42, result);
        using var restored = WindowsIdentity.GetCurrent();
        Assert.Equal(expected, restored.User!.Value);
        Assert.Equal(original.ImpersonationLevel, restored.ImpersonationLevel);
    }

    [Fact]
    public async Task CallerActionFailureRestoresOriginalIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var original = WindowsIdentity.GetCurrent();
        await Assert.ThrowsAsync<IOException>(() => SessionCallerIdentity.RunAsync<int>(Environment.ProcessId, async () =>
        {
            await Task.Yield();
            throw new IOException("Expected action failure");
        }));
        using var restored = WindowsIdentity.GetCurrent();
        Assert.Equal(original.User!.Value, restored.User!.Value);
        Assert.Equal(original.ImpersonationLevel, restored.ImpersonationLevel);
    }

    [Fact]
    public async Task EndedCallerCannotRunUserStateOperation()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var child = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            Arguments = "/c exit 0", UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true
        })!;
        await child.WaitForExitAsync();
        var ran = false;
        var error = await Assert.ThrowsAsync<SessionCallerIdentityException>(() =>
            SessionCallerIdentity.RunAsync(child.Id, () => { ran = true; return Task.FromResult(true); }));
        Assert.Equal("INVALID_PROCESS", error.Status);
        Assert.False(ran);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestrictedContextCannotAccessPrivateResourceButExactCallerScopeCan(bool socketProbe)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var original = WindowsIdentity.GetCurrent();
        // Disable one enabled permission group while preserving the caller's
        // user SID, so the restricted token can still inspect its own process.
        var permissionSid = new SecurityIdentifier(WellKnownSidType.AuthenticatedUserSid, null);
        Assert.Contains(permissionSid, original.Groups!.OfType<SecurityIdentifier>());
        var directory = Path.Combine(Path.GetTempPath(), "tlbx-caller-identity-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "private.txt");
        var socketPath = Path.Combine(directory, "s");
        using var listener = socketProbe ? new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified) : null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var sidBytes = new byte[permissionSid.BinaryLength];
        permissionSid.GetBinaryForm(sidBytes, 0);
        var sidMemory = Marshal.AllocHGlobal(sidBytes.Length);
        try
        {
            await File.WriteAllTextAsync(file, "caller-private fixture", timeout.Token);
            var acl = new DirectorySecurity();
            acl.SetOwner(original.User!);
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            acl.AddAccessRule(new FileSystemAccessRule(permissionSid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(acl);
            if (listener is not null)
            {
                listener.Bind(new UnixDomainSocketEndPoint(socketPath));
                listener.Listen(1);
            }

            Marshal.Copy(sidBytes, 0, sidMemory, sidBytes.Length);
            var disabled = new SidAndAttributes { Sid = sidMemory };
            Assert.True(CreateRestrictedToken(original.AccessToken, 0, 1, ref disabled,
                0, 0, 0, 0, out var restricted), "CreateRestrictedToken failed: " +
                Marshal.GetLastPInvokeError().ToString(CultureInfo.InvariantCulture));
            using (restricted)
            {
                await WindowsIdentity.RunImpersonatedAsync(restricted, async () =>
                {
                    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                    Assert.False(File.Exists(file));
                    Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllText(file));
                    if (socketProbe) await AssertSocketDenied();
                    var text = await SessionCallerIdentity.RunAsync(Environment.ProcessId, async () =>
                    {
                        await Task.Yield();
                        if (listener is not null)
                        {
                            using var stop = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                            var serving = ServeHttpAsync(stop.Token);
                            try
                            {
                                using var handler = CodexCliIdentityService.CreateDaemonHandler(socketPath);
                                var connect = handler.ConnectCallback!;
                                // Suppressed ExecutionContext could revert to this process's
                                // owner token and still pass access. Check the real pool callback.
                                handler.ConnectCallback = async (context, token) =>
                                {
                                    if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
                                    using var identity = WindowsIdentity.GetCurrent();
                                    Assert.NotEqual(TokenImpersonationLevel.None, identity.ImpersonationLevel);
                                    return await connect(context, token);
                                };
                                using var invoker = new HttpMessageInvoker(handler);
                                using var request = new HttpRequestMessage(HttpMethod.Get, "http://localhost/");
                                using var response = await invoker.SendAsync(request, stop.Token);
                                response.EnsureSuccessStatusCode();
                                Assert.Equal("caller", await response.Content.ReadAsStringAsync(stop.Token));
                                await serving;
                            }
                            finally
                            {
                                await stop.CancelAsync();
                                try { await serving; }
                                catch (OperationCanceledException) { }
                            }
                        }
                        return await File.ReadAllTextAsync(file, timeout.Token);
                    });
                    Assert.Equal("caller-private fixture", text);
                    // The nested caller scope must restore the denied outer token.
                    Assert.False(File.Exists(file));
                    Assert.Throws<UnauthorizedAccessException>(() => File.ReadAllText(file));
                    if (socketProbe) await AssertSocketDenied();
                });
            }
            Assert.Equal("caller-private fixture", await File.ReadAllTextAsync(file, timeout.Token));

            async Task AssertSocketDenied()
            {
                var error = await Assert.ThrowsAsync<SocketException>(async () =>
                {
                    using var denied = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                    await denied.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), timeout.Token);
                });
                Assert.Equal(SocketError.AccessDenied, error.SocketErrorCode);
            }

            async Task ServeHttpAsync(CancellationToken token)
            {
                using var peer = await listener!.AcceptAsync(token);
                var bytes = new byte[1024];
                var request = new StringBuilder();
                while (!request.ToString().Contains("\r\n\r\n", StringComparison.Ordinal))
                {
                    var count = await peer.ReceiveAsync(bytes, SocketFlags.None, token);
                    Assert.True(count > 0);
                    request.Append(Encoding.ASCII.GetString(bytes, 0, count));
                    Assert.True(request.Length < 8192);
                }
                Assert.StartsWith("GET / HTTP/1.1\r\n", request.ToString(), StringComparison.Ordinal);
                using var stream = new NetworkStream(peer, ownsSocket: false);
                await stream.WriteAsync(Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 OK\r\nContent-Length: 6\r\nConnection: close\r\n\r\ncaller"), token);
            }
        }
        finally
        {
            Marshal.FreeHGlobal(sidMemory);
            listener?.Dispose();
            var resolved = Path.GetFullPath(directory);
            Assert.StartsWith(Path.GetFullPath(Path.GetTempPath()), resolved, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith("tlbx-caller-identity-", Path.GetFileName(resolved), StringComparison.Ordinal);
            Directory.Delete(resolved, recursive: true);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SidAndAttributes
    {
        public nint Sid;
        public uint Attributes;
    }

    // The test project deliberately does not enable unsafe source-generated interop.
#pragma warning disable SYSLIB1054
    [DllImport("advapi32.dll", SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateRestrictedToken(SafeAccessTokenHandle token, uint flags,
        uint disabledCount, ref SidAndAttributes disabled, uint deletedCount, nint deleted,
        uint restrictedCount, nint restricting, out SafeAccessTokenHandle result);
#pragma warning restore SYSLIB1054
}

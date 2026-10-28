using System.Security.Cryptography;
#if WINDOWS
using System.Security.AccessControl;
using System.Security.Principal;
#endif

namespace Ai.Tlbx.MidTerm.Services.Connect;

internal static class ConnectTransportLocator
{
    private static readonly Lock Gate = new();

    public static string Resolve(string settingsDirectory)
    {
        var configured = Environment.GetEnvironmentVariable("TLBX_CONNECT_TRANSPORT_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            var explicitPath = Path.GetFullPath(configured);
            if (!File.Exists(explicitPath)) throw new FileNotFoundException("Configured Connect transport was not found.", explicitPath);
            return explicitPath;
        }
        var filename = OperatingSystem.IsWindows() ? "mtconnect.exe" : "mtconnect";
        var adjacent = Path.Combine(AppContext.BaseDirectory, filename);
        if (File.Exists(adjacent)) return adjacent;
        lock (Gate)
        {
            using var resource = typeof(ConnectTransportLocator).Assembly.GetManifestResourceStream("mtconnect.runtime");
            if (resource is null)
                throw new FileNotFoundException("The tlbx Connect transport is missing from this installation.");
            var hash = Convert.ToHexStringLower(SHA256.HashData(resource));
            resource.Position = 0;
            var root = Path.Combine(settingsDirectory, "native", "connect");
            EnsurePrivateDirectory(root);
            var directory = Path.Combine(root, hash);
            EnsurePrivateDirectory(directory);
            var executable = Path.Combine(directory, filename);
            if (File.Exists(executable))
            {
                RejectReparsePoint(executable);
                using var existing = File.OpenRead(executable);
                if (Convert.ToHexStringLower(SHA256.HashData(existing)) != hash)
                    throw new IOException("The cached Connect transport failed its integrity check.");
                EnsureExecutable(executable);
                return executable;
            }
            var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                    resource.CopyTo(output);
                EnsureExecutable(temporary);
                try { File.Move(temporary, executable); }
                catch (IOException) when (File.Exists(executable))
                {
                    RejectReparsePoint(executable);
                    using var concurrent = File.OpenRead(executable);
                    if (Convert.ToHexStringLower(SHA256.HashData(concurrent)) != hash)
                        throw new IOException("The cached Connect transport failed its integrity check.");
                }
                return executable;
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    private static void EnsurePrivateDirectory(string directory)
    {
        Directory.CreateDirectory(directory);
        RejectReparsePoint(directory);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#if WINDOWS
        else
        {
            using var identity = WindowsIdentity.GetCurrent();
            var owner = identity.User ?? throw new IOException("Cannot identify the Connect transport owner.");
            var security = new DirectorySecurity();
            security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            var inheritance = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            foreach (var sid in new[]
            {
                owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
                new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
            })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    inheritance, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
        }
#endif
    }

    private static void EnsureExecutable(string path)
    {
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The Connect transport cache cannot contain symbolic links.");
    }
}

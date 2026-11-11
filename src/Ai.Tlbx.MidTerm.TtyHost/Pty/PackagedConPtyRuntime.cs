#if WINDOWS
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Globalization;

namespace Ai.Tlbx.MidTerm.TtyHost.Pty;

internal static class PackagedConPtyRuntime
{
    private const string PackageVersion = "1.24.260710001";
    private static readonly Lock Sync = new();
    private static IntPtr _nativeHandle;
    private static string? _nativePath;
    private static bool _resolverInstalled;

    public static void EnsureLoaded()
    {
        lock (Sync)
        {
            if (_nativeHandle != IntPtr.Zero)
            {
                return;
            }

            if (!_resolverInstalled)
            {
                NativeLibrary.SetDllImportResolver(typeof(PackagedConPtyRuntime).Assembly, ResolveImport);
                _resolverInstalled = true;
            }

            _nativePath = HasMatchingAdjacentRuntime(AppContext.BaseDirectory)
                ? Path.Combine(AppContext.BaseDirectory, "conpty.dll")
                : ExtractEmbeddedRuntime();
            _nativeHandle = NativeLibrary.Load(_nativePath);
        }
    }

    private static IntPtr ResolveImport(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, "conpty.dll", StringComparison.OrdinalIgnoreCase))
        {
            return IntPtr.Zero;
        }

        lock (Sync)
        {
            if (_nativeHandle != IntPtr.Zero)
            {
                return _nativeHandle;
            }

            if (string.IsNullOrWhiteSpace(_nativePath))
            {
                throw new DllNotFoundException("The packaged tlbx ConPTY runtime was not prepared.");
            }

            _nativeHandle = NativeLibrary.Load(_nativePath);
            return _nativeHandle;
        }
    }

    private static string ExtractEmbeddedRuntime()
    {
        var architecture = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var root = Path.Combine(Path.GetTempPath(), "tlbx-conpty", PackageVersion, architecture);
        Directory.CreateDirectory(root);

        ExtractResource("tlbx.conpty.conpty.dll", Path.Combine(root, "conpty.dll"));
        ExtractResource("tlbx.conpty.x64.OpenConsole.exe", Path.Combine(root, "x64", "OpenConsole.exe"));
        ExtractResource("tlbx.conpty.arm64.OpenConsole.exe", Path.Combine(root, "arm64", "OpenConsole.exe"));
        if (RuntimeInformation.ProcessArchitecture == Architecture.X86)
        {
            ExtractResource("tlbx.conpty.x86.OpenConsole.exe", Path.Combine(root, "x86", "OpenConsole.exe"));
        }

        return Path.Combine(root, "conpty.dll");
    }

    private static bool HasMatchingAdjacentRuntime(string root)
    {
        if (!ResourceMatchesFile("tlbx.conpty.conpty.dll", Path.Combine(root, "conpty.dll")) ||
            !ResourceMatchesFile("tlbx.conpty.x64.OpenConsole.exe", Path.Combine(root, "x64", "OpenConsole.exe")) ||
            !ResourceMatchesFile("tlbx.conpty.arm64.OpenConsole.exe", Path.Combine(root, "arm64", "OpenConsole.exe")))
        {
            return false;
        }

        return RuntimeInformation.ProcessArchitecture != Architecture.X86 ||
            ResourceMatchesFile("tlbx.conpty.x86.OpenConsole.exe", Path.Combine(root, "x86", "OpenConsole.exe"));
    }

    private static bool ResourceMatchesFile(string resourceName, string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return false;
            }

            using var resource = typeof(PackagedConPtyRuntime).Assembly.GetManifestResourceStream(resourceName);
            if (resource is null)
            {
                return false;
            }

            using var file = File.OpenRead(path);
            return CryptographicOperations.FixedTimeEquals(SHA256.HashData(resource), SHA256.HashData(file));
        }
        catch
        {
            return false;
        }
    }

    private static void ExtractResource(string resourceName, string destinationPath)
    {
        using var resource = typeof(PackagedConPtyRuntime).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded ConPTY resource is missing: {resourceName}");
        using var buffer = new MemoryStream(checked((int)resource.Length));
        resource.CopyTo(buffer);
        var bytes = buffer.GetBuffer().AsSpan(0, checked((int)buffer.Length));

        if (File.Exists(destinationPath) && HasExpectedHash(destinationPath, bytes))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        var temporaryPath = string.Concat(
            destinationPath,
            ".",
            Environment.ProcessId.ToString(CultureInfo.InvariantCulture),
            ".",
            Guid.NewGuid().ToString("N"),
            ".tmp");
        try
        {
            File.WriteAllBytes(temporaryPath, bytes);
            File.Move(temporaryPath, destinationPath, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporaryPath); } catch { }
        }

        if (!HasExpectedHash(destinationPath, bytes))
        {
            throw new IOException($"Extracted ConPTY runtime failed verification: {destinationPath}");
        }
    }

    private static bool HasExpectedHash(string path, ReadOnlySpan<byte> expectedBytes)
    {
        try
        {
            using var file = File.OpenRead(path);
            return CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(file),
                SHA256.HashData(expectedBytes));
        }
        catch
        {
            return false;
        }
    }
}
#endif

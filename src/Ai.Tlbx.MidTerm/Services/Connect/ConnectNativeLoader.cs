using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
#if WINDOWS
using System.Security.AccessControl;
using System.Security.Principal;
#endif

namespace Ai.Tlbx.MidTerm.Services.Connect;

/// <summary>Loads only the hashed RID-specific library closure embedded in mt.</summary>
internal static partial class ConnectNativeLoader
{
    internal const string ResourceName = "tlbx.native.connect.bundle";
    private const int MaximumFiles = 128;
    private const long MaximumBundleBytes = 512L * 1024 * 1024;
    private static readonly Lock Gate = new();
    private static ConnectNativeApi? _api;

    public static ConnectNativeApi GetApi(string settingsDirectory)
    {
        lock (Gate)
        {
            if (_api is not null) return _api;
            using var bundle = typeof(ConnectNativeLoader).Assembly.GetManifestResourceStream(ResourceName)
                ?? throw new IOException("This tlbx package does not contain its native WebRTC runtime.");
            var path = Extract(bundle, settingsDirectory, RuntimeInformation.RuntimeIdentifier);
            try
            {
                var handle = OperatingSystem.IsWindows() ? LoadWindows(path) : NativeLibrary.Load(path);
                // The runtime is process-wide; never unload it while native worker threads can exist.
                _api = new ConnectNativeApi(handle);
                return _api;
            }
            catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException)
            { throw new IOException("The packaged WebRTC runtime could not be loaded.", ex); }
        }
    }

    internal static string Extract(Stream bundle, string settingsDirectory, string runtimeIdentifier)
    {
        if (!bundle.CanSeek || bundle.Length > MaximumBundleBytes) throw new InvalidDataException("Invalid WebRTC bundle size.");
        bundle.Position = 0;
        var hash = Convert.ToHexStringLower(SHA256.HashData(bundle));
        bundle.Position = 0;
        using var archive = new ZipArchive(bundle, ZipArchiveMode.Read, leaveOpen: true);
        Manifest manifest;
        try { manifest = ReadManifest(archive, runtimeIdentifier); }
        catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new InvalidDataException("The WebRTC bundle manifest is invalid.", ex); }
        var native = Path.Combine(Path.GetFullPath(settingsDirectory), "native");
        EnsurePrivateDirectory(native);
        var root = Path.Combine(native, "connect");
        EnsurePrivateDirectory(root);
        var destination = Path.Combine(root, hash);
        if (Directory.Exists(destination))
        {
            VerifyDirectory(destination, manifest);
            return Path.Combine(destination, manifest.EntryPoint);
        }
        var staging = Path.Combine(root, ".tmp-" + Guid.NewGuid().ToString("N"));
        EnsurePrivateDirectory(staging);
        try
        {
            foreach (var entry in archive.Entries)
            {
                var target = Path.Combine(staging, entry.FullName);
                using var input = entry.Open();
                using var output = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                CopyBounded(input, output, entry.Length);
            }
            VerifyDirectory(staging, manifest);
            try { Directory.Move(staging, destination); }
            catch (IOException) when (Directory.Exists(destination)) { VerifyDirectory(destination, manifest); }
            return Path.Combine(destination, manifest.EntryPoint);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                RejectReparsePoint(staging);
                // Every file is flat and staging is our newly-created private directory.
                foreach (var file in Directory.EnumerateFiles(staging)) File.Delete(file);
                Directory.Delete(staging);
            }
        }
    }

    internal static string? ReadNotices()
    {
        using var bundle = typeof(ConnectNativeLoader).Assembly.GetManifestResourceStream(ResourceName);
        if (bundle is null) return null;
        using var archive = new ZipArchive(bundle, ZipArchiveMode.Read);
        var entry = archive.GetEntry("THIRD-PARTY-NOTICES.txt");
        if (entry is null || entry.Length > 2 * 1024 * 1024) throw new InvalidDataException("WebRTC notices are missing or too large.");
        using var input = entry.Open();
        using var reader = new StreamReader(input);
        return reader.ReadToEnd();
    }

    private static Manifest ReadManifest(ZipArchive archive, string rid)
    {
        if (archive.Entries.Count is < 2 or > MaximumFiles + 1) throw new InvalidDataException("Invalid WebRTC bundle file count.");
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            if (!SafeName(entry.FullName) || !names.Add(entry.FullName) || entry.Length > MaximumBundleBytes
                || (total += entry.Length) > MaximumBundleBytes) throw new InvalidDataException("Invalid WebRTC bundle entry.");
        }
        var metadata = archive.GetEntry("manifest.json") ?? throw new InvalidDataException("Missing WebRTC bundle manifest.");
        if (metadata.Length > 128 * 1024) throw new InvalidDataException("WebRTC manifest is too large.");
        using var input = metadata.Open();
        using var document = JsonDocument.Parse(input, new JsonDocumentOptions { MaxDepth = 8 });
        var value = document.RootElement;
        var entryPoint = value.GetProperty("entryPoint").GetString();
        var expected = rid.StartsWith("win-", StringComparison.Ordinal) ? "datachannel.dll"
            : rid.StartsWith("osx-", StringComparison.Ordinal) ? "libdatachannel.dylib" : "libdatachannel.so";
        if (value.GetProperty("version").GetInt32() != 1 || value.GetProperty("runtimeIdentifier").GetString() != rid
            || entryPoint != expected || value.GetProperty("versions").GetProperty("libdatachannel").GetString() != "0.24.6")
            throw new InvalidDataException("WebRTC runtime does not match this package or ABI.");
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        using var fileRecords = value.GetProperty("files").EnumerateArray();
        foreach (var item in fileRecords)
        {
            var name = item.GetProperty("name").GetString(); var digest = item.GetProperty("sha256").GetString();
            if (name is null || !SafeName(name) || name == "manifest.json" || digest is not { Length: 64 }
                || !digest.All(x => x is >= '0' and <= '9' or >= 'a' and <= 'f') || !files.TryAdd(name, digest))
                throw new InvalidDataException("Invalid WebRTC manifest file record.");
        }
        if (!files.ContainsKey(expected) || !files.ContainsKey("THIRD-PARTY-NOTICES.txt") || files.Count + 1 != names.Count
            || files.Keys.Any(name => !names.Contains(name))) throw new InvalidDataException("WebRTC manifest does not cover the complete bundle.");
        return new(expected, files);
    }

    private static bool SafeName(string name) => name.Length is > 0 and <= 128 && name.All(x => char.IsAsciiLetterOrDigit(x) || x is '.' or '-' or '_')
        && name[0] != '.' && (name is "manifest.json" or "THIRD-PARTY-NOTICES.txt" or "native-dependencies.spdx.json"
            || name.EndsWith(".dll", StringComparison.Ordinal) || name.EndsWith(".dylib", StringComparison.Ordinal)
            || name.EndsWith(".so", StringComparison.Ordinal) || name.Contains(".so.", StringComparison.Ordinal));

    private static void VerifyDirectory(string directory, Manifest manifest)
    {
        EnsurePrivateDirectory(directory);
        if (Directory.EnumerateDirectories(directory).Any() || Directory.EnumerateFiles(directory).Count() != manifest.Files.Count + 1)
            throw new InvalidDataException("Unexpected files in the WebRTC runtime cache.");
        foreach (var file in manifest.Files)
        {
            var path = Path.Combine(directory, file.Key);
            RejectReparsePoint(path);
            using var input = File.OpenRead(path);
            if (Convert.ToHexStringLower(SHA256.HashData(input)) != file.Value)
                throw new InvalidDataException("The cached WebRTC runtime failed its integrity check.");
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserExecute);
        }
        RejectReparsePoint(Path.Combine(directory, "manifest.json"));
    }

    private static void CopyBounded(Stream input, Stream output, long expected)
    {
        var bytes = new byte[65536]; long written = 0; int count;
        while ((count = input.Read(bytes)) != 0)
        {
            written += count;
            if (written > expected) throw new InvalidDataException("WebRTC bundle entry exceeds its declared size.");
            output.Write(bytes, 0, count);
        }
        if (written != expected) throw new InvalidDataException("Truncated WebRTC bundle entry.");
    }

    private static void EnsurePrivateDirectory(string directory)
    {
        Directory.CreateDirectory(directory); RejectReparsePoint(directory);
        if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
#if WINDOWS
        else
        {
            using var identity = WindowsIdentity.GetCurrent();
            var owner = identity.User ?? throw new IOException("Cannot identify the WebRTC runtime owner.");
            var security = new DirectorySecurity(); security.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null) })
                security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(directory).SetAccessControl(security);
        }
#endif
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("WebRTC runtime paths cannot contain links.");
    }

    private static nint LoadWindows(string path)
    {
        var handle = LoadLibraryEx(path, 0, 0x00000100 | 0x00000800); // DLL_LOAD_DIR | SYSTEM32
        return handle != 0 ? handle : throw new IOException("Cannot load the private WebRTC runtime.", new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError()));
    }
    [LibraryImport("kernel32.dll", EntryPoint = "LoadLibraryExW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
    private static partial nint LoadLibraryEx(string filename, nint reserved, uint flags);
    private sealed record Manifest(string EntryPoint, Dictionary<string, string> Files);
}

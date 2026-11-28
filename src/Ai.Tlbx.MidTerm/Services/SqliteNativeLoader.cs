using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Ai.Tlbx.MidTerm.Services;

/// <summary>
/// Makes the SQLite native library available before the first SqliteConnection.
///
/// Native AOT single-file publishes embed the RID-matching e_sqlite3 library as a
/// manifest resource (see Ai.Tlbx.MidTerm.csproj); this loader extracts it to
/// &lt;settings&gt;/native/sqlite, loads it, and registers the SQLitePCL provider explicitly so
/// Microsoft.Data.Sqlite's reflection-based bundle init never has to resolve the
/// library from the install directory (which only ever contains mt itself — release
/// archives and the web-only updater move single binaries, never sidecar files).
///
/// JIT builds (dev loop, unit tests) have no embedded resource and fall back to the
/// normal SQLitePCLRaw bundle init against the native asset next to the assembly.
/// </summary>
internal static class SqliteNativeLoader
{
    private static readonly Lock _lock = new();
    private static bool _initialized;

    public static void EnsureProvider(string settingsDirectory)
    {
        lock (_lock)
        {
            if (_initialized)
            {
                return;
            }

            var fileName = OperatingSystem.IsWindows() ? "e_sqlite3.dll"
                : OperatingSystem.IsMacOS() ? "libe_sqlite3.dylib"
                : "libe_sqlite3.so";
            using var stream = typeof(SqliteNativeLoader).Assembly.GetManifestResourceStream($"tlbx.native.{fileName}");
            if (stream is null)
            {
                SQLitePCL.Batteries_V2.Init();
                _initialized = true;
                return;
            }

            var libPath = ExtractNativeLibrary(stream, settingsDirectory, fileName);
            var handle = NativeLibrary.Load(libPath);
            NativeLibrary.SetDllImportResolver(
                typeof(SQLitePCL.SQLite3Provider_e_sqlite3).Assembly,
                (name, _, _) => name.Contains("e_sqlite3", StringComparison.Ordinal) ? handle : IntPtr.Zero);
            SQLitePCL.raw.SetProvider(new SQLitePCL.SQLite3Provider_e_sqlite3());
            _initialized = true;
        }
    }

    private static string ExtractNativeLibrary(Stream stream, string settingsDirectory, string fileName)
    {
        var digest = SHA256.HashData(stream);
        stream.Position = 0;
        var nativeDir = Path.Combine(settingsDirectory, "native", "sqlite", Convert.ToHexStringLower(digest));
        Directory.CreateDirectory(nativeDir);
        var libPath = Path.Combine(nativeDir, fileName);

        if (File.Exists(libPath))
        {
            using var cached = File.OpenRead(libPath);
            if (SHA256.HashData(cached).AsSpan().SequenceEqual(digest))
            {
                return libPath;
            }
        }

        var tempPath = $"{libPath}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var file = new FileStream(tempPath, FileMode.CreateNew, FileAccess.Write))
            {
                stream.CopyTo(file);
            }
            File.Move(tempPath, libPath, overwrite: true);
        }
        finally
        {
            File.Delete(tempPath);
        }

        return libPath;
    }
}

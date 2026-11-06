using System.Buffers.Binary;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Models.Update;
using Ai.Tlbx.MidTerm.Services.Updates;
namespace Ai.Tlbx.Builds.Server;

public interface IReleaseSignatureVerifier
{
    bool Verify(string directory, VersionManifest manifest, string rid, string version, string channel);
}
public sealed class ReleaseSignatureVerifier : IReleaseSignatureVerifier
{
    public bool Verify(string directory, VersionManifest manifest, string rid, string version, string channel) =>
        UpdateVerification.VerifyUpdate(directory, manifest, rid, version, channel);
}

public sealed class ArchiveVerifier(IReleaseSignatureVerifier signatures)
{
    private static readonly JsonSerializerOptions ManifestJson = new() { PropertyNameCaseInsensitive = true };
    private const long MaxExpanded = 2L * 1024 * 1024 * 1024;
    public async Task<byte[]> VerifyAsync(string archive, string sbom, string scratch, string rid, ReleaseIdentity release, CancellationToken ct)
    {
        Directory.CreateDirectory(scratch);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        long expanded = 0;
        async Task Extract(string name, Stream? input, long length, bool directory)
        {
            if (name.StartsWith("./", StringComparison.Ordinal)) name = name[2..];
            name = name.TrimEnd('/');
            if (name.Length == 0 && directory) return;
            if (name.Length is < 1 or > 200 || name.Contains('\\', StringComparison.Ordinal) || name.StartsWith('/') ||
                name.Split('/').Any(s => s.Length == 0 || s is "." or ".." || !s.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
                throw new BuildProtocolException(400, "Unsafe archive path");
            if (!names.Add(name) || names.Count > 256) throw new BuildProtocolException(400, "Duplicate or excessive archive entries");
            var destination = Path.Combine(scratch, name.Replace('/', Path.DirectorySeparatorChar));
            if (directory) { Directory.CreateDirectory(destination); return; }
            if (length < 0 || length > MaxExpanded - expanded || input is null) throw new BuildProtocolException(413, "Expanded archive exceeds limit");
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            await using var output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
            var buffer = new byte[65536]; int read;
            long copied = 0;
            while ((read = await input.ReadAsync(buffer, ct)) != 0)
            {
                copied += read; expanded += read;
                if (expanded > MaxExpanded || copied > length) throw new BuildProtocolException(413, "Expanded archive exceeds limit");
                await output.WriteAsync(buffer.AsMemory(0, read), ct);
            }
            if (copied != length) throw new BuildProtocolException(400, "Truncated archive entry");
        }
        if (archive.EndsWith(".zip", StringComparison.Ordinal))
        {
            await CheckZipDirectoryAsync(archive, ct);
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                var kind = (entry.ExternalAttributes >> 16) & 0xf000;
                if (kind != 0 && kind != 0x8000 && kind != 0x4000) throw new BuildProtocolException(400, "Archive links/devices are forbidden");
                var directory = entry.FullName.EndsWith('/');
                await using var data = directory ? null : entry.Open();
                await Extract(entry.FullName, data, entry.Length, directory);
            }
        }
        else
        {
            await using var file = File.OpenRead(archive);
            await using var gzip = new GZipStream(file, CompressionMode.Decompress);
            await using var tar = new TarReader(gzip);
            TarEntry? entry;
            while ((entry = await tar.GetNextEntryAsync(cancellationToken: ct)) is not null)
            {
                if (entry.EntryType is not (TarEntryType.Directory or TarEntryType.RegularFile or TarEntryType.V7RegularFile))
                    throw new BuildProtocolException(400, "Archive links/devices are forbidden");
                await Extract(entry.Name, entry.DataStream, entry.Length, entry.EntryType == TarEntryType.Directory);
            }
        }
        var manifestPath = Path.Combine(scratch, "version.json");
        if (!File.Exists(manifestPath) || new FileInfo(manifestPath).Length > 256 * 1024) throw new BuildProtocolException(400, "Missing/big signed version manifest");
        var bytes = await File.ReadAllBytesAsync(manifestPath, ct);
        using var manifestDocument = JsonDocument.Parse(bytes);
        if (manifestDocument.RootElement.ValueKind != JsonValueKind.Object) throw new BuildProtocolException(400, "Invalid version manifest");
        var propertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var properties = manifestDocument.RootElement.EnumerateObject())
            while (properties.MoveNext())
                if (!propertyNames.Add(properties.Current.Name)) throw new BuildProtocolException(400, "Ambiguous version manifest");
        var manifest = JsonSerializer.Deserialize<VersionManifest>(bytes, ManifestJson)
            ?? throw new BuildProtocolException(400, "Invalid version manifest");
        var required = rid.StartsWith("win-", StringComparison.Ordinal) ? new[] { "mt.exe", "mthost.exe", "mtagenthost.exe", "mttmux.exe" } : ["mt", "mthost", "mtagenthost"];
        if (manifest.Checksums is null || required.Any(n => !manifest.Checksums.ContainsKey(n)) ||
            !signatures.Verify(scratch, manifest, rid, release.Number, release.IsDev ? "dev" : "stable"))
            throw new BuildProtocolException(400, "Vendor signature or runtime checksum verification failed");
        foreach (var file in Directory.EnumerateFiles(scratch, "*", SearchOption.AllDirectories))
        {
            var name = Path.GetRelativePath(scratch, file).Replace('\\', '/');
            if (!IsKnownExtra(rid, name) && !manifest.Checksums.ContainsKey(name))
                throw new BuildProtocolException(400, "Unsigned archive payload");
        }
        if (new FileInfo(sbom).Length > 16 * 1024 * 1024) throw new BuildProtocolException(413, "SBOM exceeds limit");
        await using var sbomStream = File.OpenRead(sbom);
        using var document = await JsonDocument.ParseAsync(sbomStream, cancellationToken: ct);
        if (!document.RootElement.TryGetProperty("spdxVersion", out var spdx) || spdx.GetString() is not ("SPDX-2.2" or "SPDX-2.3") ||
            !document.RootElement.TryGetProperty("SPDXID", out var id) || id.GetString() != "SPDXRef-DOCUMENT")
            throw new BuildProtocolException(400, "Invalid SPDX document");
        return bytes;
    }
    internal static bool IsKnownExtra(string rid, string name)
    {
        // CI identity and the publisher's archive digest authenticate these exact
        // packaging extras; vendor signatures still authenticate every runtime.
        if (name is "version.json" or "SHA256SUMS.txt" or "runtime-inputs.json") return true;
        if (!rid.StartsWith("win-", StringComparison.Ordinal)) return false;
        return name is "THIRD-PARTY-LICENSES.txt" or "conpty.dll" or "arm64/OpenConsole.exe" or "x64/OpenConsole.exe"
            || (rid == "win-x86" && name == "x86/OpenConsole.exe");
    }    private static async Task CheckZipDirectoryAsync(string path, CancellationToken ct)
    {
        // Bound ZIP directory allocation before ZipArchive materializes entries.
        // Official archives are below 1 GiB/256 files and do not need ZIP64.
        await using var stream = File.OpenRead(path);
        var tail = new byte[(int)Math.Min(stream.Length, 65557)];
        stream.Position = stream.Length - tail.Length;
        await stream.ReadExactlyAsync(tail, ct);
        for (var i = tail.Length - 22; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) != 0x06054b50 ||
                BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20)) != tail.Length - i - 22) continue;
            var disk = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 4));
            var entries = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 10));
            var localEntries = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 8));
            var size = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 12));
            var offset = BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i + 16));
            if (disk != 0 || entries is < 1 or > 256 || localEntries != entries || size > 1024 * 1024 ||
                (long)offset + size > stream.Length - tail.Length + i)
                throw new BuildProtocolException(400, "ZIP directory exceeds supported bounds");
            return;
        }
        throw new BuildProtocolException(400, "Invalid ZIP directory");
    }
}

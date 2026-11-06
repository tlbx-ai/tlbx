using System.Security.Cryptography;
using System.Text.Json;
namespace Ai.Tlbx.Builds.Server;

public sealed class ReleaseStore : IDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly BuildOptions _options;
    private readonly ArchiveVerifier _archives;
    private readonly TimeProvider _clock;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _admission = new(4, 4);
    private string Staging => Path.Combine(_options.StorageRoot, "staging");
    private string Public => Path.Combine(_options.StorageRoot, "published");
    private string Work => Path.Combine(_options.StorageRoot, "work");
    public ReleaseStore(BuildOptions options, ArchiveVerifier archives, TimeProvider clock)
    {
        options.Validate(); _options = options; _archives = archives; _clock = clock;
        foreach (var path in new[] { options.StorageRoot, Staging, Work, Public })
        {
            NoLinks(path); Directory.CreateDirectory(path); NoLinks(path);
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, path == Public ? PublicDirectory : PrivateDirectory);
        }
        foreach (var channel in new[] { "dev", "main" })
        {
            Directory.CreateDirectory(Path.Combine(Public, channel));
            NoLinks(Path.Combine(Public, channel));
            Retain(channel);
        }
        CleanStaging();
    }
    public void Dispose() { _gate.Dispose(); _admission.Dispose(); }
    private const UnixFileMode PrivateDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PublicDirectory = PrivateDirectory | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute;
    private const UnixFileMode PublicFile = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead | UnixFileMode.OtherRead;

    public async Task<(ReleaseAsset Asset, bool Exists)> UploadAsync(ReleaseIdentity release, string name, Stream input, long? contentLength, CancellationToken ct)
    {
        var rid = AssetNames.Rid(name);
        var canonicalName = name == AssetNames.Sbom(rid) ? AssetNames.Sbom(rid) : AssetNames.Archive(rid);
        if (contentLength is > BuildOptions.MaxAssetBytes or 0) throw new BuildProtocolException(413, "Asset size out of bounds");
        if (!await _admission.WaitAsync(0, ct)) throw new BuildProtocolException(429, "Upload concurrency exceeded");
        try
        {
            await _gate.WaitAsync(ct);
            try
            {
                CleanStaging(); CheckFloor(release);
                var stage = Stage(release); NoLinks(stage);
                if (!Directory.Exists(stage) && Directory.EnumerateDirectories(Staging).Take(65).Count() >= 64)
                    throw new BuildProtocolException(429, "Staging capacity exceeded");
                Directory.CreateDirectory(stage);
                if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(stage, PrivateDirectory);
                var temporary = Path.Combine(stage, ".upload-" + Guid.NewGuid().ToString("N"));
                try
                {
                    long size = 0;
                    using var hasher = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous))
                    {
                        var buffer = new byte[65536]; int count;
                        while ((count = await input.ReadAsync(buffer, ct)) > 0)
                        {
                            size += count;
                            if (size > BuildOptions.MaxAssetBytes) throw new BuildProtocolException(413, "Asset exceeds 1 GiB");
                            hasher.AppendData(buffer, 0, count);
                            await output.WriteAsync(buffer.AsMemory(0, count), ct);
                        }
                        await output.FlushAsync(ct);
                    }
                    if (size == 0 || (contentLength is not null && size != contentLength)) throw new BuildProtocolException(400, "Empty or truncated asset");
                    var asset = new ReleaseAsset(canonicalName, Convert.ToHexString(hasher.GetHashAndReset()).ToLowerInvariant(), size);
                    var completed = ReadCompleted(release);
                    if (completed is not null)
                    {
                        if (!completed.Publication.Assets.Contains(asset)) throw new BuildProtocolException(409, "Published assets are immutable");
                        return (asset, true);
                    }
                    var target = Path.GetFullPath(Path.Combine(stage, canonicalName));
                    if (!target.StartsWith(Path.GetFullPath(Staging) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                        || !target.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                        throw new BuildProtocolException(400, "Upload target escapes staging root");
                    NoLinks(target);
                    if (File.Exists(target))
                    {
                        var existing = await DescribeAsync(target, canonicalName, ct);
                        if (existing != asset) throw new BuildProtocolException(409, "Uploaded assets are immutable");
                        return (asset, true);
                    }
                    File.Move(temporary, target);
                    Directory.SetLastWriteTimeUtc(stage, _clock.GetUtcNow().UtcDateTime);
                    return (asset, false);
                }
                finally { File.Delete(temporary); }
            }
            finally { _gate.Release(); }
        }
        finally { _admission.Release(); }
    }

    public async Task<bool> PublishAsync(ReleaseIdentity release, PublishRequest request, PublisherIdentity publisher, CancellationToken ct)
    {
        ValidatePublication(request);
        await _gate.WaitAsync(ct);
        try
        {
            CheckFloor(release);
            var normalized = request with { SourceCommit = request.SourceCommit.ToLowerInvariant(), Assets = request.Assets.OrderBy(a => a.Name, StringComparer.Ordinal).ToArray() };
            var completed = ReadCompleted(release);
            if (completed is not null)
            {
                if (JsonSerializer.Serialize(completed.Publication, Json) != JsonSerializer.Serialize(normalized, Json))
                    throw new BuildProtocolException(409, "Published releases are immutable");
                return true;
            }
            if (!publisher.IsBackfill && !request.SourceCommit.Equals(publisher.Sha, StringComparison.OrdinalIgnoreCase))
                throw new BuildProtocolException(400, "Publisher commit mismatch");
            CleanStaging();
            var stage = Stage(release); NoLinks(stage);
            if (!Directory.Exists(stage)) throw new BuildProtocolException(400, "No staged assets");
            var stagedFiles = Directory.EnumerateFiles(stage).Select(Path.GetFileName).ToArray();
            if (stagedFiles.Length != normalized.Assets.Length || stagedFiles.Any(n => !normalized.Assets.Any(a => a.Name == n)))
                throw new BuildProtocolException(400, "Publication must include every staged asset");
            foreach (var asset in normalized.Assets)
            {
                var file = Path.GetFullPath(Path.Combine(stage, asset.Name));
                if (!file.StartsWith(Path.GetFullPath(Staging) + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || !file.StartsWith(stage + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    throw new BuildProtocolException(400, "Publication asset escapes staging root");
                NoLinks(file);
                if (!File.Exists(file) || await DescribeAsync(file, asset.Name, ct) != asset)
                    throw new BuildProtocolException(400, "Asset digest or length mismatch");
            }
            var working = Path.Combine(Work, Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(working);
            try
            {
                var prepared = Path.Combine(working, "release"); Directory.CreateDirectory(prepared);
                foreach (var rid in normalized.Assets.Select(a => AssetNames.Rid(a.Name)).Distinct(StringComparer.Ordinal))
                {
                    var archive = AssetNames.Archive(rid); var sbom = AssetNames.Sbom(rid);
                    if (!normalized.Assets.Any(a => a.Name == archive) || !normalized.Assets.Any(a => a.Name == sbom))
                        throw new BuildProtocolException(400, "Each platform needs its archive and SPDX pair");
                    var scratch = Path.Combine(working, "extract");
                    var originalManifest = await _archives.VerifyAsync(Path.Combine(stage, archive), Path.Combine(stage, sbom), scratch, rid, release, ct);
                    Directory.Delete(scratch, true);
                    var target = Path.Combine(prepared, rid); Directory.CreateDirectory(target);
                    foreach (var name in new[] { archive, sbom })
                    {
                        await using var from = File.OpenRead(Path.Combine(stage, name));
                        await using var to = new FileStream(Path.Combine(target, name), FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, FileOptions.Asynchronous);
                        await from.CopyToAsync(to, ct);
                    }
                    await File.WriteAllBytesAsync(Path.Combine(target, "version.json"), originalManifest, ct);
                }
                await File.WriteAllTextAsync(Path.Combine(prepared, "release.json"), JsonSerializer.Serialize(new CompletedRelease(release.Tag, normalized), Json), ct);
                SetPublicModes(prepared);
                Directory.Move(prepared, Destination(release));
                Retain(release.Channel);
                Directory.Delete(stage, true);
                return false;
            }
            finally { if (Directory.Exists(working)) Directory.Delete(working, true); }
        }
        finally { _gate.Release(); }
    }

    public Task<object[]> CatalogAsync(string channel, CancellationToken ct)
    {
        if (channel is not ("dev" or "main")) throw new BuildProtocolException(404, "Unknown channel");
        ct.ThrowIfCancellationRequested();
        // Only complete directories are moved here atomically. Reading three
        // immutable records must never wait behind a network upload or validation.
        var catalog = Completed(channel).Take(3).Select(r =>
        {
            var release = ReleaseIdentity.Parse(r.Tag);
            return (object)new
            {
                tag_name = r.Tag, prerelease = release.IsDev, draft = false, body = r.Publication.ReleaseNotes,
                html_url = $"https://github.com/tlbx-ai/tlbx/releases/tag/{r.Tag}", target_commitish = r.Publication.SourceCommit,
                assets = r.Publication.Assets.Select(a => new { name = a.Name, size = a.Size, digest = "sha256:" + a.Sha256,
                    browser_download_url = new Uri(_options.PublicOrigin, $"{release.Channel}/{release.Number}/{AssetNames.Rid(a.Name)}/{a.Name}").AbsoluteUri }).ToArray()
            };
        }).ToArray();
        return Task.FromResult(catalog);
    }
    public (string Path, string Digest)? PublicAsset(string channel, string version, string rid, string name)
    {
        var release = ReleaseIdentity.Parse("v" + version);
        if (release.Channel != channel || !AssetNames.Rids.Contains(rid, StringComparer.Ordinal)) return null;
        var completed = ReadCompleted(release); if (completed is null) return null;
        var asset = completed.Publication.Assets.FirstOrDefault(a => a.Name == name && AssetNames.Rid(a.Name) == rid);
        if (asset is null) return null;
        var path = Path.GetFullPath(Path.Combine(Destination(release), rid, name));
        if (!path.StartsWith(Path.GetFullPath(Public) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new BuildProtocolException(400, "Asset path escapes published root");
        return FileExistsWithoutLinks(path) ? (path, asset.Sha256) : null;
    }
    public string? Manifest(ReleaseIdentity release, string rid)
    {
        if (!AssetNames.Rids.Contains(rid, StringComparer.Ordinal) || ReadCompleted(release) is null) return null;
        var path = Path.GetFullPath(Path.Combine(Destination(release), rid, "version.json"));
        if (!path.StartsWith(Path.GetFullPath(Public) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new BuildProtocolException(400, "Manifest path escapes published root");
        return FileExistsWithoutLinks(path) ? path : null;
    }
    private static void ValidatePublication(PublishRequest request)
    {
        if (request.SourceCommit is null || request.SourceCommit.Length != 40 || !request.SourceCommit.All(char.IsAsciiHexDigit) ||
            request.ReleaseNotes is null || request.ReleaseNotes.Length > 32768 || request.Assets is null || request.Assets.Length is < 2 or > 12)
            throw new BuildProtocolException(400, "Invalid publication metadata or publisher commit");
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in request.Assets)
        {
            if (a is null || a.Name is null || !names.Add(a.Name) || a.Size is < 1 or > BuildOptions.MaxAssetBytes || a.Sha256 is null || a.Sha256.Length != 64 ||
                !a.Sha256.All(c => char.IsAsciiDigit(c) || c is >= 'a' and <= 'f')) throw new BuildProtocolException(400, "Invalid asset metadata");
            _ = AssetNames.Rid(a.Name);
        }
    }
    private static async Task<ReleaseAsset> DescribeAsync(string path, string name, CancellationToken ct)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream, ct);
        return new(name, Convert.ToHexString(hash).ToLowerInvariant(), stream.Length);
    }
    private string Stage(ReleaseIdentity r)
    {
        var root = Path.GetFullPath(Staging);
        var path = Path.GetFullPath(Path.Combine(root, r.Tag));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new BuildProtocolException(400, "Release staging path escapes root");
        return path;
    }
    private string Destination(ReleaseIdentity r)
    {
        var root = Path.GetFullPath(Public);
        var path = Path.GetFullPath(Path.Combine(root, r.Channel, r.Number));
        if (!path.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            throw new BuildProtocolException(400, "Release destination escapes published root");
        return path;
    }
    private static bool FileExistsWithoutLinks(string path)
    {
        try { NoLinks(path); return File.Exists(path); }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }
    private CompletedRelease? ReadCompleted(ReleaseIdentity r)
    {
        try
        {
            var path = Path.GetFullPath(Path.Combine(Destination(r), "release.json"));
            if (!path.StartsWith(Path.GetFullPath(Public) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                throw new BuildProtocolException(400, "Release metadata escapes published root");
            NoLinks(path);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
            return JsonSerializer.Deserialize<CompletedRelease>(stream, Json);
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }
    private IEnumerable<CompletedRelease> Completed(string channel) => Directory.EnumerateDirectories(Path.Combine(Public, channel))
        .Select(p => ReleaseIdentity.Parse("v" + Path.GetFileName(p))).Select(ReadCompleted).Where(r => r is not null).Select(r => r!)
        .OrderByDescending(r => ReleaseIdentity.Parse(r.Tag).Version);
    private string Floor(string channel) => Path.Combine(_options.StorageRoot, "retired-" + channel);
    private void CheckFloor(ReleaseIdentity release)
    {
        var path = Floor(release.Channel); NoLinks(path);
        if (File.Exists(path) && release.Version <= ReleaseIdentity.Parse(File.ReadAllText(path)).Version)
            throw new BuildProtocolException(409, "Release is outside the retained version window");
    }
    private void Retain(string channel)
    {
        var old = Completed(channel).Skip(3).ToArray();
        if (old.Length == 0) return;
        var newestRetired = old.MaxBy(r => ReleaseIdentity.Parse(r.Tag).Version)!;
        var floor = Floor(channel); NoLinks(floor);
        if (!File.Exists(floor) || ReleaseIdentity.Parse(File.ReadAllText(floor)).Version < ReleaseIdentity.Parse(newestRetired.Tag).Version)
        {
            File.WriteAllText(floor + ".tmp", newestRetired.Tag); File.Move(floor + ".tmp", floor, true);
        }
        foreach (var r in old) { var path = Destination(ReleaseIdentity.Parse(r.Tag)); NoLinks(path); Directory.Delete(path, true); }
    }
    private void CleanStaging()
    {
        foreach (var directory in Directory.EnumerateDirectories(Staging).Concat(Directory.EnumerateDirectories(Work)))
        {
            NoLinks(directory);
            if (_clock.GetUtcNow().UtcDateTime - Directory.GetLastWriteTimeUtc(directory) > TimeSpan.FromHours(24))
            {
                Directory.Delete(directory, true);
                continue;
            }
            if (Path.GetDirectoryName(directory) == Staging)
            {
                // Mutations share the exclusive gate: none of these temporary
                // streams can be active. Remove only our exact crashed-upload name.
                foreach (var file in Directory.EnumerateFiles(directory, ".upload-*"))
                {
                    var name = Path.GetFileName(file);
                    if (name.Length == 40 && name[8..].All(char.IsAsciiHexDigit))
                    {
                        NoLinks(file); File.Delete(file);
                    }
                }
            }
        }
        foreach (var directory in Directory.EnumerateDirectories(Work).OrderByDescending(Directory.GetLastWriteTimeUtc).Skip(8))
        {
            NoLinks(directory); Directory.Delete(directory, true);
        }
    }
    private static void NoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if ((File.Exists(current) || Directory.Exists(current)) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new BuildProtocolException(400, "Symlink/reparse storage paths are forbidden");
    }
    private static void SetPublicModes(string root)
    {
        if (OperatingSystem.IsWindows()) return;
        foreach (var path in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)) File.SetUnixFileMode(path, PublicFile);
        foreach (var path in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).Prepend(root)) File.SetUnixFileMode(path, PublicDirectory);
    }
}

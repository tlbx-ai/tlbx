using System.Globalization;
using System.Security.Cryptography;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using SkiaSharp;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>Immutable generated outputs. Handles never accept or resolve client file paths.</summary>
internal sealed class TlbxMcpArtifactStore : IDisposable
{
    private sealed record Entry(string Owner, string Name, string Mime, string Path, int Length, DateTimeOffset ExpiresAt, long Sequence);
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly IHttpContextAccessor _contexts;
    private readonly TimeProvider _clock;
    private readonly TimeSpan _ttl;
    private readonly int _maxCount;
    private readonly long _maxBytes;
    private readonly ITimer _timer;
    private long _bytes;
    private long _sequence;
    private bool _disposed;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tlbx-mcp-artifacts-" + Guid.NewGuid().ToString("N"));
    private const int ChunkSize = 4 * 1024 * 1024;

    public TlbxMcpArtifactStore(IHttpContextAccessor contexts)
        : this(contexts, TimeProvider.System, TimeSpan.FromHours(1), 128, 512L * 1024 * 1024) { }

    internal TlbxMcpArtifactStore(IHttpContextAccessor contexts, TimeProvider clock, TimeSpan ttl, int maxCount, long maxBytes)
    {
        if (ttl <= TimeSpan.Zero || maxCount < 1 || maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(ttl));
        _contexts = contexts; _clock = clock; _ttl = ttl; _maxCount = maxCount; _maxBytes = maxBytes;
        _timer = clock.CreateTimer(_ => Sweep(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    internal string StorageDirectory => _directory;

    internal ResourceLinkBlock Register(byte[] bytes, string name, string mime)
    {
        var owner = Owner();
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            SweepLocked();
            var uri = "tlbx-artifact://capture/" + Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(24));
            FileRecoveryStorage.EnsurePrivate(_directory);
            var path = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
            try { File.WriteAllBytes(path, bytes); }
            catch { File.Delete(path); throw; }
            var entry = new Entry(owner, name, mime, path, bytes.Length, _clock.GetUtcNow() + _ttl, ++_sequence);
            _entries[uri] = entry;
            _bytes += bytes.Length;
            // Always preserve the newest complete output, including one larger than the normal cache budget.
            while (_entries.Count > _maxCount || _bytes > _maxBytes && _entries.Count > 1)
            {
                var oldest = _entries.MinBy(pair => pair.Value.Sequence);
                if (!RemoveLocked(oldest.Key, oldest.Value))
                {
                    RemoveLocked(uri, entry);
                    throw new IOException("Generated artifact cache could not evict an older payload. The browser action must not be replayed.");
                }
            }
            return Link(uri, entry);
        }
    }

    internal static ImageContentBlock? PreviewPng(byte[] bytes)
    {
        using var data = SKData.CreateCopy(bytes);
        using var codec = SKCodec.Create(data);
        if (codec is null) return null;
        var scale = Math.Min(1f, 1600f / Math.Max(codec.Info.Width, codec.Info.Height));
        var size = codec.GetScaledDimensions(scale);
        // Some codecs cannot decode a scaled bitmap. Never allocate an unbounded raster.
        if ((long)size.Width * size.Height > 16_000_000) return null;
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var bitmap = new SKBitmap(info);
        if (codec.GetPixels(info, bitmap.GetPixels()) != SKCodecResult.Success) return null;
        using var image = SKImage.FromBitmap(bitmap);
        var ratio = Math.Min(1f, 1600f / Math.Max(size.Width, size.Height));
        var width = Math.Max(1, (int)(size.Width * ratio));
        var height = Math.Max(1, (int)(size.Height * ratio));
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        if (surface is null) return null;
        surface.Canvas.Clear(SKColors.White);
        surface.Canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear));
        using var thumbnail = surface.Snapshot();
        using var encoded = thumbnail.Encode(SKEncodedImageFormat.Jpeg, 75);
        return encoded is null ? null : ImageContentBlock.FromBytes(encoded.ToArray(), "image/jpeg");
    }

    private string Owner() => _contexts.HttpContext?.Items[TlbxMcpEndpoints.CallerIdentityItem] as string
        ?? throw new UnauthorizedAccessException("An authenticated MCP caller identity is required.");

    internal ValueTask<ListResourcesResult> ListAsync(RequestContext<ListResourcesRequestParams> request, CancellationToken ct)
        => ValueTask.FromResult(List(ct));

    internal ListResourcesResult List(CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var owner = Owner();
        lock (_gate)
        {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SweepLocked();
        return new ListResourcesResult
        {
            Resources = _entries.Where(pair => pair.Value.Owner == owner && pair.Value.ExpiresAt > _clock.GetUtcNow()).Select(pair => new Resource
            {
                Uri = pair.Key, Name = pair.Value.Name, MimeType = pair.Value.Mime, Size = pair.Value.Length,
                Description = Description(pair.Value)
            }).ToList()
        };
        }
    }

    internal ValueTask<ReadResourceResult> ReadAsync(RequestContext<ReadResourceRequestParams> request, CancellationToken ct)
        => ValueTask.FromResult(Read(request.Params?.Uri ?? "", ct));

    internal ReadResourceResult Read(string uri, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var owner = Owner();
        var parts = uri.Split('?', 2);
        lock (_gate)
        {
        ObjectDisposedException.ThrowIf(_disposed, this);
        SweepLocked();
        if (!_entries.TryGetValue(parts[0], out var entry) || entry.Owner != owner || entry.ExpiresAt <= _clock.GetUtcNow())
            throw new McpProtocolException("Artifact unavailable for this caller.", McpErrorCode.InvalidParams);
        var offset = 0;
        var length = entry.Length;
        if (parts.Length == 2)
        {
            var query = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(parts[1]);
            if (query.Count != 2 || !query.TryGetValue("offset", out var start) || start.Count != 1
                || !query.TryGetValue("length", out var count) || count.Count != 1
                || !int.TryParse(start[0], NumberStyles.None, CultureInfo.InvariantCulture, out offset)
                || !int.TryParse(count[0], NumberStyles.None, CultureInfo.InvariantCulture, out length)
                || offset < 0 || offset > entry.Length || length < 1 || length > ChunkSize)
                throw new McpProtocolException("Use nonnegative offset and length between 1 and 4194304.", McpErrorCode.InvalidParams);
            length = Math.Min(length, entry.Length - offset);
        }
        else if (length > ChunkSize)
            throw new McpProtocolException("Read this artifact in byte chunks: " + uri + "?offset=0&length=4194304. The resource size is the complete byte length.", McpErrorCode.InvalidParams);
        if (new FileInfo(entry.Path).LinkTarget is not null)
            throw new McpProtocolException("Artifact unavailable for this caller.", McpErrorCode.InvalidParams);
        using var stream = new FileStream(entry.Path, FileMode.Open, FileAccess.Read, FileShare.Read);
        stream.Position = offset;
        var bytes = new byte[length];
        stream.ReadExactly(bytes);
        return new ReadResourceResult
        {
            Contents = [BlobResourceContents.FromBytes(bytes, uri, entry.Mime)]
        };
        }
    }

    private ResourceLinkBlock Link(string uri, Entry entry) => new()
    {
        Uri = uri, Name = entry.Name, MimeType = entry.Mime, Size = entry.Length,
        Description = Description(entry)
    };

    private string Description(Entry entry) => "Complete generated artifact; byte chunks: ?offset=0&length=4194304. Expires "
        + entry.ExpiresAt.ToString("O", CultureInfo.InvariantCulture) + ". Cache evicts oldest first at "
        + _maxCount.ToString(CultureInfo.InvariantCulture) + " artifacts or " + _maxBytes.ToString(CultureInfo.InvariantCulture)
        + " bytes; a single oversized newest output is retained whole. Workspace originals are unaffected.";

    internal void Sweep()
    {
        lock (_gate) { if (!_disposed) SweepLocked(); }
    }

    private void SweepLocked()
    {
        var now = _clock.GetUtcNow();
        foreach (var pair in _entries.Where(pair => pair.Value.ExpiresAt <= now).ToArray())
            RemoveLocked(pair.Key, pair.Value);
    }

    private bool RemoveLocked(string uri, Entry entry)
    {
        try { File.Delete(entry.Path); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        _entries.Remove(uri);
        _bytes -= entry.Length;
        return true;
    }

    public void Dispose()
    {
        _timer.Dispose();
        lock (_gate)
        {
        if (_disposed) return;
        _disposed = true;
        // Only server-generated, registered paths are deleted; never workspace artifacts.
        foreach (var entry in _entries.Values)
        {
            try { File.Delete(entry.Path); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        _entries.Clear();
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        }
    }
}

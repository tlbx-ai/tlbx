using SkiaSharp;

namespace Ai.Tlbx.MidTerm.Services;

internal static class FileImagePreview
{
    private const int MaxEdge = 320;
    private const int MaxCacheBytes = 8 * 1024 * 1024;
    private const long MaxPixels = 64_000_000;
    private const long MaxEncodedBytes = 64 * 1024 * 1024;
    private static readonly Lock CacheLock = new();
    private static readonly Dictionary<string, Entry> Cache = new(OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private static readonly SemaphoreSlim DecodeGate = new(1, 1);
    private static int _cacheBytes;
    private static long _sequence;
    private readonly record struct Entry(long Length, long Stamp, byte[] Bytes, long Sequence);

    internal static async Task<byte[]?> GetAsync(FileInfo file, CancellationToken ct)
    {
        var path = file.FullName;
        // Cache hits must still prove OS read access under the session's identity.
        using var access = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (access.Length > MaxEncodedBytes) return null;
        var stamp = file.LastWriteTimeUtc.Ticks;
        byte[]? Cached()
        {
            lock (CacheLock)
            {
                if (!Cache.TryGetValue(path, out var value) || value.Length != file.Length || value.Stamp != stamp) return null;
                Cache[path] = value with { Sequence = ++_sequence };
                return value.Bytes;
            }
        }
        if (Cached() is { } cached) return cached;
        // A burst of images must not allocate a full decoded raster per request.
        await DecodeGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (Cached() is { } queued) return queued;
            ct.ThrowIfCancellationRequested();
            var bytes = Encode(path);
            if (bytes is null) return null;
            lock (CacheLock)
            {
                if (Cache.Remove(path, out var previous)) _cacheBytes -= previous.Bytes.Length;
                Cache[path] = new(file.Length, stamp, bytes, ++_sequence);
                _cacheBytes += bytes.Length;
                while (Cache.Count > 64 || _cacheBytes > MaxCacheBytes)
                {
                    var oldest = Cache.MinBy(static pair => pair.Value.Sequence);
                    Cache.Remove(oldest.Key);
                    _cacheBytes -= oldest.Value.Bytes.Length;
                }
            }
            return bytes;
        }
        finally { DecodeGate.Release(); }
    }

    private static byte[]? Encode(string path)
    {
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var stream = new SKManagedStream(file);
        using var codec = SKCodec.Create(stream);
        if (codec is null) return null;
        var original = codec.Info;
        if (original.Width <= 0 || original.Height <= 0 || (long)original.Width * original.Height > MaxPixels) return null;
        var scale = Math.Min(1f, (float)MaxEdge / Math.Max(original.Width, original.Height));
        var size = codec.GetScaledDimensions(scale);
        if (size.Width <= 0 || size.Height <= 0 || (long)size.Width * size.Height > MaxPixels) return null;
        var info = new SKImageInfo(size.Width, size.Height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using var decoded = new SKBitmap(info);
        if (codec.GetPixels(info, decoded.GetPixels()) != SKCodecResult.Success) return null;
        scale = Math.Min(1f, (float)MaxEdge / Math.Max(size.Width, size.Height));
        var width = Math.Max(1, (int)Math.Round(size.Width * scale));
        var height = Math.Max(1, (int)Math.Round(size.Height * scale));
        var orientation = (int)codec.EncodedOrigin;
        var swapped = orientation is >= 5 and <= 8;
        using var output = new SKBitmap(swapped ? height : width, swapped ? width : height, SKColorType.Rgba8888, SKAlphaType.Premul);
        using (var canvas = new SKCanvas(output))
        using (var image = SKImage.FromBitmap(decoded))
        {
            canvas.Clear(SKColors.Transparent);
            canvas.SetMatrix(BackgroundImageEncoder.OrientationMatrix(width, height, orientation));
            canvas.DrawImage(image, new SKRect(0, 0, width, height), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
        using var pixels = output.PeekPixels();
        using var encoded = SKWebpEncoder.Encode(pixels, new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, 80));
        return encoded?.ToArray();
    }
}

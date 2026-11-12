using SkiaSharp;

namespace Ai.Tlbx.MidTerm.Services;

internal static class BackgroundImageEncoder
{
    internal const int MaxEdge = 2048;
    internal const int WebpQuality = 85;
    private const long MaxPixels = 64_000_000;

    internal static byte[] Encode(byte[] upload)
    {
        using var data = SKData.CreateCopy(upload);
        using var codec = SKCodec.Create(data);
        if (codec is null || codec.EncodedFormat is not
            (SKEncodedImageFormat.Png or SKEncodedImageFormat.Jpeg or SKEncodedImageFormat.Webp))
        {
            throw new ArgumentException("Only valid PNG, JPG and WebP images are supported.");
        }
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > MaxPixels ||
            info.Width > 32768 || info.Height > 32768)
        {
            throw new ArgumentException("Background image must contain at most 64 megapixels and be no larger than 32768 pixels per side.");
        }
        using var decoded = new SKBitmap(new SKImageInfo(info.Width, info.Height,
            SKColorType.Rgba8888, SKAlphaType.Premul));
        if (codec.GetPixels(decoded.Info, decoded.GetPixels()) != SKCodecResult.Success)
        {
            throw new ArgumentException("Cannot read background image. Use a complete, valid PNG, JPG or WebP image.");
        }
        var scale = Math.Min(1d, (double)MaxEdge / Math.Max(info.Width, info.Height));
        var width = Math.Max(1, (int)Math.Round(info.Width * scale));
        var height = Math.Max(1, (int)Math.Round(info.Height * scale));
        var orientation = (int)codec.EncodedOrigin;
        var swapAxes = orientation is >= 5 and <= 8;
        using var output = new SKBitmap(swapAxes ? height : width, swapAxes ? width : height,
            SKColorType.Rgba8888, SKAlphaType.Opaque);
        using (var canvas = new SKCanvas(output))
        using (var image = SKImage.FromBitmap(decoded))
        {
            // Preserve the existing black backdrop for transparent uploads. Premultiplied
            // decoding prevents hidden RGB from bleeding into the resized edges.
            canvas.Clear(SKColors.Black);
            canvas.SetMatrix(OrientationMatrix(width, height, orientation));
            canvas.DrawImage(image, new SKRect(0, 0, width, height),
                new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));
        }
        using var pixels = output.PeekPixels();
        using var encoded = SKWebpEncoder.Encode(pixels,
            new SKWebpEncoderOptions(SKWebpEncoderCompression.Lossy, WebpQuality));
        return encoded?.ToArray() ?? throw new InvalidOperationException("Cannot encode background as WebP.");
    }

    internal static SKMatrix OrientationMatrix(int width, int height, int orientation) =>
        orientation switch
        {
            2 => new SKMatrix(-1, 0, width, 0, 1, 0, 0, 0, 1),
            3 => new SKMatrix(-1, 0, width, 0, -1, height, 0, 0, 1),
            4 => new SKMatrix(1, 0, 0, 0, -1, height, 0, 0, 1),
            5 => new SKMatrix(0, 1, 0, 1, 0, 0, 0, 0, 1),
            6 => new SKMatrix(0, -1, height, 1, 0, 0, 0, 0, 1),
            7 => new SKMatrix(0, -1, height, -1, 0, width, 0, 0, 1),
            8 => new SKMatrix(0, 1, 0, -1, 0, width, 0, 0, 1),
            _ => SKMatrix.Identity
        };
}

using System.Buffers.Binary;
using Ai.Tlbx.MidTerm.Services;
using SkiaSharp;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class BackgroundImageEncoderTests
{
    internal static byte[] CreatePng(int width, int height, byte alpha = 255)
    {
        return CreateImage(width, height, SKEncodedImageFormat.Png, alpha);
    }

    internal static byte[] CreateImage(int width, int height, SKEncodedImageFormat format, byte alpha = 255)
    {
        using var bitmap = new SKBitmap(width, height);
        bitmap.Erase(new SKColor(240, 120, 60, alpha));
        using var image = SKImage.FromBitmap(bitmap);
        using var data = image.Encode(format, 95);
        return data.ToArray();
    }

    [Theory]
    [InlineData(4096, 512, 2048, 256)]
    [InlineData(512, 4096, 256, 2048)]
    [InlineData(65, 33, 65, 33)]
    public void Encode_PreservesAspectRatioWithoutUpscaling(int width, int height, int expectedWidth, int expectedHeight)
    {
        var webp = BackgroundImageEncoder.Encode(CreatePng(width, height));
        Assert.True(webp.AsSpan(0, 4).SequenceEqual("RIFF"u8));
        Assert.True(webp.AsSpan(8, 4).SequenceEqual("WEBP"u8));
        using var image = SKBitmap.Decode(webp);
        Assert.Equal(expectedWidth, image.Width);
        Assert.Equal(expectedHeight, image.Height);

    }

    [Theory]
    [InlineData(128, 120)]
    public void Encode_CompositesTransparencyOnBlack(byte alpha, int expectedRed)
    {
        using var image = SKBitmap.Decode(BackgroundImageEncoder.Encode(CreatePng(16, 16, alpha)));
        Assert.InRange((int)image.GetPixel(8, 8).Red, expectedRed - 3, expectedRed + 3);
    }

    [Fact]
    public void Encode_RejectsExcessiveDimensionsBeforeDecodingPixels()
    {
        var png = CreatePng(1, 1);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), 100_000);
        BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), 100_000);
        Assert.Throws<ArgumentException>(() => BackgroundImageEncoder.Encode(png));
    }

    [Theory]
    [InlineData(6, 23, 8)]
    [InlineData(8, 8, 55)]
    public void Encode_AppliesExifOrientationAndRemovesMetadata(byte orientation, int redX, int redY)
    {
        using var bitmap = new SKBitmap(64, 32);
        bitmap.Erase(SKColors.Blue);
        using (var canvas = new SKCanvas(bitmap))
        using (var paint = new SKPaint { Color = SKColors.Red })
        {
            canvas.DrawRect(0, 0, 16, 16, paint);
        }
        using var source = SKImage.FromBitmap(bitmap);
        using var data = source.Encode(SKEncodedImageFormat.Jpeg, 100);
        var jpeg = data.ToArray();
        // APP1 / little-endian TIFF, IFD0 orientation.
        byte[] exif = [255, 225, 0, 34, 69, 120, 105, 102, 0, 0,
            73, 73, 42, 0, 8, 0, 0, 0, 1, 0, 18, 1, 3, 0, 1, 0, 0, 0, 6, 0, 0, 0, 0, 0, 0, 0];
        exif[28] = orientation;
        byte[] withExif = [.. jpeg.AsSpan(0, 2), .. exif, .. jpeg.AsSpan(2)];
        var normalized = BackgroundImageEncoder.Encode(withExif);
        using var image = SKBitmap.Decode(normalized);
        Assert.Equal(orientation >= 5 ? 32 : 64, image.Width);
        Assert.Equal(orientation >= 5 ? 64 : 32, image.Height);
        Assert.True(image.GetPixel(redX, redY).Red > 220);
        Assert.True(image.GetPixel(redX, redY).Blue < 35);
        Assert.Equal(-1, normalized.AsSpan().IndexOf("Exif"u8));
    }

    [Fact]
    public void Encode_AcceptsWebpUploads()
    {
        using var image = SKBitmap.Decode(BackgroundImageEncoder.Encode(CreateImage(33, 17, SKEncodedImageFormat.Webp)));
        Assert.Equal(33, image.Width);
        Assert.Equal(17, image.Height);
    }
}

using System.IO.Compression;
using System.Text;
using Ai.Tlbx.MidTerm.Services.StaticFiles;
using Ai.Tlbx.MidTerm.Startup;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class DevAssetHtmlTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tlbx-dev-html-" + Guid.NewGuid().ToString("N"));
    private const string Html = "<script src=\"/js/terminal.min.js?v=old\"></script><link href=\"/css/app.css?v=old\" rel=\"stylesheet\">";

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Overlay_RewritesActualHtmlFromSourceOrCompressedPublishedAsset(bool published)
    {
        Directory.CreateDirectory(_directory);
        if (published)
        {
            await using var output = File.Create(Path.Combine(_directory, "index.html.br"));
            await using var compressor = new BrotliStream(output, CompressionLevel.Optimal);
            await compressor.WriteAsync(Encoding.UTF8.GetBytes(Html));
        }
        else await File.WriteAllTextAsync(Path.Combine(_directory, "index.html"), Html);
        using var provider = new PhysicalFileProvider(_directory);
        var actual = await ServerSetup.ReadHtmlEntryPointAsync(provider, "/index.html", CancellationToken.None);
        Assert.Equal(Html, actual);
        var overlay = StaticAssetCacheHeaders.RewriteDevAssetUrls(
            StaticAssetCacheHeaders.StampHtmlAssetUrls(actual!, "dev-probe"), "https://127.0.0.1:2110");
        Assert.Contains("src=\"https://127.0.0.1:2110/js/terminal.min.js?v=dev-probe\"", overlay, StringComparison.Ordinal);
        Assert.Contains("href=\"https://127.0.0.1:2110/css/app.css?v=dev-probe\"", overlay, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Overlay_PrefersUpdatedUncompressedHtmlAndReturnsNullForMissingAsset()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Path.Combine(_directory, "index.html"), Html);
        await File.WriteAllTextAsync(Path.Combine(_directory, "index.html.br"), "stale invalid compressed data");
        using var provider = new PhysicalFileProvider(_directory);
        Assert.Equal(Html, await ServerSetup.ReadHtmlEntryPointAsync(provider, "/index.html", CancellationToken.None));
        Assert.Null(await ServerSetup.ReadHtmlEntryPointAsync(provider, "/missing.html", CancellationToken.None));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

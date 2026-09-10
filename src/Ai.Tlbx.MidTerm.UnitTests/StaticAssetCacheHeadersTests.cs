using Ai.Tlbx.MidTerm.Services.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public class StaticAssetCacheHeadersTests
{
    [Theory]
    [InlineData("", "/index.html")]
    [InlineData("/", "/index.html")]
    [InlineData("/login.html", "/login.html")]
    public void ResolveHtmlEntryPointPath_MapsTheDefaultDocumentBeforeCustomHtmlMiddleware(
        string requestPath,
        string expected)
    {
        Assert.Equal(expected, StaticAssetCacheHeaders.ResolveHtmlEntryPointPath(requestPath));
    }

    [Fact]
    public void CreateETag_SamePathAndMetadata_ReturnsStableValue()
    {
        var fileInfo = new TestFileInfo(1024, new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero));

        var first = StaticAssetCacheHeaders.CreateETag("/js/terminal.min.js", fileInfo);
        var second = StaticAssetCacheHeaders.CreateETag("/js/terminal.min.js", fileInfo);

        Assert.Equal(first, second);
    }

    [Fact]
    public void CreateETag_DifferentPath_ReturnsDifferentValue()
    {
        var fileInfo = new TestFileInfo(1024, new DateTimeOffset(2026, 3, 10, 12, 0, 0, TimeSpan.Zero));

        var first = StaticAssetCacheHeaders.CreateETag("/js/terminal.min.js", fileInfo);
        var second = StaticAssetCacheHeaders.CreateETag("/js/webAudioAccess.js", fileInfo);

        Assert.NotEqual(first, second, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("/index.html", "public, max-age=0, must-revalidate")]
    [InlineData("/css/app.css", "public, max-age=0, must-revalidate")]
    [InlineData("/js/terminal.min.js", "public, max-age=0, must-revalidate")]
    [InlineData("/site.webmanifest", "public, max-age=0, must-revalidate")]
    [InlineData("/img/logo.png", "public, max-age=86400")]
    public void GetCacheControl_ReturnsExpectedPolicy(string path, string expected)
    {
        Assert.Equal(expected, StaticAssetCacheHeaders.GetCacheControl(path));
    }

    [Theory]
    [InlineData("/fonts/CascadiaCode-Regular.woff2", true)]
    [InlineData("/fonts/midFont.woff", true)]
    [InlineData("/fonts/CascadiaCode-Regular.ttf", true)]
    [InlineData("/img/logo.png", false)]
    public void IsFontAsset_DetectsFontExtensions(string path, bool expected)
    {
        Assert.Equal(expected, StaticAssetCacheHeaders.IsFontAsset(path));
    }

    [Theory]
    [InlineData("/index.html", true)]
    [InlineData("/login.html", true)]
    [InlineData("/css/app.css", false)]
    public void IsHtmlEntryPoint_DetectsHtmlPages(string path, bool expected)
    {
        Assert.Equal(expected, StaticAssetCacheHeaders.IsHtmlEntryPoint(path));
    }

    [Fact]
    public void StampHtmlAssetUrls_ReplacesExistingAssetVersionQueries()
    {
        const string html = """
            <link rel="stylesheet" href="/css/app.css?v=oldhash" />
            <script src="/js/terminal.min.js?v=oldhash"></script>
            """;

        var stamped = StaticAssetCacheHeaders.StampHtmlAssetUrls(html, "dev-123");

        Assert.Contains("/css/app.css?v=dev-123", stamped, StringComparison.Ordinal);
        Assert.Contains("/js/terminal.min.js?v=dev-123", stamped, StringComparison.Ordinal);
        Assert.DoesNotContain("oldhash", stamped, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("https://localhost:2100", "https://localhost:2100")]
    [InlineData("https://127.0.0.1:2100/", "https://127.0.0.1:2100")]
    public void TryNormalizeLoopbackAssetOrigin_AcceptsLocalHttpsOrigin(string value, string expected)
    {
        Assert.True(StaticAssetCacheHeaders.TryNormalizeLoopbackAssetOrigin(value, out var origin));
        Assert.Equal(expected, origin);
    }

    [Theory]
    [InlineData("http://localhost:2100")]
    [InlineData("https://example.com:2100")]
    [InlineData("https://localhost:2100/assets")]
    [InlineData("https://localhost:443")]
    public void TryNormalizeLoopbackAssetOrigin_RejectsUnsafeOrigin(string value)
    {
        Assert.False(StaticAssetCacheHeaders.TryNormalizeLoopbackAssetOrigin(value, out _));
    }

    [Fact]
    public void RewriteDevAssetUrls_OnlyMovesScriptsAndStyles()
    {
        const string html = """
            <link rel="stylesheet" href="/css/app.css?v=stable">
            <script src='/js/terminal.min.js?v=stable'></script>
            <img src="/img/logo.svg?v=stable">
            """;

        var rewritten = StaticAssetCacheHeaders.RewriteDevAssetUrls(html, "https://localhost:2100");

        Assert.Contains("href=\"https://localhost:2100/css/app.css?v=stable\"", rewritten, StringComparison.Ordinal);
        Assert.Contains("src='https://localhost:2100/js/terminal.min.js?v=stable'", rewritten, StringComparison.Ordinal);
        Assert.Contains("src=\"/img/logo.svg?v=stable\"", rewritten, StringComparison.Ordinal);
    }

    private sealed class TestFileInfo : IFileInfo
    {
        public TestFileInfo(long length, DateTimeOffset lastModified)
        {
            Length = length;
            LastModified = lastModified;
        }

        public bool Exists => true;
        public long Length { get; }
        public string? PhysicalPath => null;
        public string Name => "test";
        public DateTimeOffset LastModified { get; }
        public bool IsDirectory => false;

        public Stream CreateReadStream()
        {
            return new MemoryStream();
        }
    }
}

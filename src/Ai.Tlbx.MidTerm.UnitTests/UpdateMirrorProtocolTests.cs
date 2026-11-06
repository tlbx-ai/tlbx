using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Models.Update;
using Ai.Tlbx.MidTerm.Services.Updates;
using Ai.Tlbx.MidTerm.Settings;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class UpdateMirrorProtocolTests : IDisposable
{
    private const string Tag = "v10.25.29-dev";
    private const string Asset = "mt-win-x64.zip";
    private const string Download = "https://builds.tlbx.ai/dev/10.25.29-dev/win-x64/mt-win-x64.zip";
    private const string Digest = "1ed17fdb0c4852fad830a00a648217b27a87e3a84126d1543c00ef77a4b4c7d2";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tlbx-mirror-test-" + Guid.NewGuid().ToString("N"));

    public UpdateMirrorProtocolTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, recursive: true);

    private SettingsService Settings()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("The isolated settings fixture uses Windows DPAPI.");
        var settings = new SettingsService(_directory);
        var values = settings.Load();
        values.UpdateChannel = "dev";
        settings.Save(values);
        return settings;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SignedMirrorDiscoveryCoalescesAndKeepsCachedMetadataDuringRetryAfter(bool absoluteRetryAfter)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var unavailable = false;
        using var handler = new MirrorHandler((request, _) =>
        {
            if (unavailable)
            {
                var response = new HttpResponseMessage(HttpStatusCode.ServiceUnavailable);
                response.Headers.RetryAfter = absoluteRetryAfter
                    ? new RetryConditionHeaderValue(clock.GetUtcNow() + TimeSpan.FromSeconds(120))
                    : new RetryConditionHeaderValue(TimeSpan.FromSeconds(120));
                return Task.FromResult(response);
            }
            return Task.FromResult(Json(request.RequestUri!.AbsolutePath.EndsWith("version.json", StringComparison.Ordinal) ? VendorManifest : Catalog()));
        });
#pragma warning disable IDISP014 // One client belongs to each isolated fake transport.
        using var http = new HttpClient(handler, disposeHandler: false);
#pragma warning restore IDISP014
        using var service = new UpdateService(Settings(), null, http, clock, "10.25.28-dev");
        var first = service.CheckForUpdateAsync();
        Assert.Same(first, service.CheckForUpdateAsync());
        var found = await first;
        Assert.True(found!.Available);
        Assert.Null(found.CheckError);
        Assert.Equal("10.25.29-dev", found.LatestVersion);
        Assert.Equal(Download, found.DownloadUrl);
        Assert.Equal(Digest, found.ArchiveSha256);
        Assert.Equal(new[] { "/v1/channels/dev/releases.json", "/v1/releases/v10.25.29-dev/win-x64/version.json" }, handler.Paths, StringComparer.Ordinal);
        clock.Advance(TimeSpan.FromSeconds(29));
        Assert.Same(first, service.CheckForUpdateAsync());
        unavailable = true;
        clock.Advance(TimeSpan.FromSeconds(2));
        var failed = await service.CheckForUpdateAsync();
        Assert.Equal(found.DownloadUrl, failed!.DownloadUrl);
        Assert.Equal(found.LatestVersion, failed.LatestVersion);
        Assert.Equal(found.Type, failed.Type);
        Assert.Contains("503", failed.CheckError, StringComparison.Ordinal);
        Assert.Equal(3, handler.Paths.Count);
        clock.Advance(TimeSpan.FromSeconds(119));
        await service.CheckForUpdateAsync();
        Assert.Equal(3, handler.Paths.Count);
        unavailable = false;
        clock.Advance(TimeSpan.FromSeconds(2));
        var repaired = await service.CheckForUpdateAsync();
        Assert.Null(repaired!.CheckError);
        Assert.Equal(5, handler.Paths.Count);
    }

    [Fact]
    public async Task MissingDevPlatformReadsMainCatalogWithoutAnyGithubFallback()
    {
        using var handler = new MirrorHandler((_, _) => Task.FromResult(Json("[]")));
#pragma warning disable IDISP014
        using var http = new HttpClient(handler, disposeHandler: false);
#pragma warning restore IDISP014
        using var service = new UpdateService(Settings(), null, http);
        var result = await service.CheckForUpdateAsync();
        Assert.False(result?.Available ?? false);
        Assert.Null(result?.CheckError);
        Assert.Equal(new[] { "/v1/channels/dev/releases.json", "/v1/channels/main/releases.json" }, handler.Paths, StringComparer.Ordinal);
    }

    [Fact]
    public async Task FailedOrTamperedManifestNeverInventsAnUpdateType()
    {
        using var handler = new MirrorHandler((request, _) => Task.FromResult(Json(
            request.RequestUri!.AbsolutePath.EndsWith("version.json", StringComparison.Ordinal)
                ? VendorManifest.Replace("\"webOnly\": true", "\"webOnly\": false", StringComparison.Ordinal)
                : Catalog())));
#pragma warning disable IDISP014
        using var http = new HttpClient(handler, disposeHandler: false);
#pragma warning restore IDISP014
        using var service = new UpdateService(Settings(), null, http, currentVersion: "10.25.28-dev");
        var result = await service.CheckForUpdateAsync();
        Assert.False(result!.Available);
        Assert.Contains("signature", result.CheckError, StringComparison.Ordinal);
        Assert.Equal(2, handler.Paths.Count);
    }

    [Fact]
    public async Task CatalogCannotMislabelSignedDevArtifactsAsStable()
    {
        using var handler = new MirrorHandler((_, _) => Task.FromResult(Json(Catalog().Replace("\"prerelease\":true", "\"prerelease\":false", StringComparison.Ordinal))));
#pragma warning disable IDISP014
        using var http = new HttpClient(handler, disposeHandler: false);
#pragma warning restore IDISP014
        using var service = new UpdateService(Settings(), null, http, currentVersion: "10.25.28-dev");
        var result = await service.CheckForUpdateAsync();
        Assert.False(result!.Available);
        Assert.NotNull(result.CheckError);
        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task MetadataTimeoutIsBoundedAndDoesNotFallBackToGithub()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new MirrorHandler(async (_, token) =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Json("[]");
        });
#pragma warning disable IDISP014
        using var http = new HttpClient(handler, disposeHandler: false);
#pragma warning restore IDISP014
        using var service = new UpdateService(Settings(), null, http, clock);
        var checking = service.CheckForUpdateAsync();
        await entered.Task.WaitAsync(TestContext.Current.CancellationToken);
        clock.Advance(TimeSpan.FromSeconds(11));
        var result = await checking;
        Assert.False(result!.Available);
        Assert.False(string.IsNullOrWhiteSpace(result.CheckError));
        Assert.Single(handler.Paths);
    }

    [Fact]
    public async Task OversizeCatalogIsRejectedBeforeItsBodyIsBuffered()
    {
        using var handler = new MirrorHandler((_, _) =>
        {
            var response = Json("[]");
            response.Content.Headers.ContentLength = 2 * 1024 * 1024 + 1;
            return Task.FromResult(response);
        });
#pragma warning disable IDISP014
        using var http = new HttpClient(handler, disposeHandler: false);
#pragma warning restore IDISP014
        using var service = new UpdateService(Settings(), null, http);
        Assert.Contains("too large", (await service.CheckForUpdateAsync())!.CheckError, StringComparison.Ordinal);
        Assert.Single(handler.Paths);
    }

    [Theory]
    [InlineData("http://builds.tlbx.ai/dev/10.25.29-dev/win-x64/mt-win-x64.zip")]
    [InlineData("https://api.github.com/dev/10.25.29-dev/win-x64/mt-win-x64.zip")]
    [InlineData("https://builds.tlbx.ai.evil.example/dev/10.25.29-dev/win-x64/mt-win-x64.zip")]
    [InlineData("https://builds.tlbx.ai/dev/10.25.28-dev/win-x64/mt-win-x64.zip")]
    [InlineData("https://builds.tlbx.ai/dev/10.25.29-dev/win-x64/mt-win-x64.zip?redirect=github")]
    public async Task CatalogCannotRedirectNativeDownloadsOutsideTheExactPublishedAsset(string url)
    {
        Assert.False(UpdateService.IsDistributionAssetUrl(url, Tag, Asset));
        using var handler = new MirrorHandler((_, _) => Task.FromResult(Json(Catalog(url))));
#pragma warning disable IDISP014
        using var http = new HttpClient(handler, disposeHandler: false);
#pragma warning restore IDISP014
        using var service = new UpdateService(Settings(), null, http, currentVersion: "10.25.28-dev");
        var result = await service.CheckForUpdateAsync();
        Assert.False(result!.Available);
        Assert.NotNull(result.CheckError);
        Assert.Single(handler.Paths);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArchiveDigestAndSignedPayloadChecksBothRemainMandatory(bool correctArchiveDigest)
    {
        using var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            var manifest = archive.CreateEntry("version.json");
            await using var output = manifest.Open();
            await output.WriteAsync(Encoding.UTF8.GetBytes(VendorManifest), TestContext.Current.CancellationToken);
        }
        var bytes = zip.ToArray();
        using var handler = new MirrorHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(bytes) }));
#pragma warning disable IDISP014
        using var http = new HttpClient(handler, disposeHandler: false);
#pragma warning restore IDISP014
        using var service = new UpdateService(Settings(), null, http);
        var update = new UpdateInfo
        {
            LatestVersion = "10.25.29-dev", AssetName = Asset, DownloadUrl = Download, ArchiveSize = bytes.Length,
            ArchiveSha256 = correctArchiveDigest ? Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes)) : new string('0', 64)
        };
        Assert.Null(await service.DownloadUpdateAsync(update));
        // With the right archive hash, the vendor-signed manifest is authentic
        // but its binaries are absent: the independent payload checks reject it.
        Assert.Single(handler.Paths);
    }

    private static HttpResponseMessage Json(string json) => new(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") };
    private static string Catalog(string url = Download) => JsonSerializer.Serialize(new[]
    {
        new { tag_name = Tag, draft = false, prerelease = true, body = "Published signed package", html_url = "https://github.com/tlbx-ai/tlbx/releases/tag/" + Tag,
            assets = new[] { new { name = Asset, browser_download_url = url, size = 77374373, digest = "sha256:" + Digest } } }
    });

    private sealed class MirrorHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        public List<string> Paths { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal("https", request.RequestUri!.Scheme);
            Assert.Equal("builds.tlbx.ai", request.RequestUri.Host);
            Paths.Add(request.RequestUri.AbsolutePath);
            return send(request, cancellationToken);
        }
    }

    // Original public Dev29 win-x64 metadata, signed by the unchanged vendor key.
    private const string VendorManifest = """
    {
      "web": "10.25.29-dev",
      "pty": "10.25.20-dev",
      "protocol": 1,
      "minCompatiblePty": "10.25.20-dev",
      "webOnly": true,
      "signatureVersion": 2,
      "platform": "win-x64",
      "channel": "dev",
      "checksums": {
        "mthost.exe": "ab40d375fd766c45b1984948d5d8a3d1a9c9fef986f72fbd66b7374abb644417",
        "mt.exe": "6fe131c48e9746504ffefaf2fe1536b8d408e9d88a7c18849fa4339543a22bb8",
        "mtagenthost.exe": "37b66bd60caa846434aae3ccefc4909b684a7a3a07e8f8340033b2560c7b1e99",
        "mttmux.exe": "42ebde1f303c9df3947aa2b34c6386b5183d0830f4a4cca0336cabc710977411"
      },
      "signature": "MGUCMFyEJ/gF/u8RZdPtFkkJ4UtXqE4RYFys4pQsth68Z+cM5kt6tTeSZBW+GFygaUv3eAIxALodLOFwUrpZMSiRsp4JcgFac/lS60SOalxvUjRofSGgLwMBAZt5Mn5aaT6iZYykGw==",
      "signedPayload": "eyJzaWduYXR1cmVWZXJzaW9uIjoyLCJ3ZWIiOiIxMC4yNS4yOS1kZXYiLCJwdHkiOiIxMC4yNS4yMC1kZXYiLCJwcm90b2NvbCI6MSwibWluQ29tcGF0aWJsZVB0eSI6IjEwLjI1LjIwLWRldiIsIndlYk9ubHkiOnRydWUsInBsYXRmb3JtIjoid2luLXg2NCIsImNoYW5uZWwiOiJkZXYiLCJjaGVja3N1bXMiOnsibXQuZXhlIjoiNmZlMTMxYzQ4ZTk3NDY1MDRmZmVmYWYyZmUxNTM2YjhkNDA4ZTlkODhhN2MxODg0OWZhNDMzOTU0M2EyMmJiOCIsIm10YWdlbnRob3N0LmV4ZSI6IjM3YjY2YmQ2MGNhYTg0NjQzNGFhZTNjY2VmYzQ5MDliNjg0YTdhM2EwN2U4ZjgzNDAwMzNiMjU2MGM3YjFlOTkiLCJtdGhvc3QuZXhlIjoiYWI0MGQzNzVmZDc2NmM0NWIxOTg0OTQ4ZDVkOGEzZDFhOWM5ZmVmOTg2ZjcyZmJkNjZiNzM3NGFiYjY0NDQxNyIsIm10dG11eC5leGUiOiI0MmViZGUxZjMwM2M5ZGYzOTQ3YWEyYjM0YzYzODZiNTE4M2QwODMwZjRhNGNjYTAzMzZjYWJjNzEwOTc3NDExIn19",
      "metadataSignature": "MGQCMFfkud9UgHstfJCkOEY6DvTUkTh1gHDtF50WeUVoLJ4NFWPBNdxXBPulOhz7UcGbEgIwIq7UZzz88RXN9kkPZMD+oncQyqq8MfK7UZvMUA54ST6nopt2l36uLpI/zPMPmjl4"
    }
    """;
}

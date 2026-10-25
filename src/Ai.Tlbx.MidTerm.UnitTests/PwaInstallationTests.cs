using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Ai.Tlbx.MidTerm.Startup;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.FileProviders;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class PwaInstallationTests
{
    private static readonly HttpClient Client = new();
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InstallationMetadata_HasMachineNameForPlainAndPublishedBrotliAssets(bool compressed)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tlbx-pwa-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var assets = new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["site.webmanifest"] = "{\"name\":\"tlbx\",\"short_name\":\"tlbx\",\"id\":\"/index.html\",\"start_url\":\"/\",\"icons\":[{\"src\":\"/icon.png?v=123\"}]}",
                ["index.html"] = "<meta name=\"apple-mobile-web-app-title\" content=\"tlbx\" />",
                ["login.html"] = "<meta name=\"apple-mobile-web-app-title\" content=\"tlbx\" />",
                ["trust.html"] = "<meta name=\"apple-mobile-web-app-title\" content=\"tlbx\" />"
            };
            foreach (var (name, content) in assets)
            {
                if (compressed)
                {
                    await using var file = File.Create(Path.Combine(directory, name + ".br"));
                    await using var brotli = new BrotliStream(file, CompressionLevel.Optimal);
                    await brotli.WriteAsync(Encoding.UTF8.GetBytes(content), TestContext.Current.CancellationToken);
                }
                else await File.WriteAllTextAsync(Path.Combine(directory, name), content, TestContext.Current.CancellationToken);
            }
            var builder = WebApplication.CreateBuilder();
            builder.Logging.ClearProviders();
            builder.WebHost.UseSetting("urls", "http://127.0.0.1:0");
            await using var app = builder.Build();
            using var provider = new PhysicalFileProvider(directory);
            ServerSetup.ConfigureStaticFiles(app, provider, compressed, false, null);
            await app.StartAsync(TestContext.Current.CancellationToken);
            var address = app.Urls.Single();
            using var response = await Client.GetAsync(address + "/site.webmanifest", TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal("application/manifest+json", response.Content.Headers.ContentType?.MediaType);
            Assert.True(response.Headers.CacheControl?.NoCache);
            var manifest = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;
            Assert.Equal($"tlbx {Environment.MachineName}", manifest["name"]!.GetValue<string>());
            Assert.Equal($"tlbx {Environment.MachineName}", manifest["short_name"]!.GetValue<string>());
            Assert.Equal("/index.html", manifest["id"]!.GetValue<string>());
            Assert.Equal("/", manifest["start_url"]!.GetValue<string>());
            Assert.Equal("/icon.png?v=123", manifest["icons"]![0]!["src"]!.GetValue<string>());
            foreach (var path in new[] { "/", "/index.html", "/login", "/trust" })
            {
                var html = await Client.GetStringAsync(address + path, TestContext.Current.CancellationToken);
                Assert.Contains($"content=\"tlbx {Environment.MachineName}\"", html, StringComparison.Ordinal);
            }
            using var headRequest = new HttpRequestMessage(HttpMethod.Head, address + "/site.webmanifest");
            using var head = await Client.SendAsync(headRequest, TestContext.Current.CancellationToken);
            Assert.Equal(HttpStatusCode.OK, head.StatusCode);
            Assert.Empty(await head.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken));
            await app.StopAsync(TestContext.Current.CancellationToken);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}

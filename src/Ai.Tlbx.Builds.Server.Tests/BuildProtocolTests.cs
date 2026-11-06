using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.Builds.Server;
using Ai.Tlbx.MidTerm.Models.Update;
using Ai.Tlbx.MidTerm.Services.Updates;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
namespace Ai.Tlbx.Builds.Server.Tests;

public sealed class BuildProtocolTests
{
    [Theory]
    [InlineData("linux-x64")]
    [InlineData("win-x64")]
    public async Task RealHttpPublishesOnlyVerifiedPairsAndPreservesBytesRangeAndManifest(string rid)
    {
        await using var f = await Fixture.Start();
        var release = ReleaseIdentity.Parse("v1.2.3-dev");
        var pair = f.Package(release, rid);
        Assert.Equal("[]", await f.Client.GetStringAsync("/v1/channels/dev/releases.json"));
        using var unauthorized = await f.Client.PostAsync($"/api/v1/releases/{release.Tag}/publish", new StringContent("malformed JSON"));
        Assert.Equal(HttpStatusCode.Unauthorized, unauthorized.StatusCode);
        f.Client.DefaultRequestHeaders.Authorization = new("Bearer", "fixture");
        var archive = await f.Upload(release, pair[0]);
        Assert.Equal(HttpStatusCode.Created, archive.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Upload(release, pair[0])).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Upload(release, (pair[0].Name, new byte[] { 99 }))).StatusCode);
        using var incomplete = await f.Client.PostAsJsonAsync($"/api/v1/releases/{release.Tag}/publish", f.Publication(pair.Take(1)));
        Assert.Equal(HttpStatusCode.BadRequest, incomplete.StatusCode);
        Assert.Equal("[]", await f.Client.GetStringAsync("/v1/channels/dev/releases.json"));
        Assert.Equal(HttpStatusCode.Created, (await f.Upload(release, pair[1])).StatusCode);
        var publication = f.Publication(pair);
        using var published = await f.Client.PostAsJsonAsync($"/api/v1/releases/{release.Tag}/publish", publication);
        Assert.Equal(HttpStatusCode.Created, published.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await f.Client.PostAsJsonAsync($"/api/v1/releases/{release.Tag}/publish", publication)).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Client.PostAsJsonAsync($"/api/v1/releases/{release.Tag}/publish", publication with { ReleaseNotes = "changed" })).StatusCode);
        using var catalog = JsonDocument.Parse(await f.Client.GetStringAsync("/v1/channels/dev/releases.json"));
        var item = catalog.RootElement[0];
        Assert.Equal(release.Tag, item.GetProperty("tag_name").GetString());
        Assert.False(item.GetProperty("draft").GetBoolean());
        Assert.Equal("sha256:" + publication.Assets[0].Sha256, item.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("name").GetString() == pair[0].Name).GetProperty("digest").GetString());
        var path = $"/dev/{release.Number}/{rid}/{pair[0].Name}";
        Assert.Equal(pair[0].Bytes, await f.Client.GetByteArrayAsync(path));
        using var rangeRequest = new HttpRequestMessage(HttpMethod.Get, path);
        rangeRequest.Headers.Range = new(2, 7);
        using var range = await f.Client.SendAsync(rangeRequest);
        Assert.Equal(HttpStatusCode.PartialContent, range.StatusCode);
        Assert.Equal(pair[0].Bytes[2..8], await range.Content.ReadAsByteArrayAsync());
        Assert.NotNull(range.Headers.ETag);
        Assert.Equal(f.LastManifest, await f.Client.GetByteArrayAsync($"/v1/releases/{release.Tag}/{rid}/version.json"));
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.GetAsync($"/staging/{release.Tag}/{pair[0].Name}")).StatusCode);
    }

    [Fact]
    public async Task TamperedSignatureUnknownAssetsAndWrongDigestNeverBecomePublic()
    {
        await using var f = await Fixture.Start(); f.Client.DefaultRequestHeaders.Authorization = new("Bearer", "fixture");
        var release = ReleaseIdentity.Parse("v1.0.0");
        var pair = f.Package(release, "linux-x64", tamper: true);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Client.PutAsync($"/api/v1/releases/{release.Tag}/assets/evil.sh", new ByteArrayContent([1]))).StatusCode);
        foreach (var a in pair) Assert.Equal(HttpStatusCode.Created, (await f.Upload(release, a)).StatusCode);
        var publication = f.Publication(pair);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Client.PostAsJsonAsync($"/api/v1/releases/{release.Tag}/publish", publication)).StatusCode);
        Assert.Equal("[]", await f.Client.GetStringAsync("/v1/channels/main/releases.json"));
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.GetAsync($"/main/1.0.0/linux-x64/{pair[0].Name}")).StatusCode);
    }

    [Fact]
    public async Task RetentionUsesSemverAndHistoricalBackfillCannotDowngradeLatestOrReviveRetiredTags()
    {
        await using var f = await Fixture.Start(); f.Client.DefaultRequestHeaders.Authorization = new("Bearer", "fixture");
        foreach (var version in new[] { "1.0.9", "1.0.11", "1.0.10", "1.0.8", "1.0.12" })
        {
            var r = ReleaseIdentity.Parse("v" + version); var pair = f.Package(r, "linux-arm64");
            foreach (var a in pair) Assert.Equal(HttpStatusCode.Created, (await f.Upload(r, a)).StatusCode);
            Assert.Equal(HttpStatusCode.Created, (await f.Client.PostAsJsonAsync($"/api/v1/releases/{r.Tag}/publish", f.Publication(pair))).StatusCode);
        }
        using var catalog = JsonDocument.Parse(await f.Client.GetStringAsync("/v1/channels/main/releases.json"));
        Assert.Equal(new[] { "v1.0.12", "v1.0.11", "v1.0.10" }, catalog.RootElement.EnumerateArray().Select(v => v.GetProperty("tag_name").GetString()));
        Assert.Equal(HttpStatusCode.Conflict, (await f.Upload(ReleaseIdentity.Parse("v1.0.9"), (AssetNames.Archive("linux-arm64"), [1]))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.GetAsync("/main/1.0.9/linux-arm64/mt-linux-arm64.tar.gz")).StatusCode);
        Assert.Equal("[]", await f.Client.GetStringAsync("/v1/channels/dev/releases.json"));
    }

    [Fact]
    public async Task StoreBoundsUploadsBeforeReadingAndRejectsTraversalAndSymlinks()
    {
        await using var f = await Fixture.Start();
        var store = f.App.Services.GetRequiredService<ReleaseStore>();
        var r = ReleaseIdentity.Parse("v1.2.3-dev");
        var e = await Assert.ThrowsAsync<BuildProtocolException>(() => store.UploadAsync(r, "mt-linux-x64.tar.gz", new Unreadable(), BuildOptions.MaxAssetBytes + 1, default));
        Assert.Equal(413, e.Status);
        Assert.Throws<BuildProtocolException>(() => ReleaseIdentity.Parse("../../escape"));
        Assert.Throws<BuildProtocolException>(() => AssetNames.Rid("../mt-linux-x64.tar.gz"));
        var symlink = Path.Combine(f.Root, "alias");
        try { Directory.CreateSymbolicLink(symlink, Path.Combine(f.Root, "published")); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or PlatformNotSupportedException or IOException) { return; }
        Assert.Throws<BuildProtocolException>(() => new ReleaseStore(f.Options with { StorageRoot = symlink }, new ArchiveVerifier(new TestSignature(f.Key)), TimeProvider.System));
    }

    [Fact]
    public async Task IncompleteSecondPlatformAndWrongDigestAreAtomicFailures()
    {
        await using var f = await Fixture.Start(); f.Client.DefaultRequestHeaders.Authorization = new("Bearer", "fixture");
        var r = ReleaseIdentity.Parse("v2.0.0-dev");
        var first = f.Package(r, "linux-x64"); var second = f.Package(r, "osx-arm64");
        var incomplete = first.Concat(second.Take(1)).ToArray();
        foreach (var a in incomplete) Assert.Equal(HttpStatusCode.Created, (await f.Upload(r, a)).StatusCode);
        var badDigest = f.Publication(incomplete);
        badDigest.Assets[0] = badDigest.Assets[0] with { Sha256 = new string('0', 64) };
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Client.PostAsJsonAsync($"/api/v1/releases/{r.Tag}/publish", badDigest)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Client.PostAsJsonAsync($"/api/v1/releases/{r.Tag}/publish", f.Publication(incomplete))).StatusCode);
        Assert.Equal("[]", await f.Client.GetStringAsync("/v1/channels/dev/releases.json"));
        Assert.Equal(HttpStatusCode.NotFound, (await f.Client.GetAsync($"/dev/{r.Number}/linux-x64/{first[0].Name}")).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await f.Upload(r, second[1])).StatusCode);
        Assert.Equal(HttpStatusCode.Created, (await f.Client.PostAsJsonAsync($"/api/v1/releases/{r.Tag}/publish", f.Publication(first.Concat(second)))).StatusCode);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArchiveTraversalAndSymlinkAreRejectedBeforeAnyPublication(bool link)
    {
        await using var f = await Fixture.Start(); f.Client.DefaultRequestHeaders.Authorization = new("Bearer", "fixture");
        var r = ReleaseIdentity.Parse("v3.0.0-dev");
        using var bytes = new MemoryStream();
        using (var gzip = new GZipStream(bytes, CompressionMode.Compress, true))
        using (var tar = new TarWriter(gzip, leaveOpen: true))
        {
            var entry = new PaxTarEntry(link ? TarEntryType.SymbolicLink : TarEntryType.RegularFile, link ? "mt" : "../../escape");
            if (link) entry.LinkName = "/etc/passwd";
            else entry.DataStream = new MemoryStream([1, 2, 3]);
            tar.WriteEntry(entry); entry.DataStream?.Dispose();
        }
        var pair = f.Package(r, "linux-x64"); pair[0] = (pair[0].Name, bytes.ToArray());
        foreach (var a in pair) Assert.Equal(HttpStatusCode.Created, (await f.Upload(r, a)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Client.PostAsJsonAsync($"/api/v1/releases/{r.Tag}/publish", f.Publication(pair))).StatusCode);
        Assert.Equal("[]", await f.Client.GetStringAsync("/v1/channels/dev/releases.json"));
        Assert.False(File.Exists(Path.Combine(f.Root, "escape")));
    }

    [Fact]
    public async Task OldOrphanStagingIsCleanedAndFreshCapacityRemainsBounded()
    {
        await using var f = await Fixture.Start();
        var store = f.App.Services.GetRequiredService<ReleaseStore>();
        var staging = Path.Combine(f.Root, "staging");
        var expired = Path.Combine(staging, "v0.0.1-dev"); Directory.CreateDirectory(expired);
        Directory.SetLastWriteTimeUtc(expired, DateTime.UtcNow.AddDays(-2));
        await store.UploadAsync(ReleaseIdentity.Parse("v1.0.0-dev"), AssetNames.Archive("linux-x64"), new MemoryStream([1]), 1, default);
        Assert.False(Directory.Exists(expired));
        for (var i = 1; i < 64; i++) Directory.CreateDirectory(Path.Combine(staging, "v1.0." + i + "-dev"));
        var e = await Assert.ThrowsAsync<BuildProtocolException>(() => store.UploadAsync(ReleaseIdentity.Parse("v9.0.0-dev"), AssetNames.Archive("linux-x64"), new Unreadable(), 1, default));
        Assert.Equal(429, e.Status);
    }
    [Fact]
    public async Task DuplicateManifestMetadataAndOversizedZipDirectoryAreRejected()
    {
        await using var f = await Fixture.Start(); f.Client.DefaultRequestHeaders.Authorization = new("Bearer", "fixture");
        var r = ReleaseIdentity.Parse("v4.0.0-dev"); var pair = f.Package(r, "win-x64", duplicate: true);
        foreach (var a in pair) await f.Upload(r, a);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Client.PostAsJsonAsync($"/api/v1/releases/{r.Tag}/publish", f.Publication(pair))).StatusCode);
        r = ReleaseIdentity.Parse("v4.0.1-dev"); pair = f.Package(r, "win-x64");
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(pair[0].Bytes.AsSpan(pair[0].Bytes.Length - 22 + 12), 2 * 1024 * 1024);
        foreach (var a in pair) await f.Upload(r, a);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Client.PostAsJsonAsync($"/api/v1/releases/{r.Tag}/publish", f.Publication(pair))).StatusCode);
        Assert.Equal("[]", await f.Client.GetStringAsync("/v1/channels/dev/releases.json"));
    }
    [Theory]
    [InlineData("linux-x64", "runtime-inputs.json", true)]
    [InlineData("osx-arm64", "runtime-inputs.json", true)]
    [InlineData("win-x64", "THIRD-PARTY-LICENSES.txt", true)]
    [InlineData("win-x64", "arm64/OpenConsole.exe", true)]
    [InlineData("win-x86", "x86/OpenConsole.exe", true)]
    [InlineData("win-x64", "x86/OpenConsole.exe", false)]
    [InlineData("linux-x64", "conpty.dll", false)]
    [InlineData("linux-x64", "unsigned.sh", false)]
    public async Task OnlyExactPlatformPackagingExtrasMayBeUnsigned(string rid, string extra, bool allowed)
    {
        await using var f = await Fixture.Start(); f.Client.DefaultRequestHeaders.Authorization = new("Bearer", "fixture");
        var r = ReleaseIdentity.Parse("v5.0.0-dev"); var pair = f.Package(r, rid, extra: extra);
        foreach (var a in pair) await f.Upload(r, a);
        Assert.Equal(allowed ? HttpStatusCode.Created : HttpStatusCode.BadRequest,
            (await f.Client.PostAsJsonAsync($"/api/v1/releases/{r.Tag}/publish", f.Publication(pair))).StatusCode);
    }

    [Fact]
    public async Task CrashedUploadTemporaryIsRemovedBeforeImmediateRetryAndPublication()
    {
        await using var f = await Fixture.Start(); f.Client.DefaultRequestHeaders.Authorization = new("Bearer", "fixture");
        var r = ReleaseIdentity.Parse("v5.1.0-dev"); var pair = f.Package(r, "linux-x64");
        var stage = Path.Combine(f.Root, "staging", r.Tag); Directory.CreateDirectory(stage);
        var orphan = Path.Combine(stage, ".upload-" + Guid.NewGuid().ToString("N")); await File.WriteAllTextAsync(orphan, "partial bytes");
        var other = Path.Combine(f.Root, "staging", "v5.1.1-dev"); Directory.CreateDirectory(other);
        var unknown = Path.Combine(other, ".upload-unknown"); await File.WriteAllTextAsync(unknown, "do not delete unknown files");
        foreach (var a in pair) await f.Upload(r, a);
        Assert.False(File.Exists(orphan)); Assert.True(File.Exists(unknown));
        Assert.Equal(HttpStatusCode.Created, (await f.Client.PostAsJsonAsync($"/api/v1/releases/{r.Tag}/publish", f.Publication(pair))).StatusCode);
    }

    [Fact]
    public async Task CorsIsPublicOnlyWithoutCredentialPermission()
    {
        await using var f = await Fixture.Start(); f.Client.DefaultRequestHeaders.Authorization = new("Bearer", "fixture");
        f.Client.DefaultRequestHeaders.Add("Origin", "https://native.example");
        var r = ReleaseIdentity.Parse("v5.2.0-dev"); var pair = f.Package(r, "linux-x64");
        foreach (var a in pair) Assert.False((await f.Upload(r, a)).Headers.Contains("Access-Control-Allow-Origin"));
        using var post = await f.Client.PostAsJsonAsync($"/api/v1/releases/{r.Tag}/publish", f.Publication(pair));
        Assert.False(post.Headers.Contains("Access-Control-Allow-Origin"));
        foreach (var path in new[] { "/v1/channels/dev/releases.json", $"/v1/releases/{r.Tag}/linux-x64/version.json", $"/dev/{r.Number}/linux-x64/{pair[0].Name}" })
        {
            foreach (var method in new[] { HttpMethod.Get, HttpMethod.Head })
            {
                using var request = new HttpRequestMessage(method, path); using var response = await f.Client.SendAsync(request);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("*", Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
                Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
            }
        }
    }

    [Fact]
    public async Task CatalogContinuesServingCompleteReleasesDuringPausedHttpUpload()
    {
        await using var f = await Fixture.Start(); f.Client.DefaultRequestHeaders.Authorization = new("Bearer", "fixture");
        var previous = ReleaseIdentity.Parse("v6.0.0-dev"); var pair = f.Package(previous, "linux-x64");
        foreach (var a in pair) await f.Upload(previous, a);
        Assert.Equal(HttpStatusCode.Created, (await f.Client.PostAsJsonAsync($"/api/v1/releases/{previous.Tag}/publish", f.Publication(pair))).StatusCode);
        using var content = new PausedContent();
        var upload = f.Client.PutAsync("/api/v1/releases/v6.0.1-dev/assets/mt-linux-x64.tar.gz", content);
        try
        {
            await content.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var stage = Path.Combine(f.Root, "staging", "v6.0.1-dev");
            for (var i = 0; i < 500 && (!Directory.Exists(stage) || !Directory.EnumerateFiles(stage, ".upload-*").Any()); i++) await Task.Delay(10);
            Assert.True(Directory.Exists(stage) && Directory.EnumerateFiles(stage, ".upload-*").Any());
            var catalog = await f.Client.GetStringAsync("/v1/channels/dev/releases.json").WaitAsync(TimeSpan.FromSeconds(1));
            using var json = JsonDocument.Parse(catalog);
            Assert.Equal(previous.Tag, json.RootElement[0].GetProperty("tag_name").GetString());
            Assert.Single(json.RootElement.EnumerateArray());
        }
        finally { content.Resume.TrySetResult(); }
        Assert.Equal(HttpStatusCode.Created, (await upload).StatusCode);
    }

    [Fact]
    public async Task CatalogDoesNotWaitForPublishSignatureValidation()
    {
        BlockingSignatures? blocker = null;
        await using var f = await Fixture.Start(key => blocker = new BlockingSignatures(new TestSignature(key)));
        f.Client.DefaultRequestHeaders.Authorization = new("Bearer", "fixture");
        var r = ReleaseIdentity.Parse("v6.1.0-dev"); var pair = f.Package(r, "linux-x64");
        foreach (var a in pair) await f.Upload(r, a);
        var publish = f.Client.PostAsJsonAsync($"/api/v1/releases/{r.Tag}/publish", f.Publication(pair));
        try
        {
            await blocker!.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal("[]", await f.Client.GetStringAsync("/v1/channels/dev/releases.json").WaitAsync(TimeSpan.FromSeconds(1)));
        }
        finally { blocker!.Resume.Set(); }
        Assert.Equal(HttpStatusCode.Created, (await publish).StatusCode);
        blocker.Dispose();
    }

    [Fact]
    public async Task PublicReadersReturnNotFoundInsteadOf500WhenRetentionRemovesACompletedRecord()
    {
        await using var f = await Fixture.Start(); f.Client.DefaultRequestHeaders.Authorization = new("Bearer", "fixture");
        var r = ReleaseIdentity.Parse("v7.0.0-dev"); var pair = f.Package(r, "linux-x64");
        foreach (var a in pair) await f.Upload(r, a);
        Assert.Equal(HttpStatusCode.Created, (await f.Client.PostAsJsonAsync($"/api/v1/releases/{r.Tag}/publish", f.Publication(pair))).StatusCode);
        var published = Path.Combine(f.Root, "published", "dev", r.Number, "release.json");
        var hidden = Path.Combine(f.Root, "work", "reader-race.json");
        var moving = Task.Run(async () =>
        {
            try
            {
                for (var i = 0; i < 100; i++)
                {
                    File.Move(published, hidden); await Task.Delay(1);
                    File.Move(hidden, published); await Task.Delay(1);
                }
            }
            finally { if (File.Exists(hidden)) File.Move(hidden, published); }
        });
        try
        {
            for (var i = 0; i < 100; i++)
            {
                foreach (var path in new[] { $"/v1/releases/{r.Tag}/linux-x64/version.json", $"/dev/{r.Number}/linux-x64/{pair[0].Name}" })
                {
                    using var response = await f.Client.GetAsync(path);
                    Assert.True(response.StatusCode is HttpStatusCode.OK or HttpStatusCode.NotFound, $"Unexpected {response.StatusCode} while completed release record disappeared");
                }
            }
        }
        finally { await moving; }
    }
    private sealed class PausedContent : HttpContent
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override bool TryComputeLength(out long length) { length = 131072; return true; }
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            await stream.WriteAsync(new byte[65536]); await stream.FlushAsync(); Started.TrySetResult();
            await Resume.Task.WaitAsync(TimeSpan.FromSeconds(10)); await stream.WriteAsync(new byte[65536]);
        }
    }
    private sealed class BlockingSignatures(IReleaseSignatureVerifier inner) : IReleaseSignatureVerifier, IDisposable
    {
        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public ManualResetEventSlim Resume { get; } = new();
        public bool Verify(string d, VersionManifest m, string rid, string v, string c)
        {
            Started.TrySetResult();
            if (!Resume.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException("Protocol test did not resume validation");
            return inner.Verify(d, m, rid, v, c);
        }
        public void Dispose() => Resume.Dispose();
    }
    [Theory]
    [InlineData("../escape")]
    [InlineData("nested/../../escape")]
    [InlineData("../zip-containment-sibling/escape")]
    [InlineData("..\\escape")]
    [InlineData("rooted")]
    public async Task ZipEntriesCannotWriteOutsideTheirExactExtractionRoot(string name)
    {
        await using var f = await Fixture.Start();
        var scratch = Path.Combine(f.Root, "zip-containment");
        var archive = Path.Combine(f.Root, "malicious.zip");
        var sbom = Path.Combine(f.Root, "fixture.spdx.json");
        await File.WriteAllTextAsync(sbom, "{\"spdxVersion\":\"SPDX-2.3\",\"SPDXID\":\"SPDXRef-DOCUMENT\"}");
        using (var zip = ZipFile.Open(archive, ZipArchiveMode.Create))
        {
            using var output = zip.CreateEntry(name == "rooted" ? Path.Combine(f.Root, "escape") : name).Open();
            output.WriteByte(42);
        }
        var verifier = new ArchiveVerifier(new TestSignature(f.Key));
        var error = await Assert.ThrowsAsync<BuildProtocolException>(() => verifier.VerifyAsync(archive, sbom, scratch, "win-x64", ReleaseIdentity.Parse("v8.0.0-dev"), default));
        Assert.Equal(400, error.Status);
        Assert.False(File.Exists(Path.Combine(f.Root, "escape")));
        Assert.False(Directory.Exists(Path.Combine(f.Root, "zip-containment-sibling")));
    }

    [Fact]
    public async Task StorageContainmentRejectsEscapingReleaseRecordsBeforeOpeningAnyFile()
    {
        await using var f = await Fixture.Start();
        var store = f.App.Services.GetRequiredService<ReleaseStore>();
        // Public routes parse their tags; containment is also enforced at the
        // filesystem boundary when an internal caller supplies an invalid record.
        var forged = new ReleaseIdentity("v../../../outside", new Version(8, 0, 0), true);
        var upload = await Assert.ThrowsAsync<BuildProtocolException>(() => store.UploadAsync(forged, AssetNames.Archive("linux-x64"), new Unreadable(), 1, default));
        Assert.Equal(400, upload.Status);
        Assert.Equal(400, Assert.Throws<BuildProtocolException>(() => store.Manifest(forged, "linux-x64")).Status);
        Assert.Equal(400, Assert.Throws<BuildProtocolException>(() => store.PublicAsset("dev", "../../outside", "linux-x64", AssetNames.Archive("linux-x64"))).Status);
    }
    private sealed class Unreadable : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override int Read(byte[] b, int o, int c) => throw new InvalidOperationException("Body read before bounds check");
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long o, SeekOrigin s) => throw new NotSupportedException();
        public override void SetLength(long v) => throw new NotSupportedException(); public override void Write(byte[] b, int o, int c) => throw new NotSupportedException();
    }
    private sealed class TestAuth : IPublisherAuthenticator
    { public Task<PublisherIdentity?> AuthenticateAsync(string token, ReleaseIdentity r, CancellationToken ct) => Task.FromResult(token == "fixture" ? new PublisherIdentity(new string('a', 40), false) : null); }
    private sealed class TestSignature(ECDsa key) : IReleaseSignatureVerifier
    {
        public bool Verify(string d, VersionManifest m, string r, string v, string c) => UpdateVerification.VerifyUpdateForTesting(d, m, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()), r, v, c);
    }
    private sealed class Fixture : IAsyncDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "tlbx-builds-test-" + Guid.NewGuid().ToString("N"));
        public ECDsa Key { get; } = ECDsa.Create(ECCurve.NamedCurves.nistP384);
        public BuildOptions Options => new(Root, new Uri("https://builds.tlbx.ai"), "1124937387", "255465097");
        public Microsoft.AspNetCore.Builder.WebApplication App { get; private set; } = null!;
        public HttpClient Client { get; private set; } = null!;
        public byte[] LastManifest { get; private set; } = [];
        public static async Task<Fixture> Start(Func<ECDsa, IReleaseSignatureVerifier>? signature = null)
        {
            var f = new Fixture();
            f.App = BuildServer.Create(f.Options, s => { s.AddSingleton<IPublisherAuthenticator, TestAuth>(); s.AddSingleton<IReleaseSignatureVerifier>(signature?.Invoke(f.Key) ?? new TestSignature(f.Key)); }, "http://127.0.0.1:0");
            await f.App.StartAsync();
            var address = f.App.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            f.Client = new HttpClient { BaseAddress = new Uri(address) };
            return f;
        }
        public Task<HttpResponseMessage> Upload(ReleaseIdentity r, (string Name, byte[] Bytes) a) => Client.PutAsync($"/api/v1/releases/{r.Tag}/assets/{a.Name}", new ByteArrayContent(a.Bytes));
        public PublishRequest Publication(IEnumerable<(string Name, byte[] Bytes)> files) => new(new string('a', 40), "Release notes", files.Select(a => new ReleaseAsset(a.Name, Convert.ToHexString(SHA256.HashData(a.Bytes)).ToLowerInvariant(), a.Bytes.Length)).ToArray());
        public (string Name, byte[] Bytes)[] Package(ReleaseIdentity r, string rid, bool tamper = false, bool duplicate = false, string? extra = null)
        {
            var files = new Dictionary<string, byte[]>(StringComparer.Ordinal);
            foreach (var binary in new[] { "mt", "mthost", "mtagenthost" }) files[binary + (rid.StartsWith("win-", StringComparison.Ordinal) ? ".exe" : "")] = Encoding.UTF8.GetBytes("signed executable fixture " + binary);
            if (rid.StartsWith("win-", StringComparison.Ordinal)) files["mttmux.exe"] = Encoding.UTF8.GetBytes("signed native shim fixture");
            var checksums = files.ToDictionary(x => x.Key, x => Convert.ToHexString(SHA256.HashData(x.Value)).ToLowerInvariant(), StringComparer.Ordinal);
            if (extra is not null) files[extra] = Encoding.UTF8.GetBytes("packaging extra fixture");
            var metadata = new { signatureVersion = 2, web = r.Number, pty = r.Number, protocol = 1, minCompatiblePty = r.Number, webOnly = false, platform = rid, channel = r.IsDev ? "dev" : "stable", checksums };
            var payload = JsonSerializer.SerializeToUtf8Bytes(metadata);
            var manifest = new VersionManifest { SignatureVersion = 2, Web = r.Number, Pty = r.Number, Protocol = 1, MinCompatiblePty = r.Number, WebOnly = false, Platform = rid, Channel = metadata.channel, Checksums = checksums,
                SignedPayload = Convert.ToBase64String(payload), MetadataSignature = Convert.ToBase64String(Key.SignData(payload, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence)) };
            if (tamper) manifest.Web = "9.9.9";
            LastManifest = JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            if (duplicate) LastManifest = Encoding.UTF8.GetBytes("{\"web\":\"9.9.9\"," + Encoding.UTF8.GetString(LastManifest)[1..]);
            files["version.json"] = LastManifest;
            using var bytes = new MemoryStream();
            if (rid.StartsWith("win-", StringComparison.Ordinal))
            {
                using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
                    foreach (var file in files) { using var output = zip.CreateEntry(file.Key).Open(); output.Write(file.Value); }
            }
            else
            {
                using var compressed = new GZipStream(bytes, CompressionMode.Compress, true);
                using (var tar = new TarWriter(compressed, leaveOpen: true))
                    foreach (var file in files) { using var input = new MemoryStream(file.Value); tar.WriteEntry(new PaxTarEntry(TarEntryType.RegularFile, file.Key) { DataStream = input }); }
            }
            return [(AssetNames.Archive(rid), bytes.ToArray()), (AssetNames.Sbom(rid), Encoding.UTF8.GetBytes("{\"spdxVersion\":\"SPDX-2.3\",\"SPDXID\":\"SPDXRef-DOCUMENT\"}"))];
        }
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.StopAsync(); await App.DisposeAsync(); Key.Dispose(); if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    }
}

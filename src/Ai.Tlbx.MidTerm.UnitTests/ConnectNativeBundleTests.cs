using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Services.Connect;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

/// <summary>No native PC is created here; interactive-host UDP belongs to the owned mt acceptance process.</summary>
public sealed class ConnectNativeBundleTests
{
    [Fact]
    public void BundleExtractsCompleteHashVerifiedClosureAndRejectsCachedTampering()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tlbx-native-bundle-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var bundle = Bundle();
            var library = ConnectNativeLoader.Extract(bundle, directory, "win-x64");
            Assert.Equal("datachannel.dll", Path.GetFileName(library));
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(library)!, "native-dependencies.spdx.json")));
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(library)!, "THIRD-PARTY-NOTICES.txt")));
            Assert.Equal(library, ConnectNativeLoader.Extract(bundle, directory, "win-x64"));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(library, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            File.WriteAllBytes(library, [9, 9, 9]);
            Assert.Throws<InvalidDataException>(() => ConnectNativeLoader.Extract(bundle, directory, "win-x64"));
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Theory]
    [InlineData("../escape.dll")]
    [InlineData("helper.exe")]
    [InlineData("sub/datachannel.dll")]
    public void BundleRejectsPathsAndExecutablesBeforeExtraction(string unexpected)
    {
        var directory = Path.Combine(Path.GetTempPath(), "tlbx-native-invalid-" + Guid.NewGuid().ToString("N"));
        using var bundle = Bundle(unexpected: unexpected);
        Assert.Throws<InvalidDataException>(() => ConnectNativeLoader.Extract(bundle, directory, "win-x64"));
        Assert.False(Directory.Exists(directory));
    }

    [Fact]
    public void BundleRejectsWrongRidAndDigest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tlbx-native-digest-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var valid = Bundle();
            Assert.Throws<InvalidDataException>(() => ConnectNativeLoader.Extract(valid, directory, "win-x86"));
            using var invalid = Bundle(invalidDigest: true);
            Assert.Throws<InvalidDataException>(() => ConnectNativeLoader.Extract(invalid, directory, "win-x64"));
            Assert.DoesNotContain(Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories), path => Path.GetFileName(path) == "datachannel.dll");
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public void NativeIceProjectionAllowsBoundedAddressExpansionWithoutRelaxingInputLimits()
    {
        var expanded = Enumerable.Range(1, 63).Select(index => "turn:[2001:db8::" + index.ToString("x", System.Globalization.CultureInfo.InvariantCulture) + "]:443?transport=tcp")
            .Prepend("stun:stun.example.test:3478").ToArray();
        var server = new ConnectIceServer(expanded, "1890:peer", "p+/=@word");
        Assert.Throws<InvalidDataException>(() => ConnectWebRtcTransport.NativeServerUrls([server]));
        var urls = ConnectWebRtcTransport.NativeServerUrls([server], expanded: true);
        Assert.Equal(64, urls.Length);
        Assert.Equal("stun:stun.example.test:3478", urls[0]);
        Assert.Equal("turn:1890%3Apeer:p%2B%2F%3D%40word@[2001:db8::1]:443?transport=tcp", urls[1]);
        Assert.Throws<InvalidDataException>(() => ConnectWebRtcTransport.NativeServerUrls([
            server with { Urls = [.. expanded, "turn:192.0.2.1:443?transport=tcp"] }
        ], expanded: true));
        Assert.Throws<InvalidDataException>(() => ConnectWebRtcTransport.NativeServerUrls(
            Enumerable.Repeat(new ConnectIceServer(["stun:stun.example.test:3478"]), 9).ToArray(), expanded: true));
    }

    [Fact]
    public void NativeIceUrlsEncodeCredentialSeparatorsAndKeepTransportSelection()
    {
        var urls = ConnectWebRtcTransport.NativeServerUrls([
            new ConnectIceServer(["stun:stun.example.test:3478"]),
            new ConnectIceServer(["turn:[::1]:443?transport=tcp"], "1890:peer", "p+/=@word")
        ]);
        Assert.Equal("stun:stun.example.test:3478", urls[0]);
        Assert.Equal("turn:1890%3Apeer:p%2B%2F%3D%40word@[::1]:443?transport=tcp", urls[1]);
        Assert.Throws<InvalidDataException>(() => ConnectWebRtcTransport.NativeServerUrls([new ConnectIceServer(["turn:embedded:secret@127.0.0.1"])]));
        Assert.Throws<InvalidDataException>(() => ConnectWebRtcTransport.NativeServerUrls([new ConnectIceServer(["turn:host\0other"])]));
        Assert.Throws<InvalidDataException>(() => ConnectWebRtcTransport.NativeServerUrls([new ConnectIceServer(["https://foreign.test"])]));
    }

    private static MemoryStream Bundle(string? unexpected = null, bool invalidDigest = false)
    {
        var stream = new MemoryStream();
        var files = new Dictionary<string, byte[]>(StringComparer.Ordinal)
        {
            ["datachannel.dll"] = [1, 2, 3], ["THIRD-PARTY-NOTICES.txt"] = Encoding.UTF8.GetBytes("Fixture notices"),
            ["native-dependencies.spdx.json"] = "{}"u8.ToArray()
        };
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in files)
            {
                using var output = archive.CreateEntry(file.Key).Open(); output.Write(file.Value);
            }
            if (unexpected is not null)
            {
                using var output = archive.CreateEntry(unexpected).Open(); output.WriteByte(1);
            }
            using var manifest = archive.CreateEntry("manifest.json").Open();
            using var json = new Utf8JsonWriter(manifest);
            json.WriteStartObject(); json.WriteNumber("version", 1); json.WriteString("runtimeIdentifier", "win-x64");
            json.WriteString("entryPoint", "datachannel.dll"); json.WriteStartObject("versions");
            json.WriteString("libdatachannel", "0.24.6"); json.WriteEndObject(); json.WriteStartArray("files");
            foreach (var file in files)
            {
                json.WriteStartObject(); json.WriteString("name", file.Key);
                json.WriteString("sha256", invalidDigest && file.Key == "datachannel.dll" ? new string('0', 64) : Convert.ToHexStringLower(SHA256.HashData(file.Value)));
                json.WriteEndObject();
            }
            json.WriteEndArray(); json.WriteEndObject();
        }
        stream.Position = 0; return stream;
    }
}

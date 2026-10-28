using System.Buffers.Binary;
using System.Diagnostics;
using Ai.Tlbx.MidTerm.Services.Connect;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class ConnectProtocolTests
{
    [Fact]
    public async Task ManagedTransportOwnsNativeChildAndStopsItOnDisposal()
    {
        var configured = Environment.GetEnvironmentVariable("TLBX_CONNECT_TRANSPORT_PATH");
        var rid = System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../.."));
        var filename = OperatingSystem.IsWindows() ? "mtconnect.exe" : "mtconnect";
        var executable = Path.Combine(root, "src", "Ai.Tlbx.MidTerm.ConnectTransport", "bin", rid, filename);
        if (!File.Exists(executable)) Assert.Skip("Build the RID-specific Connect transport before its managed process smoke test.");
        var directory = Path.Combine(root, ".dev", "connect-managed-smoke");
        Directory.CreateDirectory(directory);
        Environment.SetEnvironmentVariable("TLBX_CONNECT_TRANSPORT_PATH", executable);
        try
        {
            var process = await ConnectTransportProcess.StartAsync(Guid.NewGuid().ToString("N"), directory, directory,
                new ConnectSignal { Type = "connect", IceServers = [] }, _ => Task.CompletedTask, TestContext.Current.CancellationToken);
            var id = process.ProcessId;
            await process.DisposeAsync();
            Assert.Throws<ArgumentException>(() => Process.GetProcessById(id));
        }
        finally { Environment.SetEnvironmentVariable("TLBX_CONNECT_TRANSPORT_PATH", configured); }
    }

    [Fact]
    public void FramesRoundTripPayloadLargerThanDataChannelMessage()
    {
        var payload = new byte[180000];
        Random.Shared.NextBytes(payload);
        var sender = new ConnectFrames();
        var receiver = new ConnectFrames();
        byte[]? result = null;
        foreach (var frame in sender.Encode(payload))
        {
            Assert.InRange(frame.Length, ConnectFrames.HeaderSize + 1, ConnectFrames.HeaderSize + ConnectFrames.ChunkSize);
            result = receiver.Receive(frame);
        }
        Assert.Equal(payload, result);
    }

    [Fact]
    public void FrameRejectsAnAllocationBeyondTheProtocolLimit()
    {
        var frame = new byte[14];
        frame[0] = 1;
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(9), (uint)ConnectFrames.MaximumMessageSize + 1);
        Assert.Throws<InvalidDataException>(() => new ConnectFrames().Receive(frame));
    }

    [Fact]
    public void FramesRejectDuplicateAndOutOfOrderChunks()
    {
        var frames = new ConnectFrames().Encode(new byte[100000]).ToArray();
        var receiver = new ConnectFrames();
        Assert.Null(receiver.Receive(frames[0]));
        Assert.Throws<InvalidDataException>(() => receiver.Receive(frames[0]));
        Assert.Throws<InvalidDataException>(() => new ConnectFrames().Receive(frames[1]));
    }

    [Theory]
    [InlineData("https://evil.example/api/settings")]
    [InlineData("//evil.example/api/settings")]
    [InlineData("/\\evil.example/")]
    [InlineData("/%2f%2fevil.example/")]
    [InlineData("/a/../api/connect/pair")]
    [InlineData("/%252e%252e/api/connect/pair")]
    [InlineData("/api/connect/status")]
    [InlineData("/API/CONNECT/pair?x=1")]
    [InlineData("/x%0d%0aInjected")]
    public void TunnelRejectsAuthorityEscapesTraversalAndRemoteConfiguration(string path)
    {
        Assert.Throws<InvalidDataException>(() => ConnectRequestPolicy.ResolvePath(new Uri("https://127.0.0.1:2100/"), path));
    }

    [Fact]
    public void TunnelPreservesLocalApiQueryAndNeverChangesDestination()
    {
        var uri = ConnectRequestPolicy.ResolvePath(new Uri("https://127.0.0.1:2100/"), "/api/files/list?path=C%3A%5CUsers");
        Assert.Equal("127.0.0.1", uri.Host);
        Assert.Equal(2100, uri.Port);
        Assert.Equal("/api/files/list", uri.AbsolutePath);
        Assert.Equal("?path=C%3A%5CUsers", uri.Query);
    }

    [Theory]
    [InlineData("Authorization")]
    [InlineData("COOKIE")]
    [InlineData("Host")]
    [InlineData("Origin")]
    [InlineData("Proxy-Authorization")]
    [InlineData("Sec-Fetch-Site")]
    [InlineData("X-Forwarded-Host")]
    [InlineData("X\r\nInjected")]
    public void PeerCannotProvideCredentialsOriginOrHopHeaders(string header) =>
        Assert.False(ConnectRequestPolicy.AllowHeader(header));

    [Theory]
    [InlineData("http://connect.tlbx.ai")]
    [InlineData("https://user:password@connect.tlbx.ai")]
    [InlineData("https://connect.tlbx.ai/another/path")]
    [InlineData("https://connect.tlbx.ai/?token=secret")]
    public void PairingRejectsInsecureOrAmbiguousOrigins(string origin) =>
        Assert.Throws<ArgumentException>(() => ConnectRequestPolicy.NormalizeServerUrl(origin));

    [Fact]
    public void PairingAllowsExplicitLocalDevelopmentOrigin() =>
        Assert.Equal("http://127.0.0.1:5215/", ConnectRequestPolicy.NormalizeServerUrl("http://127.0.0.1:5215").AbsoluteUri);
}

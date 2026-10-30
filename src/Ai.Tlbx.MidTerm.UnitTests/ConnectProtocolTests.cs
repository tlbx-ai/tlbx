using System.Buffers.Binary;
using System.Runtime.InteropServices;
using Ai.Tlbx.MidTerm.Services.Connect;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class ConnectProtocolTests
{
    [Fact]
    public void ManagedInteropStructuresMatchThePinnedCAbi()
    {
        Assert.Equal(IntPtr.Size == 8 ? 56 : 40, Marshal.SizeOf<ConnectNativeApi.Configuration>());
        Assert.Equal(12, Marshal.SizeOf<ConnectNativeApi.Reliability>());
        Assert.Equal(IntPtr.Size, Marshal.OffsetOf<ConnectNativeApi.Configuration>(nameof(ConnectNativeApi.Configuration.IceServersCount)).ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 16 : 8,
            Marshal.OffsetOf<ConnectNativeApi.Configuration>(nameof(ConnectNativeApi.Configuration.ProxyServer)).ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 32 : 16,
            Marshal.OffsetOf<ConnectNativeApi.Configuration>(nameof(ConnectNativeApi.Configuration.CertificateType)).ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 40 : 24,
            Marshal.OffsetOf<ConnectNativeApi.Configuration>(nameof(ConnectNativeApi.Configuration.EnableIceTcp)).ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 44 : 28,
            Marshal.OffsetOf<ConnectNativeApi.Configuration>(nameof(ConnectNativeApi.Configuration.PortRangeBegin)).ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 48 : 32,
            Marshal.OffsetOf<ConnectNativeApi.Configuration>(nameof(ConnectNativeApi.Configuration.Mtu)).ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 52 : 36,
            Marshal.OffsetOf<ConnectNativeApi.Configuration>(nameof(ConnectNativeApi.Configuration.MaximumMessageSize)).ToInt32());
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
    [InlineData("/x%0d%0aInjected")]
    [InlineData("/webpreview/0123456789abcdefabcd/")]
    [InlineData("/%77ebpreview/0123456789abcdefabcd/")]
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

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void NewPairingDefaultsToLinkOrigin(string? origin) =>
        Assert.Equal("https://lnk.tlbx.ai/", ConnectRequestPolicy.NormalizeServerUrl(origin).AbsoluteUri);

    [Theory]
    [InlineData("https://connect.tlbx.ai")]
    [InlineData("https://lnk.tlbx.ai")]
    [InlineData("https://link.example.test:8443")]
    public void PairingPreservesExplicitConfiguredOrigins(string origin) =>
        Assert.Equal(origin + "/", ConnectRequestPolicy.NormalizeServerUrl(origin).AbsoluteUri);

    [Theory]
    [InlineData("/api/settings", "/webpreview/0123456789abcdefabcd/api/settings")]
    [InlineData("/ws/mux", "/webpreview/0123456789abcdefabcd/ws/mux")]
    [InlineData("/index.html", "/webpreview/0123456789abcdefabcd/index.html")]
    [InlineData("/webpreview/0123456789abcdefabcd/api/data", "/webpreview/0123456789abcdefabcd/api/data")]
    public void PreviewRootRequestsAlwaysRemainOnScopedNativePreviewPort(string path, string expected)
    {
        var destination = ConnectRequestPolicy.ResolvePreviewPath(new Uri("https://127.0.0.1:2101/"), path, "0123456789abcdefabcd");
        Assert.Equal(2101, destination.Port);
        Assert.Equal(expected, destination.AbsolutePath);
    }

    [Theory]
    [InlineData("/webpreview/11111111111111111111/api/data")]
    [InlineData("//evil.example/webpreview/0123456789abcdefabcd/")]
    [InlineData("/webpreview/0123456789abcdefabcd/../../api/settings")]
    [InlineData("/webpreview/%31%31%31%31%31%31%31%31%31%31%31%31%31%31%31%31%31%31%31%31/")]
    public void PreviewCannotEscapeItsBoundRoute(string path) =>
        Assert.Throws<InvalidDataException>(() => ConnectRequestPolicy.ResolvePreviewPath(
            new Uri("https://127.0.0.1:2101/"), path, "0123456789abcdefabcd"));

    [Fact]
    public void NativePreviewCommandSocketRequiresRegistryTokenForTheBoundRoute()
    {
        var registry = new BrowserPreviewRegistry();
        var client = registry.Create("session", "default", "0123456789abcdefabcd");
        var correct = new Uri($"https://127.0.0.1:2101/ws/browser?previewId={client.PreviewId}&token={client.PreviewToken}");
        Assert.True(ConnectRequestPolicy.AuthorizePreviewSocket(registry, correct, "0123456789abcdefabcd"));
        Assert.False(ConnectRequestPolicy.AuthorizePreviewSocket(registry, correct, "11111111111111111111"));
        Assert.False(ConnectRequestPolicy.AuthorizePreviewSocket(registry,
            new Uri(correct.AbsoluteUri + "&routeKey=11111111111111111111"), "0123456789abcdefabcd"));
        Assert.False(ConnectRequestPolicy.AuthorizePreviewSocket(registry,
            new Uri("https://127.0.0.1:2101/ws/browser?previewId=wrong&token=wrong"), "0123456789abcdefabcd"));
    }

    [Fact]
    public void PreviewCookieJarsCannotShareCredentialsWithOtherPreviewRoutes()
    {
        using var first = new ConnectPreviewClient();
        using var second = new ConnectPreviewClient();
        var origin = new Uri("https://127.0.0.1:2101/");
        first.Cookies.SetCookies(origin, "mt-preview-example=preview-id:preview-token; Secure; HttpOnly; Path=/");
        Assert.Contains("mt-preview-example=", first.Cookies.GetCookieHeader(origin), StringComparison.Ordinal);
        Assert.Empty(second.Cookies.GetCookieHeader(origin));
        Assert.DoesNotContain(AuthService.SessionCookieName + "=", first.Cookies.GetCookieHeader(origin), StringComparison.Ordinal);
    }

    [Fact]
    public void PreviewPreservesUpstreamBearerAuthWithoutAcceptingBrowserCookiesOrInternalProxyHeaders()
    {
        Assert.False(ConnectRequestPolicy.AllowHeader("Authorization"));
        Assert.True(ConnectRequestPolicy.AllowPreviewHeader("Authorization"));
        Assert.False(ConnectRequestPolicy.AllowPreviewHeader("Cookie"));
        Assert.False(ConnectRequestPolicy.AllowPreviewHeader("Host"));
        Assert.False(ConnectRequestPolicy.AllowPreviewHeader("X-MidTerm-Internal-Proxy"));
    }
}

using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Connect;
using Ai.Tlbx.MidTerm.Services.Hosting;
using Ai.Tlbx.MidTerm.Services.Security;
using Ai.Tlbx.MidTerm.Settings;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class ConnectKeySecurityTests
{
    private static string Sdp(char digit) => "v=0\r\na=fingerprint:sha-256 " + string.Join(':', Enumerable.Repeat(new string(digit, 2), 32)) + "\r\n";

    internal static ConnectPeerAuthentication CreateAuthenticatedPeer()
    {
        var generated = ConnectKeyProtocol.Generate("https://connect.tlbx.ai", "fixture");
        var peer = new ConnectPeerAuthentication(generated.Configuration, "fixture-connection");
        peer.SetOffer(Sdp('a')); peer.SetAnswer(Sdp('b'));
        var challenge = peer.CreateChallenge();
        using var access = ConnectKeyProtocol.PrivateKey(generated.Downloads.AccessKey.PrivateKey);
        peer.AcceptProof(new ConnectEnvelope { Type = "authProof", Data = ConnectKeyProtocol.Encode(ConnectKeyProtocol.Sign(access, ConnectKeyProtocol.Decode(challenge.Data!))) });
        return peer;
    }

    [Fact]
    public void GeneratedRegistrationHasStandardP256SignatureAndSeparateAccessPrivateKey()
    {
        var (configuration, downloads) = ConnectKeyProtocol.Generate("https://connect.tlbx.ai", "fixture");
        var payload = ConnectKeyProtocol.Decode(downloads.Registration.Payload);
        var registration = JsonSerializer.Deserialize(payload, ConnectJsonContext.Default.ConnectRegistrationPayload)!;
        using var host = ConnectKeyProtocol.PublicKey(registration.HostPublicKey);
        using var access = ConnectKeyProtocol.PrivateKey(downloads.AccessKey.PrivateKey);
        Assert.True(ConnectKeyProtocol.Verify(host, payload, downloads.Registration.Signature));
        Assert.Equal(64, ConnectKeyProtocol.Decode(downloads.Registration.Signature).Length);
        Assert.Equal(registration.AccessPublicKey, ConnectKeyProtocol.Encode(access.ExportSubjectPublicKeyInfo()));
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(host.ExportSubjectPublicKeyInfo()))[..32], configuration.DeviceId);
        Assert.NotEqual(configuration.HostPrivateKey, downloads.AccessKey.PrivateKey, StringComparer.Ordinal);
        Assert.DoesNotContain(configuration.HostPrivateKey, JsonSerializer.Serialize(downloads, ConnectJsonContext.Default.ConnectGenerateResponse), StringComparison.Ordinal);
        var replacement = ConnectKeyProtocol.Generate("https://connect.tlbx.ai", "fixture");
        Assert.NotEqual(configuration.DeviceId, replacement.Configuration.DeviceId, StringComparer.Ordinal);
    }

    [Fact]
    public void PeerChallengePinsBothFingerprintsAndAcceptsOnlyOneCorrectProof()
    {
        var generated = ConnectKeyProtocol.Generate("https://connect.tlbx.ai", "fixture");
        var peer = new ConnectPeerAuthentication(generated.Configuration, "connection-one");
        peer.SetOffer(Sdp('a')); peer.SetAnswer(Sdp('b'));
        var challenge = peer.CreateChallenge();
        var bytes = ConnectKeyProtocol.Decode(challenge.Data!);
        var payload = JsonSerializer.Deserialize(bytes, ConnectJsonContext.Default.ConnectPeerPayload)!;
        Assert.Equal(new string('a', 64), payload.BrowserFingerprint);
        Assert.Equal(new string('b', 64), payload.HostFingerprint);
        Assert.Equal("connection-one", payload.ConnectionId);
        using var host = ConnectKeyProtocol.PublicKey(generated.Configuration.HostPublicKey);
        Assert.True(ConnectKeyProtocol.Verify(host, bytes, challenge.Signature!));
        using var access = ConnectKeyProtocol.PrivateKey(generated.Downloads.AccessKey.PrivateKey);
        var proof = new ConnectEnvelope { Type = "authProof", Data = ConnectKeyProtocol.Encode(ConnectKeyProtocol.Sign(access, bytes)) };
        peer.AcceptProof(proof);
        Assert.True(peer.Authenticated);
        Assert.Throws<InvalidDataException>(() => peer.AcceptProof(proof));
        var other = new ConnectPeerAuthentication(generated.Configuration, "connection-two");
        other.SetOffer(Sdp('a')); other.SetAnswer(Sdp('c')); other.CreateChallenge();
        Assert.Throws<InvalidDataException>(() => other.AcceptProof(proof));
        Assert.False(other.Authenticated);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a=fingerprint:sha-1 AA:BB\r\n")]
    [InlineData("a=fingerprint:sha-256 AA:BB\r\n")]
    public void MissingOrUnsupportedDtlsFingerprintIsRejected(string sdp) => Assert.Throws<InvalidDataException>(() => ConnectKeyProtocol.Fingerprint(sdp));

    [Fact]
    public void ConflictingFingerprintsAndRepeatedOfferAreRejected()
    {
        Assert.Throws<InvalidDataException>(() => ConnectKeyProtocol.Fingerprint(Sdp('a') + Sdp('b')));
        var generated = ConnectKeyProtocol.Generate("https://connect.tlbx.ai", "fixture");
        var peer = new ConnectPeerAuthentication(generated.Configuration, "connection");
        peer.SetOffer(Sdp('a'));
        Assert.Throws<InvalidDataException>(() => peer.SetOffer(Sdp('b')));
    }

    [Fact]
    public void ExpiredProofAndProofFromAnotherAccessKeyAreRejected()
    {
        var generated = ConnectKeyProtocol.Generate("https://connect.tlbx.ai", "fixture");
        var clock = new FakeTimeProvider();
        var peer = new ConnectPeerAuthentication(generated.Configuration, "connection", clock);
        peer.SetOffer(Sdp('a')); peer.SetAnswer(Sdp('b'));
        var challenge = peer.CreateChallenge();
        using var access = ConnectKeyProtocol.PrivateKey(generated.Downloads.AccessKey.PrivateKey);
        var proof = new ConnectEnvelope { Type = "authProof", Data = ConnectKeyProtocol.Encode(ConnectKeyProtocol.Sign(access, ConnectKeyProtocol.Decode(challenge.Data!))) };
        clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Throws<InvalidDataException>(() => peer.AcceptProof(proof));
        Assert.False(peer.Authenticated);
        var other = new ConnectPeerAuthentication(generated.Configuration, "other"); other.SetOffer(Sdp('a')); other.SetAnswer(Sdp('b'));
        var otherChallenge = other.CreateChallenge();
        using var wrongKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.Throws<InvalidDataException>(() => other.AcceptProof(new ConnectEnvelope { Type = "authProof", Data = ConnectKeyProtocol.Encode(ConnectKeyProtocol.Sign(wrongKey, ConnectKeyProtocol.Decode(otherChallenge.Data!))) }));
        Assert.False(other.Authenticated);
    }

    [Theory]
    [InlineData("browser", "https://connect.tlbx.ai", 60)]
    [InlineData("host", "https://other.test", 60)]
    [InlineData("host", "https://connect.tlbx.ai", -1)]
    [InlineData("host", "https://connect.tlbx.ai", 600)]
    public void HostNeverSignsAnUnboundOrExpiredPortalChallenge(string role, string origin, int expiry)
    {
        var generated = ConnectKeyProtocol.Generate("https://connect.tlbx.ai", "fixture");
        var payload = new ConnectAuthenticationPayload("tlbx-connect-auth", 1, role, origin, generated.Configuration.DeviceId,
            ConnectKeyProtocol.Encode(RandomNumberGenerator.GetBytes(32)), DateTimeOffset.UtcNow.AddSeconds(expiry).ToUnixTimeSeconds());
        var encoded = ConnectKeyProtocol.Encode(JsonSerializer.SerializeToUtf8Bytes(payload, ConnectJsonContext.Default.ConnectAuthenticationPayload));
        Assert.Throws<InvalidDataException>(() => ConnectKeyProtocol.ValidateHostChallenge(generated.Configuration, encoded));
    }

    [Theory]
    [InlineData("/api/connect/status")]
    [InlineData("/API/CONNECT/generate?x=1")]
    [InlineData("/%61pi/connect/unlock")]
    [InlineData("/%2561pi/connect/generate")]
    [InlineData("/connect.html")]
    [InlineData("/%63onnect.html")]
    [InlineData("/connect-setup.js")]
    public void BothTunnelSurfacesRejectSetupBeforeForwarding(string path)
    {
        Assert.Throws<ConnectLocalAccessException>(() => ConnectRequestPolicy.ResolvePath(new Uri("https://127.0.0.1:2100"), path));
        Assert.Throws<ConnectLocalAccessException>(() => ConnectRequestPolicy.ResolvePreviewPath(new Uri("https://127.0.0.1:2101"), path, "0123456789abcdefabcd"));
    }

    private static DefaultHttpContext LocalContext()
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = IPAddress.Loopback; context.Connection.LocalIpAddress = IPAddress.Loopback; context.Connection.LocalPort = 2100;
        context.Request.Scheme = "https"; context.Request.Host = new HostString("localhost", 2100); context.Request.Method = "POST";
        context.Request.ContentType = "application/json"; context.Request.Headers.Origin = "https://localhost:2100"; context.Request.Headers["Sec-Fetch-Site"] = "same-origin";
        return context;
    }

    [Fact]
    public void SetupRequiresPhysicalLoopbackAndExactBrowserOrigin()
    {
        var context = LocalContext(); Assert.True(ConnectLocalAccessPolicy.Allows(context, false));
        Assert.False(ConnectLocalAccessPolicy.Allows(context, true));
        context.Connection.RemoteIpAddress = IPAddress.Parse("100.91.240.65"); Assert.False(ConnectLocalAccessPolicy.Allows(context, false));
        context.Connection.RemoteIpAddress = IPAddress.Loopback; context.Connection.LocalIpAddress = IPAddress.Parse("192.168.1.2"); Assert.False(ConnectLocalAccessPolicy.Allows(context, false));
        context.Connection.LocalIpAddress = IPAddress.Loopback; context.Request.Host = new HostString("evil.test", 2100); Assert.False(ConnectLocalAccessPolicy.Allows(context, false));
        context.Request.Host = new HostString("localhost", 2100); context.Request.Headers.Origin = "https://evil.test"; Assert.False(ConnectLocalAccessPolicy.Allows(context, false));
        context.Request.Headers.Remove("Origin"); Assert.False(ConnectLocalAccessPolicy.Allows(context, false));
    }

    [Theory]
    [InlineData("none")]
    [InlineData("same-origin")]
    [InlineData("same-site")]
    [InlineData("cross-site")]
    public void SetupDocumentCanBeOpenedDirectlyOrFromAnExternalLink(string site)
    {
        var context = LocalContext();
        context.Request.Method = "GET"; context.Request.Path = "/connect.html";
        context.Request.Headers.Remove("Origin");
        context.Request.Headers["Sec-Fetch-Site"] = site;
        context.Request.Headers["Sec-Fetch-Mode"] = "navigate";
        context.Request.Headers["Sec-Fetch-Dest"] = "document";
        Assert.True(ConnectLocalAccessPolicy.Allows(context, false));
        Assert.False(ConnectLocalAccessPolicy.Allows(context, true));
        context.Connection.RemoteIpAddress = IPAddress.Parse("192.168.1.3");
        Assert.False(ConnectLocalAccessPolicy.Allows(context, false));
    }

    [Theory]
    [InlineData("GET", "/connect.html", "navigate", "iframe")]
    [InlineData("GET", "/connect.html", "cors", "empty")]
    [InlineData("GET", "/api/connect/status", "navigate", "document")]
    [InlineData("GET", "/connect-setup.js", "navigate", "document")]
    [InlineData("GET", "/connect-setup.html", "navigate", "document")]
    [InlineData("POST", "/api/connect/unlock", "navigate", "document")]
    [InlineData("POST", "/api/connect/generate", "navigate", "document")]
    [InlineData("POST", "/connect.html", "navigate", "document")]
    public void ExternalNavigationExceptionCannotReadApisEmbedOrMutate(string method, string path, string mode, string destination)
    {
        var context = LocalContext();
        context.Request.Method = method; context.Request.Path = path;
        context.Request.Headers.Remove("Origin");
        context.Request.Headers["Sec-Fetch-Site"] = "cross-site";
        context.Request.Headers["Sec-Fetch-Mode"] = mode;
        context.Request.Headers["Sec-Fetch-Dest"] = destination;
        Assert.False(ConnectLocalAccessPolicy.Allows(context, false));
    }

    [Theory]
    [InlineData("Forwarded")]
    [InlineData("X-Forwarded-For")]
    [InlineData("X-Forwarded-Host")]
    [InlineData("X-Forwarded-Proto")]
    [InlineData("X-MidTerm-Internal-Proxy")]
    [InlineData("Via")]
    [InlineData("Authorization")]
    public void LocalProxyAndSpoofedForwardingHintsAreRejected(string header)
    {
        var context = LocalContext(); context.Request.Headers[header] = "1";
        Assert.False(ConnectLocalAccessPolicy.Allows(context, false));
    }

    [Fact]
    public void LocalUnlockRequiresFreshPasswordAndIsSessionBoundAndSingleUse()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Settings fixture uses the Windows secret-storage build.");
        var directory = Path.Combine(Path.GetTempPath(), "tlbx-connect-keys-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var settings = new SettingsService(directory); var auth = new AuthService(settings, new ApiKeyService(settings));
            var state = settings.Load(); state.PasswordHash = auth.HashPassword("fixture-long-password-only"); settings.Save(state);
            var context = LocalContext(); context.Request.Headers.Cookie = AuthService.SessionCookieName + "=" + auth.CreateSessionToken();
            var clock = new FakeTimeProvider();
            var gate = new ConnectProvisioningGate(settings, auth, clock);
            Assert.Equal(401, gate.Unlock(LocalContext(), "fixture-long-password-only", out _));
            Assert.Equal(401, gate.Unlock(context, null, out _)); Assert.Equal(401, gate.Unlock(context, "wrong", out _));
            Assert.Equal(200, gate.Unlock(context, "fixture-long-password-only", out var unlocked));
            var other = LocalContext(); other.Request.Headers.Cookie = AuthService.SessionCookieName + "=" + auth.CreateSessionToken();
            Assert.False(gate.Consume(other, unlocked!.UnlockToken)); Assert.False(gate.Consume(context, "wrong"));
            Assert.True(gate.Consume(context, unlocked.UnlockToken)); Assert.False(gate.Consume(context, unlocked.UnlockToken));
            Assert.Equal(200, gate.Unlock(context, "fixture-long-password-only", out var expiring));
            clock.Advance(TimeSpan.FromSeconds(121)); Assert.False(gate.Consume(context, expiring!.UnlockToken));
            for (var attempt = 0; attempt < 5; attempt++) Assert.Equal(401, gate.Unlock(context, "wrong", out _));
            Assert.Equal(429, gate.Unlock(context, "fixture-long-password-only", out _));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "IDISP014:Use a single instance of HttpClient", Justification = "The fixture owns one client with an isolated counting handler.")]
    public async Task TunnelRejectsHttpBeforeProofAndSmallAuthLimitBeforeAllocationThenDeniesSetupWith403()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Settings fixture uses the Windows secret-storage build.");
        var directory = Path.Combine(Path.GetTempPath(), "tlbx-connect-tunnel-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory);
        try
        {
            var settings = new SettingsService(directory); var auth = new AuthService(settings, new ApiKeyService(settings));
            var generated = ConnectKeyProtocol.Generate("https://connect.tlbx.ai", "fixture");
            var peer = new ConnectPeerAuthentication(generated.Configuration, "connection"); peer.SetOffer(Sdp('a')); peer.SetAnswer(Sdp('b'));
            var envelopes = new List<ConnectEnvelope>(); var receive = new ConnectFrames();
            using var handler = new CountingHandler();
            using var http = new HttpClient(handler, disposeHandler: false);
            await using var tunnel = new ConnectTunnel(new ServerBindingInfo(2100, "127.0.0.1"), new BrowserPreviewOriginService(2100, 2101, true),
                new BrowserPreviewRegistry(), auth, http, (signal, _) =>
                {
                    var bytes = receive.Receive(signal.Span);
                    if (bytes is not null) envelopes.Add(JsonSerializer.Deserialize(bytes, ConnectJsonContext.Default.ConnectEnvelope)!);
                    return Task.CompletedTask;
                }, peer, TestContext.Current.CancellationToken);
            var frames = new ConnectFrames();
            var early = frames.Encode(JsonSerializer.SerializeToUtf8Bytes(new ConnectEnvelope { Type = "http", Id = "early", Path = "/api/version" }, ConnectJsonContext.Default.ConnectEnvelope)).Single();
            await Assert.ThrowsAsync<InvalidDataException>(() => tunnel.ReceiveFrameAsync(early));
            var oversized = new byte[ConnectFrames.HeaderSize + 1]; oversized[0] = 1; BinaryPrimitives.WriteUInt32LittleEndian(oversized.AsSpan(9), 32 * 1024 * 1024);
            await Assert.ThrowsAsync<InvalidDataException>(() => tunnel.ReceiveFrameAsync(oversized));
            Assert.Equal(0, handler.RequestCount);
            await tunnel.BeginAuthenticationAsync();
            var challenge = Assert.Single(envelopes); Assert.Equal("authChallenge", challenge.Type); envelopes.Clear();
            using var access = ConnectKeyProtocol.PrivateKey(generated.Downloads.AccessKey.PrivateKey);
            var proof = new ConnectEnvelope { Type = "authProof", Data = ConnectKeyProtocol.Encode(ConnectKeyProtocol.Sign(access, ConnectKeyProtocol.Decode(challenge.Data!))) };
            foreach (var frame in frames.Encode(JsonSerializer.SerializeToUtf8Bytes(proof, ConnectJsonContext.Default.ConnectEnvelope))) await tunnel.ReceiveFrameAsync(frame);
            Assert.Equal("authOk", Assert.Single(envelopes).Type); envelopes.Clear();
            var denied = new ConnectEnvelope { Type = "http", Id = "denied", Method = "POST", Path = "/api/connect/generate", Body = "bm90LWpzb24=" };
            foreach (var frame in frames.Encode(JsonSerializer.SerializeToUtf8Bytes(denied, ConnectJsonContext.Default.ConnectEnvelope))) await tunnel.ReceiveFrameAsync(frame);
            await Task.Yield();
            Assert.Equal(403, Assert.Single(envelopes).Status); Assert.Equal(0, handler.RequestCount);
            envelopes.Clear();
            var allowed = new ConnectEnvelope { Type = "http", Id = "allowed", Method = "GET", Path = "/api/version" };
            foreach (var frame in frames.Encode(JsonSerializer.SerializeToUtf8Bytes(allowed, ConnectJsonContext.Default.ConnectEnvelope))) await tunnel.ReceiveFrameAsync(frame);
            await Task.Yield();
            Assert.Equal(200, Assert.Single(envelopes).Status); Assert.Equal(1, handler.RequestCount);
            Assert.True(auth.ValidateSessionToken(handler.SessionToken));
        }
        finally { Directory.Delete(directory, true); }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }
        public string? SessionToken { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            SessionToken = request.Headers.GetValues("Cookie").Single()[(AuthService.SessionCookieName.Length + 1)..];
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("ok") });
        }
    }
}

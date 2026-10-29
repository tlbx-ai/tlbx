using System.Net;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Ai.Tlbx.MidTerm.Services.Connect;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

// GetStream returns the stream owned by each TcpClient. Clients are disposed by
// their using scope or by the bounded-client test's finally block.
#pragma warning disable IDISP004

public sealed class ConnectTurnProxyTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task UdpOnlyConfigurationDoesNotOpenProxy()
    {
        var server = new ConnectIceServer(["stun:example.test:3478", "turn:example.test:3478?transport=udp"], "user", "secret");
        await using var proxy = await ConnectTurnProxy.CreateAsync([server], Ct);
        Assert.Null(proxy.ProxyUrl);
        Assert.Equal(server.Urls, Assert.Single(proxy.NativeServers).Urls);
    }

    [Fact]
    public async Task UnresolvableTcpAndTlsTargetsPreserveOtherIceOptionsAndCredentials()
    {
        using var listener = Listen();
        var working = $"turn:127.0.0.1:{Port(listener)}?transport=tcp";
        var stun = "stun:127.0.0.1:3478";
        var udp = "turn:127.0.0.1:3478?transport=udp";
        await using var proxy = await ConnectTurnProxy.CreateAsync([new([
            "turn:tlbx-unresolvable.invalid:3478?transport=tcp",
            "turns:tlbx-unresolvable.invalid:5349", stun, udp, working], "user", "credential")], Ct);
        var server = Assert.Single(proxy.NativeServers);
        Assert.Equal(new[] { stun, udp, working }, server.Urls);
        Assert.Equal("user", server.Username);
        Assert.Equal("credential", server.Credential);
        Assert.NotNull(proxy.ProxyUrl);
        var echo = EchoAsync(listener, tls: false);
        using var client = await OpenAsync(proxy);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(Request(proxy, $"127.0.0.1:{Port(listener)}")), Ct);
        Assert.StartsWith("HTTP/1.1 200 ", await HeaderAsync(client.GetStream()), StringComparison.Ordinal);
        await client.GetStream().WriteAsync(new byte[] { 0, 255, 13, 10 }, Ct);
        var bytes = new byte[4];
        await client.GetStream().ReadExactlyAsync(bytes, Ct);
        Assert.Equal(new byte[] { 0, 255, 13, 10 }, bytes);
        await echo.WaitAsync(TimeSpan.FromSeconds(3), Ct);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EmptyUnresolvableGroupsAreOmittedAndDoNotRequireProxy(bool preserveStun)
    {
        var servers = new List<ConnectIceServer> { new(["turns:tlbx-unresolvable.invalid:5349"]) };
        if (preserveStun) servers.Add(new(["stun:127.0.0.1:3478"]));
        await using var proxy = await ConnectTurnProxy.CreateAsync(servers.ToArray(), Ct);
        Assert.Null(proxy.ProxyUrl);
        if (preserveStun) Assert.Equal("stun:127.0.0.1:3478", Assert.Single(Assert.Single(proxy.NativeServers).Urls));
        else Assert.Empty(proxy.NativeServers);
    }

    [Fact]
    public async Task FailedDnsDoesNotHideMalformedLaterTurnConfiguration()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConnectTurnProxy.CreateAsync([new([
            "turns:tlbx-unresolvable.invalid:5349", "turns:127.0.0.1:5349?transport=udp"])], Ct));
    }

    [Fact]
    public async Task CallerCancellationIsNotTreatedAsOptionalDnsFailure()
    {
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConnectTurnProxy.CreateAsync(
            [new(["turns:tlbx-unresolvable.invalid:5349", "stun:127.0.0.1:3478"])], canceled.Token));
    }

    [Fact]
    public async Task SplitConnectAndPipelinedBinaryBytesReachOnlyAuthorizedPlainTcpTarget()
    {
        using var listener = Listen();
        var port = Port(listener);
        await using var proxy = await ConnectTurnProxy.CreateAsync(
            [new([$"turn:127.0.0.1:{port}?transport=tcp"], "turn-user", "turn-password")], Ct);
        var server = EchoAsync(listener, tls: false);
        using var client = await OpenAsync(proxy);
        var bytes = Encoding.ASCII.GetBytes(Request(proxy, $"127.0.0.1:{port}"));
        await client.GetStream().WriteAsync(bytes.AsMemory(0, 11), Ct);
        await client.GetStream().WriteAsync(bytes.AsMemory(11, bytes.Length - 13), Ct);
        await client.GetStream().WriteAsync(bytes.AsMemory(bytes.Length - 2), Ct);
        await client.GetStream().WriteAsync(new byte[] { 0, 255, 13, 10 }, Ct);
        Assert.StartsWith("HTTP/1.1 200 ", await HeaderAsync(client.GetStream()), StringComparison.Ordinal);
        var response = new byte[4];
        await client.GetStream().ReadExactlyAsync(response, Ct);
        Assert.Equal(new byte[] { 0, 255, 13, 10 }, response);
        await server.WaitAsync(TimeSpan.FromSeconds(3), Ct);
        Assert.Equal("turn-user", proxy.NativeServers[0].Username);
        Assert.Equal("turn-password", proxy.NativeServers[0].Credential);
    }

    [Theory]
    [InlineData("missing-auth", 407)]
    [InlineData("wrong-auth", 407)]
    [InlineData("duplicate-auth", 407)]
    [InlineData("unknown-port", 403)]
    [InlineData("hostname-authority", 403)]
    [InlineData("method", 400)]
    [InlineData("body", 400)]
    [InlineData("transfer-encoding", 400)]
    [InlineData("folded-header", 400)]
    public async Task RejectsUnauthorizedOrMalformedConnectBeforeUpstreamAccept(string mutation, int status)
    {
        using var listener = Listen();
        var port = Port(listener);
        await using var proxy = await ConnectTurnProxy.CreateAsync([new([$"turn:127.0.0.1:{port}?transport=tcp"])], Ct);
        var request = Request(proxy, $"127.0.0.1:{port}");
        request = mutation switch
        {
            "missing-auth" => $"CONNECT 127.0.0.1:{port} HTTP/1.1\r\n\r\n",
            "wrong-auth" => request.Replace(Authorization(proxy), "Basic invalid", StringComparison.Ordinal),
            "duplicate-auth" => request.Replace("\r\n\r\n", $"\r\nProxy-Authorization: {Authorization(proxy)}\r\n\r\n", StringComparison.Ordinal),
            "unknown-port" => Request(proxy, "127.0.0.1:" + (port == "1" ? "2" : "1")),
            "hostname-authority" => Request(proxy, $"localhost:{port}"),
            "method" => request.Replace("CONNECT", "GET", StringComparison.Ordinal),
            "body" => request.Replace("\r\n\r\n", "\r\nContent-Length: 1\r\n\r\n", StringComparison.Ordinal),
            "transfer-encoding" => request.Replace("\r\n\r\n", "\r\nTransfer-Encoding: chunked\r\n\r\n", StringComparison.Ordinal),
            "folded-header" => request.Replace("\r\n\r\n", "\r\n continued\r\n\r\n", StringComparison.Ordinal),
            _ => throw new InvalidOperationException()
        };
        using var client = await OpenAsync(proxy);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(request), Ct);
        var response = await HeaderAsync(client.GetStream());
        Assert.StartsWith("HTTP/1.1 " + status.ToString(CultureInfo.InvariantCulture) + " ", response, StringComparison.Ordinal);
        if (status == 407) Assert.Contains("Proxy-Authenticate: Basic", response, StringComparison.Ordinal);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task OversizedHeaderIsRejectedWithoutConnectingToTarget()
    {
        using var listener = Listen();
        await using var proxy = await ConnectTurnProxy.CreateAsync([new([$"turn:127.0.0.1:{Port(listener)}?transport=tcp"])], Ct);
        using var client = await OpenAsync(proxy);
        const string prefix = "CONNECT 127.0.0.1:1 HTTP/1.1\r\nX:";
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(prefix
            + new string('a', ConnectTurnProxy.MaximumHeaderBytes - prefix.Length)), Ct);
        Assert.StartsWith("HTTP/1.1 431 ", await HeaderAsync(client.GetStream()), StringComparison.Ordinal);
        Assert.False(listener.Pending());
    }

    [Fact]
    public async Task MixedTlsAndPlainTargetsOnSameAddressAreRejected()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ConnectTurnProxy.CreateAsync(
            [new(["turn:127.0.0.1:5349?transport=tcp", "turns:127.0.0.1:5349"])], Ct));
    }

    [Fact]
    public async Task FailedTlsAttemptDoesNotDisableOtherPlainTcpAttempts()
    {
        using var tlsListener = Listen();
        using var plainListener = Listen();
        await using var proxy = await ConnectTurnProxy.CreateAsync([new([
            $"turns:127.0.0.1:{Port(tlsListener)}",
            $"turn:127.0.0.1:{Port(plainListener)}?transport=tcp"])], Ct);
        // This deliberately plain server cannot complete the required TLS handshake.
        var badServer = EchoAsync(tlsListener, tls: false);
        using (var rejected = await OpenAsync(proxy))
        {
            await rejected.GetStream().WriteAsync(Encoding.ASCII.GetBytes(Request(proxy, $"127.0.0.1:{Port(tlsListener)}")), Ct);
            Assert.StartsWith("HTTP/1.1 502 ", await HeaderAsync(rejected.GetStream()), StringComparison.Ordinal);
        }
        await badServer.WaitAsync(TimeSpan.FromSeconds(3), Ct);
        var server = EchoAsync(plainListener, tls: false);
        using var client = await OpenAsync(proxy);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(Request(proxy, $"127.0.0.1:{Port(plainListener)}")), Ct);
        Assert.StartsWith("HTTP/1.1 200 ", await HeaderAsync(client.GetStream()), StringComparison.Ordinal);
        await client.GetStream().WriteAsync(new byte[] { 0, 255, 13, 10 }, Ct);
        var response = new byte[4];
        await client.GetStream().ReadExactlyAsync(response, Ct);
        Assert.Equal(new byte[] { 0, 255, 13, 10 }, response);
        await server.WaitAsync(TimeSpan.FromSeconds(3), Ct);
    }

    public static bool SupportsIpv6 => Socket.OSSupportsIPv6;

    [Fact(Skip = "IPv6 is unavailable on this platform.", SkipUnless = nameof(SupportsIpv6))]
    public async Task BracketedIpv6TargetIsMappedAndTunneled()
    {
        using var listener = new TcpListener(IPAddress.IPv6Loopback, 0);
        listener.Start();
        var url = $"turn:[::1]:{Port(listener)}?transport=tcp";
        await using var proxy = await ConnectTurnProxy.CreateAsync([new([url])], Ct);
        Assert.Equal(url, Assert.Single(proxy.NativeServers[0].Urls));
        var server = EchoAsync(listener, tls: false);
        using var client = await OpenAsync(proxy);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(Request(proxy, $"[::1]:{Port(listener)}")), Ct);
        Assert.StartsWith("HTTP/1.1 200 ", await HeaderAsync(client.GetStream()), StringComparison.Ordinal);
        await client.GetStream().WriteAsync(new byte[] { 0, 255, 13, 10 }, Ct);
        var response = new byte[4];
        await client.GetStream().ReadExactlyAsync(response, Ct);
        Assert.Equal(new byte[] { 0, 255, 13, 10 }, response);
        await server.WaitAsync(TimeSpan.FromSeconds(3), Ct);
    }

    [Fact]
    public async Task IncompleteHeaderHasFiniteHandshakeDeadline()
    {
        using var listener = Listen();
        await using var proxy = await ConnectTurnProxy.CreateAsync([new([$"turn:127.0.0.1:{Port(listener)}?transport=tcp"])], Ct);
        using var client = await OpenAsync(proxy);
        await client.GetStream().WriteAsync("CONNECT "u8.ToArray(), Ct);
        var response = new byte[512];
        var count = await client.GetStream().ReadAsync(response, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(12), Ct);
        Assert.StartsWith("HTTP/1.1 502 ", Encoding.ASCII.GetString(response, 0, count), StringComparison.Ordinal);
        Assert.False(listener.Pending());
    }

    [Theory]
    [InlineData("localhost", true, 200)]
    [InlineData("localhost", false, 502)]
    [InlineData("different.invalid", true, 502)]
    public async Task TurnsUsesActualTlsAndChecksBothTrustAndOriginalDnsName(string certificateName, bool trustRoot, int expected)
    {
        using var listener = Listen();
        var (root, certificate) = Certificates(certificateName);
        using var rootOwner = root;
        using var certificateOwner = certificate;
        var policy = trustRoot ? new X509ChainPolicy { TrustMode = X509ChainTrustMode.CustomRootTrust,
            RevocationMode = X509RevocationMode.NoCheck } : null;
        policy?.CustomTrustStore.Add(root);
        await using var proxy = await ConnectTurnProxy.CreateAsync(
            [new([$"turns:localhost:{Port(listener)}"], "u", "p")], Ct, policy);
        Assert.All(proxy.NativeServers[0].Urls, url =>
        {
            Assert.DoesNotContain("localhost", url, StringComparison.Ordinal);
            Assert.StartsWith("turn:", url, StringComparison.Ordinal);
            Assert.EndsWith("?transport=tcp", url, StringComparison.Ordinal);
        });
        var server = EchoAsync(listener, tls: true, certificate);
        using var client = await OpenAsync(proxy);
        await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(Request(proxy, $"127.0.0.1:{Port(listener)}")), Ct);
        Assert.StartsWith("HTTP/1.1 " + expected.ToString(CultureInfo.InvariantCulture) + " ",
            await HeaderAsync(client.GetStream()), StringComparison.Ordinal);
        if (expected == 200)
        {
            await client.GetStream().WriteAsync(new byte[] { 0, 255, 13, 10 }, Ct);
            var response = new byte[4];
            await client.GetStream().ReadExactlyAsync(response, Ct);
            Assert.Equal(new byte[] { 0, 255, 13, 10 }, response);
        }
        await server.WaitAsync(TimeSpan.FromSeconds(3), Ct);
    }

    [Fact]
    public async Task DisposalCancelsIncompleteRequestsAndClosesListeningPort()
    {
        await using var proxy = await ConnectTurnProxy.CreateAsync([new(["turn:127.0.0.1:3478?transport=tcp"])], Ct);
        var url = new Uri(proxy.ProxyUrl!);
        using var client = await OpenAsync(proxy);
        await client.GetStream().WriteAsync("CONNECT "u8.ToArray(), Ct);
        await proxy.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(3), Ct);
        var bytes = new byte[1];
        try { Assert.Equal(0, await client.GetStream().ReadAsync(bytes, Ct)); }
        catch (IOException) { }
        using var retry = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(async () => await retry.ConnectAsync(IPAddress.Loopback, url.Port, Ct));
    }

    [Fact]
    public async Task ExcessConcurrentClientsAreClosedAndCancellationReleasesTheOthers()
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        await using var proxy = await ConnectTurnProxy.CreateAsync([new(["turn:127.0.0.1:3478?transport=tcp"])], lifetime.Token);
        var clients = new List<TcpClient>();
        try
        {
            for (var i = 0; i <= ConnectTurnProxy.MaximumConnections; i++) clients.Add(await OpenAsync(proxy));
            var buffer = new byte[1];
            Assert.Equal(0, await clients[^1].GetStream().ReadAsync(buffer, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(3), Ct));
            await lifetime.CancelAsync();
            for (var i = 0; i < ConnectTurnProxy.MaximumConnections; i++)
                Assert.Equal(0, await clients[i].GetStream().ReadAsync(buffer, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(3), Ct));
        }
        finally { foreach (var client in clients) client.Dispose(); }
    }

    private static TcpListener Listen()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return listener;
    }

    private static string Port(TcpListener listener) => ((IPEndPoint)listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);

    private static async Task<TcpClient> OpenAsync(ConnectTurnProxy proxy)
    {
        var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, new Uri(proxy.ProxyUrl!).Port, Ct);
        return client;
    }

    private static string Authorization(ConnectTurnProxy proxy) => "Basic " + Convert.ToBase64String(
        Encoding.ASCII.GetBytes(Uri.UnescapeDataString(new Uri(proxy.ProxyUrl!).UserInfo)));

    private static string Request(ConnectTurnProxy proxy, string target) =>
        $"CONNECT {target} HTTP/1.1\r\nHost: {target}\r\nProxy-Authorization: {Authorization(proxy)}\r\n\r\n";

    private static async Task<string> HeaderAsync(Stream stream)
    {
        var bytes = new List<byte>();
        var value = new byte[1];
        while (bytes.Count < 1024)
        {
            await stream.ReadExactlyAsync(value, Ct).AsTask().WaitAsync(TimeSpan.FromSeconds(3), Ct);
            bytes.Add(value[0]);
            if (bytes.Count >= 4 && bytes[^4] == 13 && bytes[^3] == 10 && bytes[^2] == 13 && bytes[^1] == 10)
                return Encoding.ASCII.GetString(bytes.ToArray());
        }
        throw new IOException("Proxy fixture response exceeded its limit.");
    }

    [SuppressMessage("Usage", "IDISP001:Dispose created", Justification = "Stream is a borrowed alias of the two await-using wrappers.")]
    private static async Task EchoAsync(TcpListener listener, bool tls, X509Certificate2? certificate = null)
    {
        using var client = await listener.AcceptTcpClientAsync(Ct);
        await using var network = client.GetStream();
        await using var secure = tls ? new SslStream(network, leaveInnerStreamOpen: true) : null;
        Stream stream = (Stream?)secure ?? network;
        try
        {
            if (secure is not null)
                await secure.AuthenticateAsServerAsync(new SslServerAuthenticationOptions { ServerCertificate = certificate }, Ct);
            var bytes = new byte[4];
            await stream.ReadExactlyAsync(bytes, Ct);
            await stream.WriteAsync(bytes, Ct);
        }
        catch (Exception ex) when (tls && ex is AuthenticationException or IOException)
        {
            TestContext.Current.TestOutputHelper?.WriteLine(ex.ToString());
        }
    }

    private static (X509Certificate2 Root, X509Certificate2 Leaf) Certificates(string name)
    {
        using var rootKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var rootRequest = new CertificateRequest("CN=TURN protocol fixture root", rootKey, HashAlgorithmName.SHA256);
        rootRequest.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, false, 0, true));
        rootRequest.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign, true));
        var root = rootRequest.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(1));
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest($"CN={name}", key, HashAlgorithmName.SHA256);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(name);
        request.CertificateExtensions.Add(names.Build());
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension([new Oid("1.3.6.1.5.5.7.3.1")], true));
        using var issued = request.Create(root, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddHours(1), RandomNumberGenerator.GetBytes(16));
        using var leaf = issued.CopyWithPrivateKey(key);
        // Schannel server credentials cannot use the ephemeral ECDSA handle.
        // Importing this in-memory PKCS12 gives it an owned platform key; no root
        // is installed into a machine/user trust store.
        var encoded = leaf.Export(X509ContentType.Pkcs12);
        try { return (root, X509CertificateLoader.LoadPkcs12(encoded, null, X509KeyStorageFlags.DefaultKeySet)); }
        finally { CryptographicOperations.ZeroMemory(encoded); }
    }
}

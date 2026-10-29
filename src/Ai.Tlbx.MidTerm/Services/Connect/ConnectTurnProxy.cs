using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Ai.Tlbx.MidTerm.Services.Connect;

// The asynchronous owner disposes these synchronous resources after its accept and
// copy tasks finish; the disposal analyzer does not follow that awaited helper.
#pragma warning disable IDISP002, IDISP006

// libnice's HTTP CONNECT support supplies the TCP stream. TLS for a turns: target
// terminates here, using the original DNS name and the normal platform trust store.
internal sealed class ConnectTurnProxy : IAsyncDisposable
{
    internal const int MaximumHeaderBytes = 8192;
    internal const int MaximumConnections = 16;
    private static readonly TimeSpan HandshakeTimeout = TimeSpan.FromSeconds(10);
    private readonly Lock _sync = new();
    private readonly CancellationTokenSource _lifetime;
    private readonly Dictionary<IPEndPoint, Target> _targets;
    private readonly byte[] _authorization;
    private readonly X509ChainPolicy? _certificateChainPolicy;
    private readonly TcpListener? _listener;
    private readonly HashSet<TcpClient> _clients = [];
    private readonly HashSet<Task> _attempts = [];
    private readonly Task _acceptTask;
    private Task? _disposeTask;

    internal ConnectIceServer[] NativeServers { get; }
    internal string? ProxyUrl { get; }

    private ConnectTurnProxy(ConnectIceServer[] servers, Dictionary<IPEndPoint, Target> targets,
        X509ChainPolicy? certificateChainPolicy, CancellationToken ct)
    {
        NativeServers = servers;
        _targets = targets;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _certificateChainPolicy = certificateChainPolicy;
        var username = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
        var password = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        _authorization = Encoding.ASCII.GetBytes("Basic " + Convert.ToBase64String(
            Encoding.ASCII.GetBytes(username + ":" + password)));
        if (targets.Count == 0)
        {
            _acceptTask = Task.CompletedTask;
            return;
        }

        _listener = new TcpListener(IPAddress.Loopback, 0);
        try { _listener.Start(MaximumConnections); }
        catch
        {
            _listener.Dispose();
            _lifetime.Dispose();
            CryptographicOperations.ZeroMemory(_authorization);
            throw;
        }
        ProxyUrl = string.Create(CultureInfo.InvariantCulture,
            $"http://{username}:{password}@127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}");
        _acceptTask = AcceptAsync();
    }

    // A custom root policy is useful for private PKI and isolated TLS protocol tests.
    // A null policy uses platform trust; hostname validation is never overridden.
    internal static async Task<ConnectTurnProxy> CreateAsync(ConnectIceServer[] servers,
        CancellationToken ct, X509ChainPolicy? certificateChainPolicy = null)
    {
        ArgumentNullException.ThrowIfNull(servers);
        var native = new List<ConnectIceServer>();
        var targets = new Dictionary<IPEndPoint, Target>();
        var count = 0;
        using var resolution = CancellationTokenSource.CreateLinkedTokenSource(ct);
        resolution.CancelAfter(HandshakeTimeout);
        foreach (var server in servers)
        {
            var urls = new List<string>();
            foreach (var url in server.Urls)
            {
                ct.ThrowIfCancellationRequested();
                if (++count > 64 || string.IsNullOrEmpty(url) || url.Length > 2048)
                    throw new InvalidOperationException("Invalid or excessive ICE server configuration.");
                var colon = url.IndexOf(':', StringComparison.Ordinal);
                var scheme = colon > 0 ? url[..colon].ToLowerInvariant() : "";
                if (scheme is not ("turn" or "turns"))
                {
                    urls.Add(url);
                    continue;
                }

                if (!Uri.TryCreate(scheme + "://" + url[(colon + 1)..], UriKind.Absolute, out var uri)
                    || uri.UserInfo.Length != 0 || uri.Fragment.Length != 0
                    || uri.AbsolutePath is not ("" or "/") || uri.Host.Length == 0)
                    throw new InvalidOperationException("Invalid TURN server URL.");
                var transport = uri.Query.ToLowerInvariant();
                if (transport is not ("" or "?transport=udp" or "?transport=tcp" or "?transport=tls")
                    || (scheme == "turns" && transport == "?transport=udp"))
                    throw new InvalidOperationException("Invalid TURN transport.");
                var tls = scheme == "turns" || transport == "?transport=tls";
                if (!tls && transport != "?transport=tcp")
                {
                    urls.Add(url);
                    continue;
                }

                var host = uri.IdnHost.Trim('[', ']');
                var port = uri.Port == -1 ? (tls ? 5349 : 3478) : uri.Port;
                if (port is < 1 or > 65535) throw new InvalidOperationException("Invalid TURN server port.");
                IPAddress[] addresses;
                if (IPAddress.TryParse(host, out var literal))
                {
                    addresses = [literal];
                }
                else
                {
                    // A failed optional relay must not prevent host/STUN/UDP
                    // connectivity. One shared deadline bounds all DNS work;
                    // literal targets and other ICE options remain usable.
                    if (resolution.IsCancellationRequested) continue;
                    try { addresses = await Dns.GetHostAddressesAsync(host, resolution.Token); }
                    catch (SocketException) { continue; }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested) { continue; }
                }
                addresses = addresses.Distinct().Take(8).ToArray();
                foreach (var address in addresses)
                {
                    var endpoint = new IPEndPoint(address, port);
                    var target = new Target(endpoint, host, tls);
                    if (targets.TryGetValue(endpoint, out var existing)
                        && (existing.Tls != tls || (tls && !string.Equals(existing.Host, host, StringComparison.OrdinalIgnoreCase))))
                        throw new InvalidOperationException("Ambiguous TURN targets share an address and port with different TLS settings or names.");
                    targets[endpoint] = target;
                    var ip = address.AddressFamily == AddressFamily.InterNetworkV6 ? "[" + address.ToString() + "]" : address.ToString();
                    // Native TURN sees an ordinary TCP stream. Only this proxy
                    // owns TLS, so neither native TLS nor legacy pseudoSSL can
                    // be layered over the already authenticated SslStream.
                    urls.Add(string.Create(CultureInfo.InvariantCulture, $"turn:{ip}:{port}?transport=tcp"));
                    if (targets.Count > 64) throw new InvalidOperationException("Too many resolved TURN targets.");
                }
            }
            if (urls.Count > 0) native.Add(server with { Urls = urls.ToArray() });
        }
        ct.ThrowIfCancellationRequested();
        return new ConnectTurnProxy(native.ToArray(), targets, certificateChainPolicy, ct);
    }

    private async Task AcceptAsync()
    {
        try
        {
            while (!_lifetime.IsCancellationRequested)
            {
                var client = await _listener!.AcceptTcpClientAsync(_lifetime.Token);
                TracePhase("accepted");
                Task attempt;
                lock (_sync)
                {
                    if (_attempts.Count >= MaximumConnections || _lifetime.IsCancellationRequested)
                    {
                        TraceFailure("capacity", 0);
                        client.Dispose();
                        continue;
                    }
                    _clients.Add(client);
                    attempt = HandleAsync(client);
                    _attempts.Add(attempt);
                }
                _ = ObserveAsync(attempt);
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
        {
            if (!_lifetime.IsCancellationRequested) TraceFailure("accept", 0);
        }
    }

    private async Task ObserveAsync(Task attempt)
    {
        try { await attempt; }
        finally { lock (_sync) _attempts.Remove(attempt); }
    }

    [SuppressMessage("Usage", "IDISP007:Don't dispose injected", Justification = "The accept loop transfers ownership of this client to the attempt.")]
    [SuppressMessage("Usage", "IDISP017:Prefer using", Justification = "Close the socket before awaiting the canceled opposite copy; using still handles every error path.")]
    [SuppressMessage("Usage", "IDISP001:Dispose created", Justification = "Local/upstream are borrowed aliases of the owned TcpClient/await-using stream wrappers.")]
    private async Task HandleAsync(TcpClient client)
    {
        var established = false;
        NetworkStream? local = null;
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        handshake.CancelAfter(HandshakeTimeout);
        try
        {
            client.NoDelay = true;
            local = client.GetStream();
            var (target, pending) = await ReadRequestAsync(local, handshake.Token);
            using var remote = new TcpClient(target.Endpoint.AddressFamily) { NoDelay = true };
            await remote.ConnectAsync(target.Endpoint.Address, target.Endpoint.Port, handshake.Token);
            TracePhase("upstreamConnected");
            await using var remoteStream = remote.GetStream();
            await using var tls = target.Tls ? new SslStream(remoteStream, leaveInnerStreamOpen: true) : null;
            Stream upstream = (Stream?)tls ?? remoteStream;
            if (tls is not null)
                await tls.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = target.Host,
                    EnabledSslProtocols = SslProtocols.None,
                    CertificateChainPolicy = _certificateChainPolicy
                }, handshake.Token);
            await RespondAsync(local, 200, handshake.Token);
            established = true;
            TracePhase("tunnelReady");
            handshake.CancelAfter(Timeout.InfiniteTimeSpan);
            if (pending.Length > 0) await upstream.WriteAsync(pending, handshake.Token);
            using var transfer = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            var upload = local.CopyToAsync(upstream, 16384, transfer.Token);
            var download = upstream.CopyToAsync(local, 16384, transfer.Token);
            var completed = await Task.WhenAny(upload, download);
            if (completed.IsFaulted) TraceFailure("stream", 0);
            await transfer.CancelAsync();
            remote.Dispose();
            client.Dispose();
            try { await Task.WhenAll(upload, download); }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
        catch (Exception ex) when (ex is IOException or SocketException or AuthenticationException
            or OperationCanceledException or ObjectDisposedException or ProxyRequestException)
        {
            var status = established || _lifetime.IsCancellationRequested ? 0 : ex is ProxyRequestException request ? request.Status : 502;
            TraceFailure(ex switch
            {
                ProxyRequestException => "request",
                AuthenticationException => "tls",
                OperationCanceledException => "canceled",
                ObjectDisposedException => "closed",
                _ => "io"
            }, status);
            if (!established && local is not null && !_lifetime.IsCancellationRequested)
            {
                try { await RespondAsync(local, status, _lifetime.Token); }
                catch (Exception error) when (error is IOException or SocketException or ObjectDisposedException
                    or InvalidOperationException or OperationCanceledException) { }
            }
        }
        finally
        {
            client.Dispose();
            lock (_sync) _clients.Remove(client);
        }
    }

    private async Task<(Target Target, byte[] Pending)> ReadRequestAsync(Stream stream, CancellationToken ct)
    {
        var bytes = new byte[MaximumHeaderBytes];
        var length = 0;
        var end = -1;
        while (end < 0)
        {
            if (length == bytes.Length) throw new ProxyRequestException(431);
            var read = await stream.ReadAsync(bytes.AsMemory(length), ct);
            if (read == 0) throw new ProxyRequestException(400);
            length += read;
            for (var i = Math.Max(0, length - read - 3); i + 3 < length; i++)
                if (bytes[i] == 13 && bytes[i + 1] == 10 && bytes[i + 2] == 13 && bytes[i + 3] == 10)
                { end = i + 4; break; }
        }
        for (var i = 0; i < end; i++)
            if (bytes[i] > 127 || (bytes[i] < 32 && bytes[i] is not (9 or 10 or 13)))
                throw new ProxyRequestException(400);
            else if ((bytes[i] == 13 && (i + 1 >= end || bytes[i + 1] != 10))
                || (bytes[i] == 10 && (i == 0 || bytes[i - 1] != 13)))
                throw new ProxyRequestException(400);
        var lines = Encoding.ASCII.GetString(bytes, 0, end - 4).Split("\r\n", StringSplitOptions.None);
        var request = lines[0].Split(' ');
        if (request.Length != 3 || request[0] != "CONNECT" || request[2] is not ("HTTP/1.0" or "HTTP/1.1"))
            throw new ProxyRequestException(400);
        string? authorization = null;
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon < 1 || line[0] is ' ' or '\t') throw new ProxyRequestException(400);
            var name = line[..colon];
            if (name.Any(c => !char.IsAsciiLetterOrDigit(c) && !"!#$%&'*+-.^_`|~".Contains(c, StringComparison.Ordinal)))
                throw new ProxyRequestException(400);
            var value = line[(colon + 1)..].Trim();
            if (name.Equals("Proxy-Authorization", StringComparison.OrdinalIgnoreCase))
            {
                if (authorization is not null) throw new ProxyRequestException(407);
                authorization = value;
            }
            if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase)
                || (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && value != "0"))
                throw new ProxyRequestException(400);
        }
        if (authorization is null || !CryptographicOperations.FixedTimeEquals(
            Encoding.ASCII.GetBytes(authorization), _authorization)) throw new ProxyRequestException(407);
        TracePhase("authenticated");
        var authority = request[1];
        var separator = authority.LastIndexOf(':');
        if (separator < 1 || !int.TryParse(authority.AsSpan(separator + 1), NumberStyles.None,
                CultureInfo.InvariantCulture, out var port) || port is < 1 or > 65535)
            throw new ProxyRequestException(403);
        var host = authority[..separator];
        var bracketed = host.StartsWith('[') && host.EndsWith(']');
        if (bracketed) host = host[1..^1];
        // libnice emits bare IPv6 CONNECT authorities. The final colon still
        // separates the port; only an exact configured numeric endpoint passes.
        if (host.Contains('[', StringComparison.Ordinal) || host.Contains(']', StringComparison.Ordinal)
            || !IPAddress.TryParse(host, out var address)
            || (bracketed && address.AddressFamily != AddressFamily.InterNetworkV6)
            || !_targets.TryGetValue(new IPEndPoint(address, port), out var target))
            throw new ProxyRequestException(403);
        return (target, bytes.AsSpan(end, length - end).ToArray());
    }

    private static ValueTask RespondAsync(Stream stream, int status, CancellationToken ct)
    {
        var extra = status == 407 ? "Proxy-Authenticate: Basic realm=\"tlbx-turn\"\r\n" : "";
        var message = status == 200 ? "Connection Established" : "Connection Rejected";
        return stream.WriteAsync(Encoding.ASCII.GetBytes(string.Create(CultureInfo.InvariantCulture,
            $"HTTP/1.1 {status} {message}\r\n{extra}Content-Length: 0\r\n\r\n")), ct);
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void TracePhase(string phase)
    {
#if DEBUG
        Console.Error.WriteLine("[connect-turn] " + phase);
#endif
    }

    [System.Diagnostics.Conditional("DEBUG")]
    private static void TraceFailure(string category, int status)
    {
#if DEBUG
        Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"[connect-turn] failure category={category} status={status}"));
#endif
    }

    public ValueTask DisposeAsync()
    {
        lock (_sync) return new ValueTask(_disposeTask ??= DisposeCoreAsync());
    }

    private async Task DisposeCoreAsync()
    {
        await _lifetime.CancelAsync();
        _listener?.Dispose();
        await _acceptTask;
        Task[] attempts;
        lock (_sync)
        {
            foreach (var client in _clients) client.Dispose();
            attempts = _attempts.ToArray();
        }
        try { await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
        catch (TimeoutException) { }
        _lifetime.Dispose();
        CryptographicOperations.ZeroMemory(_authorization);
    }

    private sealed record Target(IPEndPoint Endpoint, string Host, bool Tls);
    private sealed class ProxyRequestException(int status) : Exception
    {
        internal int Status { get; } = status;
    }
}

using System.Collections.Concurrent;
using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Services.Hosting;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Startup;

namespace Ai.Tlbx.MidTerm.Services.Connect;

/// <summary>Routes authenticated peer traffic only to this installation, never an arbitrary destination.</summary>
internal sealed class ConnectTunnel : IAsyncDisposable
{
    private const int MaximumBody = 20 * 1024 * 1024;
    private readonly Uri _origin;
    private readonly Uri? _previewOrigin;
    private readonly BrowserPreviewRegistry _previewRegistry;
    private string? _cookie;
    private readonly ConnectPeerAuthentication _peer;
    private readonly HttpClient _http;
    private readonly AuthService _auth;
    private readonly Func<ConnectSignal, CancellationToken, Task> _send;
    private readonly CancellationTokenSource _lifetime;
    private readonly ConnectFrames _frames = new();
    private readonly SemaphoreSlim _sendGate = new(1, 1);
    private readonly SemaphoreSlim _requestSlots = new(16, 16);
    private readonly ConcurrentDictionary<string, ConnectWebSocketRelay> _sockets = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Task> _requests = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ConnectPreviewClient> _previews = new(StringComparer.Ordinal);
    private readonly Lock _previewGate = new();
    private readonly Lock _socketGate = new();
    private bool _disposed;

    public ConnectTunnel(ServerBindingInfo binding, BrowserPreviewOriginService previewOrigin, BrowserPreviewRegistry previewRegistry,
        AuthService auth, HttpClient http,
        Func<ConnectSignal, CancellationToken, Task> send, ConnectPeerAuthentication peer, CancellationToken ct)
    {
        var host = binding.BindAddress is "0.0.0.0" or "*" or "+" ? "127.0.0.1"
            : binding.BindAddress == "::" ? "::1" : binding.BindAddress;
        _origin = new UriBuilder("https", host, binding.Port).Uri;
        _previewOrigin = previewOrigin.IsEnabled ? new UriBuilder("https", host, previewOrigin.PreviewPort).Uri : null;
        _previewRegistry = previewRegistry;
        _auth = auth;
        _peer = peer;
        _send = send;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _http = http;
    }

    internal static HttpClientHandler CreateLoopbackHandler() => new()
        {
            UseProxy = false,
            UseCookies = false,
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.All,
            ServerCertificateCustomValidationCallback = static (_, cert, _, _) => IsOwnCertificate(cert)
        };

    private static bool IsOwnCertificate(X509Certificate? certificate)
    {
        var own = ServerSetup.LoadedCertificate;
        return own is not null && certificate is not null
            && own.RawData.AsSpan().SequenceEqual(certificate.GetRawCertData());
    }

    public Task BeginAuthenticationAsync() => SendEnvelopeAsync(_peer.CreateChallenge(), _lifetime.Token);

    public async Task ReceiveFrameAsync(byte[] frame)
    {
        if (!_peer.Authenticated && (frame.Length < ConnectFrames.HeaderSize || BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(9)) > 4096))
            throw new InvalidDataException("Unauthenticated Connect frame exceeds the authentication limit.");
        var complete = _frames.Receive(frame);
        if (complete is null) return;
        var message = JsonSerializer.Deserialize(complete, ConnectJsonContext.Default.ConnectEnvelope)
            ?? throw new InvalidDataException("Empty Connect request.");
        if (!_peer.Authenticated)
        {
            _peer.AcceptProof(message);
            _cookie = _auth.CreateSessionToken();
            await SendEnvelopeAsync(new ConnectEnvelope { Type = "authOk" }, _lifetime.Token);
            return;
        }
        if (string.IsNullOrEmpty(message.Id) || message.Id.Length > 100 || message.Id.Any(char.IsControl))
            throw new InvalidDataException("Invalid Connect request identifier.");
        if (message.Type is "ws-data" or "ws-close")
        {
            await DispatchSocketAsync(message, _lifetime.Token);
            return;
        }
        if (message.Type is not ("http" or "ws-open"))
            throw new InvalidDataException("Unsupported Connect request type.");
        if (_requests.ContainsKey(message.Id)) throw new InvalidDataException("Duplicate Connect request identifier.");
        if (_requests.Count >= 128) { await RejectBusyAsync(message); return; }
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_requests.TryAdd(message.Id, completion.Task))
            throw new InvalidDataException("Duplicate Connect request identifier.");
        _ = CompleteRequestAsync(message, completion);
    }

    private async Task CompleteRequestAsync(ConnectEnvelope message, TaskCompletionSource completion)
    {
        var entered = false;
        try
        {
            await _requestSlots.WaitAsync(_lifetime.Token);
            entered = true;
            if (message.Type == "http") await ForwardHttpAsync(message, _lifetime.Token);
            else await OpenSocketAsync(message, _lifetime.Token);
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception ex) when (ex is HttpRequestException or WebSocketException or InvalidDataException or FormatException
            or ArgumentException or IOException or OperationCanceledException or ConnectLocalAccessException)
        {
            try
            {
                await SendEnvelopeAsync(message.Type == "http"
                    ? new ConnectEnvelope
                    {
                        Type = "http-result", Id = message.Id, Status = ex is ConnectLocalAccessException ? 403 : 502,
                        Headers = new(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "text/plain; charset=utf-8" },
                        Body = Convert.ToBase64String(Encoding.UTF8.GetBytes("The local tlbx request could not be completed."))
                    }
                    : new ConnectEnvelope { Type = "ws-close", Id = message.Id, Code = ex is ConnectLocalAccessException ? 1008 : 1011, Reason = "Local connection failed." },
                    _lifetime.Token);
            }
            catch (Exception sendException) when (sendException is IOException or OperationCanceledException or ObjectDisposedException) { }
        }
        finally
        {
            if (entered) _requestSlots.Release();
            _requests.TryRemove(message.Id, out _);
            completion.TrySetResult();
        }
    }

    private Task RejectBusyAsync(ConnectEnvelope message) => SendEnvelopeAsync(message.Type == "http"
        ? new ConnectEnvelope
        {
            Type = "http-result", Id = message.Id, Status = 429,
            Headers = new(StringComparer.OrdinalIgnoreCase) { ["Content-Type"] = "text/plain; charset=utf-8", ["Retry-After"] = "1" },
            Body = Convert.ToBase64String(Encoding.UTF8.GetBytes("Too many pending requests. Try again shortly."))
        }
        : new ConnectEnvelope { Type = "ws-close", Id = message.Id, Code = 1008, Reason = "Too many pending requests." }, _lifetime.Token);

    private async Task ForwardHttpAsync(ConnectEnvelope message, CancellationToken ct)
    {
        var (destination, preview) = ResolveDestination(message);
        var method = message.Method?.ToUpperInvariant() ?? "GET";
        if (method is not ("GET" or "HEAD" or "POST" or "PUT" or "PATCH" or "DELETE" or "OPTIONS"))
            throw new InvalidDataException("Unsupported HTTP method.");
        using var request = new HttpRequestMessage(new HttpMethod(method), destination);
        if (message.Body is not null)
        {
            var body = Convert.FromBase64String(message.Body);
            if (body.Length > MaximumBody) throw new InvalidDataException("Connect request body is too large.");
            request.Content = new ByteArrayContent(body);
        }
        if (message.Headers is not null)
        {
            if (message.Headers.Count > 64) throw new InvalidDataException("Too many Connect headers.");
            foreach (var (name, value) in message.Headers)
            {
                if (value.Length > 16384 || value.Any(c => c is '\r' or '\n'))
                    throw new InvalidDataException("Invalid Connect request header.");
                if (name.Equals("Accept-Encoding", StringComparison.OrdinalIgnoreCase)
                    || !(preview is null ? ConnectRequestPolicy.AllowHeader(name) : ConnectRequestPolicy.AllowPreviewHeader(name))) continue;
                if (!request.Headers.TryAddWithoutValidation(name, value) && request.Content is not null)
                    request.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }
        if (preview is null) request.Headers.Add("Cookie", $"{AuthService.SessionCookieName}={_cookie}");
        else
        {
            request.Headers.Add("Origin", _previewOrigin!.GetLeftPart(UriPartial.Authority));
            request.Headers.Referrer = new Uri(_previewOrigin, "/webpreview/" + message.PreviewRouteKey + "/");
        }
        using var response = await (preview?.Http ?? _http).SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var header in response.Headers.Concat(response.Content.Headers))
        {
            if (header.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                || header.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase)
                || !ConnectRequestPolicy.AllowHeader(header.Key)) continue;
            headers[header.Key] = string.Join(", ", header.Value);
        }
        if (response.Headers.Location is { IsAbsoluteUri: true } location && location.Authority == destination.Authority)
            headers["Location"] = location.PathAndQuery;
        await using var source = await response.Content.ReadAsStreamAsync(ct);
        var bytes = await ReadBoundedAsync(source, MaximumBody, ct);
        await SendEnvelopeAsync(new ConnectEnvelope
        {
            Type = "http-result", Id = message.Id, Status = (int)response.StatusCode,
            Headers = headers, Body = Convert.ToBase64String(bytes)
        }, ct);
    }

    private async Task OpenSocketAsync(ConnectEnvelope message, CancellationToken ct)
    {
        var (destination, preview) = ResolveDestination(message);
        if (preview is not null && destination.AbsolutePath == "/ws/browser")
        {
            if (!ConnectRequestPolicy.AuthorizePreviewSocket(_previewRegistry, destination, message.PreviewRouteKey!))
                throw new InvalidDataException("The browser command channel requires this preview's credentials.");
        }
        var builder = new UriBuilder(destination) { Scheme = "wss" };
        var socket = new ClientWebSocket();
        var owner = new ConnectWebSocketRelay(socket, message.Id, SendEnvelopeAsync, MaximumBody);
        var registered = false;
        var started = false;
        try
        {
            socket.Options.RemoteCertificateValidationCallback = static (_, cert, _, _) => IsOwnCertificate(cert);
            if (preview is null) socket.Options.SetRequestHeader("Cookie", $"{AuthService.SessionCookieName}={_cookie}");
            else
            {
                var cookie = preview.Cookies.GetCookieHeader(destination);
                if (!string.IsNullOrEmpty(cookie)) socket.Options.SetRequestHeader("Cookie", cookie);
                socket.Options.SetRequestHeader("Origin", _previewOrigin!.GetLeftPart(UriPartial.Authority));
                socket.Options.SetRequestHeader("Referer", new Uri(_previewOrigin, "/webpreview/" + message.PreviewRouteKey + "/").AbsoluteUri);
            }
            if (message.Protocols is not null)
            {
                if (message.Protocols.Length > 8) throw new InvalidDataException("Too many WebSocket protocols.");
                foreach (var protocol in message.Protocols) socket.Options.AddSubProtocol(protocol);
            }
            lock (_socketGate)
            {
                if (_sockets.Count >= 16) throw new InvalidDataException("Connect WebSocket limit exceeded.");
                registered = _sockets.TryAdd(message.Id, owner);
                if (!registered) throw new InvalidDataException("Duplicate Connect WebSocket identifier.");
            }
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(15));
            await socket.ConnectAsync(builder.Uri, timeout.Token);
            await SendEnvelopeAsync(new ConnectEnvelope { Type = "ws-opened", Id = message.Id, Protocol = socket.SubProtocol }, ct);
            owner.ReceiveTask = ReceiveSocketAsync(message.Id, owner, ct);
            started = true;
        }
        finally
        {
            if (!started)
            {
                if (registered) _sockets.TryRemove(message.Id, out _);
                await owner.DisposeAsync();
            }
        }
    }

    private async Task ReceiveSocketAsync(string id, ConnectWebSocketRelay owner, CancellationToken ct)
    {
        try
        {
            await owner.ReceiveAsync(ct);
        }
        finally
        {
            _sockets.TryRemove(id, out _);
        }
    }

    internal (Uri Destination, ConnectPreviewClient? Preview) ResolveDestination(ConnectEnvelope message)
    {
        if (message.Surface is null or "main")
            return (ConnectRequestPolicy.ResolvePath(_origin, message.Path), null);
        if (message.Surface != "preview" || _previewOrigin is null)
            throw new InvalidDataException("Unsupported Connect surface.");
        var destination = ConnectRequestPolicy.ResolvePreviewPath(_previewOrigin, message.Path, message.PreviewRouteKey);
        lock (_previewGate)
        {
            if (!_previews.TryGetValue(message.PreviewRouteKey!, out var preview))
            {
                if (!_previewRegistry.HasActiveRoute(message.PreviewRouteKey!))
                    throw new InvalidDataException("The preview route is no longer registered.");
                foreach (var stale in _previews.Keys.Where(key => !_previewRegistry.HasActiveRoute(key)).ToArray())
                {
                    _previews[stale].Dispose();
                    _previews.Remove(stale);
                }
                if (_previews.Count >= 32) throw new InvalidDataException("Connect preview limit exceeded.");
                preview = new ConnectPreviewClient();
                _previews.Add(message.PreviewRouteKey!, preview);
            }
            return (destination, preview);
        }
    }

    private async Task DispatchSocketAsync(ConnectEnvelope message, CancellationToken ct)
    {
        if (!_sockets.TryGetValue(message.Id, out var owner)) return;
        await owner.DispatchAsync(message, ct);
    }

    private async Task SendEnvelopeAsync(ConnectEnvelope envelope, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ConnectJsonContext.Default.ConnectEnvelope);
        await _sendGate.WaitAsync(ct);
        try
        {
            foreach (var frame in _frames.Encode(bytes))
                await _send(new ConnectSignal { Type = "send", Data = Convert.ToBase64String(frame) }, ct);
        }
        finally { _sendGate.Release(); }
    }

    private static async Task<byte[]> ReadBoundedAsync(Stream source, int limit, CancellationToken ct)
    {
        using var target = new MemoryStream();
        var buffer = new byte[48 * 1024];
        int read;
        while ((read = await source.ReadAsync(buffer, ct)) > 0)
        {
            if (target.Length > limit - read) throw new InvalidDataException("Connect response body is too large.");
            target.Write(buffer, 0, read);
        }
        return target.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        var sockets = _sockets.Values.ToArray();
        foreach (var owner in sockets) owner.Abort();
        await Task.WhenAll(_requests.Values);
        foreach (var owner in sockets) await owner.DisposeAsync();
        foreach (var preview in _previews.Values) preview.Dispose();
        if (_cookie is not null) _auth.RevokeSessionToken(_cookie);
        _sendGate.Dispose();
        _requestSlots.Dispose();
        _lifetime.Dispose();
    }

}

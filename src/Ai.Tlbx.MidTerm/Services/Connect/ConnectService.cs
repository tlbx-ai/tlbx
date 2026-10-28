using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Services.Hosting;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services.Connect;

/// <summary>Opt-in outbound device registration and peer rendezvous. Signaling never carries workstation data.</summary>
public sealed class ConnectService : BackgroundService
{
    private const string SecretKey = "connect.device.v1";
    private readonly SettingsService _settings;
    private readonly AuthService _auth;
    private readonly ServerBindingInfo _binding;
    private readonly HttpClientHandler _registrationHandler = new() { AllowAutoRedirect = false };
    private readonly HttpClientHandler _loopbackHandler = ConnectTunnel.CreateLoopbackHandler();
    private readonly HttpClient _registrationHttp;
    private readonly HttpClient _loopbackHttp;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly SemaphoreSlim _pairGate = new(1, 1);
    private readonly SemaphoreSlim _signalWriter = new(1, 1);
    private readonly ConcurrentDictionary<string, ConnectionOwner> _connections = new(StringComparer.Ordinal);
    private ConnectDeviceConfiguration? _configuration;
    private ConnectAuthorizeResponse? _grant;
    private Uri? _grantOrigin;
    private string? _grantName;
    private DateTimeOffset _grantExpires;
    // ExecuteAsync owns and disposes each generation source. Control endpoints only request cancellation.
    private Action? _cancelGeneration;
    private bool _online;
    private bool _revoked;
    private string? _lastError;

    [SuppressMessage("Usage", "IDISP014:Use a single instance of HttpClient",
        Justification = "This service is a process singleton. Its two lifetime clients isolate public TLS validation from pinned local requests.")]
    public ConnectService(SettingsService settings, AuthService auth, ServerBindingInfo binding)
    {
        _settings = settings;
        _auth = auth;
        _binding = binding;
        _registrationHttp = new HttpClient(_registrationHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        _loopbackHttp = new HttpClient(_loopbackHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) };
        var stored = settings.SecretStorage.GetSecret(SecretKey);
        if (stored is null) return;
        try
        {
            var configuration = JsonSerializer.Deserialize(stored, ConnectJsonContext.Default.ConnectDeviceConfiguration);
            if (configuration is null || string.IsNullOrWhiteSpace(configuration.DeviceToken)
                || string.IsNullOrWhiteSpace(configuration.DeviceId)) throw new InvalidDataException();
            _ = ConnectRequestPolicy.NormalizeServerUrl(configuration.ServerUrl);
            _configuration = configuration;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException)
        {
            _lastError = "The saved Connect registration is invalid. Pair this installation again.";
        }
    }

    public ConnectStatus GetStatus()
    {
        lock (_gate)
        {
            return new ConnectStatus(_configuration is not null, _online, _configuration?.ServerUrl ?? _grantOrigin?.AbsoluteUri,
                _configuration?.Name ?? _grantName, _configuration?.DeviceId, _lastError, _connections.Count,
                _grant is null ? null : new ConnectPairResponse(_grant.UserCode, _grant.VerificationUri,
                    Math.Max(0, (int)(_grantExpires - DateTimeOffset.UtcNow).TotalSeconds)));
        }
    }

    public async Task<ConnectPairResponse> PairAsync(ConnectPairRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_settings.Load().PasswordHash))
            throw new InvalidOperationException("Set a local tlbx password before enabling Connect.");
        var origin = ConnectRequestPolicy.NormalizeServerUrl(request.ServerUrl);
        var name = string.IsNullOrWhiteSpace(request.Name) ? Environment.MachineName : request.Name.Trim();
        if (name.Length is < 1 or > 100 || name.Any(char.IsControl)) throw new ArgumentException("Use a device name of 1–100 characters.");
        await _pairGate.WaitAsync(ct);
        try
        {
            using var response = await _registrationHttp.PostAsJsonAsync(new Uri(origin, "/api/device/authorize"),
                new ConnectAuthorizeRequest(name), ConnectJsonContext.Default.ConnectAuthorizeRequest, ct);
            response.EnsureSuccessStatusCode();
            var grant = await ReadJsonAsync(response, ConnectJsonContext.Default.ConnectAuthorizeResponse, ct);
            if (grant.ExpiresIn is < 1 or > 1800 || grant.Interval is < 1 or > 60 || grant.DeviceCode.Length > 512
                || grant.UserCode.Length > 32 || !Uri.TryCreate(grant.VerificationUri, UriKind.Absolute, out var verification)
                || verification.GetLeftPart(UriPartial.Authority) != origin.GetLeftPart(UriPartial.Authority))
                throw new InvalidDataException("Connect returned an invalid pairing grant.");
            lock (_gate)
            {
                _grant = grant;
                _grantOrigin = origin;
                _grantName = name;
                _grantExpires = DateTimeOffset.UtcNow.AddSeconds(grant.ExpiresIn);
                _revoked = false;
                _lastError = null;
                _cancelGeneration?.Invoke();
            }
            Wake();
            return new ConnectPairResponse(grant.UserCode, grant.VerificationUri, grant.ExpiresIn);
        }
        finally { _pairGate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        await _pairGate.WaitAsync(ct);
        try
        {
            lock (_gate)
            {
                _settings.SecretStorage.DeleteSecret(SecretKey);
                _configuration = null;
                _grant = null;
                _grantOrigin = null;
                _grantName = null;
                _lastError = null;
                _revoked = false;
                _cancelGeneration?.Invoke();
            }
        }
        finally { _pairGate.Release(); }
        Wake();
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            ConnectDeviceConfiguration? configuration;
            ConnectAuthorizeResponse? grant;
            Uri? grantOrigin;
            string? grantName;
            bool revoked;
            using var generation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            lock (_gate)
            {
                _cancelGeneration = generation.Cancel;
                configuration = _configuration;
                grant = _grant;
                grantOrigin = _grantOrigin;
                grantName = _grantName;
                revoked = _revoked;
            }
            try
            {
                if (grant is not null && grantOrigin is not null)
                    await PollPairingAsync(grant, grantOrigin, grantName!, generation.Token);
                else if (configuration is not null && !revoked)
                    await RunSignalingAsync(configuration, generation.Token);
                else await _changed.WaitAsync(generation.Token);
            }
            catch (OperationCanceledException) when (generation.IsCancellationRequested) { }
            catch (Exception ex) when (ex is HttpRequestException or WebSocketException or IOException or JsonException
                or InvalidOperationException or ArgumentException or OperationCanceledException)
            {
                lock (_gate) _lastError = "Connect is temporarily unavailable. Retrying automatically.";
                try { await Task.Delay(TimeSpan.FromSeconds(10), generation.Token); }
                catch (OperationCanceledException) when (generation.IsCancellationRequested) { }
            }
            finally
            {
                lock (_gate)
                {
                    _cancelGeneration = null;
                    _online = false;
                }
                foreach (var id in _connections.Keys) await CloseConnectionAsync(id);
            }
        }
    }

    private async Task PollPairingAsync(ConnectAuthorizeResponse grant, Uri origin, string name, CancellationToken ct)
    {
        var interval = grant.Interval;
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(interval), ct);
            DateTimeOffset expires;
            lock (_gate) expires = _grantExpires;
            if (DateTimeOffset.UtcNow >= expires)
            {
                lock (_gate) { _grant = null; _lastError = "Pairing expired. Start pairing again."; }
                return;
            }
            using var response = await _registrationHttp.PostAsJsonAsync(new Uri(origin, "/api/device/token"),
                new ConnectTokenRequest(grant.DeviceCode), ConnectJsonContext.Default.ConnectTokenRequest, ct);
            if ((int)response.StatusCode == 428) continue;
            if (response.StatusCode == HttpStatusCode.TooManyRequests) { interval = Math.Min(60, interval + 5); continue; }
            if (response.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden or HttpStatusCode.Gone)
            {
                lock (_gate) { _grant = null; _lastError = "Pairing was declined or expired. Start pairing again."; }
                return;
            }
            response.EnsureSuccessStatusCode();
            var token = await ReadJsonAsync(response, ConnectJsonContext.Default.ConnectTokenResponse, ct);
            if (string.IsNullOrEmpty(token.DeviceId) || token.DeviceId.Length > 100
                || string.IsNullOrEmpty(token.DeviceToken) || token.DeviceToken.Length > 2048)
                throw new InvalidDataException("Connect returned an invalid device registration.");
            ct.ThrowIfCancellationRequested();
            var configuration = new ConnectDeviceConfiguration(origin.AbsoluteUri, name, token.DeviceId, token.DeviceToken);
            lock (_gate)
            {
                ct.ThrowIfCancellationRequested();
                _settings.SecretStorage.SetSecret(SecretKey, JsonSerializer.Serialize(configuration,
                    ConnectJsonContext.Default.ConnectDeviceConfiguration));
                _configuration = configuration;
                _grant = null;
                _lastError = null;
            }
            return;
        }
    }

    private async Task RunSignalingAsync(ConnectDeviceConfiguration configuration, CancellationToken ct)
    {
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Authorization", $"Bearer {configuration.DeviceToken}");
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(30);
        var origin = new Uri(configuration.ServerUrl);
        var uri = new UriBuilder(new Uri(origin, "/ws/device")) { Scheme = origin.Scheme == "https" ? "wss" : "ws" }.Uri;
        try
        {
            using var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(20));
            await socket.ConnectAsync(uri, connectTimeout.Token);
        }
        catch (WebSocketException) when (socket.HttpStatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            lock (_gate) { _revoked = true; _lastError = "Connect authorization was revoked. Pair this installation again."; }
            return;
        }
        lock (_gate) { _online = true; _lastError = null; }
        while (socket.State == WebSocketState.Open && !ct.IsCancellationRequested)
        {
            var message = await ReceiveSignalAsync(socket, ct);
            if (message is null) return;
            var id = message.ConnectionId;
            if (string.IsNullOrEmpty(id) || id.Length > 100 || !id.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
                throw new InvalidDataException("Invalid Connect connection identifier.");
            switch (message.Type)
            {
                case "connect":
                    if (_connections.Count >= 8 || _connections.ContainsKey(id))
                        throw new InvalidDataException("Connect peer limit exceeded.");
                    var owner = new ConnectionOwner(_binding, _auth, _loopbackHttp, ct);
                    if (!_connections.TryAdd(id, owner)) { await owner.DisposeAsync(); break; }
                    try
                    {
                        await owner.StartAsync(id, _settings.SettingsDirectory, Path.Combine(_settings.SettingsDirectory, "logs", "connect"), message,
                            async transportMessage =>
                            {
                                if (transportMessage.Type == "data")
                                {
                                    if (transportMessage.Data is null) throw new InvalidDataException("Missing Connect data.");
                                    if (_connections.TryGetValue(id, out var current))
                                        await current.Tunnel.ReceiveFrameAsync(Convert.FromBase64String(transportMessage.Data));
                                }
                                else if (transportMessage.Type is "answer" or "candidate" or "state")
                                {
                                    transportMessage.ConnectionId = id;
                                    await SendSignalAsync(socket, transportMessage, ct);
                                }
                                else if (transportMessage.Type == "error")
                                {
                                    await SendSignalAsync(socket, new ConnectSignal { Type = "state", ConnectionId = id, State = "failed" }, ct);
                                    _ = CloseConnectionAsync(id);
                                }
                            }, ct);
                    }
                    catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException)
                    {
                        await CloseConnectionAsync(id);
                        if (!ct.IsCancellationRequested)
                            await SendSignalAsync(socket, new ConnectSignal { Type = "state", ConnectionId = id, State = "failed" }, ct);
                    }
                    break;
                case "offer":
                case "candidate":
                    if (_connections.TryGetValue(id, out var connection))
                    {
                        message.ConnectionId = null;
                        await connection.SendAsync(message, ct);
                    }
                    break;
                case "close":
                    await CloseConnectionAsync(id);
                    break;
                default: throw new InvalidDataException("Unsupported Connect signaling message.");
            }
        }
    }

    private async Task CloseConnectionAsync(string id)
    {
        try
        {
            if (!_connections.TryRemove(id, out var owner)) return;
            await using (owner.ConfigureAwait(false)) { }
        }
        catch (Exception ex) when (ex is IOException or OperationCanceledException or ObjectDisposedException) { }
    }

    private async Task SendSignalAsync(ClientWebSocket socket, ConnectSignal message, CancellationToken ct)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(message, ConnectJsonContext.Default.ConnectSignal);
        await _signalWriter.WaitAsync(ct);
        try { await socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct); }
        finally { _signalWriter.Release(); }
    }

    private static async Task<ConnectSignal?> ReceiveSignalAsync(ClientWebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await socket.ReceiveAsync(buffer.AsMemory(), ct);
            if (result.MessageType == WebSocketMessageType.Close) return null;
            if (result.MessageType != WebSocketMessageType.Text || message.Length > 160 * 1024 - result.Count)
                throw new InvalidDataException("Invalid Connect signaling frame.");
            message.Write(buffer, 0, result.Count);
        } while (!result.EndOfMessage);
        return JsonSerializer.Deserialize(message.GetBuffer().AsSpan(0, checked((int)message.Length)),
            ConnectJsonContext.Default.ConnectSignal) ?? throw new InvalidDataException("Empty Connect signaling message.");
    }

    private static async Task<T> ReadJsonAsync<T>(HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo, CancellationToken ct)
    {
        if (response.Content.Headers.ContentLength > 64 * 1024) throw new InvalidDataException("Connect response is too large.");
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        var buffer = new byte[64 * 1024 + 1];
        var offset = 0;
        int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory(offset), ct)) > 0)
        {
            offset += count;
            if (offset > 64 * 1024) throw new InvalidDataException("Connect response is too large.");
        }
        return JsonSerializer.Deserialize(buffer.AsSpan(0, offset), typeInfo)
            ?? throw new InvalidDataException("Connect response is empty.");
    }

    private void Wake()
    {
        try { _changed.Release(); }
        catch (SemaphoreFullException) { /* A pending wake already covers the latest state. */ }
    }

    public override void Dispose()
    {
        base.Dispose();
        _changed.Dispose();
        _pairGate.Dispose();
        _signalWriter.Dispose();
        _registrationHttp.Dispose();
        _loopbackHttp.Dispose();
        _registrationHandler.Dispose();
        _loopbackHandler.Dispose();
    }

    private sealed class ConnectionOwner : IAsyncDisposable
    {
        private ConnectTransportProcess? _transport;
        private readonly CancellationTokenSource _lifetime;
        public ConnectTunnel Tunnel { get; }

        public ConnectionOwner(ServerBindingInfo binding, AuthService auth, HttpClient http, CancellationToken ct)
        {
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Tunnel = new ConnectTunnel(binding, auth, http, SendAsync, _lifetime.Token);
        }

        public async Task StartAsync(string id, string settingsDirectory, string logDirectory, ConnectSignal connect,
            Func<ConnectSignal, Task> onMessage, CancellationToken ct) =>
            _transport = await ConnectTransportProcess.StartAsync(id, settingsDirectory, logDirectory, connect, onMessage, _lifetime.Token);

        public Task SendAsync(ConnectSignal message, CancellationToken ct) =>
            (_transport ?? throw new InvalidOperationException("Connect transport is not ready.")).SendAsync(message, ct);

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            await Tunnel.DisposeAsync();
            if (_transport is not null) await _transport.DisposeAsync();
            _lifetime.Dispose();
        }
    }
}

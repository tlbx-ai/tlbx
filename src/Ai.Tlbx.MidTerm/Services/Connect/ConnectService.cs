using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Security.Cryptography;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Services.Hosting;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services.Connect;

/// <summary>Opt-in outbound device registration and peer rendezvous. Signaling never carries workstation data.</summary>
public sealed class ConnectService : BackgroundService
{
    private const string SecretKey = "connect.keys.v1";
    private readonly SettingsService _settings;
    private readonly AuthService _auth;
    private readonly ServerBindingInfo _binding;
    private readonly BrowserPreviewOriginService _previewOrigin;
    private readonly BrowserPreviewRegistry _previewRegistry;
    private readonly HttpClientHandler _registrationHandler = new() { AllowAutoRedirect = false };
    private readonly HttpClientHandler _loopbackHandler = ConnectTunnel.CreateLoopbackHandler();
    private readonly HttpClient _registrationHttp;
    private readonly HttpClient _loopbackHttp;
    private readonly Lock _gate = new();
    private readonly SemaphoreSlim _changed = new(0, 1);
    private readonly SemaphoreSlim _provisionGate = new(1, 1);
    private readonly SemaphoreSlim _signalWriter = new(1, 1);
    private readonly ConcurrentDictionary<string, ConnectionOwner> _connections = new(StringComparer.Ordinal);
    private ConnectDeviceConfiguration? _configuration;
    // ExecuteAsync owns and disposes each generation source. Control endpoints only request cancellation.
    private Action? _cancelGeneration;
    private TaskCompletionSource? _generationCompletion;
    private string? _deviceToken;
    private bool _online;
    private bool _revoked;
    private string? _lastError;

    [SuppressMessage("Usage", "IDISP014:Use a single instance of HttpClient",
        Justification = "This service is a process singleton. Its two lifetime clients isolate public TLS validation from pinned local requests.")]
    public ConnectService(SettingsService settings, AuthService auth, ServerBindingInfo binding,
        BrowserPreviewOriginService previewOrigin, BrowserPreviewRegistry previewRegistry)
    {
        _settings = settings;
        _auth = auth;
        _binding = binding;
        _previewOrigin = previewOrigin;
        _previewRegistry = previewRegistry;
        _registrationHttp = new HttpClient(_registrationHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(20) };
        _loopbackHttp = new HttpClient(_loopbackHandler, disposeHandler: false) { Timeout = TimeSpan.FromSeconds(60) };
        var stored = settings.SecretStorage.GetSecret(SecretKey);
        if (stored is null) return;
        try
        {
            var configuration = JsonSerializer.Deserialize(stored, ConnectJsonContext.Default.ConnectDeviceConfiguration);
            if (configuration is null || string.IsNullOrWhiteSpace(configuration.HostPrivateKey)
                || string.IsNullOrWhiteSpace(configuration.DeviceId)) throw new InvalidDataException();
            _ = ConnectRequestPolicy.NormalizeServerUrl(configuration.ServerUrl);
            using var host = ConnectKeyProtocol.PrivateKey(configuration.HostPrivateKey);
            using var access = ConnectKeyProtocol.PublicKey(configuration.AccessPublicKey);
            var publicBytes = host.ExportSubjectPublicKeyInfo();
            if (ConnectKeyProtocol.Encode(publicBytes) != configuration.HostPublicKey
                || Convert.ToHexStringLower(SHA256.HashData(publicBytes))[..32] != configuration.DeviceId)
                throw new InvalidDataException();
            _configuration = configuration;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidDataException or CryptographicException or FormatException)
        {
            _lastError = "The saved Connect keys are invalid. Generate new keys locally.";
        }
    }

    public ConnectStatus GetStatus()
    {
        lock (_gate)
        {
            return new ConnectStatus(_configuration is not null, _online, _configuration?.ServerUrl,
                _configuration?.Name, _configuration?.DeviceId, _lastError, _connections.Count);
        }
    }

    public async Task<ConnectGenerateResponse> GenerateAsync(ConnectGenerateRequest request, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(_settings.Load().PasswordHash))
            throw new InvalidOperationException("Set a local tlbx password before enabling Connect.");
        var origin = ConnectRequestPolicy.NormalizeServerUrl(request.ServerUrl);
        var name = string.IsNullOrWhiteSpace(request.Name) ? Environment.MachineName : request.Name.Trim();
        if (name.Length is < 1 or > 100 || name.Any(char.IsControl)) throw new ArgumentException("Use a device name of 1-100 characters.");
        await _provisionGate.WaitAsync(ct);
        try
        {
            var (configuration, downloads) = ConnectKeyProtocol.Generate(origin.GetLeftPart(UriPartial.Authority), name);
            ct.ThrowIfCancellationRequested();
            var previousRemoved = await DisableCurrentAsync();
            lock (_gate)
            {
                _settings.SecretStorage.SetSecret(SecretKey, JsonSerializer.Serialize(configuration, ConnectJsonContext.Default.ConnectDeviceConfiguration));
                _settings.SecretStorage.DeleteSecret("connect.device.v1");
                _configuration = configuration;
                _revoked = false;
                _lastError = previousRemoved
                    ? "Import the public registration file in Connect, then use the private access file in your browser."
                    : "New keys saved. Import both files in Connect. Remove the previous offline computer entry in Connect if it remains.";
            }
            Wake();
            return downloads;
        }
        finally { _provisionGate.Release(); }
    }

    public async Task DisconnectAsync(CancellationToken ct)
    {
        await _provisionGate.WaitAsync(ct);
        try
        {
            var cloudRemoved = await DisableCurrentAsync();
            lock (_gate)
            {
                _lastError = cloudRemoved ? "Connect keys removed from this computer."
                    : "Connect is disabled locally. Remove this computer's offline entry in Connect if it remains.";
                _revoked = false;
            }
        }
        finally { _provisionGate.Release(); }
        Wake();
    }

    private async Task<bool> DisableCurrentAsync()
    {
        ConnectDeviceConfiguration? previous;
        string? token;
        Task? finished;
        lock (_gate)
        {
            previous = _configuration;
            token = _deviceToken;
            _configuration = null;
            _online = false;
            finished = _generationCompletion?.Task;
            _cancelGeneration?.Invoke();
        }
        // A cancelled generation cannot create a replacement tunnel after this
        // completes. Local disable is independent of cloud availability.
        if (finished is not null) await finished;
        foreach (var id in _connections.Keys) await CloseConnectionAsync(id);
        _settings.SecretStorage.DeleteSecret(SecretKey);
        _settings.SecretStorage.DeleteSecret("connect.device.v1");
        if (previous is null) return true;
        if (token is null) return false;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(new Uri(previous.ServerUrl), "/api/keys/revoke"));
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        try
        {
            using var response = await _registrationHttp.SendAsync(request, deadline.Token);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or OperationCanceledException) { return false; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            ConnectDeviceConfiguration? configuration;
            bool revoked;
            using var generation = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                _cancelGeneration = generation.Cancel;
                _generationCompletion = completion;
                configuration = _configuration;
                revoked = _revoked;
            }
            try
            {
                if (configuration is not null && !revoked)
                    await RunSignalingAsync(configuration, generation.Token);
                else await _changed.WaitAsync(generation.Token);
            }
            catch (OperationCanceledException) when (generation.IsCancellationRequested) { }
            catch (Exception ex) when (ex is HttpRequestException or WebSocketException or IOException or JsonException
                or InvalidOperationException or ArgumentException or OperationCanceledException or CryptographicException or FormatException)
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
                    _deviceToken = null;
                }
                try { foreach (var id in _connections.Keys) await CloseConnectionAsync(id); }
                finally { completion.TrySetResult(); }
            }
        }
    }

    private async Task<string?> AuthenticateHostAsync(ConnectDeviceConfiguration configuration, CancellationToken ct)
    {
        var origin = new Uri(configuration.ServerUrl);
        using var response = await _registrationHttp.PostAsJsonAsync(new Uri(origin, "/api/keys/challenge"),
            new ConnectChallengeRequest(configuration.DeviceId, "host"), ConnectJsonContext.Default.ConnectChallengeRequest, ct);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            lock (_gate) _lastError = "Import this computer's public registration file in Connect. Waiting for registration.";
            await Task.Delay(TimeSpan.FromSeconds(10), ct);
            return null;
        }
        response.EnsureSuccessStatusCode();
        var challenge = await ReadJsonAsync(response, ConnectJsonContext.Default.ConnectChallengeResponse, ct);
        if (string.IsNullOrWhiteSpace(challenge.ChallengeId) || challenge.ChallengeId.Length > 128) throw new InvalidDataException("Invalid Connect authentication challenge.");
        var payload = ConnectKeyProtocol.ValidateHostChallenge(configuration, challenge.Payload);
        using var host = ConnectKeyProtocol.PrivateKey(configuration.HostPrivateKey);
        using var authenticated = await _registrationHttp.PostAsJsonAsync(new Uri(origin, "/api/keys/authenticate"),
            new ConnectAuthenticationRequest(challenge.ChallengeId, ConnectKeyProtocol.Encode(ConnectKeyProtocol.Sign(host, payload))),
            ConnectJsonContext.Default.ConnectAuthenticationRequest, ct);
        authenticated.EnsureSuccessStatusCode();
        var token = await ReadJsonAsync(authenticated, ConnectJsonContext.Default.ConnectTokenResponse, ct);
        if (token.DeviceId != configuration.DeviceId || token.DeviceToken is null || token.DeviceToken.Length != 64 || !token.DeviceToken.All(char.IsAsciiHexDigit))
            throw new InvalidDataException("Invalid Connect host authorization.");
        return token.DeviceToken;
    }

    private async Task RunSignalingAsync(ConnectDeviceConfiguration configuration, CancellationToken ct)
    {
        var deviceToken = await AuthenticateHostAsync(configuration, ct);
        if (deviceToken is null) return;
        lock (_gate) _deviceToken = deviceToken;
        using var socket = new ClientWebSocket();
        socket.Options.CollectHttpResponseDetails = true;
        socket.Options.SetRequestHeader("Authorization", $"Bearer {deviceToken}");
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
            lock (_gate) { _revoked = true; _lastError = "Connect authorization was revoked. Generate new keys locally."; }
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
                    var owner = new ConnectionOwner(_binding, _previewOrigin, _previewRegistry, _auth, _loopbackHttp, new ConnectPeerAuthentication(configuration, id), ct);
                    if (!_connections.TryAdd(id, owner)) { await using (owner.ConfigureAwait(false)) { } break; }
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
                                else if (transportMessage.Type == "open")
                                    await owner.Tunnel.BeginAuthenticationAsync();
                                else if (transportMessage.Type is "answer" or "candidate" or "state")
                                {
                                    if (transportMessage.Type == "answer") owner.Peer.SetAnswer(transportMessage.Sdp ?? "");
                                    transportMessage.ConnectionId = id;
                                    await SendSignalAsync(socket, transportMessage, ct);
                                }
                                else if (transportMessage.Type == "error")
                                {
                                    await SendSignalAsync(socket, new ConnectSignal { Type = "state", ConnectionId = id, State = "failed" }, ct);
                                    _ = CloseConnectionAsync(id);
                                }
                            }, ct);
                        owner.EnforceAuthenticationDeadline(() => CloseConnectionAsync(id));
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
                        if (message.Type == "offer") connection.Peer.SetOffer(message.Sdp ?? "");
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
        _provisionGate.Dispose();
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
        private Task? _authenticationDeadline;
        public ConnectTunnel Tunnel { get; }
        public ConnectPeerAuthentication Peer { get; }

        public ConnectionOwner(ServerBindingInfo binding, BrowserPreviewOriginService previewOrigin, BrowserPreviewRegistry previewRegistry,
            AuthService auth, HttpClient http, ConnectPeerAuthentication peer, CancellationToken ct)
        {
            Peer = peer;
            _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Tunnel = new ConnectTunnel(binding, previewOrigin, previewRegistry, auth, http, SendAsync, peer, _lifetime.Token);
        }

        public async Task StartAsync(string id, string settingsDirectory, string logDirectory, ConnectSignal connect,
            Func<ConnectSignal, Task> onMessage, CancellationToken ct) =>
            _transport = await ConnectTransportProcess.StartAsync(id, settingsDirectory, logDirectory, connect, onMessage, _lifetime.Token);

        public Task SendAsync(ConnectSignal message, CancellationToken ct) =>
            (_transport ?? throw new InvalidOperationException("Connect transport is not ready.")).SendAsync(message, ct);

        public void EnforceAuthenticationDeadline(Func<Task> close) => _authenticationDeadline = EnforceAsync(close);

        private async Task EnforceAsync(Func<Task> close)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(40), _lifetime.Token);
                if (!Peer.Authenticated) _ = close();
            }
            catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        }

        public async ValueTask DisposeAsync()
        {
            _lifetime.Cancel();
            if (_authenticationDeadline is not null) await _authenticationDeadline;
            await Tunnel.DisposeAsync();
            if (_transport is not null) await _transport.DisposeAsync();
            _lifetime.Dispose();
        }
    }
}

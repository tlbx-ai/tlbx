using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;

namespace Ai.Tlbx.MidTerm.Services.Connect;

/// <summary>One in-process native PeerConnection; native callbacks never wait for application work.</summary>
internal sealed class ConnectWebRtcTransport : IAsyncDisposable
{
    internal const int MaximumFrameSize = 65536;
    internal const int MaximumBufferedBytes = 1024 * 1024;
    private static readonly ConcurrentDictionary<nint, ConnectWebRtcTransport> Owners = new();
    private static int _nextToken;
    private static ConnectNativeApi? _loadedApi;
    private readonly ConnectNativeApi _api;
    private readonly ConnectTurnProxy _proxy;
    [SuppressMessage("Usage", "IDISP002:Dispose member", Justification = "ReleaseManagedResources disposes this source after all callbacks and operations have drained.")]
    [SuppressMessage("Usage", "IDISP006:Implement IDisposable", Justification = "This owner requires asynchronous native shutdown and implements IAsyncDisposable.")]
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "DisposeAsync delegates final cleanup to ReleaseManagedResources after workers finish.")]
    private readonly CancellationTokenSource _lifetime;
    private readonly CancellationToken _token;
    private readonly Channel<ConnectSignal> _events = Channel.CreateBounded<ConnectSignal>(32);
    private readonly Channel<byte[]> _frames = Channel.CreateBounded<byte[]>(32);
    [SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "ReleaseManagedResources disposes these gates after pending operations and callbacks have drained.")]
    private readonly SemaphoreSlim _sender = new(1, 1), _signals = new(1, 1), _bufferedLow = new(0, 1);
    private readonly List<ConnectSignal> _pending = [];
    private readonly Func<ConnectSignal, Task> _onSignal;
    private readonly Func<ReadOnlyMemory<byte>, Task> _onFrame;
    private readonly Func<Task> _onOpen;
    private readonly Action _onFailure;
    private readonly nint _ownerToken;
    private readonly Task _eventWorker, _receiveWorker;
    private readonly TaskCompletionSource _operationsDrained = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Lock _nativeGate = new();
    private int _peer = -1, _channel = -1, _closed, _failed, _operations, _openedOnce;
    private bool _offered;
    private Task? _disposeTask;

    private ConnectWebRtcTransport(ConnectNativeApi api, ConnectTurnProxy proxy, Func<ConnectSignal, Task> onSignal,
        Func<ReadOnlyMemory<byte>, Task> onFrame, Func<Task> onOpen, Action onFailure, CancellationToken ct)
    {
        _api = api; _loadedApi = api; _proxy = proxy;
        _onSignal = onSignal; _onFrame = onFrame; _onOpen = onOpen; _onFailure = onFailure;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct); _token = _lifetime.Token;
        do { _ownerToken = Interlocked.Increment(ref _nextToken); } while (_ownerToken == 0 || !Owners.TryAdd(_ownerToken, this));
        _eventWorker = RunEventsAsync(); _receiveWorker = RunFramesAsync();
    }

    public static async Task<ConnectWebRtcTransport> CreateAsync(string settingsDirectory, ConnectSignal configuration,
        Func<ConnectSignal, Task> onSignal, Func<ReadOnlyMemory<byte>, Task> onFrame, Func<Task> onOpen,
        Action onFailure, CancellationToken ct)
    {
        var servers = configuration.IceServers ?? [];
        _ = NativeServerUrls(servers); // Validate before starting proxy DNS/network work.
        var proxy = await ConnectTurnProxy.CreateAsync(servers, ct);
        ConnectWebRtcTransport? transport = null;
        try
        {
            transport = new ConnectWebRtcTransport(ConnectNativeLoader.GetApi(settingsDirectory), proxy, onSignal, onFrame, onOpen, onFailure, ct);
            ct.ThrowIfCancellationRequested();
            var peer = transport._api.Create(NativeServerUrls(proxy.NativeServers, expanded: true), proxy.ProxyUrl,
                configuration.RelayOnly == true, transport._ownerToken);
            lock (transport._nativeGate)
            {
                if (transport._closed != 0) { transport._api.DeletePeer(peer); throw new IOException("Connect closed during initialization."); }
                transport._peer = peer;
            }
            return transport;
        }
        catch
        {
            if (transport is not null) await transport.DisposeAsync();
            else await proxy.DisposeAsync();
            throw;
        }
    }

    public async Task ApplySignalAsync(ConnectSignal signal, CancellationToken ct)
    {
        EnterOperation();
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(ct, _token);
        var entered = false;
        try
        {
            await _signals.WaitAsync(operation.Token); entered = true;
            ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
            if (signal.Type == "offer")
            {
                if (_offered || string.IsNullOrEmpty(signal.Sdp) || signal.Sdp.Contains('\0', StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(signal.Sdp) > 65536)
                    throw new InvalidDataException("Exactly one bounded Connect offer is required.");
                _offered = true;
                _api.ApplyOffer(_peer, signal.Sdp);
                foreach (var candidate in _pending) AddCandidate(candidate);
                _pending.Clear();
            }
            else if (signal.Type == "candidate")
            {
                ValidateCandidate(signal);
                if (_offered) AddCandidate(signal);
                else
                {
                    if (_pending.Count >= 128) throw new InvalidDataException("Too many Connect candidates before the offer.");
                    _pending.Add(signal);
                }
            }
            else throw new InvalidDataException("Unsupported Connect transport signal.");
        }
        catch (Exception) when (!_token.IsCancellationRequested) { Fail(); throw; }
        finally { if (entered) _signals.Release(); ExitOperation(); }
    }

    public async Task SendAsync(ReadOnlyMemory<byte> frame, CancellationToken ct)
    {
        if (frame.Length is 0 or > MaximumFrameSize) throw new InvalidDataException("Invalid Connect binary frame size.");
        EnterOperation();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct, _token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var entered = false;
        try
        {
            await _sender.WaitAsync(deadline.Token); entered = true;
            while (true)
            {
                ObjectDisposedException.ThrowIf(Volatile.Read(ref _closed) != 0, this);
                var channel = Volatile.Read(ref _channel);
                if (channel < 0 || !_api.IsOpen(channel)) throw new IOException("The Connect data channel is not open.");
                if (_api.BufferedAmount(channel) <= MaximumBufferedBytes - frame.Length)
                {
                    _api.Send(channel, frame.Span);
                    return;
                }
                await _bufferedLow.WaitAsync(deadline.Token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && !_token.IsCancellationRequested)
        { Fail(); throw new IOException("Connect data-channel backpressure exceeded its deadline."); }
        catch (IOException) { Fail(); throw; }
        finally { if (entered) _sender.Release(); ExitOperation(); }
    }

    private void AddCandidate(ConnectSignal candidate)
    {
        ValidateCandidate(candidate);
        if (!string.IsNullOrEmpty(candidate.Candidate)) _api.AddCandidate(_peer, candidate.Candidate, candidate.SdpMid);
    }
    private static void ValidateCandidate(ConnectSignal signal)
    {
        if (signal.Candidate is null || Encoding.UTF8.GetByteCount(signal.Candidate) > 8192 || signal.Candidate.Any(x => x is '\0' or '\r' or '\n')
            || signal.SdpMid?.Length > 256 || signal.SdpMid?.Contains('\0', StringComparison.Ordinal) == true || signal.SdpMLineIndex is < 0 or > 65535)
            throw new InvalidDataException("Invalid Connect ICE candidate.");
    }

    internal static string[] NativeServerUrls(ConnectIceServer[] servers, bool expanded = false)
    {
        if (servers.Length > 8) throw new InvalidDataException("Too many Connect ICE servers.");
        var urls = new List<string>();
        foreach (var server in servers)
        {
            if (server.Urls is not { Length: > 0 } || server.Urls.Length > (expanded ? 64 : 8)
                || server.Username?.Length > 512 || server.Credential?.Length > 1024
                || server.Username?.Any(char.IsControl) == true || server.Credential?.Any(char.IsControl) == true)
                throw new InvalidDataException("Invalid Connect ICE configuration.");
            foreach (var url in server.Urls)
            {
                if (string.IsNullOrWhiteSpace(url) || url.Length > 2048 || url.Any(char.IsControl)) throw new InvalidDataException("Invalid Connect ICE URL.");
                var colon = url.IndexOf(':', StringComparison.Ordinal);
                var scheme = colon > 0 ? url[..colon].ToLowerInvariant() : "";
                var address = colon > 0 ? url[(colon + 1)..] : "";
                if (address.StartsWith("//", StringComparison.Ordinal)) address = address[2..];
                if (scheme is not ("stun" or "turn" or "turns") || address.Length == 0 || address.Contains('@', StringComparison.Ordinal)) throw new InvalidDataException("Unsupported Connect ICE URL.");
                var credentials = scheme != "stun" && (server.Username is not null || server.Credential is not null)
                    ? Uri.EscapeDataString(server.Username ?? "") + ":" + Uri.EscapeDataString(server.Credential ?? "") + "@" : "";
                urls.Add(scheme + ":" + credentials + address);
            }
        }
        return urls.ToArray();
    }

    private async Task RunEventsAsync()
    {
        try
        {
            await foreach (var signal in _events.Reader.ReadAllAsync(_token))
            {
                if (signal.Type == "open") await _onOpen();
                else await _onSignal(signal);
            }
        }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception) { Fail(); }
    }
    private async Task RunFramesAsync()
    {
        try { await foreach (var frame in _frames.Reader.ReadAllAsync(_token)) await _onFrame(frame); }
        catch (OperationCanceledException) when (_token.IsCancellationRequested) { }
        catch (Exception) { Fail(); }
    }
    private void Emit(ConnectSignal signal)
    {
        if (Volatile.Read(ref _closed) == 0 && !_events.Writer.TryWrite(signal)) Fail();
    }
    private void Fail()
    {
        if (Volatile.Read(ref _closed) != 0 || Interlocked.Exchange(ref _failed, 1) != 0) return;
        // Never close/delete native objects synchronously on their own callback stack.
        _ = Task.Run(async () =>
        {
            try { _onFailure(); } catch (Exception) { /* Closing this peer is best effort during shutdown. */ }
            await DisposeAsync();
        }, CancellationToken.None);
    }

    private static ConnectWebRtcTransport? Owner(nint token) => Owners.TryGetValue(token, out var owner) && Volatile.Read(ref owner._closed) == 0 ? owner : null;
    private static unsafe string Text(byte* value, int maximum)
    {
        if (value is null) return "";
        var length = 0;
        while (length <= maximum && value[length] != 0) length++;
        if (length > maximum) throw new InvalidDataException("Native WebRTC callback exceeds its limit.");
        return Encoding.UTF8.GetString(new ReadOnlySpan<byte>(value, length));
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe void NativeDescription(int peer, byte* sdp, byte* type, nint token)
    {
        var owner = Owner(token); if (owner is null) return;
        try
        {
            if (Text(type, 16) != "answer") throw new InvalidDataException("Unexpected local Connect description.");
            owner.Emit(new ConnectSignal { Type = "answer", Sdp = Text(sdp, 65536) });
        }
        catch (Exception) { owner.Fail(); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe void NativeCandidate(int peer, byte* candidate, byte* mid, nint token)
    {
        var owner = Owner(token); if (owner is null) return;
        try { owner.Emit(new ConnectSignal { Type = "candidate", Candidate = Text(candidate, 8192), SdpMid = Text(mid, 256) }); }
        catch (Exception) { owner.Fail(); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void NativeState(int peer, int state, nint token)
    {
        var owner = Owner(token); if (owner is null) return;
        try
        {
            var value = state switch { 0 => "new", 1 => "connecting", 2 => "connected", 3 => "disconnected", 4 => "failed", 5 => "closed", _ => null };
            if (value is null) { owner.Fail(); return; }
            owner.Emit(new ConnectSignal { Type = "state", State = value });
            if (state is 4 or 5) owner.Fail();
        }
        catch (Exception) { owner.Fail(); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void NativeGathering(int peer, int state, nint token)
    {
        var owner = Owner(token); if (owner is null) return;
        try { if (state == 2) owner.Emit(new ConnectSignal { Type = "candidate", Candidate = "" }); }
        catch (Exception) { owner.Fail(); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void NativeChannel(int peer, int channel, nint token)
    {
        var owner = Owner(token);
        try
        {
            if (owner is null) { _loadedApi?.DeleteChannel(channel); return; }
            lock (owner._nativeGate)
            {
                if (Volatile.Read(ref owner._closed) != 0 || owner._channel >= 0 || !owner._api.ReliableTlbxChannel(channel))
                { owner._api.DeleteChannel(channel); owner.Fail(); return; }
                owner._channel = channel;
                owner._api.AttachChannel(channel, token);
                if (owner._api.IsOpen(channel)) owner.MarkOpened();
            }
        }
        catch (Exception) { owner?.Fail(); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void NativeOpen(int id, nint token)
    {
        var owner = Owner(token); if (owner is null) return;
        try { owner.MarkOpened(); } catch (Exception) { owner.Fail(); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe void NativeMessage(int id, byte* message, int size, nint token)
    {
        var owner = Owner(token); if (owner is null) return;
        try
        {
            if (size is <= 0 or > MaximumFrameSize || message is null) { owner.Fail(); return; }
            // Native owns the callback buffer only until this callback returns.
            if (!owner._frames.Writer.TryWrite(new ReadOnlySpan<byte>(message, size).ToArray())) owner.Fail();
        }
        catch (Exception) { owner.Fail(); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void NativeClosed(int id, nint token) { try { Owner(token)?.Fail(); } catch (Exception) { } }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static unsafe void NativeError(int id, byte* error, nint token) { try { Owner(token)?.Fail(); } catch (Exception) { } }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    internal static void NativeBufferedLow(int id, nint token)
    {
        try { Owner(token)?._bufferedLow.Release(); } catch (Exception) { }
    }

    public ValueTask DisposeAsync()
    {
        lock (_nativeGate) return new ValueTask(_disposeTask ??= Task.Run(DisposeCoreAsync, CancellationToken.None));
    }
    private async Task DisposeCoreAsync()
    {
        lock (_nativeGate)
        {
            _closed = 1;
            if (_operations == 0) _operationsDrained.TrySetResult();
        }
        _lifetime.Cancel();
        _events.Writer.TryComplete(); _frames.Writer.TryComplete();
        await _operationsDrained.Task;
        if (_channel >= 0) _api.DeleteChannel(_channel);
        if (_peer >= 0) _api.DeletePeer(_peer);
        Owners.TryRemove(_ownerToken, out _);
        await _proxy.DisposeAsync();
        var workers = Task.WhenAll(_eventWorker, _receiveWorker);
        try { await workers.WaitAsync(TimeSpan.FromSeconds(3), CancellationToken.None); }
        catch (TimeoutException) { /* A shared signaling send may finish after this peer closes. */ }
        if (workers.IsCompleted) ReleaseManagedResources();
        else _ = ReleaseAfterWorkersAsync(workers);
    }
    private async Task ReleaseAfterWorkersAsync(Task workers)
    {
        try { await workers; } finally { ReleaseManagedResources(); }
    }
    private void ReleaseManagedResources()
    {
        _lifetime.Dispose(); _sender.Dispose(); _signals.Dispose(); _bufferedLow.Dispose();
    }
    private void EnterOperation()
    {
        lock (_nativeGate) { ObjectDisposedException.ThrowIf(_closed != 0, this); _operations++; }
    }
    private void ExitOperation()
    {
        lock (_nativeGate) { if (--_operations == 0 && _closed != 0) _operationsDrained.TrySetResult(); }
    }
    private void MarkOpened()
    {
        if (Interlocked.Exchange(ref _openedOnce, 1) == 0) Emit(new ConnectSignal { Type = "open" });
    }
}

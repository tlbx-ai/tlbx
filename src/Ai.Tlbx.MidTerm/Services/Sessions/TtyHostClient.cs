using System.Buffers;
using System.Globalization;
using System.Net.Sockets;
#if WINDOWS
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
#endif
using Ai.Tlbx.MidTerm.Common.Ipc;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Common.Protocol;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

/// <summary>
/// Robust IPC client for a single mmttyhost process.
/// Auto-reconnects on failure, retries operations, buffers during disconnects.
/// </summary>
public sealed class TtyHostClient : IAsyncDisposable
{
#if WINDOWS
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool PeekNamedPipe(
        SafePipeHandle hNamedPipe,
        IntPtr lpBuffer,
        uint nBufferSize,
        IntPtr lpBytesRead,
        out uint lpTotalBytesAvail,
        IntPtr lpBytesLeftThisMessage);
#endif
    private readonly string _sessionId;
    private readonly int _hostPid;
    private readonly string _endpoint;
    private readonly string? _instanceId;
    private readonly string? _ownerToken;
    private readonly bool _useLegacyEndpoint;
    private readonly object _streamLock = new();
    private readonly object _responseLock = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly SemaphoreSlim _requestLock = new(1, 1);

#if WINDOWS
    private NamedPipeClientStream? _pipe;
#else
    private Socket? _socket;
    private NetworkStream? _networkStream;
#endif
    private Stream? _stream;
    private CancellationTokenSource? _cts;
    private Task? _readTask;
    private Task? _heartbeatTask;
    private Task? _reconnectTask;
    private CancellationTokenSource? _readCancellation; // Allows heartbeat to unblock reads instantly
    private CancellationTokenSource? _readTimeoutCts;
    private CancellationTokenRegistration _timeoutReg;
    private CancellationTokenRegistration _externalReg;
    private static readonly Action<object?> s_cancelCallback = static state =>
        ((CancellationTokenSource?)state)?.Cancel();
    private bool _disposed;
    private bool _intentionalDisconnect;
    private int _reconnectAttempts;
    private int _successfulReconnectCount;
    private int _consecutiveRequestTimeouts;
    private int _consecutiveReadTimeouts;
    private DateTime _lastDataReceived = DateTime.UtcNow;

    private TaskCompletionSource<(TtyHostMessageType type, byte[] payload)>? _pendingResponse;

    private const int MaxReconnectAttempts = 10; // Give up after 10 attempts (~2 minutes with exponential backoff)
    private const int InitialReconnectDelayMs = 100;
    private const int MaxReconnectDelayMs = 30000; // Cap at 30s between attempts
    private const int HeartbeatIntervalMs = 5000; // Check connection every 5 seconds
    private const int ReadTimeoutMs = 10000; // 10 seconds - shorter now that we have heartbeat

    public string SessionId => _sessionId;
    public int HostPid => _hostPid;
    public bool IsConnected
    {
        get
        {
#if WINDOWS
            return _pipe?.IsConnected ?? false;
#else
            return _socket?.Connected ?? false;
#endif
        }
    }

    public event Action<string, ulong, int, int, ReadOnlyMemory<byte>>? OnOutput;
    public event Action<string>? OnStateChanged;
    public event Action<string>? OnDisconnected;
    public event Action<string>? OnReconnected;
    public event Action<string, ForegroundChangePayload>? OnForegroundChanged;
    public event Action<string, TtyHostDataLossPayload>? OnDataLoss;
    public event Action<string, TtyHostInputTraceReport>? OnInputTrace;

    public TtyHostClient(string sessionId, int hostPid, string? instanceId = null, string? ownerToken = null, bool useLegacyEndpoint = false)
    {
        _sessionId = sessionId;
        _hostPid = hostPid;
        _instanceId = instanceId;
        _ownerToken = ownerToken;
        _useLegacyEndpoint = useLegacyEndpoint || string.IsNullOrWhiteSpace(instanceId) || string.IsNullOrWhiteSpace(ownerToken);
        _endpoint = _useLegacyEndpoint
            ? IpcEndpoint.GetLegacySessionEndpoint(sessionId, hostPid)
            : IpcEndpoint.GetSessionEndpoint(instanceId!, sessionId, hostPid);
    }

    public async Task<bool> ConnectAsync(int timeoutMs = 5000, int maxAttempts = 3, CancellationToken ct = default)
    {
        if (_disposed) return false;

        var attempts = Math.Max(1, maxAttempts);
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            try
            {
#if WINDOWS
                lock (_streamLock)
                {
                    _pipe?.Dispose();
                    _pipe = new NamedPipeClientStream(".", _endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
                }

                await _pipe.ConnectAsync(timeoutMs, ct).ConfigureAwait(false);
                _stream = _pipe;
#else
                lock (_streamLock)
                {
                    _networkStream?.Dispose();
                    _socket?.Dispose();
                    _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                }

                using var timeoutCts = new CancellationTokenSource(timeoutMs);
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                await _socket.ConnectAsync(new UnixDomainSocketEndPoint(_endpoint), linkedCts.Token).ConfigureAwait(false);
                _networkStream = new NetworkStream(_socket, ownsSocket: false);
                _stream = _networkStream;
#endif
                if (!await PerformAttachHandshakeAsync(ct).ConfigureAwait(false))
                {
                    DisconnectCurrentStream();
                    return false;
                }

                _reconnectAttempts = 0;
                return true;
            }
            catch (TimeoutException)
            {
            }
            catch (OperationCanceledException)
            {
                return false;
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            catch (Exception ex)
            {
                Log.Verbose(() => string.Create(CultureInfo.InvariantCulture, $"[IPC] {_sessionId}: Connect attempt {attempt} failed: {ex.GetType().Name}: {ex.Message}"));
            }

            if (attempt + 1 < attempts)
            {
                await Task.Delay(200 * (attempt + 1), ct).ConfigureAwait(false);
            }
        }

        return false;
    }

    public void StartReadLoop()
    {
        if (_readTask is not null) return;
        _cts?.Dispose();
        _cts = new CancellationTokenSource();
        _readTask = ReadLoopWithReconnectAsync(_cts.Token);
        _heartbeatTask = HeartbeatLoopAsync(_cts.Token);
    }

    public async Task<SessionInfo?> GetInfoAsync(CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (!IsConnected)
            {
                await Task.Delay(100, ct).ConfigureAwait(false);
                continue;
            }

            try
            {
                if (_readTask is not null)
                {
                    var request = TtyHostProtocol.CreateInfoRequest();
                    var response = await SendRequestAsync(request, TtyHostMessageType.Info, ct).ConfigureAwait(false);
                    if (response is null) continue;
                    return TtyHostProtocol.ParseInfo(response);
                }

                var requestBytes = TtyHostProtocol.CreateInfoRequest();
                await WriteWithLockAsync(requestBytes, ct).ConfigureAwait(false);

                // During discovery, mthost may be flooding output - skip those messages
                // and wait for the actual Info response
                const int maxSkip = 1000;
                for (var skip = 0; skip < maxSkip; skip++)
                {
                    var directResponse = await ReadMessageAsync(ct).ConfigureAwait(false);
                    if (directResponse is null)
                    {
                        break;
                    }

                    var (type, payload) = directResponse.Value;
                    if (type == TtyHostMessageType.Info)
                    {
                        return TtyHostProtocol.ParseInfo(payload.Span);
                    }
                }

                continue;
            }
            catch
            {
            }
        }

        return null;
    }

    public async Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_disposed) return;

        var frameSize = TtyHostProtocol.HeaderSize + data.Length;
        var buffer = ArrayPool<byte>.Shared.Rent(frameSize);
        try
        {
            if (data.Length < 20)
            {
                Log.Verbose(() => $"[IPC-SEND] {_sessionId}: {BitConverter.ToString(data.ToArray())}");
            }
            TtyHostProtocol.WriteInputFrameInto(data.Span, buffer.AsSpan(0, frameSize));
            await WriteWithLockAsync(buffer, frameSize, ct).ConfigureAwait(false);
        }
        catch
        {
            TriggerReconnect();
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public async Task<TtyHostInputWriteTiming?> SendInputWithTraceAsync(
        ReadOnlyMemory<byte> data,
        uint traceId,
        CancellationToken ct = default)
    {
        if (_disposed) return null;

        var traceMarkerFrameSize = TtyHostProtocol.HeaderSize + TtyHostProtocol.InputTraceMarkerPayloadSize;
        var inputFrameSize = TtyHostProtocol.HeaderSize + data.Length;
        var frameSize = traceMarkerFrameSize + inputFrameSize;
        var buffer = ArrayPool<byte>.Shared.Rent(frameSize);
        try
        {
            if (data.Length < 20)
            {
                Log.Verbose(() => $"[IPC-SEND] {_sessionId}: trace={traceId} {BitConverter.ToString(data.ToArray())}");
            }

            TtyHostProtocol.WriteInputTraceMarkerFrameInto(traceId, buffer.AsSpan(0, traceMarkerFrameSize));
            TtyHostProtocol.WriteInputFrameInto(data.Span, buffer.AsSpan(traceMarkerFrameSize, inputFrameSize));

            var writeStartAtMs = Environment.TickCount64;
            await WriteWithLockAsync(buffer, frameSize, ct).ConfigureAwait(false);
            return new TtyHostInputWriteTiming(writeStartAtMs, Environment.TickCount64);
        }
        catch
        {
            TriggerReconnect();
            return null;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private async Task WriteWithLockAsync(byte[] data, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteAsync(data.AsMemory(), ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private async Task WriteWithLockAsync(byte[] data, int length, CancellationToken ct)
    {
        await _writeLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await WriteAsync(data.AsMemory(0, length), ct).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    public async Task<bool> ResizeAsync(int cols, int rows, CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!IsConnected) return false;

            try
            {
                var msg = TtyHostProtocol.CreateResizeMessage(cols, rows);
                var response = await SendRequestAsync(msg, TtyHostMessageType.ResizeAck, ct).ConfigureAwait(false);
                return response is not null;
            }
            catch
            {
                TriggerReconnect();
            }
        }

        return false;
    }

    public int ReconnectCount => _successfulReconnectCount;

    public async Task<TtyHostBufferSnapshot?> GetBufferAsync(
        int? maxBytes = null,
        TerminalReplayReason reason = TerminalReplayReason.Manual,
        CancellationToken ct = default)
    {
        for (var attempt = 0; attempt < 2; attempt++)
        {
            if (!IsConnected)
            {
                return null;
            }

            try
            {
                var msg = TtyHostProtocol.CreateGetBuffer(maxBytes, reason);
                var response = await SendRequestAsync(msg, TtyHostMessageType.Buffer, ct).ConfigureAwait(false);
                return response is null ? null : TtyHostProtocol.ParseBuffer(response);
            }
            catch
            {
            }
        }

        return null;
    }

    public async Task<bool> SetNameAsync(string? name, CancellationToken ct = default)
    {
        if (!IsConnected) return false;

        try
        {
            var msg = TtyHostProtocol.CreateSetName(name);
            var response = await SendRequestAsync(msg, TtyHostMessageType.SetNameAck, ct).ConfigureAwait(false);
            return response is not null;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> SetOrderAsync(byte order, CancellationToken ct = default)
    {
        if (!IsConnected) return false;

        try
        {
            var msg = TtyHostProtocol.CreateSetOrder(order);
            var response = await SendRequestAsync(msg, TtyHostMessageType.SetOrderAck, ct).ConfigureAwait(false);
            return response is not null;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> SetMetadataAsync(TtyHostSessionMetadata metadata, CancellationToken ct = default)
    {
        if (!IsConnected) return false;

        try
        {
            var msg = TtyHostProtocol.CreateSetMetadata(metadata);
            var response = await SendRequestAsync(msg, TtyHostMessageType.SetMetadataAck, ct).ConfigureAwait(false);
            return response is not null;
        }
        catch
        {
            return false;
        }
    }

    public async Task<bool> SetClipboardImageAsync(
        string filePath,
        string? mimeType,
        CancellationToken ct = default)
    {
        if (!IsConnected) return false;

        try
        {
            var msg = TtyHostProtocol.CreateSetClipboardImage(filePath, mimeType);
            var response = await SendRequestAsync(msg, TtyHostMessageType.SetClipboardImageAck, ct).ConfigureAwait(false);
            if (response is null)
            {
                return false;
            }

            var parsed = TtyHostProtocol.ParseSetClipboardImageAck(response);
            return parsed?.Success == true;
        }
        catch
        {
            return false;
        }
    }

    public async Task<byte[]?> PingAsync(byte[] pingData, CancellationToken ct = default)
    {
        if (!IsConnected) return null;

        try
        {
            var msg = TtyHostProtocol.CreatePing(pingData);
            return await SendRequestAsync(msg, TtyHostMessageType.Pong, ct).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
    }

    public async Task<bool> CloseAsync(CancellationToken ct = default)
    {
        _intentionalDisconnect = true;

        if (!IsConnected) return true;

        try
        {
            var msg = TtyHostProtocol.CreateClose();
            var response = await SendRequestAsync(msg, TtyHostMessageType.CloseAck, ct).ConfigureAwait(false);
            return response is not null;
        }
        catch (Exception ex)
        {
            Log.Exception(ex, $"TtyHostClient.CloseAsync({_sessionId})");
            return true;
        }
    }

    private async Task<byte[]?> SendRequestAsync(byte[] request, TtyHostMessageType expectedType, CancellationToken ct)
    {
        await _requestLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var tcs = new TaskCompletionSource<(TtyHostMessageType type, byte[] payload)>();

            lock (_responseLock)
            {
                _pendingResponse = tcs;
            }

            try
            {
                await WriteWithLockAsync(request, ct).ConfigureAwait(false);

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                (TtyHostMessageType type, byte[] payload) response;
                try
                {
                    response = await tcs.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
                {
                    RegisterRequestTimeout();
                    return null;
                }

                _consecutiveRequestTimeouts = 0;
                return response.type == expectedType ? response.payload : null;
            }
            finally
            {
                lock (_responseLock)
                {
                    _pendingResponse = null;
                }
            }
        }
        finally
        {
            _requestLock.Release();
        }
    }

    private async Task WriteAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        var stream = _stream;
        if (stream is null || !IsConnected)
        {
            throw new IOException("Not connected");
        }

        await stream.WriteAsync(data, ct).ConfigureAwait(false);
    }

    private async Task<bool> PerformAttachHandshakeAsync(CancellationToken ct)
    {
        if (_useLegacyEndpoint)
        {
            return true;
        }

        var request = TtyHostProtocol.CreateAttachRequest(new TtyHostAttachRequest
        {
            InstanceId = _instanceId!,
            OwnerToken = _ownerToken!
        });

        if (_readTask is not null)
        {
            var response = await SendRequestAsync(request, TtyHostMessageType.AttachAck, ct).ConfigureAwait(false);
            var parsed = response is null ? null : TtyHostProtocol.ParseAttachAck(response);
            return parsed?.Accepted == true;
        }

        await WriteWithLockAsync(request, ct).ConfigureAwait(false);
        var directResponse = await ReadMessageAsync(ct).ConfigureAwait(false);
        if (directResponse is null || directResponse.Value.type != TtyHostMessageType.AttachAck)
        {
            return false;
        }

        var attach = TtyHostProtocol.ParseAttachAck(directResponse.Value.payload.Span);
        return attach?.Accepted == true;
    }

    private void DisconnectCurrentStream()
    {
        lock (_streamLock)
        {
#if WINDOWS
            _pipe?.Dispose();
            _pipe = null;
#else
            _networkStream?.Dispose();
            _networkStream = null;
            _socket?.Dispose();
            _socket = null;
#endif
            _stream = null;
        }
    }

    private async Task ReadLoopWithReconnectAsync(CancellationToken ct)
    {
        var headerBuffer = new byte[TtyHostProtocol.HeaderSize];
        byte[]? rentedPayload = null;

        try
        {
            while (!ct.IsCancellationRequested && !_disposed)
            {
                try
                {
                    if (!IsConnected)
                    {
                        await Task.Delay(100, ct).ConfigureAwait(false);
                        continue;
                    }

                    var stream = _stream;
                    if (stream is null) continue;

                    // Initialize or reset reusable CTS instances (zero-alloc when TryReset succeeds)
                    if (_readCancellation is null || !_readCancellation.TryReset())
                    {
                        _readCancellation?.Dispose();
                        _readCancellation = new CancellationTokenSource();
                    }
                    if (_readTimeoutCts is null || !_readTimeoutCts.TryReset())
                    {
                        _readTimeoutCts?.Dispose();
                        _readTimeoutCts = new CancellationTokenSource();
                    }
                    _readTimeoutCts.CancelAfter(ReadTimeoutMs);

                    // Manual linking via UnsafeRegister (zero-alloc when tokens don't cancel)
                    _timeoutReg.Dispose();
                    _externalReg.Dispose();
                    _timeoutReg = _readTimeoutCts.Token.UnsafeRegister(s_cancelCallback, _readCancellation);
                    _externalReg = ct.UnsafeRegister(s_cancelCallback, _readCancellation);

                    int bytesRead;
                    try
                    {
                        bytesRead = await stream.ReadAsync(headerBuffer, _readCancellation.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (_readCancellation?.IsCancellationRequested == true && !ct.IsCancellationRequested)
                    {
                        if (HasPendingResponse())
                        {
                            RegisterReadTimeout();
                        }
                        continue;
                    }
                    catch (OperationCanceledException) when (_readTimeoutCts?.IsCancellationRequested == true && !ct.IsCancellationRequested)
                    {
                        if (HasPendingResponse())
                        {
                            RegisterReadTimeout();
                        }
                        continue;
                    }

                    _lastDataReceived = DateTime.UtcNow;
                    _consecutiveReadTimeouts = 0;
                    if (bytesRead == 0)
                    {
                        HandleDisconnect();
                        continue;
                    }

                    while (bytesRead < TtyHostProtocol.HeaderSize)
                    {
                        var more = await stream.ReadAsync(headerBuffer.AsMemory(bytesRead), ct).ConfigureAwait(false);
                        if (more == 0)
                        {
                            HandleDisconnect();
                            break;
                        }
                        bytesRead += more;
                    }

                    if (bytesRead < TtyHostProtocol.HeaderSize) continue;

                    if (!TtyHostProtocol.TryReadHeader(headerBuffer, out var msgType, out var payloadLength))
                    {
                        HandleDisconnect();
                        break;
                    }

                    // Return previous rented buffer before renting new one
                    if (rentedPayload is not null)
                    {
                        ArrayPool<byte>.Shared.Return(rentedPayload);
                        rentedPayload = null;
                    }

                    Memory<byte> payload = Memory<byte>.Empty;
                    if (payloadLength > 0)
                    {
                        rentedPayload = ArrayPool<byte>.Shared.Rent(payloadLength);
                        var totalRead = 0;
                        while (totalRead < payloadLength)
                        {
                            var chunk = await stream.ReadAsync(rentedPayload.AsMemory(totalRead, payloadLength - totalRead), ct).ConfigureAwait(false);
                            if (chunk == 0)
                            {
                                HandleDisconnect();
                                break;
                            }
                            totalRead += chunk;
                        }

                        if (totalRead < payloadLength) continue;
                        payload = rentedPayload.AsMemory(0, payloadLength);
                    }

                    ProcessMessage(msgType, payload);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (IOException)
                {
                    HandleDisconnect();
                }
                catch (SocketException)
                {
                    HandleDisconnect();
                }
                catch (Exception ex)
                {
                    Log.Verbose(() => $"[IPC] {_sessionId}: ReadLoop error: {ex.GetType().Name}: {ex.Message}");
                    HandleDisconnect();
                }
            }
        }
        finally
        {
            if (rentedPayload is not null)
            {
                ArrayPool<byte>.Shared.Return(rentedPayload);
            }
        }
    }

    private void ProcessMessage(TtyHostMessageType msgType, Memory<byte> payload)
    {
        switch (msgType)
        {
            case TtyHostMessageType.Output:
                try
                {
                    var sequenceStart = TtyHostProtocol.ParseOutputSequenceStart(payload.Span);
                    var (cols, rows) = TtyHostProtocol.ParseOutputDimensions(payload.Span);
                    OnOutput?.Invoke(_sessionId, sequenceStart, cols, rows, payload.Slice(12));
                }
                catch (Exception ex)
                {
                    Log.Exception(ex, $"TtyHostClient.OnOutput({_sessionId})");
                }
                break;

            case TtyHostMessageType.StateChange:
                try
                {
                    OnStateChanged?.Invoke(_sessionId);
                }
                catch (Exception ex)
                {
                    Log.Exception(ex, $"TtyHostClient.OnStateChanged({_sessionId})");
                }
                break;

            case TtyHostMessageType.ForegroundChange:
                try
                {
                    var foregroundChange = TtyHostProtocol.ParseForegroundChange(payload.Span);
                    if (foregroundChange is not null)
                    {
                        OnForegroundChanged?.Invoke(_sessionId, foregroundChange);
                    }
                }
                catch (Exception ex)
                {
                    Log.Exception(ex, $"TtyHostClient.OnForegroundChanged({_sessionId})");
                }
                break;

            case TtyHostMessageType.Buffer:
            case TtyHostMessageType.ResizeAck:
            case TtyHostMessageType.SetNameAck:
            case TtyHostMessageType.SetOrderAck:
            case TtyHostMessageType.SetMetadataAck:
            case TtyHostMessageType.SetClipboardImageAck:
            case TtyHostMessageType.CloseAck:
            case TtyHostMessageType.Info:
            case TtyHostMessageType.AttachAck:
            case TtyHostMessageType.Pong:
                lock (_responseLock)
                {
                    _pendingResponse?.TrySetResult((msgType, payload.ToArray()));
                }
                break;

            case TtyHostMessageType.DataLoss:
                try
                {
                    var dataLoss = TtyHostProtocol.ParseDataLoss(payload.Span);
                    if (dataLoss is not null)
                    {
                        OnDataLoss?.Invoke(_sessionId, dataLoss);
                    }
                }
                catch (Exception ex)
                {
                    Log.Exception(ex, $"TtyHostClient.OnDataLoss({_sessionId})");
                }
                break;

            case TtyHostMessageType.InputTrace:
                try
                {
                    var trace = TtyHostProtocol.ParseInputTraceReport(payload.Span);
                    if (trace is not null)
                    {
                        OnInputTrace?.Invoke(_sessionId, trace.Value);
                    }
                }
                catch (Exception ex)
                {
                    Log.Exception(ex, $"TtyHostClient.OnInputTrace({_sessionId})");
                }
                break;

            default:
                break;
        }
    }

    private async Task HeartbeatLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested && !_disposed)
        {
            try
            {
                await Task.Delay(HeartbeatIntervalMs, ct).ConfigureAwait(false);

                if (_disposed || _intentionalDisconnect || !IsConnected) continue;

                // Use PeekNamedPipe on Windows for instant stale detection
#if WINDOWS
                var pipe = _pipe;
                if (pipe is not null && pipe.IsConnected)
                {
                    try
                    {
                        var handle = pipe.SafePipeHandle;
                        if (!PeekNamedPipe(handle, IntPtr.Zero, 0, IntPtr.Zero, out _, IntPtr.Zero))
                        {
                            CancelReadAndReconnect();
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        // Pipe was disposed, will reconnect
                    }
                }
#else
                // On Unix, try a zero-byte write to detect broken socket
                var socket = _socket;
                if (socket is not null && socket.Connected)
                {
                    try
                    {
                        if (socket.Poll(0, SelectMode.SelectError))
                        {
                            CancelReadAndReconnect();
                        }
                    }
                    catch (SocketException)
                    {
                        CancelReadAndReconnect();
                    }
                    catch (ObjectDisposedException)
                    {
                        // Socket was disposed, will reconnect
                    }
                }
#endif
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Verbose(() => $"[IPC] {_sessionId}: Heartbeat error: {ex.GetType().Name}: {ex.Message}");
            }
        }
    }

    private void CancelReadAndReconnect()
    {
        try { _readCancellation?.Cancel(); } catch { }
        HandleDisconnect();
    }

    private void HandleDisconnect()
    {
        if (_disposed || _intentionalDisconnect) return;
        lock (_responseLock)
        {
            _pendingResponse?.TrySetCanceled(_cts?.Token ?? CancellationToken.None);
        }
        OnDisconnected?.Invoke(_sessionId);
        TriggerReconnect();
    }

    private void TriggerReconnect()
    {
        if (_disposed || _intentionalDisconnect) return;
        if (_reconnectTask is not null && !_reconnectTask.IsCompleted) return;

        DisconnectCurrentStream();
        _reconnectTask = ReconnectAsync();
    }

    private async Task ReconnectAsync()
    {
        var ct = _cts?.Token ?? CancellationToken.None;

        while (!_disposed && !_intentionalDisconnect && !ct.IsCancellationRequested && _reconnectAttempts < MaxReconnectAttempts)
        {
            _reconnectAttempts++;
            var delay = Math.Min(InitialReconnectDelayMs * (1 << _reconnectAttempts), MaxReconnectDelayMs);

            try
            {
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }

            if (_disposed || _intentionalDisconnect || ct.IsCancellationRequested) return;

            try
            {
#if WINDOWS
                lock (_streamLock)
                {
                    _pipe?.Dispose();
                    _pipe = new NamedPipeClientStream(".", _endpoint, PipeDirection.InOut, PipeOptions.Asynchronous);
                }

                using var connectCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                connectCts.CancelAfter(2000);
                await _pipe.ConnectAsync(connectCts.Token).ConfigureAwait(false);
                _stream = _pipe;
#else
                lock (_streamLock)
                {
                    _networkStream?.Dispose();
                    _socket?.Dispose();
                    _socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
                }

                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(2000);
                await _socket.ConnectAsync(new UnixDomainSocketEndPoint(_endpoint), timeoutCts.Token).ConfigureAwait(false);
                _networkStream = new NetworkStream(_socket, ownsSocket: false);
                _stream = _networkStream;
#endif
                if (!await PerformAttachHandshakeAsync(ct).ConfigureAwait(false))
                {
                    DisconnectCurrentStream();
                    continue;
                }

                var info = await GetInfoAsync(ct).ConfigureAwait(false);
                if (info is not null)
                {
                    _reconnectAttempts = 0;
                    _successfulReconnectCount++;
                    _consecutiveReadTimeouts = 0;
                    _consecutiveRequestTimeouts = 0;
                    OnReconnected?.Invoke(_sessionId);
                    return;
                }
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch
            {
            }
        }

        if (_reconnectAttempts >= MaxReconnectAttempts)
        {
            OnStateChanged?.Invoke(_sessionId);
        }
    }

    private async Task<(TtyHostMessageType type, Memory<byte> payload)?> ReadMessageAsync(CancellationToken ct)
    {
        var stream = _stream;
        if (stream is null || !IsConnected) return null;

        var headerBuffer = new byte[TtyHostProtocol.HeaderSize];
        var bytesRead = await stream.ReadAsync(headerBuffer, ct).ConfigureAwait(false);
        if (bytesRead == 0) return null;

        while (bytesRead < TtyHostProtocol.HeaderSize)
        {
            var more = await stream.ReadAsync(headerBuffer.AsMemory(bytesRead), ct).ConfigureAwait(false);
            if (more == 0) return null;
            bytesRead += more;
        }

        if (!TtyHostProtocol.TryReadHeader(headerBuffer, out var msgType, out var payloadLength))
        {
            return null;
        }

        if (payloadLength < 0 || payloadLength > TtyHostProtocol.MaxPayloadSize)
        {
            return null;
        }

        var payload = new byte[payloadLength];
        if (payloadLength > 0)
        {
            var totalRead = 0;
            while (totalRead < payloadLength)
            {
                var chunk = await stream.ReadAsync(payload.AsMemory(totalRead), ct).ConfigureAwait(false);
                if (chunk == 0) return null;
                totalRead += chunk;
            }
        }

        return (msgType, payload);
    }

    private bool HasPendingResponse()
    {
        lock (_responseLock)
        {
            return _pendingResponse is not null;
        }
    }

    private void RegisterReadTimeout()
    {
        _consecutiveReadTimeouts++;
        if (_consecutiveReadTimeouts < 3)
        {
            return;
        }

        Log.Warn(() => string.Create(CultureInfo.InvariantCulture, $"[IPC] {_sessionId}: {_consecutiveReadTimeouts} consecutive read timeouts while waiting for a response; reconnecting"));
        _consecutiveReadTimeouts = 0;
        TriggerReconnect();
        try
        {
            _readCancellation?.Cancel();
        }
        catch
        {
        }
    }

    private void RegisterRequestTimeout()
    {
        _consecutiveRequestTimeouts++;
        if (_consecutiveRequestTimeouts < 2)
        {
            return;
        }

        Log.Warn(() => string.Create(CultureInfo.InvariantCulture, $"[IPC] {_sessionId}: {_consecutiveRequestTimeouts} consecutive request timeouts; reconnecting"));
        _consecutiveRequestTimeouts = 0;
        TriggerReconnect();
        try
        {
            _readCancellation?.Cancel();
        }
        catch
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _intentionalDisconnect = true;

        _cts?.Cancel();

        lock (_responseLock)
        {
            _pendingResponse?.TrySetCanceled(_cts?.Token ?? CancellationToken.None);
        }

        if (_readTask is not null)
        {
            try { await _readTask.ConfigureAwait(false); }
            catch (Exception ex) { Log.Exception(ex, $"TtyHostClient.Dispose.ReadTask({_sessionId})"); }
        }

        if (_heartbeatTask is not null)
        {
            try { await _heartbeatTask.ConfigureAwait(false); }
            catch (Exception ex) { Log.Exception(ex, $"TtyHostClient.Dispose.HeartbeatTask({_sessionId})"); }
        }

        if (_reconnectTask is not null)
        {
            try { await _reconnectTask.ConfigureAwait(false); }
            catch (Exception ex) { Log.Exception(ex, $"TtyHostClient.Dispose.ReconnectTask({_sessionId})"); }
        }

        _cts?.Dispose();
        _readCancellation?.Dispose();
        _readTimeoutCts?.Dispose();
        _timeoutReg.Dispose();
        _externalReg.Dispose();
        _writeLock.Dispose();
        _requestLock.Dispose();

        lock (_streamLock)
        {
#if WINDOWS
            _pipe?.Dispose();
#else
            _networkStream?.Dispose();
            _socket?.Dispose();
#endif
        }
    }
}

public readonly record struct TtyHostInputWriteTiming(
    long IpcWriteStartAtMs,
    long IpcWriteDoneAtMs);

using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Channels;
#if WINDOWS
using Microsoft.Win32.SafeHandles;
using System.IO.Pipes;
#else
using System.Net.Sockets;
#endif
using Ai.Tlbx.MidTerm.Common.Ipc;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Common.Process;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Common.Shells;
using Ai.Tlbx.MidTerm.TtyHost.Process;
using Ai.Tlbx.MidTerm.TtyHost.Pty;

namespace Ai.Tlbx.MidTerm.TtyHost;

public static class Program
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
#else
    // SIGPIPE handling via native interop (not available in PosixSignal enum)
    private const int SIGPIPE = 13;
    private static readonly IntPtr SIG_IGN = new(1);

    [DllImport("libc", SetLastError = true)]
    private static extern IntPtr signal(int signum, IntPtr handler);
#endif

    private const int HeartbeatIntervalMs = 5000;
    private const int HandshakeTimeoutMs = 10000;
    private const int MinScrollbackBytes = 64 * 1024;
    private const int MaxScrollbackBytes = 10 * 1024 * 1024;
    private const int MaxIpcQueuedFramesPerClient = 256;

    private static CancellationTokenSource? _shutdownCts;

    public static async Task<int> Main(string[] args)
    {
#if !WINDOWS
        // PTY exec mode - check FIRST before any .NET initialization
        // Usage: mthost --pty-exec <slave-path> <cols> <rows> <shell> [shell-args...]
        // This replaces the process with the shell via execvp() and never returns
        if (args.Length >= 5 && args[0] == "--pty-exec")
        {
            int.TryParse(args[2], out var execCols);
            int.TryParse(args[3], out var execRows);
            return PtyExec.Execute(args[1], execCols, execRows, args[4..]);
        }
#endif

        if (args.Contains("--version") || args.Contains("-v"))
        {
            Console.WriteLine($"mthost {VersionInfo.Version}");
            return 0;
        }

        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintHelp();
            return 0;
        }

        var config = ParseArgs(args);
        if (config is null)
        {
            Console.Error.WriteLine("Missing required arguments. Use --help for usage.");
            return 1;
        }

        var logDirectory = LogPaths.GetLogDirectory(isWindowsService: false);
        Log.Initialize($"mthost-{config.SessionId}", logDirectory, LogSeverity.Exception);

#if !WINDOWS
        // Register Unix signal handlers for graceful shutdown
        PosixSignalRegistration.Create(PosixSignal.SIGTERM, OnSignal);
        PosixSignalRegistration.Create(PosixSignal.SIGINT, OnSignal);
        PosixSignalRegistration.Create(PosixSignal.SIGHUP, OnSignal);
        // SIGPIPE: Ignore to prevent crash when client disconnects mid-write
        // (not available in PosixSignal enum, use native signal())
        signal(SIGPIPE, SIG_IGN);
#endif

        Log.Info(() => $"mthost {VersionInfo.Version} starting, session={config.SessionId}");
        Console.WriteLine($"[mthost] {VersionInfo.Version} starting, session={config.SessionId}");

        try
        {
            await RunAsync(config).ConfigureAwait(false);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[mthost] Fatal error: {ex.Message}");
            Log.Exception(ex, "Fatal error");
            return 1;
        }
        finally
        {
            Log.Shutdown();
        }
    }

    private static async Task RunAsync(SessionConfig config)
    {
        var shellRegistry = new ShellRegistry();
        var shellConfig = shellRegistry.GetConfigurationByName(config.ShellType)
            ?? shellRegistry.GetConfigurationOrDefault(null);

        IPtyConnection? pty = null;
        IProcessMonitor? processMonitor = null;
        TerminalSession? session = null;
        CancellationTokenSource? shutdownCts = null;
        try
        {
            Console.WriteLine($"[mthost] Creating PTY: {shellConfig.ExecutablePath}");
            var environment = shellConfig.GetEnvironmentVariables();
            InjectTmuxEnvironment(environment, config);
            TerminalEnvironmentOverrides.ApplyMarkedOverrides(environment);
            pty = PtyConnectionFactory.Create(
                shellConfig.ExecutablePath,
                shellConfig.Arguments,
                config.WorkingDirectory,
                config.Cols,
                config.Rows,
                environment);
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[mthost] PTY created, PID={pty.Pid}"));

            processMonitor = CreateProcessMonitor();
            session = new TerminalSession(
                config.SessionId,
                config.MtInstanceId,
                config.MtOwnerToken,
                pty,
                shellConfig.ShellType,
                config.Cols,
                config.Rows,
                config.ScrollbackBytes,
                processMonitor);
            var endpoint = string.IsNullOrWhiteSpace(config.MtInstanceId)
                ? IpcEndpoint.GetLegacySessionEndpoint(config.SessionId, Environment.ProcessId)
                : IpcEndpoint.GetSessionEndpoint(config.MtInstanceId, config.SessionId, Environment.ProcessId);
            Console.WriteLine($"[mthost] Listening on: {endpoint}");
            Log.Info(() => string.Create(CultureInfo.InvariantCulture, $"PTY ready, PID={pty.Pid}, endpoint={endpoint}"));

            if (processMonitor is not null)
            {
                processMonitor.StartMonitoring(pty.Pid, pty.MasterFd);
                Log.Info(() => string.Create(CultureInfo.InvariantCulture, $"Process monitor started for PID={pty.Pid}, masterFd={pty.MasterFd}"));
            }

            shutdownCts = new CancellationTokenSource();
            var previousShutdownCts = Interlocked.Exchange(ref _shutdownCts, shutdownCts);
            previousShutdownCts?.Dispose();
            Task? ptyReadTask = null;

            // Accept client connections (mt.exe)
            // The read loop is started by the first client that connects
            await AcceptClientsAsync(session, endpoint, shutdownCts.Token, () =>
            {
                if (ptyReadTask is null)
                {
                    ptyReadTask = session.StartReadLoopAsync(shutdownCts.Token);
                }
            }).ConfigureAwait(false);

            shutdownCts.Cancel();
            if (ptyReadTask is not null)
            {
                await ptyReadTask.ConfigureAwait(false);
            }
        }
        finally
        {
            if (shutdownCts is not null)
            {
                if (ReferenceEquals(_shutdownCts, shutdownCts))
                {
                    Interlocked.Exchange(ref _shutdownCts, null);
                }

                shutdownCts.Dispose();
            }
            session?.Dispose();
            processMonitor?.StopMonitoring();
            processMonitor?.Dispose();
            pty?.Dispose();
        }
    }

    private static void InjectTmuxEnvironment(Dictionary<string, string> env, SessionConfig config)
    {
        if (config.MtPort is null || config.MtToken is null)
        {
            return;
        }

        var pid = Environment.ProcessId;
        var tmuxPath = OperatingSystem.IsWindows()
            ? string.Create(CultureInfo.InvariantCulture, $@"\\.\pipe\midterm-tmux-{pid},{pid},0")
            : string.Create(CultureInfo.InvariantCulture, $"/tmp/midterm-tmux-{pid},{pid},0");

        env["TMUX"] = tmuxPath;
        env["TMUX_PANE"] = string.Create(CultureInfo.InvariantCulture, $"%{config.PaneIndex ?? 0}");
        env["MT_PORT"] = config.MtPort.Value.ToString(CultureInfo.InvariantCulture);
        env["MT_TOKEN"] = config.MtToken;
        env["MT_SESSION_ID"] = config.SessionId;
        env["MT_PREVIEW_NAME"] = "default";

        // Prepend tmux script directory to PATH so the tmux shim is found
        if (!string.IsNullOrEmpty(config.TmuxBinDir))
        {
            var separator = Path.PathSeparator;
            var currentPath = env.TryGetValue("PATH", out var p) ? p : "";
            env["PATH"] = $"{config.TmuxBinDir}{separator}{currentPath}";
        }
    }

    private static IProcessMonitor? CreateProcessMonitor()
    {
        try
        {
#if WINDOWS
#pragma warning disable CA1416 // Platform compatibility - guarded by #if WINDOWS
            return new WindowsProcessMonitor();
#pragma warning restore CA1416
#elif LINUX
            return new LinuxProcessMonitor();
#elif MACOS
            return new MacOSProcessMonitor();
#else
            return null;
#endif
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"Failed to create process monitor: {ex.Message}");
            return null;
        }
    }

    // Track current client to disconnect when a new one connects
    private static CancellationTokenSource? _currentClientCts;
    private static readonly object _clientLock = new();

    private static async Task AcceptClientsAsync(TerminalSession session, string endpoint, CancellationToken ct, Action? onFirstClientSubscribed = null)
    {
        var firstClientSubscribed = false;
        var connectionCount = 0;
        var clientTasks = new List<Task>();

        using var server = IpcServerFactory.Create(endpoint);

        while (!ct.IsCancellationRequested && session.IsRunning)
        {
            try
            {
                var client = await server.AcceptAsync(ct).ConfigureAwait(false);
                connectionCount++;
                Log.Info(() => string.Create(CultureInfo.InvariantCulture, $"Client connected (#{connectionCount})"));

                // Start the read loop when the first client subscribes to output
                Action? onSubscribed = null;
                if (!firstClientSubscribed && onFirstClientSubscribed is not null)
                {
                    onSubscribed = () =>
                    {
                        if (!firstClientSubscribed)
                        {
                            firstClientSubscribed = true;
                            onFirstClientSubscribed();
                        }
                    };
                }

                var handlerTask = RunClientAsync(session, client, onSubscribed, ct);
                lock (clientTasks)
                {
                    clientTasks.Add(handlerTask);
                }

                _ = handlerTask.ContinueWith(t =>
                {
                    try
                    {
                        client.Dispose();
                    }
                    catch (Exception disposeEx)
                    {
                        Log.Exception(disposeEx, "RunClient.ClientDispose");
                    }

                    if (t.Exception is not null)
                    {
                        Log.Exception(t.Exception.Flatten().InnerException ?? t.Exception, "HandleClient.Task");
                    }
                    lock (clientTasks)
                    {
                        clientTasks.Remove(t);
                    }
                }, TaskScheduler.Default);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                Log.Error(() => $"Accept error: {ex.Message}");
                Log.Exception(ex, "AcceptClients");
                await Task.Delay(100, ct).ConfigureAwait(false);
            }
        }

        Task[] remaining;
        lock (clientTasks)
        {
            remaining = clientTasks.ToArray();
        }

        if (remaining.Length > 0)
        {
            await Task.WhenAll(remaining).ConfigureAwait(false);
        }
    }

    private static async Task RunClientAsync(
        TerminalSession session,
        IIpcClientConnection client,
        Action? onSubscribed,
        CancellationToken shutdownToken)
    {
        using var clientCts = new CancellationTokenSource();
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(shutdownToken, clientCts.Token);

        try
        {
            await HandleClientAsync(
                session,
                client,
                linkedCts.Token,
                onSubscribed,
                () => PromoteCurrentClient(clientCts)).ConfigureAwait(false);
        }
        finally
        {
        }
    }

    private static void PromoteCurrentClient(CancellationTokenSource nextClientCts)
    {
        lock (_clientLock)
        {
            if (ReferenceEquals(_currentClientCts, nextClientCts))
            {
                return;
            }

            if (_currentClientCts is not null)
            {
                _currentClientCts.Cancel();
                _currentClientCts.Dispose();
                _currentClientCts = null;
            }

            _currentClientCts = nextClientCts;
        }
    }

    private static void DisposeCurrentClientCts_NoLock()
    {
        if (_currentClientCts is null)
        {
            return;
        }

        _currentClientCts.Cancel();
        _currentClientCts.Dispose();
        _currentClientCts = null;
    }

    private readonly record struct PooledFrame(
        byte[] Buffer,
        int Length,
        int TerminalBytes,
        ulong SequenceEndExclusive,
        long EnqueuedAtMs,
        uint InputTraceId);

    private readonly record struct PendingInputTraceOutputFlush(
        uint TraceId,
        ulong SequenceEndExclusive,
        long EnqueuedAtMs,
        long IpcWriteDoneAtMs);

    private static bool EnqueueFrame(
        ChannelWriter<PooledFrame> writer,
        ReadOnlySpan<byte> frame,
        int terminalBytes = 0,
        ulong sequenceEndExclusive = 0,
        uint inputTraceId = 0)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(frame.Length);
        frame.CopyTo(buffer);
        if (!writer.TryWrite(new PooledFrame(
            buffer,
            frame.Length,
            terminalBytes,
            sequenceEndExclusive,
            Environment.TickCount64,
            inputTraceId)))
        {
            ArrayPool<byte>.Shared.Return(buffer);
            return false;
        }

        return true;
    }

    private static async Task DrainWriteChannelAsync(
        TerminalSession session,
        Stream stream,
        ChannelReader<PooledFrame> reader,
        CancellationToken ct)
    {
        try
        {
            while (await reader.WaitToReadAsync(ct).ConfigureAwait(false))
            {
                List<PendingInputTraceOutputFlush>? traceFlushes = null;
                while (reader.TryRead(out var frame))
                {
                    try
                    {
                        await stream.WriteAsync(frame.Buffer.AsMemory(0, frame.Length), ct).ConfigureAwait(false);
                        var writeDoneAtMs = Environment.TickCount64;
                        session.RecordIpcFlushed(frame.SequenceEndExclusive, frame.TerminalBytes, frame.EnqueuedAtMs);
                        if (frame.InputTraceId != 0)
                        {
                            traceFlushes ??= [];
                            traceFlushes.Add(new PendingInputTraceOutputFlush(
                                frame.InputTraceId,
                                frame.SequenceEndExclusive,
                                frame.EnqueuedAtMs,
                                writeDoneAtMs));
                        }
                    }
                    finally
                    {
                        ArrayPool<byte>.Shared.Return(frame.Buffer);
                    }
                }
                await stream.FlushAsync(ct).ConfigureAwait(false);

                if (traceFlushes is { Count: > 0 })
                {
                    var flushDoneAtMs = Environment.TickCount64;
                    foreach (var traceFlush in traceFlushes)
                    {
                        if (session.TryCreateInputTraceOutputReport(
                            traceFlush.TraceId,
                            traceFlush.SequenceEndExclusive,
                            traceFlush.EnqueuedAtMs,
                            traceFlush.IpcWriteDoneAtMs,
                            flushDoneAtMs,
                            out var report))
                        {
                            await stream.WriteAsync(report, ct).ConfigureAwait(false);
                        }
                    }

                    await stream.FlushAsync(ct).ConfigureAwait(false);
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Log.Error(() => $"Drain loop error: {ex.Message}");
            Log.Exception(ex, "DrainWriteChannel");
        }
        finally
        {
            // Drain and return any remaining pooled buffers
            while (reader.TryRead(out var remaining))
            {
                ArrayPool<byte>.Shared.Return(remaining.Buffer);
            }
        }
    }

    private static async Task HandleClientAsync(
        TerminalSession session,
        IIpcClientConnection client,
        CancellationToken ct,
        Action? onSubscribed = null,
        Action? onAttached = null)
    {
        var outputLock = new object();
        var handshakeComplete = false;
        var stream = client.Stream;
        var handshakeCursor = session.GetOutputCursor();

        var writeChannel = Channel.CreateBounded<PooledFrame>(new BoundedChannelOptions(MaxIpcQueuedFramesPerClient)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.DropWrite
        });
        var channelWriter = writeChannel.Writer;
        session.ResetIpcTransportState();

        // CTS that heartbeat can cancel to terminate message processing
        using var clientCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var handshakeTimeoutCts = new CancellationTokenSource(HandshakeTimeoutMs);
        using var handshakeTimeoutRegistration = handshakeTimeoutCts.Token.Register(() =>
        {
            lock (outputLock)
            {
                if (!handshakeComplete)
                {
                    Log.Warn(() => "Handshake timeout - closing client connection");
                    clientCts.Cancel();
                }
            }
        });

        var drainTask = DrainWriteChannelAsync(session, stream, writeChannel.Reader, clientCts.Token);
        var heartbeatTask = HeartbeatLoopAsync(client, clientCts);

        try
        {
            void OnOutput(ulong sequenceStart, ReadOnlyMemory<byte> data)
            {
                try
                {
                    if (!Volatile.Read(ref handshakeComplete))
                    {
                        return;
                    }

                    if (client.IsConnected)
                    {
                        var sequenceEndExclusive = sequenceStart + (ulong)data.Length;
                        var inputTraceId = session.GetInputTraceIdForOutput(sequenceEndExclusive);
                        TtyHostProtocol.WriteOutputMessage(sequenceStart, session.Cols, session.Rows, data.Span, frame =>
                        {
                            if (EnqueueFrame(channelWriter, frame, data.Length, sequenceEndExclusive, inputTraceId))
                            {
                                session.RecordIpcEnqueued(sequenceEndExclusive, data.Length);
                                return;
                            }

                            session.RecordDataLoss(TerminalReplayReason.MthostIpcOverflow, data.Length);
                            Log.Warn(() => $"IPC client backlog overflow for session {session.Id}, forcing reconnect");
                            clientCts.Cancel();
                        });
                    }
                    else
                    {
                        Log.Verbose(() => $"[IPC-OUTPUT] Client not connected, discarding {data.Length} bytes");
                    }
                }
                catch (Exception ex)
                {
                    Log.Error(() => $"Output write failed: {ex.Message}");
                    Log.Exception(ex, "OnOutput.Write");
                }
            }

            void OnStateChange()
            {
                try
                {
                    if (client.IsConnected)
                    {
                        var msg = TtyHostProtocol.CreateStateChange(session.IsRunning, session.ExitCode);
                        if (!EnqueueFrame(channelWriter, msg))
                        {
                            clientCts.Cancel();
                        }
                    }
                }
                catch (Exception ex) { Log.Exception(ex, "OnStateChange"); }
            }

            void OnForegroundChanged(ForegroundProcessInfo info)
            {
                try
                {
                    if (client.IsConnected && handshakeComplete)
                    {
                        var payload = new ForegroundChangePayload
                        {
                            Pid = info.Pid,
                            Name = info.Name,
                            CommandLine = info.CommandLine,
                            Cwd = info.Cwd,
                            AgentAttachPoint = SessionAgentAttachPointDetector.Detect(info.Name, info.CommandLine)
                        };
                        var msg = TtyHostProtocol.CreateForegroundChange(payload);
                        if (!EnqueueFrame(channelWriter, msg))
                        {
                            clientCts.Cancel();
                        }
                    }
                }
                catch (Exception ex) { Log.Exception(ex, "OnForegroundChanged"); }
            }

            void OnHandshakeComplete()
            {
                bool alreadyComplete;
                lock (outputLock)
                {
                    alreadyComplete = handshakeComplete;
                }

                if (alreadyComplete)
                {
                    return;
                }

                // Dispose the timeout registration BEFORE cancelling the CTS.
                // The registration checks handshakeComplete (still false here) and would
                // incorrectly cancel the client connection if it fires.
                handshakeTimeoutRegistration.Dispose();
                handshakeTimeoutCts.Cancel();

                Log.Verbose(() => $"[HANDSHAKE] Complete, client connected: {client.IsConnected}");

                if (!client.IsConnected)
                {
                    lock (outputLock) { handshakeComplete = true; }
                    return;
                }

                // Replay buffered output BEFORE enabling live forwarding.
                // While handshakeComplete is false, OnOutput callbacks return early,
                // so replay frames won't interleave with live output on the IPC stream.
                var replayed = session.TryReplayOutputSince(handshakeCursor, (sequenceStart, bufferedSegment) =>
                {
                    if (!client.IsConnected)
                    {
                        return;
                    }

                    var sequenceEndExclusive = sequenceStart + (ulong)bufferedSegment.Length;
                    TtyHostProtocol.WriteOutputMessage(sequenceStart, session.Cols, session.Rows, bufferedSegment.Span, frame =>
                    {
                        if (EnqueueFrame(channelWriter, frame, bufferedSegment.Length, sequenceEndExclusive))
                        {
                            session.RecordIpcEnqueued(sequenceEndExclusive, bufferedSegment.Length);
                            return;
                        }

                        session.RecordDataLoss(TerminalReplayReason.MthostIpcOverflow, bufferedSegment.Length);
                        clientCts.Cancel();
                    });
                });

                if (!replayed)
                {
                    Log.Warn(() => "Buffered output dropped before handshake completed (scrollback too small)");
                }

                // Now enable live forwarding — all replay data has been enqueued
                lock (outputLock)
                {
                    handshakeComplete = true;
                }
            }

            session.OnOutput += OnOutput;
            onSubscribed?.Invoke(); // Notify that we're subscribed - read loop can start now

            // Don't subscribe to OnStateChanged until after handshake - OSC-7 during startup
            // can fire StateChange before Info response, breaking the handshake
            var stateChangeSubscribed = false;

            try
            {
                await ProcessMessagesAsync(session, stream, channelWriter, clientCts.Token, () =>
                {
                    OnHandshakeComplete();
                    // Only subscribe once - repeated GetInfo requests must not add duplicate handlers
                    // (duplicate handlers cause exponential StateChange message growth)
                    if (!stateChangeSubscribed)
                    {
                        stateChangeSubscribed = true;
                        session.OnStateChanged += OnStateChange;
                        session.OnForegroundChanged += OnForegroundChanged;
                    }
                },
                onAttached).ConfigureAwait(false);
            }
            finally
            {
                session.OnOutput -= OnOutput;
                session.OnStateChanged -= OnStateChange;
                session.OnForegroundChanged -= OnForegroundChanged;
            }
        }
        catch (Exception ex)
        {
            Log.Error(() => $"Client handler error: {ex.Message}");
            Log.Exception(ex, "HandleClient");
        }
        finally
        {
            channelWriter.Complete();
            try { await drainTask.ConfigureAwait(false); } catch { }
            clientCts.Cancel();
            // Await heartbeat completion; exceptions are expected during cancellation
            try { await heartbeatTask.ConfigureAwait(false); } catch { }
            Log.Info(() => "Client disconnected");
        }
    }

    private static async Task HeartbeatLoopAsync(IIpcClientConnection client, CancellationTokenSource clientCts)
    {
        var ct = clientCts.Token;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(HeartbeatIntervalMs, ct).ConfigureAwait(false);

                if (!client.IsConnected)
                {
                    Log.Info(() => "Heartbeat: client disconnected");
                    clientCts.Cancel();
                    break;
                }

#if WINDOWS
                // Use PeekNamedPipe for instant stale detection on Windows
                if (client.Stream is NamedPipeServerStream pipe)
                {
                    try
                    {
                        var handle = pipe.SafePipeHandle;
                        if (!PeekNamedPipe(handle, IntPtr.Zero, 0, IntPtr.Zero, out _, IntPtr.Zero))
                        {
                            var error = Marshal.GetLastWin32Error();
                            Log.Warn(() => string.Create(CultureInfo.InvariantCulture, $"Heartbeat: PeekNamedPipe failed (error {error}) - pipe stale"));
                            clientCts.Cancel();
                            break;
                        }
                    }
                    catch (ObjectDisposedException)
                    {
                        clientCts.Cancel();
                        break;
                    }
                }
#else
                // On Unix, check socket state
                if (client.Stream is NetworkStream ns)
                {
                    try
                    {
                        var socket = ns.Socket;
                        if (socket.Poll(0, SelectMode.SelectError))
                        {
                            Log.Warn(() => "Heartbeat: socket error detected");
                            clientCts.Cancel();
                            break;
                        }
                    }
                    catch
                    {
                        clientCts.Cancel();
                        break;
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
                Log.Error(() => $"Heartbeat error: {ex.Message}");
            }
        }
    }

    private static async Task ProcessMessagesAsync(
        TerminalSession session,
        Stream stream,
        ChannelWriter<PooledFrame> channelWriter,
        CancellationToken ct,
        Action? onHandshakeComplete = null,
        Action? onAttached = null)
    {
        var headerBuffer = new byte[TtyHostProtocol.HeaderSize];
        var attached = !session.RequiresOwnershipHandshake;
        uint? pendingInputTraceId = null;
        long pendingInputTraceMarkerReceivedAtMs = 0;

        while (!ct.IsCancellationRequested)
        {
            int bytesRead;
            try
            {
                bytesRead = await stream.ReadAsync(headerBuffer, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Error(() => $"IPC read error: {ex.Message}");
                break;
            }

            if (bytesRead == 0)
            {
                break;
            }

            while (bytesRead < TtyHostProtocol.HeaderSize)
            {
                var more = await stream.ReadAsync(headerBuffer.AsMemory(bytesRead, TtyHostProtocol.HeaderSize - bytesRead), ct).ConfigureAwait(false);
                if (more == 0) break;
                bytesRead += more;
            }

            if (!TtyHostProtocol.TryReadHeader(headerBuffer, out var msgType, out var payloadLength))
            {
                Log.Warn(() => "Invalid message header");
                break;
            }

            if (payloadLength < 0 || payloadLength > TtyHostProtocol.MaxPayloadSize)
            {
                Log.Warn(() => string.Create(CultureInfo.InvariantCulture, $"Invalid payload length: {payloadLength}"));
                break;
            }

            // Read payload - allocate dynamically based on actual size
            byte[]? payloadBuffer = null;
            if (payloadLength > 0)
            {
                payloadBuffer = ArrayPool<byte>.Shared.Rent(payloadLength);
                var totalRead = 0;
                while (totalRead < payloadLength)
                {
                    var chunk = await stream.ReadAsync(payloadBuffer.AsMemory(totalRead, payloadLength - totalRead), ct).ConfigureAwait(false);
                    if (chunk == 0) break;
                    totalRead += chunk;
                }

                if (totalRead < payloadLength)
                {
                    ArrayPool<byte>.Shared.Return(payloadBuffer);
                    break;
                }
            }

            var payload = payloadLength > 0
                ? payloadBuffer.AsSpan(0, payloadLength)
                : ReadOnlySpan<byte>.Empty;

            // Process message - wrap in try-catch for robustness
            try
            {
                if (!attached)
                {
                    if (msgType != TtyHostMessageType.Attach)
                    {
                        Log.Warn(() => $"Rejecting client before attach: first message was {msgType}");
                        break;
                    }

                    var attachRequest = TtyHostProtocol.ParseAttachRequest(payload);
                    if (attachRequest is null ||
                        !string.Equals(attachRequest.InstanceId, session.OwnerInstanceId, StringComparison.Ordinal) ||
                        !string.Equals(attachRequest.OwnerToken, session.OwnerToken, StringComparison.Ordinal))
                    {
                        var reject = TtyHostProtocol.CreateAttachAck(false, "mthost ownership mismatch");
                        EnqueueFrame(channelWriter, reject);
                        Log.Warn(() => $"Rejected client for session {session.Id}: ownership mismatch");
                        break;
                    }

                    attached = true;
                    var accept = TtyHostProtocol.CreateAttachAck(true);
                    EnqueueFrame(channelWriter, accept);
                    onAttached?.Invoke();
                    continue;
                }

                switch (msgType)
                {
                    case TtyHostMessageType.GetInfo:
                        var info = session.GetInfo();
                        var infoMsg = TtyHostProtocol.CreateInfoResponse(info);
                        EnqueueFrame(channelWriter, infoMsg);
                        onHandshakeComplete?.Invoke();
                        break;

                    case TtyHostMessageType.Input:
                        var inputReceivedAtMs = Environment.TickCount64;
                        var inputSlice = payloadLength > 0 && payloadBuffer is not null
                            ? payloadBuffer.AsMemory(0, payloadLength)
                            : ReadOnlyMemory<byte>.Empty;
                        Log.Verbose(() => $"[IPC-INPUT] {inputSlice.Length} bytes");
                        if (pendingInputTraceId is uint pendingTraceId)
                        {
                            session.BeginInputTrace(
                                pendingTraceId,
                                pendingInputTraceMarkerReceivedAtMs,
                                inputReceivedAtMs);
                        }

                        await session.SendInputAsync(inputSlice, ct).ConfigureAwait(false);
                        if (pendingInputTraceId is uint traceId)
                        {
                            var ptyWriteDoneAtMs = Environment.TickCount64;
                            session.MarkInputTracePtyWriteDone(traceId, ptyWriteDoneAtMs);
                            var report = TtyHostProtocol.CreateInputTraceReport(new TtyHostInputTraceReport(
                                traceId,
                                pendingInputTraceMarkerReceivedAtMs,
                                inputReceivedAtMs,
                                ptyWriteDoneAtMs));
                            EnqueueFrame(channelWriter, report);
                            pendingInputTraceId = null;
                            pendingInputTraceMarkerReceivedAtMs = 0;
                        }
                        break;

                    case TtyHostMessageType.InputTraceMarker:
                        if (TtyHostProtocol.TryParseInputTraceMarker(payload, out var markerTraceId))
                        {
                            pendingInputTraceId = markerTraceId;
                            pendingInputTraceMarkerReceivedAtMs = Environment.TickCount64;
                        }
                        break;

                    case TtyHostMessageType.Resize:
                        var (cols, rows) = TtyHostProtocol.ParseResize(payload);
                        session.Resize(cols, rows);
                        var resizeAck = TtyHostProtocol.CreateResizeAck();
                        EnqueueFrame(channelWriter, resizeAck);
                        break;

                    case TtyHostMessageType.GetBuffer:
                        byte[]? snapshot = null;
                        try
                        {
                            var request = TtyHostProtocol.ParseGetBuffer(payload) ?? new TtyHostGetBufferRequest();
                            var guess = session.GetBufferLength(request.MaxBytes);
                            if (guess <= 0)
                            {
                                TtyHostProtocol.WriteBufferResponse(0, ReadOnlySpan<byte>.Empty, frame =>
                                {
                                    EnqueueFrame(channelWriter, frame);
                                });
                                break;
                            }

                            while (true)
                            {
                                snapshot = ArrayPool<byte>.Shared.Rent(Math.Max(guess, 1));
                                var written = session.CopyBufferSnapshot(snapshot, request.MaxBytes, request.Reason, out var sequenceStart);
                                if (written >= 0)
                                {
                                    var payloadSlice = snapshot.AsSpan(0, written);
                                    TtyHostProtocol.WriteBufferResponse(sequenceStart, payloadSlice, frame =>
                                    {
                                        EnqueueFrame(channelWriter, frame);
                                    });
                                    break;
                                }

                                ArrayPool<byte>.Shared.Return(snapshot, clearArray: false);
                                snapshot = null;
                                guess = -written;
                            }
                        }
                        finally
                        {
                            if (snapshot is not null)
                            {
                                ArrayPool<byte>.Shared.Return(snapshot, clearArray: false);
                            }
                        }
                        break;

                    case TtyHostMessageType.SetName:
                        var name = TtyHostProtocol.ParseSetName(payload);
                        session.SetName(string.IsNullOrEmpty(name) ? null : name);
                        var nameAck = TtyHostProtocol.CreateSetNameAck();
                        EnqueueFrame(channelWriter, nameAck);
                        break;

                    case TtyHostMessageType.SetOrder:
                        var order = TtyHostProtocol.ParseSetOrder(payload);
                        session.SetOrder(order);
                        var orderAck = TtyHostProtocol.CreateSetOrderAck();
                        EnqueueFrame(channelWriter, orderAck);
                        Log.Verbose(() => $"Order set to {order}");
                        break;

                    case TtyHostMessageType.SetMetadata:
                        var metadata = TtyHostProtocol.ParseSetMetadata(payload) ?? new TtyHostSessionMetadata();
                        session.SetMetadata(metadata);
                        var metadataAck = TtyHostProtocol.CreateSetMetadataAck();
                        EnqueueFrame(channelWriter, metadataAck);
                        break;

                    case TtyHostMessageType.SetClipboardImage:
                        var clipboardRequest = TtyHostProtocol.ParseSetClipboardImage(payload);
                        if (clipboardRequest is null || string.IsNullOrWhiteSpace(clipboardRequest.FilePath))
                        {
                            var invalidAck = TtyHostProtocol.CreateSetClipboardImageAck(false, "Invalid clipboard image request");
                            EnqueueFrame(channelWriter, invalidAck);
                            break;
                        }

                        var clipboardResult = await LocalClipboard.SetImageAsync(
                            clipboardRequest.FilePath,
                            clipboardRequest.MimeType).ConfigureAwait(false);
                        var clipboardAck = TtyHostProtocol.CreateSetClipboardImageAck(
                            clipboardResult.Success,
                            clipboardResult.Error);
                        EnqueueFrame(channelWriter, clipboardAck);
                        break;

                    case TtyHostMessageType.Ping:
                        var pongMsg = TtyHostProtocol.CreatePong(payload);
                        EnqueueFrame(channelWriter, pongMsg);
                        break;

                    case TtyHostMessageType.Close:
                        Log.Info(() => "Received close request, shutting down");
                        var closeAck = TtyHostProtocol.CreateCloseAck();
                        EnqueueFrame(channelWriter, closeAck);
                        session.Kill();
                        // Signal graceful shutdown - let finally blocks run
                        _shutdownCts?.Cancel();
                        return;

                    default:
                        Log.Warn(() => $"Unknown message type: {msgType}");
                        break;
                }
            }
            catch (Exception ex) when (msgType != TtyHostMessageType.Close)
            {
                Log.Error(() => $"Error processing message type {msgType}: {ex.Message}");
                Log.Exception(ex, $"ProcessMessage.{msgType}");
            }
            finally
            {
                if (payloadBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(payloadBuffer);
                }
            }
        }
    }

    private static SessionConfig? ParseArgs(string[] args)
    {
        string? sessionId = null;
        string? shellType = null;
        string? workingDir = null;
        int cols = 80;
        int rows = 24;
        var logLevel = LogSeverity.Warn;
        var scrollbackBytes = TerminalSession.DefaultBufferCapacity;
        int? mtPort = null;
        string? mtToken = null;
        string? mtInstanceId = null;
        string? mtOwnerToken = null;
        int? paneIndex = null;
        string? tmuxBinDir = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--session" when i + 1 < args.Length:
                    sessionId = args[++i];
                    break;
                case "--shell" when i + 1 < args.Length:
                    shellType = args[++i];
                    break;
                case "--cwd" when i + 1 < args.Length:
                    workingDir = args[++i];
                    break;
                case "--cols" when i + 1 < args.Length &&
                    int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var c):
                    cols = c;
                    i++;
                    break;
                case "--rows" when i + 1 < args.Length &&
                    int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var r):
                    rows = r;
                    i++;
                    break;
                case "--scrollback" when i + 1 < args.Length &&
                    int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var sb):
                    scrollbackBytes = sb;
                    i++;
                    break;
                case "--scrollback-bytes" when i + 1 < args.Length &&
                    int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var sbBytes):
                    scrollbackBytes = sbBytes;
                    i++;
                    break;
                case "--loglevel" when i + 1 < args.Length && Enum.TryParse<LogSeverity>(args[i + 1], ignoreCase: true, out var level):
                    logLevel = level;
                    i++;
                    break;
                case "--debug":
                    logLevel = LogSeverity.Verbose;
                    break;
                case "--mt-port" when i + 1 < args.Length &&
                    int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var mp):
                    mtPort = mp;
                    i++;
                    break;
                case "--mt-token" when i + 1 < args.Length:
                    mtToken = args[++i];
                    break;
                case "--mt-instance-id" when i + 1 < args.Length:
                    mtInstanceId = args[++i];
                    break;
                case "--mt-owner-token" when i + 1 < args.Length:
                    mtOwnerToken = args[++i];
                    break;
                case "--pane-index" when i + 1 < args.Length &&
                    int.TryParse(args[i + 1], CultureInfo.InvariantCulture, out var pi):
                    paneIndex = pi;
                    i++;
                    break;
                case "--tmux-bin-dir" when i + 1 < args.Length:
                    tmuxBinDir = args[++i];
                    break;
            }
        }

        sessionId ??= Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
        workingDir ??= Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        scrollbackBytes = Math.Clamp(scrollbackBytes, MinScrollbackBytes, MaxScrollbackBytes);

        return new SessionConfig(sessionId, shellType, workingDir, cols, rows, logLevel, scrollbackBytes,
            mtPort, mtToken, mtInstanceId, mtOwnerToken, paneIndex, tmuxBinDir);
    }

    private static void PrintHelp()
    {
        Console.WriteLine($"""
            mthost {VersionInfo.Version} - MidTerm Console Host

            Usage: mthost --session <id> [options]
                   mthost --pty-exec <slave-path> <cols> <rows> <shell> [shell-args...]

            Required:
              --session <id>    Unique session identifier

            Options:
              --shell <type>    Shell type (pwsh, cmd, bash, zsh)
              --cwd <path>      Working directory
              --cols <n>        Terminal columns (default: 80)
              --rows <n>        Terminal rows (default: 24)
              --scrollback <b>  Scrollback buffer in bytes (default: 2097152)
              --scrollback-bytes <b>  Alias for --scrollback
              --loglevel <lvl>  Log level: exception, error, warn, info, verbose (default: warn)
              --debug           Shortcut for --loglevel verbose
              -h, --help        Show this help
              -v, --version     Show version

            PTY Exec Mode (Unix only):
              --pty-exec        Set up PTY and exec shell (internal, does not return)

            IPC (Windows):
              Listens on named pipe: mthost-<session-id>-<pid>
            IPC (macOS/Linux):
              Listens on Unix socket: /tmp/mthost-<session-id>-<pid>.sock
            """);
    }

#if !WINDOWS
    private static void OnSignal(PosixSignalContext context)
    {
        Log.Info(() => $"Received signal {context.Signal}, initiating graceful shutdown");
        context.Cancel = true;
        _shutdownCts?.Cancel();
    }
#endif

    private sealed record SessionConfig(
        string SessionId, string? ShellType, string WorkingDirectory, int Cols, int Rows,
        LogSeverity LogSeverity, int ScrollbackBytes,
        int? MtPort = null,
        string? MtToken = null,
        string? MtInstanceId = null,
        string? MtOwnerToken = null,
        int? PaneIndex = null,
        string? TmuxBinDir = null);
}

internal sealed class TerminalSession : IDisposable
{
    internal const int DefaultBufferCapacity = 2 * 1024 * 1024; // 2MB fixed buffer

    private sealed class InputTraceState
    {
        public InputTraceState(uint traceId, long markerReceivedAtMs, long inputReceivedAtMs)
        {
            TraceId = traceId;
            MarkerReceivedAtMs = markerReceivedAtMs;
            InputReceivedAtMs = inputReceivedAtMs;
        }

        public uint TraceId { get; }
        public long MarkerReceivedAtMs { get; }
        public long InputReceivedAtMs { get; }
        public long PtyWriteDoneAtMs { get; set; }
        public ulong FirstOutputSequenceEndExclusive { get; set; }
        public long PtyOutputReadAtMs { get; set; }
    }

    private readonly IPtyConnection _pty;
    private readonly IProcessMonitor? _processMonitor;
    private readonly CircularByteBuffer _outputBuffer;
    private readonly object _bufferLock = new();
    private readonly object _transportLock = new();
    private readonly object _inputTraceLock = new();
    private readonly object _metadataLock = new();
    private readonly int _scrollbackBytes;
    private TtyHostTransportInfo _transportInfo;
    private InputTraceState? _inputTrace;
    private string? _topic;
    private TtyHostGitRepoMetadata[] _extraGitRepos = [];

    public string Id { get; }
    public string? OwnerInstanceId { get; }
    public string? OwnerToken { get; }
    public bool RequiresOwnershipHandshake => !string.IsNullOrWhiteSpace(OwnerInstanceId) && !string.IsNullOrWhiteSpace(OwnerToken);
    public ShellType ShellType { get; }
    public int Cols { get; private set; }
    public int Rows { get; private set; }
    public string? Name { get; private set; }
    public byte Order { get; private set; }
    public DateTime CreatedAt { get; } = DateTime.UtcNow;

    public int Pid => _pty.Pid;
    public bool IsRunning => _pty.IsRunning;
    public int? ExitCode => _pty.ExitCode;

    public event Action<ulong, ReadOnlyMemory<byte>>? OnOutput;
    public event Action? OnStateChanged;
    public event Action<ForegroundProcessInfo>? OnForegroundChanged;

    public TerminalSession(
        string id,
        string? ownerInstanceId,
        string? ownerToken,
        IPtyConnection pty,
        ShellType shellType,
        int cols,
        int rows,
        int scrollbackBytes,
        IProcessMonitor? processMonitor = null)
    {
        Id = id;
        OwnerInstanceId = ownerInstanceId;
        OwnerToken = ownerToken;
        _pty = pty;
        _processMonitor = processMonitor;
        ShellType = shellType;
        Cols = cols;
        Rows = rows;
        _scrollbackBytes = scrollbackBytes;
        _outputBuffer = new CircularByteBuffer(scrollbackBytes);
        _transportInfo = new TtyHostTransportInfo
        {
            ScrollbackBytes = scrollbackBytes
        };

        if (_processMonitor is not null)
        {
            _processMonitor.OnForegroundChanged += info => OnForegroundChanged?.Invoke(info);
        }
    }

    public Task StartReadLoopAsync(CancellationToken ct)
    {
        return Task.Factory.StartNew(
            () => RunReadLoop(ct),
            CancellationToken.None,
            TaskCreationOptions.LongRunning,
            TaskScheduler.Default);
    }

    private void RunReadLoop(CancellationToken ct)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(65536);
        using var cancelRegistration = ct.Register(static state =>
        {
            try
            {
                ((IPtyConnection)state!).Dispose();
            }
            catch
            {
                // Best-effort cancellation for blocking PTY reads during shutdown.
            }
        }, _pty);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                int bytesRead;
                try
                {
                    bytesRead = _pty.ReaderStream.Read(buffer, 0, buffer.Length);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (IOException ex)
                {
                    if (ct.IsCancellationRequested)
                    {
                        break;
                    }

                    Log.Exception(ex, "TerminalSession.ReadLoop");
                    break;
                }

                if (bytesRead == 0)
                {
                    break;
                }

                var ptyOutputReadAtMs = Environment.TickCount64;
                var data = buffer.AsMemory(0, bytesRead);
                Log.Verbose(() => string.Create(CultureInfo.InvariantCulture, $"[PTY-READ] {bytesRead} bytes"));
                ulong sequenceStart;
                ulong sequenceEndExclusive;

                lock (_bufferLock)
                {
                    sequenceStart = _outputBuffer.TotalBytesWritten;
                    _outputBuffer.Write(data.Span);
                    sequenceEndExclusive = sequenceStart + (ulong)data.Length;
                }

                lock (_transportLock)
                {
                    _transportInfo.SourceSeq = sequenceEndExclusive;
                }

                MarkInputTracePtyOutputRead(sequenceEndExclusive, ptyOutputReadAtMs);
                OnOutput?.Invoke(sequenceStart, data);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            OnStateChanged?.Invoke();
        }
    }

    public async Task SendInputAsync(ReadOnlyMemory<byte> data, CancellationToken ct)
    {
        Log.Verbose(() => $"[PTY-WRITE] {data.Length} bytes");
        await _pty.WriterStream.WriteAsync(data, ct).ConfigureAwait(false);
    }

    public void BeginInputTrace(uint traceId, long markerReceivedAtMs, long inputReceivedAtMs)
    {
        if (traceId == 0)
        {
            return;
        }

        lock (_inputTraceLock)
        {
            _inputTrace = new InputTraceState(traceId, markerReceivedAtMs, inputReceivedAtMs);
        }
    }

    public void MarkInputTracePtyWriteDone(uint traceId, long ptyWriteDoneAtMs)
    {
        lock (_inputTraceLock)
        {
            if (_inputTrace?.TraceId == traceId)
            {
                _inputTrace.PtyWriteDoneAtMs = ptyWriteDoneAtMs;
            }
        }
    }

    private void MarkInputTracePtyOutputRead(ulong sequenceEndExclusive, long ptyOutputReadAtMs)
    {
        lock (_inputTraceLock)
        {
            if (_inputTrace is null || _inputTrace.FirstOutputSequenceEndExclusive != 0)
            {
                return;
            }

            _inputTrace.FirstOutputSequenceEndExclusive = sequenceEndExclusive;
            _inputTrace.PtyOutputReadAtMs = ptyOutputReadAtMs;
        }
    }

    public uint GetInputTraceIdForOutput(ulong sequenceEndExclusive)
    {
        lock (_inputTraceLock)
        {
            return _inputTrace is not null &&
                _inputTrace.FirstOutputSequenceEndExclusive == sequenceEndExclusive
                ? _inputTrace.TraceId
                : 0;
        }
    }

    public bool TryCreateInputTraceOutputReport(
        uint traceId,
        ulong sequenceEndExclusive,
        long ipcOutputEnqueuedAtMs,
        long ipcOutputWriteDoneAtMs,
        long ipcOutputFlushDoneAtMs,
        out byte[] report)
    {
        lock (_inputTraceLock)
        {
            if (_inputTrace is null ||
                _inputTrace.TraceId != traceId ||
                _inputTrace.FirstOutputSequenceEndExclusive != sequenceEndExclusive)
            {
                report = [];
                return false;
            }

            report = TtyHostProtocol.CreateInputTraceReport(new TtyHostInputTraceReport(
                _inputTrace.TraceId,
                _inputTrace.MarkerReceivedAtMs,
                _inputTrace.InputReceivedAtMs,
                _inputTrace.PtyWriteDoneAtMs,
                _inputTrace.FirstOutputSequenceEndExclusive,
                _inputTrace.PtyOutputReadAtMs,
                ipcOutputEnqueuedAtMs,
                ipcOutputWriteDoneAtMs,
                ipcOutputFlushDoneAtMs));
            _inputTrace = null;
            return true;
        }
    }

    public void Resize(int cols, int rows)
    {
        if (Cols == cols && Rows == rows) return;
        Cols = cols;
        Rows = rows;
        _pty.Resize(cols, rows);
        OnStateChanged?.Invoke();
    }

    public void SetName(string? name)
    {
        Name = name;
        OnStateChanged?.Invoke();
    }

    public void SetOrder(byte order)
    {
        Order = order;
    }

    public void SetMetadata(TtyHostSessionMetadata metadata)
    {
        lock (_metadataLock)
        {
            _topic = string.IsNullOrWhiteSpace(metadata.Topic) ? null : metadata.Topic;
            _extraGitRepos = metadata.ExtraGitRepos
                .Where(static repo => !string.IsNullOrWhiteSpace(repo.RepoRoot))
                .Select(static repo => new TtyHostGitRepoMetadata
                {
                    RepoRoot = repo.RepoRoot,
                    Label = repo.Label,
                    Role = repo.Role,
                    Source = repo.Source
                })
                .ToArray();
        }
    }

    public int GetBufferLength(int? maxBytes = null)
    {
        lock (_bufferLock)
        {
            var length = _outputBuffer.Count;
            if (maxBytes is int cap && cap > 0)
            {
                length = Math.Min(length, cap);
            }

            return length;
        }
    }

    public int CopyBufferSnapshot(Span<byte> destination, int? maxBytes, TerminalReplayReason reason, out ulong sequenceStart)
    {
        lock (_bufferLock)
        {
            var length = _outputBuffer.Count;
            if (maxBytes is int cap && cap > 0)
            {
                length = Math.Min(length, cap);
            }

            if (length == 0)
            {
                sequenceStart = 0;
                RecordReplay(0, reason);
                return 0;
            }

            if (destination.Length < length)
            {
                sequenceStart = 0;
                return -length;
            }

            if (length == _outputBuffer.Count)
            {
                _outputBuffer.CopyTo(destination.Slice(0, length));
                sequenceStart = _outputBuffer.TailPosition;
            }
            else
            {
                var written = _outputBuffer.CopyTailTo(destination.Slice(0, length), out sequenceStart);
                length = written;
            }

            RecordReplay(length, reason);
            return length;
        }
    }

    public ulong GetOutputCursor()
    {
        lock (_bufferLock)
        {
            return _outputBuffer.TotalBytesWritten;
        }
    }

    public bool TryReplayOutputSince(ulong cursor, Action<ulong, ReadOnlyMemory<byte>> consumer)
    {
        var chunkSize = Math.Min(_scrollbackBytes, 64 * 1024);
        if (chunkSize <= 0)
        {
            chunkSize = 4096;
        }

        var scratch = ArrayPool<byte>.Shared.Rent(chunkSize);
        try
        {
            ulong position = cursor;
            while (true)
            {
                int copied;
                lock (_bufferLock)
                {
                    if (!_outputBuffer.TryCopySince(position, scratch, out copied))
                    {
                        return false;
                    }
                }

                if (copied == 0)
                {
                    return true;
                }

                consumer(position, new ReadOnlyMemory<byte>(scratch, 0, copied));
                position += (ulong)copied;
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    public void ResetIpcTransportState()
    {
        lock (_transportLock)
        {
            _transportInfo.IpcQueuedSeq = _transportInfo.IpcFlushedSeq;
            _transportInfo.IpcBacklogFrames = 0;
            _transportInfo.IpcBacklogBytes = 0;
            _transportInfo.OldestBacklogAgeMs = 0;
        }
    }

    public void RecordIpcEnqueued(ulong sequenceEndExclusive, int terminalBytes)
    {
        if (terminalBytes <= 0)
        {
            return;
        }

        lock (_transportLock)
        {
            if (_transportInfo.IpcBacklogFrames == 0)
            {
                _transportInfo.OldestBacklogAgeMs = 0;
            }

            _transportInfo.IpcQueuedSeq = sequenceEndExclusive;
            _transportInfo.IpcBacklogFrames++;
            _transportInfo.IpcBacklogBytes += terminalBytes;
        }
    }

    public void RecordIpcFlushed(ulong sequenceEndExclusive, int terminalBytes, long enqueuedAtMs)
    {
        if (terminalBytes <= 0)
        {
            return;
        }

        lock (_transportLock)
        {
            _transportInfo.IpcFlushedSeq = sequenceEndExclusive;
            _transportInfo.IpcBacklogFrames = Math.Max(0, _transportInfo.IpcBacklogFrames - 1);
            _transportInfo.IpcBacklogBytes = Math.Max(0, _transportInfo.IpcBacklogBytes - terminalBytes);
            _transportInfo.OldestBacklogAgeMs = _transportInfo.IpcBacklogFrames > 0
                ? Math.Max(0, Environment.TickCount64 - enqueuedAtMs)
                : 0;
        }
    }

    public void RecordReplay(int bytes, TerminalReplayReason reason)
    {
        lock (_transportLock)
        {
            _transportInfo.LastReplayBytes = bytes;
            _transportInfo.LastReplayReason = reason;
        }
    }

    public void RecordDataLoss(TerminalReplayReason reason, int droppedBytes)
    {
        lock (_transportLock)
        {
            _transportInfo.DataLossCount++;
            _transportInfo.LastDataLossReason = reason;
            _transportInfo.LastReplayReason = reason;
            if (droppedBytes > 0)
            {
                _transportInfo.LastReplayBytes = droppedBytes;
            }
            _transportInfo.IpcBacklogFrames = 0;
            _transportInfo.IpcBacklogBytes = 0;
            _transportInfo.OldestBacklogAgeMs = 0;
            _transportInfo.IpcQueuedSeq = _transportInfo.IpcFlushedSeq;
        }
    }

    public void Dispose()
    {
        _outputBuffer.Dispose();
    }

    public SessionInfo GetInfo()
    {
        var info = new SessionInfo
        {
            Id = Id,
            Pid = Pid,
            HostPid = Environment.ProcessId,
            ShellType = ShellType.ToString(),
            Cols = Cols,
            Rows = Rows,
            IsRunning = IsRunning,
            ExitCode = ExitCode,
            Name = Name,
            Order = Order,
            CreatedAt = CreatedAt,
            TtyHostVersion = VersionInfo.Version,
            OwnerInstanceId = OwnerInstanceId,
            Transport = GetTransportInfo()
        };

        lock (_metadataLock)
        {
            info.Topic = _topic;
            info.ExtraGitRepos = _extraGitRepos
                .Select(static repo => new TtyHostGitRepoMetadata
                {
                    RepoRoot = repo.RepoRoot,
                    Label = repo.Label,
                    Role = repo.Role,
                    Source = repo.Source
                })
                .ToArray();
        }

        if (_processMonitor is not null)
        {
            info.CurrentDirectory = _processMonitor.GetShellCwd();
            var foreground = _processMonitor.GetCurrentForeground();
            if (foreground.Pid != Pid)
            {
                info.ForegroundPid = foreground.Pid;
                info.ForegroundName = foreground.Name;
                info.ForegroundCommandLine = foreground.CommandLine;
                info.AgentAttachPoint = SessionAgentAttachPointDetector.Detect(foreground.Name, foreground.CommandLine);
            }
        }

        return info;
    }

    public void Kill()
    {
        _pty.Kill();
    }

    private TtyHostTransportInfo GetTransportInfo()
    {
        lock (_transportLock)
        {
            return new TtyHostTransportInfo
            {
                SourceSeq = _transportInfo.SourceSeq,
                IpcQueuedSeq = _transportInfo.IpcQueuedSeq,
                IpcFlushedSeq = _transportInfo.IpcFlushedSeq,
                IpcBacklogFrames = _transportInfo.IpcBacklogFrames,
                IpcBacklogBytes = _transportInfo.IpcBacklogBytes,
                OldestBacklogAgeMs = _transportInfo.OldestBacklogAgeMs,
                ScrollbackBytes = _transportInfo.ScrollbackBytes,
                LastReplayBytes = _transportInfo.LastReplayBytes,
                LastReplayReason = _transportInfo.LastReplayReason,
                DataLossCount = _transportInfo.DataLossCount,
                LastDataLossReason = _transportInfo.LastDataLossReason
            };
        }
    }
}

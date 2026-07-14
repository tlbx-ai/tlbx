using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Models;

using Ai.Tlbx.MidTerm.Services.Updates;
using Ai.Tlbx.MidTerm.Models.Auth;
using Ai.Tlbx.MidTerm.Models.Certificates;
using Ai.Tlbx.MidTerm.Models.Files;
using Ai.Tlbx.MidTerm.Models.Git;
using Ai.Tlbx.MidTerm.Models.History;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Models.System;
using Ai.Tlbx.MidTerm.Settings;
using Ai.Tlbx.MidTerm.Services.Hosting;
namespace Ai.Tlbx.MidTerm.Services.Sessions;

/// <summary>
/// Manages mmttyhost processes. Spawns new sessions, discovers existing ones on startup.
/// </summary>
public sealed class TtyHostSessionManager : IAsyncDisposable
{
    public const int MaxSessions = 256;
    private const int SessionIdLength = 8;
    private const int MaximumTerminalRows = 100;
    private const string FallbackMinCompatibleVersion = "2.0.0";

    private readonly SessionRegistry _registry;
    private readonly string? _expectedTtyHostVersion;
    private readonly string? _minCompatibleVersion;
    private readonly MidTermInstanceIdentity _instanceIdentity;
    private readonly TtyHostOwnershipRegistry _ownershipRegistry;
    private readonly SessionForegroundProcessService _foregroundProcessService;
    private readonly SettingsService? _settingsService;
    private readonly ConcurrentDictionary<string, TerminalTransportRuntimeState> _transportState = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _resizeGates = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TerminalDimensions> _redrawDimensionOverrides = new(StringComparer.Ordinal);
    private string? _runAsUser;
    private string? _runAsUserSid;
    private bool _disposed;
    private int? _mtPort;
    private Func<string>? _generateToken;
    private string? _tmuxBinDir;
    private readonly ConcurrentDictionary<string, TtyHostClient> _clients;
    private readonly ConcurrentDictionary<string, SessionInfo> _sessionCache;
    private ConcurrentDictionary<string, int> _sessionOrder => _registry.SessionOrder;
    private ConcurrentDictionary<string, byte> _tmuxCreatedSessions => _registry.TmuxCreatedSessions;
    private ConcurrentDictionary<string, byte> _tmuxCommandStarted => _registry.TmuxCommandStarted;
    private ConcurrentDictionary<string, byte> _hiddenSessions => _registry.HiddenSessions;
    private ConcurrentDictionary<string, string> _tmuxParentSessions => _registry.TmuxParentSessions;
    private ConcurrentDictionary<string, string> _bookmarkLinks => _registry.BookmarkLinks;
    private int _nextOrder
    {
        get => _registry.NextOrder;
        set => _registry.SetNextOrder(value);
    }

    public event Action<string, ulong, int, int, ReadOnlyMemory<byte>>? OnOutput;
    public event Action<string>? OnStateChanged;
    public event Action<string>? OnSessionClosed;
    public event Action<string, int>? OnSessionCreated;
    public event Action<string, ForegroundChangePayload>? OnForegroundChanged;
    public event Action<string, string>? OnCwdChanged;
    public event Action<string, TtyHostInputTraceReport>? OnInputTrace;
    public event Action<string, TtyHostDataLossPayload>? OnDataLoss;

    public TtyHostSessionManager(
        string? expectedVersion = null,
        string? minCompatibleVersion = null,
        string? runAsUser = null,
        string? runAsUserSid = null,
        bool isServiceMode = false,
        SessionControlStateService? sessionControlStateService = null,
        SessionLayoutStateService? sessionLayoutStateService = null,
        MidTermInstanceIdentity? instanceIdentity = null,
        SessionForegroundProcessService? foregroundProcessService = null,
        SettingsService? settingsService = null)
    {
        _registry = new SessionRegistry(isServiceMode, sessionControlStateService, sessionLayoutStateService);
        _clients = _registry.Clients;
        _sessionCache = _registry.SessionCache;
        _expectedTtyHostVersion = expectedVersion ?? TtyHostSpawner.GetTtyHostVersion();
        _minCompatibleVersion = minCompatibleVersion ?? GetMinCompatibleVersionFromManifest();
        _instanceIdentity = instanceIdentity ?? MidTermInstanceIdentity.Load(
            Path.Combine(Path.GetTempPath(), "midterm-test-instance", Guid.NewGuid().ToString("N")),
            0);
        _ownershipRegistry = new TtyHostOwnershipRegistry(_instanceIdentity.SessionRegistryPath);
        _foregroundProcessService = foregroundProcessService ?? new SessionForegroundProcessService();
        _settingsService = settingsService;
        _runAsUser = runAsUser;
        _runAsUserSid = runAsUserSid;
    }

    private static string? GetMinCompatibleVersionFromManifest()
    {
        try
        {
            return UpdateService.ReadInstalledManifest().MinCompatiblePty;
        }
        catch
        {
            // Fallback to permissive minimum to avoid killing sessions when manifest can't be read
            return FallbackMinCompatibleVersion;
        }
    }

    public void UpdateRunAsUser(string? runAsUser, string? runAsUserSid = null)
    {
        _runAsUser = runAsUser;
        _runAsUserSid = runAsUserSid;
        Log.Info(() => $"TtyHostSessionManager: RunAsUser updated to: {runAsUser ?? "(none)"}");
    }

    public void ConfigureTmux(int port, Func<string> generateToken, string? tmuxBinDir)
    {
        _mtPort = port;
        _generateToken = generateToken;
        _tmuxBinDir = tmuxBinDir;
    }

    /// <summary>
    /// Discover and connect to existing mmttyhost sessions.
    /// Kills incompatible or unresponsive processes, cleans up stale endpoints.
    /// </summary>
    public async Task DiscoverExistingSessionsAsync(CancellationToken ct = default)
    {
        Log.Info(() => $"TtyHostSessionManager: Discovering existing sessions for instance {_instanceIdentity.GetShortInstanceId()}...");

        var discoveredOrders = new List<int>();
        var ownedRecords = _ownershipRegistry.GetSessions();

        foreach (var ownedRecord in ownedRecords)
        {
            if (ct.IsCancellationRequested) break;
            if (_registry.Clients.ContainsKey(ownedRecord.SessionId)) continue;

            var result = await TryConnectToSessionAsync(
                ownedRecord.SessionId,
                ownedRecord.HostPid,
                ownedRecord.IsLegacyEndpoint,
                allowLegacyOwnerless: ownedRecord.IsLegacyEndpoint,
                ct).ConfigureAwait(false);

            HandleDiscoveryResult(ownedRecord.SessionId, ownedRecord.HostPid, ownedRecord.IsLegacyEndpoint, result, discoveredOrders);
        }

        var existingEndpoints = SessionEndpointDiscovery.GetExistingEndpoints(_instanceIdentity.InstanceId);
        Log.Info(() => $"TtyHostSessionManager: Found {existingEndpoints.Count} owned IPC endpoints");

        foreach (var (sessionId, hostPid) in existingEndpoints)
        {
            if (ct.IsCancellationRequested) break;
            if (_registry.Clients.ContainsKey(sessionId)) continue;

            var result = await TryConnectToSessionAsync(
                sessionId,
                hostPid,
                isLegacyEndpoint: false,
                allowLegacyOwnerless: false,
                ct).ConfigureAwait(false);
            HandleDiscoveryResult(sessionId, hostPid, legacyEndpoint: false, result, discoveredOrders);
        }

        if (ownedRecords.Count == 0 && _instanceIdentity.CanImportLegacySessions())
        {
            var legacyEndpoints = SessionEndpointDiscovery.GetLegacyEndpoints();
            Log.Info(() => $"TtyHostSessionManager: Importing {legacyEndpoints.Count} legacy IPC endpoints");

            foreach (var (sessionId, hostPid) in legacyEndpoints)
            {
                if (ct.IsCancellationRequested) break;
                if (_registry.Clients.ContainsKey(sessionId)) continue;

                var result = await TryConnectToSessionAsync(
                    sessionId,
                    hostPid,
                    isLegacyEndpoint: true,
                    allowLegacyOwnerless: true,
                    ct).ConfigureAwait(false);
                HandleDiscoveryResult(sessionId, hostPid, legacyEndpoint: true, result, discoveredOrders);
            }

            _instanceIdentity.MarkLegacySessionsImported();
        }

        // Set _nextOrder to max discovered + 1 to avoid collisions
        _registry.SetNextOrder(discoveredOrders.Count > 0 ? discoveredOrders.Max() + 1 : 0);

        Log.Info(() => string.Create(CultureInfo.InvariantCulture, $"TtyHostSessionManager: Discovered {_registry.ClientCount} active sessions, nextOrder={_registry.NextOrder}"));
    }

    private void HandleDiscoveryResult(
        string sessionId,
        int hostPid,
        bool legacyEndpoint,
        DiscoveryResult result,
        List<int> discoveredOrders)
    {
        switch (result)
        {
            case DiscoveryResult.Connected connected:
                discoveredOrders.Add(connected.Order);
                break;

            case DiscoveryResult.Incompatible incompatible:
                Log.Warn(() => string.Create(CultureInfo.InvariantCulture, $"TtyHostSessionManager: Session {sessionId} incompatible (v{incompatible.Version}), killing PID {hostPid}"));
                KillProcess(hostPid);
                SessionEndpointDiscovery.CleanupEndpoint(_instanceIdentity.InstanceId, sessionId, hostPid, legacyEndpoint);
                _ownershipRegistry.Remove(sessionId);
                break;

            case DiscoveryResult.Unresponsive:
                Log.Warn(() => string.Create(CultureInfo.InvariantCulture, $"TtyHostSessionManager: Session {sessionId} unresponsive, killing PID {hostPid}"));
                KillProcess(hostPid);
                SessionEndpointDiscovery.CleanupEndpoint(_instanceIdentity.InstanceId, sessionId, hostPid, legacyEndpoint);
                _ownershipRegistry.Remove(sessionId);
                break;

            case DiscoveryResult.NoProcess:
                Log.Warn(() => string.Create(CultureInfo.InvariantCulture, $"TtyHostSessionManager: Session {sessionId} has stale endpoint (PID {hostPid} not running)"));
                SessionEndpointDiscovery.CleanupEndpoint(_instanceIdentity.InstanceId, sessionId, hostPid, legacyEndpoint);
                _ownershipRegistry.Remove(sessionId);
                break;
        }
    }

    private async Task<DiscoveryResult> TryConnectToSessionAsync(
        string sessionId,
        int hostPid,
        bool isLegacyEndpoint,
        bool allowLegacyOwnerless,
        CancellationToken ct)
    {
        DiscoveryConnectResult connectResult;
        try
        {
            connectResult = await ConnectDiscoveredSessionAsync(
                sessionId,
                hostPid,
                isLegacyEndpoint,
                allowLegacyOwnerless,
                ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"TtyHostSessionManager: Failed to connect to {sessionId}: {ex.Message}");
            Log.Exception(ex, $"TtyHostSessionManager.TryConnect({sessionId})");
            return new DiscoveryResult.Unresponsive();
        }

        switch (connectResult)
        {
            case DiscoveryConnectResult.NoProcess:
                return new DiscoveryResult.NoProcess();

            case DiscoveryConnectResult.Unresponsive:
                return new DiscoveryResult.Unresponsive();

            case DiscoveryConnectResult.Incompatible incompatible:
                return new DiscoveryResult.Incompatible(hostPid, incompatible.Version);

            case DiscoveryConnectResult.Connected connected:
                _registry.Clients[sessionId] = connected.Client;
                SubscribeToClient(connected.Client);
                connected.Client.StartReadLoop();
                _registry.SessionCache[sessionId] = connected.Info;
                ApplyDiscoveredHostMetadata(connected.Info);
                _transportState.TryAdd(sessionId, new TerminalTransportRuntimeState());
                _ownershipRegistry.Upsert(sessionId, hostPid, isLegacyEndpoint || string.IsNullOrWhiteSpace(connected.Info.OwnerInstanceId));

                var order = connected.Info.Order;
                _registry.SessionOrder.TryAdd(sessionId, order);
                OnSessionCreated?.Invoke(sessionId, order);
                Log.Info(() => string.Create(CultureInfo.InvariantCulture, $"TtyHostSessionManager: Reconnected to session {sessionId} (PID {hostPid}, order={order})"));

                return new DiscoveryResult.Connected(order);

            default:
                throw new InvalidOperationException("Unexpected discovery connect result.");
        }
    }

    private async Task<DiscoveryConnectResult> ConnectDiscoveredSessionAsync(
        string sessionId,
        int hostPid,
        bool isLegacyEndpoint,
        bool allowLegacyOwnerless,
        CancellationToken ct)
    {
        TtyHostClient? ownedClient = null;

        try
        {
            ownedClient = new TtyHostClient(sessionId, hostPid, _instanceIdentity.InstanceId, _instanceIdentity.OwnerToken, useLegacyEndpoint: isLegacyEndpoint);

            if (!await ownedClient.ConnectAsync(1500, ct: ct).ConfigureAwait(false))
            {
                return DiscoveryConnectResult.NoProcess.Instance;
            }

            var info = await ownedClient.GetInfoAsync(ct).ConfigureAwait(false);
            if (info is null)
            {
                return DiscoveryConnectResult.Unresponsive.Instance;
            }

            if (!string.IsNullOrWhiteSpace(info.OwnerInstanceId) &&
                !string.Equals(info.OwnerInstanceId, _instanceIdentity.InstanceId, StringComparison.Ordinal))
            {
                return DiscoveryConnectResult.Unresponsive.Instance;
            }

            if (string.IsNullOrWhiteSpace(info.OwnerInstanceId) && !allowLegacyOwnerless)
            {
                return DiscoveryConnectResult.Unresponsive.Instance;
            }

            if (!IsVersionCompatible(info.TtyHostVersion))
            {
                return new DiscoveryConnectResult.Incompatible(info.TtyHostVersion);
            }

            return DiscoveryConnectResult.Connected.CreateOwned(ref ownedClient, info);
        }
        finally
        {
            if (ownedClient is not null)
            {
                await ownedClient.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private bool IsVersionCompatible(string? conHostVersion)
    {
        if (string.IsNullOrEmpty(conHostVersion)) return false;
        if (conHostVersion == _expectedTtyHostVersion) return true;
        if (_minCompatibleVersion is null) return false;

        return UpdateService.CompareVersions(conHostVersion, _minCompatibleVersion) >= 0;
    }
    private abstract record DiscoveryResult
    {
        public sealed record Connected(int Order) : DiscoveryResult;
        public sealed record Incompatible(int HostPid, string? Version) : DiscoveryResult;
        public sealed record Unresponsive() : DiscoveryResult;
        public sealed record NoProcess() : DiscoveryResult;
    }

    private abstract record DiscoveryConnectResult
    {
        public sealed record Connected(TtyHostClient Client, SessionInfo Info) : DiscoveryConnectResult
        {
            public static Connected CreateOwned(ref TtyHostClient? client, SessionInfo info)
            {
                var ownedClient = client ?? throw new ArgumentNullException(nameof(client));
                client = null;
                return new Connected(ownedClient, info);
            }
        }
        public sealed record Incompatible(string? Version) : DiscoveryConnectResult;

        public sealed record Unresponsive : DiscoveryConnectResult
        {
            public static Unresponsive Instance { get; } = new();
        }

        public sealed record NoProcess : DiscoveryConnectResult
        {
            public static NoProcess Instance { get; } = new();
        }
    }

    public Task<SessionInfo?> CreateSessionAsync(
        string? shellType,
        int cols,
        int rows,
        string? workingDirectory,
        CancellationToken ct = default)
    {
        return CreateSessionAsync(
            shellType,
            cols,
            rows,
            workingDirectory,
            applyTerminalEnvironmentVariables: true,
            ct);
    }

    internal async Task<SessionInfo?> CreateSessionAsync(
        string? shellType,
        int cols,
        int rows,
        string? workingDirectory,
        bool applyTerminalEnvironmentVariables,
        CancellationToken ct = default)
    {
        return (await CreateSessionDetailedAsync(
            shellType,
            cols,
            rows,
            workingDirectory,
            applyTerminalEnvironmentVariables,
            ct).ConfigureAwait(false)).Session;
    }

    internal Task<SessionCreationResult> CreateSessionDetailedAsync(
        string? shellType,
        int cols,
        int rows,
        string? workingDirectory,
        CancellationToken ct = default)
    {
        return CreateSessionDetailedAsync(
            shellType,
            cols,
            rows,
            workingDirectory,
            applyTerminalEnvironmentVariables: true,
            ct);
    }

    internal async Task<SessionCreationResult> CreateSessionDetailedAsync(
        string? shellType,
        int cols,
        int rows,
        string? workingDirectory,
        bool applyTerminalEnvironmentVariables,
        CancellationToken ct = default)
    {
        var creationTimer = Stopwatch.StartNew();
        if (_registry.SessionCount >= MaxSessions)
        {
            Log.Warn(() => $"Session limit reached ({MaxSessions})");
            return SessionCreationResult.Failed(new SessionLaunchFailure(
                "limits",
                $"Session limit reached ({MaxSessions}).",
                "MidTerm refused to create a new session because the maximum number of live sessions is already in use. Close an existing session and try again."));
        }

        var sessionId = Guid.NewGuid().ToString("N")[..SessionIdLength];

        var paneIndex = _registry.ReserveNextOrder();
        var mtToken = _generateToken?.Invoke();
        var scrollbackBytes = ResolveScrollbackBytes();
        var terminalEnvironmentOverrides = ResolveTerminalEnvironmentOverrides(applyTerminalEnvironmentVariables);
        var spawnResult = TtyHostSpawner.SpawnTtyHost(
            sessionId,
            shellType,
            workingDirectory,
            cols,
            rows,
            _instanceIdentity.InstanceId,
            _instanceIdentity.OwnerToken,
            _runAsUser,
            _runAsUserSid,
            scrollbackBytes,
            terminalEnvironmentOverrides,
            _mtPort,
            mtToken,
            paneIndex,
            _tmuxBinDir);
        if (!spawnResult.Succeeded)
        {
            return SessionCreationResult.Failed(spawnResult.Failure!);
        }

        var hostPid = spawnResult.ProcessId;
        var spawnElapsedMs = creationTimer.ElapsedMilliseconds;
        var connectPid = hostPid;
        if (!OperatingSystem.IsWindows())
        {
            // When using sudo -u on Unix, the returned PID is sudo's PID rather than mthost's.
            // Probe briefly for the real endpoint before attempting IPC.
            await Task.Delay(50, ct).ConfigureAwait(false);
            int? actualPid = null;
            for (var wait = 50; wait < 500; wait *= 2)
            {
                if (SessionEndpointDiscovery.EndpointExists(_instanceIdentity.InstanceId, sessionId, hostPid))
                {
                    actualPid = hostPid;
                    break;
                }

                actualPid = SessionEndpointDiscovery.FindEndpointPid(_instanceIdentity.InstanceId, sessionId);
                if (actualPid is not null)
                {
                    break;
                }

                await Task.Delay(wait, ct).ConfigureAwait(false);
            }

            connectPid = actualPid ?? hostPid;
        }

        // Windows gets the real mthost PID from CreateProcess/CreateProcessAsUser, so there is
        // no need for the endpoint probe loop above on the hot create-session path.
        // Connect to the new session using sessionId + actual PID for endpoint.
        var client = new TtyHostClient(sessionId, connectPid, _instanceIdentity.InstanceId, _instanceIdentity.OwnerToken);
        var connected = false;
        var connectTimer = Stopwatch.StartNew();
        var connectAttempts = OperatingSystem.IsWindows() ? 20 : 10;
        var connectTimeoutMs = OperatingSystem.IsWindows() ? 150 : 1000;
        var retryDelayMs = OperatingSystem.IsWindows() ? 50 : 200;

        for (var attempt = 0; attempt < connectAttempts && !connected; attempt++)
        {
            connected = await client.ConnectAsync(connectTimeoutMs, maxAttempts: 1, ct: ct).ConfigureAwait(false);
            if (!connected && attempt + 1 < connectAttempts)
            {
                if (!IsProcessRunning(connectPid))
                {
                    break;
                }

                await Task.Delay(retryDelayMs, ct).ConfigureAwait(false);
            }
        }

        if (!connected)
        {
            var failedConnectElapsedMs = connectTimer.ElapsedMilliseconds;
            var processRunning = IsProcessRunning(connectPid);
            var processDescription = DescribeSpawnedProcesses(hostPid, connectPid);
            Log.Error(() =>
                string.Create(CultureInfo.InvariantCulture, $"TtyHostSessionManager: Failed to connect to new session {sessionId}, killing orphan process(es) [{processDescription}] after {creationTimer.ElapsedMilliseconds} ms (spawn={spawnElapsedMs} ms, connect={failedConnectElapsedMs} ms)"));
            await CleanupFailedSessionCreationAsync(sessionId, hostPid, connectPid).ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
            return SessionCreationResult.Failed(new SessionLaunchFailure(
                "connect",
                processRunning
                    ? "The terminal host did not open its IPC channel in time."
                    : "The terminal host exited before MidTerm could attach to it.",
                string.Create(CultureInfo.InvariantCulture, $"MidTerm launched mthost ({processDescription}) but could not complete IPC attach after {failedConnectElapsedMs} ms."),
                processRunning ? null : "ProcessExited"));
        }

        var connectElapsedMs = connectTimer.ElapsedMilliseconds;
        var infoTimer = Stopwatch.StartNew();
        var info = await client.GetInfoAsync(ct).ConfigureAwait(false);
        if (info is null)
        {
            var failedInfoElapsedMs = infoTimer.ElapsedMilliseconds;
            var processDescription = DescribeSpawnedProcesses(hostPid, connectPid);
            Log.Error(() =>
                string.Create(CultureInfo.InvariantCulture, $"TtyHostSessionManager: Failed to get info for session {sessionId}, killing orphan process(es) [{processDescription}] after {creationTimer.ElapsedMilliseconds} ms (spawn={spawnElapsedMs} ms, connect={connectElapsedMs} ms, info={failedInfoElapsedMs} ms)"));
            await CleanupFailedSessionCreationAsync(sessionId, hostPid, connectPid).ConfigureAwait(false);
            await client.DisposeAsync().ConfigureAwait(false);
            return SessionCreationResult.Failed(new SessionLaunchFailure(
                "handshake",
                "The terminal host connected but never returned session metadata.",
                string.Create(CultureInfo.InvariantCulture, $"MidTerm established IPC to mthost ({processDescription}) but GetInfo returned no data after {failedInfoElapsedMs} ms.")));
        }

        info.TerminalTitle = NormalizeTerminalTitle(info, info.TerminalTitle);
        ApplyStartupWorkingDirectoryFallback(info, workingDirectory);

        // Start read loop after handshake completes (avoids race condition with GetInfoAsync)
        SubscribeToClient(client);
        client.StartReadLoop();
        _registry.Clients[sessionId] = client;
        _registry.SessionCache[sessionId] = info;
        _transportState[sessionId] = new TerminalTransportRuntimeState();
        _registry.SessionOrder[sessionId] = paneIndex;
        _ownershipRegistry.Upsert(sessionId, connectPid, isLegacyEndpoint: false);

        await client.SetOrderAsync((byte)(paneIndex % 256), ct).ConfigureAwait(false);

        var infoElapsedMs = infoTimer.ElapsedMilliseconds;
        Log.Info(() =>
            string.Create(CultureInfo.InvariantCulture, $"TtyHostSessionManager: Created session {sessionId} (PID {connectPid}) in {creationTimer.ElapsedMilliseconds} ms [spawn={spawnElapsedMs} ms, connect={connectElapsedMs} ms, info={infoElapsedMs} ms]"));
        OnSessionCreated?.Invoke(sessionId, paneIndex);
        OnStateChanged?.Invoke(sessionId);
        NotifyStateChange();

        return SessionCreationResult.Success(info);
    }

    public SessionInfo? GetSession(string sessionId)
    {
        return _registry.GetSession(sessionId);
    }

    private int ResolveScrollbackBytes()
    {
        var configured = _settingsService?.Load().ScrollbackBytes ?? MidTermSettings.DefaultScrollbackBytes;
        return Math.Clamp(
            configured,
            MidTermSettings.MinScrollbackBytes,
            MidTermSettings.MaxScrollbackBytes);
    }

    private IReadOnlyDictionary<string, string?>? ResolveTerminalEnvironmentOverrides(
        bool applyTerminalEnvironmentVariables)
    {
        if (!applyTerminalEnvironmentVariables)
        {
            return null;
        }

        var configured = _settingsService?.Load().TerminalEnvironmentVariables;
        return TerminalEnvironmentVariableParser.Parse(configured);
    }

    public void MarkTmuxCreated(string sessionId)
    {
        _registry.MarkTmuxCreated(sessionId);
    }

    public void SetTmuxParent(string childSessionId, string parentSessionId)
    {
        _registry.SetTmuxParent(childSessionId, parentSessionId);
    }

    public async Task<SessionInfo?> GetSessionFreshAsync(string sessionId, CancellationToken ct = default)
    {
        if (!_clients.ContainsKey(sessionId))
        {
            return null;
        }

        var resizeGate = GetResizeGate(sessionId);
        await resizeGate.WaitAsync(ct).ConfigureAwait(false);
        var removeResizeGate = false;
        try
        {
            if (!_clients.TryGetValue(sessionId, out var client))
            {
                removeResizeGate = true;
                return null;
            }

            var info = await client.GetInfoAsync(ct).ConfigureAwait(false);
            if (info is not null)
            {
                CacheRefreshedSessionInfo(sessionId, info);
            }
            return info;
        }
        finally
        {
            resizeGate.Release();
            if (removeResizeGate && !_clients.ContainsKey(sessionId))
            {
                TryRemoveResizeGate(sessionId, resizeGate);
            }
        }
    }

    private void ApplyDiscoveredHostMetadata(SessionInfo info)
    {
        if (!string.IsNullOrWhiteSpace(info.Topic))
        {
            _registry.SetSessionTopic(info.Id, info.Topic);
        }
    }

    private async Task PersistHostMetadataAsync(string sessionId)
    {
        if (!_clients.TryGetValue(sessionId, out var client))
        {
            return;
        }

        if (!_sessionCache.TryGetValue(sessionId, out var info))
        {
            return;
        }

        try
        {
            var metadata = new TtyHostSessionMetadata
            {
                Topic = _registry.SessionTopics.TryGetValue(sessionId, out var topic) ? topic : null,
                ExtraGitRepos = info.ExtraGitRepos
            };

            await client.SetMetadataAsync(metadata).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            Log.Verbose(() => $"TtyHostSessionManager: Failed to persist host metadata for {sessionId}: {ex.Message}");
        }
    }

    public async Task<bool> SetClipboardImageAsync(
        string sessionId,
        string filePath,
        string? mimeType,
        CancellationToken ct = default)
    {
        if (!_clients.TryGetValue(sessionId, out var client))
        {
            return false;
        }

        return await client.SetClipboardImageAsync(filePath, mimeType, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Get or create the temp directory for file uploads for a session.
    /// </summary>
    public string GetTempDirectory(string sessionId)
    {
        return _registry.GetTempDirectory(sessionId);
    }

    private void CleanupTempDirectory(string sessionId)
    {
        _registry.CleanupTempDirectory(sessionId);
    }

    public void MarkHidden(string sessionId)
    {
        _registry.MarkHidden(sessionId);
    }

    public bool IsHidden(string sessionId)
    {
        return _registry.IsHidden(sessionId);
    }

    public IReadOnlyList<SessionInfo> GetAllSessions()
    {
        return _registry.GetAllSessions();
    }

    public SessionListDto GetSessionList(bool includeHidden = false)
    {
        var response = _registry.GetSessionList(includeHidden);
        foreach (var session in response.Sessions)
        {
            var descriptor = _foregroundProcessService.Describe(
                session.ForegroundName,
                session.ForegroundCommandLine,
                session.AgentAttachPoint);
            session.ForegroundDisplayName = string.IsNullOrWhiteSpace(descriptor.DisplayName) ? null : descriptor.DisplayName;
            session.ForegroundProcessIdentity = string.IsNullOrWhiteSpace(descriptor.ProcessIdentity) ? null : descriptor.ProcessIdentity;
        }

        return response;
    }

    public async Task<bool> CloseSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (!_clients.ContainsKey(sessionId))
        {
            return false;
        }

        var resizeGate = GetResizeGate(sessionId);
        await resizeGate.WaitAsync(ct).ConfigureAwait(false);
        var closed = false;
        try
        {
            closed = await CloseSessionCoreAsync(sessionId, ct).ConfigureAwait(false);
            return closed;
        }
        finally
        {
            resizeGate.Release();
            if (closed || !_clients.ContainsKey(sessionId))
            {
                TryRemoveResizeGate(sessionId, resizeGate);
            }
        }
    }

    private async Task<bool> CloseSessionCoreAsync(string sessionId, CancellationToken ct)
    {
        if (!_clients.TryRemove(sessionId, out var client))
        {
            return false;
        }

        _registry.RemoveSessionState(sessionId);
        _ownershipRegistry.Remove(sessionId);
        _transportState.TryRemove(sessionId, out _);
        _redrawDimensionOverrides.TryRemove(sessionId, out _);

        await client.CloseAsync(ct).ConfigureAwait(false);
        await client.DisposeAsync().ConfigureAwait(false);
        TtyHostSpawner.CleanupMacOsGuiLaunchAgent(sessionId);

        OnSessionClosed?.Invoke(sessionId);
        OnStateChanged?.Invoke(sessionId);
        NotifyStateChange();
        return true;
    }

    public async Task<bool> ResizeSessionAsync(string sessionId, int cols, int rows, CancellationToken ct = default)
    {
        if (!_clients.ContainsKey(sessionId))
        {
            return false;
        }

        var resizeGate = GetResizeGate(sessionId);
        await resizeGate.WaitAsync(ct).ConfigureAwait(false);
        var removeResizeGate = false;
        try
        {
            if (!_clients.TryGetValue(sessionId, out var client))
            {
                removeResizeGate = true;
                return false;
            }

            var success = await client.ResizeAsync(cols, rows, ct).ConfigureAwait(false);

            if (success && _sessionCache.TryGetValue(sessionId, out var info))
            {
                info.Cols = cols;
                info.Rows = rows;
                OnStateChanged?.Invoke(sessionId);
                NotifyStateChange();
            }

            return success;
        }
        finally
        {
            resizeGate.Release();
            if (removeResizeGate && !_clients.ContainsKey(sessionId))
            {
                TryRemoveResizeGate(sessionId, resizeGate);
            }
        }
    }

    /// <summary>
    /// Requests a deterministic foreground redraw without changing the browser-visible terminal geometry.
    /// The one-row pulse makes full-screen console applications repaint their own model, then restores the
    /// canonical dimensions before releasing the per-session resize gate.
    /// </summary>
    public async Task<bool> RedrawSessionAsync(string sessionId, CancellationToken ct = default)
    {
        if (!_clients.ContainsKey(sessionId) || !_sessionCache.ContainsKey(sessionId))
        {
            return false;
        }

        var resizeGate = GetResizeGate(sessionId);
        await resizeGate.WaitAsync(ct).ConfigureAwait(false);
        var removeResizeGate = false;
        try
        {
            if (!_clients.TryGetValue(sessionId, out var client) ||
                !_sessionCache.TryGetValue(sessionId, out var info) ||
                info.Cols <= 0 ||
                info.Rows <= 0)
            {
                removeResizeGate = !_clients.ContainsKey(sessionId);
                return false;
            }

            var canonicalDimensions = new TerminalDimensions(info.Cols, info.Rows);
            _redrawDimensionOverrides[sessionId] = canonicalDimensions;
            try
            {
                return await ExecuteRedrawPulseAsync(
                    canonicalDimensions.Cols,
                    canonicalDimensions.Rows,
                    client.ResizeAsync,
                    ct).ConfigureAwait(false);
            }
            finally
            {
                _redrawDimensionOverrides.TryRemove(sessionId, out _);
            }
        }
        finally
        {
            resizeGate.Release();
            if (removeResizeGate && !_clients.ContainsKey(sessionId))
            {
                TryRemoveResizeGate(sessionId, resizeGate);
            }
        }
    }

    private SemaphoreSlim GetResizeGate(string sessionId) =>
        _resizeGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));

    private bool TryRemoveResizeGate(string sessionId, SemaphoreSlim resizeGate) =>
        ((ICollection<KeyValuePair<string, SemaphoreSlim>>)_resizeGates)
            .Remove(new KeyValuePair<string, SemaphoreSlim>(sessionId, resizeGate));

    internal static async Task<bool> ExecuteRedrawPulseAsync(
        int cols,
        int rows,
        Func<int, int, CancellationToken, Task<bool>> resizeAsync,
        CancellationToken ct)
    {
        var pulseRows = rows < MaximumTerminalRows ? rows + 1 : rows - 1;
        if (cols <= 0 || rows <= 0 || pulseRows <= 0 || pulseRows == rows)
        {
            return false;
        }

        var pulseAccepted = false;
        var restoreAccepted = false;
        try
        {
            pulseAccepted = await resizeAsync(cols, pulseRows, ct).ConfigureAwait(false);
        }
        finally
        {
            // A canceled request may have reached mthost before its acknowledgement was observed.
            // Briefly allow the foreground application to observe the intermediate size,
            // then restore with an independent token so the PTY cannot remain at pulse geometry.
            await Task.Delay(TimeSpan.FromMilliseconds(25), CancellationToken.None).ConfigureAwait(false);
            restoreAccepted = await resizeAsync(cols, rows, CancellationToken.None).ConfigureAwait(false);
        }

        return pulseAccepted && restoreAccepted;
    }

    public async Task SendInputAsync(string sessionId, ReadOnlyMemory<byte> data, CancellationToken ct = default)
    {
        if (_clients.TryGetValue(sessionId, out var client))
        {
            await client.SendInputAsync(data, ct).ConfigureAwait(false);
        }
    }

    public async Task<TtyHostInputWriteTiming?> SendInputWithTraceAsync(
        string sessionId,
        ReadOnlyMemory<byte> data,
        uint traceId,
        CancellationToken ct = default)
    {
        if (!_clients.TryGetValue(sessionId, out var client))
        {
            return null;
        }

        return await client.SendInputWithTraceAsync(data, traceId, ct).ConfigureAwait(false);
    }

    public async Task<byte[]?> PingAsync(string sessionId, byte[] pingData, CancellationToken ct = default)
    {
        if (!_clients.TryGetValue(sessionId, out var client))
        {
            return null;
        }
        return await client.PingAsync(pingData, ct).ConfigureAwait(false);
    }

    public async Task<TtyHostBufferSnapshot?> GetBufferAsync(
        string sessionId,
        int? maxBytes = null,
        TerminalReplayReason reason = TerminalReplayReason.Manual,
        ulong? sinceSequence = null,
        CancellationToken ct = default)
    {
        if (!_clients.TryGetValue(sessionId, out var client))
        {
            return null;
        }

        var snapshot = await client.GetBufferAsync(maxBytes, reason, sinceSequence, ct).ConfigureAwait(false);
        if (snapshot is not null)
        {
            var state = _transportState.GetOrAdd(sessionId, static _ => new TerminalTransportRuntimeState());
            state.SourceSeq = snapshot.SequenceStart + (ulong)snapshot.Data.Length;
            state.MuxReceivedSeq = state.SourceSeq;
            state.LastReplayBytes = snapshot.Data.Length;
            state.LastReplayReason = reason;
        }

        return snapshot;
    }

    public TerminalTransportRuntimeSnapshot GetTransportRuntimeSnapshot(string sessionId)
    {
        if (!_transportState.TryGetValue(sessionId, out var state))
        {
            return new TerminalTransportRuntimeSnapshot();
        }

        return state.ToSnapshot();
    }

    public async Task<bool> SetSessionNameAsync(string sessionId, string? name, bool isManual = true, CancellationToken ct = default)
    {
        if (!_sessionCache.TryGetValue(sessionId, out var info))
        {
            return false;
        }

        if (isManual)
        {
            if (!_clients.TryGetValue(sessionId, out var client))
            {
                return false;
            }

            // User-set name: store in Name field and send to mthost
            info.ManuallyNamed = !string.IsNullOrWhiteSpace(name);
            var success = await client.SetNameAsync(name, ct).ConfigureAwait(false);
            if (success)
            {
                info.Name = string.IsNullOrWhiteSpace(name) ? null : name;
                OnStateChanged?.Invoke(sessionId);
                NotifyStateChange();
            }
            return success;
        }
        else
        {
            // Terminal-reported title: store in TerminalTitle field (local only, no IPC)
            var terminalTitle = NormalizeTerminalTitle(info, name);
            if (string.Equals(info.TerminalTitle, terminalTitle, StringComparison.Ordinal))
            {
                return true;
            }

            info.TerminalTitle = terminalTitle;
            OnStateChanged?.Invoke(sessionId);
            NotifyStateChange();
            return true;
        }
    }

    public bool SetBookmarkId(string sessionId, string bookmarkId)
    {
        return _registry.SetBookmarkId(sessionId, bookmarkId);
    }

    public bool SetSessionNotes(string sessionId, string? notes)
    {
        return _registry.SetSessionNotes(sessionId, notes);
    }

    public bool SetSessionTopic(string sessionId, string? topic)
    {
        var updated = _registry.SetSessionTopic(sessionId, topic);
        if (updated)
        {
            _ = PersistHostMetadataAsync(sessionId);
        }

        return updated;
    }

    public bool SetSessionExtraGitReposMetadata(string sessionId, IEnumerable<GitRepoBinding> repos)
    {
        if (!_sessionCache.TryGetValue(sessionId, out var info))
        {
            return false;
        }

        info.ExtraGitRepos = repos
            .Where(static repo => !repo.IsPrimary && !string.IsNullOrWhiteSpace(repo.RepoRoot))
            .Select(static repo => new TtyHostGitRepoMetadata
            {
                RepoRoot = repo.RepoRoot,
                Label = repo.Label,
                Role = repo.Role,
                Source = repo.Source
            })
            .ToArray();

        _ = PersistHostMetadataAsync(sessionId);
        return true;
    }

    public TtyHostGitRepoMetadata[] GetPersistedSessionExtraGitRepos(string sessionId)
    {
        return _sessionCache.TryGetValue(sessionId, out var info)
            ? info.ExtraGitRepos
            : [];
    }

    public bool SetAgentControlled(string sessionId, bool agentControlled)
    {
        return _registry.SetAgentControlled(sessionId, agentControlled);
    }

    public bool SetAppServerControlOnly(string sessionId, bool appServerControlOnly)
    {
        return _registry.SetAppServerControlOnly(sessionId, appServerControlOnly);
    }

    public bool SetProfileHint(string sessionId, string? profile)
    {
        return _registry.SetProfileHint(sessionId, profile);
    }

    public bool SetLaunchOrigin(string sessionId, string? launchOrigin)
    {
        return _registry.SetLaunchOrigin(sessionId, launchOrigin);
    }

    public string? GetLaunchOrigin(string sessionId)
    {
        return _registry.GetLaunchOrigin(sessionId);
    }

    public bool SetAppServerControlResumeThreadId(string sessionId, string? resumeThreadId)
    {
        return _registry.SetAppServerControlResumeThreadId(sessionId, resumeThreadId);
    }

    public bool SetSpaceId(string sessionId, string? spaceId)
    {
        return _registry.SetSpaceId(sessionId, spaceId);
    }

    public bool SetWorkspacePath(string sessionId, string? workspacePath)
    {
        return _registry.SetWorkspacePath(sessionId, workspacePath);
    }

    public bool SetSurface(string sessionId, string? surface)
    {
        return _registry.SetSurface(sessionId, surface);
    }

    public int ClearBookmarksByHistoryId(string bookmarkId)
    {
        return _registry.ClearBookmarksByHistoryId(bookmarkId);
    }

    private static void ApplyStartupWorkingDirectoryFallback(SessionInfo info, string? workingDirectory)
    {
        if (!string.IsNullOrWhiteSpace(info.CurrentDirectory) ||
            string.IsNullOrWhiteSpace(workingDirectory))
        {
            return;
        }

        info.CurrentDirectory = workingDirectory.Trim();
    }

    public bool ReorderSessions(IList<string> sessionIds)
    {
        if (!_registry.ReorderSessions(sessionIds))
        {
            return false;
        }

        _ = SendOrderUpdatesAsync(sessionIds).ContinueWith(
            t => Log.Exception(t.Exception!.InnerException!, "TtyHostSessionManager.SendOrderUpdates"),
            TaskContinuationOptions.OnlyOnFaulted);

        return true;
    }

    private async Task SendOrderUpdatesAsync(IList<string> sessionIds)
    {
        for (var i = 0; i < sessionIds.Count; i++)
        {
            var id = sessionIds[i];
            if (_clients.TryGetValue(id, out var client))
            {
                try
                {
                    await client.SetOrderAsync((byte)i).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    Log.Warn(() => $"Failed to persist order for {id}: {ex.Message}");
                }
            }
        }
    }

    public string AddStateListener(Action callback)
    {
        return _registry.AddStateListener(callback);
    }

    public void RemoveStateListener(string id)
    {
        _registry.RemoveStateListener(id);
    }

    private void NotifyStateChange()
    {
        _registry.NotifyStateChange();
    }

    private void SubscribeToClient(TtyHostClient client)
    {
        client.OnOutput += HandleClientOutput;
        client.OnForegroundChanged += HandleClientForegroundChanged;
        client.OnStateChanged += id => _ = HandleClientStateChangedAsync(id);
        client.OnReconnected += HandleClientReconnected;
        client.OnDataLoss += HandleClientDataLoss;
        client.OnInputTrace += HandleClientInputTrace;
    }

    private void HandleClientOutput(string sessionId, ulong sequenceStart, int cols, int rows, ReadOnlyMemory<byte> data)
    {
        if (_redrawDimensionOverrides.TryGetValue(sessionId, out var canonicalDimensions))
        {
            cols = canonicalDimensions.Cols;
            rows = canonicalDimensions.Rows;
        }

        var state = _transportState.GetOrAdd(sessionId, static _ => new TerminalTransportRuntimeState());
        state.SourceSeq = sequenceStart + (ulong)data.Length;
        state.MuxReceivedSeq = state.SourceSeq;
        OnOutput?.Invoke(sessionId, sequenceStart, cols, rows, data);
        ScanForOscSequences(sessionId, data.Span);
    }

    private void HandleClientReconnected(string sessionId)
    {
        var state = _transportState.GetOrAdd(sessionId, static _ => new TerminalTransportRuntimeState());
        state.ReconnectCount++;
    }

    private void HandleClientDataLoss(string sessionId, TtyHostDataLossPayload payload)
    {
        var state = _transportState.GetOrAdd(sessionId, static _ => new TerminalTransportRuntimeState());
        state.DataLossCount++;
        state.LastDataLossReason = payload.Reason;
        OnDataLoss?.Invoke(sessionId, payload);
    }

    private void HandleClientInputTrace(string sessionId, TtyHostInputTraceReport report)
    {
        OnInputTrace?.Invoke(sessionId, report);
    }

    /// <summary>
    /// Scan terminal output for OSC sequences we care about:
    /// - OSC 0/2: Window title (ESC ] 0 ; title BEL)
    /// - OSC 7: CWD reporting (ESC ] 7 ; file://host/path BEL)
    /// </summary>
    private static ReadOnlySpan<byte> EscOsc => [0x1B, 0x5D];

    private void ScanForOscSequences(string sessionId, ReadOnlySpan<byte> data)
    {
        // Quick check: does the data contain ESC ] at all?
        if (data.IndexOf(EscOsc) < 0) return;

        var pos = 0;
        var changed = false;

        while (pos < data.Length - 2)
        {
            // Find next ESC ]
            var idx = data[pos..].IndexOf(EscOsc);
            if (idx < 0) break;
            idx += pos;

            // Read the OSC number and semicolon
            var numStart = idx + 2;
            if (numStart >= data.Length) break;

            // Parse single-digit OSC number followed by ;
            var oscNum = -1;
            var payloadStart = -1;
            if (numStart + 1 < data.Length && data[numStart] >= (byte)'0' && data[numStart] <= (byte)'9' && data[numStart + 1] == (byte)';')
            {
                oscNum = data[numStart] - '0';
                payloadStart = numStart + 2;
            }

            if (oscNum < 0 || payloadStart >= data.Length)
            {
                pos = numStart;
                continue;
            }

            // Find terminator: BEL (0x07) or ST (ESC \ = 0x1B 0x5C)
            var end = -1;
            for (var i = payloadStart; i < data.Length; i++)
            {
                if (data[i] == 0x07) { end = i; break; }
                if (data[i] == 0x1B && i + 1 < data.Length && data[i + 1] == 0x5C) { end = i; break; }
            }
            if (end <= payloadStart)
            {
                pos = payloadStart;
                continue;
            }

            var payload = System.Text.Encoding.UTF8.GetString(data[payloadStart..end]);

            switch (oscNum)
            {
                case 0:
                case 2:
                    changed |= HandleOscTitle(sessionId, payload);
                    break;
                case 7:
                    changed |= HandleOscCwdUpdate(sessionId, payload);
                    break;
            }

            pos = end + 1;
        }

        if (changed)
        {
            NotifyStateChange();
        }
    }

    private bool HandleOscTitle(string sessionId, string title)
    {
        if (!_sessionCache.TryGetValue(sessionId, out var info)) return false;
        var trimmed = NormalizeTerminalTitle(info, title);
        if (string.Equals(info.TerminalTitle, trimmed, StringComparison.Ordinal)) return false;
        info.TerminalTitle = trimmed;
        return true;
    }

    private static string? NormalizeTerminalTitle(SessionInfo session, string? title)
    {
        var trimmed = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        if (trimmed is null)
        {
            return null;
        }

        var normalizedTitle = NormalizeExecutableIdentity(trimmed);
        var normalizedShell = NormalizeExecutableIdentity(session.ShellType);
        if (!string.IsNullOrEmpty(normalizedTitle) &&
            !string.IsNullOrEmpty(normalizedShell) &&
            string.Equals(normalizedTitle, normalizedShell, StringComparison.Ordinal))
        {
            return null;
        }

        return trimmed;
    }

    private static string NormalizeExecutableIdentity(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var candidate = value.Trim();
        var firstChar = candidate[0];
        if ((firstChar == '"' || firstChar == '\'') && candidate.Length > 1)
        {
            var closingQuote = candidate.IndexOf(firstChar, 1);
            if (closingQuote > 1)
            {
                candidate = candidate[1..closingQuote];
            }
        }

        candidate = candidate.Replace('\\', '/');
        var basename = candidate.Split('/').LastOrDefault() ?? candidate;
        var token = basename.Trim().Split(' ', '\t').FirstOrDefault() ?? basename.Trim();
        return token.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? token[..^4].ToLowerInvariant()
            : token.ToLowerInvariant();
    }

    private bool HandleOscCwdUpdate(string sessionId, string payload)
    {
        if (!_sessionCache.TryGetValue(sessionId, out var info)) return false;

        if (!payload.StartsWith("file://", StringComparison.OrdinalIgnoreCase)) return false;
        var pathStart = payload.IndexOf('/', 7);
        if (pathStart < 0) return false;

        var path = Uri.UnescapeDataString(payload[pathStart..]);

        // Windows: /C:/foo → C:\foo
        if (path.Length >= 3 && path[0] == '/' && char.IsLetter(path[1]) && path[2] == ':')
        {
            path = path[1..].Replace('/', '\\');
        }

        if (string.IsNullOrWhiteSpace(path)) return false;
        if (string.Equals(info.CurrentDirectory, path, StringComparison.OrdinalIgnoreCase)) return false;
        info.CurrentDirectory = path;
        OnCwdChanged?.Invoke(sessionId, path);
        return true;
    }

    private void HandleClientForegroundChanged(string sessionId, ForegroundChangePayload payload)
    {
        var descriptor = _foregroundProcessService.Describe(payload.Name, payload.CommandLine, payload.AgentAttachPoint);
        payload.DisplayName = string.IsNullOrWhiteSpace(descriptor.DisplayName) ? null : descriptor.DisplayName;
        payload.ProcessIdentity = string.IsNullOrWhiteSpace(descriptor.ProcessIdentity) ? null : descriptor.ProcessIdentity;

        if (_sessionCache.TryGetValue(sessionId, out var info))
        {
            if (!HasForegroundStateChanged(info, payload))
            {
                OnForegroundChanged?.Invoke(sessionId, payload);
                return;
            }

            info.ForegroundPid = payload.Pid;
            info.ForegroundName = payload.Name;
            info.ForegroundCommandLine = payload.CommandLine;
            info.ForegroundDisplayName = payload.DisplayName;
            info.ForegroundProcessIdentity = payload.ProcessIdentity;
            info.AgentAttachPoint = payload.AgentAttachPoint;
            if (!string.IsNullOrEmpty(payload.Cwd))
            {
                info.CurrentDirectory = payload.Cwd;
            }
        }

        if (_tmuxCreatedSessions.ContainsKey(sessionId))
        {
            var shellName = info?.ShellType.ToString();
            var isShellForeground = shellName is not null &&
                string.Equals(payload.Name, shellName, StringComparison.OrdinalIgnoreCase);

            if (!isShellForeground)
            {
                _tmuxCommandStarted.TryAdd(sessionId, 0);
            }
            else if (_tmuxCommandStarted.TryRemove(sessionId, out _))
            {
                _ = CloseSessionAsync(sessionId, CancellationToken.None);
                return;
            }
        }

        OnForegroundChanged?.Invoke(sessionId, payload);
        NotifyStateChange();
    }

    private static bool HasForegroundStateChanged(SessionInfo info, ForegroundChangePayload payload)
    {
        var nextDirectory = string.IsNullOrEmpty(payload.Cwd) ? info.CurrentDirectory : payload.Cwd;
        return info.ForegroundPid != payload.Pid ||
               !string.Equals(info.ForegroundName, payload.Name, StringComparison.Ordinal) ||
               !string.Equals(info.ForegroundCommandLine, payload.CommandLine, StringComparison.Ordinal) ||
               !string.Equals(info.ForegroundDisplayName, payload.DisplayName, StringComparison.Ordinal) ||
               !string.Equals(info.ForegroundProcessIdentity, payload.ProcessIdentity, StringComparison.Ordinal) ||
               info.AgentAttachPoint != payload.AgentAttachPoint ||
               !string.Equals(info.CurrentDirectory, nextDirectory, StringComparison.OrdinalIgnoreCase);
    }

    private async Task HandleClientStateChangedAsync(string sessionId)
    {
        if (!_clients.ContainsKey(sessionId))
        {
            return;
        }

        var resizeGate = GetResizeGate(sessionId);
        await resizeGate.WaitAsync().ConfigureAwait(false);
        var removeResizeGate = false;
        try
        {
            if (_clients.TryGetValue(sessionId, out var c))
            {
                var info = await c.GetInfoAsync().ConfigureAwait(false);
                if (info is not null)
                {
                    CacheRefreshedSessionInfo(sessionId, info);
                }

                if (info is null || !info.IsRunning)
                {
                    if (_tmuxCreatedSessions.TryRemove(sessionId, out _))
                    {
                        removeResizeGate = await CloseSessionCoreAsync(sessionId, CancellationToken.None).ConfigureAwait(false);
                        return;
                    }

                    if (_clients.TryRemove(sessionId, out var removed))
                    {
                        await removed.DisposeAsync().ConfigureAwait(false);
                    }
                    _registry.RemoveSessionState(sessionId);
                    _transportState.TryRemove(sessionId, out _);
                    _redrawDimensionOverrides.TryRemove(sessionId, out _);
                    _ownershipRegistry.Remove(sessionId);
                    removeResizeGate = true;
                }
            }
            else
            {
                removeResizeGate = true;
            }

            OnStateChanged?.Invoke(sessionId);
            NotifyStateChange();
        }
        catch (Exception ex)
        {
            Log.Exception(ex, $"TtyHostSessionManager.HandleClientStateChanged({sessionId})");
        }
        finally
        {
            resizeGate.Release();
            if (removeResizeGate && !_clients.ContainsKey(sessionId))
            {
                TryRemoveResizeGate(sessionId, resizeGate);
            }
        }
    }

    private void CacheRefreshedSessionInfo(string sessionId, SessionInfo refreshed)
    {
        if (_sessionCache.TryGetValue(sessionId, out var existing))
        {
            MergeCachedFields(refreshed, existing);
        }

        if (_redrawDimensionOverrides.TryGetValue(sessionId, out var canonicalDimensions))
        {
            refreshed.Cols = canonicalDimensions.Cols;
            refreshed.Rows = canonicalDimensions.Rows;
        }

        _sessionCache[sessionId] = refreshed;
    }

    private static void MergeCachedFields(SessionInfo refreshed, SessionInfo existing)
    {
        // These fields are mt-owned metadata, not provided by mthost GetInfo.
        refreshed.TerminalTitle = existing.TerminalTitle;
        refreshed.ManuallyNamed = existing.ManuallyNamed;

        // Preserve user rename if a sparse refresh omits name.
        if (string.IsNullOrWhiteSpace(refreshed.Name) &&
            existing.ManuallyNamed &&
            !string.IsNullOrWhiteSpace(existing.Name))
        {
            refreshed.Name = existing.Name;
        }

        // Always prefer the existing CWD — it's kept current by HandleOscCwdUpdate
        // and HandleClientForegroundChanged. The refreshed snapshot from GetInfoAsync
        // reads the Win32 process PEB, which is stale on PowerShell (Set-Location
        // doesn't call SetCurrentDirectoryW). Only use the refreshed value for
        // initial population when the existing entry has no CWD yet.
        if (!string.IsNullOrWhiteSpace(existing.CurrentDirectory))
        {
            refreshed.CurrentDirectory = existing.CurrentDirectory;
        }

        if (refreshed.ForegroundPid is null && existing.ForegroundPid is not null)
        {
            refreshed.ForegroundPid = existing.ForegroundPid;
        }

        if (string.IsNullOrWhiteSpace(refreshed.ForegroundName) &&
            !string.IsNullOrWhiteSpace(existing.ForegroundName))
        {
            refreshed.ForegroundName = existing.ForegroundName;
        }

        if (string.IsNullOrWhiteSpace(refreshed.ForegroundCommandLine) &&
            !string.IsNullOrWhiteSpace(existing.ForegroundCommandLine))
        {
            refreshed.ForegroundCommandLine = existing.ForegroundCommandLine;
        }

        if (string.IsNullOrWhiteSpace(refreshed.ForegroundDisplayName) &&
            !string.IsNullOrWhiteSpace(existing.ForegroundDisplayName))
        {
            refreshed.ForegroundDisplayName = existing.ForegroundDisplayName;
        }

        if (string.IsNullOrWhiteSpace(refreshed.ForegroundProcessIdentity) &&
            !string.IsNullOrWhiteSpace(existing.ForegroundProcessIdentity))
        {
            refreshed.ForegroundProcessIdentity = existing.ForegroundProcessIdentity;
        }

        if (refreshed.AgentAttachPoint is null && existing.AgentAttachPoint is not null)
        {
            refreshed.AgentAttachPoint = existing.AgentAttachPoint;
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        foreach (var client in _clients.Values)
        {
            var clientSessionId = client.SessionId;

            try { await client.DisposeAsync().ConfigureAwait(false); }
            catch (Exception ex) { Log.Exception(ex, $"TtyHostSessionManager.Dispose({clientSessionId})"); }
        }

        // Clean up all temp directories
        foreach (var sessionId in _registry.TempDirectorySessionIds)
        {
            CleanupTempDirectory(sessionId);
        }

        _registry.ClearAll();
        _resizeGates.Clear();
        _redrawDimensionOverrides.Clear();
    }

    internal readonly record struct TerminalDimensions(int Cols, int Rows);

    private static void KillProcess(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            process.Kill();
        }
        catch
        {
            // Process may have already exited
        }
    }

    private static bool IsProcessRunning(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private static string DescribeSpawnedProcesses(int hostPid, int connectPid)
    {
        return hostPid == connectPid
            ? string.Create(CultureInfo.InvariantCulture, $"PID {connectPid}")
            : string.Create(CultureInfo.InvariantCulture, $"spawn PID {hostPid}, attach PID {connectPid}");
    }

    private static Task CleanupFailedSessionCreationAsync(
        string sessionId,
        int hostPid,
        int connectPid)
    {
        KillProcess(connectPid);
        if (hostPid != connectPid)
        {
            KillProcess(hostPid);
        }

        TtyHostSpawner.CleanupMacOsGuiLaunchAgent(sessionId);
        return Task.CompletedTask;
    }

    public sealed class TerminalTransportRuntimeSnapshot
    {
        public ulong SourceSeq { get; init; }
        public ulong MuxReceivedSeq { get; init; }
        public int ReconnectCount { get; init; }
        public int DataLossCount { get; init; }
        public TerminalReplayReason? LastDataLossReason { get; init; }
        public int LastReplayBytes { get; init; }
        public TerminalReplayReason? LastReplayReason { get; init; }
    }

    private sealed class TerminalTransportRuntimeState
    {
        public ulong SourceSeq;
        public ulong MuxReceivedSeq;
        public int ReconnectCount;
        public int DataLossCount;
        public TerminalReplayReason? LastDataLossReason;
        public int LastReplayBytes;
        public TerminalReplayReason? LastReplayReason;

        public TerminalTransportRuntimeSnapshot ToSnapshot()
        {
            return new TerminalTransportRuntimeSnapshot
            {
                SourceSeq = SourceSeq,
                MuxReceivedSeq = MuxReceivedSeq,
                ReconnectCount = ReconnectCount,
                DataLossCount = DataLossCount,
                LastDataLossReason = LastDataLossReason,
                LastReplayBytes = LastReplayBytes,
                LastReplayReason = LastReplayReason
            };
        }
    }
}

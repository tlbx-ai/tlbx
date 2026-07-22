using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed class TerminalSizeControlService
{
    public static readonly TimeSpan OfflineTakeoverDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan ConnectedIdleTakeoverDelay = TimeSpan.FromMinutes(5);

    private readonly Lock _lock = new();
    private readonly Dictionary<string, OwnershipRecord> _ownership = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<object>> _browserConnections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionGates = new(StringComparer.Ordinal);
    private readonly TimeProvider _timeProvider;
    private readonly string _statePath;

    public TerminalSizeControlService(SettingsService settingsService)
        : this(settingsService.SettingsDirectory, TimeProvider.System)
    {
    }

    internal TerminalSizeControlService(string settingsDirectory, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        _statePath = Path.Combine(settingsDirectory, "terminal-size-control.json");
        Load();
    }

    public event Action? OnChanged;

    public void RegisterBrowser(string browserId, object connectionToken)
    {
        if (string.IsNullOrWhiteSpace(browserId))
        {
            return;
        }

        var changed = false;
        lock (_lock)
        {
            if (!_browserConnections.TryGetValue(browserId, out var connections))
            {
                connections = new HashSet<object>(ReferenceEqualityComparer.Instance);
                _browserConnections[browserId] = connections;
            }

            changed = connections.Add(connectionToken) && connections.Count == 1;
        }

        if (changed)
        {
            OnChanged?.Invoke();
        }
    }

    public void UnregisterBrowser(string browserId, object connectionToken)
    {
        var changed = false;
        lock (_lock)
        {
            if (!_browserConnections.TryGetValue(browserId, out var connections))
            {
                return;
            }

            connections.Remove(connectionToken);
            if (connections.Count == 0)
            {
                _browserConnections.Remove(browserId);
                changed = true;
            }
        }

        if (changed)
        {
            OnChanged?.Invoke();
        }
    }

    public TerminalSizeControlStatus GetStatus(string sessionId, string browserId)
    {
        lock (_lock)
        {
            return BuildStatusLocked(sessionId, browserId, _timeProvider.GetUtcNow());
        }
    }

    public List<TerminalSizeControlStatus> GetStatuses(
        string browserId,
        IEnumerable<string> sessionIds)
    {
        var ids = sessionIds
            .Where(static id => !string.IsNullOrWhiteSpace(id))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        var now = _timeProvider.GetUtcNow();

        lock (_lock)
        {
            return ids.Select(id => BuildStatusLocked(id, browserId, now)).ToList();
        }
    }

    public void AssignNewSession(string sessionId, string browserId, string? browserLabel = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(browserId))
        {
            return;
        }

        lock (_lock)
        {
            var nextEpoch = _ownership.TryGetValue(sessionId, out var existing)
                ? existing.Epoch + 1
                : 1;
            _ownership[sessionId] = new OwnershipRecord
            {
                BrowserId = browserId,
                BrowserLabel = browserLabel,
                Epoch = nextEpoch,
                LastInteractionUtc = _timeProvider.GetUtcNow()
            };
            PersistLocked();
        }

        OnChanged?.Invoke();
    }

    public async Task<TerminalSizeControlCommandResult> RequestControlAsync(
        string sessionId,
        string browserId,
        bool force,
        string? browserLabel = null,
        CancellationToken ct = default)
    {
        var gate = _sessionGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        var changed = false;
        TerminalSizeControlStatus status;
        try
        {
            lock (_lock)
            {
                var now = _timeProvider.GetUtcNow();
                if (_ownership.TryGetValue(sessionId, out var current) &&
                    string.Equals(current.BrowserId, browserId, StringComparison.Ordinal))
                {
                    current.LastInteractionUtc = now;
                    if (!string.IsNullOrWhiteSpace(browserLabel))
                    {
                        current.BrowserLabel = browserLabel;
                    }
                    PersistLocked();
                }
                else if (force || CanTakeOverAutomaticallyLocked(current, browserId, now))
                {
                    _ownership[sessionId] = new OwnershipRecord
                    {
                        BrowserId = browserId,
                        BrowserLabel = browserLabel,
                        Epoch = (current?.Epoch ?? 0) + 1,
                        LastInteractionUtc = now
                    };
                    PersistLocked();
                    changed = true;
                }

                status = BuildStatusLocked(sessionId, browserId, now);
            }
        }
        finally
        {
            gate.Release();
        }

        if (changed)
        {
            OnChanged?.Invoke();
        }

        return new TerminalSizeControlCommandResult
        {
            Status = status,
            OwnershipChanged = changed
        };
    }

    public async Task<TerminalSizeControlCommandResult> ResizeAsync(
        string sessionId,
        string browserId,
        long expectedEpoch,
        int cols,
        int rows,
        Func<CancellationToken, Task<bool>> resize,
        CancellationToken ct = default)
    {
        TerminalSizeLimits.ThrowIfInvalid(cols, rows);
        var gate = _sessionGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            TerminalSizeControlStatus status;
            lock (_lock)
            {
                status = BuildStatusLocked(sessionId, browserId, _timeProvider.GetUtcNow());
                if (!status.IsOwner || status.Epoch != expectedEpoch)
                {
                    return new TerminalSizeControlCommandResult
                    {
                        Status = status,
                        Cols = cols,
                        Rows = rows
                    };
                }
            }

            var resized = await resize(ct).ConfigureAwait(false);
            return new TerminalSizeControlCommandResult
            {
                Status = status,
                ResizeApplied = resized,
                Cols = cols,
                Rows = rows
            };
        }
        finally
        {
            gate.Release();
        }
    }

    public void PruneSessions(IEnumerable<string> validSessionIds)
    {
        var valid = validSessionIds.ToHashSet(StringComparer.Ordinal);
        var changed = false;
        lock (_lock)
        {
            foreach (var sessionId in _ownership.Keys.Where(id => !valid.Contains(id)).ToArray())
            {
                _ownership.Remove(sessionId);
                _sessionGates.TryRemove(sessionId, out _);
                changed = true;
            }

            if (changed)
            {
                PersistLocked();
            }
        }

        if (changed)
        {
            OnChanged?.Invoke();
        }
    }

    private TerminalSizeControlStatus BuildStatusLocked(
        string sessionId,
        string browserId,
        DateTimeOffset now)
    {
        _ownership.TryGetValue(sessionId, out var owner);
        var isOwner = owner is not null &&
                      string.Equals(owner.BrowserId, browserId, StringComparison.Ordinal);
        return new TerminalSizeControlStatus
        {
            SessionId = sessionId,
            IsOwner = isOwner,
            HasOwner = owner is not null,
            OwnerOnline = owner is not null && IsBrowserOnlineLocked(owner.BrowserId),
            OwnerInSameBrowserProfile = owner is not null &&
                BrowserIdentity.AreSameBrowser(owner.BrowserId, browserId),
            CanTakeOverAutomatically = isOwner || CanTakeOverAutomaticallyLocked(owner, browserId, now),
            OwnerLabel = owner?.BrowserLabel,
            Epoch = owner?.Epoch ?? 0
        };
    }

    private bool CanTakeOverAutomaticallyLocked(
        OwnershipRecord? owner,
        string requestingBrowserId,
        DateTimeOffset now)
    {
        if (owner is null)
        {
            return true;
        }

        var ownerOnline = IsBrowserOnlineLocked(owner.BrowserId);
        if (!ownerOnline && BrowserIdentity.AreSameBrowser(owner.BrowserId, requestingBrowserId))
        {
            return true;
        }

        var idleFor = now - owner.LastInteractionUtc;
        return ownerOnline
            ? idleFor >= ConnectedIdleTakeoverDelay
            : idleFor >= OfflineTakeoverDelay;
    }

    private bool IsBrowserOnlineLocked(string browserId)
    {
        return _browserConnections.TryGetValue(browserId, out var connections) && connections.Count > 0;
    }

    private void Load()
    {
        if (!File.Exists(_statePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(_statePath);
            var state = JsonSerializer.Deserialize(
                json,
                TerminalSizeControlJsonContext.Default.TerminalSizeControlPersistedState);
            if (state?.Sessions is null)
            {
                return;
            }

            foreach (var (sessionId, record) in state.Sessions)
            {
                if (!string.IsNullOrWhiteSpace(sessionId) && !string.IsNullOrWhiteSpace(record.BrowserId))
                {
                    _ownership[sessionId] = record;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"Failed to load terminal size ownership: {ex.Message}");
        }
    }

    private void PersistLocked()
    {
        try
        {
            var directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            if (_ownership.Count == 0)
            {
                if (File.Exists(_statePath))
                {
                    File.Delete(_statePath);
                }
                return;
            }

            var state = new TerminalSizeControlPersistedState
            {
                Sessions = new Dictionary<string, OwnershipRecord>(_ownership, StringComparer.Ordinal)
            };
            var json = JsonSerializer.Serialize(
                state,
                TerminalSizeControlJsonContext.Default.TerminalSizeControlPersistedState);
            File.WriteAllText(_statePath, json);
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"Failed to save terminal size ownership: {ex.Message}");
        }
    }

    internal sealed class OwnershipRecord
    {
        public string BrowserId { get; set; } = string.Empty;
        public string? BrowserLabel { get; set; }
        public long Epoch { get; set; }
        public DateTimeOffset LastInteractionUtc { get; set; }
    }

    internal sealed class TerminalSizeControlPersistedState
    {
        public Dictionary<string, OwnershipRecord> Sessions { get; set; } = new(StringComparer.Ordinal);
    }
}

public static class TerminalSizeLimits
{
    public const int MinCols = 10;
    public const int MaxCols = 300;
    public const int MinRows = 5;
    public const int MaxRows = 100;

    public static void ThrowIfInvalid(int cols, int rows)
    {
        if (cols is < MinCols or > MaxCols || rows is < MinRows or > MaxRows)
        {
            throw new ArgumentOutOfRangeException(
                nameof(cols),
                $"Terminal dimensions must be between {MinCols}x{MinRows} and {MaxCols}x{MaxRows}.");
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(TerminalSizeControlService.TerminalSizeControlPersistedState))]
internal sealed partial class TerminalSizeControlJsonContext : JsonSerializerContext;

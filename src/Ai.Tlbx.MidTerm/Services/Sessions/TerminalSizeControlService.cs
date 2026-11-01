using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed class TerminalSizeControlService : IDisposable
{
    private readonly Lock _lock = new();
    private readonly Dictionary<string, OwnershipRecord> _ownership = new(StringComparer.Ordinal);
    private readonly Dictionary<string, long> _latestInputIntents = new(StringComparer.Ordinal);
    private long _inputIntentSequence;
    private readonly Dictionary<string, HashSet<object>> _browserConnections = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _sessionGates = new(StringComparer.Ordinal);
    private readonly string _statePath;
    private Task _persistenceTask = Task.CompletedTask;

    public TerminalSizeControlService(SettingsService settingsService)
        : this(settingsService.SettingsDirectory)
    {
    }

    internal TerminalSizeControlService(string settingsDirectory)
    {
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

    public string? GetOwnerBrowserId(string sessionId)
    {
        lock (_lock)
        {
            return _ownership.GetValueOrDefault(sessionId)?.BrowserId;
        }
    }

    public TerminalSizeControlStatus GetStatus(string sessionId, string browserId)
    {
        lock (_lock)
        {
            return BuildStatusLocked(sessionId, browserId);
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

        lock (_lock)
        {
            return ids.Select(id => BuildStatusLocked(id, browserId)).ToList();
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
                Epoch = nextEpoch
            };
            PersistLocked();
        }

        OnChanged?.Invoke();
    }

    public Task<TerminalSizeControlCommandResult> RequestControlAsync(
        string sessionId,
        string browserId,
        bool force,
        string? browserLabel = null,
        long? expectedEpoch = null,
        CancellationToken ct = default)
        => UpdateControlAsync(sessionId, browserId, force, false, browserLabel, expectedEpoch, null, ct);

    // Called only with actual browser-authored input at the server input boundary.
    // Terminal replies, API automation and passive control requests are not activity.
    public Task<TerminalSizeControlCommandResult> RecordInputAsync(
        string sessionId, string browserId, string? browserLabel = null, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        long inputIntent;
        lock (_lock)
        {
            inputIntent = ++_inputIntentSequence;
            _latestInputIntents[sessionId] = inputIntent;
            if (_ownership.TryGetValue(sessionId, out var owner))
            {
                if (string.Equals(owner.BrowserId, browserId, StringComparison.Ordinal))
                {
                    return Task.FromResult(new TerminalSizeControlCommandResult
                    {
                        Status = BuildStatusLocked(sessionId, browserId)
                    });
                }
            }
        }
        // Only a transfer needs serialization with an in-flight resize. Ordinary
        // typing must never wait for the PTY to acknowledge a resize.
        return UpdateControlAsync(sessionId, browserId, false, true, browserLabel, null, inputIntent, ct);
    }

    private async Task<TerminalSizeControlCommandResult> UpdateControlAsync(
        string sessionId, string browserId, bool force, bool isInput,
        string? browserLabel, long? expectedEpoch, long? inputIntent, CancellationToken ct)
    {
        var gate = _sessionGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        var changed = false;
        TerminalSizeControlStatus status;
        try
        {
            lock (_lock)
            {
                if (_ownership.TryGetValue(sessionId, out var current) &&
                    string.Equals(current.BrowserId, browserId, StringComparison.Ordinal))
                {
                    // Current input needs neither a new epoch nor a persistence write.
                }
                else if (
                    (force && (expectedEpoch is null || expectedEpoch.Value == (current?.Epoch ?? 0))) ||
                    (!force && (isInput
                        ? inputIntent == _latestInputIntents.GetValueOrDefault(sessionId)
                        : current is null)))
                {
                    if (force) _latestInputIntents[sessionId] = ++_inputIntentSequence;
                    _ownership[sessionId] = new OwnershipRecord
                    {
                        BrowserId = browserId,
                        BrowserLabel = browserLabel,
                        Epoch = (current?.Epoch ?? 0) + 1
                    };
                    PersistLocked();
                    changed = true;
                }

                status = BuildStatusLocked(sessionId, browserId);
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
                status = BuildStatusLocked(sessionId, browserId);
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

    // Headless API/tmux callers have no browser lease. They may size only unowned sessions.
    public async Task<bool> ResizeUnownedAsync(
        string sessionId, int cols, int rows, Func<CancellationToken, Task<bool>> resize,
        CancellationToken ct = default)
    {
        TerminalSizeLimits.ThrowIfInvalid(cols, rows);
        var gate = _sessionGates.GetOrAdd(sessionId, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            lock (_lock)
            {
                if (_ownership.ContainsKey(sessionId)) return false;
            }
            return await resize(ct).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }

    public void PruneSessions(IEnumerable<string> validSessionIds)
    {
        var valid = validSessionIds.ToHashSet(StringComparer.Ordinal);
        var changed = false;
        lock (_lock)
        {
            foreach (var sessionId in _ownership.Keys.Concat(_latestInputIntents.Keys)
                         .Distinct(StringComparer.Ordinal).Where(id => !valid.Contains(id)).ToArray())
            {
                changed |= _ownership.Remove(sessionId);
                _latestInputIntents.Remove(sessionId);
                _sessionGates.TryRemove(sessionId, out _);
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
        string browserId)
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
            // Eligibility for actual input, never permission for a passive browser claim.
            CanTakeOverAutomatically = true,
            OwnerLabel = owner?.BrowserLabel,
            Epoch = owner?.Epoch ?? 0
        };
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

            PersistLocked();
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"Failed to load terminal size ownership: {ex.Message}");
        }
    }

    private void PersistLocked()
    {
        // Snapshot while holding the state lock; serialize and write in order off the input path.
        var state = new TerminalSizeControlPersistedState
        {
            Sessions = _ownership.ToDictionary(pair => pair.Key, pair => new OwnershipRecord
            {
                BrowserId = pair.Value.BrowserId,
                BrowserLabel = pair.Value.BrowserLabel,
                Epoch = pair.Value.Epoch
            }, StringComparer.Ordinal)
        };
        _persistenceTask = _persistenceTask.ContinueWith(_ => Save(state),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    private void Save(TerminalSizeControlPersistedState state)
    {
        try
        {
            var directory = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
            if (state.Sessions.Count == 0)
            {
                File.Delete(_statePath);
                return;
            }
            var json = JsonSerializer.Serialize(state,
                TerminalSizeControlJsonContext.Default.TerminalSizeControlPersistedState);
            var temporaryPath = _statePath + ".tmp";
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _statePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"Failed to save terminal size ownership: {ex.Message}");
        }
    }

    public void Dispose()
    {
        Task pending;
        lock (_lock)
        {
            PersistLocked();
            pending = _persistenceTask;
        }
        pending.GetAwaiter().GetResult();
    }

    internal sealed class OwnershipRecord
    {
        public string BrowserId { get; set; } = string.Empty;
        public string? BrowserLabel { get; set; }
        public long Epoch { get; set; }
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

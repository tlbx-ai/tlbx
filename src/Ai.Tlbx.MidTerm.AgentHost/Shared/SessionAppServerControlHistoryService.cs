using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Ai.Tlbx.MidTerm.AgentHost;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Settings;
using Ai.Tlbx.MidTerm.Services.Updates;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed partial class SessionAppServerControlHistoryService
{
    private const int DefaultHistoryWindowSize = 80;
    private const int MaxTransientEventBacklog = 256;
    private const int PersistDebounceMilliseconds = 200;
    private const string ScreenLogFormatVersion = "midterm-appServerControl-screen-log-v1";
    private const int CollapsibleHistoryBodyMinLines = 8;
    private const int CollapsibleHistoryBodyMinChars = 320;
    private const int HistoryPreviewMaxChars = 160;
    private const int MaxVisibleDiffScreenLogLines = 200;
    private const int MaxAssistantStreamChars = 262144;
    private const int MaxReasoningStreamChars = 8192;
    private const int MaxReasoningSummaryStreamChars = 8192;
    private const int MaxPlanStreamChars = 16384;
    private const int MaxCommandOutputStreamChars = 16384;
    private const int MaxFileChangeOutputStreamChars = 16384;
    private const int MaxUnifiedDiffChars = 32768;
    private const int MaxHistoryBodyChars = 4096;
    private const int MaxHistoryLineChars = 512;
    private const string TailRetentionMarker = "... earlier output omitted ...";
    private const string HeadRetentionMarker = "... output truncated ...";
    private readonly ConcurrentDictionary<string, SessionAppServerControlHistoryLog> _logs = new(StringComparer.Ordinal);
    private readonly string _storeDirectory;
    private readonly bool _screenLoggingEnabled;
    private readonly string? _screenLogDirectory;

    public SessionAppServerControlHistoryService(
        SettingsService? settingsService = null,
        string? storeDirectory = null,
        bool? enableScreenLogging = null,
        string? screenLogDirectory = null,
        TtyHostSessionManager? sessionManager = null,
        SessionPathAllowlistService? allowlistService = null)
    {
        _sessionManager = sessionManager;
        _allowlistService = allowlistService;
        _storeDirectory = storeDirectory ??
                          (settingsService is not null
                              ? Path.Combine(settingsService.SettingsDirectory, "appServerControl-history")
                              : Path.Combine(Path.GetTempPath(), "midterm-appServerControl-history", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(_storeDirectory);

        _screenLoggingEnabled = enableScreenLogging ??
                                (settingsService is not null &&
                                 (UpdateService.IsDevEnvironment || settingsService.Load().DevMode));

        if (!_screenLoggingEnabled)
        {
            return;
        }

        _screenLogDirectory = screenLogDirectory ?? ResolveScreenLogDirectory(settingsService);
        Directory.CreateDirectory(_screenLogDirectory);
    }

    public void Append(AppServerControlProviderEvent appServerControlEvent)
    {
        ArgumentNullException.ThrowIfNull(appServerControlEvent);

        var retainedEvent = AppServerControlProviderEventCompaction.CloneForRetention(appServerControlEvent);
        var log = GetOrLoadLog(appServerControlEvent.SessionId);
        lock (log.SyncRoot)
        {
            retainedEvent.Sequence = ++log.NextSequence;
            log.EventBacklog.Add(retainedEvent);
            TrimEventBacklog(log.EventBacklog);
            ApplyEvent(log.State, retainedEvent);
            var delta = BuildDelta(log.SessionId, log.NextSequence, log.State, retainedEvent);
            PersistScreenLogDelta(log, delta);
            log.PersistenceDirty = true;

            var staleSubscribers = new List<AppServerControlProviderEventSubscriber>();
            foreach (var subscriber in log.Subscribers)
            {
                if (!subscriber.Writer.TryWrite(CloneEvent(retainedEvent)))
                {
                    staleSubscribers.Add(subscriber);
                }
            }

            if (staleSubscribers.Count > 0)
            {
                foreach (var staleSubscriber in staleSubscribers)
                {
                    log.Subscribers.Remove(staleSubscriber);
                }
            }

            var staleDeltaSubscribers = new List<AppServerControlHistoryPatchSubscriber>();
            foreach (var subscriber in log.DeltaSubscribers)
            {
                if (!subscriber.Writer.TryWrite(CloneDelta(delta)))
                {
                    staleDeltaSubscribers.Add(subscriber);
                }
            }

            if (staleDeltaSubscribers.Count > 0)
            {
                foreach (var staleSubscriber in staleDeltaSubscribers)
                {
                    log.DeltaSubscribers.Remove(staleSubscriber);
                }
            }
        }

        SchedulePersistence(log.SessionId);
    }

    public AppServerControlProviderEventSubscription SubscribeProviderEvents(string sessionId, long afterSequence, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var log = GetOrLoadLog(sessionId);
        var channel = Channel.CreateUnbounded<AppServerControlProviderEvent>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        var subscriber = new AppServerControlProviderEventSubscriber(channel.Writer);
        List<AppServerControlProviderEvent> backlog;

        lock (log.SyncRoot)
        {
            backlog = log.EventBacklog
                .Where(appServerControlEvent => appServerControlEvent.Sequence > afterSequence)
                .Select(CloneEvent)
                .ToList();
            log.Subscribers.Add(subscriber);
        }

        foreach (var appServerControlEvent in backlog)
        {
            channel.Writer.TryWrite(appServerControlEvent);
        }

        var state = new SubscriptionState(
            () =>
            {
                lock (log.SyncRoot)
                {
                    log.Subscribers.Remove(subscriber);
                }

                channel.Writer.TryComplete();
            });
        var subscription = new AppServerControlProviderEventSubscription(channel.Reader, state);

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(static state =>
            {
                if (state is SubscriptionState subscriptionState)
                {
                    subscriptionState.Close();
                }
            }, state);
        }

        return subscription;
    }

    public AppServerControlProviderEventListResponse GetProviderEvents(string sessionId, long afterSequence = 0)
    {
        if (!TryGetLog(sessionId, out var log))
        {
            return new AppServerControlProviderEventListResponse
            {
                SessionId = sessionId
            };
        }

        lock (log.SyncRoot)
        {
            return new AppServerControlProviderEventListResponse
            {
                SessionId = sessionId,
                LatestSequence = log.NextSequence,
                Events = log.EventBacklog
                    .Where(e => e.Sequence > afterSequence)
                    .Select(CloneEvent)
                    .ToList()
            };
        }
    }

    public SessionAppServerControlHistoryPatchSubscription SubscribeHistoryPatches(string sessionId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var log = GetOrLoadLog(sessionId);
        var channel = Channel.CreateUnbounded<AppServerControlHistoryPatch>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        var subscriber = new AppServerControlHistoryPatchSubscriber(channel.Writer);

        lock (log.SyncRoot)
        {
            log.DeltaSubscribers.Add(subscriber);
        }

        var state = new SubscriptionState(
            () =>
            {
                lock (log.SyncRoot)
                {
                    log.DeltaSubscribers.Remove(subscriber);
                }

                channel.Writer.TryComplete();
            });
        var subscription = new SessionAppServerControlHistoryPatchSubscription(channel.Reader, state);

        if (cancellationToken.CanBeCanceled)
        {
            cancellationToken.Register(static state =>
            {
                if (state is SubscriptionState subscriptionState)
                {
                    subscriptionState.Close();
                }
            }, state);
        }

        return subscription;
    }

    public bool HasHistory(string sessionId)
    {
        if (!TryGetLog(sessionId, out var log))
        {
            return false;
        }

        lock (log.SyncRoot)
        {
            return log.NextSequence > 0;
        }
    }

    public AppServerControlHistoryWindowResponse? GetSnapshot(string sessionId)
    {
        if (!TryGetLog(sessionId, out var log))
        {
            return null;
        }

        lock (log.SyncRoot)
        {
            if (log.NextSequence == 0)
            {
                return null;
            }
            return CloneSnapshot(sessionId, log.NextSequence, log.State, 0, null, null);
        }
    }

    public SessionAppServerControlHeatSnapshot GetHeatSnapshot(string sessionId)
    {
        if (!TryGetLog(sessionId, out var log))
        {
            return SessionAppServerControlHeatSnapshot.Cold;
        }

        lock (log.SyncRoot)
        {
            return BuildHeatSnapshot(log.State);
        }
    }

    public AppServerControlHistoryWindowResponse? GetSnapshotWindow(
        string sessionId,
        int? startIndex = null,
        int? count = null,
        int? viewportWidth = null)
    {
        if (!TryGetLog(sessionId, out var log))
        {
            return null;
        }

        lock (log.SyncRoot)
        {
            if (log.NextSequence == 0)
            {
                return null;
            }

            var totalCount = log.State.HistoryEntries.Count;
            var boundedCount = Math.Max(1, count ?? DefaultHistoryWindowSize);
            var effectiveStart = startIndex ?? Math.Max(0, totalCount - boundedCount);
            effectiveStart = Math.Clamp(effectiveStart, 0, Math.Max(0, totalCount - 1));
            return CloneSnapshot(
                sessionId,
                log.NextSequence,
                log.State,
                effectiveStart,
                boundedCount,
                viewportWidth);
        }
    }

    public void ImportHistoryWindow(AppServerControlHistoryWindowResponse historyWindow)
    {
        ArgumentNullException.ThrowIfNull(historyWindow);
        ArgumentException.ThrowIfNullOrWhiteSpace(historyWindow.SessionId);

        var log = GetOrLoadLog(historyWindow.SessionId);
        lock (log.SyncRoot)
        {
            ImportHistoryWindow(log.State, historyWindow);
            log.NextSequence = Math.Max(log.NextSequence, Math.Max(1, historyWindow.LatestSequence));
            log.PersistenceDirty = true;
        }

        SchedulePersistence(log.SessionId);
    }

    public void ApplyHistoryPatch(AppServerControlHistoryPatch historyPatch)
    {
        ArgumentNullException.ThrowIfNull(historyPatch);
        ArgumentException.ThrowIfNullOrWhiteSpace(historyPatch.SessionId);

        var log = GetOrLoadLog(historyPatch.SessionId);
        lock (log.SyncRoot)
        {
            if (historyPatch.LatestSequence < log.NextSequence)
            {
                return;
            }

            ApplyHistoryPatch(log.State, historyPatch);
            log.NextSequence = Math.Max(log.NextSequence, Math.Max(1, historyPatch.LatestSequence));
            log.PersistenceDirty = true;
        }

        SchedulePersistence(log.SessionId);
    }

    public void ImportProviderEvents(
        string sessionId,
        long latestSequence,
        IReadOnlyList<AppServerControlProviderEvent> providerEvents)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        ArgumentNullException.ThrowIfNull(providerEvents);

        var log = GetOrLoadLog(sessionId);
        lock (log.SyncRoot)
        {
            log.EventBacklog.Clear();
            foreach (var appServerControlEvent in providerEvents
                         .Where(static appServerControlEvent => !string.IsNullOrWhiteSpace(appServerControlEvent.SessionId))
                         .OrderBy(static appServerControlEvent => appServerControlEvent.Sequence))
            {
                log.EventBacklog.Add(CloneEvent(appServerControlEvent));
            }

            TrimEventBacklog(log.EventBacklog);
            log.NextSequence = Math.Max(
                log.NextSequence,
                Math.Max(
                    latestSequence,
                    log.EventBacklog.Count == 0 ? 0 : log.EventBacklog[^1].Sequence));
            log.PersistenceDirty = true;
        }

        SchedulePersistence(log.SessionId);
    }

    public void Forget(string sessionId)
    {
        if (_logs.TryRemove(sessionId, out var log))
        {
            lock (log.SyncRoot)
            {
                foreach (var subscriber in log.Subscribers)
                {
                    subscriber.Writer.TryComplete();
                }

                foreach (var subscriber in log.DeltaSubscribers)
                {
                    subscriber.Writer.TryComplete();
                }

                log.Subscribers.Clear();
                log.DeltaSubscribers.Clear();
            }

            TryDeleteStore(sessionId);
        }
    }

    private bool TryGetLog(string sessionId, out SessionAppServerControlHistoryLog log)
    {
        if (_logs.TryGetValue(sessionId, out log!))
        {
            return true;
        }

        if (TryLoadLog(sessionId, out var loaded))
        {
            log = _logs.GetOrAdd(sessionId, loaded);
            return true;
        }

        log = null!;
        return false;
    }

    private SessionAppServerControlHistoryLog GetOrLoadLog(string sessionId)
    {
        return _logs.GetOrAdd(sessionId, LoadLog);
    }

    private SessionAppServerControlHistoryLog LoadLog(string sessionId)
    {
        if (TryLoadLog(sessionId, out var log))
        {
            return log;
        }

        return new SessionAppServerControlHistoryLog(sessionId);
    }

    private bool TryLoadLog(string sessionId, out SessionAppServerControlHistoryLog log)
    {
        log = new SessionAppServerControlHistoryLog(sessionId);
        if (TryLoadPersistedState(sessionId, log))
        {
            return true;
        }

        if (!TryLoadLegacyEventLog(sessionId, log))
        {
            return false;
        }

        PersistState(sessionId, BuildPersistedState(log));
        TryDeleteLegacyStore(sessionId);
        return log.NextSequence > 0;
    }

    private bool TryLoadPersistedState(string sessionId, SessionAppServerControlHistoryLog log)
    {
        var path = GetStateStorePath(sessionId);
        if (!File.Exists(path))
        {
            return false;
        }

        var payload = File.ReadAllText(path, Encoding.UTF8);
        if (string.IsNullOrWhiteSpace(payload))
        {
            return false;
        }

        var persisted = JsonSerializer.Deserialize(payload, AppServerControlPersistenceJsonContext.Default.AppServerControlPersistedSessionState);
        if (persisted is null || persisted.NextSequence <= 0)
        {
            return false;
        }

        log.NextSequence = persisted.NextSequence;
        RestoreConversationState(log.State, persisted.State);
        return true;
    }

    private bool TryLoadLegacyEventLog(string sessionId, SessionAppServerControlHistoryLog log)
    {
        var path = GetLegacyEventStorePath(sessionId);
        if (!File.Exists(path))
        {
            return false;
        }

        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var appServerControlEvent = JsonSerializer.Deserialize(line, AppServerControlProviderEventJsonContext.Default.AppServerControlProviderEvent);
            if (appServerControlEvent is null)
            {
                continue;
            }

            var retainedEvent = AppServerControlProviderEventCompaction.CloneForRetention(appServerControlEvent);
            log.EventBacklog.Add(retainedEvent);
            TrimEventBacklog(log.EventBacklog);
            log.NextSequence = Math.Max(log.NextSequence, retainedEvent.Sequence);
            ApplyEvent(log.State, retainedEvent);
        }

        return log.NextSequence > 0;
    }

    private void SchedulePersistence(string sessionId)
    {
        if (!_logs.TryGetValue(sessionId, out var log))
        {
            return;
        }

        var shouldSchedule = false;
        lock (log.SyncRoot)
        {
            if (!log.PersistenceScheduled)
            {
                log.PersistenceScheduled = true;
                shouldSchedule = true;
            }
        }

        if (!shouldSchedule)
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(PersistDebounceMilliseconds).ConfigureAwait(false);

                if (!_logs.TryGetValue(sessionId, out var current))
                {
                    return;
                }

                AppServerControlPersistedSessionState? persisted = null;
                lock (current.SyncRoot)
                {
                    if (!current.PersistenceDirty)
                    {
                        current.PersistenceScheduled = false;
                        return;
                    }

                    current.PersistenceDirty = false;
                    persisted = BuildPersistedState(current);
                }

                try
                {
                    PersistState(sessionId, persisted);
                }
                catch (Exception ex)
                {
                    Log.Verbose(() => $"[AppServerControlHistory] Failed to persist canonical AppServerControl state for {sessionId}: {ex.Message}");
                }
            }
        });
    }

    private void PersistState(string sessionId, AppServerControlPersistedSessionState persisted)
    {
        var path = GetStateStorePath(sessionId);
        var tempPath = path + ".tmp";
        var payload = JsonSerializer.Serialize(persisted, AppServerControlPersistenceJsonContext.Default.AppServerControlPersistedSessionState);
        File.WriteAllText(tempPath, payload, Encoding.UTF8);
        File.Move(tempPath, path, overwrite: true);
    }

    private void TryDeleteStore(string sessionId)
    {
        var statePath = GetStateStorePath(sessionId);
        if (File.Exists(statePath))
        {
            File.Delete(statePath);
        }

        TryDeleteLegacyStore(sessionId);
    }

    private void TryDeleteLegacyStore(string sessionId)
    {
        var path = GetLegacyEventStorePath(sessionId);
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }

    private static string ResolveScreenLogDirectory(SettingsService? settingsService)
    {
        if (settingsService is null)
        {
            return Path.Combine(Path.GetTempPath(), "midterm-appServerControl-logs");
        }

        var isWindowsService = OperatingSystem.IsWindows() && settingsService.IsRunningAsService;
        var isUnixService = !OperatingSystem.IsWindows() && settingsService.IsRunningAsService;
        var logRoot = LogPaths.GetLogDirectory(isWindowsService, isUnixService);
        return Path.Combine(logRoot, "appServerControl");
    }

    private string GetStateStorePath(string sessionId)
    {
        var safeName = Uri.EscapeDataString(sessionId);
        return Path.Combine(_storeDirectory, $"{safeName}.json");
    }

    private string GetLegacyEventStorePath(string sessionId)
    {
        var safeName = Uri.EscapeDataString(sessionId);
        return Path.Combine(_storeDirectory, $"{safeName}.ndjson");
    }

    private void PersistScreenLogDelta(SessionAppServerControlHistoryLog log, AppServerControlHistoryPatch delta)
    {
        if (!_screenLoggingEnabled || string.IsNullOrWhiteSpace(_screenLogDirectory))
        {
            return;
        }

        var screenLogPath = EnsureScreenLogPath(log, delta.Provider, delta.GeneratedAt);
        if (screenLogPath is null)
        {
            return;
        }

        var record = new AppServerControlScreenLogDeltaRecord
        {
            Format = ScreenLogFormatVersion,
            RecordType = "screen_delta",
            LogId = log.ScreenLogId ?? string.Empty,
            SessionId = log.SessionId,
            Provider = delta.Provider,
            LatestSequence = delta.LatestSequence,
            RecordedAt = delta.GeneratedAt,
            Session = new AppServerControlScreenLogSessionState
            {
                State = delta.Session.State,
                StateLabel = delta.Session.StateLabel,
                Reason = delta.Session.Reason,
                LastError = delta.Session.LastError
            },
            CurrentTurn = new AppServerControlScreenLogTurnState
            {
                TurnId = delta.CurrentTurn.TurnId,
                State = delta.CurrentTurn.State,
                StateLabel = delta.CurrentTurn.StateLabel,
                Model = delta.CurrentTurn.Model,
                Effort = delta.CurrentTurn.Effort
            },
            HistoryUpserts = delta.HistoryUpserts
                .Where(ShouldIncludeInScreenHistoryLog)
                .Select(BuildScreenLogHistoryEntry)
                .OrderBy(entry => entry.Order)
                .ToList(),
            HistoryRemovals = [.. delta.HistoryRemovals]
        };

        var signature = BuildScreenLogSignature(record);
        if (string.Equals(signature, log.LastScreenLogSignature, StringComparison.Ordinal))
        {
            return;
        }

        log.LastScreenLogSignature = signature;

        File.AppendAllText(
            screenLogPath,
            JsonSerializer.Serialize(record, AppServerControlScreenLogJsonContext.Default.AppServerControlScreenLogDeltaRecord) + Environment.NewLine,
            Encoding.UTF8);
    }

    private string? EnsureScreenLogPath(SessionAppServerControlHistoryLog log, string provider, DateTimeOffset createdAt)
    {
        if (string.IsNullOrWhiteSpace(_screenLogDirectory))
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(log.ScreenLogPath))
        {
            return log.ScreenLogPath;
        }

        log.ScreenLogId = Guid.CreateVersion7().ToString("N");
        log.ScreenLogPath = Path.Combine(_screenLogDirectory, $"{log.ScreenLogId}.appServerControllog.jsonl");

        var header = new AppServerControlScreenLogHeaderRecord
        {
            Format = ScreenLogFormatVersion,
            RecordType = "header",
            LogId = log.ScreenLogId,
            SessionId = log.SessionId,
            Provider = provider,
            CreatedAt = createdAt
        };

        File.AppendAllText(
            log.ScreenLogPath,
            JsonSerializer.Serialize(header, AppServerControlScreenLogJsonContext.Default.AppServerControlScreenLogHeaderRecord) + Environment.NewLine,
            Encoding.UTF8);

        return log.ScreenLogPath;
    }

    private static void ApplyEvent(AppServerControlConversationState state, AppServerControlProviderEvent appServerControlEvent)
    {
        if (!string.IsNullOrWhiteSpace(appServerControlEvent.Provider))
        {
            state.Provider = appServerControlEvent.Provider;
        }

        // Pre-attach notices carry the session ID as a fallback. Replay publishes them after
        // authoritative thread metadata; they must not replace that established provider ID.
        // Explicit ThreadState metadata is still applied below, including legitimate replacements.
        if (!string.IsNullOrWhiteSpace(appServerControlEvent.ThreadId) &&
            (!string.Equals(appServerControlEvent.ThreadId, appServerControlEvent.SessionId, StringComparison.Ordinal) ||
             string.IsNullOrWhiteSpace(state.Thread.ThreadId) ||
             string.Equals(state.Thread.ThreadId, appServerControlEvent.SessionId, StringComparison.Ordinal)))
        {
            if (!string.Equals(state.Thread.ThreadId, appServerControlEvent.ThreadId, StringComparison.Ordinal)) state.Thread.Goal = null;
            state.Thread.ThreadId = appServerControlEvent.ThreadId;
        }

        state.Session.LastEventAt = appServerControlEvent.CreatedAt;
        if (appServerControlEvent.AgentMetadataUpdated is { } metadata)
        {
            if (metadata.AgentName is not null) state.Session.AgentName = metadata.AgentName;
            if (metadata.Usage is not null) state.Session.Usage = metadata.Usage.Clone();
            if (metadata.AvailableCommands is not null) state.Session.AvailableCommands = AppServerControlCommand.CloneMany(metadata.AvailableCommands);
        }
        if (appServerControlEvent.GoalUpdated is { } goal &&
            string.Equals(goal.ThreadId, state.Thread.ThreadId, StringComparison.Ordinal))
            state.Thread.Goal = goal.Clone();
        ApplySessionState(state, appServerControlEvent);
        ApplyThreadState(state, appServerControlEvent);

        if (appServerControlEvent.TurnStarted is not null)
        {
            ApplyTurnStarted(state, appServerControlEvent);
        }

        if (appServerControlEvent.TurnCompleted is not null)
        {
            ApplyTurnCompleted(state, appServerControlEvent);
        }

        if (appServerControlEvent.QuickSettingsUpdated is not null)
        {
            ApplyQuickSettingsUpdate(state, appServerControlEvent);
        }

        if (appServerControlEvent.ContentDelta is not null)
        {
            ApplyContentDelta(state, appServerControlEvent);
        }

        if (appServerControlEvent.PlanDelta is not null || appServerControlEvent.PlanCompleted is not null)
        {
            ApplyPlanUpdate(state, appServerControlEvent);
        }

        if (appServerControlEvent.DiffUpdated is not null)
        {
            ApplyDiffUpdate(state, appServerControlEvent);
        }

        if (appServerControlEvent.Item is not null)
        {
            ApplyItemUpdate(state, appServerControlEvent);
        }

        if (appServerControlEvent.RequestOpened is not null ||
            appServerControlEvent.RequestResolved is not null ||
            appServerControlEvent.UserInputRequested is not null ||
            appServerControlEvent.UserInputResolved is not null)
        {
            ApplyRequestUpdate(state, appServerControlEvent);
        }

        if (appServerControlEvent.RuntimeMessage is not null)
        {
            ApplyRuntimeNotice(state, appServerControlEvent, appServerControlEvent.RuntimeMessage, timelineVisible: true);
        }

        if (appServerControlEvent.RuntimeNoticeOnly is not null)
        {
            ApplyRuntimeNotice(state, appServerControlEvent, appServerControlEvent.RuntimeNoticeOnly, timelineVisible: false);
        }
        PruneInactiveItemIndex(state);
        foreach (var id in CollectTouchedHistoryIds(state, appServerControlEvent))
        {
            if (state.HistoryEntries.TryGetValue(id, out var entry)) RefreshHistoryIndexes(state, entry);
        }
    }

    private AppServerControlHistoryWindowResponse CloneSnapshot(
        string sessionId,
        long latestSequence,
        AppServerControlConversationState state,
        int historyWindowStart,
        int? historyWindowCount,
        int? viewportWidth)
    {
        var totalHistoryCount = state.HistoryOrder.Count;
        var boundedStart = Math.Clamp(historyWindowStart, 0, totalHistoryCount == 0 ? 0 : totalHistoryCount - 1);
        var boundedCount = historyWindowCount is null
            ? totalHistoryCount
            : Math.Max(1, historyWindowCount.Value);
        if (totalHistoryCount == 0)
        {
            boundedStart = 0;
            boundedCount = 0;
        }

        var sliceCount = Math.Min(boundedCount, totalHistoryCount - boundedStart);
        var historySlice = new List<AppServerControlHistoryItem>(sliceCount);
        for (var index = boundedStart; index < boundedStart + sliceCount; index++)
        {
            historySlice.Add(CloneHistoryEntry(state.HistoryEntries[state.HistoryOrder.Values[index]], viewportWidth));
        }
        var historyWindowEnd = boundedStart + historySlice.Count;

        return new AppServerControlHistoryWindowResponse
        {
            SessionId = sessionId,
            Provider = state.Provider,
            GeneratedAt = state.Session.LastEventAt ?? DateTimeOffset.UtcNow,
            LatestSequence = latestSequence,
            HistoryCount = totalHistoryCount,
            HistoryWindowStart = boundedStart,
            HistoryWindowEnd = historyWindowEnd,
            HasOlderHistory = boundedStart > 0,
            HasNewerHistory = historyWindowEnd < totalHistoryCount,
            Session = CloneSessionSummary(state.Session),
            Thread = CloneThreadSummary(state.Thread),
            CurrentTurn = CloneTurnSummary(state.CurrentTurn),
            QuickSettings = CloneQuickSettingsSummary(state.QuickSettings),
            Streams = CloneStreamsSummary(state.Streams),
            History = historySlice,
            Items = historyWindowCount is not null ? CloneWindowItems(state, historySlice) : state.Items.Values
                .OrderByDescending(item => item.UpdatedAt)
                .Select(CloneItemSummary)
                .ToList(),
            Requests = historyWindowCount is not null ? CloneWindowRequests(state, historySlice) : state.Requests.Values
                .OrderByDescending(request => request.UpdatedAt)
                .Select(CloneRequestSummary)
                .ToList(),
            Notices = state.Notices
                .OrderByDescending(notice => notice.CreatedAt)
                .Select(CloneRuntimeNotice)
                .ToList()
        };
    }

    private static SessionAppServerControlHeatSnapshot BuildHeatSnapshot(AppServerControlConversationState state)
    {
        if (!ShouldSurfaceWorkingHeat(state))
        {
            return SessionAppServerControlHeatSnapshot.Cold;
        }

        return new SessionAppServerControlHeatSnapshot
        {
            CurrentHeat = 1,
            LastActivityAt = state.Session.LastEventAt ?? state.CurrentTurn.StartedAt
        };
    }

    private static void TrimEventBacklog(List<AppServerControlProviderEvent> eventBacklog)
    {
        if (eventBacklog.Count <= MaxTransientEventBacklog)
        {
            return;
        }

        eventBacklog.RemoveRange(0, eventBacklog.Count - MaxTransientEventBacklog);
    }

    private static AppServerControlPersistedSessionState BuildPersistedState(SessionAppServerControlHistoryLog log)
    {
        return new AppServerControlPersistedSessionState
        {
            NextSequence = log.NextSequence,
            State = BuildPersistedConversationState(log.State)
        };
    }

    private static AppServerControlPersistedConversationState BuildPersistedConversationState(AppServerControlConversationState source)
    {
        return new AppServerControlPersistedConversationState
        {
            Provider = source.Provider,
            Session = CloneSessionSummary(source.Session),
            Thread = CloneThreadSummary(source.Thread),
            CurrentTurn = CloneTurnSummary(source.CurrentTurn),
            QuickSettings = CloneQuickSettingsSummary(source.QuickSettings),
            Streams = CloneStreamsSummary(source.Streams),
            HistoryEntries = source.HistoryEntries.Values
                .OrderBy(entry => entry.Order)
                .Select(entry => CloneHistoryEntry(entry))
                .ToList(),
            Items = source.Items.Values
                .OrderBy(item => item.ItemId, StringComparer.Ordinal)
                .Select(CloneItemSummary)
                .ToList(),
            Requests = source.Requests.Values
                .OrderBy(request => request.RequestId, StringComparer.Ordinal)
                .Select(CloneRequestSummary)
                .ToList(),
            ToolRenderStates = source.ToolRenderStates
                .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                .Select(static pair => new AppServerControlPersistedToolRenderState
                {
                    EntryId = pair.Key,
                    Presentation = AppServerControlToolPresentationProtocol.Clone(pair.Value.Presentation),
                    Output = pair.Value.Output.Export()
                })
                .ToList(),
            Notices = source.Notices.Select(CloneRuntimeNotice).ToList(),
            NextHistoryOrder = source.NextHistoryOrder
        };
    }

    private static void RestoreConversationState(AppServerControlConversationState target, AppServerControlPersistedConversationState source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);
        target.Provider = source.Provider;
        target.NextHistoryOrder = source.NextHistoryOrder;

        CopySessionSummary(target.Session, source.Session);
        CopyThreadSummary(target.Thread, source.Thread);
        CopyTurnSummary(target.CurrentTurn, source.CurrentTurn);
        CopyQuickSettingsSummary(target.QuickSettings, source.QuickSettings);
        CopyStreamsSummary(target.Streams, source.Streams);

        ClearHistoryEntries(target);
        foreach (var entry in source.HistoryEntries)
        {
            SetHistoryEntry(target, CloneHistoryEntry(entry));
        }

        target.Items.Clear();
        target.UserItemsByTurn.Clear();
        target.UserItemTurns.Clear();
        target.ItemAliases.Clear();
        target.ActiveItemIds.Clear();
        foreach (var item in source.Items)
        {
            SetItemSummary(target, CloneItemSummary(item));
        }

        target.Requests.Clear();
        target.OpenRequestIds.Clear();
        foreach (var request in source.Requests)
        {
            target.Requests[request.RequestId] = CloneRequestSummary(request);
            RefreshRequestIndex(target, request);
        }

        target.ToolRenderStates.Clear();
        foreach (var toolState in source.ToolRenderStates)
        {
            target.ToolRenderStates[toolState.EntryId] = new AppServerControlToolRenderState
            {
                Presentation = AppServerControlToolPresentationProtocol.Clone(toolState.Presentation)
            };
            target.ToolRenderStates[toolState.EntryId].Output.Restore(toolState.Output);
        }

        target.Notices.Clear();
        target.Notices.AddRange(source.Notices.Select(CloneRuntimeNotice));
    }

    private static void ImportHistoryWindow(AppServerControlConversationState target, AppServerControlHistoryWindowResponse source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        target.Provider = source.Provider;
        target.NextHistoryOrder = 0;

        CopySessionSummary(target.Session, source.Session);
        CopyThreadSummary(target.Thread, source.Thread);
        CopyTurnSummary(target.CurrentTurn, source.CurrentTurn);
        CopyQuickSettingsSummary(target.QuickSettings, source.QuickSettings);
        CopyStreamsSummary(target.Streams, source.Streams);

        ClearHistoryEntries(target);
        foreach (var entry in source.History.OrderBy(static item => item.Order))
        {
            SetHistoryEntry(target, CloneHistoryEntry(entry));
            target.NextHistoryOrder = Math.Max(target.NextHistoryOrder, entry.Order + 1);
        }

        target.Items.Clear();
        target.UserItemsByTurn.Clear();
        target.UserItemTurns.Clear();
        target.ItemAliases.Clear();
        target.ActiveItemIds.Clear();
        foreach (var item in source.Items)
        {
            SetItemSummary(target, CloneItemSummary(item));
        }

        target.Requests.Clear();
        target.OpenRequestIds.Clear();
        foreach (var request in source.Requests)
        {
            target.Requests[request.RequestId] = CloneRequestSummary(request);
            RefreshRequestIndex(target, request);
        }

        target.ToolRenderStates.Clear();

        target.Notices.Clear();
        target.Notices.AddRange(source.Notices.Select(CloneRuntimeNotice));
    }

    private static void ApplyHistoryPatch(AppServerControlConversationState target, AppServerControlHistoryPatch source)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(source);

        target.Provider = source.Provider;

        CopySessionSummary(target.Session, source.Session);
        CopyThreadSummary(target.Thread, source.Thread);
        CopyTurnSummary(target.CurrentTurn, source.CurrentTurn);
        CopyQuickSettingsSummary(target.QuickSettings, source.QuickSettings);
        CopyStreamsSummary(target.Streams, source.Streams);

        foreach (var historyId in source.HistoryRemovals)
        {
            RemoveHistoryEntry(target, historyId);
            target.ToolRenderStates.Remove(historyId);
        }

        foreach (var entry in source.HistoryUpserts)
        {
            SetHistoryEntry(target, CloneHistoryEntry(entry));
        }

        target.NextHistoryOrder = target.HistoryEntries.Count == 0
            ? 0
            : target.HistoryOrder.Keys[target.HistoryOrder.Count - 1].Order + 1;

        foreach (var itemId in source.ItemRemovals)
        {
            RemoveItemSummary(target, itemId);
        }

        foreach (var item in source.ItemUpserts)
        {
            SetItemSummary(target, CloneItemSummary(item));
        }

        foreach (var requestId in source.RequestRemovals)
        {
            target.Requests.Remove(requestId);
            target.OpenRequestIds.Remove(requestId);
        }

        foreach (var request in source.RequestUpserts)
        {
            target.Requests[request.RequestId] = CloneRequestSummary(request);
            RefreshRequestIndex(target, request);
        }
        PruneInactiveItemIndex(target);

        var noticesById = target.Notices
            .Where(static notice => !string.IsNullOrWhiteSpace(notice.EventId))
            .ToDictionary(static notice => notice.EventId, CloneRuntimeNotice, StringComparer.Ordinal);
        foreach (var notice in source.NoticeUpserts)
        {
            noticesById[notice.EventId] = CloneRuntimeNotice(notice);
        }

        target.Notices.Clear();
        target.Notices.AddRange(noticesById.Values.OrderByDescending(static notice => notice.CreatedAt));
    }

    private static bool ShouldSurfaceWorkingHeat(AppServerControlConversationState state)
    {
        if (state.OpenRequestIds.Count > 0)
        {
            return false;
        }

        if (IsWorkingTurnState(state.CurrentTurn.State))
        {
            return true;
        }

        return string.IsNullOrWhiteSpace(state.CurrentTurn.State) &&
               IsWorkingSessionState(state.Session.State);
    }

    private static bool IsWorkingTurnState(string? state)
    {
        return state?.Trim().ToLowerInvariant() switch
        {
            "running" => true,
            "in_progress" => true,
            "started" => true,
            _ => false
        };
    }

    private static bool IsWorkingSessionState(string? state)
    {
        return state?.Trim().ToLowerInvariant() switch
        {
            "starting" => true,
            "running" => true,
            _ => false
        };
    }

    private AppServerControlHistoryPatch BuildDelta(
        string sessionId,
        long latestSequence,
        AppServerControlConversationState state,
        AppServerControlProviderEvent appServerControlEvent)
    {
        var historyIds = CollectTouchedHistoryIds(state, appServerControlEvent);
        var itemIds = CollectTouchedItemIds(state, appServerControlEvent);
        var requestIds = CollectTouchedRequestIds(appServerControlEvent);
        var noticeIds = CollectTouchedNoticeIds(appServerControlEvent);

        return new AppServerControlHistoryPatch
        {
            SessionId = sessionId,
            Provider = state.Provider,
            GeneratedAt = state.Session.LastEventAt ?? appServerControlEvent.CreatedAt,
            LatestSequence = latestSequence,
            HistoryCount = state.HistoryEntries.Count,
            Session = CloneSessionSummary(state.Session),
            Thread = CloneThreadSummary(state.Thread),
            CurrentTurn = CloneTurnSummary(state.CurrentTurn),
            QuickSettings = CloneQuickSettingsSummary(state.QuickSettings),
            Streams = CloneStreamsSummary(state.Streams),
            HistoryUpserts = historyIds
                .Select(id => state.HistoryEntries.TryGetValue(id, out var entry) ? CloneHistoryEntry(sessionId, entry) : null)
                .Where(static entry => entry is not null)
                .Select(static entry => entry!)
                .OrderBy(entry => entry.Order)
                .ToList(),
            ItemUpserts = itemIds
                .Select(id => state.Items.TryGetValue(id, out var item) ? CloneItemSummary(item) : null)
                .Where(static item => item is not null)
                .Select(static item => item!)
                .OrderByDescending(item => item.UpdatedAt)
                .ToList(),
            RequestUpserts = requestIds
                .Select(id => state.Requests.TryGetValue(id, out var request) ? CloneRequestSummary(request) : null)
                .Where(static request => request is not null)
                .Select(static request => request!)
                .OrderByDescending(request => request.UpdatedAt)
                .ToList(),
            NoticeUpserts = noticeIds
                .Select(id => state.Notices.LastOrDefault(notice => string.Equals(notice.EventId, id, StringComparison.Ordinal)))
                .Where(static notice => notice is not null)
                .Select(static notice => CloneRuntimeNotice(notice!))
                .OrderByDescending(notice => notice.CreatedAt)
                .ToList()
        };
    }

    private static void ApplyTurnStarted(AppServerControlConversationState state, AppServerControlProviderEvent appServerControlEvent)
    {
        if (!string.IsNullOrWhiteSpace(appServerControlEvent.TurnId) &&
            !string.Equals(state.CurrentTurn.TurnId, appServerControlEvent.TurnId, StringComparison.Ordinal))
        {
            ResetStreams(state.Streams);
        }

        state.CurrentTurn.TurnId = appServerControlEvent.TurnId;
        state.CurrentTurn.State = "running";
        state.CurrentTurn.StateLabel = "Running";
        var model = AppServerControlQuickSettings.NormalizeOptionalValue(appServerControlEvent.TurnStarted?.Model);
        var effort = AppServerControlQuickSettings.NormalizeOptionalValue(appServerControlEvent.TurnStarted?.Effort);
        var replay = appServerControlEvent.TurnStarted?.IsReplay == true;
        state.CurrentTurn.Model = model ?? (replay ? null : state.QuickSettings.Model);
        state.CurrentTurn.Effort = effort ?? (replay ? null : state.QuickSettings.Effort);
        state.CurrentTurn.StartedAt = appServerControlEvent.CreatedAt;
        state.CurrentTurn.CompletedAt = null;
        if (model is not null && !replay)
        {
            state.QuickSettings.Model = model;
        }

        if (effort is not null && !replay)
        {
            state.QuickSettings.Effort = effort;
        }
    }

    private static void ApplyTurnCompleted(AppServerControlConversationState state, AppServerControlProviderEvent appServerControlEvent)
    {
        if (!string.IsNullOrWhiteSpace(appServerControlEvent.TurnId))
        {
            state.CurrentTurn.TurnId = appServerControlEvent.TurnId;
        }

        state.CurrentTurn.State = appServerControlEvent.TurnCompleted?.State ?? state.CurrentTurn.State;
        state.CurrentTurn.StateLabel = appServerControlEvent.TurnCompleted?.StateLabel ?? state.CurrentTurn.StateLabel;
        state.CurrentTurn.CompletedAt = appServerControlEvent.CreatedAt;
        if (!string.IsNullOrWhiteSpace(appServerControlEvent.TurnCompleted?.ErrorMessage))
        {
            state.Session.LastError = appServerControlEvent.TurnCompleted.ErrorMessage;
        }

        CompleteStreamingHistoryEntries(state, appServerControlEvent.TurnId);
    }

    private static void ApplyQuickSettingsUpdate(AppServerControlConversationState state, AppServerControlProviderEvent appServerControlEvent)
    {
        var quickSettings = appServerControlEvent.QuickSettingsUpdated;
        if (quickSettings is null)
        {
            return;
        }

        state.QuickSettings.Model = AppServerControlQuickSettings.NormalizeOptionalValue(quickSettings.Model);
        state.QuickSettings.Effort = AppServerControlQuickSettings.NormalizeOptionalValue(quickSettings.Effort);
        state.QuickSettings.PlanMode = AppServerControlQuickSettings.NormalizePlanMode(quickSettings.PlanMode);
        state.QuickSettings.PermissionMode = AppServerControlQuickSettings.NormalizePermissionMode(quickSettings.PermissionMode);
        state.QuickSettings.FastMode = AppServerControlQuickSettings.NormalizeFastMode(quickSettings.FastMode);
        state.QuickSettings.ModelOptions = AppServerControlQuickSettings.CloneOptions(quickSettings.ModelOptions);
        state.QuickSettings.EffortOptions = AppServerControlQuickSettings.CloneOptions(quickSettings.EffortOptions);
        state.QuickSettings.AcknowledgedFields = [.. quickSettings.AcknowledgedFields];
        state.QuickSettings.ConfigOptions = AppServerControlQuickSettings.CloneConfigOptions(quickSettings.ConfigOptions);
    }

    private static void ApplyPlanUpdate(AppServerControlConversationState state, AppServerControlProviderEvent appServerControlEvent)
    {
        if (!string.IsNullOrWhiteSpace(appServerControlEvent.PlanDelta?.Delta))
        {
            state.Streams.PlanText = AppendRetainedHistoryChunk(
                state.Streams.PlanText,
                appServerControlEvent.PlanDelta.Delta,
                MaxPlanStreamChars,
                retainHead: false);
        }

        if (!string.IsNullOrWhiteSpace(appServerControlEvent.PlanCompleted?.PlanMarkdown))
        {
            state.Streams.PlanText = RetainWithinBudget(
                NormalizeHistoryText(appServerControlEvent.PlanCompleted.PlanMarkdown),
                MaxPlanStreamChars,
                retainHead: false);
        }

        var entry = EnsureHistoryEntry(
            state,
            $"plan:{appServerControlEvent.TurnId ?? state.CurrentTurn.TurnId ?? appServerControlEvent.EventId}",
            "plan",
            appServerControlEvent.CreatedAt);
        entry.TurnId = appServerControlEvent.TurnId ?? state.CurrentTurn.TurnId;
        entry.Status = "completed";
        entry.Title = "Plan";
        if (appServerControlEvent.PlanCompleted?.IsProposal == true) entry.ItemType = "proposed_plan";
        entry.Body = !string.IsNullOrWhiteSpace(appServerControlEvent.PlanCompleted?.PlanMarkdown)
            ? RetainWithinBudget(NormalizeHistoryText(appServerControlEvent.PlanCompleted.PlanMarkdown), MaxPlanStreamChars, retainHead: false)
            : AppendRetainedHistoryChunk(entry.Body, appServerControlEvent.PlanDelta?.Delta, MaxPlanStreamChars, retainHead: false);
        entry.Streaming = false;
        entry.UpdatedAt = appServerControlEvent.CreatedAt;
    }

    private static void ApplyDiffUpdate(AppServerControlConversationState state, AppServerControlProviderEvent appServerControlEvent)
    {
        state.Streams.UnifiedDiff = RetainWithinBudget(
            NormalizeHistoryText(appServerControlEvent.DiffUpdated?.UnifiedDiff),
            MaxUnifiedDiffChars,
            retainHead: false);
        var entry = EnsureHistoryEntry(
            state,
            $"diff:{appServerControlEvent.TurnId ?? state.CurrentTurn.TurnId ?? appServerControlEvent.EventId}",
            "diff",
            appServerControlEvent.CreatedAt);
        entry.TurnId = appServerControlEvent.TurnId ?? state.CurrentTurn.TurnId;
        entry.Status = "updated";
        entry.Title = "Working diff";
        entry.Body = state.Streams.UnifiedDiff;
        entry.Streaming = false;
        entry.UpdatedAt = appServerControlEvent.CreatedAt;
    }

    private static void ApplySessionState(AppServerControlHistoryWindowResponse snapshot, AppServerControlProviderEvent appServerControlEvent)
    {
        if (appServerControlEvent.SessionState is null)
        {
            return;
        }

        snapshot.Session.State = appServerControlEvent.SessionState.State;
        snapshot.Session.StateLabel = appServerControlEvent.SessionState.StateLabel;
        snapshot.Session.Reason = appServerControlEvent.SessionState.Reason;
        if (string.Equals(appServerControlEvent.SessionState.State, "error", StringComparison.OrdinalIgnoreCase))
        {
            snapshot.Session.LastError = appServerControlEvent.SessionState.Reason;
        }
    }

    private static void ApplyThreadState(AppServerControlHistoryWindowResponse snapshot, AppServerControlProviderEvent appServerControlEvent)
    {
        if (appServerControlEvent.ThreadState is null)
        {
            return;
        }

        snapshot.Thread.State = appServerControlEvent.ThreadState.State;
        snapshot.Thread.StateLabel = appServerControlEvent.ThreadState.StateLabel;
        if (!string.IsNullOrWhiteSpace(appServerControlEvent.ThreadState.ProviderThreadId))
        {
            snapshot.Thread.ThreadId = appServerControlEvent.ThreadState.ProviderThreadId;
        }
    }

    private static void ApplySessionState(AppServerControlConversationState state, AppServerControlProviderEvent appServerControlEvent)
    {
        if (appServerControlEvent.SessionState is null)
        {
            return;
        }

        state.Session.State = appServerControlEvent.SessionState.State;
        state.Session.StateLabel = appServerControlEvent.SessionState.StateLabel;
        state.Session.Reason = appServerControlEvent.SessionState.Reason;
        if (string.Equals(appServerControlEvent.SessionState.State, "error", StringComparison.OrdinalIgnoreCase))
        {
            state.Session.LastError = appServerControlEvent.SessionState.Reason;
        }
    }

    private static void ApplyThreadState(AppServerControlConversationState state, AppServerControlProviderEvent appServerControlEvent)
    {
        if (appServerControlEvent.ThreadState is null)
        {
            return;
        }

        state.Thread.State = appServerControlEvent.ThreadState.State;
        state.Thread.StateLabel = appServerControlEvent.ThreadState.StateLabel;
        if (!string.IsNullOrWhiteSpace(appServerControlEvent.ThreadState.ProviderThreadId))
        {
            if (!string.Equals(state.Thread.ThreadId, appServerControlEvent.ThreadState.ProviderThreadId, StringComparison.Ordinal)) state.Thread.Goal = null;
            state.Thread.ThreadId = appServerControlEvent.ThreadState.ProviderThreadId;
        }
    }

    private static void ApplyContentDelta(
        AppServerControlProviderEvent appServerControlEvent,
        StringBuilder assistant,
        StringBuilder reasoning,
        StringBuilder reasoningSummary,
        StringBuilder plan,
        StringBuilder commandOutput,
        StringBuilder fileChangeOutput)
    {
        if (appServerControlEvent.ContentDelta is null)
        {
            return;
        }

        switch (appServerControlEvent.ContentDelta.StreamKind)
        {
            case "assistant_text":
                assistant.Append(appServerControlEvent.ContentDelta.Delta);
                break;
            case "reasoning_text":
                reasoning.Append(appServerControlEvent.ContentDelta.Delta);
                break;
            case "reasoning_summary_text":
                reasoningSummary.Append(appServerControlEvent.ContentDelta.Delta);
                break;
            case "plan_text":
                plan.Append(appServerControlEvent.ContentDelta.Delta);
                break;
            case "command_output":
                commandOutput.Append(appServerControlEvent.ContentDelta.Delta);
                break;
            case "file_change_output":
                fileChangeOutput.Append(appServerControlEvent.ContentDelta.Delta);
                break;
        }
    }

    private static void ApplyContentDelta(AppServerControlConversationState state, AppServerControlProviderEvent appServerControlEvent)
    {
        if (appServerControlEvent.ContentDelta is null)
        {
            return;
        }

        if (!appServerControlEvent.ContentDelta.Replace &&
            (appServerControlEvent.ItemId?.StartsWith("acp:", StringComparison.Ordinal) != true ||
             string.Equals(appServerControlEvent.TurnId, state.CurrentTurn.TurnId, StringComparison.Ordinal)))
            switch (appServerControlEvent.ContentDelta.StreamKind)
            {
                case "assistant_text":
                    state.Streams.AssistantText = AppendAssistantDelta(state.Streams.AssistantText, appServerControlEvent.ContentDelta.Delta);
                    break;
                case "reasoning_text":
                    state.Streams.ReasoningText = AppendRetainedHistoryChunk(
                        state.Streams.ReasoningText,
                        appServerControlEvent.ContentDelta.Delta,
                        MaxReasoningStreamChars,
                        retainHead: false);
                    break;
                case "reasoning_summary_text":
                    state.Streams.ReasoningSummaryText = AppendRetainedHistoryChunk(
                        state.Streams.ReasoningSummaryText,
                        appServerControlEvent.ContentDelta.Delta,
                        MaxReasoningSummaryStreamChars,
                        retainHead: false);
                    break;
                case "plan_text":
                    state.Streams.PlanText = AppendRetainedHistoryChunk(
                        state.Streams.PlanText,
                        appServerControlEvent.ContentDelta.Delta,
                        MaxPlanStreamChars,
                        retainHead: false);
                    break;
                case "command_output":
                    state.Streams.CommandOutput = AppendRetainedHistoryChunk(
                        state.Streams.CommandOutput,
                        appServerControlEvent.ContentDelta.Delta,
                        MaxCommandOutputStreamChars,
                        retainHead: false);
                    break;
                case "file_change_output":
                    state.Streams.FileChangeOutput = AppendRetainedHistoryChunk(
                        state.Streams.FileChangeOutput,
                        appServerControlEvent.ContentDelta.Delta,
                        MaxFileChangeOutputStreamChars,
                        retainHead: false);
                    break;
            }

        var historyKind = HistoryKindFromStream(appServerControlEvent.ContentDelta.StreamKind);
        if (historyKind is null)
        {
            return;
        }

        var entry = EnsureHistoryEntry(
            state,
            ResolveHistoryEntryIdForStream(state, appServerControlEvent, historyKind, appServerControlEvent.ContentDelta.StreamKind),
            historyKind,
            appServerControlEvent.CreatedAt);
        entry.TurnId = appServerControlEvent.TurnId ?? state.CurrentTurn.TurnId;
        entry.ItemId = appServerControlEvent.ItemId;
        entry.ItemType = appServerControlEvent.ContentDelta.StreamKind;
        entry.Status = "streaming";
        if (appServerControlEvent.ContentDelta.Replace)
        {
            entry.Title = historyKind == "user" ? null : ResolveStreamTitle(historyKind, appServerControlEvent.ContentDelta.StreamKind, entry.Title);
            entry.Body = AppendRetainedStreamText(string.Empty, appServerControlEvent.ContentDelta.Delta,
                historyKind is "assistant" or "user" ? MaxAssistantStreamChars : MaxReasoningStreamChars,
                retainHead: false, separateWithNewline: false);
            entry.Streaming = historyKind != "user";
            if (historyKind == "user")
            {
                entry.ItemType = "user_message";
                entry.Status = "completed";
                PromoteUserHistoryEntryToTurnLead(state, entry);
            }
            if (string.Equals(entry.TurnId, state.CurrentTurn.TurnId, StringComparison.Ordinal))
            {
                var text = CollectTurnStreamTail(state, entry,
                    historyKind == "assistant" ? MaxAssistantStreamChars : MaxReasoningStreamChars);
                if (historyKind == "assistant") state.Streams.AssistantText = AppendAssistantDelta(string.Empty, text);
                if (historyKind == "reasoning") state.Streams.ReasoningText = AppendRetainedHistoryChunk(string.Empty, text, MaxReasoningStreamChars, retainHead: false);
            }
        }
        else if (historyKind == "user")
        {
            entry.Title = null;
            entry.Body = AppendAssistantDelta(entry.Body, appServerControlEvent.ContentDelta.Delta);
            entry.ItemType = "user_message";
            entry.Status = "completed";
            entry.Streaming = false;
            PromoteUserHistoryEntryToTurnLead(state, entry);
        }
        else if (historyKind == "assistant")
        {
            entry.Title = ResolveStreamTitle(historyKind, appServerControlEvent.ContentDelta.StreamKind, entry.Title);
            entry.Body = AppendAssistantDelta(entry.Body, appServerControlEvent.ContentDelta.Delta);
            entry.Streaming = true;
        }
        else if (historyKind == "tool")
        {
            entry.ItemId ??= ResolveToolItemIdFromHistoryEntryId(
                entry.EntryId,
                appServerControlEvent.ContentDelta.StreamKind);
            var toolState = GetOrCreateToolRenderState(state, entry.EntryId);
            toolState.Presentation ??=
                AppServerControlToolPresentationProtocol.Clone(entry.ToolPresentation) ??
                AppServerControlToolPresentationProjector.FromLegacy(
                    appServerControlEvent.ContentDelta.StreamKind,
                    "in_progress",
                    entry.Title,
                    null);
            toolState.Output.Append(appServerControlEvent.ContentDelta.Delta);
            AppServerControlToolPresentationProjector.ApplyOutput(
                toolState.Presentation,
                toolState.Output,
                "in_progress");
            ApplyToolPresentation(entry, toolState.Presentation);
            entry.Streaming = true;
        }
        else
        {
            entry.Title = ResolveStreamTitle(historyKind, appServerControlEvent.ContentDelta.StreamKind, entry.Title);
            entry.Body = appServerControlEvent.ItemId?.StartsWith("acp:", StringComparison.Ordinal) == true
                ? AppendRetainedStreamText(entry.Body, appServerControlEvent.ContentDelta.Delta, MaxReasoningStreamChars, retainHead: false, separateWithNewline: false)
                : AppendHistoryChunk(entry.Body, appServerControlEvent.ContentDelta.Delta);
            entry.Streaming = true;
        }

        entry.UpdatedAt = appServerControlEvent.CreatedAt;
    }

    private static AppServerControlRequestSummary GetOrCreateRequestSummary(
        IDictionary<string, AppServerControlRequestSummary> requests,
        string requestId,
        string? turnId)
    {
        if (!requests.TryGetValue(requestId, out var request))
        {
            request = new AppServerControlRequestSummary
            {
                RequestId = requestId,
                TurnId = turnId,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            requests[requestId] = request;
        }

        return request;
    }

    private static AppServerControlItemSummary GetOrCreateItemSummary(
        IDictionary<string, AppServerControlItemSummary> items,
        string itemId,
        string? turnId)
    {
        if (!items.TryGetValue(itemId, out var item))
        {
            item = new AppServerControlItemSummary
            {
                ItemId = itemId,
                TurnId = turnId,
                UpdatedAt = DateTimeOffset.UtcNow
            };
            items[itemId] = item;
        }

        return item;
    }

    private static string HumanizeRequestType(string requestType)
    {
        return requestType switch
        {
            "command_execution_approval" => "Command approval",
            "file_read_approval" => "File read approval",
            "file_change_approval" => "File change approval",
            "permissions_approval" => "Permissions approval",
            "interview" => "Interview",
            _ => requestType
        };
    }

    private static AppServerControlToolRenderState GetOrCreateToolRenderState(
        AppServerControlConversationState state,
        string entryId)
    {
        if (!state.ToolRenderStates.TryGetValue(entryId, out var toolState))
        {
            toolState = new AppServerControlToolRenderState();
            state.ToolRenderStates[entryId] = toolState;
        }

        return toolState;
    }

    private static void ApplyToolPresentation(
        AppServerControlHistoryItem entry,
        AppServerControlToolPresentation presentation)
    {
        entry.ToolPresentation = AppServerControlToolPresentationProtocol.Clone(presentation);
        entry.Title = presentation.Label;
        entry.CommandText = null;
        entry.Body = BuildToolPresentationBody(presentation);
    }

    private static string BuildToolPresentationBody(AppServerControlToolPresentation presentation)
    {
        var builder = new StringBuilder();
        if (string.Equals(presentation.EvidenceKind, "error", StringComparison.Ordinal))
        {
            AppendToolPresentationSection(builder, presentation.Outcome);
        }
        AppendToolPresentationSection(builder, presentation.Subject);
        foreach (var path in presentation.Paths)
        {
            if (!string.Equals(path, presentation.Subject, StringComparison.Ordinal))
            {
                AppendToolPresentationSection(builder, path);
            }
        }
        if (!string.Equals(presentation.EvidenceKind, "error", StringComparison.Ordinal))
        {
            AppendToolPresentationSection(builder, presentation.Outcome);
        }
        if (!string.IsNullOrWhiteSpace(presentation.Evidence))
        {
            if (presentation.OmittedLineCount > 0)
            {
                AppendToolPresentationSection(
                    builder,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"... {presentation.OmittedLineCount} lines omitted ..."));
            }
            AppendToolPresentationSection(builder, presentation.Evidence);
        }
        return builder.ToString();
    }

    private static void AppendToolPresentationSection(StringBuilder builder, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return;
        }
        if (builder.Length > 0)
        {
            builder.AppendLine();
        }
        builder.Append(value);
    }

    private static void ApplyItemUpdate(AppServerControlConversationState state, AppServerControlProviderEvent appServerControlEvent)
    {
        if (appServerControlEvent.Item is null)
        {
            return;
        }

        var normalizedType = NormalizeItemType(appServerControlEvent.Item.ItemType);
        var historyKind = HistoryKindFromItem(normalizedType);
        var canonicalItemId = ResolveCanonicalItemId(state, appServerControlEvent, historyKind);
        var item = GetOrCreateItemSummary(state.Items, canonicalItemId, appServerControlEvent.TurnId);
        item.TurnId = appServerControlEvent.TurnId ?? item.TurnId;
        item.ItemType = normalizedType;
        var submittedUserInput = historyKind == "user" && IsLocalUserItemId(appServerControlEvent.ItemId ?? string.Empty);
        var hasSubmittedUserInput = historyKind == "user" && (IsLocalUserItemId(item.ItemId) || item.AliasItemIds.Any(IsLocalUserItemId));
        item.Status = historyKind == "user"
            ? ChoosePreferredUserMessageStatus(item.Status, appServerControlEvent.Item.Status)
            : PreferNonEmpty(appServerControlEvent.Item.Status, item.Status);
        item.Title = historyKind == "tool"
            ? PreferNonGenericText(item.Title, appServerControlEvent.Item.Title)
            : PreferMeaningfulText(item.Title, appServerControlEvent.Item.Title);
        item.Detail = appServerControlEvent.Item.Replace ? appServerControlEvent.Item.Detail : historyKind switch
        {
            "assistant" => MergeAssistantItemDetail(item.Detail, appServerControlEvent.Item.Detail, appServerControlEvent.Item.Status),
            "user" when hasSubmittedUserInput => submittedUserInput ? appServerControlEvent.Item.Detail : item.Detail,
            "user" => PreferMeaningfulText(item.Detail, appServerControlEvent.Item.Detail),
            _ => MergeHistoryBody(item.Detail, appServerControlEvent.Item.Detail)
        };
        item.Attachments = MergeAttachments(item.Attachments, appServerControlEvent.Item.Attachments);
        item.UpdatedAt = appServerControlEvent.CreatedAt;

        var entry = EnsureHistoryEntry(
            state,
            ResolveHistoryEntryIdForItem(state, appServerControlEvent, historyKind, canonicalItemId),
            historyKind,
            appServerControlEvent.CreatedAt);
        entry.TurnId = appServerControlEvent.TurnId ?? entry.TurnId;
        entry.ItemId = canonicalItemId;
        entry.Status = item.Status;
        entry.ItemType = normalizedType;
        entry.Attachments = MergeAttachments(entry.Attachments, appServerControlEvent.Item.Attachments);
        entry.UpdatedAt = appServerControlEvent.CreatedAt;

        switch (historyKind)
        {
            case "user":
                entry.CommandText = null;
                entry.Title = null;
                entry.Body = hasSubmittedUserInput ? item.Detail ?? string.Empty
                    : appServerControlEvent.Item.Replace ? appServerControlEvent.Item.Detail ?? string.Empty
                    : PreferMeaningfulText(entry.Body, appServerControlEvent.Item.Detail) ?? string.Empty;
                entry.Streaming = false;
                PromoteUserHistoryEntryToTurnLead(state, entry);
                break;
            case "assistant":
                entry.CommandText = null;
                entry.Title = null;
                entry.Body = appServerControlEvent.Item.Replace ? appServerControlEvent.Item.Detail ?? string.Empty
                    : MergeAssistantItemDetail(entry.Body, appServerControlEvent.Item.Detail, item.Status);
                entry.Streaming = !IsTerminalStatus(item.Status);
                break;
            case "reasoning":
            case "plan":
                entry.CommandText = null;
                entry.Title = PreferMeaningfulText(entry.Title, appServerControlEvent.Item.Title);
                entry.Body = appServerControlEvent.Item.Replace ? appServerControlEvent.Item.Detail ?? string.Empty
                    : MergeHistoryBody(entry.Body, appServerControlEvent.Item.Detail);
                entry.Streaming = !IsTerminalStatus(item.Status);
                item.Detail = entry.Body;
                break;
            default:
                if (appServerControlEvent.Item.Replace) state.ToolRenderStates.Remove(entry.EntryId);
                var toolState = GetOrCreateToolRenderState(state, entry.EntryId);
                var incomingPresentation = appServerControlEvent.Item.ToolPresentation ??
                                           AppServerControlToolPresentationProjector.FromLegacy(
                                               normalizedType,
                                               item.Status,
                                               appServerControlEvent.Item.Title,
                                               appServerControlEvent.Item.Detail);
                if (toolState.Presentation is null)
                {
                    toolState.Presentation = AppServerControlToolPresentationProjector.Clone(incomingPresentation);
                }
                else
                {
                    AppServerControlToolPresentationProjector.Merge(toolState.Presentation, incomingPresentation);
                }

                entry.ItemType = normalizedType;
                if (toolState.Output.HasContent)
                {
                    AppServerControlToolPresentationProjector.ApplyOutput(
                        toolState.Presentation,
                        toolState.Output,
                        item.Status);
                }
                ApplyToolPresentation(entry, toolState.Presentation);
                entry.Streaming = !IsTerminalStatus(item.Status);
                item.Detail = entry.Body;
                if (IsTerminalStatus(item.Status))
                {
                    state.ToolRenderStates.Remove(entry.EntryId);
                }
                break;
        }
        RefreshItemIndexes(state, item);
    }

    private static void ApplyRequestUpdate(AppServerControlConversationState state, AppServerControlProviderEvent appServerControlEvent)
    {
        var requestId = string.IsNullOrWhiteSpace(appServerControlEvent.RequestId)
            ? $"request:{appServerControlEvent.EventId}"
            : appServerControlEvent.RequestId;
        var request = GetOrCreateRequestSummary(state.Requests, requestId, appServerControlEvent.TurnId);
        request.TurnId = appServerControlEvent.TurnId ?? request.TurnId;
        request.UpdatedAt = appServerControlEvent.CreatedAt;

        if (appServerControlEvent.RequestOpened is not null)
        {
            request.Kind = appServerControlEvent.RequestOpened.RequestType;
            request.KindLabel = PreferNonEmpty(
                appServerControlEvent.RequestOpened.RequestTypeLabel,
                HumanizeRequestType(appServerControlEvent.RequestOpened.RequestType));
            request.State = "open";
            request.Detail = PreferMeaningfulText(request.Detail, appServerControlEvent.RequestOpened.Detail);
        }

        if (appServerControlEvent.RequestResolved is not null)
        {
            request.Kind = PreferNonEmpty(appServerControlEvent.RequestResolved.RequestType, request.Kind);
            request.KindLabel = PreferNonEmpty(request.KindLabel, HumanizeRequestType(request.Kind));
            request.State = "resolved";
            request.Decision = appServerControlEvent.RequestResolved.Decision;
            if (request.Kind == "mcp_tool_approval")
                request.Detail = AppServerControlProviderEventCompaction.CompactRequestDetail(request.Detail, request.Kind, pending: false);
        }

        if (appServerControlEvent.UserInputRequested is not null)
        {
            request.Kind = "interview";
            request.KindLabel = HumanizeRequestType(request.Kind);
            request.State = "open";
            request.Questions = appServerControlEvent.UserInputRequested.Questions.Select(CloneQuestion).ToList();
        }

        if (appServerControlEvent.UserInputResolved is not null)
        {
            request.Kind = PreferNonEmpty(request.Kind, "interview");
            request.KindLabel = PreferNonEmpty(request.KindLabel, HumanizeRequestType(request.Kind));
            request.State = "resolved";
            request.Answers = appServerControlEvent.UserInputResolved.Answers.Select(CloneAnsweredQuestion).ToList();
        }

        var entry = EnsureHistoryEntry(state, $"request:{requestId}", "request", appServerControlEvent.CreatedAt);
        entry.TurnId = request.TurnId;
        entry.RequestId = request.RequestId;
        entry.Status = string.Equals(request.Decision, "cancel", StringComparison.Ordinal) ? "cancelled" : request.State;
        entry.ItemType = request.Kind;
        entry.Title = request.KindLabel;
        entry.Body = BuildRequestBody(request);
        entry.Questions = request.Questions.Select(CloneQuestion).ToList();
        entry.Answers = request.Answers.Select(CloneAnsweredQuestion).ToList();
        entry.Streaming = false;
        entry.UpdatedAt = appServerControlEvent.CreatedAt;
        RefreshRequestIndex(state, request);
    }

    private static void ApplyRuntimeNotice(
        AppServerControlConversationState state,
        AppServerControlProviderEvent appServerControlEvent,
        AppServerControlProviderRuntimeMessagePayload runtimeMessage,
        bool timelineVisible)
    {
        var message = AppServerControlHistoryTextSanitizer.Sanitize(runtimeMessage.Message);
        var detail = AppServerControlHistoryTextSanitizer.Sanitize(runtimeMessage.Detail);
        state.Notices.Add(new AppServerControlRuntimeNotice
        {
            EventId = appServerControlEvent.EventId,
            Type = appServerControlEvent.Type,
            Message = message,
            Detail = detail,
            CreatedAt = appServerControlEvent.CreatedAt
        });
        if (state.Notices.Count > 64)
        {
            state.Notices.RemoveRange(0, state.Notices.Count - 64);
        }

        if (!timelineVisible)
        {
            return;
        }

        var entry = EnsureHistoryEntry(
            state,
            $"runtime:{appServerControlEvent.EventId}",
            RuntimeHistoryKindFromEventType(appServerControlEvent.Type),
            appServerControlEvent.CreatedAt);
        entry.Status = RuntimeHistoryStatusFromEventType(appServerControlEvent.Type);
        entry.ItemType = RuntimeHistoryItemTypeFromEventType(appServerControlEvent.Type);
        entry.Title = RuntimeHistoryTitleFromEventType(appServerControlEvent.Type);
        entry.Body = AppServerControlHistoryTextSanitizer.JoinDistinctSections(message, detail);
        entry.Streaming = false;
        entry.UpdatedAt = appServerControlEvent.CreatedAt;
    }

    private static AppServerControlProviderEvent CloneEvent(AppServerControlProviderEvent source)
    {
        return new AppServerControlProviderEvent
        {
            Sequence = source.Sequence,
            EventId = source.EventId,
            SessionId = source.SessionId,
            Provider = source.Provider,
            ThreadId = source.ThreadId,
            TurnId = source.TurnId,
            ItemId = source.ItemId,
            RequestId = source.RequestId,
            CreatedAt = source.CreatedAt,
            Type = source.Type,
            Raw = source.Raw is null ? null : new AppServerControlProviderEventRaw
            {
                Source = source.Raw.Source,
                Method = source.Raw.Method,
                PayloadOmitted = source.Raw.PayloadOmitted
            },
            AgentMetadataUpdated = source.AgentMetadataUpdated is null ? null : new AppServerControlProviderAgentMetadataPayload
            {
                AgentName = source.AgentMetadataUpdated.AgentName,
                Usage = source.AgentMetadataUpdated.Usage?.Clone(),
                AvailableCommands = source.AgentMetadataUpdated.AvailableCommands is null ? null : AppServerControlCommand.CloneMany(source.AgentMetadataUpdated.AvailableCommands)
            },
            SessionState = source.SessionState is null ? null : new AppServerControlProviderSessionStatePayload
            {
                State = source.SessionState.State,
                StateLabel = source.SessionState.StateLabel,
                Reason = source.SessionState.Reason
            },
            GoalUpdated = source.GoalUpdated?.Clone(),
            ThreadState = source.ThreadState is null ? null : new AppServerControlProviderThreadStatePayload
            {
                State = source.ThreadState.State,
                StateLabel = source.ThreadState.StateLabel,
                ProviderThreadId = source.ThreadState.ProviderThreadId
            },
            TurnStarted = source.TurnStarted is null ? null : new AppServerControlProviderTurnStartedPayload
            {
                IsReplay = source.TurnStarted.IsReplay,
                Model = source.TurnStarted.Model,
                Effort = source.TurnStarted.Effort
            },
            TurnCompleted = source.TurnCompleted is null ? null : new AppServerControlProviderTurnCompletedPayload
            {
                State = source.TurnCompleted.State,
                StateLabel = source.TurnCompleted.StateLabel,
                StopReason = source.TurnCompleted.StopReason,
                ErrorMessage = source.TurnCompleted.ErrorMessage
            },
            ContentDelta = source.ContentDelta is null ? null : new AppServerControlProviderContentDeltaPayload
            {
                StreamKind = source.ContentDelta.StreamKind,
                Replace = source.ContentDelta.Replace,
                Delta = source.ContentDelta.Delta
            },
            PlanDelta = source.PlanDelta is null ? null : new AppServerControlProviderPlanDeltaPayload
            {
                Delta = source.PlanDelta.Delta
            },
            PlanCompleted = source.PlanCompleted is null ? null : new AppServerControlProviderPlanCompletedPayload
            {
                IsProposal = source.PlanCompleted.IsProposal,
                PlanMarkdown = source.PlanCompleted.PlanMarkdown
            },
            DiffUpdated = source.DiffUpdated is null ? null : new AppServerControlProviderDiffUpdatedPayload
            {
                UnifiedDiff = source.DiffUpdated.UnifiedDiff
            },
            Item = source.Item is null ? null : new AppServerControlProviderItemPayload
            {
                Replace = source.Item.Replace,
                ItemType = source.Item.ItemType,
                Status = source.Item.Status,
                Title = source.Item.Title,
                Detail = source.Item.Detail,
                ToolPresentation = AppServerControlToolPresentationProtocol.Clone(source.Item.ToolPresentation),
                Attachments = CloneAttachments(source.Item.Attachments)
            },
            Task = source.Task is null ? null : new AppServerControlProviderTaskPayload
            {
                TaskId = source.Task.TaskId,
                Status = source.Task.Status,
                TaskType = source.Task.TaskType,
                Description = source.Task.Description,
                Summary = source.Task.Summary,
                LastToolName = source.Task.LastToolName,
                UsageJson = source.Task.UsageJson
            },
            QuickSettingsUpdated = source.QuickSettingsUpdated is null ? null : new AppServerControlQuickSettingsPayload
            {
                Model = source.QuickSettingsUpdated.Model,
                Effort = source.QuickSettingsUpdated.Effort,
                PlanMode = AppServerControlQuickSettings.NormalizePlanMode(source.QuickSettingsUpdated.PlanMode),
                PermissionMode = AppServerControlQuickSettings.NormalizePermissionMode(source.QuickSettingsUpdated.PermissionMode),
                FastMode = AppServerControlQuickSettings.NormalizeFastMode(source.QuickSettingsUpdated.FastMode),
                ModelOptions = AppServerControlQuickSettings.CloneOptions(source.QuickSettingsUpdated.ModelOptions),
                EffortOptions = AppServerControlQuickSettings.CloneOptions(source.QuickSettingsUpdated.EffortOptions),
                AcknowledgedFields = [.. source.QuickSettingsUpdated.AcknowledgedFields],
                ConfigOptions = AppServerControlQuickSettings.CloneConfigOptions(source.QuickSettingsUpdated.ConfigOptions)
            },
            RequestOpened = source.RequestOpened is null ? null : new AppServerControlProviderRequestOpenedPayload
            {
                RequestType = source.RequestOpened.RequestType,
                RequestTypeLabel = source.RequestOpened.RequestTypeLabel,
                Detail = source.RequestOpened.Detail
            },
            RequestResolved = source.RequestResolved is null ? null : new AppServerControlProviderRequestResolvedPayload
            {
                RequestType = source.RequestResolved.RequestType,
                Decision = source.RequestResolved.Decision
            },
            UserInputRequested = source.UserInputRequested is null ? null : new AppServerControlProviderUserInputRequestedPayload
            {
                Questions = source.UserInputRequested.Questions.Select(CloneQuestion).ToList()
            },
            UserInputResolved = source.UserInputResolved is null ? null : new AppServerControlProviderUserInputResolvedPayload
            {
                Answers = source.UserInputResolved.Answers.Select(CloneAnsweredQuestion).ToList()
            },
            RuntimeMessage = source.RuntimeMessage is null ? null : new AppServerControlProviderRuntimeMessagePayload
            {
                Message = source.RuntimeMessage.Message,
                Detail = source.RuntimeMessage.Detail
            },
            RuntimeNoticeOnly = source.RuntimeNoticeOnly is null ? null : new AppServerControlProviderRuntimeMessagePayload
            {
                Message = source.RuntimeNoticeOnly.Message,
                Detail = source.RuntimeNoticeOnly.Detail
            }
        };
    }

    private static AppServerControlHistoryPatch CloneDelta(AppServerControlHistoryPatch source)
    {
        return new AppServerControlHistoryPatch
        {
            SessionId = source.SessionId,
            Provider = source.Provider,
            GeneratedAt = source.GeneratedAt,
            LatestSequence = source.LatestSequence,
            HistoryCount = source.HistoryCount,
            Session = CloneSessionSummary(source.Session),
            Thread = CloneThreadSummary(source.Thread),
            CurrentTurn = CloneTurnSummary(source.CurrentTurn),
            QuickSettings = CloneQuickSettingsSummary(source.QuickSettings),
            Streams = CloneStreamsSummary(source.Streams),
            HistoryUpserts = source.HistoryUpserts.Select(entry => CloneHistoryEntry(entry)).ToList(),
            HistoryRemovals = [.. source.HistoryRemovals],
            ItemUpserts = source.ItemUpserts.Select(CloneItemSummary).ToList(),
            ItemRemovals = [.. source.ItemRemovals],
            RequestUpserts = source.RequestUpserts.Select(CloneRequestSummary).ToList(),
            RequestRemovals = [.. source.RequestRemovals],
            NoticeUpserts = source.NoticeUpserts.Select(CloneRuntimeNotice).ToList()
        };
    }

    private static AppServerControlQuestion CloneQuestion(AppServerControlQuestion source)
    {
        return new AppServerControlQuestion
        {
            Id = source.Id,
            Header = source.Header,
            Question = source.Question,
            MultiSelect = source.MultiSelect,
            AllowCustomAnswer = source.AllowCustomAnswer,
            IsSecret = source.IsSecret,
            Options = source.Options.Select(option => new AppServerControlQuestionOption
            {
                Label = option.Label,
                Value = option.Value,
                Description = option.Description
            }).ToList()
        };
    }

    private static AppServerControlAnsweredQuestion CloneAnsweredQuestion(AppServerControlAnsweredQuestion source)
    {
        return new AppServerControlAnsweredQuestion
        {
            QuestionId = source.QuestionId,
            Answers = [.. source.Answers]
        };
    }

    private static AppServerControlSessionSummary CloneSessionSummary(AppServerControlSessionSummary source)
    {
        return new AppServerControlSessionSummary
        {
            AgentName = source.AgentName,
            Usage = source.Usage?.Clone(),
            AvailableCommands = AppServerControlCommand.CloneMany(source.AvailableCommands),
            State = source.State,
            StateLabel = source.StateLabel,
            Reason = source.Reason,
            LastError = source.LastError,
            LastEventAt = source.LastEventAt
        };
    }

    private static void CopySessionSummary(AppServerControlSessionSummary target, AppServerControlSessionSummary source)
    {
        target.AgentName = source.AgentName;
        target.Usage = source.Usage?.Clone();
        target.AvailableCommands = AppServerControlCommand.CloneMany(source.AvailableCommands);
        target.State = source.State;
        target.StateLabel = source.StateLabel;
        target.Reason = source.Reason;
        target.LastError = source.LastError;
        target.LastEventAt = source.LastEventAt;
    }

    private static AppServerControlThreadSummary CloneThreadSummary(AppServerControlThreadSummary source)
    {
        return new AppServerControlThreadSummary
        {
            ThreadId = source.ThreadId,
            Goal = source.Goal?.Clone(),
            State = source.State,
            StateLabel = source.StateLabel
        };
    }

    private static void CopyThreadSummary(AppServerControlThreadSummary target, AppServerControlThreadSummary source)
    {
        target.ThreadId = source.ThreadId;
        target.Goal = source.Goal?.Clone();
        target.State = source.State;
        target.StateLabel = source.StateLabel;
    }

    private static AppServerControlTurnSummary CloneTurnSummary(AppServerControlTurnSummary source)
    {
        return new AppServerControlTurnSummary
        {
            TurnId = source.TurnId,
            State = source.State,
            StateLabel = source.StateLabel,
            Model = source.Model,
            Effort = source.Effort,
            StartedAt = source.StartedAt,
            CompletedAt = source.CompletedAt
        };
    }

    private static void CopyTurnSummary(AppServerControlTurnSummary target, AppServerControlTurnSummary source)
    {
        target.TurnId = source.TurnId;
        target.State = source.State;
        target.StateLabel = source.StateLabel;
        target.Model = source.Model;
        target.Effort = source.Effort;
        target.StartedAt = source.StartedAt;
        target.CompletedAt = source.CompletedAt;
    }

    private static AppServerControlQuickSettingsSummary CloneQuickSettingsSummary(AppServerControlQuickSettingsSummary source)
    {
        return new AppServerControlQuickSettingsSummary
        {
            Model = AppServerControlQuickSettings.NormalizeOptionalValue(source.Model),
            Effort = AppServerControlQuickSettings.NormalizeOptionalValue(source.Effort),
            PlanMode = AppServerControlQuickSettings.NormalizePlanMode(source.PlanMode),
            PermissionMode = AppServerControlQuickSettings.NormalizePermissionMode(source.PermissionMode),
            FastMode = AppServerControlQuickSettings.NormalizeFastMode(source.FastMode),
            ModelOptions = AppServerControlQuickSettings.CloneOptions(source.ModelOptions),
            EffortOptions = AppServerControlQuickSettings.CloneOptions(source.EffortOptions),
            AcknowledgedFields = [.. source.AcknowledgedFields],
            ConfigOptions = AppServerControlQuickSettings.CloneConfigOptions(source.ConfigOptions)
        };
    }

    private static void CopyQuickSettingsSummary(AppServerControlQuickSettingsSummary target, AppServerControlQuickSettingsSummary source)
    {
        target.Model = AppServerControlQuickSettings.NormalizeOptionalValue(source.Model);
        target.Effort = AppServerControlQuickSettings.NormalizeOptionalValue(source.Effort);
        target.PlanMode = AppServerControlQuickSettings.NormalizePlanMode(source.PlanMode);
        target.PermissionMode = AppServerControlQuickSettings.NormalizePermissionMode(source.PermissionMode);
        target.FastMode = AppServerControlQuickSettings.NormalizeFastMode(source.FastMode);
        target.ModelOptions = AppServerControlQuickSettings.CloneOptions(source.ModelOptions);
        target.EffortOptions = AppServerControlQuickSettings.CloneOptions(source.EffortOptions);
        target.AcknowledgedFields = [.. source.AcknowledgedFields];
        target.ConfigOptions = AppServerControlQuickSettings.CloneConfigOptions(source.ConfigOptions);
    }

    private static AppServerControlStreamsSummary CloneStreamsSummary(AppServerControlStreamsSummary source)
    {
        return new AppServerControlStreamsSummary
        {
            AssistantText = source.AssistantText,
            ReasoningText = source.ReasoningText,
            ReasoningSummaryText = source.ReasoningSummaryText,
            PlanText = source.PlanText,
            CommandOutput = source.CommandOutput,
            FileChangeOutput = source.FileChangeOutput,
            UnifiedDiff = source.UnifiedDiff
        };
    }

    private static void CopyStreamsSummary(AppServerControlStreamsSummary target, AppServerControlStreamsSummary source)
    {
        target.AssistantText = source.AssistantText;
        target.ReasoningText = source.ReasoningText;
        target.ReasoningSummaryText = source.ReasoningSummaryText;
        target.PlanText = source.PlanText;
        target.CommandOutput = source.CommandOutput;
        target.FileChangeOutput = source.FileChangeOutput;
        target.UnifiedDiff = source.UnifiedDiff;
    }

    private static AppServerControlHistoryItem CloneHistoryEntry(AppServerControlHistoryItem source)
    {
        return CloneHistoryEntry(source, null);
    }

    private static AppServerControlHistoryItem CloneHistoryEntry(AppServerControlHistoryItem source, int? viewportWidth)
    {
        return new AppServerControlHistoryItem
        {
            EntryId = source.EntryId,
            Order = source.Order,
            EstimatedHeightPx = EstimateHistoryEntryHeightPx(source, viewportWidth),
            Kind = source.Kind,
            TurnId = source.TurnId,
            ItemId = source.ItemId,
            RequestId = source.RequestId,
            Status = source.Status,
            ItemType = source.ItemType,
            Title = source.Title,
            CommandText = source.CommandText,
            ToolPresentation = AppServerControlToolPresentationProtocol.Clone(source.ToolPresentation),
            Body = source.Body,
            Attachments = CloneAttachments(source.Attachments),
            FileMentions = CloneInlineFileReferences(source.FileMentions),
            ImagePreviews = CloneInlineImagePreviews(source.ImagePreviews),
            Questions = source.Questions.Select(CloneQuestion).ToList(),
            Answers = source.Answers.Select(CloneAnsweredQuestion).ToList(),
            Streaming = source.Streaming,
            CreatedAt = source.CreatedAt,
            UpdatedAt = source.UpdatedAt,
            EnrichmentSourceSignature = source.EnrichmentSourceSignature
        };
    }

    private static int EstimateTotalHistoryHeightPx(AppServerControlConversationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return state.HistoryEntries.Values.Sum(static entry => EstimateHistoryEntryHeightPx(entry));
    }

    private static int EstimateHistoryEntryHeightPx(AppServerControlHistoryItem entry, int? viewportWidth = null)
    {
        ArgumentNullException.ThrowIfNull(entry);

        var effectiveViewportWidth = Math.Clamp(viewportWidth ?? 960, 240, 2400);
        var horizontalChrome = ResolveHistoryHorizontalChromePx(entry.Kind);
        var contentWidth = Math.Max(140, effectiveViewportWidth - horizontalChrome);
        var averageCharWidthPx = ResolveHistoryAverageCharWidthPx(entry.Kind);
        var charsPerLine = Math.Max(18, (int)Math.Floor(contentWidth / averageCharWidthPx));
        var textLines = EstimateWrappedTextLines(entry.Body, charsPerLine);
        var bodyHeight = Math.Min(420, 18 * textLines);
        if (entry.ImagePreviews.Count > 0)
        {
            var previewRows = (int)Math.Ceiling(entry.ImagePreviews.Count / 3d);
            bodyHeight += previewRows * 128 + 12;
        }
        return ResolveHistoryBaseHeightPx(entry.Kind) + bodyHeight;
    }

    private static int ResolveHistoryHorizontalChromePx(string? kind)
    {
        return string.Equals(kind, "user", StringComparison.Ordinal)
            ? 72
            : UsesCompactHistoryChrome(kind) ? 56 : 28;
    }

    private static double ResolveHistoryAverageCharWidthPx(string? kind)
    {
        return UsesCompactHistoryChrome(kind) ? 7.4d : 8.1d;
    }

    private static int ResolveHistoryBaseHeightPx(string? kind)
    {
        return kind switch
        {
            "tool" or "reasoning" or "diff" or "plan" => 84,
            "request" => 108,
            "user" => 68,
            "assistant" => 52,
            "system" or "notice" => 64,
            _ => 64
        };
    }

    private static bool UsesCompactHistoryChrome(string? kind)
    {
        return kind is "tool" or "reasoning" or "diff" or "request" or "system" or "notice";
    }

    private static int EstimateWrappedTextLines(string? body, int charsPerLine)
    {
        if (string.IsNullOrEmpty(body))
        {
            return 1;
        }

        var effectiveCharsPerLine = Math.Max(1, charsPerLine);
        var lines = body.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
        var lineCount = 0;
        foreach (var line in lines)
        {
            var length = Math.Max(1, line.Length);
            lineCount += Math.Max(1, (int)Math.Ceiling(length / (double)effectiveCharsPerLine));
        }

        return Math.Max(1, lineCount);
    }

    private static AppServerControlItemSummary CloneItemSummary(AppServerControlItemSummary source)
    {
        return new AppServerControlItemSummary
        {
            ItemId = source.ItemId,
            AliasItemIds = source.AliasItemIds.ToList(),
            TurnId = source.TurnId,
            ItemType = source.ItemType,
            Status = source.Status,
            Title = source.Title,
            Detail = source.Detail,
            Attachments = CloneAttachments(source.Attachments),
            UpdatedAt = source.UpdatedAt
        };
    }

    private static AppServerControlRequestSummary CloneRequestSummary(AppServerControlRequestSummary source)
    {
        return new AppServerControlRequestSummary
        {
            RequestId = source.RequestId,
            TurnId = source.TurnId,
            Kind = source.Kind,
            KindLabel = source.KindLabel,
            State = source.State,
            Detail = source.Detail,
            Decision = source.Decision,
            Questions = source.Questions.Select(CloneQuestion).ToList(),
            Answers = source.Answers.Select(CloneAnsweredQuestion).ToList(),
            UpdatedAt = source.UpdatedAt
        };
    }

    private static AppServerControlRuntimeNotice CloneRuntimeNotice(AppServerControlRuntimeNotice source)
    {
        return new AppServerControlRuntimeNotice
        {
            EventId = source.EventId,
            Type = source.Type,
            Message = source.Message,
            Detail = source.Detail,
            CreatedAt = source.CreatedAt
        };
    }

    private static HashSet<string> CollectTouchedHistoryIds(
        AppServerControlConversationState state,
        AppServerControlProviderEvent appServerControlEvent)
    {
        var historyIds = new HashSet<string>(StringComparer.Ordinal);

        if (appServerControlEvent.ContentDelta is not null)
        {
            var historyKind = HistoryKindFromStream(appServerControlEvent.ContentDelta.StreamKind);
            if (historyKind is not null)
            {
                historyIds.Add(ResolveHistoryEntryIdForStream(state, appServerControlEvent, historyKind, appServerControlEvent.ContentDelta.StreamKind));
            }
        }

        if (appServerControlEvent.PlanDelta is not null || appServerControlEvent.PlanCompleted is not null)
        {
            historyIds.Add($"plan:{appServerControlEvent.TurnId ?? state.CurrentTurn.TurnId ?? appServerControlEvent.EventId}");
        }

        if (appServerControlEvent.DiffUpdated is not null)
        {
            historyIds.Add($"diff:{appServerControlEvent.TurnId ?? state.CurrentTurn.TurnId ?? appServerControlEvent.EventId}");
        }

        if (appServerControlEvent.Item is not null)
        {
            var normalizedType = NormalizeItemType(appServerControlEvent.Item.ItemType);
            var historyKind = HistoryKindFromItem(normalizedType);
            var canonicalItemId = ResolveCanonicalItemId(state, appServerControlEvent, historyKind);
            historyIds.Add(ResolveHistoryEntryIdForItem(state, appServerControlEvent, historyKind, canonicalItemId));
        }

        if (appServerControlEvent.RequestOpened is not null ||
            appServerControlEvent.RequestResolved is not null ||
            appServerControlEvent.UserInputRequested is not null ||
            appServerControlEvent.UserInputResolved is not null)
        {
            historyIds.Add($"request:{ResolveRequestId(appServerControlEvent)}");
        }

        if (appServerControlEvent.RuntimeMessage is not null)
        {
            historyIds.Add($"runtime:{appServerControlEvent.EventId}");
        }

        return historyIds;
    }

    private static HashSet<string> CollectTouchedItemIds(
        AppServerControlConversationState state,
        AppServerControlProviderEvent appServerControlEvent)
    {
        var itemIds = new HashSet<string>(StringComparer.Ordinal);
        if (appServerControlEvent.Item is null)
        {
            return itemIds;
        }

        var normalizedType = NormalizeItemType(appServerControlEvent.Item.ItemType);
        var historyKind = HistoryKindFromItem(normalizedType);
        itemIds.Add(ResolveCanonicalItemId(state, appServerControlEvent, historyKind));
        return itemIds;
    }

    private static HashSet<string> CollectTouchedRequestIds(AppServerControlProviderEvent appServerControlEvent)
    {
        var requestIds = new HashSet<string>(StringComparer.Ordinal);
        if (appServerControlEvent.RequestOpened is null &&
            appServerControlEvent.RequestResolved is null &&
            appServerControlEvent.UserInputRequested is null &&
            appServerControlEvent.UserInputResolved is null)
        {
            return requestIds;
        }

        requestIds.Add(ResolveRequestId(appServerControlEvent));
        return requestIds;
    }

    private static HashSet<string> CollectTouchedNoticeIds(AppServerControlProviderEvent appServerControlEvent)
    {
        var noticeIds = new HashSet<string>(StringComparer.Ordinal);
        if (appServerControlEvent.RuntimeMessage is not null || appServerControlEvent.RuntimeNoticeOnly is not null)
        {
            noticeIds.Add(appServerControlEvent.EventId);
        }

        return noticeIds;
    }

    private static string ResolveRequestId(AppServerControlProviderEvent appServerControlEvent)
    {
        return string.IsNullOrWhiteSpace(appServerControlEvent.RequestId)
            ? $"request:{appServerControlEvent.EventId}"
            : appServerControlEvent.RequestId;
    }

    private static string NormalizeItemType(string? itemType)
    {
        return (itemType ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "usermessage" => "user_message",
            _ => itemType ?? string.Empty
        };
    }

    private static string ResolveCanonicalItemId(
        AppServerControlConversationState state,
        AppServerControlProviderEvent appServerControlEvent,
        string historyKind)
    {
        if (historyKind == "user") return ResolveCanonicalUserItemId(state, appServerControlEvent);

        if (!string.IsNullOrWhiteSpace(appServerControlEvent.ItemId))
        {
            return appServerControlEvent.ItemId;
        }

        if (historyKind == "user" && !string.IsNullOrWhiteSpace(appServerControlEvent.TurnId))
        {
            return $"local-user:{appServerControlEvent.TurnId}";
        }

        return $"item:{appServerControlEvent.EventId}";
    }

    private static string ResolveHistoryEntryIdForItem(
        AppServerControlConversationState state,
        AppServerControlProviderEvent appServerControlEvent,
        string historyKind,
        string canonicalItemId)
    {
        return historyKind switch
        {
            "assistant" => ResolveAssistantHistoryEntryIdForItem(state, appServerControlEvent, canonicalItemId),
            "user" => ResolveUserHistoryEntryId(state, appServerControlEvent.TurnId, canonicalItemId),
            "tool" => ResolveToolHistoryEntryIdForItem(state, appServerControlEvent, canonicalItemId),
            _ => $"{historyKind}:{canonicalItemId}"
        };
    }

    private static string ResolveAssistantHistoryEntryIdForItem(
        AppServerControlConversationState state,
        AppServerControlProviderEvent appServerControlEvent,
        string canonicalItemId)
    {
        if (!string.IsNullOrWhiteSpace(appServerControlEvent.TurnId))
        {
            var streamedEntryId = $"assistant-stream:{appServerControlEvent.TurnId}";
            if (state.HistoryEntries.ContainsKey(streamedEntryId))
            {
                return streamedEntryId;
            }
        }

        return $"assistant:{canonicalItemId}";
    }

    private static string ResolveToolHistoryEntryIdForItem(
        AppServerControlConversationState state,
        AppServerControlProviderEvent appServerControlEvent,
        string canonicalItemId)
    {
        var canonicalEntryId = $"tool:{canonicalItemId}";
        if (state.HistoryEntries.ContainsKey(canonicalEntryId))
        {
            return canonicalEntryId;
        }

        if (TryAdoptProvisionalToolHistoryEntry(state, appServerControlEvent, canonicalItemId, canonicalEntryId))
        {
            return canonicalEntryId;
        }

        return canonicalEntryId;
    }

    private static bool TryAdoptProvisionalToolHistoryEntry(
        AppServerControlConversationState state,
        AppServerControlProviderEvent appServerControlEvent,
        string canonicalItemId,
        string canonicalEntryId)
    {
        if (string.IsNullOrWhiteSpace(appServerControlEvent.TurnId) || appServerControlEvent.Item is null)
        {
            return false;
        }

        var normalizedItemType = NormalizeItemType(appServerControlEvent.Item.ItemType);
        if (string.IsNullOrWhiteSpace(normalizedItemType))
        {
            return false;
        }

        var provisionalEntry = state.ProvisionalToolIds.Select(id => state.HistoryEntries[id])
            .Where(entry =>
                string.Equals(entry.Kind, "tool", StringComparison.Ordinal) &&
                string.Equals(entry.TurnId, appServerControlEvent.TurnId, StringComparison.Ordinal) &&
                string.IsNullOrWhiteSpace(entry.ItemId) &&
                entry.Streaming &&
                AreCompatibleToolHistoryTypes(entry.ItemType, normalizedItemType))
            .OrderByDescending(entry => entry.UpdatedAt)
            .FirstOrDefault();
        if (provisionalEntry is null)
        {
            return false;
        }

        RemoveHistoryEntry(state, provisionalEntry.EntryId);
        if (state.ToolRenderStates.Remove(provisionalEntry.EntryId, out var toolState))
        {
            state.ToolRenderStates[canonicalEntryId] = toolState;
        }

        provisionalEntry.EntryId = canonicalEntryId;
        provisionalEntry.ItemId = canonicalItemId;
        SetHistoryEntry(state, provisionalEntry);
        return true;
    }

    private static bool AreCompatibleToolHistoryTypes(string? existingItemType, string? incomingItemType)
    {
        var normalizedExisting = NormalizeItemType(existingItemType);
        var normalizedIncoming = NormalizeItemType(incomingItemType);
        if (string.Equals(normalizedExisting, normalizedIncoming, StringComparison.Ordinal))
        {
            return true;
        }

        return (normalizedExisting, normalizedIncoming) switch
        {
            ("command_output", "command_execution") => true,
            ("command_execution", "command_output") => true,
            ("file_change_output", "file_change") => true,
            ("file_change", "file_change_output") => true,
            _ => false
        };
    }

    private static string ResolveHistoryEntryIdForStream(
        AppServerControlConversationState state,
        AppServerControlProviderEvent appServerControlEvent,
        string historyKind,
        string streamKind)
    {
        var turnId = appServerControlEvent.TurnId ?? state.CurrentTurn.TurnId;
        return historyKind switch
        {
            "user" when !string.IsNullOrWhiteSpace(turnId) => $"user:{turnId}",
            "user" => $"user:{appServerControlEvent.ItemId ?? appServerControlEvent.EventId}",
            "assistant" when !string.IsNullOrWhiteSpace(appServerControlEvent.ItemId) => $"assistant:{appServerControlEvent.ItemId}",
            "assistant" when !string.IsNullOrWhiteSpace(turnId) => $"assistant-stream:{turnId}",
            "assistant" => $"assistant-stream:{appServerControlEvent.EventId}",
            "tool" => ResolveToolHistoryEntryIdForStream(state.Items, appServerControlEvent, turnId, streamKind),
            "reasoning" => $"reasoning:{streamKind}:{appServerControlEvent.ItemId ?? turnId ?? appServerControlEvent.EventId}",
            _ => $"{historyKind}:{turnId ?? appServerControlEvent.ItemId ?? appServerControlEvent.EventId}"
        };
    }

    private static string ResolveToolHistoryEntryIdForStream(
        IReadOnlyDictionary<string, AppServerControlItemSummary> items,
        AppServerControlProviderEvent appServerControlEvent,
        string? turnId,
        string streamKind)
    {
        if (!string.IsNullOrWhiteSpace(appServerControlEvent.ItemId))
        {
            return $"tool:{appServerControlEvent.ItemId}";
        }

        var ownerItemId = ResolveOwningToolItemIdForStream(items, turnId, streamKind);
        return !string.IsNullOrWhiteSpace(ownerItemId)
            ? $"tool:{ownerItemId}"
            : $"tool:{streamKind}:{turnId ?? appServerControlEvent.EventId}";
    }

    private static string? ResolveToolItemIdFromHistoryEntryId(string entryId, string? streamKind)
    {
        if (!entryId.StartsWith("tool:", StringComparison.Ordinal))
        {
            return null;
        }

        var suffix = entryId["tool:".Length..];
        var normalizedStreamKind = NormalizeItemType(streamKind);
        if (!string.IsNullOrWhiteSpace(normalizedStreamKind) &&
            suffix.StartsWith($"{normalizedStreamKind}:", StringComparison.Ordinal))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(suffix) ? null : suffix;
    }

    private static string HistoryKindFromItem(string itemType)
    {
        var normalized = itemType.Trim().ToLowerInvariant();
        if (normalized.Contains("assistant", StringComparison.Ordinal))
        {
            return "assistant";
        }

        if (normalized.Contains("user", StringComparison.Ordinal) ||
            normalized.Contains("input", StringComparison.Ordinal))
        {
            return "user";
        }

        if (normalized.Contains("reasoning", StringComparison.Ordinal) ||
            normalized.Contains("task", StringComparison.Ordinal))
        {
            return "reasoning";
        }

        if (normalized.Contains("plan", StringComparison.Ordinal))
        {
            return "plan";
        }

        return "tool";
    }

    private static string RuntimeHistoryKindFromEventType(string? eventType)
    {
        return eventType switch
        {
            "agent.error" => "notice",
            "agent.state" => "system",
            "runtime.error" => "notice",
            "runtime.warning" => "system",
            "config.warning" => "notice",
            "deprecation.notice" => "notice",
            _ => "system"
        };
    }

    private static string RuntimeHistoryStatusFromEventType(string? eventType)
    {
        return eventType switch
        {
            "agent.error" => "agent.error",
            "agent.state" => "info",
            "runtime.error" => "runtime.error",
            "runtime.warning" => "runtime.warning",
            "config.warning" => "warning",
            "deprecation.notice" => "warning",
            "mcp.oauth.completed" => "completed",
            _ => "info"
        };
    }

    private static string RuntimeHistoryItemTypeFromEventType(string? eventType)
    {
        return eventType switch
        {
            "agent.error" => "agent_error",
            "agent.state" => "agent_state",
            _ => string.Empty
        };
    }

    private static string RuntimeHistoryTitleFromEventType(string? eventType)
    {
        return eventType switch
        {
            "agent.error" => "Agent error",
            "agent.state" => "Agent state",
            "runtime.error" => "Error",
            "runtime.warning" => "Runtime",
            "thread.metadata.updated" => "Thread updated",
            "thread.token-usage.updated" => "Context window",
            "model.rerouted" => "Model rerouted",
            "config.warning" => "Configuration warning",
            "deprecation.notice" => "Deprecation notice",
            "account.updated" => "Account updated",
            "account.rate-limits.updated" => "Rate limits updated",
            "mcp.oauth.completed" => "MCP sign-in",
            "thread.realtime.started" => "Realtime started",
            "thread.realtime.item-added" => "Realtime item",
            "thread.realtime.audio.delta" => "Realtime audio",
            "thread.realtime.error" => "Realtime error",
            "thread.realtime.closed" => "Realtime closed",
            "auth.status" => "Authentication",
            "session.exited" => "Session exited",
            _ => "System"
        };
    }

    private static string? HistoryKindFromStream(string streamKind)
    {
        var normalized = streamKind.Trim().ToLowerInvariant();
        if (normalized == "user_text") return "user";
        if (normalized == "assistant_text")
        {
            return "assistant";
        }

        if (normalized is "reasoning_text" or "reasoning_summary_text")
        {
            return "reasoning";
        }

        if (normalized is "command_output" or "file_change_output" ||
            normalized.EndsWith("_output", StringComparison.Ordinal) ||
            normalized.EndsWith("_result", StringComparison.Ordinal))
        {
            return "tool";
        }

        return null;
    }

    private static string ResolveUserHistoryEntryId(AppServerControlConversationState state, string? turnId, string itemId)
    {
        if (string.IsNullOrWhiteSpace(turnId) || itemId.StartsWith("local-steer:", StringComparison.Ordinal)) return $"user:{itemId}";
        var primaryId = $"user:{turnId}";
        return !state.HistoryEntries.TryGetValue(primaryId, out var primary) || primary.ItemId == itemId
            ? primaryId : $"user:{itemId}";
    }

    private static string ResolveCanonicalUserItemId(
        AppServerControlConversationState state,
        AppServerControlProviderEvent providerEvent)
    {
        var incomingId = providerEvent.ItemId ?? $"local-user:{providerEvent.TurnId ?? providerEvent.EventId}";
        if (state.Items.ContainsKey(incomingId)) return incomingId;
        var candidates = state.UserItemsByTurn.TryGetValue(providerEvent.TurnId ?? string.Empty, out var ids)
            ? ids.Select(id => state.Items[id]).Where(item =>
                string.Equals(item.TurnId, providerEvent.TurnId, StringComparison.Ordinal)).ToArray() : [];
        var matched = candidates.FirstOrDefault(item => item.AliasItemIds.Contains(incomingId, StringComparer.Ordinal));
        if (matched is not null) return matched.ItemId;

        var detail = providerEvent.Item?.Detail;
        if (IsLocalUserItemId(incomingId))
        {
            var native = candidates.Where(item => !IsLocalUserItemId(item.ItemId) &&
                !item.AliasItemIds.Any(IsLocalUserItemId)).ToArray();
            matched = native.FirstOrDefault(item => SameUserMessageBody(item.Detail, detail));
            if (matched is null && incomingId.StartsWith("local-user:", StringComparison.Ordinal)) matched = native.FirstOrDefault();
            matched ??= native.FirstOrDefault(item => string.IsNullOrWhiteSpace(item.Detail));
        }
        else
        {
            var local = candidates.Where(item => IsLocalUserItemId(item.ItemId) &&
                !item.AliasItemIds.Any(static id => !IsLocalUserItemId(id))).ToArray();
            matched = local.FirstOrDefault(item => SameUserMessageBody(item.Detail, detail)) ??
                local.FirstOrDefault(item => item.ItemId.StartsWith("local-user:", StringComparison.Ordinal));
            if (matched is null && string.IsNullOrWhiteSpace(detail)) matched = local.FirstOrDefault();
        }

        if (matched is null) return incomingId;
        matched.AliasItemIds.Add(incomingId);
        return matched.ItemId;
    }

    private static bool IsLocalUserItemId(string itemId) =>
        itemId.StartsWith("local-user:", StringComparison.Ordinal) || itemId.StartsWith("local-steer:", StringComparison.Ordinal);

    private static bool SameUserMessageBody(string? first, string? second) =>
        !string.IsNullOrWhiteSpace(first) && string.Equals(first, second, StringComparison.Ordinal);

    private static string? ResolveOwningToolItemIdForStream(
        IReadOnlyDictionary<string, AppServerControlItemSummary> items,
        string? turnId,
        string? streamKind)
    {
        var normalizedStreamKind = NormalizeItemType(streamKind);
        if (string.IsNullOrWhiteSpace(normalizedStreamKind))
        {
            return null;
        }

        string[] preferredItemTypes = normalizedStreamKind switch
        {
            "command_output" => ["command_execution", "command_output"],
            "file_change_output" => ["file_change", "file_change_output"],
            _ => [normalizedStreamKind]
        };

        return items.Values
            .Where(item =>
                preferredItemTypes.Contains(NormalizeItemType(item.ItemType), StringComparer.Ordinal) &&
                (string.IsNullOrWhiteSpace(turnId) ||
                 string.Equals(item.TurnId, turnId, StringComparison.Ordinal)))
            .OrderByDescending(item => item.UpdatedAt)
            .Select(item => item.ItemId)
            .FirstOrDefault();
    }

    private static string ChoosePreferredUserMessageStatus(string? existingStatus, string? incomingStatus)
    {
        if (string.Equals(existingStatus, "completed", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(incomingStatus, "completed", StringComparison.OrdinalIgnoreCase))
        {
            return "completed";
        }

        return string.IsNullOrWhiteSpace(incomingStatus) ? existingStatus ?? string.Empty : incomingStatus;
    }

    private static string ResolveStreamTitle(string historyKind, string streamKind, string? existingTitle)
    {
        if (!string.IsNullOrWhiteSpace(existingTitle))
        {
            return existingTitle;
        }

        return (historyKind, streamKind) switch
        {
            ("reasoning", "reasoning_summary_text") => "Reasoning summary",
            ("reasoning", _) => "Reasoning",
            ("tool", "command_output") => "Command output",
            ("tool", "file_change_output") => "File change output",
            _ => string.Empty
        };
    }

    private static string BuildRequestBody(AppServerControlRequestSummary request)
    {
        var sections = new List<string>();
        if (!string.IsNullOrWhiteSpace(request.Detail))
        {
            sections.Add(request.Detail.Trim());
        }

        if (request.Questions.Count > 0)
        {
            sections.Add(string.Join(
                "\n",
                request.Questions.Select(static question => question.Question)
                    .Where(static question => !string.IsNullOrWhiteSpace(question))));
        }

        if (request.Answers.Count > 0)
        {
            sections.Add(string.Join(
                "\n",
                request.Answers.Select(static answer => $"{answer.QuestionId}: {string.Join(", ", answer.Answers)}")));
        }

        if (!string.IsNullOrWhiteSpace(request.Decision))
        {
            sections.Add($"Decision: {request.Decision}");
        }

        return string.Join("\n\n", sections.Where(static section => !string.IsNullOrWhiteSpace(section)));
    }

    private static string MergeProgressiveMessage(string? existing, string? incoming)
    {
        var normalizedExisting = NormalizeHistoryText(existing);
        var normalizedIncoming = NormalizeHistoryText(incoming);
        var trimmedExisting = normalizedExisting.Trim();
        if (string.IsNullOrWhiteSpace(trimmedExisting))
        {
            return normalizedIncoming;
        }

        if (trimmedExisting == normalizedIncoming)
        {
            return trimmedExisting;
        }

        if (normalizedIncoming.Contains(trimmedExisting, StringComparison.Ordinal))
        {
            return normalizedIncoming;
        }

        if (trimmedExisting.Contains(normalizedIncoming, StringComparison.Ordinal))
        {
            return trimmedExisting;
        }

        var overlapLength = FindMessageOverlap(trimmedExisting, normalizedIncoming);
        if (overlapLength > 0)
        {
            return $"{trimmedExisting}{normalizedIncoming[overlapLength..]}";
        }

        return AppendHistoryChunk(trimmedExisting, normalizedIncoming);
    }

    private static string MergeAssistantItemDetail(
        string? existing,
        string? incoming,
        string? status)
    {
        var normalizedIncoming = NormalizeHistoryText(incoming).Trim();
        if (IsTerminalStatus(status) && !string.IsNullOrWhiteSpace(normalizedIncoming))
        {
            return normalizedIncoming;
        }

        return MergeProgressiveMessage(existing, incoming);
    }

    private static string AppendAssistantDelta(string? existing, string? delta)
    {
        return AppendRetainedStreamText(existing, delta, MaxAssistantStreamChars, retainHead: false, separateWithNewline: false);
    }

    private static int FindMessageOverlap(string left, string right)
    {
        var maxOverlap = Math.Min(left.Length, right.Length);
        for (var overlap = maxOverlap; overlap > 0; overlap--)
        {
            if (string.Equals(left[^overlap..], right[..overlap], StringComparison.Ordinal))
            {
                return overlap;
            }
        }

        return 0;
    }

    private static string MergeHistoryBody(string? existing, string? incoming)
    {
        var normalizedExisting = NormalizeHistoryText(existing);
        var normalizedIncoming = NormalizeHistoryText(incoming);
        if (string.IsNullOrWhiteSpace(normalizedIncoming))
        {
            return normalizedExisting;
        }

        if (string.IsNullOrWhiteSpace(normalizedExisting))
        {
            return normalizedIncoming;
        }

        if (string.Equals(normalizedExisting, normalizedIncoming, StringComparison.Ordinal))
        {
            return normalizedExisting;
        }

        if (normalizedIncoming.Contains(normalizedExisting, StringComparison.Ordinal))
        {
            return normalizedIncoming;
        }

        if (normalizedExisting.Contains(normalizedIncoming, StringComparison.Ordinal))
        {
            return normalizedExisting;
        }

        return AppendHistoryChunk(normalizedExisting, normalizedIncoming);
    }

    private static string AppendHistoryChunk(string? existing, string? chunk)
    {
        var normalizedExisting = NormalizeHistoryText(existing);
        var normalizedChunk = NormalizeHistoryText(chunk);
        if (string.IsNullOrWhiteSpace(normalizedChunk))
        {
            return normalizedExisting;
        }

        if (string.IsNullOrWhiteSpace(normalizedExisting))
        {
            return normalizedChunk;
        }

        if (normalizedExisting.EndsWith('\n') || normalizedChunk.StartsWith('\n'))
        {
            return normalizedExisting + normalizedChunk;
        }

        return $"{normalizedExisting}\n{normalizedChunk}";
    }

    private static string AppendRetainedHistoryChunk(string? existing, string? incoming, int maxChars, bool retainHead)
    {
        return AppendRetainedStreamText(existing, incoming, maxChars, retainHead, separateWithNewline: true);
    }

    private static string AppendRetainedStreamText(
        string? existing,
        string? incoming,
        int maxChars,
        bool retainHead,
        bool separateWithNewline)
    {
        var normalizedIncoming = NormalizeHistoryText(incoming);
        if (separateWithNewline ? string.IsNullOrWhiteSpace(normalizedIncoming) : string.IsNullOrEmpty(normalizedIncoming))
        {
            return NormalizeHistoryText(existing);
        }

        var normalizedExisting = StripRetentionMarkers(NormalizeHistoryText(existing));
        if (separateWithNewline ? string.IsNullOrWhiteSpace(normalizedExisting) : string.IsNullOrEmpty(normalizedExisting))
        {
            return RetainWithinBudget(normalizedIncoming, maxChars, retainHead);
        }

        var separator = separateWithNewline &&
                        !normalizedExisting.EndsWith('\n') &&
                        !normalizedIncoming.StartsWith('\n')
            ? "\n"
            : string.Empty;
        return RetainWithinBudget(normalizedExisting + separator + normalizedIncoming, maxChars, retainHead);
    }

    private static string RetainWithinBudget(string? value, int maxChars, bool retainHead)
    {
        var normalized = NormalizeHistoryText(value);
        if (normalized.Length <= maxChars)
        {
            return normalized;
        }

        var marker = retainHead ? HeadRetentionMarker : TailRetentionMarker;
        var availableChars = maxChars - marker.Length - 1;
        if (availableChars <= 0)
        {
            return normalized[..Math.Max(0, maxChars)];
        }

        return retainHead
            ? string.Concat(normalized.AsSpan(0, availableChars), "\n", marker)
            : string.Concat(marker, "\n", normalized.AsSpan(normalized.Length - availableChars));
    }

    private static string StripRetentionMarkers(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        if (value.StartsWith(TailRetentionMarker + "\n", StringComparison.Ordinal))
        {
            return value[(TailRetentionMarker.Length + 1)..];
        }

        if (value.EndsWith("\n" + HeadRetentionMarker, StringComparison.Ordinal))
        {
            return value[..^(HeadRetentionMarker.Length + 1)];
        }

        return value;
    }

    private static string CompactHistoryLine(string line)
    {
        if (line.Length <= MaxHistoryLineChars)
        {
            return line;
        }

        var availableChars = MaxHistoryLineChars - HeadRetentionMarker.Length - 1;
        if (availableChars <= 0)
        {
            return line[..MaxHistoryLineChars];
        }

        return string.Concat(line.AsSpan(0, availableChars), " ", HeadRetentionMarker);
    }

    private static string CompactHistoryLine(string line, int maxChars)
    {
        if (maxChars <= 0)
        {
            return string.Empty;
        }

        if (line.Length <= maxChars)
        {
            return line;
        }

        var availableChars = maxChars - HeadRetentionMarker.Length - 1;
        if (availableChars <= 0)
        {
            return line[..Math.Min(line.Length, maxChars)];
        }

        return string.Concat(line.AsSpan(0, availableChars), " ", HeadRetentionMarker);
    }

    private static string CompactHistorySections(IReadOnlyList<string> sections)
    {
        if (sections.Count == 0)
        {
            return string.Empty;
        }

        var normalizedSections = sections
            .Select(static (string section) => section ?? string.Empty)
            .ToArray();
        var body = string.Join("\n", normalizedSections);
        if (body.Length <= MaxHistoryBodyChars)
        {
            return body;
        }

        var nonEmptyIndices = normalizedSections
            .Select((section, index) => (section, index))
            .Where(static item => item.section.Length > 0)
            .Select(static item => item.index)
            .ToArray();
        if (nonEmptyIndices.Length == 0)
        {
            return body[..Math.Min(body.Length, MaxHistoryBodyChars)];
        }

        var availableChars = Math.Max(0, MaxHistoryBodyChars - Math.Max(0, normalizedSections.Length - 1));
        var remainingChars = availableChars;
        var remainingSections = nonEmptyIndices.Length;

        foreach (var index in nonEmptyIndices)
        {
            var maxChars = Math.Max(24, Math.Min(MaxHistoryLineChars, remainingChars / Math.Max(1, remainingSections)));
            var compacted = CompactHistoryLine(normalizedSections[index], maxChars);
            normalizedSections[index] = compacted;
            remainingChars = Math.Max(0, remainingChars - compacted.Length);
            remainingSections--;
        }

        return string.Join("\n", normalizedSections);
    }

    private static string CompactHistoryBody(string body, bool retainHead)
    {
        return RetainWithinBudget(body, MaxHistoryBodyChars, retainHead);
    }

    private static string NormalizeHistoryText(string? value)
    {
        return (value ?? string.Empty).Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
    }

    private static string PreferNonEmpty(string? incoming, string? fallback)
    {
        return string.IsNullOrWhiteSpace(incoming) ? fallback ?? string.Empty : incoming;
    }

    private static string? PreferMeaningfulText(string? existing, string? incoming)
    {
        if (!string.IsNullOrWhiteSpace(incoming))
        {
            return NormalizeHistoryText(incoming).Trim();
        }

        return string.IsNullOrWhiteSpace(existing) ? null : existing;
    }

    private static string? PreferNonGenericText(string? existing, string? incoming)
    {
        if (!string.IsNullOrWhiteSpace(incoming))
        {
            return incoming;
        }

        return existing;
    }

    private static bool IsTerminalStatus(string? status)
    {
        return string.Equals(status, "completed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "resolved", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "failed", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "stopped", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "error", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(status, "cancelled", StringComparison.OrdinalIgnoreCase);
    }

    private static List<AppServerControlAttachmentReference> CloneAttachments(
        IReadOnlyList<AppServerControlAttachmentReference>? attachments)
    {
        if (attachments is null || attachments.Count == 0)
        {
            return [];
        }

        return attachments.Select(static attachment => new AppServerControlAttachmentReference
        {
            Kind = attachment.Kind,
            Path = attachment.Path,
            MimeType = attachment.MimeType,
            DisplayName = string.IsNullOrWhiteSpace(attachment.DisplayName)
                ? Path.GetFileName(attachment.Path)
                : attachment.DisplayName
        }).ToList();
    }

    private static List<AppServerControlAttachmentReference> MergeAttachments(
        IReadOnlyList<AppServerControlAttachmentReference>? existing,
        IReadOnlyList<AppServerControlAttachmentReference>? incoming)
    {
        if ((existing is null || existing.Count == 0) && (incoming is null || incoming.Count == 0))
        {
            return [];
        }

        var merged = CloneAttachments(existing);
        foreach (var attachment in CloneAttachments(incoming))
        {
            if (merged.Any(existingAttachment =>
                    string.Equals(existingAttachment.Kind, attachment.Kind, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existingAttachment.Path, attachment.Path, StringComparison.OrdinalIgnoreCase) &&
                    string.Equals(existingAttachment.DisplayName, attachment.DisplayName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            merged.Add(attachment);
        }

        return merged;
    }

    private static AppServerControlHistoryItem EnsureHistoryEntry(
        AppServerControlConversationState state,
        string entryId,
        string kind,
        DateTimeOffset createdAt)
    {
        if (!state.HistoryEntries.TryGetValue(entryId, out var entry))
        {
            entry = new AppServerControlHistoryItem
            {
                EntryId = entryId,
                Order = ++state.NextHistoryOrder,
                Kind = kind,
                CreatedAt = createdAt,
                UpdatedAt = createdAt
            };
            SetHistoryEntry(state, entry);
        }

        entry.Kind = kind;
        if (entry.UpdatedAt < createdAt)
        {
            entry.UpdatedAt = createdAt;
        }

        return entry;
    }

    private static void PromoteUserHistoryEntryToTurnLead(
        AppServerControlConversationState state,
        AppServerControlHistoryItem entry)
    {
        if (!string.Equals(entry.Kind, "user", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(entry.TurnId) ||
            !string.Equals(entry.EntryId, $"user:{entry.TurnId}", StringComparison.Ordinal))
        {
            return;
        }

        RefreshHistoryIndexes(state, entry);
        var turnLeadOrder = long.MaxValue;
        foreach (var key in state.HistoryByTurn[entry.TurnId])
        {
            var candidate = state.HistoryEntries[key.EntryId];
            if (ReferenceEquals(candidate, entry) ||
                !string.Equals(candidate.TurnId, entry.TurnId, StringComparison.Ordinal))
            {
                continue;
            }

            turnLeadOrder = candidate.Order;
            break;
        }

        if (turnLeadOrder == long.MaxValue || entry.Order <= turnLeadOrder)
        {
            return;
        }

        foreach (var candidate in state.HistoryEntries.Values)
        {
            if (ReferenceEquals(candidate, entry))
            {
                continue;
            }

            if (candidate.Order >= turnLeadOrder && candidate.Order < entry.Order)
            {
                candidate.Order += 1;
            }
        }

        entry.Order = turnLeadOrder;
        RebuildHistoryOrderIndex(state);
    }

    private static void CompleteStreamingHistoryEntries(AppServerControlConversationState state, string? turnId)
    {
        foreach (var id in state.StreamingHistoryIds.ToArray())
        {
            var entry = state.HistoryEntries[id];
            if (!entry.Streaming)
            {
                continue;
            }

            if (!string.IsNullOrWhiteSpace(turnId) &&
                !string.Equals(entry.TurnId, turnId, StringComparison.Ordinal))
            {
                continue;
            }

            entry.Streaming = false;
            state.StreamingHistoryIds.Remove(id);
            state.ProvisionalToolIds.Remove(id);
            if (string.IsNullOrWhiteSpace(entry.Status) ||
                string.Equals(entry.Status, "streaming", StringComparison.OrdinalIgnoreCase))
            {
                entry.Status = "completed";
            }
        }
    }

    private static void ResetStreams(AppServerControlStreamsSummary streams)
    {
        streams.AssistantText = string.Empty;
        streams.ReasoningText = string.Empty;
        streams.ReasoningSummaryText = string.Empty;
        streams.PlanText = string.Empty;
        streams.CommandOutput = string.Empty;
        streams.FileChangeOutput = string.Empty;
        streams.UnifiedDiff = string.Empty;
    }

    private static AppServerControlScreenLogHistoryEntry BuildScreenLogHistoryEntry(AppServerControlHistoryItem entry)
    {
        var kind = NormalizeHistoryKind(entry.Kind);
        var statusLabel = entry.Streaming
            ? "Streaming"
            : Prettify(entry.Status);
        var diffScreenBody = kind == "diff"
            ? BuildScreenLogDiffBody(entry.Body)
            : entry.Body;
        var collapsedByDefault = kind == "diff" || entry.ToolPresentation is not null
            ? false
            : ShouldCollapseHistoryBodyByDefault(entry, kind);

        return new AppServerControlScreenLogHistoryEntry
        {
            EntryId = entry.EntryId,
            Order = entry.Order,
            Kind = kind,
            ItemType = entry.ItemType,
            Status = entry.Status,
            Label = ResolveHistoryLabel(kind),
            Title = entry.Title ?? string.Empty,
            CommandText = entry.CommandText ?? string.Empty,
            ToolPresentation = AppServerControlToolPresentationProtocol.Clone(entry.ToolPresentation),
            Meta = FormatHistoryMeta(kind, statusLabel, entry.UpdatedAt),
            Body = diffScreenBody,
            RenderMode = entry.ToolPresentation is not null
                ? "tool"
                : ResolveHistoryRenderMode(kind, entry.Streaming),
            CollapsedByDefault = collapsedByDefault,
            Preview = kind == "diff" ? BuildHistoryPreview(diffScreenBody) : BuildHistoryPreview(entry.Body),
            LineCount = kind == "diff" ? CountHistoryBodyLines(diffScreenBody) : CountHistoryBodyLines(entry.Body),
            Streaming = entry.Streaming,
            UpdatedAt = entry.UpdatedAt,
            Attachments = CloneAttachments(entry.Attachments)
        };
    }

    private static bool ShouldIncludeInScreenHistoryLog(AppServerControlHistoryItem entry)
    {
        return !string.IsNullOrWhiteSpace(entry.Body) ||
               (entry.Attachments?.Count ?? 0) > 0 ||
               entry.Kind is "request" or "system" or "notice";
    }

    private static string BuildScreenLogSignature(AppServerControlScreenLogDeltaRecord record)
    {
        var hash = new HashCode();
        hash.Add(record.Session.State, StringComparer.Ordinal);
        hash.Add(record.Session.StateLabel, StringComparer.Ordinal);
        hash.Add(record.Session.Reason ?? string.Empty, StringComparer.Ordinal);
        hash.Add(record.Session.LastError ?? string.Empty, StringComparer.Ordinal);
        hash.Add(record.CurrentTurn.TurnId ?? string.Empty, StringComparer.Ordinal);
        hash.Add(record.CurrentTurn.State, StringComparer.Ordinal);
        hash.Add(record.CurrentTurn.StateLabel, StringComparer.Ordinal);
        hash.Add(record.CurrentTurn.Model ?? string.Empty, StringComparer.Ordinal);
        hash.Add(record.CurrentTurn.Effort ?? string.Empty, StringComparer.Ordinal);

        foreach (var entry in record.HistoryUpserts)
        {
            hash.Add(entry.EntryId, StringComparer.Ordinal);
            hash.Add(entry.Kind, StringComparer.Ordinal);
            hash.Add(entry.ItemType ?? string.Empty, StringComparer.Ordinal);
            hash.Add(entry.Status, StringComparer.Ordinal);
            hash.Add(entry.Title, StringComparer.Ordinal);
            hash.Add(entry.CommandText, StringComparer.Ordinal);
            hash.Add(entry.Meta, StringComparer.Ordinal);
            hash.Add(entry.Body, StringComparer.Ordinal);
            hash.Add(entry.RenderMode, StringComparer.Ordinal);
            hash.Add(entry.CollapsedByDefault);
            hash.Add(entry.Streaming);
        }

        foreach (var removal in record.HistoryRemovals)
        {
            hash.Add(removal, StringComparer.Ordinal);
        }

        return hash.ToHashCode().ToString("X8");
    }

    private static string NormalizeHistoryKind(string? kind)
    {
        return string.IsNullOrWhiteSpace(kind) ? "system" : kind.Trim().ToLowerInvariant();
    }

    private static string ResolveHistoryLabel(string kind)
    {
        return kind switch
        {
            "user" => "You",
            "assistant" => "Assistant",
            "reasoning" => "Reasoning",
            "tool" => "Tool",
            "request" => "Request",
            "plan" => "Plan",
            "diff" => "Diff",
            "notice" => "Error",
            _ => "System"
        };
    }

    private static string ResolveHistoryRenderMode(string kind, bool streaming)
    {
        if (kind == "assistant")
        {
            return streaming ? "streaming_text" : "markdown";
        }

        if (kind == "diff")
        {
            return "diff";
        }

        return kind is "tool" or "reasoning" or "plan" ? "monospace" : "plain";
    }

    private static bool ShouldCollapseHistoryBodyByDefault(AppServerControlHistoryItem entry, string kind)
    {
        if (entry.Streaming || kind is not ("tool" or "reasoning" or "plan"))
        {
            return false;
        }

        var lineCount = CountHistoryBodyLines(entry.Body);
        return lineCount >= CollapsibleHistoryBodyMinLines || entry.Body.Length >= CollapsibleHistoryBodyMinChars;
    }

    private static int CountHistoryBodyLines(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return 0;
        }

        return NormalizeHistoryText(body).Split('\n').Length;
    }

    private static string BuildHistoryPreview(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        var firstContentLine = NormalizeHistoryText(body)
            .Split('\n')
            .Select(static line => line.Trim())
            .FirstOrDefault(static line => !string.IsNullOrWhiteSpace(line))
            ?? string.Empty;
        var singleLine = string.Join(
            " ",
            firstContentLine.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (singleLine.Length <= HistoryPreviewMaxChars)
        {
            return singleLine;
        }

        return singleLine[..(HistoryPreviewMaxChars - 1)] + "…";
    }

    private static string BuildScreenLogDiffBody(string? body)
    {
        if (string.IsNullOrWhiteSpace(body))
        {
            return string.Empty;
        }

        var normalized = NormalizeHistoryText(body);
        var renderedLines = BuildScreenLogDiffLines(normalized);
        if (renderedLines.Count == 0)
        {
            return normalized.Trim();
        }

        if (renderedLines.Count > MaxVisibleDiffScreenLogLines)
        {
            var omittedCount = renderedLines.Count - MaxVisibleDiffScreenLogLines;
            renderedLines = renderedLines.Take(MaxVisibleDiffScreenLogLines)
                .Append(string.Create(CultureInfo.InvariantCulture, $"... {omittedCount} more diff lines omitted ..."))
                .ToList();
        }

        return string.Join('\n', renderedLines);
    }

    private static List<string> BuildScreenLogDiffLines(string normalizedBody)
    {
        var sourceLines = normalizedBody.Split('\n');
        var rendered = new List<string>();
        string oldPath = string.Empty;
        string newPath = string.Empty;
        var currentBody = new List<string>();
        var sawHunk = false;

        void FlushSection()
        {
            if (currentBody.Count == 0)
            {
                oldPath = string.Empty;
                newPath = string.Empty;
                sawHunk = false;
                return;
            }

            var header = FormatDiffSectionHeader(oldPath, newPath);
            if (!string.IsNullOrWhiteSpace(header))
            {
                rendered.Add(header);
            }

            rendered.AddRange(currentBody);
            currentBody.Clear();
            oldPath = string.Empty;
            newPath = string.Empty;
            sawHunk = false;
        }

        foreach (var line in sourceLines)
        {
            if (line.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                FlushSection();
                oldPath = ExtractDiffGitPath(line, "a/");
                newPath = ExtractDiffGitPath(line, "b/");
                continue;
            }

            if (line.StartsWith("--- ", StringComparison.Ordinal))
            {
                oldPath = NormalizeDiffPath(line[4..]);
                continue;
            }

            if (line.StartsWith("+++ ", StringComparison.Ordinal))
            {
                newPath = NormalizeDiffPath(line[4..]);
                continue;
            }

            if (line.StartsWith("index ", StringComparison.Ordinal) ||
                line.StartsWith("new file mode ", StringComparison.Ordinal) ||
                line.StartsWith("deleted file mode ", StringComparison.Ordinal) ||
                line.StartsWith("old mode ", StringComparison.Ordinal) ||
                line.StartsWith("new mode ", StringComparison.Ordinal) ||
                line.StartsWith("similarity index ", StringComparison.Ordinal) ||
                line.StartsWith("rename from ", StringComparison.Ordinal) ||
                line.StartsWith("rename to ", StringComparison.Ordinal) ||
                line.StartsWith("copy from ", StringComparison.Ordinal) ||
                line.StartsWith("copy to ", StringComparison.Ordinal))
            {
                continue;
            }

            if (line.StartsWith("@@", StringComparison.Ordinal))
            {
                sawHunk = true;
                currentBody.Add(line);
                continue;
            }

            if (line.StartsWith("Binary files ", StringComparison.Ordinal) ||
                line.StartsWith("GIT binary patch", StringComparison.Ordinal) ||
                line.StartsWith("literal ", StringComparison.Ordinal) ||
                line.StartsWith("delta ", StringComparison.Ordinal))
            {
                currentBody.Add(line);
                continue;
            }

            if (!sawHunk && currentBody.Count == 0)
            {
                continue;
            }

            currentBody.Add(line);
        }

        FlushSection();
        if (rendered.Count > 0)
        {
            return rendered;
        }

        return sourceLines.Where(line => !string.IsNullOrWhiteSpace(line)).ToList();
    }

    private static string ExtractDiffGitPath(string line, string prefix)
    {
        if (!line.StartsWith("diff --git ", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        var parts = line["diff --git ".Length..].Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return string.Empty;
        }

        return NormalizeDiffPath(string.Equals(prefix, "a/", StringComparison.Ordinal) ? parts[0] : parts[1]);
    }

    private static string NormalizeDiffPath(string? value)
    {
        var trimmed = (value ?? string.Empty).Trim().Trim('"', '\'');
        if (string.IsNullOrWhiteSpace(trimmed) || string.Equals(trimmed, "/dev/null", StringComparison.Ordinal))
        {
            return trimmed;
        }

        return trimmed.StartsWith("a/", StringComparison.Ordinal) || trimmed.StartsWith("b/", StringComparison.Ordinal)
            ? trimmed[2..]
            : trimmed;
    }

    private static string FormatDiffSectionHeader(string oldPath, string newPath)
    {
        var normalizedOld = NormalizeDiffPath(oldPath);
        var normalizedNew = NormalizeDiffPath(newPath);
        if (string.IsNullOrWhiteSpace(normalizedOld) && string.IsNullOrWhiteSpace(normalizedNew))
        {
            return string.Empty;
        }

        if (string.Equals(normalizedOld, "/dev/null", StringComparison.Ordinal))
        {
            return normalizedNew;
        }

        if (string.Equals(normalizedNew, "/dev/null", StringComparison.Ordinal))
        {
            return $"{normalizedOld} (deleted)";
        }

        if (string.IsNullOrWhiteSpace(normalizedOld) || string.Equals(normalizedOld, normalizedNew, StringComparison.Ordinal))
        {
            return normalizedNew;
        }

        if (string.IsNullOrWhiteSpace(normalizedNew))
        {
            return normalizedOld;
        }

        return $"{normalizedOld} -> {normalizedNew}";
    }

    private static string Prettify(string? value)
    {
        return (value ?? string.Empty)
            .Replace("_", " ", StringComparison.Ordinal)
            .Replace(".", " ", StringComparison.Ordinal)
            .Replace("/", " ", StringComparison.Ordinal)
            .Replace("-", " ", StringComparison.Ordinal)
            .Trim() switch
        {
            "" => string.Empty,
            var normalized => string.Join(
                ' ',
                normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                    .Select(static part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()))
        };
    }

    private static string FormatHistoryMeta(string kind, string statusLabel, DateTimeOffset value)
    {
        var timeText = value.ToString("HH:mm:ss", CultureInfo.InvariantCulture);
        var normalizedStatus = statusLabel.Trim();
        if (string.IsNullOrWhiteSpace(normalizedStatus) || ShouldHideStatusInMeta(kind, normalizedStatus))
        {
            return timeText;
        }

        return $"{normalizedStatus} • {timeText}";
    }

    private static bool ShouldHideStatusInMeta(string kind, string statusLabel)
    {
        var normalizedStatus = statusLabel.Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalizedStatus))
        {
            return true;
        }

        if (kind is "user" or "assistant")
        {
            return normalizedStatus is "completed" or "updated" or "assistant text" or "snapshot";
        }

        if (kind is "tool" or "reasoning" or "plan" or "diff")
        {
            return normalizedStatus is "completed" or "updated";
        }

        return false;
    }

    private sealed class SessionAppServerControlHistoryLog
    {
        public SessionAppServerControlHistoryLog(string sessionId)
        {
            SessionId = sessionId;
        }

        public string SessionId { get; }
        public Lock SyncRoot { get; } = new();
        public long NextSequence { get; set; }
        public List<AppServerControlProviderEvent> EventBacklog { get; } = [];
        public List<AppServerControlProviderEventSubscriber> Subscribers { get; } = [];
        public List<AppServerControlHistoryPatchSubscriber> DeltaSubscribers { get; } = [];
        public AppServerControlConversationState State { get; } = new();
        public bool PersistenceDirty { get; set; }
        public bool PersistenceScheduled { get; set; }
        public string? ScreenLogId { get; set; }
        public string? ScreenLogPath { get; set; }
        public string? LastScreenLogSignature { get; set; }
    }

    private sealed class AppServerControlConversationState
    {
        public string Provider { get; set; } = string.Empty;
        public AppServerControlSessionSummary Session { get; } = new();
        public AppServerControlThreadSummary Thread { get; } = new();
        public AppServerControlTurnSummary CurrentTurn { get; } = new();
        public AppServerControlQuickSettingsSummary QuickSettings { get; } = new();
        public AppServerControlStreamsSummary Streams { get; } = new();
        public Dictionary<string, AppServerControlHistoryItem> HistoryEntries { get; } = new(StringComparer.Ordinal);
        // Monotone appends are the normal case. BCL indexed access avoids sorting/skipping the archive per window.
        public SortedList<(long Order, string EntryId), string> HistoryOrder { get; } = new(
            Comparer<(long Order, string EntryId)>.Create(static (left, right) =>
            {
                var order = left.Order.CompareTo(right.Order);
                return order != 0 ? order : StringComparer.Ordinal.Compare(left.EntryId, right.EntryId);
            }));
        public Dictionary<string, (long Order, string EntryId)> HistoryOrderKeys { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, SortedSet<(long Order, string EntryId)>> HistoryByTurn { get; } = new(StringComparer.Ordinal);
        public Dictionary<(string Turn, string Kind), SortedSet<(long Order, string EntryId)>> HistoryByTurnKind { get; } = new();
        public Dictionary<string, (string Turn, string Kind, long Order)> HistoryIndexKeys { get; } = new(StringComparer.Ordinal);
        public HashSet<string> StreamingHistoryIds { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ProvisionalToolIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, AppServerControlItemSummary> Items { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> ItemAliases { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, HashSet<string>> UserItemsByTurn { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> UserItemTurns { get; } = new(StringComparer.Ordinal);
        public HashSet<string> ActiveItemIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, AppServerControlRequestSummary> Requests { get; } = new(StringComparer.Ordinal);
        public HashSet<string> OpenRequestIds { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, AppServerControlToolRenderState> ToolRenderStates { get; } = new(StringComparer.Ordinal);
        public List<AppServerControlRuntimeNotice> Notices { get; } = [];
        public long NextHistoryOrder { get; set; }
    }

    private sealed class AppServerControlProviderEventSubscriber(ChannelWriter<AppServerControlProviderEvent> writer)
    {
        public ChannelWriter<AppServerControlProviderEvent> Writer { get; } = writer;
    }

    private sealed class AppServerControlHistoryPatchSubscriber(ChannelWriter<AppServerControlHistoryPatch> writer)
    {
        public ChannelWriter<AppServerControlHistoryPatch> Writer { get; } = writer;
    }

    private sealed class AppServerControlToolRenderState
    {
        public AppServerControlToolPresentation? Presentation { get; set; }
        public BoundedToolOutputAccumulator Output { get; } = new();
    }

    private sealed class AppServerControlPersistedSessionState
    {
        public long NextSequence { get; init; }
        public AppServerControlPersistedConversationState State { get; init; } = new();
    }

    private sealed class AppServerControlPersistedConversationState
    {
        public string Provider { get; init; } = string.Empty;
        public AppServerControlSessionSummary Session { get; init; } = new();
        public AppServerControlThreadSummary Thread { get; init; } = new();
        public AppServerControlTurnSummary CurrentTurn { get; init; } = new();
        public AppServerControlQuickSettingsSummary QuickSettings { get; init; } = new();
        public AppServerControlStreamsSummary Streams { get; init; } = new();
        public List<AppServerControlHistoryItem> HistoryEntries { get; init; } = [];
        public List<AppServerControlItemSummary> Items { get; init; } = [];
        public List<AppServerControlRequestSummary> Requests { get; init; } = [];
        public List<AppServerControlPersistedToolRenderState> ToolRenderStates { get; init; } = [];
        public List<AppServerControlRuntimeNotice> Notices { get; init; } = [];
        public long NextHistoryOrder { get; init; }
    }

    private sealed class AppServerControlPersistedToolRenderState
    {
        public string EntryId { get; init; } = string.Empty;
        public AppServerControlToolPresentation? Presentation { get; init; }
        public BoundedToolOutputSnapshot? Output { get; init; }
    }

    private sealed class AppServerControlScreenLogHeaderRecord
    {
        public string Format { get; init; } = ScreenLogFormatVersion;
        public string RecordType { get; init; } = "header";
        public string LogId { get; init; } = string.Empty;
        public string SessionId { get; init; } = string.Empty;
        public string Provider { get; init; } = string.Empty;
        public DateTimeOffset CreatedAt { get; init; }
    }

    private sealed class AppServerControlScreenLogDeltaRecord
    {
        public string Format { get; init; } = ScreenLogFormatVersion;
        public string RecordType { get; init; } = "screen_delta";
        public string LogId { get; init; } = string.Empty;
        public string SessionId { get; init; } = string.Empty;
        public string Provider { get; init; } = string.Empty;
        public long LatestSequence { get; init; }
        public DateTimeOffset RecordedAt { get; init; }
        public AppServerControlScreenLogSessionState Session { get; init; } = new();
        public AppServerControlScreenLogTurnState CurrentTurn { get; init; } = new();
        public List<AppServerControlScreenLogHistoryEntry> HistoryUpserts { get; init; } = [];
        public List<string> HistoryRemovals { get; init; } = [];
    }

    private sealed class AppServerControlScreenLogSessionState
    {
        public string State { get; init; } = string.Empty;
        public string StateLabel { get; init; } = string.Empty;
        public string? Reason { get; init; }
        public string? LastError { get; init; }
    }

    private sealed class AppServerControlScreenLogTurnState
    {
        public string? TurnId { get; init; }
        public string State { get; init; } = string.Empty;
        public string StateLabel { get; init; } = string.Empty;
        public string? Model { get; init; }
        public string? Effort { get; init; }
    }

    private sealed class AppServerControlScreenLogHistoryEntry
    {
        public string EntryId { get; init; } = string.Empty;
        public long Order { get; init; }
        public string Kind { get; init; } = string.Empty;
        public string? ItemType { get; init; }
        public string Status { get; init; } = string.Empty;
        public string Label { get; init; } = string.Empty;
        public string Title { get; init; } = string.Empty;
        public string CommandText { get; init; } = string.Empty;
        public AppServerControlToolPresentation? ToolPresentation { get; init; }
        public string Meta { get; init; } = string.Empty;
        public string Body { get; init; } = string.Empty;
        public string RenderMode { get; init; } = string.Empty;
        public bool CollapsedByDefault { get; init; }
        public string Preview { get; init; } = string.Empty;
        public int LineCount { get; init; }
        public bool Streaming { get; init; }
        public DateTimeOffset UpdatedAt { get; init; }
        public List<AppServerControlAttachmentReference> Attachments { get; init; } = [];
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(AppServerControlPersistedSessionState))]
    private sealed partial class AppServerControlPersistenceJsonContext : JsonSerializerContext;

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(AppServerControlScreenLogHeaderRecord))]
    [JsonSerializable(typeof(AppServerControlScreenLogDeltaRecord))]
    private sealed partial class AppServerControlScreenLogJsonContext : JsonSerializerContext;
}

internal sealed class SubscriptionState
{
    private readonly Action _dispose;
    private int _disposed;

    public SubscriptionState(Action dispose)
    {
        _dispose = dispose;
    }

    public void Close()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            _dispose();
        }
    }
}

public sealed class AppServerControlProviderEventSubscription : IDisposable
{
    private readonly SubscriptionState _state;
    private int _disposed;

    internal AppServerControlProviderEventSubscription(ChannelReader<AppServerControlProviderEvent> reader, SubscriptionState state)
    {
        Reader = reader;
        _state = state;
    }

    public ChannelReader<AppServerControlProviderEvent> Reader { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _state.Close();
    }
}

public sealed class SessionAppServerControlHistoryPatchSubscription : IDisposable
{
    private readonly SubscriptionState _state;
    private int _disposed;

    internal SessionAppServerControlHistoryPatchSubscription(ChannelReader<AppServerControlHistoryPatch> reader, SubscriptionState state)
    {
        Reader = reader;
        _state = state;
    }

    public ChannelReader<AppServerControlHistoryPatch> Reader { get; }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _state.Close();
    }
}











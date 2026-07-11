using Ai.Tlbx.MidTerm.Common.Protocol;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Settings;
using System.Globalization;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed class ManagerBarQueueService : IAsyncDisposable
{
    private const string AutomationQueueKind = "automation";
    private const string PromptQueueKind = "prompt";
    private const double CooldownHeatThreshold = 0.25;
    private const int PostTriggerIgnoreHeatMs = 5000;
    private const int TerminalHeatSettleWindowMs = 5000;
    private const int PollIntervalMs = 500;
    private const int DuplicateEnqueueWindowMs = 1500;

    private readonly string _statePath;
    private readonly IManagerBarQueueRuntime _runtime;
    private readonly TimeProvider _timeProvider;
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly TaskCompletionSource _startedTcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private List<ManagerBarQueueEntryDto> _entries = [];
    private readonly Dictionary<string, RecentEnqueue> _recentEnqueues = new(StringComparer.Ordinal);
    private readonly HashSet<string> _activeImmediateDispatchSessions = new(StringComparer.Ordinal);
    private string _serializedState = string.Empty;
    private Task? _processingTask;

    private sealed record RecentEnqueue(string QueueId, DateTimeOffset EnqueuedAt);

    public ManagerBarQueueService(
        SettingsService settingsService,
        IManagerBarQueueRuntime runtime,
        TimeProvider? timeProvider = null)
        : this(settingsService.SettingsDirectory, runtime, timeProvider)
    {
    }

    public ManagerBarQueueService(
        string settingsDirectory,
        IManagerBarQueueRuntime runtime,
        TimeProvider? timeProvider = null)
    {
        _statePath = Path.Combine(settingsDirectory, "manager-bar-queue.json");
        _runtime = runtime;
        _timeProvider = timeProvider ?? TimeProvider.System;
        Load();
    }

    public event Action? OnChanged;

    public void Start()
    {
        lock (_lock)
        {
            if (_processingTask is not null)
            {
                return;
            }

            _processingTask = Task.Run(ProcessLoopAsync, _shutdownCts.Token);
            _startedTcs.TrySetResult();
        }
    }

    public IReadOnlyList<ManagerBarQueueEntryDto> GetSnapshot(IEnumerable<string>? validSessionIds = null)
    {
        List<ManagerBarQueueEntryDto> snapshot;
        lock (_lock)
        {
            snapshot = CloneEntries(_entries);
        }

        return FilterToValidSessions(snapshot, validSessionIds);
    }

    public ManagerBarQueueEntryDto? Enqueue(string sessionId, ManagerBarButton action)
    {
        if (!TryNormalizeActionSubmission(sessionId, action, out var trimmedSessionId, out var normalizedAction))
        {
            return null;
        }

        if (IsImmediateManagerAction(normalizedAction))
        {
            return null;
        }

        var now = _timeProvider.GetUtcNow();
        ManagerBarQueueEntryDto? entry;
        lock (_lock)
        {
            entry = EnqueueAutomationCoreLocked(trimmedSessionId, normalizedAction, now);
        }

        if (entry is not null)
        {
            OnChanged?.Invoke();
        }

        return entry is null ? null : CloneEntry(entry);
    }

    public ManagerBarQueueEntryDto? EnqueuePrompt(string sessionId, AppServerControlTurnRequest turn)
    {
        if (!TryNormalizePromptSubmission(sessionId, turn, out var trimmedSessionId, out var normalizedTurn))
        {
            return null;
        }

        ManagerBarQueueEntryDto entry;
        lock (_lock)
        {
            entry = EnqueuePromptLocked(trimmedSessionId, normalizedTurn);
        }

        OnChanged?.Invoke();
        return CloneEntry(entry);
    }

    public ManagerBarQueueEntryDto? EnqueuePromptAt(
        string sessionId,
        AppServerControlTurnRequest turn,
        DateTimeOffset runAt)
    {
        if (!TryNormalizePromptSubmission(sessionId, turn, out var trimmedSessionId, out var normalizedTurn))
        {
            return null;
        }

        ManagerBarQueueEntryDto entry;
        lock (_lock)
        {
            entry = EnqueuePromptLocked(trimmedSessionId, normalizedTurn, QueuePhase.PendingInterval, runAt);
        }

        OnChanged?.Invoke();
        return CloneEntry(entry);
    }

    public async Task<(bool Accepted, ManagerBarQueueEntryDto? Entry)> SubmitPromptAsync(
        string sessionId,
        AppServerControlTurnRequest turn,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePromptSubmission(sessionId, turn, out var trimmedSessionId, out var normalizedTurn))
        {
            return (false, null);
        }

        ManagerBarQueueEntryDto? queuedEntry = null;
        AppServerControlTurnRequest? immediateTurn = null;
        lock (_lock)
        {
            if (CanDispatchImmediatelyLocked(trimmedSessionId))
            {
                _activeImmediateDispatchSessions.Add(trimmedSessionId);
                immediateTurn = CloneTurn(normalizedTurn);
            }
            else
            {
                queuedEntry = EnqueuePromptLocked(trimmedSessionId, normalizedTurn);
            }
        }

        if (queuedEntry is not null)
        {
            OnChanged?.Invoke();
            return (true, CloneEntry(queuedEntry));
        }

        if (immediateTurn is null)
        {
            return (false, null);
        }

        try
        {
            await _runtime.SendTurnAsync(trimmedSessionId, immediateTurn, cancellationToken).ConfigureAwait(false);
            return (true, null);
        }
        finally
        {
            lock (_lock)
            {
                _activeImmediateDispatchSessions.Remove(trimmedSessionId);
            }
        }
    }

    public async Task<bool> DispatchPromptDirectAsync(
        string sessionId,
        AppServerControlTurnRequest turn,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizePromptSubmission(sessionId, turn, out var trimmedSessionId, out var normalizedTurn))
        {
            return false;
        }

        await _runtime.SendTurnAsync(trimmedSessionId, normalizedTurn, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<(bool Accepted, ManagerBarQueueEntryDto? Entry)> SubmitActionAsync(
        string sessionId,
        ManagerBarButton action,
        CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeActionSubmission(sessionId, action, out var trimmedSessionId, out var normalizedAction))
        {
            return (false, null);
        }

        var now = _timeProvider.GetUtcNow();
        ManagerBarQueueEntryDto? queuedEntry = null;
        string? immediatePrompt = null;

        lock (_lock)
        {
            if (IsImmediateManagerAction(normalizedAction) && CanDispatchImmediatelyLocked(trimmedSessionId))
            {
                _activeImmediateDispatchSessions.Add(trimmedSessionId);
                immediatePrompt = normalizedAction.Prompts[0];
            }
            else
            {
                queuedEntry = EnqueueAutomationCoreLocked(trimmedSessionId, normalizedAction, now);
            }
        }

        if (queuedEntry is not null)
        {
            OnChanged?.Invoke();
            return (true, CloneEntry(queuedEntry));
        }

        if (string.IsNullOrWhiteSpace(immediatePrompt))
        {
            return (false, null);
        }

        try
        {
            await _runtime.SendPromptAsync(trimmedSessionId, immediatePrompt, cancellationToken).ConfigureAwait(false);
            return (true, null);
        }
        finally
        {
            lock (_lock)
            {
                _activeImmediateDispatchSessions.Remove(trimmedSessionId);
            }
        }
    }

    private ManagerBarQueueEntryDto? EnqueueAutomationCoreLocked(
        string trimmedSessionId,
        ManagerBarButton normalizedAction,
        DateTimeOffset now)
    {
        var phase = GetInitialQueuePhase(normalizedAction);
        var nextRunAt = phase == QueuePhase.PendingSchedule
            ? ComputeNextScheduleTime(normalizedAction.Trigger.Schedule, now)
            : null;
        if (phase == QueuePhase.PendingSchedule && nextRunAt is null)
        {
            return null;
        }

        PruneRecentEnqueuesLocked(now);
        var enqueueSignature = BuildEnqueueSignature(trimmedSessionId, normalizedAction);
        if (TryGetRecentDuplicateLocked(enqueueSignature, now, out var existing))
        {
            return existing;
        }

        var entry = new ManagerBarQueueEntryDto
        {
            QueueId = $"{normalizedAction.Id}-{Guid.NewGuid():N}",
            SessionId = trimmedSessionId,
            Kind = AutomationQueueKind,
            Action = normalizedAction,
            Turn = null,
            Phase = phase,
            NextPromptIndex = 0,
            CompletedCycles = 0,
            NextRunAt = nextRunAt,
            IgnoreHeatUntil = null,
            AwaitingHeatRise = false
        };

        _entries.Add(CloneEntry(entry));
        _recentEnqueues[enqueueSignature] = new RecentEnqueue(entry.QueueId, now);
        PersistLocked();
        return entry;
    }

    public bool Remove(string queueId)
    {
        if (string.IsNullOrWhiteSpace(queueId))
        {
            return false;
        }

        var removed = false;
        lock (_lock)
        {
            var index = _entries.FindIndex(entry => string.Equals(entry.QueueId, queueId, StringComparison.Ordinal));
            if (index >= 0)
            {
                _entries.RemoveAt(index);
                PersistLocked();
                removed = true;
            }
        }

        if (removed)
        {
            OnChanged?.Invoke();
        }

        return removed;
    }

    public void RemoveSession(string sessionId)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        var removed = false;
        lock (_lock)
        {
            for (var index = _entries.Count - 1; index >= 0; index--)
            {
                if (!string.Equals(_entries[index].SessionId, sessionId, StringComparison.Ordinal))
                {
                    continue;
                }

                _entries.RemoveAt(index);
                removed = true;
            }

            if (removed)
            {
                PersistLocked();
            }
        }

        if (removed)
        {
            OnChanged?.Invoke();
        }
    }

    public void PruneToValidSessions(IEnumerable<string>? validSessionIds)
    {
        var validSet = CreateValidSet(validSessionIds);
        if (validSet is null)
        {
            return;
        }

        var removed = false;
        lock (_lock)
        {
            for (var index = _entries.Count - 1; index >= 0; index--)
            {
                if (validSet.Contains(_entries[index].SessionId))
                {
                    continue;
                }

                _entries.RemoveAt(index);
                removed = true;
            }

            if (removed)
            {
                PersistLocked();
            }
        }

        if (removed)
        {
            OnChanged?.Invoke();
        }
    }

    private async Task ProcessLoopAsync()
    {
        var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(PollIntervalMs));
        try
        {
            while (await timer.WaitForNextTickAsync(_shutdownCts.Token).ConfigureAwait(false))
            {
                await ProcessEntriesAsync(_shutdownCts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            timer.Dispose();
        }
    }

    private async Task ProcessEntriesAsync(CancellationToken cancellationToken)
    {
        await _startedTcs.Task.ConfigureAwait(false);

        var validSessionIds = new HashSet<string>(_runtime.GetActiveSessionIds(), StringComparer.Ordinal);
        var now = _timeProvider.GetUtcNow();
        var changed = false;
        var pendingSends = new List<PendingQueueDispatch>();

        lock (_lock)
        {
            var dispatchedSessions = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < _entries.Count;)
            {
                var entry = _entries[index];
                if (!validSessionIds.Contains(entry.SessionId))
                {
                    _entries.RemoveAt(index);
                    changed = true;
                    continue;
                }

                if (dispatchedSessions.Contains(entry.SessionId))
                {
                    index += 1;
                    continue;
                }

                if (!IsQueueEntryReady(entry, now))
                {
                    index += 1;
                    continue;
                }

                var dispatch = ResolveDispatch(entry);
                if (dispatch is null)
                {
                    _entries.RemoveAt(index);
                    changed = true;
                    continue;
                }

                pendingSends.Add(dispatch);
                var queueId = entry.QueueId;
                var sessionId = entry.SessionId;
                var usesTurnQueue = _runtime.UsesTurnQueue(sessionId);
                var removed = AdvanceQueueEntry(entry, index, now);
                if (!usesTurnQueue)
                {
                    ApplyTerminalRearmFenceLocked(sessionId, now, queueId);
                }

                dispatchedSessions.Add(sessionId);
                changed = true;

                if (!removed)
                {
                    index += 1;
                }
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

        foreach (var send in pendingSends)
        {
            try
            {
                if (send.Kind == PromptQueueKind && send.Turn is not null)
                {
                    await _runtime.SendTurnAsync(send.SessionId, send.Turn, cancellationToken).ConfigureAwait(false);
                }
                else if (!string.IsNullOrWhiteSpace(send.Prompt))
                {
                    await _runtime.SendPromptAsync(send.SessionId, send.Prompt, cancellationToken).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                Log.Warn(() => $"Manager bar queue send failed for session {send.SessionId}: {ex.Message}");
            }
        }
    }

    private bool IsQueueEntryReady(ManagerBarQueueEntryDto entry, DateTimeOffset now)
    {
        var phase = ParsePhase(entry.Phase);
        if (_runtime.UsesTurnQueue(entry.SessionId))
        {
            if (!IsTimeGateReady(entry, phase, now))
            {
                return false;
            }

            return _runtime.IsTurnQueueReady(entry.SessionId);
        }

        if (!IsTimeGateReady(entry, phase, now))
        {
            return false;
        }

        var gateActive =
            phase is QueuePhase.PendingCooldown or QueuePhase.ChainCooldown ||
            entry.IgnoreHeatUntil is not null ||
            entry.AwaitingHeatRise;
        if (!gateActive)
        {
            return true;
        }

        var heat = _runtime.GetHeatSnapshot(entry.SessionId);
        return EvaluateCooldown(entry, heat, now);
    }

    private static bool IsTimeGateReady(
        ManagerBarQueueEntryDto entry,
        string phase,
        DateTimeOffset now)
    {
        if (phase is QueuePhase.PendingInterval or QueuePhase.PendingSchedule)
        {
            return entry.NextRunAt is not null && now >= entry.NextRunAt.Value;
        }

        return true;
    }

    private static bool EvaluateCooldown(
        ManagerBarQueueEntryDto entry,
        SessionHeatSnapshot heat,
        DateTimeOffset now)
    {
        if (entry.IgnoreHeatUntil is not null && now < entry.IgnoreHeatUntil.Value)
        {
            return false;
        }

        if (entry.AwaitingHeatRise && HasObservedOutputSinceLastDispatch(entry, heat.LastOutputAt))
        {
            entry.AwaitingHeatRise = false;
        }

        if (!IsTerminalCooldownReady(heat, now))
        {
            return false;
        }

        return !entry.AwaitingHeatRise;
    }

    private static bool IsTerminalCooldownReady(SessionHeatSnapshot heat, DateTimeOffset now)
    {
        return heat.CurrentHeat <= CooldownHeatThreshold &&
               !HasRecentOutput(heat.LastOutputAt, now);
    }

    private static bool HasRecentOutput(DateTimeOffset? lastOutputAt, DateTimeOffset now)
    {
        return lastOutputAt is { } outputAt &&
               now < outputAt.AddMilliseconds(TerminalHeatSettleWindowMs);
    }

    private static bool HasObservedOutputSinceLastDispatch(
        ManagerBarQueueEntryDto entry,
        DateTimeOffset? lastOutputAt)
    {
        if (lastOutputAt is null)
        {
            return false;
        }

        if (entry.IgnoreHeatUntil is not { } ignoreHeatUntil)
        {
            return true;
        }

        var dispatchStartedAt = ignoreHeatUntil.AddMilliseconds(-PostTriggerIgnoreHeatMs);
        return lastOutputAt >= dispatchStartedAt;
    }

    private bool AdvanceQueueEntry(ManagerBarQueueEntryDto entry, int index, DateTimeOffset now)
    {
        if (string.Equals(entry.Kind, PromptQueueKind, StringComparison.Ordinal))
        {
            _entries.RemoveAt(index);
            return true;
        }

        var action = entry.Action?.Normalize();
        if (action is null)
        {
            _entries.RemoveAt(index);
            return true;
        }

        entry.Action = action;
        entry.NextPromptIndex += 1;
        if (entry.NextPromptIndex < action.Prompts.Count)
        {
            entry.Phase = QueuePhase.ChainCooldown;
            entry.IgnoreHeatUntil = now.AddMilliseconds(PostTriggerIgnoreHeatMs);
            entry.AwaitingHeatRise = true;
            entry.NextRunAt = null;
            return false;
        }

        entry.CompletedCycles += 1;
        entry.NextPromptIndex = 0;

        var trigger = action.Trigger.Normalize();
        switch (trigger.Kind)
        {
            case "fireAndForget":
            case "onCooldown":
                _entries.RemoveAt(index);
                return true;
            case "repeatCount":
                if (entry.CompletedCycles >= trigger.RepeatCount)
                {
                    _entries.RemoveAt(index);
                    return true;
                }

                entry.Phase = QueuePhase.PendingCooldown;
                entry.IgnoreHeatUntil = now.AddMilliseconds(PostTriggerIgnoreHeatMs);
                entry.AwaitingHeatRise = true;
                entry.NextRunAt = null;
                return false;
            case "repeatInterval":
                entry.Phase = QueuePhase.PendingInterval;
                entry.NextRunAt = now.Add(IntervalToTimeSpan(trigger));
                entry.IgnoreHeatUntil = null;
                entry.AwaitingHeatRise = false;
                return false;
            case "schedule":
                entry.Phase = QueuePhase.PendingSchedule;
                entry.NextRunAt = ComputeNextScheduleTime(trigger.Schedule, now);
                entry.IgnoreHeatUntil = null;
                entry.AwaitingHeatRise = false;
                if (entry.NextRunAt is null)
                {
                    _entries.RemoveAt(index);
                    return true;
                }
                return false;
            default:
                _entries.RemoveAt(index);
                return true;
        }
    }

    private void ApplyTerminalRearmFenceLocked(string sessionId, DateTimeOffset now, string excludeQueueId)
    {
        var ignoreUntil = now.AddMilliseconds(PostTriggerIgnoreHeatMs);
        foreach (var entry in _entries)
        {
            if (!string.Equals(entry.SessionId, sessionId, StringComparison.Ordinal) ||
                string.Equals(entry.QueueId, excludeQueueId, StringComparison.Ordinal))
            {
                continue;
            }

            entry.IgnoreHeatUntil = entry.IgnoreHeatUntil is { } existing && existing > ignoreUntil
                ? existing
                : ignoreUntil;
            entry.AwaitingHeatRise = true;
        }
    }

    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_statePath))
            {
                _entries = [];
                _serializedState = SerializeEntries(_entries);
                return;
            }

            try
            {
                var json = File.ReadAllText(_statePath);
                var stored = JsonSerializer.Deserialize(
                                 json,
                                 AppJsonContext.Default.ListManagerBarQueueEntryDto)
                             ?? [];
                _entries = stored
                    .Select(NormalizeEntry)
                    .Where(static entry => entry is not null)
                    .Cast<ManagerBarQueueEntryDto>()
                    .ToList();
                _serializedState = SerializeEntries(_entries);
            }
            catch (Exception ex)
            {
                Log.Warn(() => $"Failed to load manager bar queue: {ex.Message}");
                _entries = [];
                _serializedState = SerializeEntries(_entries);
            }
        }
    }

    private void PersistLocked()
    {
        try
        {
            _serializedState = SerializeEntries(_entries);
            var dir = Path.GetDirectoryName(_statePath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            if (_entries.Count == 0)
            {
                if (File.Exists(_statePath))
                {
                    File.Delete(_statePath);
                }

                return;
            }

            File.WriteAllText(_statePath, _serializedState);
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"Failed to save manager bar queue: {ex.Message}");
        }
    }

    private bool TryGetRecentDuplicateLocked(
        string enqueueSignature,
        DateTimeOffset now,
        out ManagerBarQueueEntryDto? entry)
    {
        entry = null;
        if (!_recentEnqueues.TryGetValue(enqueueSignature, out var recent))
        {
            return false;
        }

        if ((now - recent.EnqueuedAt).TotalMilliseconds > DuplicateEnqueueWindowMs)
        {
            _recentEnqueues.Remove(enqueueSignature);
            return false;
        }

        var existing = _entries.FirstOrDefault(candidate =>
            string.Equals(candidate.QueueId, recent.QueueId, StringComparison.Ordinal));
        if (existing is null)
        {
            _recentEnqueues.Remove(enqueueSignature);
            return false;
        }

        entry = CloneEntry(existing);
        return true;
    }

    private void PruneRecentEnqueuesLocked(DateTimeOffset now)
    {
        foreach (var pair in _recentEnqueues.ToArray())
        {
            if ((now - pair.Value.EnqueuedAt).TotalMilliseconds <= DuplicateEnqueueWindowMs)
            {
                continue;
            }

            _recentEnqueues.Remove(pair.Key);
        }
    }

    private static string BuildEnqueueSignature(string sessionId, ManagerBarButton action)
    {
        var prompts = string.Join(
            "\u001f",
            action.Prompts.Select(static prompt => prompt.Trim()));
        var schedule = string.Join(
            "\u001e",
            action.Trigger.Schedule.Select(static scheduleEntry => $"{scheduleEntry.Repeat}@{scheduleEntry.TimeOfDay}"));

        return string.Join(
            "\u001d",
            sessionId,
            action.Id,
            action.Label,
            action.ActionType,
            action.Trigger.Kind,
            action.Trigger.RepeatCount.ToString(CultureInfo.InvariantCulture),
            action.Trigger.RepeatEveryValue.ToString(CultureInfo.InvariantCulture),
            action.Trigger.RepeatEveryUnit,
            prompts,
            schedule);
    }

    private static ManagerBarQueueEntryDto? NormalizeEntry(ManagerBarQueueEntryDto? entry)
    {
        if (entry is null || string.IsNullOrWhiteSpace(entry.SessionId))
        {
            return null;
        }

        var kind = NormalizeQueueKind(entry.Kind, entry.Action, entry.Turn);
        var normalizedAction = entry.Action?.Normalize();
        var normalizedTurn = NormalizeTurn(entry.Turn);
        if (kind == PromptQueueKind)
        {
            if (normalizedTurn is null)
            {
                return null;
            }

            return new ManagerBarQueueEntryDto
            {
                QueueId = string.IsNullOrWhiteSpace(entry.QueueId) ? Guid.NewGuid().ToString("N") : entry.QueueId,
                SessionId = entry.SessionId.Trim(),
                Kind = PromptQueueKind,
                Action = null,
                Turn = normalizedTurn,
                Phase = ParsePhase(entry.Phase),
                NextPromptIndex = 0,
                CompletedCycles = 0,
                NextRunAt = entry.NextRunAt,
                IgnoreHeatUntil = entry.IgnoreHeatUntil,
                AwaitingHeatRise = entry.AwaitingHeatRise
            };
        }

        if (normalizedAction is null)
        {
            return null;
        }

        var maxPromptIndex = Math.Max(0, normalizedAction.Prompts.Count - 1);
        var phase = ParsePhase(entry.Phase);
        return new ManagerBarQueueEntryDto
        {
            QueueId = string.IsNullOrWhiteSpace(entry.QueueId) ? Guid.NewGuid().ToString("N") : entry.QueueId,
            SessionId = entry.SessionId.Trim(),
            Kind = AutomationQueueKind,
            Action = normalizedAction,
            Turn = null,
            Phase = phase,
            NextPromptIndex = Math.Clamp(entry.NextPromptIndex, 0, maxPromptIndex),
            CompletedCycles = Math.Max(0, entry.CompletedCycles),
            NextRunAt = entry.NextRunAt,
            IgnoreHeatUntil = entry.IgnoreHeatUntil,
            AwaitingHeatRise = entry.AwaitingHeatRise
        };
    }

    private static IReadOnlyList<ManagerBarQueueEntryDto> FilterToValidSessions(
        IReadOnlyList<ManagerBarQueueEntryDto> entries,
        IEnumerable<string>? validSessionIds)
    {
        var validSet = CreateValidSet(validSessionIds);
        if (validSet is null)
        {
            return entries;
        }

        return entries
            .Where(entry => validSet.Contains(entry.SessionId))
            .Select(CloneEntry)
            .ToArray();
    }

    private static HashSet<string>? CreateValidSet(IEnumerable<string>? validSessionIds)
    {
        return validSessionIds is null
            ? null
            : new HashSet<string>(
                validSessionIds.Where(static id => !string.IsNullOrWhiteSpace(id)),
                StringComparer.Ordinal);
    }

    private static List<ManagerBarQueueEntryDto> CloneEntries(IEnumerable<ManagerBarQueueEntryDto> entries)
    {
        return entries.Select(CloneEntry).ToList();
    }

    private static ManagerBarQueueEntryDto CloneEntry(ManagerBarQueueEntryDto entry)
    {
        return new ManagerBarQueueEntryDto
        {
            QueueId = entry.QueueId,
            SessionId = entry.SessionId,
            Kind = NormalizeQueueKind(entry.Kind, entry.Action, entry.Turn),
            Action = entry.Action?.Normalize(),
            Turn = CloneTurn(entry.Turn),
            Phase = ParsePhase(entry.Phase),
            NextPromptIndex = entry.NextPromptIndex,
            CompletedCycles = entry.CompletedCycles,
            NextRunAt = entry.NextRunAt,
            IgnoreHeatUntil = entry.IgnoreHeatUntil,
            AwaitingHeatRise = entry.AwaitingHeatRise
        };
    }

    private static bool IsImmediateManagerAction(ManagerBarButton action)
    {
        return string.Equals(action.ActionType, "single", StringComparison.Ordinal)
               && string.Equals(action.Trigger.Kind, "fireAndForget", StringComparison.Ordinal);
    }

    private static string NormalizeQueueKind(string? kind, ManagerBarButton? action, AppServerControlTurnRequest? turn)
    {
        if (string.Equals(kind, PromptQueueKind, StringComparison.Ordinal))
        {
            return PromptQueueKind;
        }

        if (turn is not null && action is null)
        {
            return PromptQueueKind;
        }

        return AutomationQueueKind;
    }

    private static string GetInitialQueuePhase(ManagerBarButton action)
    {
        if (string.Equals(action.Trigger.Kind, "schedule", StringComparison.Ordinal))
        {
            return QueuePhase.PendingSchedule;
        }

        if (ShouldWaitForInitialCooldown(action))
        {
            return QueuePhase.PendingCooldown;
        }

        return QueuePhase.PendingImmediate;
    }

    private static bool ShouldWaitForInitialCooldown(ManagerBarButton action)
    {
        return action.Trigger.Kind is "onCooldown" or "repeatCount" or "repeatInterval";
    }

    private static string ParsePhase(string? phase)
    {
        return phase switch
        {
            QueuePhase.PendingCooldown => QueuePhase.PendingCooldown,
            QueuePhase.ChainCooldown => QueuePhase.ChainCooldown,
            QueuePhase.PendingInterval => QueuePhase.PendingInterval,
            QueuePhase.PendingSchedule => QueuePhase.PendingSchedule,
            _ => QueuePhase.PendingImmediate
        };
    }

    private static TimeSpan IntervalToTimeSpan(ManagerBarTrigger trigger)
    {
        var value = Math.Max(1, trigger.RepeatEveryValue);
        return trigger.RepeatEveryUnit switch
        {
            "seconds" => TimeSpan.FromSeconds(value),
            "hours" => TimeSpan.FromHours(value),
            "days" => TimeSpan.FromDays(value),
            _ => TimeSpan.FromMinutes(value)
        };
    }

    private static DateTimeOffset? ComputeNextScheduleTime(
        IEnumerable<ManagerBarScheduleEntry> schedule,
        DateTimeOffset from)
    {
        DateTimeOffset? best = null;
        var baseLocal = from.LocalDateTime;

        for (var dayOffset = 0; dayOffset < 8; dayOffset += 1)
        {
            var day = baseLocal.Date.AddDays(dayOffset);
            foreach (var entry in schedule)
            {
                var normalized = entry.Normalize();
                if (normalized is null || !IsScheduleRepeatActive(normalized.Repeat, day.DayOfWeek))
                {
                    continue;
                }

                var parts = normalized.TimeOfDay.Split(':', StringSplitOptions.TrimEntries);
                if (parts.Length != 2 ||
                    !int.TryParse(parts[0], CultureInfo.InvariantCulture, out var hours) ||
                    !int.TryParse(parts[1], CultureInfo.InvariantCulture, out var minutes))
                {
                    continue;
                }

                var candidateLocal = day.AddHours(hours).AddMinutes(minutes);
                var candidate = new DateTimeOffset(candidateLocal, from.Offset);
                if (candidate <= from)
                {
                    continue;
                }

                if (best is null || candidate < best.Value)
                {
                    best = candidate;
                }
            }
        }

        return best;
    }

    private static bool IsScheduleRepeatActive(string repeat, DayOfWeek dayOfWeek)
    {
        return repeat switch
        {
            "weekdays" => dayOfWeek is >= DayOfWeek.Monday and <= DayOfWeek.Friday,
            "weekends" => dayOfWeek is DayOfWeek.Saturday or DayOfWeek.Sunday,
            _ => true
        };
    }

    private static string SerializeEntries(IReadOnlyList<ManagerBarQueueEntryDto> entries)
    {
        return JsonSerializer.Serialize(entries, AppJsonContext.Default.ListManagerBarQueueEntryDto);
    }

    private static PendingQueueDispatch? ResolveDispatch(ManagerBarQueueEntryDto entry)
    {
        if (string.Equals(entry.Kind, PromptQueueKind, StringComparison.Ordinal))
        {
            var turn = NormalizeTurn(entry.Turn);
            return turn is null
                ? null
                : new PendingQueueDispatch(entry.SessionId, PromptQueueKind, turn.Text, turn);
        }

        var action = entry.Action?.Normalize();
        var prompt = action?.Prompts.ElementAtOrDefault(entry.NextPromptIndex);
        return string.IsNullOrWhiteSpace(prompt)
            ? null
            : new PendingQueueDispatch(entry.SessionId, AutomationQueueKind, prompt, null);
    }

    private static AppServerControlTurnRequest? NormalizeTurn(AppServerControlTurnRequest? turn)
    {
        if (turn is null)
        {
            return null;
        }

        var normalized = new AppServerControlTurnRequest
        {
            Text = string.IsNullOrWhiteSpace(turn.Text) ? null : turn.Text.Trim(),
            Model = string.IsNullOrWhiteSpace(turn.Model) ? null : turn.Model.Trim(),
            Effort = string.IsNullOrWhiteSpace(turn.Effort) ? null : turn.Effort.Trim(),
            PlanMode = string.IsNullOrWhiteSpace(turn.PlanMode) ? null : turn.PlanMode.Trim(),
            PermissionMode = string.IsNullOrWhiteSpace(turn.PermissionMode) ? null : turn.PermissionMode.Trim(),
            Attachments = turn.Attachments?
                .Select(CloneAttachment)
                .Where(static attachment => attachment is not null)
                .Cast<AppServerControlAttachmentReference>()
                .ToList() ?? [],
            TerminalReplay = turn.TerminalReplay?
                .Select(CloneTerminalReplayStep)
                .Where(static step => step is not null)
                .Cast<AppServerControlTerminalReplayStep>()
                .ToList() ?? []
        };

        return string.IsNullOrWhiteSpace(normalized.Text)
               && normalized.Attachments.Count == 0
               && normalized.TerminalReplay.Count == 0
            ? null
            : normalized;
    }

    private static AppServerControlTurnRequest? CloneTurn(AppServerControlTurnRequest? turn)
    {
        return NormalizeTurn(turn);
    }

    private static AppServerControlAttachmentReference? CloneAttachment(AppServerControlAttachmentReference? attachment)
    {
        if (attachment is null || string.IsNullOrWhiteSpace(attachment.Path))
        {
            return null;
        }

        return new AppServerControlAttachmentReference
        {
            Kind = string.IsNullOrWhiteSpace(attachment.Kind) ? "file" : attachment.Kind.Trim(),
            Path = attachment.Path.Trim(),
            MimeType = string.IsNullOrWhiteSpace(attachment.MimeType) ? null : attachment.MimeType.Trim(),
            DisplayName = string.IsNullOrWhiteSpace(attachment.DisplayName) ? null : attachment.DisplayName.Trim()
        };
    }

    private static AppServerControlTerminalReplayStep? CloneTerminalReplayStep(AppServerControlTerminalReplayStep? step)
    {
        if (step is null)
        {
            return null;
        }

        var kind = string.IsNullOrWhiteSpace(step.Kind) ? "text" : step.Kind.Trim();
        var text = string.IsNullOrEmpty(step.Text) ? null : step.Text;
        var path = string.IsNullOrWhiteSpace(step.Path) ? null : step.Path.Trim();
        var mimeType = string.IsNullOrWhiteSpace(step.MimeType) ? null : step.MimeType.Trim();

        return kind switch
        {
            "text" when !string.IsNullOrEmpty(text) => new AppServerControlTerminalReplayStep
            {
                Kind = "text",
                Text = text,
                UseBracketedPaste = step.UseBracketedPaste
            },
            "image" when !string.IsNullOrWhiteSpace(path) => new AppServerControlTerminalReplayStep
            {
                Kind = "image",
                Path = path,
                MimeType = mimeType,
                UseBracketedPaste = step.UseBracketedPaste
            },
            "filePath" when !string.IsNullOrWhiteSpace(path) => new AppServerControlTerminalReplayStep
            {
                Kind = "filePath",
                Path = path,
                UseBracketedPaste = step.UseBracketedPaste
            },
            "textFile" when !string.IsNullOrWhiteSpace(path) => new AppServerControlTerminalReplayStep
            {
                Kind = "textFile",
                Path = path,
                UseBracketedPaste = step.UseBracketedPaste
            },
            _ => null
        };
    }

    private sealed record PendingQueueDispatch(
        string SessionId,
        string Kind,
        string? Prompt,
        AppServerControlTurnRequest? Turn);

    private bool TryNormalizePromptSubmission(
        string? sessionId,
        AppServerControlTurnRequest? turn,
        out string trimmedSessionId,
        out AppServerControlTurnRequest normalizedTurn)
    {
        trimmedSessionId = string.Empty;
        normalizedTurn = new AppServerControlTurnRequest();
        if (string.IsNullOrWhiteSpace(sessionId) || turn is null)
        {
            return false;
        }

        var normalized = NormalizeTurn(turn);
        if (normalized is null)
        {
            return false;
        }

        trimmedSessionId = sessionId.Trim();
        if (!_runtime.SessionExists(trimmedSessionId))
        {
            return false;
        }

        normalizedTurn = normalized;
        return true;
    }

    private bool TryNormalizeActionSubmission(
        string? sessionId,
        ManagerBarButton? action,
        out string trimmedSessionId,
        out ManagerBarButton normalizedAction)
    {
        trimmedSessionId = string.Empty;
        normalizedAction = new ManagerBarButton();
        if (string.IsNullOrWhiteSpace(sessionId) || action is null)
        {
            return false;
        }

        trimmedSessionId = sessionId.Trim();
        if (!_runtime.SessionExists(trimmedSessionId))
        {
            return false;
        }

        normalizedAction = action.Normalize();
        return true;
    }

    private ManagerBarQueueEntryDto EnqueuePromptLocked(
        string sessionId,
        AppServerControlTurnRequest normalizedTurn,
        string phase = QueuePhase.PendingCooldown,
        DateTimeOffset? nextRunAt = null)
    {
        var entry = new ManagerBarQueueEntryDto
        {
            QueueId = $"prompt-{Guid.NewGuid():N}",
            SessionId = sessionId,
            Kind = PromptQueueKind,
            Action = null,
            Turn = normalizedTurn,
            Phase = phase,
            NextPromptIndex = 0,
            CompletedCycles = 0,
            NextRunAt = nextRunAt,
            IgnoreHeatUntil = null,
            AwaitingHeatRise = false
        };

        _entries.Add(CloneEntry(entry));
        PersistLocked();
        return entry;
    }

    private bool CanDispatchImmediatelyLocked(string sessionId)
    {
        if (_entries.Count > 0 || _activeImmediateDispatchSessions.Count > 0)
        {
            return false;
        }

        if (_runtime.UsesTurnQueue(sessionId))
        {
            return _runtime.IsTurnQueueReady(sessionId);
        }

        return IsTerminalCooldownReady(_runtime.GetHeatSnapshot(sessionId), _timeProvider.GetUtcNow());
    }

    public async ValueTask DisposeAsync()
    {
        _shutdownCts.Cancel();
        if (_processingTask is not null)
        {
            try
            {
                await _processingTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        _shutdownCts.Dispose();
    }

    private static class QueuePhase
    {
        public const string PendingImmediate = "pendingImmediate";
        public const string PendingCooldown = "pendingCooldown";
        public const string ChainCooldown = "chainCooldown";
        public const string PendingInterval = "pendingInterval";
        public const string PendingSchedule = "pendingSchedule";
    }
}

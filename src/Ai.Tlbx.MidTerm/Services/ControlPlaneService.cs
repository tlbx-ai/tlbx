using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Models.ControlPlane;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services;

public sealed class ControlPlaneService : IDisposable
{
    internal const int MaxWorkItems = 300;
    internal const int MaxSessionStatuses = 100;
    internal const int MaxCheckpoints = 500;
    internal const int MaxEvents = 500;
    private const int MaxTitleLength = 512;
    private const int MaxSummaryLength = 4096;
    private const int MaxDetailsLength = 8192;
    private static readonly TimeSpan SaveDebounceDelay = TimeSpan.FromMilliseconds(200);

    private readonly string _path;
    private readonly Lock _lock = new();
    private readonly Timer _saveTimer;
    private ControlPlaneDocument _document = new();
    private bool _savePending;
    private bool _disposed;

    public ControlPlaneService(SettingsService settingsService)
    {
        _path = Path.Combine(settingsService.SettingsDirectory, "control-plane.json");
        _saveTimer = new Timer(_ => FlushPendingSave(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Load();
    }

    public ControlPlaneWorkItem CreateWorkItem(CreateControlPlaneWorkItemRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);

        var dedupeKey = Optional(request.DedupeKey, 256);
        lock (_lock)
        {
            if (dedupeKey is not null)
            {
                var existing = _document.WorkItems.FirstOrDefault(item =>
                    string.Equals(item.DedupeKey, dedupeKey, StringComparison.Ordinal));
                if (existing is not null)
                {
                    return Clone(existing);
                }
            }

            var now = DateTimeOffset.UtcNow;
            var item = new ControlPlaneWorkItem
            {
                Id = Guid.NewGuid().ToString("N"),
                Kind = Required(request.Kind ?? "todo", 64, nameof(request.Kind)),
                State = WorkItemState(request.State ?? ControlPlaneWorkItemStates.Open),
                Priority = Priority(request.Priority ?? ControlPlanePriorities.Normal),
                Title = Required(request.Title, MaxTitleLength, nameof(request.Title)),
                Summary = Optional(request.Summary, MaxSummaryLength),
                NextAction = Optional(request.NextAction, MaxSummaryLength),
                Project = Optional(request.Project, 256),
                RepositoryPath = Optional(request.RepositoryPath, 4096),
                SessionId = Optional(request.SessionId, 128),
                Url = Optional(request.Url, 4096),
                Source = Required(request.Source ?? "agent", 128, nameof(request.Source)),
                DedupeKey = dedupeKey,
                CreatedAt = now,
                UpdatedAt = now,
                Revision = 1
            };
            _document.WorkItems.Add(item);
            AddEventLocked(
                ControlPlaneEventTypes.WorkItemCreated,
                item.Id,
                item.SessionId,
                item.State,
                item.Priority,
                item.Title,
                item.Source);
            PruneLocked();
            ScheduleSaveLocked();
            return Clone(item);
        }
    }

    public ControlPlaneWorkItem? UpdateWorkItem(string id, UpdateControlPlaneWorkItemRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalizedId = Required(id, 64, nameof(id));

        lock (_lock)
        {
            var item = _document.WorkItems.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, normalizedId, StringComparison.Ordinal));
            if (item is null)
            {
                return null;
            }

            if (request.Kind is not null) item.Kind = Required(request.Kind, 64, nameof(request.Kind));
            if (request.State is not null) item.State = WorkItemState(request.State);
            if (request.Priority is not null) item.Priority = Priority(request.Priority);
            if (request.Title is not null) item.Title = Required(request.Title, MaxTitleLength, nameof(request.Title));
            if (request.Summary is not null) item.Summary = Optional(request.Summary, MaxSummaryLength);
            if (request.NextAction is not null) item.NextAction = Optional(request.NextAction, MaxSummaryLength);
            if (request.Project is not null) item.Project = Optional(request.Project, 256);
            if (request.RepositoryPath is not null) item.RepositoryPath = Optional(request.RepositoryPath, 4096);
            if (request.SessionId is not null) item.SessionId = Optional(request.SessionId, 128);
            if (request.Url is not null) item.Url = Optional(request.Url, 4096);
            if (request.Source is not null) item.Source = Required(request.Source, 128, nameof(request.Source));
            item.UpdatedAt = DateTimeOffset.UtcNow;
            item.Revision++;
            AddEventLocked(
                ControlPlaneEventTypes.WorkItemUpdated,
                item.Id,
                item.SessionId,
                item.State,
                item.Priority,
                item.Title,
                item.Source);
            PruneLocked();
            ScheduleSaveLocked();
            return Clone(item);
        }
    }

    public ControlPlaneWorkItemListResponse GetWorkItems(
        string? state,
        string? kind,
        string? sessionId,
        string? project,
        int limit)
    {
        var normalizedState = Optional(state, 32);
        if (normalizedState is not null) normalizedState = WorkItemState(normalizedState);
        var normalizedKind = Optional(kind, 64);
        var normalizedSessionId = Optional(sessionId, 128);
        var normalizedProject = Optional(project, 256);
        var boundedLimit = Math.Clamp(limit, 1, MaxWorkItems);

        lock (_lock)
        {
            var query = _document.WorkItems.AsEnumerable();
            if (normalizedState is not null)
            {
                query = query.Where(item => string.Equals(item.State, normalizedState, StringComparison.Ordinal));
            }
            if (normalizedKind is not null)
            {
                query = query.Where(item => string.Equals(item.Kind, normalizedKind, StringComparison.Ordinal));
            }
            if (normalizedSessionId is not null)
            {
                query = query.Where(item => string.Equals(item.SessionId, normalizedSessionId, StringComparison.Ordinal));
            }
            if (normalizedProject is not null)
            {
                query = query.Where(item => string.Equals(item.Project, normalizedProject, StringComparison.Ordinal));
            }

            var matching = query
                .OrderBy(static item => WorkItemStateOrder(item.State))
                .ThenBy(static item => PriorityOrder(item.Priority))
                .ThenByDescending(static item => item.UpdatedAt)
                .ToList();
            return new ControlPlaneWorkItemListResponse
            {
                TotalCount = matching.Count,
                Items = matching.Take(boundedLimit).Select(Clone).ToList()
            };
        }
    }

    public bool RemoveWorkItem(string id)
    {
        var normalizedId = Required(id, 64, nameof(id));
        lock (_lock)
        {
            var item = _document.WorkItems.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, normalizedId, StringComparison.Ordinal));
            if (item is null) return false;
            _document.WorkItems.Remove(item);
            AddEventLocked(
                ControlPlaneEventTypes.WorkItemDeleted,
                item.Id,
                item.SessionId,
                item.State,
                item.Priority,
                item.Title,
                item.Source);
            PruneLocked();
            ScheduleSaveLocked();
            return true;
        }
    }

    public ControlPlaneSessionStatus PublishSessionStatus(
        string sessionId,
        PublishControlPlaneSessionStatusRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var normalizedSessionId = Required(sessionId, 128, nameof(sessionId));
        var now = DateTimeOffset.UtcNow;

        lock (_lock)
        {
            var status = _document.SessionStatuses.FirstOrDefault(candidate =>
                string.Equals(candidate.SessionId, normalizedSessionId, StringComparison.Ordinal));
            if (status is null)
            {
                status = new ControlPlaneSessionStatus
                {
                    SessionId = normalizedSessionId,
                    Revision = 0
                };
                _document.SessionStatuses.Add(status);
            }

            status.State = SessionState(request.State ?? ControlPlaneSessionStates.Working);
            status.Summary = Required(request.Summary, MaxSummaryLength, nameof(request.Summary));
            status.CurrentTask = Optional(request.CurrentTask, MaxSummaryLength);
            status.NextAction = Optional(request.NextAction, MaxSummaryLength);
            status.Project = Optional(request.Project, 256);
            status.RepositoryPath = Optional(request.RepositoryPath, 4096);
            status.Source = Required(request.Source ?? "agent", 128, nameof(request.Source));
            status.UpdatedAt = now;
            status.Revision++;
            AddEventLocked(
                ControlPlaneEventTypes.SessionStatusPublished,
                status.SessionId,
                status.SessionId,
                status.State,
                null,
                status.Summary,
                status.Source);
            PruneLocked();
            ScheduleSaveLocked();
            return Clone(status);
        }
    }

    public ControlPlaneSessionStatusListResponse GetSessionStatuses(string? sessionId)
    {
        var normalizedSessionId = Optional(sessionId, 128);
        lock (_lock)
        {
            var statuses = _document.SessionStatuses.AsEnumerable();
            if (normalizedSessionId is not null)
            {
                statuses = statuses.Where(status =>
                    string.Equals(status.SessionId, normalizedSessionId, StringComparison.Ordinal));
            }

            return new ControlPlaneSessionStatusListResponse
            {
                Statuses = statuses.OrderByDescending(static status => status.UpdatedAt).Select(Clone).ToList()
            };
        }
    }

    public bool ClearSessionStatus(string sessionId)
    {
        var normalizedSessionId = Required(sessionId, 128, nameof(sessionId));
        lock (_lock)
        {
            var status = _document.SessionStatuses.FirstOrDefault(candidate =>
                string.Equals(candidate.SessionId, normalizedSessionId, StringComparison.Ordinal));
            if (status is null) return false;
            _document.SessionStatuses.Remove(status);
            AddEventLocked(
                ControlPlaneEventTypes.SessionStatusCleared,
                status.SessionId,
                status.SessionId,
                status.State,
                null,
                status.Summary,
                status.Source);
            PruneLocked();
            ScheduleSaveLocked();
            return true;
        }
    }

    public ControlPlaneCheckpoint CreateCheckpoint(CreateControlPlaneCheckpointRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var checkpoint = new ControlPlaneCheckpoint
        {
            Id = Guid.NewGuid().ToString("N"),
            SessionId = Required(request.SessionId, 128, nameof(request.SessionId)),
            Kind = Required(request.Kind ?? "progress", 64, nameof(request.Kind)),
            Summary = Required(request.Summary, MaxSummaryLength, nameof(request.Summary)),
            Details = Optional(request.Details, MaxDetailsLength),
            Project = Optional(request.Project, 256),
            RepositoryPath = Optional(request.RepositoryPath, 4096),
            Source = Required(request.Source ?? "agent", 128, nameof(request.Source)),
            CreatedAt = DateTimeOffset.UtcNow
        };

        lock (_lock)
        {
            _document.Checkpoints.Add(checkpoint);
            AddEventLocked(
                ControlPlaneEventTypes.CheckpointCreated,
                checkpoint.Id,
                checkpoint.SessionId,
                null,
                null,
                checkpoint.Summary,
                checkpoint.Source);
            PruneLocked();
            ScheduleSaveLocked();
            return Clone(checkpoint);
        }
    }

    public ControlPlaneCheckpointListResponse GetCheckpoints(string? sessionId, string? kind, int limit)
    {
        var normalizedSessionId = Optional(sessionId, 128);
        var normalizedKind = Optional(kind, 64);
        var boundedLimit = Math.Clamp(limit, 1, MaxCheckpoints);
        lock (_lock)
        {
            var query = _document.Checkpoints.AsEnumerable();
            if (normalizedSessionId is not null)
            {
                query = query.Where(item => string.Equals(item.SessionId, normalizedSessionId, StringComparison.Ordinal));
            }
            if (normalizedKind is not null)
            {
                query = query.Where(item => string.Equals(item.Kind, normalizedKind, StringComparison.Ordinal));
            }

            var matching = query.OrderByDescending(static checkpoint => checkpoint.CreatedAt).ToList();
            return new ControlPlaneCheckpointListResponse
            {
                TotalCount = matching.Count,
                Checkpoints = matching.Take(boundedLimit).Select(Clone).ToList()
            };
        }
    }

    public ControlPlaneEventListResponse GetEvents(long afterSequence, int limit)
    {
        var boundedAfter = Math.Max(0, afterSequence);
        var boundedLimit = Math.Clamp(limit, 1, MaxEvents);
        lock (_lock)
        {
            return new ControlPlaneEventListResponse
            {
                LatestSequence = Math.Max(0, _document.NextEventSequence - 1),
                Events = _document.Events
                    .Where(item => item.Sequence > boundedAfter)
                    .OrderBy(static item => item.Sequence)
                    .Take(boundedLimit)
                    .Select(Clone)
                    .ToList()
            };
        }
    }

    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_path)) return;
            try
            {
                _document = JsonSerializer.Deserialize(
                    File.ReadAllText(_path),
                    ControlPlaneJsonContext.Default.ControlPlaneDocument) ?? new ControlPlaneDocument();
                _document = Clone(_document);
                PruneLocked();
            }
            catch (Exception ex)
            {
                Log.Warn(() => $"Failed to load control plane: {ex.Message}");
                _document = new ControlPlaneDocument();
            }
        }
    }

    private void PruneLocked()
    {
        _document.WorkItems = _document.WorkItems
            .OrderByDescending(static item => item.UpdatedAt)
            .Take(MaxWorkItems)
            .ToList();
        _document.SessionStatuses = _document.SessionStatuses
            .GroupBy(static status => status.SessionId, StringComparer.Ordinal)
            .Select(static group => group.OrderByDescending(status => status.UpdatedAt).First())
            .OrderByDescending(static status => status.UpdatedAt)
            .Take(MaxSessionStatuses)
            .ToList();
        _document.Checkpoints = _document.Checkpoints
            .OrderByDescending(static checkpoint => checkpoint.CreatedAt)
            .Take(MaxCheckpoints)
            .ToList();
        _document.Events = _document.Events
            .OrderByDescending(static item => item.Sequence)
            .Take(MaxEvents)
            .OrderBy(static item => item.Sequence)
            .ToList();
        var highestSequence = _document.Events.Count == 0
            ? 0
            : _document.Events.Max(static item => item.Sequence);
        _document.NextEventSequence = Math.Max(Math.Max(1, _document.NextEventSequence), highestSequence + 1);
    }

    private void AddEventLocked(
        string type,
        string entityId,
        string? sessionId,
        string? state,
        string? priority,
        string summary,
        string source)
    {
        _document.Events.Add(new ControlPlaneEvent
        {
            Sequence = _document.NextEventSequence++,
            Type = type,
            EntityId = entityId,
            SessionId = sessionId,
            State = state,
            Priority = priority,
            Summary = Optional(summary, MaxSummaryLength) ?? type,
            Source = Optional(source, 128) ?? "agent",
            CreatedAt = DateTimeOffset.UtcNow
        });
    }

    private void ScheduleSaveLocked()
    {
        _savePending = true;
        _saveTimer.Change(SaveDebounceDelay, Timeout.InfiniteTimeSpan);
    }

    private void FlushPendingSave()
    {
        ControlPlaneDocument? snapshot = null;
        lock (_lock)
        {
            if (!_savePending) return;
            _savePending = false;
            snapshot = Clone(_document);
        }

        Persist(snapshot);
    }

    private void Persist(ControlPlaneDocument snapshot)
    {
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            temporaryPath = $"{_path}.{Guid.NewGuid():N}.tmp";
            File.WriteAllText(
                temporaryPath,
                JsonSerializer.Serialize(snapshot, ControlPlaneJsonContext.Default.ControlPlaneDocument));
            File.Move(temporaryPath, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"Failed to save control plane: {ex.Message}");
            if (temporaryPath is not null)
            {
                try { File.Delete(temporaryPath); } catch { }
            }
        }
    }

    private static string Required(string? value, int maxLength, string paramName)
    {
        return Optional(value, maxLength)
            ?? throw new ArgumentException("A non-empty value is required.", paramName);
    }

    private static string? Optional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string WorkItemState(string value)
    {
        var normalized = Required(value, 32, nameof(value));
        return ControlPlaneWorkItemStates.All.Contains(normalized)
            ? normalized
            : throw new ArgumentException($"Unknown work item state '{normalized}'.", nameof(value));
    }

    private static string Priority(string value)
    {
        var normalized = Required(value, 32, nameof(value));
        return ControlPlanePriorities.All.Contains(normalized)
            ? normalized
            : throw new ArgumentException($"Unknown priority '{normalized}'.", nameof(value));
    }

    private static string SessionState(string value)
    {
        var normalized = Required(value, 32, nameof(value));
        return ControlPlaneSessionStates.All.Contains(normalized)
            ? normalized
            : throw new ArgumentException($"Unknown published session state '{normalized}'.", nameof(value));
    }

    private static int WorkItemStateOrder(string state) => state switch
    {
        ControlPlaneWorkItemStates.Active => 0,
        ControlPlaneWorkItemStates.Blocked => 1,
        ControlPlaneWorkItemStates.Waiting => 2,
        ControlPlaneWorkItemStates.Open => 3,
        ControlPlaneWorkItemStates.Done => 4,
        _ => 5
    };

    private static int PriorityOrder(string priority) => priority switch
    {
        ControlPlanePriorities.Urgent => 0,
        ControlPlanePriorities.High => 1,
        ControlPlanePriorities.Normal => 2,
        _ => 3
    };

    private static ControlPlaneDocument Clone(ControlPlaneDocument document) => new()
    {
        WorkItems = (document.WorkItems ?? []).Select(Clone).ToList(),
        SessionStatuses = (document.SessionStatuses ?? []).Select(Clone).ToList(),
        Checkpoints = (document.Checkpoints ?? []).Select(Clone).ToList(),
        NextEventSequence = Math.Max(1, document.NextEventSequence),
        Events = (document.Events ?? []).Select(Clone).ToList()
    };

    private static ControlPlaneWorkItem Clone(ControlPlaneWorkItem item) => new()
    {
        Id = Optional(item.Id, 64) ?? Guid.NewGuid().ToString("N"),
        Kind = Optional(item.Kind, 64) ?? "todo",
        State = ControlPlaneWorkItemStates.All.Contains(item.State) ? item.State : ControlPlaneWorkItemStates.Open,
        Priority = ControlPlanePriorities.All.Contains(item.Priority) ? item.Priority : ControlPlanePriorities.Normal,
        Title = Optional(item.Title, MaxTitleLength) ?? "Untitled",
        Summary = Optional(item.Summary, MaxSummaryLength),
        NextAction = Optional(item.NextAction, MaxSummaryLength),
        Project = Optional(item.Project, 256),
        RepositoryPath = Optional(item.RepositoryPath, 4096),
        SessionId = Optional(item.SessionId, 128),
        Url = Optional(item.Url, 4096),
        Source = Optional(item.Source, 128) ?? "agent",
        DedupeKey = Optional(item.DedupeKey, 256),
        CreatedAt = item.CreatedAt,
        UpdatedAt = item.UpdatedAt,
        Revision = Math.Max(1, item.Revision)
    };

    private static ControlPlaneSessionStatus Clone(ControlPlaneSessionStatus status) => new()
    {
        SessionId = Optional(status.SessionId, 128) ?? "unknown",
        State = ControlPlaneSessionStates.All.Contains(status.State) ? status.State : ControlPlaneSessionStates.Working,
        Summary = Optional(status.Summary, MaxSummaryLength) ?? "Published without summary",
        CurrentTask = Optional(status.CurrentTask, MaxSummaryLength),
        NextAction = Optional(status.NextAction, MaxSummaryLength),
        Project = Optional(status.Project, 256),
        RepositoryPath = Optional(status.RepositoryPath, 4096),
        Source = Optional(status.Source, 128) ?? "agent",
        UpdatedAt = status.UpdatedAt,
        Revision = Math.Max(1, status.Revision)
    };

    private static ControlPlaneCheckpoint Clone(ControlPlaneCheckpoint checkpoint) => new()
    {
        Id = Optional(checkpoint.Id, 64) ?? Guid.NewGuid().ToString("N"),
        SessionId = Optional(checkpoint.SessionId, 128) ?? "unknown",
        Kind = Optional(checkpoint.Kind, 64) ?? "progress",
        Summary = Optional(checkpoint.Summary, MaxSummaryLength) ?? "Checkpoint",
        Details = Optional(checkpoint.Details, MaxDetailsLength),
        Project = Optional(checkpoint.Project, 256),
        RepositoryPath = Optional(checkpoint.RepositoryPath, 4096),
        Source = Optional(checkpoint.Source, 128) ?? "agent",
        CreatedAt = checkpoint.CreatedAt
    };

    private static ControlPlaneEvent Clone(ControlPlaneEvent item) => new()
    {
        Sequence = Math.Max(0, item.Sequence),
        Type = Optional(item.Type, 64) ?? "unknown",
        EntityId = Optional(item.EntityId, 128) ?? "unknown",
        SessionId = Optional(item.SessionId, 128),
        State = Optional(item.State, 32),
        Priority = Optional(item.Priority, 32),
        Summary = Optional(item.Summary, MaxSummaryLength) ?? "Event",
        Source = Optional(item.Source, 128) ?? "agent",
        CreatedAt = item.CreatedAt
    };

    public void Dispose()
    {
        ControlPlaneDocument? snapshot = null;
        lock (_lock)
        {
            if (_disposed) return;
            _disposed = true;
            if (_savePending)
            {
                _savePending = false;
                snapshot = Clone(_document);
            }
        }

        _saveTimer.Dispose();
        if (snapshot is not null) Persist(snapshot);
    }
}

using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Models.InputHistory;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services;

public sealed class InputHistoryService : IDisposable
{
    internal const int MaxEntries = 500;
    internal const int MaxEntryTextCharacters = 64 * 1024;
    internal const int MaxTotalTextCharacters = 4 * 1024 * 1024;
    internal const int MaxAttachmentsPerEntry = 32;
    internal const int MaxReplayStepsPerEntry = 64;
    private static readonly TimeSpan SaveDebounceDelay = TimeSpan.FromMilliseconds(200);

    private readonly string _historyPath;
    private readonly Lock _lock = new();
    private readonly Timer _saveTimer;
    private InputHistoryDocument _history = new();
    private bool _savePending;
    private bool _disposed;

    public InputHistoryService(SettingsService settingsService)
    {
        _historyPath = Path.Combine(settingsService.SettingsDirectory, "input-history.json");
        _saveTimer = new Timer(_ => FlushPendingSave(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Load();
    }

    public InputHistoryEntry RecordPrompt(
        string sessionId,
        string? sessionName,
        string? workingDirectory,
        string source,
        string surface,
        AppServerControlTurnRequest turn)
    {
        ArgumentNullException.ThrowIfNull(turn);

        var normalizedTurn = CloneTurn(turn);
        if (normalizedTurn is null)
        {
            throw new ArgumentException("Prompt history requires text, attachments, or terminal replay steps.", nameof(turn));
        }

        return Add(new InputHistoryEntry
        {
            SessionId = sessionId,
            SessionName = sessionName,
            WorkingDirectory = workingDirectory,
            Kind = InputHistoryKinds.Prompt,
            Source = NormalizeSource(source),
            Surface = NormalizeSurface(surface),
            Text = normalizedTurn.Text,
            Submit = true,
            Turn = normalizedTurn
        });
    }

    public InputHistoryEntry RecordPaste(
        string sessionId,
        string? sessionName,
        string? workingDirectory,
        string text,
        bool bracketedPaste,
        bool isFilePath,
        string source = InputHistorySources.TerminalPaste)
    {
        if (string.IsNullOrEmpty(text))
        {
            throw new ArgumentException("Paste history requires text.", nameof(text));
        }

        return Add(new InputHistoryEntry
        {
            SessionId = sessionId,
            SessionName = sessionName,
            WorkingDirectory = workingDirectory,
            Kind = InputHistoryKinds.TextPaste,
            Source = NormalizeSource(source),
            Surface = InputHistorySurfaces.Terminal,
            Text = text,
            BracketedPaste = bracketedPaste,
            IsFilePath = isFilePath,
            Submit = false
        });
    }

    public InputHistoryEntry RecordUpload(
        string sessionId,
        string? sessionName,
        string? workingDirectory,
        string path,
        string? displayName,
        string? mimeType,
        long sizeBytes,
        string source,
        string surface = InputHistorySurfaces.Terminal)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Upload history requires a path.", nameof(path));
        }

        var normalizedMimeType = NormalizeOptional(mimeType, 128);
        var isImage = normalizedMimeType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
        return Add(new InputHistoryEntry
        {
            SessionId = sessionId,
            SessionName = sessionName,
            WorkingDirectory = workingDirectory,
            Kind = isImage ? InputHistoryKinds.ImagePaste : InputHistoryKinds.FileUpload,
            Source = NormalizeSource(source),
            Surface = NormalizeSurface(surface),
            Path = NormalizeRequired(path, 4096, nameof(path)),
            DisplayName = NormalizeOptional(displayName, 512),
            MimeType = normalizedMimeType,
            SizeBytes = Math.Max(0, sizeBytes),
            IsFilePath = true,
            Submit = false
        });
    }

    public InputHistoryListResponse GetEntries(string sessionId, string? kind, int limit)
    {
        var boundedLimit = Math.Clamp(limit, 1, MaxEntries);
        var normalizedSessionId = NormalizeRequired(sessionId, 128, nameof(sessionId));
        var normalizedKind = NormalizeOptional(kind, 32);

        lock (_lock)
        {
            var query = _history.Entries.Where(entry =>
                string.Equals(entry.SessionId, normalizedSessionId, StringComparison.Ordinal));

            if (normalizedKind is not null)
            {
                query = query.Where(entry => string.Equals(entry.Kind, normalizedKind, StringComparison.Ordinal));
            }

            var matching = query.OrderByDescending(static entry => entry.CreatedAt).ToList();
            return new InputHistoryListResponse
            {
                TotalCount = matching.Count,
                Entries = matching.Take(boundedLimit).Select(CloneEntry).ToList()
            };
        }
    }

    public InputHistoryEntry? GetEntry(string id)
    {
        var normalizedId = NormalizeOptional(id, 64);
        if (normalizedId is null)
        {
            return null;
        }

        lock (_lock)
        {
            var entry = _history.Entries.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, normalizedId, StringComparison.Ordinal));
            return entry is null ? null : CloneEntry(entry);
        }
    }

    public bool Remove(string id)
    {
        var normalizedId = NormalizeOptional(id, 64);
        if (normalizedId is null)
        {
            return false;
        }

        bool removed;
        lock (_lock)
        {
            removed = _history.Entries.RemoveAll(candidate =>
                string.Equals(candidate.Id, normalizedId, StringComparison.Ordinal)) > 0;
        }

        if (removed)
        {
            ScheduleSave();
        }

        return removed;
    }

    public int ClearSession(string sessionId)
    {
        var normalizedSessionId = NormalizeOptional(sessionId, 128);
        if (normalizedSessionId is null)
        {
            return 0;
        }

        int removed;
        lock (_lock)
        {
            removed = _history.Entries.RemoveAll(candidate =>
                string.Equals(candidate.SessionId, normalizedSessionId, StringComparison.Ordinal));
        }

        if (removed > 0)
        {
            ScheduleSave();
        }

        return removed;
    }

    private InputHistoryEntry Add(InputHistoryEntry entry)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        entry.Id = Guid.NewGuid().ToString("N");
        entry.SessionId = NormalizeRequired(entry.SessionId, 128, nameof(entry.SessionId));
        entry.SessionName = NormalizeOptional(entry.SessionName, 512);
        entry.WorkingDirectory = NormalizeOptional(entry.WorkingDirectory, 4096);
        entry.CreatedAt = DateTimeOffset.UtcNow;
        entry.Text = BoundText(entry.Text);

        lock (_lock)
        {
            _history.Entries.Add(CloneEntry(entry));
            PruneLocked();
        }

        ScheduleSave();
        return CloneEntry(entry);
    }

    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_historyPath))
            {
                return;
            }

            try
            {
                var json = File.ReadAllText(_historyPath);
                _history = JsonSerializer.Deserialize(json, InputHistoryJsonContext.Default.InputHistoryDocument)
                    ?? new InputHistoryDocument();
                _history.Entries = _history.Entries.Select(CloneEntry).ToList();
                PruneLocked();
            }
            catch (Exception ex)
            {
                Log.Warn(() => $"Failed to load input history: {ex.Message}");
                _history = new InputHistoryDocument();
            }
        }
    }

    private void ScheduleSave()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _savePending = true;
            _saveTimer.Change(SaveDebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void FlushPendingSave()
    {
        InputHistoryDocument? snapshot = null;
        lock (_lock)
        {
            if (!_savePending)
            {
                return;
            }

            _savePending = false;
            snapshot = CloneDocument(_history);
        }

        PersistSnapshot(snapshot);
    }

    private void PersistSnapshot(InputHistoryDocument snapshot)
    {
        string? temporaryPath = null;
        try
        {
            var directory = Path.GetDirectoryName(_historyPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            temporaryPath = $"{_historyPath}.{Guid.NewGuid():N}.tmp";
            var json = JsonSerializer.Serialize(snapshot, InputHistoryJsonContext.Default.InputHistoryDocument);
            File.WriteAllText(temporaryPath, json);
            File.Move(temporaryPath, _historyPath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"Failed to save input history: {ex.Message}");
            if (temporaryPath is not null)
            {
                try
                {
                    File.Delete(temporaryPath);
                }
                catch
                {
                }
            }
        }
    }

    private void PruneLocked()
    {
        _history.Entries = _history.Entries
            .OrderByDescending(static entry => entry.CreatedAt)
            .Take(MaxEntries)
            .ToList();

        var totalTextCharacters = 0;
        var kept = new List<InputHistoryEntry>(_history.Entries.Count);
        foreach (var entry in _history.Entries)
        {
            var textCharacters = GetStoredTextCharacterCount(entry);
            if (kept.Count > 0 && totalTextCharacters + textCharacters > MaxTotalTextCharacters)
            {
                continue;
            }

            totalTextCharacters += textCharacters;
            kept.Add(entry);
        }

        _history.Entries = kept;
    }

    private static int GetStoredTextCharacterCount(InputHistoryEntry entry)
    {
        var total = entry.Text?.Length ?? 0;
        if (entry.Turn?.Text is { } turnText && !string.Equals(turnText, entry.Text, StringComparison.Ordinal))
        {
            total += turnText.Length;
        }

        if (entry.Turn?.TerminalReplay is not null)
        {
            total += entry.Turn.TerminalReplay.Sum(static step => step.Text?.Length ?? 0);
        }

        return total;
    }

    private static string NormalizeRequired(string value, int maxLength, string paramName)
    {
        var normalized = NormalizeOptional(value, maxLength);
        return normalized ?? throw new ArgumentException("A non-empty value is required.", paramName);
    }

    private static string? NormalizeOptional(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength];
    }

    private static string NormalizeSource(string? source)
    {
        return NormalizeOptional(source, 64) ?? InputHistorySources.Upload;
    }

    private static string NormalizeSurface(string? surface)
    {
        return string.Equals(surface, InputHistorySurfaces.AgentControl, StringComparison.Ordinal)
            ? InputHistorySurfaces.AgentControl
            : InputHistorySurfaces.Terminal;
    }

    private static string? BoundText(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return null;
        }

        return text.Length <= MaxEntryTextCharacters ? text : text[..MaxEntryTextCharacters];
    }

    private static InputHistoryDocument CloneDocument(InputHistoryDocument document)
    {
        return new InputHistoryDocument
        {
            Entries = document.Entries.Select(CloneEntry).ToList()
        };
    }

    private static InputHistoryEntry CloneEntry(InputHistoryEntry entry)
    {
        return new InputHistoryEntry
        {
            Id = entry.Id,
            SessionId = entry.SessionId,
            SessionName = entry.SessionName,
            WorkingDirectory = entry.WorkingDirectory,
            Kind = entry.Kind,
            Source = entry.Source,
            Surface = entry.Surface,
            CreatedAt = entry.CreatedAt,
            Text = BoundText(entry.Text),
            Path = entry.Path,
            DisplayName = entry.DisplayName,
            MimeType = entry.MimeType,
            SizeBytes = entry.SizeBytes,
            BracketedPaste = entry.BracketedPaste,
            IsFilePath = entry.IsFilePath,
            Submit = entry.Submit,
            Turn = CloneTurn(entry.Turn)
        };
    }

    internal static AppServerControlTurnRequest? CloneTurn(AppServerControlTurnRequest? turn)
    {
        if (turn is null)
        {
            return null;
        }

        var text = BoundText(turn.Text);
        var attachments = turn.Attachments?
            .Where(static attachment => attachment is not null && !string.IsNullOrWhiteSpace(attachment.Path))
            .Take(MaxAttachmentsPerEntry)
            .Select(static attachment => new AppServerControlAttachmentReference
            {
                Kind = string.IsNullOrWhiteSpace(attachment.Kind) ? "file" : attachment.Kind.Trim(),
                Path = attachment.Path.Trim(),
                MimeType = NormalizeOptional(attachment.MimeType, 128),
                DisplayName = NormalizeOptional(attachment.DisplayName, 512)
            })
            .ToList() ?? [];
        var remainingTextCharacters = MaxEntryTextCharacters - (text?.Length ?? 0);
        var terminalReplay = new List<AppServerControlTerminalReplayStep>();
        if (turn.TerminalReplay is not null)
        {
            foreach (var step in turn.TerminalReplay.Where(static step => step is not null).Take(MaxReplayStepsPerEntry))
            {
                var stepText = BoundText(step.Text, remainingTextCharacters);
                remainingTextCharacters -= stepText?.Length ?? 0;
                var normalizedStep = new AppServerControlTerminalReplayStep
                {
                    Kind = NormalizeOptional(step.Kind, 32) ?? "text",
                    Text = stepText,
                    Path = NormalizeOptional(step.Path, 4096),
                    MimeType = NormalizeOptional(step.MimeType, 128),
                    UseBracketedPaste = step.UseBracketedPaste
                };
                if (normalizedStep.Text is not null || normalizedStep.Path is not null)
                {
                    terminalReplay.Add(normalizedStep);
                }
            }
        }

        if (text is null && attachments.Count == 0 && terminalReplay.Count == 0)
        {
            return null;
        }

        return new AppServerControlTurnRequest
        {
            Text = text,
            Model = NormalizeOptional(turn.Model, 256),
            Effort = NormalizeOptional(turn.Effort, 64),
            PlanMode = NormalizeOptional(turn.PlanMode, 64),
            PermissionMode = NormalizeOptional(turn.PermissionMode, 64),
            Attachments = attachments,
            TerminalReplay = terminalReplay
        };
    }

    private static string? BoundText(string? text, int maxLength)
    {
        if (string.IsNullOrEmpty(text) || maxLength <= 0)
        {
            return null;
        }

        return text.Length <= maxLength ? text : text[..maxLength];
    }

    public void Dispose()
    {
        InputHistoryDocument? snapshot = null;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            if (_savePending)
            {
                _savePending = false;
                snapshot = CloneDocument(_history);
            }
        }

        _saveTimer.Dispose();
        if (snapshot is not null)
        {
            PersistSnapshot(snapshot);
        }
    }
}

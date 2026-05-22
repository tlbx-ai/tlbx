using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Models;
using Ai.Tlbx.MidTerm.Settings;

using Ai.Tlbx.MidTerm.Models.Auth;
using Ai.Tlbx.MidTerm.Models.Certificates;
using Ai.Tlbx.MidTerm.Models.Files;
using Ai.Tlbx.MidTerm.Models.History;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Models.System;
using Ai.Tlbx.MidTerm.Services.Sessions;
namespace Ai.Tlbx.MidTerm.Services;

public sealed class HistoryService : IDisposable
{
    private const int MaxRecentEntries = 50;
    private static readonly TimeSpan SaveDebounceDelay = TimeSpan.FromMilliseconds(250);

    private readonly string _historyPath;
    private readonly Lock _lock = new();
    private readonly Timer _saveTimer;
    private LaunchHistory _history = new();
    private bool _savePending;
    private bool _disposed;

    public HistoryService(SettingsService settingsService)
    {
        _historyPath = Path.Combine(settingsService.SettingsDirectory, "history.json");
        _saveTimer = new Timer(_ => FlushPendingSave(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        Log.Info(() => $"HistoryService: path={_historyPath}");
        Load();
        MigrateStarredOrder();
    }

    private void Load()
    {
        lock (_lock)
        {
            if (!File.Exists(_historyPath))
            {
                _history = new LaunchHistory();
                return;
            }

            try
            {
                var json = File.ReadAllText(_historyPath);
                _history = JsonSerializer.Deserialize(json, HistoryJsonContext.Default.LaunchHistory)
                    ?? new LaunchHistory();
            }
            catch (Exception ex)
            {
                Log.Warn(() => $"Failed to load history: {ex.Message}");
                _history = new LaunchHistory();
            }
        }
    }

    private void ScheduleSave()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_lock)
        {
            _savePending = true;
            _saveTimer.Change(SaveDebounceDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private void FlushPendingSave()
    {
        LaunchHistory? snapshot = null;

        lock (_lock)
        {
            if (!_savePending)
            {
                return;
            }

            _savePending = false;
            snapshot = CloneHistory(_history);
        }

        PersistSnapshot(snapshot);
    }

    private void PersistSnapshot(LaunchHistory snapshot)
    {
        try
        {
            var dir = Path.GetDirectoryName(_historyPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            var json = JsonSerializer.Serialize(snapshot, HistoryJsonContext.Default.LaunchHistory);
            File.WriteAllText(_historyPath, json);
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"Failed to save history: {ex.Message}");
        }
    }

    private static LaunchHistory CloneHistory(LaunchHistory history)
    {
        return new LaunchHistory
        {
            Entries = history.Entries.Select(CloneEntry).ToList()
        };
    }

    private static LaunchEntry CloneEntry(LaunchEntry entry)
    {
        return new LaunchEntry
        {
            Id = entry.Id,
            ShellType = entry.ShellType,
            Executable = entry.Executable,
            CommandLine = entry.CommandLine,
            WorkingDirectory = entry.WorkingDirectory,
            IsStarred = entry.IsStarred,
            Label = entry.Label,
            Notes = entry.Notes,
            LastUsed = entry.LastUsed,
            Order = entry.Order,
            LaunchMode = NormalizeLaunchMode(entry.LaunchMode),
            Profile = NormalizeProfile(entry.Profile),
            LaunchOrigin = NormalizeLaunchOrigin(entry.LaunchOrigin),
            SurfaceType = NormalizeSurfaceType(entry.SurfaceType, entry.LaunchMode, entry.Profile),
            ForegroundProcessName = entry.ForegroundProcessName,
            ForegroundProcessCommandLine = entry.ForegroundProcessCommandLine,
            ForegroundProcessDisplayName = entry.ForegroundProcessDisplayName,
            ForegroundProcessIdentity = entry.ForegroundProcessIdentity
        };
    }

    public string? RecordEntry(
        string shellType,
        string executable,
        string? commandLine,
        string workingDirectory,
        string? label = null,
        string? notes = null,
        string? dedupeKey = null,
        string? launchMode = null,
        string? profile = null,
        string? launchOrigin = null,
        string? surfaceType = null,
        string? foregroundProcessName = null,
        string? foregroundProcessCommandLine = null,
        string? foregroundProcessDisplayName = null,
        string? foregroundProcessIdentity = null)
    {
        var normalizedLaunchMode = NormalizeLaunchMode(launchMode);
        var normalizedProfile = NormalizeProfile(profile);
        var normalizedLaunchOrigin = NormalizeLaunchOrigin(launchOrigin);
        var normalizedSurfaceType = NormalizeSurfaceType(surfaceType, normalizedLaunchMode, normalizedProfile);
        Log.Info(() => $"RecordEntry: shell={shellType}, exe={executable}, cmd={commandLine}, cwd={workingDirectory}, label={label}, dedupeKey={dedupeKey}, launchMode={normalizedLaunchMode}, profile={normalizedProfile}, launchOrigin={normalizedLaunchOrigin}, surfaceType={normalizedSurfaceType}");
        var normalizedNotes = NormalizeNotes(notes);

        if (string.IsNullOrWhiteSpace(executable) || string.IsNullOrWhiteSpace(workingDirectory))
        {
            Log.Info(() => "RecordEntry skipped: empty executable or workingDirectory");
            return null;
        }

        // Strip .exe extension for cleaner display
        var cleanExecutable = executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? executable[..^4]
            : executable;

        // Don't record shell as subprocess (e.g., "pwsh" when running pwsh)
        if (cleanExecutable.Equals(shellType, StringComparison.OrdinalIgnoreCase))
        {
            Log.Info(() => $"RecordEntry skipped: exe matches shell ({cleanExecutable})");
            return null;
        }
        executable = cleanExecutable;

        var id = GenerateId(shellType, executable, commandLine, workingDirectory, dedupeKey);
        Log.Info(() => $"RecordEntry: recording id={id}");

        lock (_lock)
        {
            var existing = _history.Entries.FirstOrDefault(e => e.Id == id);
            if (existing is not null)
            {
                existing.ShellType = shellType;
                existing.Executable = executable;
                existing.CommandLine = commandLine;
                existing.WorkingDirectory = workingDirectory;
                existing.LastUsed = DateTime.UtcNow;
                existing.LaunchMode = normalizedLaunchMode;
                existing.Profile = normalizedProfile;
                existing.LaunchOrigin = normalizedLaunchOrigin;
                existing.SurfaceType = normalizedSurfaceType;
                existing.ForegroundProcessName = foregroundProcessName;
                existing.ForegroundProcessCommandLine = foregroundProcessCommandLine;
                existing.ForegroundProcessDisplayName = foregroundProcessDisplayName;
                existing.ForegroundProcessIdentity = foregroundProcessIdentity;
                if (!string.IsNullOrWhiteSpace(label))
                {
                    existing.Label = label;
                }
                if (notes is not null)
                {
                    existing.Notes = normalizedNotes;
                }
            }
            else
            {
                var entry = new LaunchEntry
                {
                    Id = id,
                    ShellType = shellType,
                    Executable = executable,
                    CommandLine = commandLine,
                    WorkingDirectory = workingDirectory,
                    IsStarred = false,
                    Label = string.IsNullOrWhiteSpace(label) ? null : label,
                    Notes = normalizedNotes,
                    LastUsed = DateTime.UtcNow,
                    LaunchMode = normalizedLaunchMode,
                    Profile = normalizedProfile,
                    LaunchOrigin = normalizedLaunchOrigin,
                    SurfaceType = normalizedSurfaceType,
                    ForegroundProcessName = foregroundProcessName,
                    ForegroundProcessCommandLine = foregroundProcessCommandLine,
                    ForegroundProcessDisplayName = foregroundProcessDisplayName,
                    ForegroundProcessIdentity = foregroundProcessIdentity
                };
                _history.Entries.Add(entry);
            }

            Prune();
        }

        ScheduleSave();

        Log.Verbose(() => $"Recorded history: {executable} in {workingDirectory}");
        return id;
    }

    public List<LaunchEntry> GetEntries()
    {
        lock (_lock)
        {
            var starred = _history.Entries
                .Where(e => e.IsStarred)
                .OrderBy(e => e.Order)
                .ToList();

            var recent = _history.Entries
                .Where(e => !e.IsStarred)
                .OrderByDescending(e => e.LastUsed)
                .ToList();

            return starred.Concat(recent).ToList();
        }
    }

    public LaunchEntry? GetEntry(string id)
    {
        lock (_lock)
        {
            var entry = _history.Entries.FirstOrDefault(e => e.Id == id);
            if (entry is null)
            {
                return null;
            }

            return new LaunchEntry
            {
                Id = entry.Id,
                ShellType = entry.ShellType,
                Executable = entry.Executable,
                CommandLine = entry.CommandLine,
                WorkingDirectory = entry.WorkingDirectory,
                IsStarred = entry.IsStarred,
                Label = entry.Label,
                Notes = entry.Notes,
                LastUsed = entry.LastUsed,
                Order = entry.Order,
                LaunchMode = NormalizeLaunchMode(entry.LaunchMode),
                Profile = NormalizeProfile(entry.Profile),
                LaunchOrigin = NormalizeLaunchOrigin(entry.LaunchOrigin),
                SurfaceType = NormalizeSurfaceType(entry.SurfaceType, entry.LaunchMode, entry.Profile),
                ForegroundProcessName = entry.ForegroundProcessName,
                ForegroundProcessCommandLine = entry.ForegroundProcessCommandLine,
                ForegroundProcessDisplayName = entry.ForegroundProcessDisplayName,
                ForegroundProcessIdentity = entry.ForegroundProcessIdentity
            };
        }
    }

    public bool ToggleStar(string id)
    {
        lock (_lock)
        {
            var entry = _history.Entries.FirstOrDefault(e => e.Id == id);
            if (entry is null)
            {
                return false;
            }

            entry.IsStarred = !entry.IsStarred;
            if (entry.IsStarred)
            {
                entry.Order = NextStarredOrder();
            }
        }

        ScheduleSave();
        return true;
    }

    public bool SetStarred(string id, bool starred)
    {
        lock (_lock)
        {
            var entry = _history.Entries.FirstOrDefault(e => e.Id == id);
            if (entry is null)
            {
                return false;
            }

            entry.IsStarred = starred;
            if (starred)
            {
                entry.Order = NextStarredOrder();
            }
        }

        ScheduleSave();
        return true;
    }

    public bool SetLabel(string id, string? label)
    {
        lock (_lock)
        {
            var entry = _history.Entries.FirstOrDefault(e => e.Id == id);
            if (entry is null)
            {
                return false;
            }

            entry.Label = string.IsNullOrWhiteSpace(label) ? null : label;
        }

        ScheduleSave();
        return true;
    }

    public bool SetNotes(string id, string? notes)
    {
        lock (_lock)
        {
            var entry = _history.Entries.FirstOrDefault(e => e.Id == id);
            if (entry is null)
            {
                return false;
            }

            entry.Notes = NormalizeNotes(notes);
        }

        ScheduleSave();
        return true;
    }

    private static string? NormalizeNotes(string? notes)
    {
        return SessionRegistry.NormalizeSessionNotes(notes);
    }

    public bool RemoveEntry(string id)
    {
        var removed = false;

        lock (_lock)
        {
            removed = _history.Entries.RemoveAll(e => e.Id == id) > 0;
        }

        if (removed)
        {
            ScheduleSave();
        }

        return removed;
    }

    public bool ReorderStarred(List<string> orderedIds)
    {
        lock (_lock)
        {
            for (var i = 0; i < orderedIds.Count; i++)
            {
                var entry = _history.Entries.FirstOrDefault(e => e.Id == orderedIds[i] && e.IsStarred);
                if (entry is not null)
                {
                    entry.Order = i;
                }
            }
        }

        ScheduleSave();
        return true;
    }

    private int NextStarredOrder()
    {
        var max = _history.Entries.Where(e => e.IsStarred).Select(e => e.Order).DefaultIfEmpty(-1).Max();
        return max + 1;
    }

    private void MigrateStarredOrder()
    {
        LaunchHistory? snapshot = null;
        var migratedCount = 0;

        lock (_lock)
        {
            var starred = _history.Entries.Where(e => e.IsStarred).ToList();
            if (starred.Count == 0) return;

            var allZero = starred.All(e => e.Order == 0);
            if (!allZero || starred.Count <= 1) return;

            for (var i = 0; i < starred.Count; i++)
            {
                starred[i].Order = i;
            }

            migratedCount = starred.Count;
            snapshot = CloneHistory(_history);
        }

        if (snapshot is not null)
        {
            PersistSnapshot(snapshot);
            Log.Info(() => string.Create(CultureInfo.InvariantCulture, $"Migrated {migratedCount} starred history entries with sequential order"));
        }
    }

    private void Prune()
    {
        var starred = _history.Entries.Where(e => e.IsStarred).ToList();
        var nonStarred = _history.Entries
            .Where(e => !e.IsStarred)
            .OrderByDescending(e => e.LastUsed)
            .Take(MaxRecentEntries)
            .ToList();

        _history.Entries = starred.Concat(nonStarred).ToList();
    }

    private static string GenerateId(
        string shellType,
        string executable,
        string? commandLine,
        string workingDirectory,
        string? dedupeKey = null)
    {
        string normalized;
        if (!string.IsNullOrWhiteSpace(dedupeKey))
        {
            normalized = $"tuple|{dedupeKey.Trim().ToLowerInvariant()}";
        }
        else
        {
            normalized = $"{shellType.ToLowerInvariant()}|{executable.ToLowerInvariant()}|{commandLine ?? ""}|{NormalizePath(workingDirectory)}";
        }
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string NormalizePath(string path)
    {
        return path.Replace('\\', '/').ToLowerInvariant().TrimEnd('/');
    }

    private static string NormalizeLaunchMode(string? launchMode)
    {
        return string.Equals(launchMode, LaunchEntryLaunchModes.AppServerControl, StringComparison.OrdinalIgnoreCase)
            ? LaunchEntryLaunchModes.AppServerControl
            : LaunchEntryLaunchModes.Terminal;
    }

    private static string? NormalizeProfile(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            return null;
        }

        return profile.Trim().ToLowerInvariant() switch
        {
            "codex" => "codex",
            "claude" => "claude",
            "grok" => "grok",
            _ => null
        };
    }

    private static string? NormalizeLaunchOrigin(string? launchOrigin)
    {
        return SessionLaunchOrigins.Normalize(launchOrigin);
    }

    private static string NormalizeSurfaceType(string? surfaceType, string? launchMode, string? profile)
    {
        var normalized = surfaceType?.Trim().ToLowerInvariant();
        return normalized switch
        {
            HistorySurfaceTypes.Terminal => HistorySurfaceTypes.Terminal,
            HistorySurfaceTypes.Codex => HistorySurfaceTypes.Codex,
            HistorySurfaceTypes.Claude => HistorySurfaceTypes.Claude,
            HistorySurfaceTypes.Grok => HistorySurfaceTypes.Grok,
            _ => NormalizeLaunchMode(launchMode) == LaunchEntryLaunchModes.AppServerControl
                ? NormalizeProfile(profile) switch
                {
                    "claude" => HistorySurfaceTypes.Claude,
                    "grok" => HistorySurfaceTypes.Grok,
                    _ => HistorySurfaceTypes.Codex
                }
                : HistorySurfaceTypes.Terminal
        };
    }

    public void Dispose()
    {
        LaunchHistory? snapshot = null;

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
                snapshot = CloneHistory(_history);
            }
        }

        try
        {
            _saveTimer.Dispose();
        }
        catch
        {
        }

        if (snapshot is not null)
        {
            PersistSnapshot(snapshot);
        }
    }
}

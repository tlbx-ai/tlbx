using System.Collections.Concurrent;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Services;

using Ai.Tlbx.MidTerm.Services.Updates;
using Ai.Tlbx.MidTerm.Services.Secrets;
namespace Ai.Tlbx.MidTerm.Settings;

public sealed class SettingsService
{
    internal const string SettingsDirectoryEnvironmentVariable = "MIDTERM_SETTINGS_DIR";
    private readonly string _settingsPath;
    private readonly ISecretStorage _secretStorage;
    private MidTermSettings? _cached;
    private readonly Lock _lock = new();
    private readonly ConcurrentDictionary<string, Action<MidTermSettings>> _settingsListeners = new(StringComparer.Ordinal);

    public SettingsLoadStatus LoadStatus { get; private set; } = SettingsLoadStatus.Default;
    public string? LoadError { get; private set; }
    public string SettingsPath => _settingsPath;
    public string SettingsDirectory => Path.GetDirectoryName(_settingsPath)!;
    public bool IsRunningAsService { get; }
    public ISecretStorage SecretStorage => _secretStorage;

    public string GetEffectiveWorktreeRootDirectory()
    {
        return ResolveEffectiveWorktreeRootDirectory(Load(), ensureExists: true);
    }

    public SettingsService()
    {
        var overrideDirectory = GetSettingsDirectoryOverride();
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            IsRunningAsService = false;
            _settingsPath = Path.Combine(overrideDirectory, "settings.json");
            _secretStorage = SecretStorageFactory.Create(overrideDirectory, isServiceMode: false);
            return;
        }

        IsRunningAsService = DetectServiceMode();
        _settingsPath = GetSettingsPath(IsRunningAsService);
        _secretStorage = SecretStorageFactory.Create(SettingsDirectory, IsRunningAsService);
    }

    internal SettingsService(string settingsDirectory)
    {
        IsRunningAsService = false;
        _settingsPath = Path.Combine(settingsDirectory, "settings.json");
        _secretStorage = SecretStorageFactory.Create(settingsDirectory, IsRunningAsService);
    }

    /// <summary>
    /// Returns the settings file path based on service mode.
    /// SYNC: These paths MUST match:
    ///   - install.sh (PATH_CONSTANTS section)
    ///   - install.ps1 (Path Constants section)
    ///   - LogPaths.cs (GetSettingsDirectory method)
    ///   - UpdateScriptGenerator.cs (CONFIG_DIR variable)
    /// </summary>
    private static string GetSettingsPath(bool isService)
    {
        if (isService)
        {
            if (OperatingSystem.IsWindows())
            {
                // Windows service: %ProgramData%\MidTerm (typically C:\ProgramData\MidTerm)
                var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                return Path.Combine(programData, "MidTerm", "settings.json");
            }
            else
            {
                // Unix service: lowercase 'midterm' - MUST match install.sh
                return "/usr/local/etc/midterm/settings.json";
            }
        }

        // User mode: ~/.midterm
        var userDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var configDir = Path.Combine(userDir, ".midterm");
        return Path.Combine(configDir, "settings.json");
    }

    internal static string? GetSettingsDirectoryOverride()
    {
        var overrideDirectory = Environment.GetEnvironmentVariable(SettingsDirectoryEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(overrideDirectory))
        {
            return null;
        }

        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(overrideDirectory.Trim()));
    }

    public static string ResolveEffectiveWorktreeRootDirectory(
        MidTermSettings settings,
        bool ensureExists = false)
    {
        var effective = !string.IsNullOrWhiteSpace(settings.WorktreeRootDirectory)
            ? NormalizeDirectoryPath(settings.WorktreeRootDirectory)
            : ResolveDefaultWorktreeRootDirectory(settings);

        if (ensureExists)
        {
            try
            {
                Directory.CreateDirectory(effective);
            }
            catch (Exception ex)
            {
                Log.Warn(() => $"Failed to ensure worktree root exists at '{effective}': {ex.Message}");
            }
        }

        return effective;
    }

    private static bool DetectServiceMode()
    {
        if (OperatingSystem.IsWindows())
        {
            return IsWindowsService();
        }

        var serviceSettingsPath = "/usr/local/etc/midterm/settings.json";
        if (!File.Exists(serviceSettingsPath))
        {
            return false;
        }

        // Service settings file exists, but verify we can actually write to it.
        // On macOS/Linux, a root-owned service install creates this file, but if mt
        // is run manually by a non-root user, writes would silently fail (settings
        // appear saved in-memory but never persist to disk). Fall back to user mode
        // (~/.midterm/settings.json) when we don't have write access.
        try
        {
            var dir = Path.GetDirectoryName(serviceSettingsPath)!;
            var testPath = Path.Combine(dir, ".write-check");
            File.WriteAllBytes(testPath, []);
            File.Delete(testPath);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsWindowsService()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return identity.IsSystem;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveDefaultWorktreeRootDirectory(MidTermSettings settings)
    {
        var overrideDirectory = GetSettingsDirectoryOverride();
        if (!string.IsNullOrWhiteSpace(overrideDirectory))
        {
            return Path.Combine(
                overrideDirectory,
                OperatingSystem.IsWindows() ? "Worktrees" : "worktrees");
        }

        if (settings.IsServiceInstall)
        {
            if (OperatingSystem.IsWindows())
            {
                var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                return Path.Combine(programData, "MidTerm", "Worktrees");
            }

            return "/usr/local/etc/midterm/worktrees";
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (OperatingSystem.IsWindows())
        {
            return Path.Combine(home, ".midTerm", "Worktrees");
        }

        return Path.Combine(home, ".midterm", "worktrees");
    }

    private static string NormalizeDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(path.Trim()));
        return fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }


    public MidTermSettings Load()
    {
        lock (_lock)
        {
            if (_cached is not null)
            {
                return _cached;
            }

            if (!File.Exists(_settingsPath))
            {
                _cached = new MidTermSettings();
                LoadSecretsIntoSettings(_cached);
                MergeInstallSettings(_cached);
                LoadStatus = SettingsLoadStatus.Default;
                return _cached;
            }

            try
            {
                var json = File.ReadAllText(_settingsPath);
                _cached = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.MidTermSettings)
                    ?? new MidTermSettings();

                // Apply defaults for properties that may be missing from older settings files
                // System.Text.Json leaves missing bool properties as false, not their initializer value
                ApplyMissingDefaults(_cached, json);

                // Migrate service install flag for existing installations
                MigrateServiceInstallFlag(_cached, json);

                // Load secrets from secure storage
                LoadSecretsIntoSettings(_cached);

                LoadStatus = SettingsLoadStatus.LoadedFromFile;

                // Check for .old file and migrate user preferences (legacy path)
                var oldPath = _settingsPath + ".old";
                if (File.Exists(oldPath))
                {
                    try
                    {
                        MigrateFromOldSettings(oldPath, _cached);
                        Save(_cached);
                        File.Delete(oldPath);
                        LoadStatus = SettingsLoadStatus.MigratedFromOld;
                        Log.Info(() => "Successfully migrated settings from .old file");
                    }
                    catch (Exception ex)
                    {
                        Log.Warn(() => $"Failed to migrate settings from .old file: {ex.Message}");
                    }
                }

                // Check for merge-settings.json from installer
                MergeInstallSettings(_cached);
            }
            catch (Exception ex)
            {
                Log.Error(() => $"Settings corrupt or unreadable: {ex.Message}");

                var bakPath = _settingsPath + ".bak";
                if (File.Exists(bakPath))
                {
                    try
                    {
                        var bakJson = File.ReadAllText(bakPath);
                        _cached = JsonSerializer.Deserialize(bakJson, SettingsJsonContext.Default.MidTermSettings)
                            ?? new MidTermSettings();
                        ApplyMissingDefaults(_cached, bakJson);
                        MigrateServiceInstallFlag(_cached, bakJson);
                        LoadSecretsIntoSettings(_cached);
                        LoadStatus = SettingsLoadStatus.RecoveredFromBackup;
                        Log.Warn(() => "Recovered settings from backup file");
                        Save(_cached);
                        return _cached;
                    }
                    catch (Exception bakEx)
                    {
                        Log.Error(() => $"Backup recovery also failed: {bakEx.Message}");
                    }
                }

                _cached = new MidTermSettings();
                LoadSecretsIntoSettings(_cached);
                LoadStatus = SettingsLoadStatus.ErrorFallbackToDefault;
                LoadError = ex.Message;
            }

            return _cached;
        }
    }

    private void LoadSecretsIntoSettings(MidTermSettings settings)
    {
        settings.SessionSecret = _secretStorage.GetSecret(SecretKeys.SessionSecret);
        settings.PasswordHash = _secretStorage.GetSecret(SecretKeys.PasswordHash);
        settings.CertificatePassword = _secretStorage.GetSecret(SecretKeys.CertificatePassword);
        settings.VoiceServerPassword = _secretStorage.GetSecret(SecretKeys.VoiceServerPassword);
        LoadHubMachineSecrets(settings);
    }

    private void LoadHubMachineSecrets(MidTermSettings settings)
    {
        foreach (var machine in settings.HubMachines)
        {
            machine.ApiKey = null;
            machine.Password = null;
        }

        var raw = _secretStorage.GetSecret(SecretKeys.HubMachineSecrets);
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        try
        {
            var secrets = JsonSerializer.Deserialize(raw, SettingsJsonContext.Default.HubMachineSecrets);
            if (secrets is null)
            {
                return;
            }

            var lookup = secrets.Machines.ToDictionary(secret => secret.Id, StringComparer.Ordinal);
            foreach (var machine in settings.HubMachines)
            {
                if (!lookup.TryGetValue(machine.Id, out var secret))
                {
                    continue;
                }

                machine.ApiKey = secret.ApiKey;
                machine.Password = secret.Password;
            }
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"Failed to load hub machine secrets: {ex.Message}");
        }
    }

    private static void ApplyMissingDefaults(MidTermSettings settings, string json)
    {
        // For boolean properties with non-false defaults, check if they were present in the JSON
        // If not present, apply the intended default value
        if (!json.Contains("\"useWebGL\"", StringComparison.OrdinalIgnoreCase))
        {
            settings.UseWebGL = true;
        }

        if (!json.Contains("\"customGlyphs\"", StringComparison.OrdinalIgnoreCase))
        {
            settings.CustomGlyphs = true;
        }

        if (!json.Contains("\"terminalLigaturesEnabled\"", StringComparison.OrdinalIgnoreCase))
        {
            settings.TerminalLigaturesEnabled = true;
        }

        if (!json.Contains("\"cursorBlink\"", StringComparison.OrdinalIgnoreCase))
        {
            settings.CursorBlink = true;
        }

        if (!json.Contains("\"rightClickPaste\"", StringComparison.OrdinalIgnoreCase))
        {
            settings.RightClickPaste = true;
        }

        if (!json.Contains("\"fileRadar\"", StringComparison.OrdinalIgnoreCase))
        {
            settings.FileRadar = true;
        }

        if (!json.Contains("\"showBookmarks\"", StringComparison.OrdinalIgnoreCase))
        {
            settings.ShowBookmarks = true;
        }

        if (!json.Contains("\"allowAdHocSessionBookmarks\"", StringComparison.OrdinalIgnoreCase))
        {
            settings.AllowAdHocSessionBookmarks = true;
        }

        if (!json.Contains("\"managerBarEnabled\"", StringComparison.OrdinalIgnoreCase))
        {
            settings.ManagerBarEnabled = true;
        }

        if (!json.Contains("\"commandBayLigaturesEnabled\"", StringComparison.OrdinalIgnoreCase))
        {
            settings.CommandBayLigaturesEnabled = true;
        }

        if (!json.Contains("\"tmuxCompatibility\"", StringComparison.OrdinalIgnoreCase))
        {
            settings.TmuxCompatibility = true;
        }

        if (!json.Contains("\"showChangelogAfterUpdate\"", StringComparison.OrdinalIgnoreCase))
        {
            settings.ShowChangelogAfterUpdate = true;
        }

        if (!json.Contains("\"showUpdateNotification\"", StringComparison.OrdinalIgnoreCase))
        {
            settings.ShowUpdateNotification = true;
        }

        if (!json.Contains("\"terminalEnterMode\"", StringComparison.OrdinalIgnoreCase))
        {
            settings.TerminalEnterMode = TerminalEnterModeSetting.ShiftEnterLineFeed;
        }

        if (!json.Contains("\"terminalTransparency\"", StringComparison.OrdinalIgnoreCase))
        {
            settings.TerminalTransparency = settings.UiTransparency;
        }

        if (!json.Contains("\"terminalCellBackgroundTransparency\"", StringComparison.OrdinalIgnoreCase))
        {
            settings.TerminalCellBackgroundTransparency = settings.TerminalTransparency;
        }
    }

    private void MigrateServiceInstallFlag(MidTermSettings settings, string json)
    {
        // If isServiceInstall wasn't in the JSON, infer from directory location
        // This ensures DPAPI scope is consistent for existing installations
        if (!json.Contains("\"isServiceInstall\"", StringComparison.OrdinalIgnoreCase))
        {
            // Service mode settings are in ProgramData (Windows) or /usr/local/etc (Unix)
            // User mode settings are in user profile directory
            if (OperatingSystem.IsWindows())
            {
                var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                settings.IsServiceInstall = _settingsPath.StartsWith(programData, StringComparison.OrdinalIgnoreCase);
            }
            else
            {
                settings.IsServiceInstall = _settingsPath.StartsWith("/usr/local/", StringComparison.Ordinal);
            }

            Log.Info(() => $"Migrated isServiceInstall={settings.IsServiceInstall} based on settings path: {_settingsPath}");
        }
    }

    private void MergeInstallSettings(MidTermSettings current)
    {
        var mergePath = Path.Combine(SettingsDirectory, "merge-settings.json");
        if (!File.Exists(mergePath))
        {
            return;
        }

        try
        {
            var json = File.ReadAllText(mergePath);
            var merge = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.MidTermSettings);
            if (merge is null)
            {
                File.Delete(mergePath);
                return;
            }

            // Always-merge keys: installer controls these
            if (!string.IsNullOrEmpty(merge.RunAsUser))
            {
                current.RunAsUser = merge.RunAsUser;
            }

            if (!string.IsNullOrEmpty(merge.RunAsUserSid))
            {
                current.RunAsUserSid = merge.RunAsUserSid;
            }

            current.AuthenticationEnabled = merge.AuthenticationEnabled;
            current.IsServiceInstall = merge.IsServiceInstall;

            // Protected keys: only set if currently empty/default
            if (string.IsNullOrEmpty(current.CertificatePath) && !string.IsNullOrEmpty(merge.CertificatePath))
            {
                current.CertificatePath = merge.CertificatePath;
                current.KeyProtection = merge.KeyProtection;
            }

            if (current.UpdateChannel == "stable" && merge.UpdateChannel != "stable")
            {
                current.UpdateChannel = merge.UpdateChannel;
            }

            Save(current);
            try
            {
                File.Delete(mergePath);
            }
            catch (Exception ex)
            {
                Log.Warn(() => $"Merged install settings but could not delete merge-settings.json: {ex.Message}");
            }
            Log.Info(() => "Merged install settings from merge-settings.json");
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"Failed to merge install settings; keeping merge-settings.json for retry: {ex.Message}");
        }
    }

    private static void MigrateFromOldSettings(string oldPath, MidTermSettings current)
    {
        var oldJson = File.ReadAllText(oldPath);
        var old = JsonSerializer.Deserialize(oldJson, SettingsJsonContext.Default.MidTermSettings);
        if (old is null)
        {
            return;
        }

        // Preserve security/installer fields that come from the installer, not the user
        var runAsUser = current.RunAsUser;
        var runAsUserSid = current.RunAsUserSid;
        var authEnabled = current.AuthenticationEnabled;
        var isServiceInstall = current.IsServiceInstall;
        var certPath = current.CertificatePath;
        var keyProtection = current.KeyProtection;
        var certThumbprint = current.CertificateThumbprint;

        // Copy ALL user preferences from old settings
        current.DefaultShell = old.DefaultShell;
        current.DefaultCols = old.DefaultCols;
        current.DefaultRows = old.DefaultRows;
        current.DefaultWorkingDirectory = old.DefaultWorkingDirectory;
        current.TerminalEnvironmentVariables = old.TerminalEnvironmentVariables;
        current.CodexYoloDefault = old.CodexYoloDefault;
        current.CodexDefaultAppServerControlModel = old.CodexDefaultAppServerControlModel;
        current.CodexEnvironmentVariables = old.CodexEnvironmentVariables;
        current.ClaudeDangerouslySkipPermissionsDefault = old.ClaudeDangerouslySkipPermissionsDefault;
        current.ClaudeDefaultAppServerControlModel = old.ClaudeDefaultAppServerControlModel;
        current.ClaudeEnvironmentVariables = old.ClaudeEnvironmentVariables;
        current.FontSize = old.FontSize;
        current.FontFamily = old.FontFamily;
        current.CustomGlyphs = old.CustomGlyphs;
        current.BoxDrawingStyle = old.BoxDrawingStyle;
        current.BoxDrawingScale = old.BoxDrawingScale;
        current.CursorStyle = old.CursorStyle;
        current.CursorBlink = old.CursorBlink;
        current.CursorInactiveStyle = old.CursorInactiveStyle;
        current.Theme = old.Theme;
        current.TerminalColorScheme = old.TerminalColorScheme;
        current.TerminalColorSchemes = old.TerminalColorSchemes;
        current.BackgroundImageEnabled = old.BackgroundImageEnabled;
        current.BackgroundImageFileName = old.BackgroundImageFileName;
        current.BackgroundImageRevision = old.BackgroundImageRevision;
        current.BackgroundKenBurnsEnabled = old.BackgroundKenBurnsEnabled;
        current.BackgroundKenBurnsZoomPercent = old.BackgroundKenBurnsZoomPercent;
        current.BackgroundKenBurnsSpeedPxPerSecond = old.BackgroundKenBurnsSpeedPxPerSecond;
        current.UiTransparency = old.UiTransparency;
        current.TerminalTransparency = old.TerminalTransparency;
        current.TerminalCellBackgroundTransparency = old.TerminalCellBackgroundTransparency;
        current.TabTitleMode = old.TabTitleMode;
        current.MinimumContrastRatio = old.MinimumContrastRatio;
        current.SmoothScrolling = old.SmoothScrolling;
        current.ScrollbarStyle = old.ScrollbarStyle;
        current.UseWebGL = old.UseWebGL;
        current.ScrollbackLines = old.ScrollbackLines;
        current.ScrollbackBytes = old.ScrollbackBytes;
        current.BellStyle = old.BellStyle;
        current.CopyOnSelect = old.CopyOnSelect;
        current.RightClickPaste = old.RightClickPaste;
        current.ClipboardShortcuts = old.ClipboardShortcuts;
        current.TerminalEnterMode = old.TerminalEnterMode;
        current.ScrollbackProtection = old.ScrollbackProtection;
        current.PreserveTerminalCursorControl = old.PreserveTerminalCursorControl;
        current.InputMode = old.InputMode;
        current.FileRadar = old.FileRadar;
        current.ShowSidebarSessionFilter = old.ShowSidebarSessionFilter;
        current.ManagerBarEnabled = old.ManagerBarEnabled;
        current.ManagerBarButtons = ManagerBarButton.NormalizeList(old.ManagerBarButtons);
        current.TmuxCompatibility = old.TmuxCompatibility;
        current.DevMode = old.DevMode;
        current.ShowChangelogAfterUpdate = old.ShowChangelogAfterUpdate;
        current.ShowUpdateNotification = old.ShowUpdateNotification;
        current.UpdateChannel = old.UpdateChannel;
        current.Language = old.Language;

        if (oldJson.Contains("\"showBookmarks\"", StringComparison.OrdinalIgnoreCase))
        {
            current.ShowBookmarks = old.ShowBookmarks;
        }

        if (oldJson.Contains("\"allowAdHocSessionBookmarks\"", StringComparison.OrdinalIgnoreCase))
        {
            current.AllowAdHocSessionBookmarks = old.AllowAdHocSessionBookmarks;
        }

        // Restore security/installer fields (these come from the installer, not the user)
        current.RunAsUser = runAsUser;
        current.RunAsUserSid = runAsUserSid;
        current.AuthenticationEnabled = authEnabled;
        current.IsServiceInstall = isServiceInstall;
        current.CertificateThumbprint = certThumbprint;

        // Only migrate cert path if current is empty (installer didn't set one)
        if (string.IsNullOrEmpty(certPath) && !string.IsNullOrEmpty(old.CertificatePath))
        {
            current.CertificatePath = old.CertificatePath;
            current.KeyProtection = old.KeyProtection;
            Log.Info(() => $"Migrated certificate path from old settings: {old.CertificatePath}");
        }
        else
        {
            current.CertificatePath = certPath;
            current.KeyProtection = keyProtection;
        }
    }

    public void Save(MidTermSettings settings)
    {
        lock (_lock)
        {
            // Save secrets to secure storage (best-effort — don't block settings file write)
            try
            {
                SaveSecretsFromSettings(settings);
            }
            catch (Exception ex)
            {
                Log.Warn(() => $"Failed to save secrets to secure storage: {ex.Message}");
            }

            if (_cached is not null && _cached.UpdateChannel != settings.UpdateChannel)
            {
                Log.Warn(() => $"UpdateChannel changing: {_cached.UpdateChannel} → {settings.UpdateChannel}");
            }

            var dir = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }

            // Backup current file before writing (recovery source if crash mid-write)
            if (File.Exists(_settingsPath))
            {
                try { File.Copy(_settingsPath, _settingsPath + ".bak", overwrite: true); }
                catch { }
            }

            // Atomic write: write to temp file, then move (prevents half-written files on crash)
            var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.MidTermSettings);
            var tmpPath = _settingsPath + ".tmp";
            File.WriteAllText(tmpPath, json);
            File.Move(tmpPath, _settingsPath, overwrite: true);
            _cached = settings;
        }

        NotifySettingsChange(settings);
    }

    private void SaveSecretsFromSettings(MidTermSettings settings)
    {
        if (!string.IsNullOrEmpty(settings.SessionSecret))
        {
            _secretStorage.SetSecret(SecretKeys.SessionSecret, settings.SessionSecret);
        }

        if (!string.IsNullOrEmpty(settings.PasswordHash))
        {
            _secretStorage.SetSecret(SecretKeys.PasswordHash, settings.PasswordHash);
        }

        if (!string.IsNullOrEmpty(settings.CertificatePassword))
        {
            _secretStorage.SetSecret(SecretKeys.CertificatePassword, settings.CertificatePassword);
        }
        else
        {
            _secretStorage.DeleteSecret(SecretKeys.CertificatePassword);
        }

        if (!string.IsNullOrEmpty(settings.VoiceServerPassword))
        {
            _secretStorage.SetSecret(SecretKeys.VoiceServerPassword, settings.VoiceServerPassword);
        }

        var hubSecrets = new HubMachineSecrets
        {
            Machines = settings.HubMachines
                .Where(machine =>
                    !string.IsNullOrWhiteSpace(machine.ApiKey) ||
                    !string.IsNullOrWhiteSpace(machine.Password))
                .Select(machine => new HubMachineSecretSettings
                {
                    Id = machine.Id,
                    ApiKey = machine.ApiKey,
                    Password = machine.Password
                })
                .ToList()
        };

        if (hubSecrets.Machines.Count > 0)
        {
            var hubJson = JsonSerializer.Serialize(hubSecrets, SettingsJsonContext.Default.HubMachineSecrets);
            _secretStorage.SetSecret(SecretKeys.HubMachineSecrets, hubJson);
        }
        else
        {
            _secretStorage.DeleteSecret(SecretKeys.HubMachineSecrets);
        }
    }

    public void InvalidateCache()
    {
        lock (_lock)
        {
            _cached = null;
        }
    }

    public string AddSettingsListener(Action<MidTermSettings> callback)
    {
        var id = Guid.NewGuid().ToString("N");
        _settingsListeners[id] = callback;
        return id;
    }

    public void RemoveSettingsListener(string id)
    {
        _settingsListeners.TryRemove(id, out _);
    }

    private void NotifySettingsChange(MidTermSettings settings)
    {
        foreach (var listener in _settingsListeners.Values)
        {
            try
            {
                listener(settings);
            }
            catch (Exception ex)
            {
                Log.Exception(ex, "SettingsService.NotifySettingsChange");
            }
        }
    }
}

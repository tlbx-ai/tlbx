using System.Text.Json.Serialization;
using Ai.Tlbx.MidTerm.Common.Shells;
using Ai.Tlbx.MidTerm.Models.Hub;

namespace Ai.Tlbx.MidTerm.Settings;

public sealed class MidTermSettings
{
    public const int DefaultScrollbackLines = 2000;
    public const int MaxScrollbackLines = 10000;
    public const int DefaultScrollbackBytes = 2 * 1024 * 1024;
    public const int MinScrollbackBytes = 64 * 1024;
    public const int MaxScrollbackBytes = 10 * 1024 * 1024;
    public const int MinBackgroundKenBurnsZoomPercent = 150;
    public const int DefaultBackgroundKenBurnsZoomPercent = 150;
    public const int MaxBackgroundKenBurnsZoomPercent = 300;
    public const int MinBackgroundKenBurnsSpeedPxPerSecond = 0;
    public const int DefaultBackgroundKenBurnsSpeedPxPerSecond = 12;
    public const int MaxBackgroundKenBurnsSpeedPxPerSecond = 120;

    // Session Defaults
    public ShellType DefaultShell { get; set; } = GetPlatformDefaultShell();
    public int DefaultCols { get; set; } = 120;
    public int DefaultRows { get; set; } = 30;
    public string DefaultWorkingDirectory { get; set; } = "";
    public string TerminalEnvironmentVariables { get; set; } = "";
    public bool CodexYoloDefault { get; set; } = false;
    public string CodexDefaultAppServerControlModel { get; set; } = "";
    public string CodexEnvironmentVariables { get; set; } = "";
    public bool ClaudeDangerouslySkipPermissionsDefault { get; set; } = false;
    public string ClaudeDefaultAppServerControlModel { get; set; } = "";
    public string ClaudeEnvironmentVariables { get; set; } = "";
    public string AgentMessageFontFamily { get; set; } = "default";
    public bool ShowAgentMessageTimestamps { get; set; } = false;
    public bool ShowUnknownAgentMessages { get; set; } = true;
    public int ToolCallOutputLines { get; set; } = 5;

    private static ShellType GetPlatformDefaultShell()
    {
        if (OperatingSystem.IsWindows())
        {
            return ShellType.Pwsh;
        }
        if (OperatingSystem.IsMacOS())
        {
            return ShellType.Zsh;
        }
        return ShellType.Bash;
    }

    // Terminal Appearance
    public int FontSize { get; set; } = 14;
    public string FontFamily { get; set; } = "Cascadia Code";
    public bool TerminalLigaturesEnabled { get; set; } = true;
    public double LineHeight { get; set; } = 1;
    public double LetterSpacing { get; set; } = 0;
    public string FontWeight { get; set; } = "normal";
    public string FontWeightBold { get; set; } = "bold";
    public bool CustomGlyphs { get; set; } = true;
    public string BoxDrawingStyle { get; set; } = "classic";
    public double BoxDrawingScale { get; set; } = 1;
    public CursorStyleSetting CursorStyle { get; set; } = CursorStyleSetting.Block;
    public bool CursorBlink { get; set; } = true;
    public CursorInactiveStyleSetting CursorInactiveStyle { get; set; } = CursorInactiveStyleSetting.None;
    public ThemeSetting Theme { get; set; } = ThemeSetting.Dark;
    public string TerminalColorScheme { get; set; } = "auto";
    public List<TerminalColorSchemeDefinition> TerminalColorSchemes { get; set; } = [];
    public bool BackgroundImageEnabled { get; set; } = false;
    public bool HideBackgroundImageOnMobile { get; set; } = true;
    public string? BackgroundImageFileName { get; set; }
    public long BackgroundImageRevision { get; set; } = 0;
    public bool BackgroundKenBurnsEnabled { get; set; } = false;
    public int BackgroundKenBurnsZoomPercent { get; set; } = DefaultBackgroundKenBurnsZoomPercent;
    public int BackgroundKenBurnsSpeedPxPerSecond { get; set; } = DefaultBackgroundKenBurnsSpeedPxPerSecond;
    public int UiTransparency { get; set; } = 0;
    public int TerminalTransparency { get; set; } = 0;
    public int TerminalCellBackgroundTransparency { get; set; } = 0;
    public TabTitleModeSetting TabTitleMode { get; set; } = TabTitleModeSetting.Hostname;
    public double MinimumContrastRatio { get; set; } = 1;
    public bool SmoothScrolling { get; set; } = false;
    public ScrollbarStyleSetting ScrollbarStyle { get; set; } = ScrollbarStyleSetting.Off;
    public bool UseWebGL { get; set; } = true;

    // Terminal Behavior
    public int ScrollbackLines { get; set; } = DefaultScrollbackLines;
    public int ScrollbackBytes { get; set; } = DefaultScrollbackBytes;
    public BellStyleSetting BellStyle { get; set; } = BellStyleSetting.Notification;
    public bool CopyOnSelect { get; set; } = false;
    public bool RightClickPaste { get; set; } = true;
    public ClipboardShortcutsSetting ClipboardShortcuts { get; set; } = ClipboardShortcutsSetting.Auto;
    public TerminalEnterModeSetting TerminalEnterMode { get; set; } = TerminalEnterModeSetting.ShiftEnterLineFeed;
    public bool ScrollbackProtection { get; set; } = false;
    public bool DisableAutoMainBrowserPromotion { get; set; } = true;
    public bool KeepSystemAwakeWithActiveSessions { get; set; } = false;
    public TerminalResumeModeSetting ResumeMode { get; set; } = TerminalResumeModeSetting.FullReplay;

    // Input mode: "keyboard" (default) or "smartinput" (floating text box, no keyboard focus on terminal)
    public string InputMode { get; set; } = "keyboard";

    // File Radar - Detects file paths in terminal output and makes them clickable
    public bool FileRadar { get; set; } = true;

    // Controls whether the bookmarks UI is visible in the sidebar
    public bool ShowBookmarks { get; set; } = true;

    // Restores bookmark pinning for local ad-hoc sessions only
    public bool AllowAdHocSessionBookmarks { get; set; } = true;

    // Sidebar session filter - Shows the sidebar filter input for narrowing visible sessions
    public bool ShowSidebarSessionFilter { get; set; } = false;

    // Linked worktree root - when empty, MidTerm falls back to an OS-specific managed default directory
    public string WorktreeRootDirectory { get; set; } = "";

    // Middle Manager Bar - Quick-action buttons below terminal area
    public bool ManagerBarEnabled { get; set; } = true;
    public bool CommandBayLigaturesEnabled { get; set; } = true;
    public List<ManagerBarButton> ManagerBarButtons { get; set; } =
    [
        new()
        {
            Id = "1",
            Label = "commit and push pls",
            Text = "commit and push pls",
            ActionType = "single",
            Prompts = ["commit and push pls"],
            Trigger = new ManagerBarTrigger { Kind = "fireAndForget" }
        }
    ];

    // Tmux Compatibility - Injects tmux shim into spawned terminals for AI tool integration
    public bool TmuxCompatibility { get; set; } = true;

    // Developer mode - enables dev features (voice sync, faster update checks, etc.)
    public bool DevMode { get; set; } = false;

    // Show changelog automatically after a successful update
    public bool ShowChangelogAfterUpdate { get; set; } = true;

    // Show prominent update notification in sidebar (false = subtle footer hint only)
    public bool ShowUpdateNotification { get; set; } = true;

    // UI Language
    public LanguageSetting Language { get; set; } = LanguageSetting.Auto;

    // Security - User to spawn terminals as (when running as service)
    public string? RunAsUser { get; set; }
    public string? RunAsUserSid { get; set; }  // Windows: User SID for token lookup

    // Authentication
    public bool AuthenticationEnabled { get; set; } = false;

    [JsonIgnore]
    public string? PasswordHash { get; set; }

    [JsonIgnore]
    public string? SessionSecret { get; set; }

    // HTTPS (always enabled - no HTTP endpoint)
    public string? CertificatePath { get; set; }

    [JsonIgnore]
    public string? CertificatePassword { get; set; }  // Deprecated: for legacy PFX migration only

    public KeyProtectionMethod KeyProtection { get; set; } = KeyProtectionMethod.OsProtected;

    // Certificate thumbprint - saved after generation for verification
    // Used to detect if cert was silently regenerated during update failures
    public string? CertificateThumbprint { get; set; }

    // Service mode flag - persisted to ensure DPAPI scope is consistent between
    // installer (which runs elevated) and runtime (which runs as service user).
    // Without this, runtime detection of IsSystem can fail for non-LocalSystem services.
    public bool IsServiceInstall { get; set; } = false;

    // Update channel: "stable" (default) or "dev" (prereleases)
    public string UpdateChannel { get; set; } = "stable";

    // Voice server password (shared secret for MidTerm.Voice authentication)
    [JsonIgnore]
    public string? VoiceServerPassword { get; set; }

    // Hub configuration for monitoring and controlling remote MidTerm instances
    public List<HubMachineSettings> HubMachines { get; set; } = [];
}

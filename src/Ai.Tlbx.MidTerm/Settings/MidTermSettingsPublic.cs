using Ai.Tlbx.MidTerm.Common.Shells;
using Ai.Tlbx.MidTerm.Models.Hub;

namespace Ai.Tlbx.MidTerm.Settings;

public sealed partial class MidTermSettingsPublic
{
    public const int DefaultScrollbackLines = 2000;
    public const int DefaultScrollbackBytes = 2 * 1024 * 1024;
    public const int DefaultBackgroundKenBurnsZoomPercent = 150;
    public const int DefaultBackgroundKenBurnsSpeedPxPerSecond = 12;

    // Session Defaults (DefaultShell intentionally nullable - platform-specific logic at runtime)
    public ShellType? DefaultShell { get; set; }
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
    public int? TerminalTransparency { get; set; }
    public int? TerminalCellBackgroundTransparency { get; set; }
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
    public string InputMode { get; set; } = "keyboard";
    public bool FileRadar { get; set; } = true;
    public bool ShowBookmarks { get; set; } = true;
    public bool AllowAdHocSessionBookmarks { get; set; } = true;
    public bool ShowSidebarSessionFilter { get; set; } = false;
    public string WorktreeRootDirectory { get; set; } = "";
    public bool TmuxCompatibility { get; set; } = true;
    public bool ManagerBarEnabled { get; set; } = true;
    public bool CommandBayLigaturesEnabled { get; set; } = true;
    public bool DevMode { get; set; } = false;
    public bool ShowChangelogAfterUpdate { get; set; } = true;
    public bool ShowUpdateNotification { get; set; } = true;
    public string? UpdateChannel { get; set; }
    public LanguageSetting Language { get; set; } = LanguageSetting.Auto;
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

    // Security
    public string? RunAsUser { get; set; }
    public string? RunAsUserSid { get; set; }

    // Authentication
    public bool AuthenticationEnabled { get; set; } = false;

    // HTTPS
    public string? CertificatePath { get; set; }

    // Hub
    public List<HubMachineInfo> HubMachines { get; set; } = [];
}

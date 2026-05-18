using Ai.Tlbx.MidTerm.Models.Hub;
using Ai.Tlbx.MidTerm.Settings;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class MidTermSettingsPublicTests
{
    [Fact]
    public void FromSettings_AndApplyTo_RoundTripTerminalTransparency()
    {
        var settings = new MidTermSettings
        {
            UiTransparency = 25,
            TerminalTransparency = 55
        };

        var publicSettings = MidTermSettingsPublic.FromSettings(settings);

        Assert.Equal(55, publicSettings.TerminalTransparency);

        settings.TerminalTransparency = 0;
        publicSettings.ApplyTo(settings);

        Assert.Equal(55, settings.TerminalTransparency);
    }

    [Fact]
    public void ApplyTo_NullTerminalTransparency_PreservesExistingValue()
    {
        var settings = new MidTermSettings
        {
            UiTransparency = 10,
            TerminalTransparency = 45
        };

        var publicSettings = new MidTermSettingsPublic
        {
            UiTransparency = 10,
            TerminalTransparency = null
        };

        publicSettings.ApplyTo(settings);

        Assert.Equal(45, settings.TerminalTransparency);
    }

    [Fact]
    public void ApplyTo_MissingUpdateChannel_PreservesExistingChannel()
    {
        var settings = new MidTermSettings
        {
            UpdateChannel = "dev"
        };

        var publicSettings = new MidTermSettingsPublic
        {
            UpdateChannel = null
        };

        publicSettings.ApplyTo(settings);

        Assert.Equal("dev", settings.UpdateChannel);
    }

    [Fact]
    public void ApplyTo_AllowsTransparencySettingsUpToOneHundredPercent()
    {
        var settings = new MidTermSettings();

        var publicSettings = new MidTermSettingsPublic
        {
            UiTransparency = 100,
            TerminalTransparency = 100
        };

        publicSettings.ApplyTo(settings);

        Assert.Equal(100, settings.UiTransparency);
        Assert.Equal(100, settings.TerminalTransparency);
    }

    [Fact]
    public void FromSettings_AndApplyTo_RoundTripBackgroundKenBurnsSettings()
    {
        var settings = new MidTermSettings
        {
            BackgroundKenBurnsEnabled = true,
            BackgroundKenBurnsZoomPercent = 210,
            BackgroundKenBurnsSpeedPxPerSecond = 28
        };

        var publicSettings = MidTermSettingsPublic.FromSettings(settings);

        Assert.True(publicSettings.BackgroundKenBurnsEnabled);
        Assert.Equal(210, publicSettings.BackgroundKenBurnsZoomPercent);
        Assert.Equal(28, publicSettings.BackgroundKenBurnsSpeedPxPerSecond);

        settings.BackgroundKenBurnsEnabled = false;
        settings.BackgroundKenBurnsZoomPercent = MidTermSettings.DefaultBackgroundKenBurnsZoomPercent;
        settings.BackgroundKenBurnsSpeedPxPerSecond = MidTermSettings.DefaultBackgroundKenBurnsSpeedPxPerSecond;
        publicSettings.ApplyTo(settings);

        Assert.True(settings.BackgroundKenBurnsEnabled);
        Assert.Equal(210, settings.BackgroundKenBurnsZoomPercent);
        Assert.Equal(28, settings.BackgroundKenBurnsSpeedPxPerSecond);
    }

    [Fact]
    public void ApplyTo_ClampsBackgroundKenBurnsSettings()
    {
        var settings = new MidTermSettings();

        var publicSettings = new MidTermSettingsPublic
        {
            BackgroundKenBurnsEnabled = true,
            BackgroundKenBurnsZoomPercent = 999,
            BackgroundKenBurnsSpeedPxPerSecond = -10
        };

        publicSettings.ApplyTo(settings);

        Assert.True(settings.BackgroundKenBurnsEnabled);
        Assert.Equal(MidTermSettings.MaxBackgroundKenBurnsZoomPercent, settings.BackgroundKenBurnsZoomPercent);
        Assert.Equal(MidTermSettings.MinBackgroundKenBurnsSpeedPxPerSecond, settings.BackgroundKenBurnsSpeedPxPerSecond);
    }

    [Fact]
    public void FromSettings_AndApplyTo_RoundTripDisableAutoMainBrowserPromotion()
    {
        var settings = new MidTermSettings
        {
            DisableAutoMainBrowserPromotion = true
        };

        var publicSettings = MidTermSettingsPublic.FromSettings(settings);

        Assert.True(publicSettings.DisableAutoMainBrowserPromotion);

        settings.DisableAutoMainBrowserPromotion = false;
        publicSettings.ApplyTo(settings);

        Assert.True(settings.DisableAutoMainBrowserPromotion);
    }

    [Fact]
    public void FromSettings_AndApplyTo_RoundTripAllowAdHocSessionBookmarks()
    {
        var settings = new MidTermSettings
        {
            AllowAdHocSessionBookmarks = false
        };

        var publicSettings = MidTermSettingsPublic.FromSettings(settings);

        Assert.False(publicSettings.AllowAdHocSessionBookmarks);

        settings.AllowAdHocSessionBookmarks = true;
        publicSettings.ApplyTo(settings);

        Assert.False(settings.AllowAdHocSessionBookmarks);
    }

    [Fact]
    public void FromSettings_AndApplyTo_RoundTripShowBookmarks()
    {
        var settings = new MidTermSettings
        {
            ShowBookmarks = false
        };

        var publicSettings = MidTermSettingsPublic.FromSettings(settings);

        Assert.False(publicSettings.ShowBookmarks);

        settings.ShowBookmarks = true;
        publicSettings.ApplyTo(settings);

        Assert.False(settings.ShowBookmarks);
    }

    [Fact]
    public void FromSettings_AndApplyTo_RoundTripTerminalEnvironmentVariables()
    {
        var settings = new MidTermSettings
        {
            TerminalEnvironmentVariables = "FOO=bar\nEMPTY=\nJSON={\"enabled\":true}"
        };

        var publicSettings = MidTermSettingsPublic.FromSettings(settings);

        Assert.Equal(settings.TerminalEnvironmentVariables, publicSettings.TerminalEnvironmentVariables);

        settings.TerminalEnvironmentVariables = string.Empty;
        publicSettings.ApplyTo(settings);

        Assert.Equal("FOO=bar\nEMPTY=\nJSON={\"enabled\":true}", settings.TerminalEnvironmentVariables);
    }

    [Fact]
    public void FromSettings_AndApplyTo_RoundTripAppServerControlDefaultModels()
    {
        var settings = new MidTermSettings
        {
            CodexDefaultAppServerControlModel = "gpt-5.4-codex",
            ClaudeDefaultAppServerControlModel = "claude-sonnet-4-6"
        };

        var publicSettings = MidTermSettingsPublic.FromSettings(settings);

        Assert.Equal("gpt-5.4-codex", publicSettings.CodexDefaultAppServerControlModel);
        Assert.Equal("claude-sonnet-4-6", publicSettings.ClaudeDefaultAppServerControlModel);

        settings.CodexDefaultAppServerControlModel = string.Empty;
        settings.ClaudeDefaultAppServerControlModel = string.Empty;
        publicSettings.ApplyTo(settings);

        Assert.Equal("gpt-5.4-codex", settings.CodexDefaultAppServerControlModel);
        Assert.Equal("claude-sonnet-4-6", settings.ClaudeDefaultAppServerControlModel);
    }

    [Fact]
    public void FromSettings_AndApplyTo_RoundTripFontRenderingSettings()
    {
        var settings = new MidTermSettings
        {
            TerminalLigaturesEnabled = true,
            LineHeight = 1.2,
            LetterSpacing = 0.4,
            FontWeight = "500",
            FontWeightBold = "700"
        };

        var publicSettings = MidTermSettingsPublic.FromSettings(settings);

        Assert.True(publicSettings.TerminalLigaturesEnabled);
        Assert.Equal(1.2, publicSettings.LineHeight);
        Assert.Equal(0.4, publicSettings.LetterSpacing);
        Assert.Equal("500", publicSettings.FontWeight);
        Assert.Equal("700", publicSettings.FontWeightBold);

        settings.TerminalLigaturesEnabled = false;
        settings.LineHeight = 1;
        settings.LetterSpacing = 0;
        settings.FontWeight = "normal";
        settings.FontWeightBold = "bold";
        publicSettings.ApplyTo(settings);

        Assert.True(settings.TerminalLigaturesEnabled);
        Assert.Equal(1.2, settings.LineHeight);
        Assert.Equal(0.4, settings.LetterSpacing);
        Assert.Equal("500", settings.FontWeight);
        Assert.Equal("700", settings.FontWeightBold);
    }

    [Fact]
    public void FromSettings_AndApplyTo_RoundTripCommandBayLigaturesSetting()
    {
        var settings = new MidTermSettings
        {
            CommandBayLigaturesEnabled = true
        };

        var publicSettings = MidTermSettingsPublic.FromSettings(settings);

        Assert.True(publicSettings.CommandBayLigaturesEnabled);

        settings.CommandBayLigaturesEnabled = false;
        publicSettings.ApplyTo(settings);

        Assert.True(settings.CommandBayLigaturesEnabled);
    }

    [Fact]
    public void ApplyTo_NormalizesAgentMessageFontFamilyToSupportedValues()
    {
        var settings = new MidTermSettings
        {
            AgentMessageFontFamily = "default"
        };

        var publicSettings = new MidTermSettingsPublic
        {
            AgentMessageFontFamily = "segoe ui"
        };

        publicSettings.ApplyTo(settings);
        Assert.Equal("Segoe UI", settings.AgentMessageFontFamily);

        publicSettings = MidTermSettingsPublic.FromSettings(settings);
        Assert.Equal("Segoe UI", publicSettings.AgentMessageFontFamily);

        publicSettings.AgentMessageFontFamily = "unsupported";
        publicSettings.ApplyTo(settings);
        Assert.Equal("default", settings.AgentMessageFontFamily);
    }

    [Fact]
    public void FromSettings_AndApplyTo_RoundTripShowAgentMessageTimestamps()
    {
        var settings = new MidTermSettings
        {
            ShowAgentMessageTimestamps = true
        };

        var publicSettings = MidTermSettingsPublic.FromSettings(settings);

        Assert.True(publicSettings.ShowAgentMessageTimestamps);

        settings.ShowAgentMessageTimestamps = false;
        publicSettings.ApplyTo(settings);

        Assert.True(settings.ShowAgentMessageTimestamps);
    }

    [Fact]
    public void FromSettings_AndApplyTo_RoundTripShowUnknownAgentMessages()
    {
        var settings = new MidTermSettings
        {
            ShowUnknownAgentMessages = false
        };

        var publicSettings = MidTermSettingsPublic.FromSettings(settings);

        Assert.False(publicSettings.ShowUnknownAgentMessages);

        settings.ShowUnknownAgentMessages = true;
        publicSettings.ApplyTo(settings);

        Assert.False(settings.ShowUnknownAgentMessages);
    }

    [Fact]
    public void FromSettings_AndApplyTo_ClampsToolCallOutputLines()
    {
        var settings = new MidTermSettings
        {
            ToolCallOutputLines = 12
        };

        var publicSettings = MidTermSettingsPublic.FromSettings(settings);

        Assert.Equal(12, publicSettings.ToolCallOutputLines);

        publicSettings.ToolCallOutputLines = 42;
        publicSettings.ApplyTo(settings);
        Assert.Equal(20, settings.ToolCallOutputLines);

        publicSettings.ToolCallOutputLines = -3;
        publicSettings.ApplyTo(settings);
        Assert.Equal(0, settings.ToolCallOutputLines);
    }

    [Fact]
    public void FromSettings_AndApplyTo_RoundTripCustomTerminalColorSchemes()
    {
        var settings = new MidTermSettings
        {
            TerminalColorScheme = "Ocean Copy",
            TerminalColorSchemes =
            [
                new TerminalColorSchemeDefinition
                {
                    Name = "Ocean Copy",
                    Background = "#101820",
                    Foreground = "#F2F7FF",
                    Cursor = "#F2F7FF",
                    CursorAccent = "#101820",
                    SelectionBackground = "#2A4C66",
                    ScrollbarSliderBackground = "rgba(242, 247, 255, 0.2)",
                    ScrollbarSliderHoverBackground = "rgba(242, 247, 255, 0.35)",
                    ScrollbarSliderActiveBackground = "rgba(242, 247, 255, 0.5)",
                    Black = "#18242E",
                    Red = "#FF6B6B",
                    Green = "#7EE787",
                    Yellow = "#F9E27D",
                    Blue = "#66B3FF",
                    Magenta = "#D2A8FF",
                    Cyan = "#7DE3FF",
                    White = "#D8E7F5",
                    BrightBlack = "#5A7288",
                    BrightRed = "#FF8E8E",
                    BrightGreen = "#9CF0A4",
                    BrightYellow = "#FFEEA8",
                    BrightBlue = "#90CCFF",
                    BrightMagenta = "#E2C0FF",
                    BrightCyan = "#A1EEFF",
                    BrightWhite = "#F2F7FF"
                }
            ]
        };

        var publicSettings = MidTermSettingsPublic.FromSettings(settings);

        Assert.Equal("Ocean Copy", publicSettings.TerminalColorScheme);
        Assert.Single(publicSettings.TerminalColorSchemes);

        settings.TerminalColorScheme = "auto";
        settings.TerminalColorSchemes.Clear();
        publicSettings.ApplyTo(settings);

        Assert.Equal("Ocean Copy", settings.TerminalColorScheme);
        var customScheme = Assert.Single(settings.TerminalColorSchemes);
        Assert.Equal("#66B3FF", customScheme.Blue);
        Assert.Equal("#A1EEFF", customScheme.BrightCyan);
    }

    [Fact]
    public void FromSettings_AndApplyTo_RoundTripDark2BuiltInTerminalColorScheme()
    {
        var settings = new MidTermSettings
        {
            TerminalColorScheme = "dark2"
        };

        var publicSettings = MidTermSettingsPublic.FromSettings(settings);

        Assert.Equal("dark2", publicSettings.TerminalColorScheme);

        settings.TerminalColorScheme = "auto";
        publicSettings.ApplyTo(settings);

        Assert.Equal("dark2", settings.TerminalColorScheme);
    }

    [Fact]
    public void ApplyTo_ClampsAndValidatesFontRenderingSettings()
    {
        var settings = new MidTermSettings
        {
            LineHeight = 1,
            LetterSpacing = 0,
            FontWeight = "normal",
            FontWeightBold = "bold"
        };

        var publicSettings = new MidTermSettingsPublic
        {
            LineHeight = 5,
            LetterSpacing = -10,
            FontWeight = "invalid",
            FontWeightBold = "900"
        };

        publicSettings.ApplyTo(settings);

        Assert.Equal(3, settings.LineHeight);
        Assert.Equal(-2, settings.LetterSpacing);
        Assert.Equal("normal", settings.FontWeight);
        Assert.Equal("900", settings.FontWeightBold);
    }

    [Fact]
    public void FromSettings_ProjectsHubMachinesWithoutExposingSecrets()
    {
        var settings = new MidTermSettings
        {
            HubMachines =
            [
                new HubMachineSettings
                {
                    Id = "machine-a",
                    Name = "Server",
                    BaseUrl = "https://server:8443",
                    Enabled = true,
                    ApiKey = "api-secret",
                    Password = "pw-secret",
                    LastFingerprint = "AA:BB",
                    PinnedFingerprint = "CC:DD"
                }
            ]
        };

        var publicSettings = MidTermSettingsPublic.FromSettings(settings);

        var machine = Assert.Single(publicSettings.HubMachines);
        Assert.Equal("machine-a", machine.Id);
        Assert.True(machine.HasApiKey);
        Assert.True(machine.HasPassword);
        Assert.Equal("AA:BB", machine.LastFingerprint);
        Assert.Equal("CC:DD", machine.PinnedFingerprint);
    }

    [Fact]
    public void ApplyTo_DoesNotReplaceExistingHubMachineSecrets()
    {
        var settings = new MidTermSettings
        {
            HubMachines =
            [
                new HubMachineSettings
                {
                    Id = "machine-a",
                    Name = "Existing",
                    BaseUrl = "https://server:8443",
                    ApiKey = "api-secret",
                    Password = "pw-secret"
                }
            ]
        };

        var publicSettings = new MidTermSettingsPublic
        {
            DefaultCols = settings.DefaultCols,
            DefaultRows = settings.DefaultRows,
            DefaultWorkingDirectory = settings.DefaultWorkingDirectory,
            FontSize = settings.FontSize,
            FontFamily = settings.FontFamily,
            LineHeight = settings.LineHeight,
            LetterSpacing = settings.LetterSpacing,
            FontWeight = settings.FontWeight,
            FontWeightBold = settings.FontWeightBold,
            CursorStyle = settings.CursorStyle,
            CursorBlink = settings.CursorBlink,
            CursorInactiveStyle = settings.CursorInactiveStyle,
            Theme = settings.Theme,
            TerminalColorScheme = settings.TerminalColorScheme,
            TerminalColorSchemes = settings.TerminalColorSchemes,
            BackgroundImageEnabled = settings.BackgroundImageEnabled,
            BackgroundKenBurnsEnabled = settings.BackgroundKenBurnsEnabled,
            BackgroundKenBurnsZoomPercent = settings.BackgroundKenBurnsZoomPercent,
            BackgroundKenBurnsSpeedPxPerSecond = settings.BackgroundKenBurnsSpeedPxPerSecond,
            UiTransparency = settings.UiTransparency,
            TerminalTransparency = settings.TerminalTransparency,
            TabTitleMode = settings.TabTitleMode,
            MinimumContrastRatio = settings.MinimumContrastRatio,
            SmoothScrolling = settings.SmoothScrolling,
            ScrollbarStyle = settings.ScrollbarStyle,
            UseWebGL = settings.UseWebGL,
            ScrollbackLines = settings.ScrollbackLines,
            BellStyle = settings.BellStyle,
            CopyOnSelect = settings.CopyOnSelect,
            RightClickPaste = settings.RightClickPaste,
            ClipboardShortcuts = settings.ClipboardShortcuts,
            TerminalEnterMode = settings.TerminalEnterMode,
            ScrollbackProtection = settings.ScrollbackProtection,
            KeepSystemAwakeWithActiveSessions = settings.KeepSystemAwakeWithActiveSessions,
            InputMode = settings.InputMode,
            FileRadar = settings.FileRadar,
            ShowSidebarSessionFilter = settings.ShowSidebarSessionFilter,
            TmuxCompatibility = settings.TmuxCompatibility,
            ManagerBarEnabled = settings.ManagerBarEnabled,
            ManagerBarButtons = settings.ManagerBarButtons,
            DevMode = settings.DevMode,
            ShowChangelogAfterUpdate = settings.ShowChangelogAfterUpdate,
            ShowUpdateNotification = settings.ShowUpdateNotification,
            UpdateChannel = settings.UpdateChannel,
            Language = settings.Language
        };

        publicSettings.ApplyTo(settings);

        var machine = Assert.Single(settings.HubMachines);
        Assert.Equal("api-secret", machine.ApiKey);
        Assert.Equal("pw-secret", machine.Password);
    }

    [Fact]
    public void ManagerBarButtons_MigrateLegacyTextToPromptWorkflow()
    {
        var settings = new MidTermSettings
        {
            ManagerBarButtons =
            [
                new ManagerBarButton
                {
                    Id = "legacy",
                    Label = "Legacy",
                    Text = "echo hi"
                }
            ]
        };

        var publicSettings = MidTermSettingsPublic.FromSettings(settings);

        var button = Assert.Single(publicSettings.ManagerBarButtons);
        Assert.Equal("single", button.ActionType);
        Assert.Equal("fireAndForget", button.Trigger.Kind);
        Assert.Equal(["echo hi"], button.Prompts);

        settings.ManagerBarButtons.Clear();
        publicSettings.ApplyTo(settings);

        button = Assert.Single(settings.ManagerBarButtons);
        Assert.Equal("echo hi", button.Text);
        Assert.Equal("single", button.ActionType);
        Assert.Equal("fireAndForget", button.Trigger.Kind);
        Assert.Equal(["echo hi"], button.Prompts);
    }
}

using System.Text.Json;
using Ai.Tlbx.MidTerm.Models.Hub;
using Ai.Tlbx.MidTerm.Settings;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class MidTermSettingsPublicTests
{
    [Fact]
    public void SettingsPatch_ProcessPriorityRoundTripsAndNormalizesUnsupportedClass()
    {
        var settings = new MidTermSettings();
        var current = MidTermSettingsPublic.FromSettings(settings);
        using var patch = JsonDocument.Parse("""{"runtimePriorityBoostEnabled":false,"runtimePriorityClass":"high"}""");
        MidTermSettingsPatch.Merge(current, patch.RootElement).ApplyTo(settings);
        Assert.False(settings.RuntimePriorityBoostEnabled);
        Assert.Equal("high", settings.RuntimePriorityClass);
        var roundTrip = MidTermSettingsPublic.FromSettings(settings);
        Assert.False(roundTrip.RuntimePriorityBoostEnabled);
        Assert.Equal("high", roundTrip.RuntimePriorityClass);
        roundTrip.RuntimePriorityClass = "realtime";
        roundTrip.ApplyTo(settings);
        Assert.Equal("aboveNormal", settings.RuntimePriorityClass);
    }

    [Fact]
    public void SettingsReplacement_RejectsPartialDocument()
    {
        var current = MidTermSettingsPublic.FromSettings(new MidTermSettings { UpdateChannel = "stable" });
        using var document = JsonDocument.Parse("""{"updateChannel":"dev"}""");

        var exception = Assert.Throws<ArgumentException>(
            () => MidTermSettingsPatch.Replace(current, document.RootElement));

        Assert.Contains("requires a complete settings document", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Use PATCH /api/settings", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPatch_RejectsUnknownProperty()
    {
        var current = MidTermSettingsPublic.FromSettings(new MidTermSettings());
        using var document = JsonDocument.Parse("""{"updateChanel":"dev"}""");

        var exception = Assert.Throws<ArgumentException>(
            () => MidTermSettingsPatch.Merge(current, document.RootElement));

        Assert.Contains("unknown settings: updateChanel", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void SettingsPatch_ChangesOnlyExplicitProperties()
    {
        var settings = new MidTermSettings
        {
            Language = LanguageSetting.English,
            BackgroundImageEnabled = true,
            BackgroundImageFileName = "app-background.png",
            UiTransparency = 50,
            TerminalColorSchemes =
            [
                new TerminalColorSchemeDefinition { Name = "Personal", Background = "#101010" }
            ],
            UpdateChannel = "stable"
        };
        var current = MidTermSettingsPublic.FromSettings(settings);
        using var document = JsonDocument.Parse("""{"updateChannel":"dev"}""");

        var merged = MidTermSettingsPatch.Merge(current, document.RootElement);
        merged.ApplyTo(settings);

        Assert.Equal("dev", settings.UpdateChannel);
        Assert.Equal(LanguageSetting.English, settings.Language);
        Assert.True(settings.BackgroundImageEnabled);
        Assert.Equal("app-background.png", settings.BackgroundImageFileName);
        Assert.Equal(50, settings.UiTransparency);
        Assert.Collection(
            settings.TerminalColorSchemes,
            scheme => Assert.Equal("Personal", scheme.Name));
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
            NotificationPriority = settings.NotificationPriority,
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

}

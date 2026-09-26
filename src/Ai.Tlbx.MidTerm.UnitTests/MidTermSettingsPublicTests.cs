using System.Text.Json;
using Ai.Tlbx.MidTerm.Models.Hub;
using Ai.Tlbx.MidTerm.Settings;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class MidTermSettingsPublicTests
{
    [Fact]
    public void StayActiveInBackground_PatchAndSerializationRoundTrip()
    {
        var settings = new MidTermSettings();
        Assert.False(settings.StayActiveInBackground);
        var current = MidTermSettingsPublic.FromSettings(settings);
        using var patch = JsonDocument.Parse("""{"stayActiveInBackground":true}""");
        MidTermSettingsPatch.Merge(current, patch.RootElement).ApplyTo(settings);
        Assert.True(settings.StayActiveInBackground);
        var json = JsonSerializer.Serialize(settings, SettingsJsonContext.Default.MidTermSettings);
        var restored = JsonSerializer.Deserialize(json, SettingsJsonContext.Default.MidTermSettings)!;
        Assert.True(MidTermSettingsPublic.FromSettings(restored).StayActiveInBackground);
        using var disable = JsonDocument.Parse("""{"stayActiveInBackground":false}""");
        MidTermSettingsPatch.Merge(MidTermSettingsPublic.FromSettings(restored), disable.RootElement).ApplyTo(restored);
        Assert.False(restored.StayActiveInBackground);
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
    public void SettingsReplacement_AcceptsFullRoundTripDocument()
    {
        var current = MidTermSettingsPublic.FromSettings(new MidTermSettings
        {
            Language = LanguageSetting.English,
            BackgroundImageEnabled = true,
            UpdateChannel = "dev"
        });
        var json = JsonSerializer.Serialize(current, Ai.Tlbx.MidTerm.Services.AppJsonContext.Default.MidTermSettingsPublic);
        using var document = JsonDocument.Parse(json);

        var replacement = MidTermSettingsPatch.Replace(current, document.RootElement);

        Assert.Equal(LanguageSetting.English, replacement.Language);
        Assert.True(replacement.BackgroundImageEnabled);
        Assert.Equal("dev", replacement.UpdateChannel);
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

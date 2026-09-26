using System.Text.Json;
using Ai.Tlbx.MidTerm.Models.Hub;
using Ai.Tlbx.MidTerm.Settings;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class SettingsServiceTests : IDisposable
{
    private readonly string _tempDir;

    public SettingsServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"midterm_settings_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    [Fact]
    public void Constructor_UsesEnvironmentOverrideDirectory_WhenProvided()
    {
        if (!OperatingSystem.IsWindows()) return;

        var overrideDir = Path.Combine(_tempDir, "override-profile");
        var previous = Environment.GetEnvironmentVariable(SettingsService.SettingsDirectoryEnvironmentVariable);
        Environment.SetEnvironmentVariable(SettingsService.SettingsDirectoryEnvironmentVariable, overrideDir);

        try
        {
            var service = new SettingsService();

            Assert.False(service.IsRunningAsService);
            Assert.Equal(Path.Combine(overrideDir, "settings.json"), service.SettingsPath);
            Assert.Equal(overrideDir, service.SettingsDirectory);
        }
        finally
        {
            Environment.SetEnvironmentVariable(SettingsService.SettingsDirectoryEnvironmentVariable, previous);
        }
    }

    [Fact]
    public void Load_MissingTerminalTransparency_InheritsUiTransparency()
    {
        if (!OperatingSystem.IsWindows()) return;

        var json = """
        {
          "uiTransparency": 40
        }
        """;
        File.WriteAllText(Path.Combine(_tempDir, "settings.json"), json);
        var service = new SettingsService(_tempDir);

        var settings = service.Load();

        Assert.Equal(40, settings.UiTransparency);
        Assert.Equal(40, settings.TerminalTransparency);
    }

    [Fact]
    public void Load_ExplicitTerminalTransparency_IsPreserved()
    {
        if (!OperatingSystem.IsWindows()) return;

        var json = """
        {
          "uiTransparency": 20,
          "terminalTransparency": 65
        }
        """;
        File.WriteAllText(Path.Combine(_tempDir, "settings.json"), json);
        var service = new SettingsService(_tempDir);

        var settings = service.Load();

        Assert.Equal(20, settings.UiTransparency);
        Assert.Equal(65, settings.TerminalTransparency);
    }

    [Fact]
    public void Load_CustomTerminalColorSchemes_ArePreserved()
    {
        if (!OperatingSystem.IsWindows()) return;

        var json = """
        {
          "terminalColorScheme": "Ocean Copy",
          "terminalColorSchemes": [
            {
              "name": "Ocean Copy",
              "background": "#101820",
              "foreground": "#F2F7FF",
              "cursor": "#F2F7FF",
              "cursorAccent": "#101820",
              "selectionBackground": "#2A4C66",
              "scrollbarSliderBackground": "rgba(242, 247, 255, 0.2)",
              "scrollbarSliderHoverBackground": "rgba(242, 247, 255, 0.35)",
              "scrollbarSliderActiveBackground": "rgba(242, 247, 255, 0.5)",
              "black": "#18242E",
              "red": "#FF6B6B",
              "green": "#7EE787",
              "yellow": "#F9E27D",
              "blue": "#66B3FF",
              "magenta": "#D2A8FF",
              "cyan": "#7DE3FF",
              "white": "#D8E7F5",
              "brightBlack": "#5A7288",
              "brightRed": "#FF8E8E",
              "brightGreen": "#9CF0A4",
              "brightYellow": "#FFEEA8",
              "brightBlue": "#90CCFF",
              "brightMagenta": "#E2C0FF",
              "brightCyan": "#A1EEFF",
              "brightWhite": "#F2F7FF"
            }
          ]
        }
        """;
        File.WriteAllText(Path.Combine(_tempDir, "settings.json"), json);
        var service = new SettingsService(_tempDir);

        var settings = service.Load();

        Assert.Equal("Ocean Copy", settings.TerminalColorScheme);
        var customScheme = Assert.Single(settings.TerminalColorSchemes);
        Assert.Equal("#66B3FF", customScheme.Blue);
        Assert.Equal("#A1EEFF", customScheme.BrightCyan);
    }

    [Fact]
    public void Load_InvalidJson_FallsBackToDefaultAndCapturesError()
    {
        if (!OperatingSystem.IsWindows()) return;

        File.WriteAllText(Path.Combine(_tempDir, "settings.json"), "{not valid");
        var service = new SettingsService(_tempDir);

        var settings = service.Load();

        Assert.NotNull(settings);
        Assert.Equal(SettingsLoadStatus.ErrorFallbackToDefault, service.LoadStatus);
        Assert.False(string.IsNullOrWhiteSpace(service.LoadError));
    }

    [Fact]
    public void Save_SecretsArePersistedSecurely_NotInSettingsJson()
    {
        if (!OperatingSystem.IsWindows()) return;

        var service = new SettingsService(_tempDir);
        var settings = service.Load();
        settings.PasswordHash = "pw-hash";
        settings.SessionSecret = Convert.ToBase64String(Guid.NewGuid().ToByteArray());
        settings.CertificatePassword = "cert-pass";
        settings.VoiceServerPassword = "voice-pass";
        settings.UpdateChannel = "dev";

        service.Save(settings);

        var savedJson = File.ReadAllText(Path.Combine(_tempDir, "settings.json"));
        Assert.DoesNotContain("pw-hash", savedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("cert-pass", savedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("voice-pass", savedJson, StringComparison.Ordinal);

        var reloadedService = new SettingsService(_tempDir);
        var reloaded = reloadedService.Load();
        Assert.Equal("pw-hash", reloaded.PasswordHash);
        Assert.Equal(settings.SessionSecret, reloaded.SessionSecret);
        Assert.Equal("cert-pass", reloaded.CertificatePassword);
        Assert.Equal("voice-pass", reloaded.VoiceServerPassword);
        Assert.Equal("dev", reloaded.UpdateChannel);
    }

    [Fact]
    public void Save_HubMachineSecretsPersistSecurely_NotInSettingsJson()
    {
        if (!OperatingSystem.IsWindows()) return;

        var service = new SettingsService(_tempDir);
        var settings = service.Load();
        settings.HubMachines =
        [
            new HubMachineSettings
            {
                Id = "machine-a",
                Name = "Server",
                BaseUrl = "https://server:8443",
                ApiKey = "api-secret",
                Password = "pw-secret",
                PinnedFingerprint = "AA:BB"
            }
        ];

        service.Save(settings);

        var savedJson = File.ReadAllText(Path.Combine(_tempDir, "settings.json"));
        Assert.DoesNotContain("api-secret", savedJson, StringComparison.Ordinal);
        Assert.DoesNotContain("pw-secret", savedJson, StringComparison.Ordinal);
        Assert.Contains("machine-a", savedJson, StringComparison.Ordinal);
        Assert.Contains("AA:BB", savedJson, StringComparison.Ordinal);

        var reloaded = new SettingsService(_tempDir).Load();
        var machine = Assert.Single(reloaded.HubMachines);
        Assert.Equal("api-secret", machine.ApiKey);
        Assert.Equal("pw-secret", machine.Password);
        Assert.Equal("AA:BB", machine.PinnedFingerprint);
    }

    [Fact]
    public void Save_NullCertificatePassword_DeletesStoredSecret()
    {
        if (!OperatingSystem.IsWindows()) return;

        var service = new SettingsService(_tempDir);
        var settings = service.Load();
        settings.CertificatePassword = "first";
        service.Save(settings);

        settings.CertificatePassword = null;
        service.Save(settings);

        var reloaded = new SettingsService(_tempDir).Load();
        Assert.Null(reloaded.CertificatePassword);
    }

    [Fact]
    public void InvalidateCache_ForcesReloadFromDisk()
    {
        if (!OperatingSystem.IsWindows()) return;

        var service = new SettingsService(_tempDir);
        var first = service.Load();
        Assert.Equal(14, first.FontSize);

        File.WriteAllText(
            Path.Combine(_tempDir, "settings.json"),
            """
            {
              "fontSize": 31
            }
            """);

        var cached = service.Load();
        Assert.Equal(14, cached.FontSize);

        service.InvalidateCache();
        var refreshed = service.Load();
        Assert.Equal(31, refreshed.FontSize);
    }

    [Fact]
    public void Load_MergeSettingsFile_MergesAndDeletesSource()
    {
        if (!OperatingSystem.IsWindows()) return;

        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var mergePath = Path.Combine(_tempDir, "merge-settings.json");

        var current = new MidTermSettings
        {
            UpdateChannel = "stable",
            AuthenticationEnabled = false
        };
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(current, SettingsJsonContext.Default.MidTermSettings));

        var merge = new MidTermSettings
        {
            RunAsUser = "alice",
            RunAsUserSid = "S-1-5-21-test",
            AuthenticationEnabled = true,
            IsServiceInstall = true,
            UpdateChannel = "dev",
            CertificatePath = @"C:\certs\midterm.pem",
            KeyProtection = KeyProtectionMethod.OsProtected
        };
        File.WriteAllText(mergePath, JsonSerializer.Serialize(merge, SettingsJsonContext.Default.MidTermSettings));

        var service = new SettingsService(_tempDir);
        var loaded = service.Load();

        Assert.Equal("alice", loaded.RunAsUser);
        Assert.Equal("S-1-5-21-test", loaded.RunAsUserSid);
        Assert.True(loaded.AuthenticationEnabled);
        Assert.True(loaded.IsServiceInstall);
        Assert.Equal("dev", loaded.UpdateChannel);
        Assert.Equal(@"C:\certs\midterm.pem", loaded.CertificatePath);
        Assert.False(File.Exists(mergePath));
    }

    [Fact]
    public void Load_MergeSettings_DoesNotDowngradeDevChannelToStable()
    {
        if (!OperatingSystem.IsWindows()) return;

        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var mergePath = Path.Combine(_tempDir, "merge-settings.json");

        var current = new MidTermSettings { UpdateChannel = "dev" };
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(current, SettingsJsonContext.Default.MidTermSettings));

        var merge = new MidTermSettings { UpdateChannel = "stable" };
        File.WriteAllText(mergePath, JsonSerializer.Serialize(merge, SettingsJsonContext.Default.MidTermSettings));

        var service = new SettingsService(_tempDir);
        var loaded = service.Load();

        Assert.Equal("dev", loaded.UpdateChannel);
        Assert.False(File.Exists(mergePath));
    }

    [Fact]
    public void Load_OldSettingsFile_MigratesPreferences_AndDeletesOldFile()
    {
        if (!OperatingSystem.IsWindows()) return;

        var currentPath = Path.Combine(_tempDir, "settings.json");
        var oldPath = currentPath + ".old";

        var current = new MidTermSettings
        {
            FontSize = 14,
            CertificatePath = null
        };
        var old = new MidTermSettings
        {
            FontSize = 22,
            RightClickPaste = false,
            Theme = ThemeSetting.Light,
            BackgroundImageEnabled = true,
            BackgroundImageFileName = "wallpaper.png",
            BackgroundImageRevision = 12345,
            BackgroundKenBurnsEnabled = true,
            BackgroundKenBurnsZoomPercent = 225,
            BackgroundKenBurnsSpeedPxPerSecond = 18,
            UiTransparency = 40,
            TerminalEnterMode = TerminalEnterModeSetting.ShiftEnterLineFeed,
            ShowBookmarks = false,
            AllowAdHocSessionBookmarks = false,
            CertificatePath = @"C:\legacy\midterm.pem",
            KeyProtection = KeyProtectionMethod.OsProtected
        };

        File.WriteAllText(currentPath, JsonSerializer.Serialize(current, SettingsJsonContext.Default.MidTermSettings));
        File.WriteAllText(oldPath, JsonSerializer.Serialize(old, SettingsJsonContext.Default.MidTermSettings));

        var service = new SettingsService(_tempDir);
        var loaded = service.Load();

        Assert.Equal(22, loaded.FontSize);
        Assert.False(loaded.RightClickPaste);
        Assert.Equal(ThemeSetting.Light, loaded.Theme);
        Assert.True(loaded.BackgroundImageEnabled);
        Assert.Equal("wallpaper.png", loaded.BackgroundImageFileName);
        Assert.Equal(12345, loaded.BackgroundImageRevision);
        Assert.True(loaded.BackgroundKenBurnsEnabled);
        Assert.Equal(225, loaded.BackgroundKenBurnsZoomPercent);
        Assert.Equal(18, loaded.BackgroundKenBurnsSpeedPxPerSecond);
        Assert.Equal(40, loaded.UiTransparency);
        Assert.Equal(0, loaded.TerminalTransparency);
        Assert.Equal(TerminalEnterModeSetting.ShiftEnterLineFeed, loaded.TerminalEnterMode);
        Assert.False(loaded.ShowBookmarks);
        Assert.False(loaded.AllowAdHocSessionBookmarks);
        Assert.Equal(@"C:\legacy\midterm.pem", loaded.CertificatePath);
        Assert.False(File.Exists(oldPath));
        Assert.Equal(SettingsLoadStatus.MigratedFromOld, service.LoadStatus);
    }

    [Fact]
    public void Load_InvalidMergeSettings_KeepsMergeFileForRetry_AndContinues()
    {
        if (!OperatingSystem.IsWindows()) return;

        var settingsPath = Path.Combine(_tempDir, "settings.json");
        var mergePath = Path.Combine(_tempDir, "merge-settings.json");

        File.WriteAllText(settingsPath, "{}");
        File.WriteAllText(mergePath, "{this is invalid json");

        var service = new SettingsService(_tempDir);
        var loaded = service.Load();

        Assert.NotNull(loaded);
        Assert.True(File.Exists(mergePath));
    }

}

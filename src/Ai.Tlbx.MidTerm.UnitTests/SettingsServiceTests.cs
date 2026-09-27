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

}

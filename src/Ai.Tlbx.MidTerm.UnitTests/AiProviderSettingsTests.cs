using System.Buffers.Binary;
using Ai.Tlbx.Inference;
using Ai.Tlbx.MidTerm.Services.Voice;
using Ai.Tlbx.MidTerm.Settings;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class AiProviderSettingsTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tlbx-ai-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void KeysPersistOutsideSettingsAndPublicStatus_AndCanBeRemoved()
    {
        Directory.CreateDirectory(_directory);
        var store = new SettingsService(_directory);
        var settings = new AiProviderSettings(store);
        foreach (var provider in AiProviderSettings.Providers) settings.SetKey(provider, "secret-" + provider);
        settings.WorkerModel = AiModel.ClaudeSonnet46;
        store.Save(store.Load());
        var reloaded = new AiProviderSettings(new SettingsService(_directory));
        Assert.Equal(AiModel.ClaudeSonnet46, reloaded.WorkerModel);
        var publicStatus = reloaded.Status().ToJsonString();
        var publicSettings = File.ReadAllText(Path.Combine(_directory, "settings.json"));
        foreach (var provider in AiProviderSettings.Providers)
        {
            Assert.Equal("secret-" + provider, reloaded.GetKey(provider));
            Assert.DoesNotContain("secret-" + provider, publicStatus, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-" + provider, publicSettings, StringComparison.Ordinal);
            reloaded.SetKey(provider, null);
            Assert.Null(reloaded.GetKey(provider));
        }
        Assert.Throws<ArgumentException>(() => settings.SetKey("unknown", "secret"));
        Assert.Throws<ArgumentException>(() => settings.SetKey("openai", "invalid\nkey"));
        Assert.Throws<ArgumentException>(() => settings.WorkerModel = (AiModel)int.MaxValue);
    }

    [Fact]
    public void PublicPreviewsIdentifySavedKeysWithoutReturningTheirHiddenContents()
    {
        Directory.CreateDirectory(_directory);
        var settings = new AiProviderSettings(new SettingsService(_directory));
        foreach (var provider in AiProviderSettings.Providers)
        {
            var key = "sk-test-" + provider + "-hidden-middle-1234";
            settings.SetKey(provider, key);
            var status = settings.Status();
            var entry = status["providers"]!.AsArray().Single(p => p!["id"]!.GetValue<string>() == provider)!;
            Assert.True(entry["configured"]!.GetValue<bool>());
            Assert.Equal("sk-test-••••••••1234", entry["keyPreview"]!.GetValue<string>());
            Assert.DoesNotContain(key, status.ToJsonString(), StringComparison.Ordinal);
            Assert.DoesNotContain("hidden-middle", status.ToJsonString(), StringComparison.Ordinal);
            settings.SetKey(provider, null);
            entry = settings.Status()["providers"]!.AsArray().Single(p => p!["id"]!.GetValue<string>() == provider)!;
            Assert.False(entry["configured"]!.GetValue<bool>());
            Assert.Null(entry["keyPreview"]);
        }
        foreach (var key in new[] { "x", "short-key", "123456789012" })
        {
            settings.SetKey("openai", key);
            Assert.Equal("••••••••", settings.Status()["providers"]![0]!["keyPreview"]!.GetValue<string>());
        }
    }

    [Fact]
    public void DictationWaveHeaderMatchesBrowserPcmFormat()
    {
        var wav = VoiceEndpoints.Wave([0, 1, 2, 3]);
        Assert.Equal(48, wav.Length);
        Assert.Equal(24000, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(24)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(22)));
        Assert.Equal(16, BinaryPrimitives.ReadInt16LittleEndian(wav.AsSpan(34)));
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, wav[44..]);
    }

    [Fact]
    public void DictationWaveOwnsOnlyTheSelectedPcmSlice()
    {
        byte[] input = [99, 0, 1, 2, 3, 88];
        var wav = VoiceEndpoints.Wave(input.AsSpan(1, 4));
        input[1] = 42;
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, wav[44..]);
        Assert.Equal(4, BinaryPrimitives.ReadInt32LittleEndian(wav.AsSpan(40)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}

using Ai.Tlbx.MidTerm.Models.Update;
using Ai.Tlbx.MidTerm.Services.Updates;
using Ai.Tlbx.MidTerm.Settings;
using Ai.Tlbx.MidTerm.Startup;
using Xunit;

namespace Ai.Tlbx.MidTerm.Tests;

public sealed class MultiInstanceRuntimeTests
{
    [Fact]
    public void ArgumentParser_ParseOptions_ReadsInstanceFlags()
    {
        var settingsDir = Path.Combine(Path.GetTempPath(), "midterm-test-instance");

        var options = ArgumentParser.ParseOptions([
            "--port", "2105",
            "--bind", "127.0.0.1",
            "--settings-dir", settingsDir,
            "--service-mode",
            "--service-name", "MidTerm-alice",
            "--launchd-label", "ai.tlbx.midterm.alice",
            "--systemd-service", "midterm-alice"
        ]);

        Assert.Equal(2105, options.Port);
        Assert.Equal("127.0.0.1", options.BindAddress);
        Assert.Equal(settingsDir, options.SettingsDirectory);
        Assert.True(options.ServiceMode);
        Assert.Equal("MidTerm-alice", options.ServiceIdentity.WindowsServiceName);
        Assert.Equal("ai.tlbx.midterm.alice", options.ServiceIdentity.LaunchdLabel);
        Assert.Equal("midterm-alice", options.ServiceIdentity.SystemdServiceName);
    }

    [Fact]
    public void RuntimeOptions_ApplyProcessEnvironment_SetsSettingsAndServiceScope()
    {
        var originalSettingsDir = Environment.GetEnvironmentVariable(SettingsService.SettingsDirectoryEnvironmentVariable);
        var originalServiceMode = Environment.GetEnvironmentVariable(MidTermRuntimeOptions.ServiceModeEnvironmentVariable);
        var originalServiceName = Environment.GetEnvironmentVariable(MidTermServiceIdentity.WindowsServiceNameEnvironmentVariable);

        try
        {
            var settingsDir = Path.Combine(Path.GetTempPath(), "midterm-env-instance");
            var options = new MidTermRuntimeOptions(
                2200,
                "0.0.0.0",
                settingsDir,
                true,
                new MidTermServiceIdentity("MidTerm-env", "ai.tlbx.midterm.env", "midterm-env"));

            options.ApplyProcessEnvironment();

            Assert.Equal(Path.GetFullPath(settingsDir), SettingsService.GetSettingsDirectoryOverride());
            Assert.True(SettingsService.GetServiceModeOverride());
            Assert.Equal("MidTerm-env", Environment.GetEnvironmentVariable(MidTermServiceIdentity.WindowsServiceNameEnvironmentVariable));
        }
        finally
        {
            Environment.SetEnvironmentVariable(SettingsService.SettingsDirectoryEnvironmentVariable, originalSettingsDir);
            Environment.SetEnvironmentVariable(MidTermRuntimeOptions.ServiceModeEnvironmentVariable, originalServiceMode);
            Environment.SetEnvironmentVariable(MidTermServiceIdentity.WindowsServiceNameEnvironmentVariable, originalServiceName);
        }
    }

    [Fact]
    public void UpdateScriptGenerator_WindowsScript_UsesInstanceServiceName()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var extractedDir = Path.Combine(Path.GetTempPath(), "midterm-update-src");
        var installDir = Path.Combine(Path.GetTempPath(), "midterm-update-install");
        var settingsDir = Path.Combine(Path.GetTempPath(), "midterm-update-settings");
        Directory.CreateDirectory(extractedDir);
        Directory.CreateDirectory(installDir);
        Directory.CreateDirectory(settingsDir);

        var scriptPath = UpdateScriptGenerator.GenerateUpdateScript(
            extractedDir,
            Path.Combine(installDir, "mt.exe"),
            settingsDir,
            new MidTermServiceIdentity("MidTerm-bob", "ai.tlbx.midterm.bob", "midterm-bob"),
            UpdateType.WebOnly);

        try
        {
            var script = File.ReadAllText(scriptPath);
            Assert.Contains("$ServiceName = 'MidTerm-bob'", script, StringComparison.Ordinal);
            Assert.Contains("Get-Service -Name $ServiceName", script, StringComparison.Ordinal);
            Assert.DoesNotContain("Get-Service -Name 'MidTerm'", script, StringComparison.Ordinal);
            Assert.DoesNotContain("Start-Service -Name 'MidTerm'", script, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(scriptPath);
        }
    }
}

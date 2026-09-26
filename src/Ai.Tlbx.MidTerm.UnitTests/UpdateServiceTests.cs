using System.Text.Json;
using Ai.Tlbx.MidTerm.Models.Update;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Services.Updates;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class UpdateServiceTests : IDisposable
{
    private const string WindowsAssetName = "mt-win-x64.zip";
    private readonly string _tempDir;

    public UpdateServiceTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"midterm_update_tests_{Guid.NewGuid():N}");
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

    public static IEnumerable<object[]> CompareVersionCases()
    {
        yield return ["1.0.0", "1.0.0", 0];
        yield return ["1.0", "1.0.0", 0];
        yield return ["1.2.0", "1.1.9", 1];
        yield return ["2.0.0", "10.0.0", -1];
        yield return ["1.0.0+abc", "1.0.0+def", 0];
        yield return ["1.0.1+abc", "1.0.0+def", 1];
        yield return ["1.0.0", "1.0.0-dev.1", 1];
        yield return ["1.0.0-dev.1", "1.0.0", -1];
        yield return ["1.0.0-dev.10", "1.0.0-dev.2", 1];
        yield return ["1.0.0-dev.2", "1.0.0-dev.10", -1];
        yield return ["1.0.0-DEV.2", "1.0.0-dev.2", 0];
        yield return ["1.0.0-alpha", "1.0.0-beta", -1];
        yield return ["1.0.x", "1.0.0", 0];
        yield return ["1.0.0.1", "1.0.0", 1];
    }

    [Theory]
    [MemberData(nameof(CompareVersionCases))]
    public void CompareVersions_HandlesSemVerAndPrereleaseOrdering(string left, string right, int expectedSign)
    {
        var actual = Math.Sign(UpdateService.CompareVersions(left, right));
        Assert.Equal(expectedSign, actual);
    }

    [Theory]
    [InlineData("dotnet", true)]
    [InlineData("dotnet.exe", true)]
    [InlineData("mt", false)]
    [InlineData("mt.exe", false)]
    public void IsSharedRuntimeHost_PreventsReplacingTheDotnetInstallation(string executable, bool expected)
    {
        Assert.Equal(expected, UpdateService.IsSharedRuntimeHost(Path.Combine(_tempDir, executable)));
    }

    [Fact]
    public void GenerateUpdateScript_RejectsSharedRuntimeBeforeCreatingArtifacts()
    {
        Assert.Throws<InvalidOperationException>(() => UpdateScriptGenerator.GenerateUpdateScript(
            _tempDir, Path.Combine(_tempDir, "dotnet.exe"), _tempDir));
        Assert.Empty(Directory.GetFiles(_tempDir));
    }

    [Theory]
    [InlineData(UpdateType.Full, "Continuing requested Full update")]
    [InlineData(UpdateType.WebOnly, "Continuing Web-only update")]
    public async Task CaptureSessionUpdateStateBestEffortAsync_DoesNotBlockRequestedUpdate(
        UpdateType updateType,
        string expectedWarning)
    {
        var result = await UpdateService.CaptureSessionUpdateStateBestEffortAsync(
            (_, _) => throw new InvalidOperationException("upstream resume format changed"),
            updateType);

        Assert.True(result.Attempted);
        Assert.False(result.Captured);
        Assert.Contains(expectedWarning, result.Warning, StringComparison.Ordinal);
        Assert.Contains("upstream resume format changed", result.Warning, StringComparison.Ordinal);
    }

    [Fact]
    public void SelectBestRelease_PicksNewestUsableUpgrade_WhenLatestTagMissingPlatformAsset()
    {
        var releases = new[]
        {
            CreateRelease("v6.10.32-dev.2", prerelease: true, "mt-linux-x64.tar.gz"),
            CreateRelease("v6.10.32-dev.1", prerelease: true, WindowsAssetName),
            CreateRelease("v6.10.31-dev.9", prerelease: true, WindowsAssetName)
        };

        var selection = UpdateService.SelectBestRelease(releases, "dev", "6.10.30-dev.5", WindowsAssetName);

        Assert.NotNull(selection);
        Assert.Equal("v6.10.32-dev.1", selection!.Release.TagName);
        Assert.False(selection.IsDowngrade);
    }

    [Fact]
    public void SelectBestRelease_ReinstallIncludesCurrentVersionOnlyWhenRequested()
    {
        var releases = new[] { CreateRelease("v10.15.1-dev", prerelease: true, WindowsAssetName) };
        Assert.Null(UpdateService.SelectBestRelease(releases, "dev", "10.15.1-dev", WindowsAssetName));
        var selection = UpdateService.SelectBestRelease(releases, "dev", "10.15.1-dev", WindowsAssetName, allowReinstall: true);
        Assert.Equal("v10.15.1-dev", selection?.Release.TagName);
    }

    [Fact]
    public void SelectBestRelease_ReinstallPrefersNewerReleaseAndHonorsChannelAndPlatform()
    {
        var releases = new[]
        {
            CreateRelease("v10.15.0", prerelease: false, WindowsAssetName),
            CreateRelease("v10.15.1", prerelease: false, WindowsAssetName),
            CreateRelease("v10.15.2-dev", prerelease: true, WindowsAssetName),
            CreateRelease("v10.16.0", prerelease: false, "mt-linux-x64.tar.gz")
        };
        var selection = UpdateService.SelectBestRelease(releases, "stable", "10.15.0", WindowsAssetName, allowReinstall: true);
        Assert.Equal("v10.15.1", selection?.Release.TagName);
    }

    [Theory]
    [InlineData("10.9.1", "10.12.2", 1, 1, UpdateType.Full)]
    [InlineData("10.12.2", "10.12.2", 1, 1, UpdateType.WebOnly)]
    [InlineData("10.12.2-dev", "10.12.2", 1, 1, UpdateType.WebOnly)]
    [InlineData("10.12.2+build", "10.12.2", 1, 1, UpdateType.WebOnly)]
    [InlineData("10.12.2", "10.12.2", 1, 2, UpdateType.Full)]
    public void DetermineUpdateType_TargetRuntimeCarriesSkippedFullUpdates(
        string installedPty, string targetPty, int installedProtocol, int targetProtocol, UpdateType expected)
    {
        var installed = new VersionManifest { Web = "10.10.2", Pty = installedPty, Protocol = installedProtocol };
        var release = new VersionManifest
        {
            Web = "10.15.1", Pty = targetPty, Protocol = targetProtocol,
            WebOnly = true, MinCompatiblePty = "2.0.0"
        };
        Assert.Equal(expected, UpdateService.DetermineUpdateType(installed, release));
    }

    [Fact]
    public void DetermineUpdateType_CurrentWebVersionWithOlderHostStillRequiresFull()
    {
        var installed = new VersionManifest { Web = "10.15.1", Pty = "10.9.1", Protocol = 1 };
        var release = new VersionManifest { Web = "10.15.1", Pty = "10.12.2", Protocol = 1, WebOnly = true, MinCompatiblePty = "2.0.0" };
        Assert.Equal(UpdateType.Full, UpdateService.DetermineUpdateType(installed, release));
    }

    [Fact]
    public void SelectBestRelease_StableChannelFallsBackToNewestUsableStable_WhenCurrentBuildIsPrerelease()
    {
        var releases = new[]
        {
            CreateRelease("v6.10.33", prerelease: false, "mt-linux-x64.tar.gz"),
            CreateRelease("v6.10.32", prerelease: false, WindowsAssetName),
            CreateRelease("v6.10.34-dev.1", prerelease: true, WindowsAssetName)
        };

        var selection = UpdateService.SelectBestRelease(releases, "stable", "6.10.33-dev.1", WindowsAssetName);

        Assert.NotNull(selection);
        Assert.Equal("v6.10.32", selection!.Release.TagName);
        Assert.True(selection.IsDowngrade);
    }

    [Fact]
    public void SelectBestRelease_DoesNotOfferOlderStableRelease_WhenCurrentBuildIsAlreadyStable()
    {
        var releases = new[]
        {
            CreateRelease("v6.10.33", prerelease: false, "mt-linux-x64.tar.gz"),
            CreateRelease("v6.10.31", prerelease: false, WindowsAssetName)
        };

        var selection = UpdateService.SelectBestRelease(releases, "stable", "6.10.32", WindowsAssetName);

        Assert.Null(selection);
    }

    [Fact]
    public void SelectBestRelease_DevChannelOffersDevPatchOverStableCurrent()
    {
        var releases = new[]
        {
            CreateRelease("v9.7.0", prerelease: false, WindowsAssetName),
            CreateRelease("v9.7.1-dev", prerelease: true, WindowsAssetName)
        };

        var selection = UpdateService.SelectBestRelease(releases, "dev", "9.7.0", WindowsAssetName);

        Assert.NotNull(selection);
        Assert.Equal("v9.7.1-dev", selection!.Release.TagName);
    }

    [Fact]
    public void GetMissingNewerReleaseTagNames_ReturnsRecentNewerTagsMissingFromReleaseList()
    {
        var releases = new[]
        {
            CreateRelease("v9.7.0", prerelease: false, WindowsAssetName),
            CreateRelease("v9.7.0-dev", prerelease: true, WindowsAssetName)
        };
        var tags = new[]
        {
            CreateTag("v9.7.1-dev"),
            CreateTag("v9.7.0"),
            CreateTag("not-a-release"),
            CreateTag("v9.6.41-dev")
        };

        var missingTags = UpdateService.GetMissingNewerReleaseTagNames(releases, tags, "9.7.0");

        Assert.Equal(["v9.7.1-dev"], missingTags);
    }

    [Theory]
    [InlineData("tlbx-ai/MidTerm", "MidTerm")]
    [InlineData("TLBX-AI/TLBX", "tlbx")]
    public void RepositoryCoordinate_OnlyAcceptsSupportedMigrationCoordinates(string value, string expectedName)
    {
        var parsed = RepositoryCoordinate.TryParseAllowed(value, out var repository);

        Assert.True(parsed);
        Assert.Equal("tlbx-ai", repository.Owner, ignoreCase: true);
        Assert.Equal(expectedName, repository.Name, ignoreCase: true);
    }

    [Fact]
    public void RepositoryCoordinate_RejectsUntrustedRepository()
    {
        Assert.False(RepositoryCoordinate.TryParseAllowed("attacker/tlbx", out _));
    }

    [Fact]
    public void TryReadLocalUpdateInfo_WebOnlyManifest_ReturnsWebOnly()
    {
        var localReleaseDir = Path.Combine(_tempDir, "localrelease");
        Directory.CreateDirectory(localReleaseDir);
        File.WriteAllText(
            Path.Combine(localReleaseDir, "version.json"),
            """
            {
              "web": "8.6.7-dev",
              "pty": "8.3.24",
              "protocol": 1,
              "minCompatiblePty": "2.0.0",
              "webOnly": true
            }
            """);

        var installed = new VersionManifest
        {
            Web = "8.6.6-dev",
            Pty = "8.3.24",
            Protocol = 1,
            MinCompatiblePty = "2.0.0"
        };

        var localUpdate = UpdateService.TryReadLocalUpdateInfo(localReleaseDir, installed, "8.6.6-dev");

        Assert.NotNull(localUpdate);
        Assert.Equal(UpdateType.WebOnly, localUpdate!.Type);
        Assert.Equal("8.6.7-dev", localUpdate.Version);
        Assert.Equal(localReleaseDir, localUpdate.Path);
    }

    [Fact]
    public void TryReadLocalUpdateInfo_WebOnlyStablePromotionOfSameDevPty_ReturnsWebOnly()
    {
        var localReleaseDir = Path.Combine(_tempDir, "localrelease");
        Directory.CreateDirectory(localReleaseDir);
        File.WriteAllText(
            Path.Combine(localReleaseDir, "version.json"),
            """
            {
              "web": "10.10.0",
              "pty": "10.9.2",
              "protocol": 1,
              "minCompatiblePty": "2.0.0",
              "webOnly": true
            }
            """);

        var installed = new VersionManifest
        {
            Web = "10.9.9-dev",
            Pty = "10.9.2-dev",
            Protocol = 1,
            MinCompatiblePty = "2.0.0",
            WebOnly = true
        };

        var localUpdate = UpdateService.TryReadLocalUpdateInfo(localReleaseDir, installed, "10.9.9-dev");

        Assert.NotNull(localUpdate);
        Assert.Equal(UpdateType.WebOnly, localUpdate!.Type);
    }

    [Fact]
    public void TryReadLocalUpdateInfo_WebOnlyManifestAfterSkippedRuntimeUpdateRequiresFull()
    {
        var localReleaseDir = Path.Combine(_tempDir, "localrelease");
        Directory.CreateDirectory(localReleaseDir);
        File.WriteAllText(
            Path.Combine(localReleaseDir, "version.json"),
            """
            {
              "web": "10.10.0",
              "pty": "10.9.3",
              "protocol": 1,
              "minCompatiblePty": "2.0.0",
              "webOnly": true
            }
            """);

        var installed = new VersionManifest
        {
            Web = "10.9.9-dev",
            Pty = "10.9.2-dev",
            Protocol = 1,
            MinCompatiblePty = "2.0.0",
            WebOnly = true
        };

        var localUpdate = UpdateService.TryReadLocalUpdateInfo(localReleaseDir, installed, "10.9.9-dev");

        Assert.NotNull(localUpdate);
        Assert.Equal(UpdateType.Full, localUpdate!.Type);
    }

    [Fact]
    public void TryReadLocalUpdateInfo_WebOnlyManifestWithIncompatibleInstalledPtyRemainsFull()
    {
        var localReleaseDir = Path.Combine(_tempDir, "localrelease");
        Directory.CreateDirectory(localReleaseDir);
        File.WriteAllText(
            Path.Combine(localReleaseDir, "version.json"),
            """
            {
              "web": "10.10.0",
              "pty": "10.9.3",
              "protocol": 1,
              "minCompatiblePty": "10.9.2",
              "webOnly": true
            }
            """);

        var installed = new VersionManifest
        {
            Web = "10.9.9-dev",
            Pty = "10.9.1",
            Protocol = 1,
            MinCompatiblePty = "2.0.0",
            WebOnly = true
        };

        var localUpdate = UpdateService.TryReadLocalUpdateInfo(localReleaseDir, installed, "10.9.9-dev");

        Assert.NotNull(localUpdate);
        Assert.Equal(UpdateType.Full, localUpdate!.Type);
    }

    [Fact]
    public void TryReadLocalUpdateInfo_PtyChange_ReturnsFull()
    {
        var localReleaseDir = Path.Combine(_tempDir, "localrelease");
        Directory.CreateDirectory(localReleaseDir);
        File.WriteAllText(
            Path.Combine(localReleaseDir, "version.json"),
            """
            {
              "web": "8.6.7-dev",
              "pty": "8.3.25",
              "protocol": 1,
              "minCompatiblePty": "2.0.0",
              "webOnly": false
            }
            """);

        var installed = new VersionManifest
        {
            Web = "8.6.6-dev",
            Pty = "8.3.24",
            Protocol = 1,
            MinCompatiblePty = "2.0.0"
        };

        var localUpdate = UpdateService.TryReadLocalUpdateInfo(localReleaseDir, installed, "8.6.6-dev");

        Assert.NotNull(localUpdate);
        Assert.Equal(UpdateType.Full, localUpdate!.Type);
    }

    [Fact]
    public void TryReadLocalUpdateInfo_ManifestAheadOfBinaryButInstalledManifestAlreadyMatches_ReturnsWebOnly()
    {
        var localReleaseDir = Path.Combine(_tempDir, "localrelease");
        Directory.CreateDirectory(localReleaseDir);
        File.WriteAllText(
            Path.Combine(localReleaseDir, "version.json"),
            """
            {
              "web": "8.6.16-dev",
              "pty": "8.3.24",
              "protocol": 1,
              "minCompatiblePty": "2.0.0",
              "webOnly": true
            }
            """);

        var installed = new VersionManifest
        {
            Web = "8.6.16-dev",
            Pty = "8.3.24",
            Protocol = 1,
            MinCompatiblePty = "2.0.0"
        };

        var localUpdate = UpdateService.TryReadLocalUpdateInfo(localReleaseDir, installed, "8.6.15-dev");

        Assert.NotNull(localUpdate);
        Assert.Equal(UpdateType.WebOnly, localUpdate!.Type);
        Assert.Equal("8.6.16-dev", localUpdate.Version);
    }

    [Fact]
    public void TryReadLocalUpdateInfo_NotNewer_ReturnsNull()
    {
        var localReleaseDir = Path.Combine(_tempDir, "localrelease");
        Directory.CreateDirectory(localReleaseDir);
        File.WriteAllText(
            Path.Combine(localReleaseDir, "version.json"),
            """
            {
              "web": "8.6.6-dev",
              "pty": "8.3.24",
              "protocol": 1,
              "minCompatiblePty": "2.0.0",
              "webOnly": true
            }
            """);

        var installed = new VersionManifest
        {
            Web = "8.6.6-dev",
            Pty = "8.3.24",
            Protocol = 1,
            MinCompatiblePty = "2.0.0"
        };

        var localUpdate = UpdateService.TryReadLocalUpdateInfo(localReleaseDir, installed, "8.6.6-dev");

        Assert.Null(localUpdate);
    }

    [Fact]
    public void InstallUnixFileAtomically_FallsBackToInPlaceOverwrite_WhenSiblingTempPathIsUnavailable()
    {
        var sourcePath = Path.Combine(_tempDir, "mt-source");
        var destinationPath = Path.Combine(_tempDir, "mt-destination");
        var blockedTempPath = destinationPath + ".new";
        File.WriteAllText(sourcePath, "new-version");
        File.WriteAllText(destinationPath, "old");
        Directory.CreateDirectory(blockedTempPath);

        var logLines = new List<string>();

        UpdateService.InstallUnixFileAtomically(
            sourcePath,
            destinationPath,
            makeExecutable: false,
            (message, level) => logLines.Add($"{level}:{message}"));

        Assert.Equal("new-version", File.ReadAllText(destinationPath));
        Assert.Contains(logLines, line => line.Contains("Falling back to in-place overwrite.", StringComparison.Ordinal));
        Assert.Contains(logLines, line => line.Contains("in-place overwrite fallback", StringComparison.Ordinal));
    }

    [Fact]
    public void ResolveInstalledHostExecutablePath_UsesSettingsFallbackWhenInstallDirectoryDoesNotContainAgentHost()
    {
        var settingsDir = Path.Combine(_tempDir, "settings-fallback");
        var baseDir = Path.Combine(_tempDir, "app-base");
        Directory.CreateDirectory(settingsDir);
        Directory.CreateDirectory(baseDir);

        var fallbackPath = UpdateService.GetAgentHostFallbackPath(settingsDir);
        File.WriteAllText(fallbackPath, "fake-agenthost");

        var resolved = SessionAppServerControlHostRuntimeService.ResolveInstalledHostExecutablePath(settingsDir, baseDir);

        Assert.Equal(fallbackPath, resolved);
    }

    [Fact]
    public void StageLinuxServiceUpdatePayload_WebOnly_SkipsMtAgentHost()
    {
        var extractedDir = Path.Combine(_tempDir, "download", "extracted");
        var settingsDir = Path.Combine(_tempDir, "settings");
        Directory.CreateDirectory(extractedDir);
        Directory.CreateDirectory(settingsDir);
        File.WriteAllText(Path.Combine(extractedDir, "mt"), "new-mt");
        File.WriteAllText(Path.Combine(extractedDir, "mtagenthost"), "new-agenthost");
        File.WriteAllText(
            Path.Combine(extractedDir, "version.json"),
            """
            {
              "web": "8.7.22-dev",
              "pty": "8.6.20",
              "protocol": 1
            }
            """);

        var stagedDir = UpdateService.StageLinuxServiceUpdatePayload(
            extractedDir,
            settingsDir,
            UpdateType.WebOnly,
            deleteSourceAfter: true,
            new UpdateArtifacts(
                Path.Combine(settingsDir, "update.log"),
                Path.Combine(settingsDir, "update-result.json")));

        Assert.Equal(Path.Combine(settingsDir, "update-staging", "payload"), stagedDir);
        Assert.True(File.Exists(Path.Combine(stagedDir, "mt")));
        Assert.False(File.Exists(Path.Combine(stagedDir, "mtagenthost")));
        Assert.True(File.Exists(Path.Combine(stagedDir, "version.json")));
        Assert.False(Directory.Exists(Path.GetDirectoryName(extractedDir)!));

        if (!OperatingSystem.IsWindows())
        {
            var mode = File.GetUnixFileMode(Path.Combine(stagedDir, "mt"));
            Assert.True(mode.HasFlag(UnixFileMode.UserExecute));
        }
    }

    [Fact]
    public void StageLinuxServiceUpdatePayload_FullUpdate_StagesMtAgentHost()
    {
        var extractedDir = Path.Combine(_tempDir, "download-full", "extracted");
        var settingsDir = Path.Combine(_tempDir, "settings-full");
        Directory.CreateDirectory(extractedDir);
        Directory.CreateDirectory(settingsDir);
        File.WriteAllText(Path.Combine(extractedDir, "mt"), "new-mt");
        File.WriteAllText(Path.Combine(extractedDir, "mthost"), "new-mthost");
        File.WriteAllText(Path.Combine(extractedDir, "mtagenthost"), "new-agenthost");
        File.WriteAllText(
            Path.Combine(extractedDir, "version.json"),
            """
            {
              "web": "8.7.22-dev",
              "pty": "8.7.22-dev",
              "protocol": 1
            }
            """);

        var stagedDir = UpdateService.StageLinuxServiceUpdatePayload(
            extractedDir,
            settingsDir,
            UpdateType.Full,
            deleteSourceAfter: true,
            new UpdateArtifacts(
                Path.Combine(settingsDir, "update.log"),
                Path.Combine(settingsDir, "update-result.json")));

        Assert.True(File.Exists(Path.Combine(stagedDir, "mt")));
        Assert.True(File.Exists(Path.Combine(stagedDir, "mthost")));
        Assert.True(File.Exists(Path.Combine(stagedDir, "mtagenthost")));
        Assert.True(File.Exists(Path.Combine(stagedDir, "version.json")));

        if (!OperatingSystem.IsWindows())
        {
            var agentMode = File.GetUnixFileMode(Path.Combine(stagedDir, "mtagenthost"));
            Assert.True(agentMode.HasFlag(UnixFileMode.UserExecute));
        }
    }

    private static GitHubRelease CreateRelease(string tagName, bool prerelease, params string[] assetNames)
    {
        return new GitHubRelease
        {
            TagName = tagName,
            Prerelease = prerelease,
            Assets = assetNames.Select(name => new GitHubAsset
            {
                Name = name,
                BrowserDownloadUrl = $"https://example.com/{name}"
            }).ToList()
        };
    }

    private static GitHubTag CreateTag(string name)
    {
        return new GitHubTag
        {
            Name = name
        };
    }
}

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

    [Fact]
    public async Task FailedDiscoveryIsDistinctFromNoUpdateAndCanBeRetried()
    {
        using var handler = new FailingDiscoveryHandler();
#pragma warning disable IDISP014 // Each test uses an isolated fault-injection transport.
        using var http = new HttpClient(handler, disposeHandler: false);
#pragma warning restore IDISP014
        using var service = new UpdateService(new Ai.Tlbx.MidTerm.Settings.SettingsService(_tempDir), null, http);
        var first = service.CheckForUpdateAsync();
        var concurrent = service.CheckForUpdateAsync();
        Assert.Same(first, concurrent);
        handler.Release.TrySetResult();
        var failed = await first;
        Assert.False(failed!.Available);
        Assert.Contains("HTTP 403", failed.CheckError, StringComparison.Ordinal);
        var requests = handler.Requests;
        await service.CheckForUpdateAsync();
        Assert.True(handler.Requests > requests);
    }

    private sealed class FailingDiscoveryHandler : HttpMessageHandler
    {
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Requests;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            await Release.Task.WaitAsync(cancellationToken);
            throw new HttpRequestException("GitHub returned HTTP 403 (rate limit).");
        }
    }

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
    [InlineData("dev", "10.17.15-dev", "v10.17.16-dev")]
    [InlineData("stable", "10.17.15", "v10.17.16")]
    public void SelectBestRelease_SkipsNewerReleasesWithoutTheCurrentPlatform(
        string channel, string currentVersion, string expectedTag)
    {
        var suffix = channel == "dev" ? "-dev" : "";
        var releases = new[]
        {
            new GitHubRelease { TagName = "v10.17.18" + suffix, Prerelease = channel == "dev", Assets = [] },
            new GitHubRelease
            {
                TagName = expectedTag, Prerelease = channel == "dev",
                Assets = [new GitHubAsset { Name = WindowsAssetName, BrowserDownloadUrl = "https://example.test/mt-win-x64.zip" }]
            }
        };

        var selection = UpdateService.SelectBestRelease(releases, channel, currentVersion, WindowsAssetName);

        Assert.NotNull(selection);
        Assert.Equal(expectedTag, selection.Release.TagName);
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

    [Theory]
    [InlineData("10.9.1", "10.12.2", 1, 1, UpdateType.Full)]
    [InlineData("10.12.2", "10.12.2", 1, 1, UpdateType.WebOnly)]
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

}

using System.Collections.Concurrent;
using System.Globalization;
using System.Diagnostics.CodeAnalysis;
using System.IO.Compression;
using System.Reflection;
using System.Security;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Startup;
using Ai.Tlbx.MidTerm.Models.Update;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services.Updates;

public sealed partial class UpdateService : IDisposable
{
    private const string AgentHostBinaryName = "mtagenthost";
    private const string DistributionOrigin = "https://builds.tlbx.ai";
    private const string DevEnvironmentName = "THELAIR";
    private const string FallbackMinCompatiblePty = "2.0.0";
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan DownloadTimeout = TimeSpan.FromMinutes(10);

    // Dev-only local update path - uses secure ProgramData folder instead of world-writable temp
    private static string LocalReleasePath => OperatingSystem.IsWindows()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "MidTerm", "localrelease")
        : Path.Combine("/var/lib/midterm", "localrelease");
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan DevCheckInterval = TimeSpan.FromMinutes(7);
    private static readonly HttpClient SharedHttpClient = CreateSharedHttpClient();

    [SuppressMessage("Usage", "IDISP014:Use a single instance of HttpClient", Justification = "This factory is called only by the process-wide SharedHttpClient initializer.")]
    [SuppressMessage("Reliability", "CA2000:Dispose objects before losing scope", Justification = "The process-wide HttpClient owns its handler through disposeHandler:true.")]
    private static HttpClient CreateSharedHttpClient()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false };
        return new HttpClient(handler, disposeHandler: true) { Timeout = Timeout.InfiniteTimeSpan };
    }

    private readonly HttpClient _httpClient;
    private readonly SettingsService _settingsService;
    private readonly SessionAppServerControlHostRuntimeService? _appServerControlHostRuntime;
    private readonly ConcurrentDictionary<string, Action<UpdateInfo>> _updateListeners = new(StringComparer.Ordinal);
    private readonly Timer _checkTimer;
    private readonly string _currentVersion;
    private readonly VersionManifest _installedManifest;
    private UpdateInfo? _latestUpdate;
    private bool _disposed;
    private int _updateInProgress;
    private readonly object _checkGate = new();
    private Task<UpdateInfo?>? _checkTask;
    private string? _checkKey;
    private DateTimeOffset _checkStarted;
    private readonly TimeProvider _timeProvider;
    private readonly ConcurrentDictionary<string, UpdateInfo> _lastGoodUpdates = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DiscoveryFailure> _discoveryFailures = new(StringComparer.OrdinalIgnoreCase);

    public UpdateInfo? LatestUpdate => _latestUpdate;
    public string CurrentVersion => _currentVersion;
    public VersionManifest InstalledManifest => _installedManifest;
    public Func<UpdateType, CancellationToken, Task>? BeforeApplyUpdateAsync { get; set; }

    public UpdateService() : this(new SettingsService(), null)
    {
    }

    public UpdateService(SettingsService settingsService) : this(settingsService, null)
    {
    }

    public UpdateService(SettingsService settingsService, SessionAppServerControlHostRuntimeService? appServerControlHostRuntime)
        : this(settingsService, appServerControlHostRuntime, SharedHttpClient)
    {
    }

    internal UpdateService(SettingsService settingsService, SessionAppServerControlHostRuntimeService? appServerControlHostRuntime,
        HttpClient httpClient, TimeProvider? timeProvider = null, string? currentVersion = null)
    {
        _settingsService = settingsService;
        _appServerControlHostRuntime = appServerControlHostRuntime;
        _httpClient = httpClient;
        _timeProvider = timeProvider ?? TimeProvider.System;

        _currentVersion = currentVersion ?? GetCurrentVersion();
        _installedManifest = ReadInstalledManifest();
        var interval = (IsDevEnvironment || settingsService.Load().DevMode) ? DevCheckInterval : CheckInterval;
        _checkTimer = new Timer(OnCheckTimer, null, TimeSpan.FromSeconds(10), interval);
    }

    private static string GetCurrentVersion()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0";

        // Strip git hash suffix (e.g., "2.10.0+abc123" -> "2.10.0")
        var plusIndex = version.IndexOf('+', StringComparison.Ordinal);
        return plusIndex > 0 ? version[..plusIndex] : version;
    }

    static UpdateService()
    {
        SharedHttpClient.DefaultRequestHeaders.Add("User-Agent", "MidTerm-UpdateCheck");
    }

    internal static VersionManifest ReadInstalledManifest()
    {
        var version = GetCurrentVersion();

        // Try to read version.json from install directory
        try
        {
            var installDir = Path.GetDirectoryName(GetCurrentBinaryPath());
            if (!string.IsNullOrEmpty(installDir))
            {
                var versionJsonPath = Path.Combine(installDir, "version.json");
                if (File.Exists(versionJsonPath))
                {
                    var json = File.ReadAllText(versionJsonPath);
                    var manifest = JsonSerializer.Deserialize<VersionManifest>(json, VersionManifestContext.Default.VersionManifest);
                    if (manifest is not null)
                    {
                        // Older web-only updaters copied the target manifest while
                        // preserving an older binary. Use the actual installed host.
                        manifest.Pty = TtyHostSpawner.GetTtyHostVersion() ?? manifest.Pty;
                        return manifest;
                    }
                }
            }
        }
        catch
        {
        }

        // Fallback: assume web and pty are same version
        // Use permissive MinCompatiblePty to avoid killing sessions when version.json is missing (dev)
        return new VersionManifest
        {
            Web = version,
            Pty = version,
            Protocol = 1,
            MinCompatiblePty = FallbackMinCompatiblePty
        };
    }

    internal static UpdateType DetermineUpdateType(VersionManifest installed, VersionManifest release)
    {
        // Protocol change = always full update
        if (release.Protocol != installed.Protocol)
        {
            return UpdateType.Full;
        }

        // The target PTY version carries every intervening runtime refresh forward.
        // Compatibility alone must never let a web-only release skip host fixes.
        if (CompareVersions(release.Pty, installed.Pty) != 0 &&
            (!release.WebOnly || !IsSamePromotedPtyVersion(installed.Pty, release.Pty)))
        {
            return UpdateType.Full;
        }

        // Only web version changed = web-only update (sessions preserved)
        if (!string.Equals(release.Web, installed.Web, StringComparison.OrdinalIgnoreCase))
        {
            return UpdateType.WebOnly;
        }

        return UpdateType.None;
    }

    private static bool IsSamePromotedPtyVersion(string installedPty, string releasePty)
    {
        // Stable promotion only removes the dev suffix; it does not change the host.
        var installed = installedPty.Split('+')[0];
        var release = releasePty.Split('+')[0];
        return string.Equals(installed + "-dev", release, StringComparison.OrdinalIgnoreCase) ||
               string.Equals(release + "-dev", installed, StringComparison.OrdinalIgnoreCase);
    }

    public string AddUpdateListener(Action<UpdateInfo> callback)
    {
        var id = Guid.NewGuid().ToString("N");
        _updateListeners[id] = callback;

        if (_latestUpdate is not null)
        {
            callback(_latestUpdate);
        }

        return id;
    }

    public void RemoveUpdateListener(string id)
    {
        _updateListeners.TryRemove(id, out _);
    }

    private void OnCheckTimer(object? state)
    {
        _ = CheckForUpdateAsync();
    }

    public Task<UpdateInfo?> CheckForUpdateAsync(bool forceFull = false)
    {
        var channel = GetDevEnvironment() is not null ? "dev" : _settingsService.Load().UpdateChannel;
        var key = channel + "/" + forceFull;
        lock (_checkGate)
        {
            // Multiple tabs and an immediate Apply click share one discovery result.
            // Failed checks remain retryable; full reinstalls have their own result.
            if (_checkTask is { } task && _checkKey == key &&
                (!task.IsCompleted || (task.IsCompletedSuccessfully && task.Result?.CheckError is null &&
                    _timeProvider.GetUtcNow() - _checkStarted < TimeSpan.FromSeconds(30)))) return task;
            if (_discoveryFailures.TryGetValue(channel, out var failure) && _timeProvider.GetUtcNow() < failure.RetryAt)
                return Task.FromResult<UpdateInfo?>(failure.Update);
            _checkKey = key;
            _checkStarted = _timeProvider.GetUtcNow();
            return _checkTask = CheckForUpdateCoreAsync(channel, forceFull);
        }
    }

    private async Task<UpdateInfo?> CheckForUpdateCoreAsync(string updateChannel, bool forceFull)
    {
        try
        {
            var devEnv = GetDevEnvironment();
            var assetName = GetAssetNameForPlatform();
            Console.Error.WriteLine($"[UpdateCheck] channel={updateChannel}, current={_currentVersion}, asset={assetName}");

            var releases = await FetchReleasesAsync(updateChannel);
            var selection = SelectBestRelease(releases, updateChannel, _currentVersion, assetName, allowReinstall: true);
            if (selection is null)
            {
                _discoveryFailures.TryRemove(updateChannel, out _);
                _lastGoodUpdates.TryRemove(updateChannel, out _);
                return TryCreateLocalOnlyUpdate(devEnv);
            }

            var release = selection.Release;
            var latestVersion = release.TagName!.TrimStart('v');
            var comparison = CompareVersions(latestVersion, _currentVersion);
            Console.Error.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[UpdateCheck] latest={latestVersion}, comparison={comparison}, downgrade={selection.IsDowngrade}"));

            var releaseManifest = await FetchReleaseManifestAsync(release.TagName!, GetPlatformFromAssetName(assetName)!);
            var updateType = forceFull ? UpdateType.Full : DetermineUpdateType(_installedManifest, releaseManifest);
            if (!forceFull && comparison == 0 && updateType != UpdateType.Full)
            {
                _discoveryFailures.TryRemove(updateChannel, out _);
                _lastGoodUpdates.TryRemove(updateChannel, out _);
                return TryCreateLocalOnlyUpdate(devEnv);
            }

            var localUpdate = devEnv is not null ? CheckLocalUpdate() : null;

            var updateInfo = new UpdateInfo
            {
                Available = true,
                CurrentVersion = _currentVersion,
                LatestVersion = latestVersion,
                ReleaseUrl = release.HtmlUrl ?? $"{DistributionOrigin}/v1/releases/{release.TagName}",
                DownloadUrl = selection.DownloadUrl,
                ArchiveSha256 = selection.ArchiveSha256,
                ArchiveSize = selection.ArchiveSize,
                AssetName = assetName,
                ReleaseNotes = release.Body,
                Type = updateType,
                Environment = devEnv,
                LocalUpdate = localUpdate,
                IsDowngrade = selection.IsDowngrade
            };

            _discoveryFailures.TryRemove(updateChannel, out _);
            if (!forceFull)
            {
                _lastGoodUpdates[updateChannel] = updateInfo;
                _latestUpdate = updateInfo;
                NotifyListeners(updateInfo);
            }
            return updateInfo;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[UpdateCheck] Error: {ex.Message}");
            Log.Warn(() => $"Update check failed: {ex.Message}");
            // Report failed discovery separately from a confirmed absence of updates.
            var devEnv = GetDevEnvironment();
            _lastGoodUpdates.TryGetValue(updateChannel, out var cached);
            var failed = new UpdateInfo
            {
                Available = cached?.Available ?? false, CurrentVersion = _currentVersion,
                LatestVersion = cached?.LatestVersion ?? _currentVersion,
                ReleaseUrl = cached?.ReleaseUrl ?? "", DownloadUrl = cached?.DownloadUrl,
                ArchiveSha256 = cached?.ArchiveSha256, ArchiveSize = cached?.ArchiveSize ?? 0,
                AssetName = cached?.AssetName, ReleaseNotes = cached?.ReleaseNotes,
                Type = cached?.Type ?? UpdateType.Full, IsDowngrade = cached?.IsDowngrade ?? false,
                LastResult = cached?.LastResult, CheckError = ex.Message, Environment = devEnv,
                LocalUpdate = devEnv is not null ? CheckLocalUpdate() : null
            };
            var failures = _discoveryFailures.TryGetValue(updateChannel, out var previous) ? Math.Min(previous.Count + 1, 6) : 1;
            var delay = ex is DistributionRequestException { RetryAfter: { } retryAfter }
                ? retryAfter
                : TimeSpan.FromSeconds(Math.Min(60, Math.Pow(2, failures)) + Random.Shared.NextDouble());
            _discoveryFailures[updateChannel] = new DiscoveryFailure(_timeProvider.GetUtcNow() + delay, failures, failed);
            if (!forceFull) { _latestUpdate = failed; NotifyListeners(failed); }
            return failed;
        }
    }

    private UpdateInfo? TryCreateLocalOnlyUpdate(string? devEnv)
    {
        if (devEnv is null)
        {
            _latestUpdate = null;
            return null;
        }

        var localUpdateOnly = CheckLocalUpdate();
        if (localUpdateOnly is null)
        {
            _latestUpdate = null;
            return null;
        }

        _latestUpdate = new UpdateInfo
        {
            Available = false,
            CurrentVersion = _currentVersion,
            LatestVersion = _currentVersion,
            ReleaseUrl = "",
            Environment = devEnv,
            LocalUpdate = localUpdateOnly
        };
        NotifyListeners(_latestUpdate);
        return _latestUpdate;
    }

    private async Task<List<GitHubRelease>> FetchReleasesAsync(string updateChannel)
    {
        var dev = string.Equals(updateChannel, "dev", StringComparison.OrdinalIgnoreCase);
        if (!dev) return await FetchCatalogAsync("main");
        // Dev accepts prereleases and stable releases. Compare both catalogs
        // even when the dev catalog still contains a compatible older build.
        var catalogs = await Task.WhenAll(FetchCatalogAsync("dev"), FetchCatalogAsync("main"));
        return [.. catalogs[0], .. catalogs[1]];
    }

    private async Task<List<GitHubRelease>> FetchCatalogAsync(string channel)
    {
        var json = await FetchMetadataAsync($"{DistributionOrigin}/v1/channels/{channel}/releases.json", 2 * 1024 * 1024);
        return JsonSerializer.Deserialize(json, GitHubReleaseContext.Default.ListGitHubRelease)
            ?? throw new InvalidDataException("Build distribution returned no release catalog.");
    }

    private async Task<string> FetchMetadataAsync(string url, int maximumBytes)
    {
        using var timeout = new CancellationTokenSource(MetadataTimeout, _timeProvider);
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode)
        {
            var retry = response.Headers.RetryAfter;
            var delay = retry?.Delta ?? (retry?.Date - _timeProvider.GetUtcNow());
            throw new DistributionRequestException(string.Create(CultureInfo.InvariantCulture, $"Build distribution returned HTTP {(int)response.StatusCode}."),
                delay is { } value ? TimeSpan.FromSeconds(Math.Clamp(value.TotalSeconds, 1, 300)) : null);
        }
        if (response.Content.Headers.ContentLength > maximumBytes)
            throw new InvalidDataException("Build distribution metadata is too large.");
        await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await input.ReadAsync(buffer, timeout.Token)) != 0)
        {
            if (output.Length + read > maximumBytes) throw new InvalidDataException("Build distribution metadata is too large.");
            output.Write(buffer, 0, read);
        }
        return System.Text.Encoding.UTF8.GetString(output.GetBuffer(), 0, checked((int)output.Length));
    }

    private sealed class DistributionRequestException(string message, TimeSpan? retryAfter) : HttpRequestException(message)
    {
        public TimeSpan? RetryAfter { get; } = retryAfter;
    }

    private sealed record DiscoveryFailure(DateTimeOffset RetryAt, int Count, UpdateInfo Update);

    internal static ReleaseSelection? SelectBestRelease(
        IEnumerable<GitHubRelease> releases,
        string updateChannel,
        string currentVersion,
        string assetName,
        bool allowReinstall = false)
    {
        var isDevChannel = string.Equals(updateChannel, "dev", StringComparison.OrdinalIgnoreCase);
        var allowStableDowngrade = !isDevChannel && HasPrerelease(currentVersion);

        ReleaseSelection? bestUpgrade = null;
        ReleaseSelection? currentRelease = null;
        ReleaseSelection? bestDowngrade = null;

        foreach (var release in releases)
        {
            if (!IsReleaseInChannel(release, isDevChannel) || string.IsNullOrWhiteSpace(release.TagName))
            {
                continue;
            }

            var downloadUrl = GetAssetDownloadUrl(release, assetName);
            if (string.IsNullOrWhiteSpace(downloadUrl))
            {
                continue;
            }

            var candidate = new ReleaseSelection
            {
                Release = release,
                DownloadUrl = downloadUrl,
                ArchiveSha256 = release.Assets!.First(asset => asset.Name == assetName).Digest!["sha256:".Length..],
                ArchiveSize = release.Assets!.First(asset => asset.Name == assetName).Size
            };

            var comparison = CompareVersions(release.TagName!.TrimStart('v'), currentVersion);
            if (comparison == 0 && allowReinstall)
            {
                currentRelease = candidate;
            }
            if (comparison > 0)
            {
                if (IsBetterReleaseCandidate(candidate, bestUpgrade))
                {
                    bestUpgrade = candidate;
                }

                continue;
            }

            if (comparison < 0 && allowStableDowngrade && IsBetterReleaseCandidate(candidate, bestDowngrade))
            {
                candidate.IsDowngrade = true;
                bestDowngrade = candidate;
            }
        }

        return bestUpgrade ?? currentRelease ?? bestDowngrade;
    }

    private static bool IsBetterReleaseCandidate(ReleaseSelection candidate, ReleaseSelection? currentBest)
    {
        if (currentBest is null)
        {
            return true;
        }

        return CompareVersions(candidate.Release.TagName!.TrimStart('v'), currentBest.Release.TagName!.TrimStart('v')) > 0;
    }

    private static bool HasPrerelease(string version)
    {
        var cleanVersion = version.Split('+')[0];
        var (_, prerelease) = ParseVersionWithPrerelease(cleanVersion);
        return prerelease is not null;
    }

    private static bool IsReleaseInChannel(GitHubRelease release, bool isDevChannel)
    {
        return !release.Draft && (isDevChannel || !release.Prerelease);
    }

    private static string? GetAssetDownloadUrl(GitHubRelease release, string assetName)
    {
        var asset = release.Assets?.FirstOrDefault(a => string.Equals(a.Name, assetName, StringComparison.Ordinal));
        if (asset is null) return null;
        if (release.Prerelease != HasPrerelease(release.TagName ?? "")
            || !IsDistributionAssetUrl(asset.BrowserDownloadUrl, release.TagName, assetName)
            || asset.Size <= 0 || !IsArchiveDigest(asset.Digest))
            throw new SecurityException($"Build distribution returned invalid metadata for {assetName}.");
        return asset.BrowserDownloadUrl;
    }

    internal static bool IsDistributionAssetUrl(string? value, string? tag, string assetName)
    {
        if (tag is null || !ReleaseTagRegex().IsMatch(tag) || !Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        var version = tag.StartsWith('v') ? tag[1..] : tag;
        var rid = GetPlatformFromAssetName(assetName);
        if (rid is not ("win-x64" or "win-x86" or "linux-x64" or "linux-arm64" or "osx-x64" or "osx-arm64")) return false;
        var channel = HasPrerelease(version) ? "dev" : "main";
        return uri.Scheme == Uri.UriSchemeHttps && uri.Host.Equals("builds.tlbx.ai", StringComparison.OrdinalIgnoreCase)
            && uri.Port == 443 && uri.UserInfo.Length == 0 && uri.Query.Length == 0 && uri.Fragment.Length == 0
            && uri.AbsolutePath == $"/{channel}/{version}/{rid}/{assetName}";
    }

    [GeneratedRegex(@"^v?[0-9]+\.[0-9]+\.[0-9]+(?:-[A-Za-z0-9.-]+)?$", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex ReleaseTagRegex();

    private static bool IsArchiveDigest(string? value) => value is { Length: 71 }
        && value.StartsWith("sha256:", StringComparison.Ordinal)
        && value.AsSpan(7).IndexOfAnyExcept("0123456789abcdefABCDEF") < 0;

    internal async Task<VersionManifest> FetchReleaseManifestAsync(string tagName, string platform)
    {
        if (!ReleaseTagRegex().IsMatch(tagName)) throw new SecurityException("Invalid release tag.");
        var json = await FetchMetadataAsync($"{DistributionOrigin}/v1/releases/{tagName}/{platform}/version.json", 128 * 1024);
        var manifest = JsonSerializer.Deserialize(json, VersionManifestContext.Default.VersionManifest);
        if (manifest is null || !UpdateVerification.VerifyManifest(manifest, platform, tagName,
                HasPrerelease(tagName) ? "dev" : "stable"))
            throw new SecurityException("Build distribution release manifest signature or identity is invalid.");
        return manifest;
    }

    private LocalUpdateInfo? CheckLocalUpdate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        return TryReadLocalUpdateInfo(LocalReleasePath, _installedManifest, _currentVersion);
    }

    internal static LocalUpdateInfo? TryReadLocalUpdateInfo(
        string localReleasePath,
        VersionManifest installedManifest,
        string currentVersion)
    {
        if (string.IsNullOrWhiteSpace(localReleasePath))
        {
            return null;
        }

        var versionJsonPath = Path.Combine(localReleasePath, "version.json");
        if (!File.Exists(versionJsonPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(versionJsonPath);
            var manifest = JsonSerializer.Deserialize<VersionManifest>(json, VersionManifestContext.Default.VersionManifest);
            if (manifest is null)
            {
                return null;
            }

            if (!IsNewerVersion(manifest.Web, currentVersion))
            {
                return null;
            }

            var updateType = DetermineUpdateType(installedManifest, manifest);
            if (updateType == UpdateType.None &&
                !string.Equals(manifest.Web, currentVersion, StringComparison.OrdinalIgnoreCase))
            {
                // If the installed manifest already advanced but the running mt.exe is still older,
                // treat the retry as web-only so the updater does not restart mthost unnecessarily.
                updateType = UpdateType.WebOnly;
            }

            return new LocalUpdateInfo
            {
                Available = true,
                Version = manifest.Web,
                Path = localReleasePath,
                Type = updateType
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? GetDevEnvironment()
    {
        var env = System.Environment.GetEnvironmentVariable("MIDTERM_ENVIRONMENT");
        return env == DevEnvironmentName ? env : null;
    }

    public static bool IsDevEnvironment => GetDevEnvironment() is not null;

    public string? GetLocalUpdatePath()
    {
        if (!IsDevEnvironment || !OperatingSystem.IsWindows())
        {
            return null;
        }

        var versionJsonPath = Path.Combine(LocalReleasePath, "version.json");
        if (!File.Exists(versionJsonPath))
        {
            return null;
        }

        // Verify required binaries exist
        var mtPath = Path.Combine(LocalReleasePath, "mt.exe");
        var mthostPath = Path.Combine(LocalReleasePath, "mthost.exe");
        var agentHostPath = Path.Combine(LocalReleasePath, $"{AgentHostBinaryName}.exe");
        if (!File.Exists(mtPath) || !File.Exists(mthostPath) || !File.Exists(agentHostPath))
        {
            return null;
        }

        return LocalReleasePath;
    }

    public async Task<string?> DownloadUpdateAsync(UpdateInfo update)
    {
        var assetName = update.AssetName ?? GetAssetNameForPlatform();
        if (!IsDistributionAssetUrl(update.DownloadUrl, "v" + update.LatestVersion.TrimStart('v'), assetName)
            || !IsArchiveDigest("sha256:" + update.ArchiveSha256) || update.ArchiveSize <= 0) return null;
        string? tempDir = null;
        var verified = false;
        try
        {
            using var timeout = new CancellationTokenSource(DownloadTimeout, _timeProvider);
            tempDir = Path.Combine(Path.GetTempPath(), $"mt-update-{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);
            var downloadPath = Path.Combine(tempDir, assetName);
            using (var response = await _httpClient.GetAsync(update.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, timeout.Token))
            {
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength is { } length && length != update.ArchiveSize)
                    throw new InvalidDataException("Build archive length does not match the catalog.");
                await using var input = await response.Content.ReadAsStreamAsync(timeout.Token);
                await using var output = File.Create(downloadPath);
                var buffer = new byte[81920];
                long written = 0;
                int read;
                while ((read = await input.ReadAsync(buffer, timeout.Token)) != 0)
                {
                    written += read;
                    if (written > update.ArchiveSize) throw new InvalidDataException("Build archive exceeds the catalog length.");
                    await output.WriteAsync(buffer.AsMemory(0, read), timeout.Token);
                }
                if (written != update.ArchiveSize) throw new InvalidDataException("Build archive is truncated.");
            }
            await using (var downloaded = File.OpenRead(downloadPath))
            {
                var actualHash = Convert.ToHexStringLower(await SHA256.HashDataAsync(downloaded, timeout.Token));
                if (!string.Equals(actualHash, update.ArchiveSha256, StringComparison.OrdinalIgnoreCase))
                    throw new SecurityException("Build archive SHA256 does not match the catalog.");
            }
            var extractDir = Path.Combine(tempDir, "extracted");
            Directory.CreateDirectory(extractDir);
            if (assetName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)) ZipFile.ExtractToDirectory(downloadPath, extractDir);
            else if (assetName.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) ExtractTarGz(downloadPath, extractDir);

            var manifestJson = await File.ReadAllTextAsync(Path.Combine(extractDir, "version.json"), timeout.Token);
            var manifest = JsonSerializer.Deserialize(manifestJson, VersionManifestContext.Default.VersionManifest);
            if (manifest is null || !UpdateVerification.VerifyUpdate(extractDir, manifest,
                    GetPlatformFromAssetName(assetName), update.LatestVersion,
                    HasPrerelease(update.LatestVersion) ? "dev" : "stable"))
                throw new SecurityException("Build archive manifest signature or file checksums are invalid.");
            verified = true;
            return extractDir;
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"Update download verification failed: {ex.Message}");
            return null;
        }
        finally
        {
            if (!verified && tempDir is not null)
                try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void ExtractTarGz(string archivePath, string extractDir)
    {
        using var fs = File.OpenRead(archivePath);
        using var gzip = new GZipStream(fs, CompressionMode.Decompress);
        using var ms = new MemoryStream();
        gzip.CopyTo(ms);
        ms.Position = 0;

        while (ms.Position < ms.Length)
        {
            var header = new byte[512];
            var read = ms.Read(header, 0, 512);
            if (read < 512 || header[0] == 0)
            {
                break;
            }

            var nameBytes = header[..100];
            var name = System.Text.Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
            if (string.IsNullOrWhiteSpace(name))
            {
                break;
            }

            var sizeStr = System.Text.Encoding.ASCII.GetString(header[124..136]).TrimEnd('\0', ' ');
            var size = string.IsNullOrEmpty(sizeStr) ? 0L : Convert.ToInt64(sizeStr, 8);

            var filePath = Path.Combine(extractDir, name);

            // Security: Validate path stays within extract directory (prevent path traversal)
            var fullPath = Path.GetFullPath(filePath);
            var fullExtractDir = Path.GetFullPath(extractDir);
            if (!fullPath.StartsWith(fullExtractDir + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                fullPath != fullExtractDir)
            {
                throw new SecurityException($"Path traversal detected in archive: {name}");
            }

            var typeFlag = header[156];

            if (typeFlag == '5' || name.EndsWith('/'))
            {
                Directory.CreateDirectory(filePath);
            }
            else if (size > 0)
            {
                const long maxExtractFileSize = 500 * 1024 * 1024;
                if (size < 0 || size > maxExtractFileSize)
                {
                    throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture, $"Tar entry too large: {size}"));
                }

                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }

                var content = new byte[(int)checked(size)];
                ms.Read(content, 0, (int)size);
                File.WriteAllBytes(filePath, content);
            }

            var remainder = (int)(512 - (size % 512)) % 512;
            if (remainder > 0)
            {
                ms.Position += remainder;
            }
        }
    }

    public static int CompareVersions(string v1, string v2)
    {
        // Strip build metadata (e.g., +abc123)
        var v1Clean = v1.Split('+')[0];
        var v2Clean = v2.Split('+')[0];

        // Parse version and prerelease (e.g., "6.10.30-dev.1" -> base="6.10.30", pre="dev.1")
        var (v1Base, v1Pre) = ParseVersionWithPrerelease(v1Clean);
        var (v2Base, v2Pre) = ParseVersionWithPrerelease(v2Clean);

        // Compare base versions first
        var baseCompare = CompareBaseVersions(v1Base, v2Base);
        if (baseCompare != 0)
        {
            return baseCompare;
        }

        // Same base version - compare prereleases
        // Stable (no prerelease) beats any prerelease
        if (v1Pre is null && v2Pre is not null)
        {
            return 1;  // v1 is stable, v2 is prerelease -> v1 wins
        }
        if (v1Pre is not null && v2Pre is null)
        {
            return -1; // v1 is prerelease, v2 is stable -> v2 wins
        }
        if (v1Pre is null && v2Pre is null)
        {
            return 0;  // Both stable
        }

        // Both have prereleases - compare them (e.g., dev.5 > dev.4)
        return ComparePrereleases(v1Pre!, v2Pre!);
    }

    private static (string baseVersion, string? prerelease) ParseVersionWithPrerelease(string version)
    {
        var dashIndex = version.IndexOf('-', StringComparison.Ordinal);
        if (dashIndex < 0)
        {
            return (version, null);
        }
        return (version[..dashIndex], version[(dashIndex + 1)..]);
    }

    private static int CompareBaseVersions(string v1, string v2)
    {
        var v1Parts = v1.Split('.').Select(s => int.TryParse(s, CultureInfo.InvariantCulture, out var n) ? n : 0).ToArray();
        var v2Parts = v2.Split('.').Select(s => int.TryParse(s, CultureInfo.InvariantCulture, out var n) ? n : 0).ToArray();

        for (var i = 0; i < Math.Max(v1Parts.Length, v2Parts.Length); i++)
        {
            var p1 = i < v1Parts.Length ? v1Parts[i] : 0;
            var p2 = i < v2Parts.Length ? v2Parts[i] : 0;

            if (p1 != p2)
            {
                return p1 - p2;
            }
        }

        return 0;
    }

    [GeneratedRegex(@"\.(\d+)$", RegexOptions.None, 1000)]
    private static partial Regex PrereleaseNumberRegex();

    private static int ComparePrereleases(string pre1, string pre2)
    {
        // Format: "dev.N" - extract the numeric part
        var match1 = PrereleaseNumberRegex().Match(pre1);
        var match2 = PrereleaseNumberRegex().Match(pre2);

        if (match1.Success && match2.Success)
        {
            var num1 = int.Parse(match1.Groups[1].Value, CultureInfo.InvariantCulture);
            var num2 = int.Parse(match2.Groups[1].Value, CultureInfo.InvariantCulture);
            return num1 - num2;
        }

        // Fallback to string comparison
        return string.Compare(pre1, pre2, StringComparison.Ordinal);
    }

    private static bool IsNewerVersion(string latest, string current)
    {
        return CompareVersions(latest, current) > 0;
    }

    private static string GetAssetNameForPlatform()
    {
        if (OperatingSystem.IsWindows())
        {
            return System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                   System.Runtime.InteropServices.Architecture.X86
                ? "mt-win-x86.zip"
                : "mt-win-x64.zip";
        }

        if (OperatingSystem.IsMacOS())
        {
            return System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
                   System.Runtime.InteropServices.Architecture.Arm64
                ? "mt-osx-arm64.tar.gz"
                : "mt-osx-x64.tar.gz";
        }

        return System.Runtime.InteropServices.RuntimeInformation.OSArchitecture ==
               System.Runtime.InteropServices.Architecture.Arm64
            ? "mt-linux-arm64.tar.gz"
            : "mt-linux-x64.tar.gz";
    }

    private static string? GetPlatformFromAssetName(string assetName)
    {
        const string prefix = "mt-";
        if (!assetName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var platform = assetName[prefix.Length..];
        if (platform.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase))
        {
            return platform[..^".tar.gz".Length];
        }

        if (platform.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            return platform[..^".zip".Length];
        }

        return null;
    }

    public static string GetCurrentBinaryPath()
    {
        return Environment.ProcessPath ?? AppContext.BaseDirectory;
    }

    internal static bool IsSharedRuntimeHost(string executablePath)
    {
        return string.Equals(Path.GetFileNameWithoutExtension(executablePath), "dotnet", StringComparison.OrdinalIgnoreCase);
    }

    internal const string SourceUpdateMessage = "This source instance runs through dotnet and cannot install binary updates. Rebuild it with scripts/dev.ps1, or update a packaged tlbx installation.";

    private void NotifyListeners(UpdateInfo update)
    {
        foreach (var listener in _updateListeners.Values)
        {
            try
            {
                listener(update);
            }
            catch
            {
            }
        }
    }

    public async Task<(bool Success, string Message)> ApplyUpdateAsync(SettingsService settingsService, string? source, bool forceFull = false)
    {
        if (Interlocked.CompareExchange(ref _updateInProgress, 1, 0) != 0)
        {
            return (false, "An update is already in progress. Check the update log before retrying.");
        }

        var success = false;
        try
        {
            var result = await ApplyUpdateCoreAsync(settingsService, source, forceFull).ConfigureAwait(false);
            success = result.Success;
            return result;
        }
        finally
        {
            // Keep ownership through the detached installer handoff until this server exits.
            if (!success) Interlocked.Exchange(ref _updateInProgress, 0);
        }
    }

    private async Task<(bool Success, string Message)> ApplyUpdateCoreAsync(SettingsService settingsService, string? source, bool forceFull)
    {
        // A source server must never replace or stop the shared .NET host.
        if (IsSharedRuntimeHost(GetCurrentBinaryPath()))
        {
            return (false, SourceUpdateMessage);
        }

        var artifacts = GetUpdateArtifacts(settingsService);
        ResetUpdateArtifacts(artifacts);
        AppendUpdateLog(artifacts.LogPath, $"Preparing update request (source={source ?? "github"}, forceFull={forceFull})");

        string? extractedDir;
        UpdateType updateType;
        var deleteSourceAfter = true;

        if (source == "local")
        {
            var localUpdate = CheckLocalUpdate();
            extractedDir = GetLocalUpdatePath();
            if (localUpdate is null || string.IsNullOrEmpty(extractedDir))
            {
                return FailUpdate(artifacts, "No local update available");
            }

            updateType = localUpdate.Type;
            deleteSourceAfter = false;
        }
        else
        {
            // Never apply a timer/UI snapshot here. A release can finish publishing
            // after the last background check but before the user presses Update.
            var update = await CheckForUpdateAsync(forceFull).ConfigureAwait(false);
            if (!string.IsNullOrEmpty(update?.CheckError))
                return FailUpdate(artifacts, "Failed to check for updates: " + update.CheckError);
            if (update is null || !update.Available)
            {
                return FailUpdate(artifacts, "No update available");
            }

            extractedDir = await DownloadUpdateAsync(update);
            if (string.IsNullOrEmpty(extractedDir))
            {
                return FailUpdate(artifacts, "Failed to download update");
            }

            // Re-evaluate the authenticated payload, not just discovery metadata.
            var manifestJson = await File.ReadAllTextAsync(Path.Combine(extractedDir, "version.json"));
            var manifest = JsonSerializer.Deserialize(manifestJson, VersionManifestContext.Default.VersionManifest)!;
            updateType = DetermineUpdateType(_installedManifest, manifest);
        }

        if (forceFull)
        {
            updateType = UpdateType.Full;
        }

        AppendUpdateLog(artifacts.LogPath, $"Downloaded update payload to {extractedDir}");
        AppendUpdateLog(artifacts.LogPath, $"Update type: {updateType}");

        var captureResult = await CaptureSessionUpdateStateBestEffortAsync(
            BeforeApplyUpdateAsync,
            updateType).ConfigureAwait(false);
        if (captureResult.Attempted)
        {
            if (captureResult.Captured)
            {
                AppendUpdateLog(artifacts.LogPath, "Captured session update state.");
            }
            else
            {
                AppendUpdateLog(artifacts.LogPath, captureResult.Warning!, "WARN");
            }
        }

        if (updateType == UpdateType.Full)
        {
            if (_appServerControlHostRuntime is not null)
            {
                try
                {
                    var terminatedHosts = await _appServerControlHostRuntime.TerminateAllOwnedHostsAsync().ConfigureAwait(false);
                    AppendUpdateLog(
                        artifacts.LogPath,
                        terminatedHosts == 0
                            ? "No owned mtagenthost runtimes were active before the full update."
                            : string.Create(CultureInfo.InvariantCulture, $"Closed {terminatedHosts} owned mtagenthost runtime(s) before the full update."));
                }
                catch (Exception ex)
                {
                    AppendUpdateLog(
                        artifacts.LogPath,
                        $"Failed to close owned mtagenthost runtimes before full update: {ex.Message}",
                        "WARN");
                }
            }
            else
            {
                AppendUpdateLog(
                    artifacts.LogPath,
                    "No live App Server Controller host runtime service was available; external full-update steps will terminate mtagenthost if needed.");
            }
        }

        var runOutsideServiceCgroup =
            OperatingSystem.IsLinux() &&
            settingsService.IsRunningAsService;

        if (runOutsideServiceCgroup)
        {
            try
            {
                extractedDir = StageLinuxServiceUpdatePayload(
                    extractedDir,
                    settingsService.SettingsDirectory,
                    updateType,
                    deleteSourceAfter,
                    artifacts);
                deleteSourceAfter = true;
                AppendUpdateLog(artifacts.LogPath, $"Prepared durable Linux service update payload at {extractedDir}");
            }
            catch (Exception ex)
            {
                return FailUpdate(artifacts, "Failed to stage Linux service update payload", ex.Message);
            }
        }

        if (OperatingSystem.IsMacOS())
        {
            if (settingsService.IsRunningAsService)
            {
                var staged = TryStageMacOsServiceUpdate(
                    extractedDir,
                    settingsService.SettingsDirectory,
                    updateType,
                    deleteSourceAfter,
                    artifacts);
                if (!staged.Success)
                {
                    return staged;
                }
            }
            else
            {
                var applied = TryApplyUnixUserUpdateInProcess(
                    extractedDir,
                    updateType,
                    deleteSourceAfter,
                    artifacts);
                if (!applied.Success)
                {
                    return applied;
                }
            }

            _ = Task.Run(async () =>
            {
                await Task.Delay(1000);
                if (!settingsService.IsRunningAsService)
                {
                    TrySpawnReplacementProcess();
                }
                Environment.Exit(0);
            });

            return (true, settingsService.IsRunningAsService
                ? "Update staged. Service will restart shortly."
                : "Update installed. Server will restart shortly.");
        }

        string scriptPath;
        try
        {
            scriptPath = UpdateScriptGenerator.GenerateUpdateScript(
                extractedDir,
                GetCurrentBinaryPath(),
                settingsService.SettingsDirectory,
                MidTermServiceIdentity.FromEnvironment(),
                updateType,
                deleteSourceAfter);

            if (runOutsideServiceCgroup)
            {
                scriptPath = StageLinuxServiceUpdateScript(scriptPath, settingsService.SettingsDirectory, artifacts);
            }
        }
        catch (Exception ex)
        {
            return FailUpdate(artifacts, "Failed to prepare update script", ex.Message);
        }

        AppendUpdateLog(artifacts.LogPath, $"Generated external update script at {scriptPath}");
        AppendUpdateLog(
            artifacts.LogPath,
            runOutsideServiceCgroup
                ? "Launching update script via transient systemd unit to survive service shutdown"
                : "Launching update script directly from the current process context");

        // Delay then execute update script
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(3000);
                UpdateScriptGenerator.ExecuteUpdateScript(scriptPath, runOutsideServiceCgroup);
                if (!runOutsideServiceCgroup)
                {
                    Environment.Exit(0);
                }
            }
            catch (Exception ex)
            {
                AppendUpdateLog(artifacts.LogPath, $"Update execution failed: {ex.Message}", "ERROR");
                WriteUpdateResult(artifacts, success: false, "Failed to execute update script", ex.Message);
                Log.Error(() => $"Update execution failed: {ex.Message}");
                Interlocked.Exchange(ref _updateInProgress, 0);
            }
        });

        return (true, "Update started. Server will restart shortly.");
    }

    internal static async Task<(bool Attempted, bool Captured, string? Warning)> CaptureSessionUpdateStateBestEffortAsync(
        Func<UpdateType, CancellationToken, Task>? capture,
        UpdateType updateType)
    {
        if (capture is null)
        {
            return (false, false, null);
        }

        try
        {
            await capture(updateType, CancellationToken.None).ConfigureAwait(false);
            return (true, true, null);
        }
        catch (Exception ex)
        {
            var warning = updateType == UpdateType.Full
                ? $"Failed to capture session update state: {ex.Message}. Continuing requested Full update; active terminals may close without automatic recovery."
                : $"Failed to capture session update state: {ex.Message}. Continuing Web-only update.";
            return (true, false, warning);
        }
    }

    private static (bool Success, string Message) TryApplyLinuxServiceWebOnlyUpdate(
        string extractedDir,
        string settingsDirectory,
        bool deleteSourceAfter,
        UpdateArtifacts artifacts)
    {
        string? backupDir = null;
        string? currentMtPath = null;
        string? currentVersionJsonPath = null;
        string? backupMtPath = null;
        string? backupVersionJsonPath = null;
        Action<string, string> writeLog = (message, level) => AppendUpdateLog(artifacts.LogPath, message, level);

        try
        {
            var installDir = Path.GetDirectoryName(GetCurrentBinaryPath());
            if (string.IsNullOrEmpty(installDir))
            {
                return FailUpdate(artifacts, "Could not determine install directory");
            }

            var newMtPath = Path.Combine(extractedDir, "mt");
            var newVersionJsonPath = Path.Combine(extractedDir, "version.json");
            currentMtPath = Path.Combine(installDir, "mt");
            currentVersionJsonPath = Path.Combine(installDir, "version.json");
            writeLog($"Applying Linux service web-only update in-process from {extractedDir}", "INFO");

            if (!File.Exists(newMtPath) || !File.Exists(newVersionJsonPath))
            {
                return FailUpdate(artifacts, "Downloaded update is incomplete");
            }

            backupDir = Path.Combine(settingsDirectory, "update-backup");
            Directory.CreateDirectory(backupDir);
            writeLog($"Using backup directory {backupDir}", "INFO");

            backupMtPath = Path.Combine(backupDir, "mt.bak");
            backupVersionJsonPath = Path.Combine(backupDir, "version.json.bak");

            if (File.Exists(currentMtPath))
            {
                File.Copy(currentMtPath, backupMtPath, overwrite: true);
                writeLog("Backed up mt", "INFO");
            }

            if (File.Exists(currentVersionJsonPath))
            {
                File.Copy(currentVersionJsonPath, backupVersionJsonPath, overwrite: true);
                writeLog("Backed up version.json", "INFO");
            }

            InstallUnixFileAtomically(newMtPath, currentMtPath, makeExecutable: true, writeLog);
            InstallUnixFileAtomically(newVersionJsonPath, currentVersionJsonPath, makeExecutable: false, writeLog);

            try
            {
                Directory.Delete(backupDir, recursive: true);
            }
            catch
            {
            }

            if (deleteSourceAfter)
            {
                try
                {
                    var tempDir = Path.GetDirectoryName(extractedDir);
                    if (!string.IsNullOrEmpty(tempDir))
                    {
                        Directory.Delete(tempDir, recursive: true);
                    }
                    else
                    {
                        Directory.Delete(extractedDir, recursive: true);
                    }
                }
                catch
                {
                }
            }

            WriteUpdateResult(artifacts, success: true, "Update installed successfully");
            Log.Info(() => "Applied Linux web-only service update in-process; waiting for systemd restart");
            writeLog("Applied Linux web-only service update in-process; waiting for service manager restart", "INFO");
            return (true, "Update installed");
        }
        catch (Exception ex)
        {
            TryRestoreLinuxServiceWebOnlyUpdate(
                currentMtPath,
                backupMtPath,
                currentVersionJsonPath,
                backupVersionJsonPath,
                writeLog);
            WriteUpdateResult(
                artifacts,
                success: false,
                "Failed to install update",
                ex.Message,
                rollbackAttempted: true);
            Log.Error(() => $"Failed to apply Linux web-only service update in-process: {ex.Message}");
            return (false, $"Failed to install update: {ex.Message}");
        }
    }

    private static (bool Success, string Message) TryStageMacOsServiceUpdate(
        string extractedDir,
        string settingsDirectory,
        UpdateType updateType,
        bool deleteSourceAfter,
        UpdateArtifacts artifacts)
    {
        try
        {
            AppendUpdateLog(artifacts.LogPath, "Preparing macOS staged update");
            EnsureMacOsLauncherScript(settingsDirectory, artifacts);

            var stagingDir = Path.Combine(settingsDirectory, "update-staging");
            RecreateDirectory(stagingDir);
            AppendUpdateLog(artifacts.LogPath, $"Staging update in {stagingDir}");

            StageUpdateFile(extractedDir, stagingDir, "mt", artifacts, required: true, makeExecutable: true);
            if (updateType != UpdateType.WebOnly)
            {
                StageUpdateFile(extractedDir, stagingDir, "mthost", artifacts, required: true, makeExecutable: true);
                StageUpdateFile(extractedDir, stagingDir, AgentHostBinaryName, artifacts, required: true, makeExecutable: true);
            }

            StageUpdateFile(extractedDir, stagingDir, "version.json", artifacts, required: true, makeExecutable: false);
            // The effective type can be Full even when the signed release is web-only.
            File.WriteAllText(Path.Combine(stagingDir, "update-type"), updateType == UpdateType.WebOnly ? "webOnly" : "full");

            if (deleteSourceAfter)
            {
                TryDeleteExtractedPayload(extractedDir);
            }

            AppendUpdateLog(artifacts.LogPath, "macOS update staged; launchd wrapper will apply it on restart");
            return (true, "Update staged");
        }
        catch (Exception ex)
        {
            AppendUpdateLog(artifacts.LogPath, $"Failed to stage macOS update: {ex.Message}", "ERROR");
            WriteUpdateResult(artifacts, success: false, "Failed to stage update", ex.Message);
            return (false, $"Failed to stage update: {ex.Message}");
        }
    }

    private static (bool Success, string Message) TryApplyUnixUserUpdateInProcess(
        string extractedDir,
        UpdateType updateType,
        bool deleteSourceAfter,
        UpdateArtifacts artifacts)
    {
        string? backupDir = null;
        string? installDir = null;
        var replacedFiles = new List<(string CurrentPath, string BackupPath, bool MakeExecutable)>();
        Action<string, string> writeLog = (message, level) => AppendUpdateLog(artifacts.LogPath, message, level);

        try
        {
            installDir = Path.GetDirectoryName(GetCurrentBinaryPath());
            if (string.IsNullOrEmpty(installDir))
            {
                return FailUpdate(artifacts, "Could not determine install directory");
            }

            backupDir = Path.Combine(Path.GetTempPath(), $"midterm-update-backup-{Guid.NewGuid():N}");
            Directory.CreateDirectory(backupDir);
            writeLog($"Applying Unix user update in-process from {extractedDir}", "INFO");

            ReplaceUnixManagedFile(extractedDir, installDir, "mt", makeExecutable: true, backupDir, replacedFiles, artifacts, required: true);
            if (updateType != UpdateType.WebOnly)
            {
                ReplaceUnixManagedFile(extractedDir, installDir, "mthost", makeExecutable: true, backupDir, replacedFiles, artifacts, required: true);
                ReplaceUnixManagedFile(extractedDir, installDir, AgentHostBinaryName, makeExecutable: true, backupDir, replacedFiles, artifacts, required: true);
            }

            ReplaceUnixManagedFile(extractedDir, installDir, "version.json", makeExecutable: false, backupDir, replacedFiles, artifacts, required: true);

            if (deleteSourceAfter)
            {
                TryDeleteExtractedPayload(extractedDir);
            }

            WriteUpdateResult(artifacts, success: true, "Update installed successfully");
            writeLog("Unix user update installed in-process; spawning replacement process", "INFO");
            return (true, "Update installed");
        }
        catch (Exception ex)
        {
            writeLog($"Failed to apply Unix user update in-process: {ex.Message}", "ERROR");
            TryRestoreUnixFiles(replacedFiles, writeLog);
            WriteUpdateResult(
                artifacts,
                success: false,
                "Failed to install update",
                ex.Message,
                rollbackAttempted: true);
            return (false, $"Failed to install update: {ex.Message}");
        }
        finally
        {
            if (!string.IsNullOrEmpty(backupDir))
            {
                try
                {
                    Directory.Delete(backupDir, recursive: true);
                }
                catch
                {
                }
            }
        }
    }

    private static void TryRestoreLinuxServiceWebOnlyUpdate(
        string? currentMtPath,
        string? backupMtPath,
        string? currentVersionJsonPath,
        string? backupVersionJsonPath,
        Action<string, string>? writeLog = null)
    {
        try
        {
            if (!string.IsNullOrEmpty(currentMtPath) &&
                !string.IsNullOrEmpty(backupMtPath) &&
                File.Exists(backupMtPath))
            {
                InstallUnixFileAtomically(backupMtPath, currentMtPath, makeExecutable: true, writeLog);
            }
        }
        catch (Exception restoreEx)
        {
            writeLog?.Invoke($"Failed to restore mt after update error: {restoreEx.Message}", "ERROR");
            Log.Error(() => $"Failed to restore mt after update error: {restoreEx.Message}");
        }

        try
        {
            if (!string.IsNullOrEmpty(currentVersionJsonPath) &&
                !string.IsNullOrEmpty(backupVersionJsonPath) &&
                File.Exists(backupVersionJsonPath))
            {
                InstallUnixFileAtomically(backupVersionJsonPath, currentVersionJsonPath, makeExecutable: false, writeLog);
            }
        }
        catch (Exception restoreEx)
        {
            writeLog?.Invoke($"Failed to restore version.json after update error: {restoreEx.Message}", "ERROR");
            Log.Error(() => $"Failed to restore version.json after update error: {restoreEx.Message}");
        }
    }

    internal static void InstallUnixFileAtomically(
        string sourcePath,
        string destinationPath,
        bool makeExecutable,
        Action<string, string>? writeLog = null)
    {
        var tempPath = destinationPath + ".new";

        try
        {
            File.Copy(sourcePath, tempPath, overwrite: true);
            ApplyUnixPermissions(tempPath, makeExecutable);
            File.Move(tempPath, destinationPath, overwrite: true);
            writeLog?.Invoke($"Installed {Path.GetFileName(destinationPath)} via sibling temp swap", "INFO");
            return;
        }
        catch (Exception ex)
        {
            try { File.Delete(tempPath); } catch { }
            writeLog?.Invoke(
                $"Sibling temp swap failed for {destinationPath}: {ex.Message}. Falling back to in-place overwrite.",
                "WARN");
        }

        if (!File.Exists(destinationPath))
        {
            throw new IOException($"Failed to replace {destinationPath}: destination file does not exist and sibling temp swap was not possible.");
        }

        using var sourceStream = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var destinationStream = new FileStream(destinationPath, FileMode.Truncate, FileAccess.Write, FileShare.None);
        sourceStream.CopyTo(destinationStream);
        destinationStream.Flush(flushToDisk: true);
        ApplyUnixPermissions(destinationPath, makeExecutable);

        if (new FileInfo(destinationPath).Length != new FileInfo(sourcePath).Length)
        {
            throw new IOException($"Failed to replace {destinationPath}: destination size did not match source size after in-place overwrite.");
        }

        writeLog?.Invoke($"Installed {Path.GetFileName(destinationPath)} via in-place overwrite fallback", "INFO");
    }

    private static void ApplyUnixPermissions(string path, bool makeExecutable)
    {
        if (!makeExecutable || (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS()))
        {
            return;
        }

        try
        {
            File.SetUnixFileMode(path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch
        {
        }
    }

    private static UpdateArtifacts GetUpdateArtifacts(SettingsService settingsService)
    {
        var isWindowsService = settingsService.IsRunningAsService && OperatingSystem.IsWindows();
        var isUnixService = settingsService.IsRunningAsService && !OperatingSystem.IsWindows();
        var logPath = LogPaths.GetUpdateLogPath(isWindowsService, isUnixService, settingsService.SettingsDirectory);
        var resultPath = Path.Combine(settingsService.SettingsDirectory, "update-result.json");
        return new UpdateArtifacts(logPath, resultPath);
    }

    private static void ResetUpdateArtifacts(UpdateArtifacts artifacts)
    {
        try
        {
            var logDir = Path.GetDirectoryName(artifacts.LogPath);
            if (!string.IsNullOrEmpty(logDir))
            {
                Directory.CreateDirectory(logDir);
            }
        }
        catch
        {
        }

        try
        {
            var resultDir = Path.GetDirectoryName(artifacts.ResultPath);
            if (!string.IsNullOrEmpty(resultDir))
            {
                Directory.CreateDirectory(resultDir);
            }
        }
        catch
        {
        }

        try { File.WriteAllText(artifacts.LogPath, string.Empty); } catch { }
    }

    private static void AppendUpdateLog(string logPath, string message, string level = "INFO")
    {
        try
        {
            var directory = Path.GetDirectoryName(logPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var line = string.Create(CultureInfo.InvariantCulture, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}");
            File.AppendAllText(logPath, line);
        }
        catch
        {
        }
    }

    private static void WriteUpdateResult(
        UpdateArtifacts artifacts,
        bool success,
        string message,
        string details = "",
        bool rollbackAttempted = false)
    {
        try
        {
            var result = new UpdateResult
            {
                Success = success,
                Message = message,
                Details = details,
                Timestamp = DateTime.UtcNow.ToString("O", CultureInfo.InvariantCulture),
                LogFile = artifacts.LogPath,
                RollbackAttempted = rollbackAttempted
            };

            var tempPath = artifacts.ResultPath + ".new";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(result, AppJsonContext.Default.UpdateResult));
            File.Move(tempPath, artifacts.ResultPath, overwrite: true);
        }
        catch (Exception ex)
        {
            AppendUpdateLog(artifacts.LogPath, $"Failed to write update result: {ex.Message}", "ERROR");
        }
    }

    private static (bool Success, string Message) FailUpdate(UpdateArtifacts artifacts, string message, string details = "")
    {
        AppendUpdateLog(artifacts.LogPath, string.IsNullOrEmpty(details) ? message : $"{message}: {details}", "ERROR");
        WriteUpdateResult(artifacts, success: false, message, details);
        return (false, message);
    }

    private static void RecreateDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }

        Directory.CreateDirectory(path);
    }

    internal static string StageLinuxServiceUpdatePayload(
        string extractedDir,
        string settingsDirectory,
        UpdateType updateType,
        bool deleteSourceAfter,
        UpdateArtifacts artifacts)
    {
        AppendUpdateLog(artifacts.LogPath, "Preparing durable Linux service update staging");

        var stagingRoot = Path.Combine(settingsDirectory, "update-staging");
        var payloadDir = Path.Combine(stagingRoot, "payload");
        RecreateDirectory(stagingRoot);
        Directory.CreateDirectory(payloadDir);
        AppendUpdateLog(artifacts.LogPath, $"Staging Linux service update payload in {payloadDir}");

        StageUpdateFile(extractedDir, payloadDir, "mt", artifacts, required: true, makeExecutable: true);
        if (updateType != UpdateType.WebOnly)
        {
            StageUpdateFile(extractedDir, payloadDir, "mthost", artifacts, required: true, makeExecutable: true);
            StageUpdateFile(extractedDir, payloadDir, AgentHostBinaryName, artifacts, required: true, makeExecutable: true);
        }

        StageUpdateFile(extractedDir, payloadDir, "version.json", artifacts, required: true, makeExecutable: false);

        if (deleteSourceAfter)
        {
            TryDeleteExtractedPayload(extractedDir);
            AppendUpdateLog(artifacts.LogPath, $"Deleted downloaded payload after staging: {extractedDir}");
        }

        return payloadDir;
    }

    internal static string StageLinuxServiceUpdateScript(string scriptPath, string settingsDirectory, UpdateArtifacts artifacts)
    {
        var stagingRoot = Path.Combine(settingsDirectory, "update-staging");
        Directory.CreateDirectory(stagingRoot);

        var stagedScriptPath = Path.Combine(stagingRoot, Path.GetFileName(scriptPath));
        File.Copy(scriptPath, stagedScriptPath, overwrite: true);
        ApplyUnixPermissions(stagedScriptPath, makeExecutable: true);
        AppendUpdateLog(artifacts.LogPath, $"Staged Linux service update script at {stagedScriptPath}");

        try
        {
            File.Delete(scriptPath);
        }
        catch
        {
        }

        return stagedScriptPath;
    }

    private static void StageUpdateFile(
        string extractedDir,
        string stagingDir,
        string fileName,
        UpdateArtifacts artifacts,
        bool required,
        bool makeExecutable)
    {
        var sourcePath = Path.Combine(extractedDir, fileName);
        if (!File.Exists(sourcePath))
        {
            if (required)
            {
                throw new IOException($"Downloaded update is missing required file: {fileName}");
            }

            return;
        }

        var tempPath = Path.Combine(stagingDir, fileName + ".tmp");
        var destinationPath = Path.Combine(stagingDir, fileName);
        File.Copy(sourcePath, tempPath, overwrite: true);
        ApplyUnixPermissions(tempPath, makeExecutable);
        File.Move(tempPath, destinationPath, overwrite: true);
        AppendUpdateLog(artifacts.LogPath, $"Staged {fileName}");
    }

    private static void ReplaceUnixManagedFile(
        string extractedDir,
        string installDir,
        string fileName,
        bool makeExecutable,
        string backupDir,
        List<(string CurrentPath, string BackupPath, bool MakeExecutable)> replacedFiles,
        UpdateArtifacts artifacts,
        bool required)
    {
        var sourcePath = Path.Combine(extractedDir, fileName);
        var destinationPath = Path.Combine(installDir, fileName);
        if (!File.Exists(sourcePath))
        {
            if (required)
            {
                throw new IOException($"Downloaded update is missing required file: {fileName}");
            }

            return;
        }

        if (File.Exists(destinationPath))
        {
            var backupPath = Path.Combine(backupDir, fileName + ".bak");
            File.Copy(destinationPath, backupPath, overwrite: true);
            replacedFiles.Add((destinationPath, backupPath, makeExecutable));
            AppendUpdateLog(artifacts.LogPath, $"Backed up {fileName}");
        }

        InstallUnixFileAtomically(
            sourcePath,
            destinationPath,
            makeExecutable,
            (message, level) => AppendUpdateLog(artifacts.LogPath, message, level));
    }

    private static void TryRestoreUnixFiles(
        IEnumerable<(string CurrentPath, string BackupPath, bool MakeExecutable)> replacedFiles,
        Action<string, string> writeLog)
    {
        foreach (var (currentPath, backupPath, makeExecutable) in replacedFiles.Reverse())
        {
            try
            {
                if (!File.Exists(backupPath))
                {
                    continue;
                }

                InstallUnixFileAtomically(backupPath, currentPath, makeExecutable, writeLog);
                writeLog($"Restored {Path.GetFileName(currentPath)} from backup", "INFO");
            }
            catch (Exception ex)
            {
                writeLog($"Failed to restore {currentPath}: {ex.Message}", "ERROR");
            }
        }
    }

    private static void TryDeleteExtractedPayload(string extractedDir)
    {
        try
        {
            var tempDir = Path.GetDirectoryName(extractedDir);
            if (!string.IsNullOrEmpty(tempDir))
            {
                Directory.Delete(tempDir, recursive: true);
            }
            else
            {
                Directory.Delete(extractedDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static void EnsureMacOsLauncherScript(string settingsDirectory, UpdateArtifacts artifacts)
    {
        var launcherPath = Path.Combine(settingsDirectory, "launcher.sh");
        File.WriteAllText(launcherPath, GetMacOsLauncherScriptContents(settingsDirectory, artifacts.LogPath));
        ApplyUnixPermissions(launcherPath, makeExecutable: true);
        AppendUpdateLog(artifacts.LogPath, $"Refreshed macOS launcher shim at {launcherPath}");
    }

    internal static string GetAgentHostFallbackPath(string settingsDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsDirectory);
        return Path.Combine(settingsDirectory, AgentHostBinaryName);
    }

    internal static string GetMacOsLauncherScriptContents(string settingsDirectory, string updateLogPath)
    {
        var installDir = Path.GetDirectoryName(GetCurrentBinaryPath()) ?? "/usr/local/bin";
        var stagingDir = Path.Combine(settingsDirectory, "update-staging");
        var resultPath = Path.Combine(settingsDirectory, "update-result.json");
        var backupDir = Path.Combine(settingsDirectory, "update-backup");
        var agentHostFallbackPath = GetAgentHostFallbackPath(settingsDirectory);
        return $@"#!/bin/bash
set -euo pipefail

CONFIG_DIR='{EscapeForBash(settingsDirectory)}'
INSTALL_DIR='{EscapeForBash(installDir)}'
CONFIG_AGENTHOST='{EscapeForBash(agentHostFallbackPath)}'
STAGING='{EscapeForBash(stagingDir)}'
LOG_FILE='{EscapeForBash(updateLogPath)}'
RESULT_FILE='{EscapeForBash(resultPath)}'
BACKUP_DIR='{EscapeForBash(backupDir)}'

mkdir -p ""$(dirname ""$LOG_FILE"")"" 2>/dev/null || true
exec >> ""$LOG_FILE"" 2>&1 < /dev/null

log() {{
    local level=""${{2:-INFO}}""
    local timestamp
    timestamp=$(date '+%Y-%m-%d %H:%M:%S.%3N')
    echo ""[$timestamp] [$level] $1""
}}

write_result() {{
    local success=""$1""
    local message=""$2""
    local details=""${{3:-}}""
    cat > ""$RESULT_FILE"" << RESULT_EOF
{{
  ""success"": $success,
  ""message"": ""$message"",
  ""details"": ""$details"",
  ""timestamp"": ""$(date -u '+%Y-%m-%dT%H:%M:%SZ')"",
  ""logFile"": ""$LOG_FILE""
}}
RESULT_EOF
}}

staged_update_is_web_only() {{
    [[ -f ""$STAGING/update-type"" ]] && [[ ""$(cat ""$STAGING/update-type"")"" == ""webOnly"" ]]
}}

resolve_agenthost_target() {{
    local primary=""$INSTALL_DIR/{AgentHostBinaryName}""
    if [[ -f ""$primary"" ]]; then
        echo ""$primary""
        return 0
    fi

    mkdir -p ""$(dirname ""$CONFIG_AGENTHOST"")"" 2>/dev/null || true
    log ""System install is missing {AgentHostBinaryName}; using writable fallback at $CONFIG_AGENTHOST"" >&2
    echo ""$CONFIG_AGENTHOST""
}}

apply_file() {{
    local src=""$1""
    local dst=""$2""
    local desc=""$3""
    local make_exec=""${{4:-false}}""

    if [[ ! -f ""$src"" ]] || [[ ! -s ""$src"" ]]; then
        log ""Missing or empty staged $desc: $src"" ""ERROR""
        return 1
    fi

    if [[ -f ""$dst"" ]]; then
        cat ""$src"" > ""$dst""
    else
        cp ""$src"" ""$dst""
    fi

    if [[ ""$make_exec"" == ""true"" ]]; then
        chmod +x ""$dst""
    fi

    if [[ ! -s ""$dst"" ]]; then
        log ""Installed $desc is empty after copy: $dst"" ""ERROR""
        return 1
    fi

    log ""Installed $desc""
}}

rollback() {{
    if [[ ! -d ""$BACKUP_DIR"" ]]; then
        return
    fi

    log ""Rolling back staged macOS update"" ""WARN""
    [[ -f ""$BACKUP_DIR/mt.bak"" ]] && cat ""$BACKUP_DIR/mt.bak"" > ""$INSTALL_DIR/mt"" && chmod +x ""$INSTALL_DIR/mt"" || true
    [[ -f ""$BACKUP_DIR/mthost.bak"" ]] && cat ""$BACKUP_DIR/mthost.bak"" > ""$INSTALL_DIR/mthost"" && chmod +x ""$INSTALL_DIR/mthost"" || true
    if [[ -f ""$BACKUP_DIR/{AgentHostBinaryName}.bak"" ]]; then
        cat ""$BACKUP_DIR/{AgentHostBinaryName}.bak"" > ""${{AGENTHOST_DST:-$INSTALL_DIR/{AgentHostBinaryName}}}"" && chmod +x ""${{AGENTHOST_DST:-$INSTALL_DIR/{AgentHostBinaryName}}}"" || true
    elif [[ -n ""${{AGENTHOST_DST:-}}"" ]] && [[ ""$AGENTHOST_DST"" != ""$INSTALL_DIR/{AgentHostBinaryName}"" ]]; then
        rm -f ""$AGENTHOST_DST"" 2>/dev/null || true
    fi
    [[ -f ""$BACKUP_DIR/version.json.bak"" ]] && cat ""$BACKUP_DIR/version.json.bak"" > ""$INSTALL_DIR/version.json"" || true
}}

if [[ -d ""$STAGING"" ]] && [[ -f ""$STAGING/mt"" ]]; then
    STAGED_IS_WEB_ONLY=false
    if staged_update_is_web_only; then
        STAGED_IS_WEB_ONLY=true
    fi

    rm -rf ""$BACKUP_DIR"" 2>/dev/null || true
    mkdir -p ""$BACKUP_DIR""
    rm -f ""$RESULT_FILE"" 2>/dev/null || true

    log ""Applying staged macOS update from $STAGING""
    log ""Staged update type: $(if [[ ""$STAGED_IS_WEB_ONLY"" == ""true"" ]]; then echo 'WebOnly'; else echo 'Full'; fi)""

    AGENTHOST_DST=""$INSTALL_DIR/{AgentHostBinaryName}""
    if [[ ""$STAGED_IS_WEB_ONLY"" == ""false"" ]]; then
        AGENTHOST_DST=""$(resolve_agenthost_target)""
    fi

    [[ -f ""$INSTALL_DIR/mt"" ]] && cp -f ""$INSTALL_DIR/mt"" ""$BACKUP_DIR/mt.bak""
    [[ ""$STAGED_IS_WEB_ONLY"" == ""false"" ]] && [[ -f ""$INSTALL_DIR/mthost"" ]] && cp -f ""$INSTALL_DIR/mthost"" ""$BACKUP_DIR/mthost.bak""
    [[ ""$STAGED_IS_WEB_ONLY"" == ""false"" ]] && [[ -f ""$AGENTHOST_DST"" ]] && cp -f ""$AGENTHOST_DST"" ""$BACKUP_DIR/{AgentHostBinaryName}.bak""
    [[ -f ""$INSTALL_DIR/version.json"" ]] && cp -f ""$INSTALL_DIR/version.json"" ""$BACKUP_DIR/version.json.bak""

    if apply_file ""$STAGING/mt"" ""$INSTALL_DIR/mt"" ""mt"" true \
        && {{ [[ ""$STAGED_IS_WEB_ONLY"" == ""true"" ]] || apply_file ""$STAGING/mthost"" ""$INSTALL_DIR/mthost"" ""mthost"" true; }} \
        && {{ [[ ""$STAGED_IS_WEB_ONLY"" == ""true"" ]] || apply_file ""$STAGING/{AgentHostBinaryName}"" ""$AGENTHOST_DST"" ""{AgentHostBinaryName}"" true; }} \
        && apply_file ""$STAGING/version.json"" ""$INSTALL_DIR/version.json"" ""version.json"" false; then
        write_result true ""Update applied""
        rm -rf ""$STAGING"" ""$BACKUP_DIR"" 2>/dev/null || true
        log ""macOS staged update applied successfully""
    else
        rollback
        write_result false ""Failed to apply staged update"" ""See update log for details""
        log ""macOS staged update failed; previous binaries restored"" ""ERROR""
    fi
fi

exec ""$INSTALL_DIR/mt"" ""$@""
";
    }

    private static string EscapeForBash(string value)
    {
        return value.Replace("'", "'\\''", StringComparison.Ordinal);
    }

    private static void TrySpawnReplacementProcess()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
            {
                return;
            }

            var cliArgs = Environment.GetCommandLineArgs();
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = exePath,
                WorkingDirectory = Path.GetDirectoryName(exePath) ?? ".",
                UseShellExecute = false,
                RedirectStandardInput = true
            };

            foreach (var arg in cliArgs.Skip(1))
            {
                psi.ArgumentList.Add(arg);
            }

            using var process = System.Diagnostics.Process.Start(psi);
            if (process is not null)
            {
                try { process.StandardInput.Close(); } catch { }
            }
        }
        catch
        {
        }
    }

    public static UpdateResult? ReadUpdateResult(string settingsDirectory, bool clear = false)
    {
        var resultPath = Path.Combine(settingsDirectory, "update-result.json");
        if (!File.Exists(resultPath))
        {
            return null;
        }

        try
        {
            var json = File.ReadAllText(resultPath);
            var result = JsonSerializer.Deserialize<UpdateResult>(json, AppJsonContext.Default.UpdateResult);
            if (result is not null)
            {
                result.Found = true;
                if (clear)
                {
                    try { File.Delete(resultPath); } catch { }
                }
                return result;
            }
        }
        catch
        {
        }

        return null;
    }

    public static void ClearUpdateResult(string settingsDirectory)
    {
        var resultPath = Path.Combine(settingsDirectory, "update-result.json");
        if (File.Exists(resultPath))
        {
            try { File.Delete(resultPath); } catch { }
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _checkTimer.Dispose();
        _updateListeners.Clear();
    }
}

internal sealed class GitHubRelease
{
    public string? TagName { get; set; }
    public string? HtmlUrl { get; set; }
    public string? Body { get; set; }
    public bool Draft { get; set; }
    public bool Prerelease { get; set; }
    public List<GitHubAsset>? Assets { get; set; }
}

internal readonly record struct UpdateArtifacts(string LogPath, string ResultPath);

internal sealed class GitHubAsset
{
    public string? Name { get; set; }
    public string? BrowserDownloadUrl { get; set; }
    public long Size { get; set; }
    public string? Digest { get; set; }
}

internal sealed class ReleaseSelection
{
    public GitHubRelease Release { get; init; } = null!;
    public string DownloadUrl { get; init; } = "";
    public string ArchiveSha256 { get; init; } = "";
    public long ArchiveSize { get; init; }
    public bool IsDowngrade { get; set; }
}

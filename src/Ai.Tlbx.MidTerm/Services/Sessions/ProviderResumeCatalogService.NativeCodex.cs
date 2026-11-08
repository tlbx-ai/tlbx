using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Process;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed partial class ProviderResumeCatalogService
{
    private Func<string?, CancellationToken, Task<IReadOnlyList<ProviderResumeCatalogEntryDto>?>>? _nativeCodex;
    private int _nativeCatalogActive;

    private static bool IsCatalogRunningAsSystem()
    {
#if WINDOWS
        return OperatingSystem.IsWindows() && TtyHostSpawner.IsRunningAsSystem();
#else
        return false;
#endif
    }

    public async Task<IReadOnlyList<ProviderResumeCatalogEntryDto>> GetCandidatesAsync(
        string provider, string? workingDirectory, bool includeAllDirectories, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        var cwd = includeAllDirectories ? null : NormalizePathOrNull(workingDirectory);
        if (!includeAllDirectories && cwd is null) return [];
        if (string.Equals(provider, "codex", StringComparison.OrdinalIgnoreCase) && _nativeCodex is not null)
        {
            if (Interlocked.CompareExchange(ref _nativeCatalogActive, 1, 0) == 0)
            {
                try
                {
                    var native = await _nativeCodex(cwd, ct).ConfigureAwait(false);
                    if (native is not null) return native;
                }
                finally { Volatile.Write(ref _nativeCatalogActive, 0); }
            }
        }
        return GetCandidates(provider, workingDirectory, includeAllDirectories, ct);
    }

    // Deliberately uncached: explicit picker refresh observes current provider metadata and
    // cannot reuse another identity/CODEX_HOME/cwd result or a stale file-mtime cache entry.
    internal static async Task<IReadOnlyList<ProviderResumeCatalogEntryDto>?> ReadNativeCodexAsync(
        MidTermSettings settings, string settingsDirectory, string? cwd,
        SessionAppServerControlHostRuntimeService.RedirectedProcessLauncher launcher,
        Func<bool> isSystem, CancellationToken ct,
        Func<string?>? resolveHost = null)
    {
        var system = isSystem();
        if (!CanLaunchNativeCatalog(settings, system)) return null;
        if (!system && !MatchesConfiguredInteractiveUser(settings)) return null;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(10));
        TtyHostSpawner.RedirectedProcessHandle? handle = null;
        Task<string>? stderr = null;
        try
        {
            var profile = ResolveNativeCodexProfileDirectory(settings);
            if (string.IsNullOrWhiteSpace(profile) || !Directory.Exists(profile)) return null;
            var host = resolveHost is null
                ? SessionAppServerControlHostRuntimeService.ResolveConfiguredHostPath() ??
                  SessionAppServerControlHostRuntimeService.ResolveInstalledHostExecutablePath(settingsDirectory)
                : resolveHost();
            if (host is null) return null;
            var args = new List<string>();
            var fileName = host;
            if (Path.GetExtension(host).Equals(".dll", StringComparison.OrdinalIgnoreCase))
            {
                fileName = AiCliCommandLocator.FindExecutableInPath("dotnet") ?? "dotnet";
                args.Add(host);
            }
            args.Add("--codex-thread-list");
            if (cwd is not null) { args.Add("--cwd"); args.Add(cwd); }
            SessionAppServerControlHostRuntimeService.BuildLaunchEnvironment(
                settings, host, profile, null, out var environment, out var path);
            if (!launcher(fileName, args, profile, environment, path, settings.RunAsUser,
                    settings.RunAsUserSid, out handle, out _) || handle is null) return null;
            stderr = BoundedProcessText.DrainTailAsync(handle.Error, timeout.Token);
            handle.Input.Close();
            var output = new BoundedProcessText(handle.Output, 1024 * 1024);
            var line = await output.ReadLineAsync(timeout.Token).ConfigureAwait(false);
            if (line is null || await output.ReadLineAsync(timeout.Token).ConfigureAwait(false) is not null) return null;
            await handle.Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await stderr.ConfigureAwait(false);
            if (handle.Process.ExitCode != 0) return null;
            return ParseNativeCodexCatalog(line, cwd);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
                                   InvalidOperationException or ArgumentException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
        finally
        {
            if (handle is not null) await BoundedProcessText.StopAsync(handle.Process).ConfigureAwait(false);
            await timeout.CancelAsync().ConfigureAwait(false);
            if (stderr is not null)
            {
                try { await stderr.ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or InvalidDataException or OperationCanceledException) { }
            }
            handle?.Dispose();
        }
    }

    internal static bool CanLaunchNativeCatalog(MidTermSettings settings, bool isSystem) =>
        !isSystem || !string.IsNullOrWhiteSpace(settings.RunAsUser) || !string.IsNullOrWhiteSpace(settings.RunAsUserSid);

    internal static string? ResolveNativeCodexProfileDirectory(MidTermSettings settings)
    {
        if (OperatingSystem.IsWindows() &&
            (!string.IsNullOrWhiteSpace(settings.RunAsUser) || !string.IsNullOrWhiteSpace(settings.RunAsUserSid)))
        {
            // A configured but unresolved identity must not fall through to the
            // executable owner's or SYSTEM's profile. SID-only settings count too.
            return AppServerControlHostEnvironmentResolver.ResolveWindowsProfileDirectory(settings.RunAsUser, settings.RunAsUserSid);
        }
        return ResolveProviderProfileDirectory(settings, "codex");
    }

    private static bool MatchesConfiguredInteractiveUser(MidTermSettings settings)
    {
        // The redirected launcher impersonates under SYSTEM (or Unix root), not an
        // arbitrary interactive Windows account. Never silently query that account instead.
        if (OperatingSystem.IsWindows())
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            return MatchesConfiguredWindowsUser(settings, identity.User?.Value, identity.Name);
        }
        return Environment.UserName == "root" || string.IsNullOrWhiteSpace(settings.RunAsUser) ||
               string.Equals(settings.RunAsUser, Environment.UserName, StringComparison.Ordinal);
    }

    internal static bool MatchesConfiguredWindowsUser(MidTermSettings settings, string? sid, string? name)
    {
        if (!string.IsNullOrWhiteSpace(settings.RunAsUserSid))
            return string.Equals(settings.RunAsUserSid, sid, StringComparison.OrdinalIgnoreCase);
        return string.IsNullOrWhiteSpace(settings.RunAsUser) ||
               string.Equals(settings.RunAsUser, name, StringComparison.OrdinalIgnoreCase) ||
               (!settings.RunAsUser.Contains('\\', StringComparison.Ordinal) &&
                string.Equals(settings.RunAsUser, name?.Split('\\').Last(), StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<ProviderResumeCatalogEntryDto> ParseNativeCodexCatalog(string text, string? cwd)
    {
        using var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.Array || document.RootElement.GetArrayLength() > MaxCandidates)
            throw new InvalidDataException("Invalid catalog array.");
        var result = new List<ProviderResumeCatalogEntryDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        using var entries = document.RootElement.EnumerateArray();
        foreach (var thread in entries)
        {
            if (thread.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid catalog entry.");
            var id = TryGetString(thread, "id");
            var directory = TryGetString(thread, "cwd");
            if (id is null || directory is null || id.Length > 256 || directory.Length > 32768 ||
                !thread.TryGetProperty("updatedAt", out var updated) || !updated.TryGetInt64(out var seconds))
                throw new InvalidDataException("Incomplete catalog metadata.");
            if (!seen.Add(id) || (cwd is not null &&
                !string.Equals(cwd, NormalizePathOrNull(directory),
                    OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))) continue;
            var metadata = CreateMetadata(id, directory,
                RealPromptOrNull(TryGetString(thread, "preview")),
                ReadNativeLatestUserText(thread), DateTimeOffset.FromUnixTimeSeconds(seconds).UtcDateTime,
                TryGetString(thread, "name"));
            result.Add(new ProviderResumeCatalogEntryDto
            {
                Provider = "codex",
                SessionId = id,
                WorkingDirectory = directory,
                Title = metadata.Title,
                PreviewText = metadata.PreviewText,
                UpdatedAtUtc = metadata.UpdatedAtUtc
            });
        }
        return result.OrderByDescending(entry => entry.UpdatedAtUtc).ToList();
    }

    private static string? ReadNativeLatestUserText(JsonElement thread)
    {
        if (!thread.TryGetProperty("latestUserTexts", out var texts) || texts.ValueKind != JsonValueKind.Array) return null;
        for (var index = 0; index < Math.Min(texts.GetArrayLength(), 16); index++)
        {
            var text = texts[index];
            if (text.ValueKind == JsonValueKind.String && RealPromptOrNull(text.GetString()) is { } prompt) return prompt;
        }
        return null;
    }
}

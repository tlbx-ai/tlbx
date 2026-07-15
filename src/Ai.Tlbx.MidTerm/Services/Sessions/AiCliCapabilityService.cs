using System.Diagnostics;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed class AiCliCapabilityService
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private readonly Lock _syncRoot = new();
    private readonly Dictionary<string, CachedSnapshot> _cache = new(StringComparer.Ordinal);
    private readonly SettingsService? _settingsService;

    public AiCliCapabilityService(SettingsService? settingsService = null)
    {
        _settingsService = settingsService;
    }

    public async Task<AiCliCapabilitySnapshot> DescribeAsync(string profile, bool appServerControlOnly = false, CancellationToken ct = default)
    {
        var cacheKey = $"{profile}\n{(appServerControlOnly ? "appServerControl" : "terminal")}";
        CachedSnapshot? cached;
        lock (_syncRoot)
        {
            if (_cache.TryGetValue(cacheKey, out cached) &&
                cached.ExpiresAt > DateTimeOffset.UtcNow)
            {
                return cached.CloneSnapshot();
            }
        }

        var snapshot = await BuildSnapshotAsync(profile, appServerControlOnly, ResolveConfiguredUserProfileDirectory(), ct).ConfigureAwait(false);
        var next = new CachedSnapshot(DateTimeOffset.UtcNow.Add(CacheLifetime), snapshot);
        lock (_syncRoot)
        {
            _cache[cacheKey] = next;
        }

        return next.CloneSnapshot();
    }

    private static async Task<AiCliCapabilitySnapshot> BuildSnapshotAsync(string profile, bool appServerControlOnly, string? userProfileDirectory, CancellationToken ct)
    {
        return profile switch
        {
            AiCliProfileService.CodexProfile => await BuildCodexSnapshotAsync(userProfileDirectory, appServerControlOnly, ct).ConfigureAwait(false),
            AiCliProfileService.ClaudeProfile => await BuildClaudeSnapshotAsync(userProfileDirectory, appServerControlOnly, ct).ConfigureAwait(false),
            AiCliProfileService.GrokProfile => await BuildGrokSnapshotAsync(userProfileDirectory, appServerControlOnly, ct).ConfigureAwait(false),
            AiCliProfileService.OpenCodeProfile => BuildOpenCodeSnapshot(),
            AiCliProfileService.GenericAiProfile => BuildGenericSnapshot(),
            AiCliProfileService.ShellProfile => BuildShellSnapshot(),
            _ => BuildUnknownSnapshot(profile)
        };
    }

    private static async Task<AiCliCapabilitySnapshot> BuildCodexSnapshotAsync(string? userProfileDirectory, bool appServerControlOnly, CancellationToken ct)
    {
        var binaryPath = AiCliCommandLocator.FindExecutableInPath("codex", userProfileDirectory);
        if (binaryPath is null)
        {
            if (appServerControlOnly)
            {
                return BuildSnapshot(
                    "native-required",
                    "attention",
                    "App Server Controller runtime unavailable",
                    "Explicit Codex App Server Controller sessions require the Codex CLI plus its structured app-server runtime on this machine.",
                    [
                        CreateCapability("cli", "Codex CLI", "missing", "Missing", "tlbx could not find `codex` on PATH."),
                        CreateCapability("native", "Codex app-server", "missing", "Missing", "Without `codex app-server`, this explicit App Server Controller session cannot become live."),
                        CreateCapability("terminal", "Terminal", "absent", "Absent", "Explicit App Server Controller sessions do not own an `mthost` terminal.")
                    ]);
            }

            return BuildSnapshot(
                "fallback-only",
                "fallback",
                "Fallback only",
                "tlbx is rendering this Codex lane from supervisor state and terminal telemetry because the Codex CLI is not available on this machine.",
                [
                    CreateCapability("cli", "Codex CLI", "missing", "Missing", "tlbx could not find `codex` on PATH."),
                    CreateCapability("native", "Native events", "missing", "Missing", "Without the Codex CLI, there is no app-server lane to attach yet."),
                    CreateCapability("terminal", "Terminal fallback", "ready", "Ready", "xterm stays fully available and remains the source of truth.")
                ]);
        }

        var probe = await ProbeAsync(binaryPath, "app-server --help", ct).ConfigureAwait(false);
        if (probe.Success)
        {
            if (appServerControlOnly)
            {
                return BuildSnapshot(
                    "native-ready",
                    "positive",
                    "App Server Controller runtime ready",
                    "This explicit Codex App Server Controller session can attach through `mtagenthost` to Codex's structured app-server runtime.",
                    [
                        CreateCapability("cli", "Codex CLI", "ready", "Ready", $"Using `{binaryPath}`."),
                        CreateCapability("native", "Codex app-server", "ready", "Ready", "The structured Codex runtime is available for explicit App Server Controller sessions."),
                        CreateCapability("terminal", "Terminal", "absent", "Absent", "Explicit App Server Controller sessions do not own an `mthost` terminal.")
                    ]);
            }

            return BuildSnapshot(
                "fallback-ready",
                "positive",
                "Fallback now, native-ready",
                "tlbx is still rendering this Codex lane from terminal signals, but `codex app-server` is available here so a native event feed can be attached later without changing the UI or replacing xterm.",
                [
                    CreateCapability("cli", "Codex CLI", "ready", "Ready", $"Using `{binaryPath}`."),
                    CreateCapability("native", "Codex app-server", "ready", "Ready", "The native Codex lane can be wired in on this machine."),
                    CreateCapability("terminal", "Terminal fallback", "ready", "Ready", "xterm remains available beside the Agent view.")
                ]);
        }

        if (appServerControlOnly)
        {
            return BuildSnapshot(
                "native-gated",
                "warning",
                "App Server Controller runtime blocked",
                "Codex CLI exists, but `codex app-server` did not answer cleanly, so this explicit App Server Controller session cannot become live yet.",
                [
                    CreateCapability("cli", "Codex CLI", "ready", "Ready", $"Using `{binaryPath}`."),
                    CreateCapability("native", "Codex app-server", "gated", "Gated", BuildProbeDetail(probe, "The structured Codex runtime is not reliably available yet.")),
                    CreateCapability("terminal", "Terminal", "absent", "Absent", "Explicit App Server Controller sessions do not own an `mthost` terminal.")
                ]);
        }

        return BuildSnapshot(
            "fallback-gated",
            "warning",
            "Fallback with upgrade gap",
            "tlbx is rendering this Codex lane from terminal signals. The Codex CLI exists, but `codex app-server` did not answer cleanly, so native events stay gated for now.",
            [
                CreateCapability("cli", "Codex CLI", "ready", "Ready", $"Using `{binaryPath}`."),
                CreateCapability("native", "Codex app-server", "gated", "Gated", BuildProbeDetail(probe, "The native Codex lane is not reliably available yet.")),
                CreateCapability("terminal", "Terminal fallback", "ready", "Ready", "The terminal lane remains active and unaffected.")
            ]);
    }

    private static async Task<AiCliCapabilitySnapshot> BuildClaudeSnapshotAsync(string? userProfileDirectory, bool appServerControlOnly, CancellationToken ct)
    {
        var binaryPath = AiCliCommandLocator.FindExecutableInPath("claude", userProfileDirectory);
        if (binaryPath is null)
        {
            if (appServerControlOnly)
            {
                return BuildSnapshot(
                    "native-required",
                    "attention",
                    "App Server Controller runtime unavailable",
                    "Explicit Claude App Server Controller sessions require the Claude CLI plus its structured runtime support on this machine.",
                    [
                        CreateCapability("cli", "Claude CLI", "missing", "Missing", "tlbx could not find `claude` on PATH."),
                        CreateCapability("native", "Claude structured runtime", "missing", "Missing", "Without structured Claude runtime support, this explicit App Server Controller session cannot become live."),
                        CreateCapability("terminal", "Terminal", "absent", "Absent", "Explicit App Server Controller sessions do not own an `mthost` terminal.")
                    ]);
            }

            return BuildSnapshot(
                "fallback-only",
                "fallback",
                "Fallback only",
                "tlbx is rendering this Claude lane from supervisor state and terminal telemetry because the Claude CLI is not available on this machine.",
                [
                    CreateCapability("cli", "Claude CLI", "missing", "Missing", "tlbx could not find `claude` on PATH."),
                    CreateCapability("native", "Claude SDK lane", "missing", "Missing", "Without the Claude CLI, there is no SDK-backed lane to attach."),
                    CreateCapability("terminal", "Terminal fallback", "ready", "Ready", "xterm stays fully available and remains the source of truth.")
                ]);
        }

        var probe = await ProbeAsync(binaryPath, "--help", ct).ConfigureAwait(false);
        var advertisesSdk = probe.Output.Contains("--sdk-mode", StringComparison.OrdinalIgnoreCase) ||
                            probe.Output.Contains("sdk mode", StringComparison.OrdinalIgnoreCase);

        if (probe.Success && advertisesSdk)
        {
            if (appServerControlOnly)
            {
                return BuildSnapshot(
                    "native-ready",
                    "positive",
                    "App Server Controller runtime ready",
                    "This explicit Claude App Server Controller session can attach through `mtagenthost` to Claude's structured runtime.",
                    [
                        CreateCapability("cli", "Claude CLI", "ready", "Ready", $"Using `{binaryPath}`."),
                        CreateCapability("native", "Claude structured runtime", "ready", "Ready", "This machine advertises structured Claude runtime support."),
                        CreateCapability("terminal", "Terminal", "absent", "Absent", "Explicit App Server Controller sessions do not own an `mthost` terminal.")
                    ]);
            }

            return BuildSnapshot(
                "fallback-ready",
                "positive",
                "Fallback now, native-ready",
                "tlbx is still rendering this Claude lane from terminal signals, but the installed CLI advertises `--sdk-mode`, so a native event feed can be added later without changing the UI or replacing xterm.",
                [
                    CreateCapability("cli", "Claude CLI", "ready", "Ready", $"Using `{binaryPath}`."),
                    CreateCapability("native", "Claude SDK lane", "ready", "Ready", "This machine advertises `--sdk-mode`."),
                    CreateCapability("terminal", "Terminal fallback", "ready", "Ready", "xterm remains available beside the Agent view.")
                ]);
        }

        if (appServerControlOnly)
        {
            return BuildSnapshot(
                "native-gated",
                "warning",
                "App Server Controller runtime blocked",
                "Claude CLI exists, but structured runtime support is not available cleanly enough for this explicit App Server Controller session yet.",
                [
                    CreateCapability("cli", "Claude CLI", "ready", "Ready", $"Using `{binaryPath}`."),
                    CreateCapability("native", "Claude structured runtime", "gated", "Gated", advertisesSdk
                        ? "The CLI mentions structured runtime support, but the probe did not complete cleanly."
                        : "This CLI build does not clearly advertise structured runtime support yet."),
                    CreateCapability("terminal", "Terminal", "absent", "Absent", "Explicit App Server Controller sessions do not own an `mthost` terminal.")
                ]);
        }

        return BuildSnapshot(
            "fallback-gated",
            "warning",
            "Fallback with upgrade gap",
            "tlbx is rendering this Claude lane from terminal signals. The Claude CLI exists, but a safe SDK lane is not advertised here yet, so native events stay gated for now.",
            [
                CreateCapability("cli", "Claude CLI", "ready", "Ready", $"Using `{binaryPath}`."),
                CreateCapability("native", "Claude SDK lane", "gated", "Gated", advertisesSdk
                    ? "The CLI mentions SDK support, but the probe did not complete cleanly."
                    : "This CLI build does not clearly advertise `--sdk-mode` yet."),
                CreateCapability("terminal", "Terminal fallback", "ready", "Ready", "The terminal lane remains active and unaffected.")
            ]);
    }

    private static async Task<AiCliCapabilitySnapshot> BuildGrokSnapshotAsync(string? userProfileDirectory, bool appServerControlOnly, CancellationToken ct)
    {
        var binaryPath = AiCliCommandLocator.FindExecutableInPath("grok", userProfileDirectory);
        if (binaryPath is null)
        {
            if (appServerControlOnly)
            {
                return BuildSnapshot(
                    "native-required",
                    "attention",
                    "App Server Controller runtime unavailable",
                    "Explicit Grok App Server Controller sessions require the Grok Build CLI plus its ACP runtime on this machine.",
                    [
                        CreateCapability("cli", "Grok CLI", "missing", "Missing", "tlbx could not find `grok` on PATH."),
                        CreateCapability("native", "Grok ACP", "missing", "Missing", "Without `grok agent stdio`, this explicit App Server Controller session cannot become live."),
                        CreateCapability("terminal", "Terminal", "absent", "Absent", "Explicit App Server Controller sessions do not own an `mthost` terminal.")
                    ]);
            }

            return BuildSnapshot(
                "fallback-only",
                "fallback",
                "Fallback only",
                "tlbx can render Grok terminal sessions from supervisor state, but the Grok CLI is not available for a structured ACP lane on this machine.",
                [
                    CreateCapability("cli", "Grok CLI", "missing", "Missing", "tlbx could not find `grok` on PATH."),
                    CreateCapability("native", "Grok ACP", "missing", "Missing", "Without the Grok CLI, there is no ACP lane to attach."),
                    CreateCapability("terminal", "Terminal fallback", "ready", "Ready", "xterm stays fully available and remains the source of truth.")
                ]);
        }

        var probe = await ProbeAsync(binaryPath, "agent stdio --help", ct).ConfigureAwait(false);
        var advertisesStdio = probe.Output.Contains("Run the agent over stdio", StringComparison.OrdinalIgnoreCase) ||
                              probe.Output.Contains("agent stdio", StringComparison.OrdinalIgnoreCase);
        if (probe.Success && advertisesStdio)
        {
            if (appServerControlOnly)
            {
                return BuildSnapshot(
                    "native-ready",
                    "positive",
                    "App Server Controller runtime ready",
                    "This explicit Grok App Server Controller session can attach through `mtagenthost` to Grok Build's ACP stdio runtime.",
                    [
                        CreateCapability("cli", "Grok CLI", "ready", "Ready", $"Using `{binaryPath}`."),
                        CreateCapability("native", "Grok ACP", "ready", "Ready", "`grok agent stdio` is available for explicit App Server Controller sessions."),
                        CreateCapability("terminal", "Terminal", "absent", "Absent", "Explicit App Server Controller sessions do not own an `mthost` terminal.")
                    ]);
            }

            return BuildSnapshot(
                "fallback-ready",
                "positive",
                "Fallback now, native-ready",
                "tlbx is rendering this Grok lane from terminal signals, but `grok agent stdio` is available here so a native ACP feed can be attached later.",
                [
                    CreateCapability("cli", "Grok CLI", "ready", "Ready", $"Using `{binaryPath}`."),
                    CreateCapability("native", "Grok ACP", "ready", "Ready", "The Grok ACP lane can be wired in on this machine."),
                    CreateCapability("terminal", "Terminal fallback", "ready", "Ready", "xterm remains available beside the Agent view.")
                ]);
        }

        if (appServerControlOnly)
        {
            return BuildSnapshot(
                "native-gated",
                "warning",
                "App Server Controller runtime blocked",
                "Grok CLI exists, but `grok agent stdio` did not answer cleanly, so this explicit App Server Controller session cannot become live yet.",
                [
                    CreateCapability("cli", "Grok CLI", "ready", "Ready", $"Using `{binaryPath}`."),
                    CreateCapability("native", "Grok ACP", "gated", "Gated", BuildProbeDetail(probe, "The Grok ACP runtime is not reliably available yet.")),
                    CreateCapability("terminal", "Terminal", "absent", "Absent", "Explicit App Server Controller sessions do not own an `mthost` terminal.")
                ]);
        }

        return BuildSnapshot(
            "fallback-gated",
            "warning",
            "Fallback with upgrade gap",
            "tlbx is rendering this Grok lane from terminal signals. The Grok CLI exists, but `grok agent stdio` did not answer cleanly, so native events stay gated for now.",
            [
                CreateCapability("cli", "Grok CLI", "ready", "Ready", $"Using `{binaryPath}`."),
                CreateCapability("native", "Grok ACP", "gated", "Gated", BuildProbeDetail(probe, "The Grok ACP lane is not reliably available yet.")),
                CreateCapability("terminal", "Terminal fallback", "ready", "Ready", "The terminal lane remains active and unaffected.")
            ]);
    }

    private static AiCliCapabilitySnapshot BuildOpenCodeSnapshot()
    {
        return BuildSnapshot(
            "fallback-only",
            "fallback",
            "Fallback only",
            "tlbx can render OpenCode sessions from terminal telemetry, but there is no tlbx-native structured lane for this provider yet.",
            [
                CreateCapability("runtime", "OpenCode runtime", "ready", "Ready", "The session can still be supervised through tlbx state and terminal output."),
                CreateCapability("native", "Native events", "planned", "Planned", "tlbx does not ship an OpenCode-native event lane yet."),
                CreateCapability("terminal", "Terminal fallback", "ready", "Ready", "xterm remains the source of truth.")
            ]);
    }

    private static AiCliCapabilitySnapshot BuildGenericSnapshot()
    {
        return BuildSnapshot(
            "fallback-only",
            "fallback",
            "Fallback only",
            "tlbx can render this agent from generic terminal supervision, but provider-native events are not standardized for this lane yet.",
            [
                CreateCapability("runtime", "Interactive agent", "ready", "Ready", "tlbx can still infer state from activity, bells, and foreground app changes."),
                CreateCapability("native", "Native events", "planned", "Planned", "A provider-specific adapter would need to be added first."),
                CreateCapability("terminal", "Terminal fallback", "ready", "Ready", "xterm remains the source of truth.")
            ]);
    }

    private static AiCliCapabilitySnapshot BuildShellSnapshot()
    {
        return BuildSnapshot(
            "terminal-only",
            "fallback",
            "Terminal only",
            "This session currently looks like a plain shell, so the Agent view is only a light telemetry appServerControl over the terminal.",
            [
                CreateCapability("runtime", "AI runtime", "missing", "Missing", "tlbx does not currently detect an interactive agent in the foreground."),
                CreateCapability("native", "Native events", "missing", "Missing", "No structured provider lane can attach while the session is just a shell."),
                CreateCapability("terminal", "Terminal fallback", "ready", "Ready", "The terminal tab remains the correct primary surface.")
            ]);
    }

    private static AiCliCapabilitySnapshot BuildUnknownSnapshot(string profile)
    {
        return BuildSnapshot(
            "fallback-only",
            "fallback",
            "Fallback only",
            "tlbx can still render this session from terminal telemetry, but this provider is not recognized yet for a richer lane.",
            [
                CreateCapability("runtime", "Provider", "ready", "Ready", string.IsNullOrWhiteSpace(profile) ? "Unknown provider." : $"Detected `{profile}`."),
                CreateCapability("native", "Native events", "planned", "Planned", "A provider-specific bridge would need to be added first."),
                CreateCapability("terminal", "Terminal fallback", "ready", "Ready", "xterm remains the source of truth.")
            ]);
    }

    private static AiCliCapabilitySnapshot BuildSnapshot(
        string mode,
        string tone,
        string label,
        string detail,
        List<AgentSessionVibeCapability> capabilities)
    {
        return new AiCliCapabilitySnapshot
        {
            Lane = new AgentSessionVibeLane
            {
                Mode = mode,
                Tone = tone,
                Label = label,
                Detail = detail
            },
            Capabilities = capabilities
        };
    }

    private static AgentSessionVibeCapability CreateCapability(
        string key,
        string label,
        string status,
        string statusLabel,
        string detail)
    {
        return new AgentSessionVibeCapability
        {
            Key = key,
            Label = label,
            Status = status,
            StatusLabel = statusLabel,
            Detail = detail
        };
    }

    private static string BuildProbeDetail(ProbeResult probe, string fallbackMessage)
    {
        if (string.IsNullOrWhiteSpace(probe.Output))
        {
            return fallbackMessage;
        }

        var output = probe.Output.Trim();
        return output.Length <= 220 ? output : output[..217] + "...";
    }

    private static async Task<ProbeResult> ProbeAsync(string fileName, string arguments, CancellationToken ct)
    {
        using var process = new Process
        {
            StartInfo = CreateProbeStartInfo(fileName, arguments)
        };

        try
        {
            if (!process.Start())
            {
                return new ProbeResult(false, "The probe process could not be started.");
            }
        }
        catch (Exception ex)
        {
            return new ProbeResult(false, ex.Message);
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        try
        {
            await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(2), ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            TryKill(process);
            return new ProbeResult(false, "The capability probe timed out.");
        }
        catch (OperationCanceledException)
        {
            TryKill(process);
            throw;
        }

        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        var output = string.Join(
            Environment.NewLine,
            new[] { stdout.Trim(), stderr.Trim() }.Where(static text => !string.IsNullOrWhiteSpace(text)));

        return new ProbeResult(process.ExitCode == 0, output);
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }
        }
        catch
        {
        }
    }

    private static ProcessStartInfo CreateProbeStartInfo(string fileName, string arguments)
    {
        if (OperatingSystem.IsWindows() &&
            Path.GetExtension(fileName).Equals(".ps1", StringComparison.OrdinalIgnoreCase))
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "pwsh",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-NoLogo");
            startInfo.ArgumentList.Add("-NoProfile");
            startInfo.ArgumentList.Add("-ExecutionPolicy");
            startInfo.ArgumentList.Add("Bypass");
            startInfo.ArgumentList.Add("-File");
            startInfo.ArgumentList.Add(fileName);
            foreach (var argument in arguments.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                startInfo.ArgumentList.Add(argument);
            }

            return startInfo;
        }

        return new ProcessStartInfo
        {
            FileName = fileName,
            Arguments = arguments,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
    }

    private sealed record ProbeResult(bool Success, string Output);

    private sealed record CachedSnapshot(DateTimeOffset ExpiresAt, AiCliCapabilitySnapshot Snapshot)
    {
        public AiCliCapabilitySnapshot CloneSnapshot()
        {
            return new AiCliCapabilitySnapshot
            {
                Lane = new AgentSessionVibeLane
                {
                    Mode = Snapshot.Lane.Mode,
                    Tone = Snapshot.Lane.Tone,
                    Label = Snapshot.Lane.Label,
                    Detail = Snapshot.Lane.Detail
                },
                Capabilities = Snapshot.Capabilities
                    .Select(static capability => new AgentSessionVibeCapability
                    {
                        Key = capability.Key,
                        Label = capability.Label,
                        Status = capability.Status,
                        StatusLabel = capability.StatusLabel,
                        Detail = capability.Detail
                    })
                    .ToList()
            };
        }
    }

    private string? ResolveConfiguredUserProfileDirectory()
    {
        var settings = _settingsService?.Load();
        if (settings is null || !OperatingSystem.IsWindows() || string.IsNullOrWhiteSpace(settings.RunAsUser))
        {
            return null;
        }

        return AppServerControlHostEnvironmentResolver.ResolveWindowsProfileDirectory(settings.RunAsUser, settings.RunAsUserSid);
    }
}

public sealed class AiCliCapabilitySnapshot
{
    public AgentSessionVibeLane Lane { get; init; } = new();
    public List<AgentSessionVibeCapability> Capabilities { get; init; } = [];
}

using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Common.Shells;
using Ai.Tlbx.MidTerm.Models;
using Ai.Tlbx.MidTerm.Models.Update;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Git;
using Ai.Tlbx.MidTerm.Services.Share;
using Ai.Tlbx.MidTerm.Services.Tmux;
using Ai.Tlbx.MidTerm.Settings;

using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Services.Updates;
using Ai.Tlbx.MidTerm.Services.WebPreview;
using Ai.Tlbx.MidTerm.Services.WebSockets;
using Ai.Tlbx.MidTerm.Services.Certificates;
using Ai.Tlbx.MidTerm.Services.Security;
using Ai.Tlbx.MidTerm.Services.Hub;
using Ai.Tlbx.MidTerm.Models.Auth;
using Ai.Tlbx.MidTerm.Models.Certificates;
using Ai.Tlbx.MidTerm.Models.Files;
using Ai.Tlbx.MidTerm.Models.History;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Models.System;
namespace Ai.Tlbx.MidTerm.Startup;

public static class EndpointSetup
{
    private const string WindowsServiceName = "MidTerm";
    private static string? _cachedGitVersion;
    private static bool _gitVersionChecked;
    private static bool _codeSigned;
    private static bool _codeSigningChecked;

    private static string GetDisplayVersion(string version)
    {
        // Do not label dev-environment builds as [LOCAL]. MidTerm currently has
        // no trustworthy source/install-origin signal here, and published GitHub
        // prereleases were being misclassified as local builds in the UI.
        return version;
    }

    public static async Task DetectGitAsync()
    {
        _cachedGitVersion = await GitCommandRunner.GetGitVersionAsync();
        _gitVersionChecked = true;
    }

    public static void DetectCodeSigning()
    {
        _codeSigned = CheckCodeSigning();
        _codeSigningChecked = true;
    }

    private static bool CheckCodeSigning()
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exePath))
                return false;

            if (OperatingSystem.IsWindows())
            {
#pragma warning disable SYSLIB0057
                using var cert = X509Certificate2.CreateFromSignedFile(exePath);
#pragma warning restore SYSLIB0057
                return cert is not null;
            }

            if (OperatingSystem.IsMacOS())
            {
                var psi = new ProcessStartInfo("/usr/bin/codesign")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };
                psi.ArgumentList.Add("--verify");
                psi.ArgumentList.Add("--strict");
                psi.ArgumentList.Add(exePath);
                using var proc = Process.Start(psi);
                proc?.WaitForExit(5000);
                if (proc?.ExitCode != 0)
                {
                    var stderr = proc?.StandardError.ReadToEnd();
                    if (!string.IsNullOrWhiteSpace(stderr))
                        Log.Warn(() => $"Code signing check failed: {stderr.Trim()}");
                }
                return proc?.ExitCode == 0;
            }

            return false;
        }
        catch
        {
            return false;
        }
    }

    private static void SpawnReplacementProcess()
    {
        var exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            Log.Error(() => "Cannot restart: unable to determine binary path");
            return;
        }

        var cliArgs = Environment.GetCommandLineArgs();
        var args = cliArgs.Length > 1
            ? string.Join(" ", cliArgs.Skip(1))
            : "";

        var psi = new ProcessStartInfo
        {
            FileName = exePath,
            Arguments = args,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? ".",
            CreateNoWindow = true,
        };

        if (OperatingSystem.IsWindows())
        {
            psi.UseShellExecute = true;
            psi.WindowStyle = ProcessWindowStyle.Hidden;
        }
        else
        {
            psi.UseShellExecute = false;
            psi.RedirectStandardInput = true;
        }

        using var proc = Process.Start(psi);
        if (proc is not null && !OperatingSystem.IsWindows())
        {
            try { proc.StandardInput.Close(); } catch { }
        }

        Log.Info(() => string.Create(CultureInfo.InvariantCulture, $"Replacement process spawned (PID {proc?.Id})"));
    }

    private static bool TryArmServiceRestart(SettingsService settingsService)
    {
        if (!settingsService.IsRunningAsService)
        {
            return true;
        }

        if (OperatingSystem.IsWindows())
        {
            return TryScheduleWindowsServiceRestartHelper(WindowsServiceName);
        }

        return true;
    }

    internal static bool TryScheduleWindowsServiceRestartHelper(string serviceName)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = GetWindowsPowerShellPath(),
                Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {EncodePowerShellScript(BuildWindowsServiceRestartScript(serviceName))}",
                UseShellExecute = true,
                CreateNoWindow = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                Log.Error(() => $"Failed to start Windows service restart helper for service '{serviceName}'");
                return false;
            }

            Log.Info(() => string.Create(CultureInfo.InvariantCulture, $"Spawned Windows service restart helper (PID {process.Id}) for service '{serviceName}'"));
            return true;
        }
        catch (Exception ex)
        {
            Log.Error(() => string.Create(CultureInfo.InvariantCulture, $"Failed to schedule Windows service restart helper: {ex.Message}"));
            return false;
        }
    }

    internal static string BuildWindowsServiceRestartScript(string serviceName, int timeoutSeconds = 45)
    {
        var escapedServiceName = serviceName.Replace("'", "''", StringComparison.Ordinal);
        var timeoutSecondsText = timeoutSeconds.ToString(CultureInfo.InvariantCulture);
        return $$"""
$serviceName = '{{escapedServiceName}}'
$deadline = [DateTime]::UtcNow.AddSeconds({{timeoutSecondsText}})

while ($true) {
    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -eq $service) {
        exit 1
    }

    if ($service.Status -eq [System.ServiceProcess.ServiceControllerStatus]::Stopped) {
        break
    }

    if ([DateTime]::UtcNow -ge $deadline) {
        exit 2
    }

    Start-Sleep -Milliseconds 500
}

Start-Service -Name $serviceName -ErrorAction Stop
""";
    }

    internal static string EncodePowerShellScript(string script)
    {
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
    }

    private static string GetWindowsPowerShellPath()
    {
        var systemDir = Environment.GetFolderPath(Environment.SpecialFolder.System);
        return Path.Combine(systemDir, "WindowsPowerShell", "v1.0", "powershell.exe");
    }

    public static void MapBootstrapEndpoints(
        WebApplication app,
        TtyHostSessionManager sessionManager,
        UpdateService updateService,
        SettingsService settingsService,
        string version)
    {
        var shellRegistry = app.Services.GetRequiredService<ShellRegistry>();

        app.MapGet("/api/bootstrap", () =>
        {
            var settings = settingsService.Load();
            var publicSettings = MidTermSettingsPublic.FromSettings(settings);

            var authStatus = new AuthStatusResponse
            {
                AuthenticationEnabled = settings.AuthenticationEnabled,
                PasswordSet = !string.IsNullOrEmpty(settings.PasswordHash)
            };

            var conHostVersion = TtyHostSpawner.GetTtyHostVersion();
            var manifest = updateService.InstalledManifest;
            var conHostExpected = manifest.Pty;
            var conHostCompatible = conHostVersion == conHostExpected ||
                (conHostVersion is not null && manifest.MinCompatiblePty is not null &&
                 UpdateService.CompareVersions(conHostVersion, manifest.MinCompatiblePty) >= 0);

            var networks = NetworkInterfaceFilter.GetNetworkInterfaces();
            var users = SystemUserProvider.GetSystemUsers();

            var shells = shellRegistry.GetPlatformShells().Select(s => new ShellInfoDto
            {
                Type = s.ShellType.ToString(),
                DisplayName = s.DisplayName,
                IsAvailable = s.IsAvailable(),
                SupportsOsc7 = s.SupportsOsc7
            }).ToList();

            var updateResult = UpdateService.ReadUpdateResult(settingsService.SettingsDirectory, clear: true);
            var isDevMode = UpdateService.IsDevEnvironment || settings.DevMode;
            var displayVersion = GetDisplayVersion(version);

            var features = new FeatureFlags
            {
                VoiceChat = isDevMode
            };

            var response = new BootstrapResponse
            {
                Auth = authStatus,
                Version = displayVersion,
                TtyHostVersion = conHostVersion,
                TtyHostCompatible = conHostCompatible,
                UptimeSeconds = (long)(DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds,
                Platform = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux",
                Hostname = Environment.MachineName,
                Settings = publicSettings,
                Networks = networks,
                Users = users,
                Shells = shells,
                UpdateResult = updateResult,
                DevMode = isDevMode,
                Features = features,
                VoicePassword = isDevMode ? settings.VoiceServerPassword : null,
                GitVersion = _gitVersionChecked ? _cachedGitVersion : null,
                CodeSigned = _codeSigningChecked && _codeSigned
            };

            return Results.Json(response, AppJsonContext.Default.BootstrapResponse);
        });

        app.MapGet("/api/bootstrap/login", () =>
        {
            var certService = app.Services.GetRequiredService<CertificateInfoService>();
            var certInfo = certService.GetInfo();

            var response = new BootstrapLoginResponse
            {
                Certificate = certInfo
            };

            return Results.Json(response, AppJsonContext.Default.BootstrapLoginResponse);
        });
    }


    public static void MapSystemEndpoints(
        WebApplication app,
        TtyHostSessionManager sessionManager,
        UpdateService updateService,
        SettingsService settingsService,
        string version)
    {
        var shellRegistry = app.Services.GetRequiredService<ShellRegistry>();
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();

        // Consolidated system endpoint (replaces /api/version, /api/health, /api/version/details)
        app.MapGet("/api/system", () =>
        {
            var sessionCount = sessionManager.GetAllSessions().Count;
            var manifest = updateService.InstalledManifest;

            string? conHostVersion = TtyHostSpawner.GetTtyHostVersion();
            var conHostExpected = manifest.Pty;
            var conHostCompatible = conHostVersion == conHostExpected ||
                (conHostVersion is not null && manifest.MinCompatiblePty is not null &&
                 UpdateService.CompareVersions(conHostVersion, manifest.MinCompatiblePty) >= 0);

            var isDevMode2 = UpdateService.IsDevEnvironment || settingsService.Load().DevMode;
            var displayVersion = GetDisplayVersion(version);

            var response = new SystemResponse
            {
                Healthy = true,
                Version = displayVersion,
                Manifest = manifest,
                SessionCount = sessionCount,
                UptimeSeconds = (long)(DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds,
                Platform = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux",
                TtyHost = new TtyHostInfo
                {
                    Version = conHostVersion,
                    Expected = conHostExpected,
                    Compatible = conHostCompatible
                },
                WebProcessId = Environment.ProcessId,
                WindowsBuildNumber = OperatingSystem.IsWindows() ? Environment.OSVersion.Version.Build : null
            };
            return Results.Json(response, AppJsonContext.Default.SystemResponse);
        });

        // Legacy endpoints kept for backward compatibility
        app.MapGet("/api/version", () =>
        {
            var isDevMode3 = UpdateService.IsDevEnvironment || settingsService.Load().DevMode;
            var displayVersion = GetDisplayVersion(version);
            return Results.Text(displayVersion);
        });

        app.MapGet("/api/health", () =>
        {
            var sessionCount = sessionManager.GetAllSessions().Count;

            string? conHostVersion = TtyHostSpawner.GetTtyHostVersion();
            var manifest = updateService.InstalledManifest;
            var conHostExpected = manifest.Pty;
            var conHostCompatible = conHostVersion == conHostExpected ||
                (conHostVersion is not null && manifest.MinCompatiblePty is not null &&
                 UpdateService.CompareVersions(conHostVersion, manifest.MinCompatiblePty) >= 0);

            var health = new SystemHealth
            {
                Healthy = true,
                Mode = "service",
                SessionCount = sessionCount,
                Version = version,
                WebProcessId = Environment.ProcessId,
                UptimeSeconds = (long)(DateTime.UtcNow - System.Diagnostics.Process.GetCurrentProcess().StartTime.ToUniversalTime()).TotalSeconds,
                Platform = OperatingSystem.IsWindows() ? "Windows" : OperatingSystem.IsMacOS() ? "macOS" : "Linux",
                TtyHostVersion = conHostVersion,
                TtyHostExpected = conHostExpected,
                TtyHostCompatible = conHostCompatible,
                WindowsBuildNumber = OperatingSystem.IsWindows() ? Environment.OSVersion.Version.Build : null
            };
            return Results.Json(health, AppJsonContext.Default.SystemHealth);
        });

        app.MapGet("/api/version/details", () =>
        {
            var manifest = updateService.InstalledManifest;
            return Results.Json(manifest, AppJsonContext.Default.VersionManifest);
        });

        app.MapGet("/api/certificate/info", () =>
        {
            var certService = app.Services.GetRequiredService<CertificateInfoService>();
            return Results.Json(certService.GetInfo(), AppJsonContext.Default.CertificateInfoResponse);
        });

        app.MapGet("/api/certificate/download/pem", () =>
        {
            var certService = app.Services.GetRequiredService<CertificateInfoService>();
            var pemBytes = certService.ExportPemBytes();
            if (pemBytes is null)
            {
                return Results.NotFound("Certificate not available");
            }
            return Results.File(pemBytes, "application/x-pem-file", "midterm.pem");
        });

        app.MapGet("/api/certificate/download/crt", () =>
        {
            var certService = app.Services.GetRequiredService<CertificateInfoService>();
            var derBytes = certService.ExportDerBytes();
            if (derBytes is null)
            {
                return Results.NotFound("Certificate not available");
            }
            return Results.File(derBytes, "application/x-x509-ca-cert", "midterm.crt");
        });

        app.MapGet("/api/certificate/download/mobileconfig", (HttpContext context) =>
        {
            var certService = app.Services.GetRequiredService<CertificateInfoService>();
            var hostname = context.Request.Host.Host;
            var configBytes = certService.GenerateMobileConfig(hostname);
            if (configBytes is null)
            {
                return Results.NotFound("Certificate not available");
            }
            return Results.File(configBytes, "application/x-apple-aspen-config", "midterm.mobileconfig");
        });

        app.MapGet("/api/certificate/share-packet", (HttpContext context) =>
        {
            var certService = app.Services.GetRequiredService<CertificateInfoService>();
            var downloadInfo = certService.GetDownloadInfo();

            var hostPort = context.Request.Host.Port ?? 2000;

            var networkList = NetworkInterfaceFilter.GetNetworkInterfaces();
            var interfaces = networkList
                .Where(n => n.Ip != "localhost")
                .Select(n => new NetworkEndpointInfo
                {
                    Name = n.Name,
                    Url = string.Create(CultureInfo.InvariantCulture, $"https://{n.Ip}:{hostPort}")
                })
                .ToArray();

            var sharePacket = new SharePacketInfo
            {
                Certificate = downloadInfo,
                Endpoints = interfaces,
                TrustPageUrl = ShareUrlBuilder.BuildTrustPageUrl(context.Request, networkList),
                Port = hostPort
            };

            return Results.Json(sharePacket, AppJsonContext.Default.SharePacketInfo);
        });

        app.MapPost("/api/certificate/regenerate", () =>
        {
            try
            {
                CertificateService.RegenerateCertificate(settingsService);
            }
            catch (Exception ex)
            {
                Log.Error(() => $"Certificate regeneration failed: {ex.Message}");
                return Results.Problem($"Failed to regenerate certificate: {ex.Message}");
            }

            if (!TryArmServiceRestart(settingsService))
            {
                return Results.Problem("Failed to schedule service restart after certificate regeneration.");
            }

            DelayedActionScheduler.Schedule(TimeSpan.FromMilliseconds(1500), () =>
            {
                if (!settingsService.IsRunningAsService)
                {
                    try
                    {
                        SpawnReplacementProcess();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(() => $"Failed to spawn replacement process: {ex.Message}");
                    }
                }
                else
                {
                    Log.Info(() => OperatingSystem.IsWindows()
                        ? "Windows service restart helper armed; stopping after certificate regeneration"
                        : "Service mode: stopping after certificate regeneration");
                }

                lifetime.StopApplication();
            });

            return Results.Ok("Certificate regenerated. Server is restarting...");
        });

        app.MapGet("/api/update/check", async () =>
        {
            var update = await updateService.CheckForUpdateAsync();
            return Results.Json(update ?? new UpdateInfo
            {
                Available = false,
                CurrentVersion = updateService.CurrentVersion,
                LatestVersion = updateService.CurrentVersion
            }, AppJsonContext.Default.UpdateInfo);
        });

        app.MapPost("/api/update/apply", async (string? source) =>
        {
            var (success, message) = await updateService.ApplyUpdateAsync(settingsService, source);
            if (!success)
            {
                return source == "local" || message.Contains("No update", StringComparison.Ordinal)
                    ? Results.BadRequest(message)
                    : Results.Problem(message);
            }

            return Results.Ok(message);
        });

        app.MapGet("/api/update/result", (bool clear = false) =>
        {
            var result = UpdateService.ReadUpdateResult(settingsService.SettingsDirectory, clear);
            return Results.Json(result ?? new UpdateResult { Found = false }, AppJsonContext.Default.UpdateResult);
        });

        app.MapDelete("/api/update/result", () =>
        {
            UpdateService.ClearUpdateResult(settingsService.SettingsDirectory);
            return Results.Ok();
        });

        // GET /api/update/log - get the update log file content
        app.MapGet("/api/update/log", () =>
        {
            var isWindowsService = settingsService.IsRunningAsService && OperatingSystem.IsWindows();
            var isUnixService = settingsService.IsRunningAsService && !OperatingSystem.IsWindows();
            var logPath = LogPaths.GetUpdateLogPath(isWindowsService, isUnixService, settingsService.SettingsDirectory);

            if (!File.Exists(logPath))
            {
                return Results.NotFound("No update log found");
            }

            try
            {
                var content = File.ReadAllText(logPath);
                if (content.Length > 100_000)
                {
                    content = content[^100_000..];
                }
                return Results.Text(content, "text/plain");
            }
            catch (Exception ex)
            {
                return Results.Problem($"Failed to read log: {ex.Message}");
            }
        });

        // POST /api/restart - restart the server process
        app.MapPost("/api/restart", () =>
        {
            Log.Info(() => "Server restart requested via API");

            if (!TryArmServiceRestart(settingsService))
            {
                return Results.Problem("Failed to schedule service restart.");
            }

            // Fire-and-forget: return response first, then exit after delay
            DelayedActionScheduler.Schedule(TimeSpan.FromMilliseconds(1500), () =>
            {
                if (!settingsService.IsRunningAsService)
                {
                    try
                    {
                        SpawnReplacementProcess();
                    }
                    catch (Exception ex)
                    {
                        Log.Error(() => $"Failed to spawn replacement process: {ex.Message}");
                    }
                }
                else
                {
                    Log.Info(() => OperatingSystem.IsWindows()
                        ? "Windows service restart helper armed; stopping service"
                        : "Service mode: exiting for service manager to respawn");
                }

                lifetime.StopApplication();
            });

            return Results.Ok("Server is restarting...");
        });

        // POST /api/shutdown - graceful shutdown without respawn (loopback only, no auth required)
        app.MapPost("/api/shutdown", () =>
        {
            Log.Info(() => "Server shutdown requested via API");

            DelayedActionScheduler.Schedule(TimeSpan.FromMilliseconds(500), () =>
            {
                lifetime.StopApplication();
            });

            return Results.Ok("Server is shutting down...");
        });

        app.MapGet("/api/networks", () =>
        {
            var interfaces = NetworkInterfaceFilter.GetNetworkInterfaces();
            return Results.Json(interfaces, AppJsonContext.Default.ListNetworkInterfaceDto);
        });

        app.MapGet("/api/shells", () =>
        {
            var shells = shellRegistry.GetPlatformShells().Select(s => new ShellInfoDto
            {
                Type = s.ShellType.ToString(),
                DisplayName = s.DisplayName,
                IsAvailable = s.IsAvailable(),
                SupportsOsc7 = s.SupportsOsc7
            }).ToList();
            return Results.Json(shells, AppJsonContext.Default.ListShellInfoDto);
        });

        app.MapGet("/api/settings", () =>
        {
            var settings = settingsService.Load();
            var publicSettings = Settings.MidTermSettingsPublic.FromSettings(settings);
            return Results.Json(publicSettings, AppJsonContext.Default.MidTermSettingsPublic);
        });

        app.MapPut("/api/settings", (Settings.MidTermSettingsPublic publicSettings) =>
        {
            try
            {
                var currentSettings = settingsService.Load();
                var previousUpdateChannel = currentSettings.UpdateChannel;
                publicSettings.ApplyTo(currentSettings);
                if (!string.Equals(previousUpdateChannel, currentSettings.UpdateChannel, StringComparison.Ordinal))
                {
                    Log.Warn(() =>
                        $"UpdateChannel changing via PUT /api/settings: {previousUpdateChannel} -> {currentSettings.UpdateChannel}");
                }
                settingsService.Save(currentSettings);
                return Results.Ok();
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: 400);
            }
            catch (Exception ex)
            {
                Common.Logging.Log.Exception(ex, "PUT /api/settings");
                return Results.Problem($"Failed to save settings: {ex.Message}");
            }
        });

        app.MapPost("/api/settings/reload", () =>
        {
            settingsService.InvalidateCache();
            var settings = settingsService.Load();
            var publicSettings = Settings.MidTermSettingsPublic.FromSettings(settings);
            return Results.Json(publicSettings, AppJsonContext.Default.MidTermSettingsPublic);
        });

        app.MapGet("/api/settings/background-image", (BackgroundImageService backgroundImageService) =>
        {
            var settings = settingsService.Load();
            var imagePath = backgroundImageService.GetCurrentImagePath(settings);
            if (imagePath is null)
            {
                return Results.NotFound();
            }

            var extension = Path.GetExtension(imagePath).ToLowerInvariant();
            var contentType = extension switch
            {
                ".png" => "image/png",
                ".jpg" or ".jpeg" => "image/jpeg",
                _ => "application/octet-stream"
            };

            return Results.File(imagePath, contentType, enableRangeProcessing: false);
        });

        app.MapPost("/api/settings/background-image", async (IFormFile file, BackgroundImageService backgroundImageService) =>
        {
            try
            {
                var info = await backgroundImageService.SaveAsync(file);
                return Results.Json(info, AppJsonContext.Default.BackgroundImageInfoResponse);
            }
            catch (ArgumentException ex)
            {
                return Results.Problem(ex.Message, statusCode: 400);
            }
            catch (Exception ex)
            {
                Common.Logging.Log.Exception(ex, "POST /api/settings/background-image");
                return Results.Problem($"Failed to save background image: {ex.Message}");
            }
        }).DisableAntiforgery();

        app.MapDelete("/api/settings/background-image", (BackgroundImageService backgroundImageService) =>
        {
            try
            {
                var info = backgroundImageService.Delete();
                return Results.Json(info, AppJsonContext.Default.BackgroundImageInfoResponse);
            }
            catch (Exception ex)
            {
                Common.Logging.Log.Exception(ex, "DELETE /api/settings/background-image");
                return Results.Problem($"Failed to delete background image: {ex.Message}");
            }
        });

        app.MapGet("/api/paths", () =>
        {
            var settings = settingsService.Load();
            var isWindowsService = settingsService.IsRunningAsService && OperatingSystem.IsWindows();
            var isUnixService = settingsService.IsRunningAsService && !OperatingSystem.IsWindows();

            var secretsFile = OperatingSystem.IsWindows()
                ? Path.Combine(settingsService.SettingsDirectory, "secrets.bin")
                : Path.Combine(settingsService.SettingsDirectory, "secrets.json");

            var response = new PathsResponse
            {
                SettingsFile = settingsService.SettingsPath,
                SecretsFile = secretsFile,
                CertificateFile = settings.CertificatePath ?? "",
                LogDirectory = LogPaths.GetLogDirectory(isWindowsService, isUnixService)
            };
            return Results.Json(response, AppJsonContext.Default.PathsResponse);
        });

        app.MapGet("/api/users", () =>
        {
            var users = SystemUserProvider.GetSystemUsers();
            return Results.Json(users, AppJsonContext.Default.ListUserInfo);
        });
    }

    public static void MapWebSocketMiddleware(
        WebApplication app,
        TtyHostSessionManager sessionManager,
        TtyHostMuxConnectionManager muxManager,
        SessionSupervisorService sessionSupervisor,
        SessionAppServerControlRuntimeService appServerControlRuntime,
        UpdateService updateService,
        SettingsService settingsService,
        AuthService authService,
        ShareGrantService shareGrantService,
        ShutdownService shutdownService,
        MainBrowserService mainBrowserService,
        SessionLayoutStateService sessionLayoutStateService,
        ManagerBarQueueService managerBarQueueService,
        GitWatcherService gitWatcher,
        BrowserCommandService browserCommandService,
        BrowserPreviewRegistry browserPreviewRegistry,
        WebPreviewService webPreviewService,
        TmuxLayoutBridge? tmuxLayoutBridge = null,
        BrowserUiBridge? browserUiBridge = null)
    {
        var muxHandler = new MuxWebSocketHandler(sessionManager, muxManager, settingsService, authService, shareGrantService, shutdownService);
        var stateHandler = new StateWebSocketHandler(sessionManager, sessionSupervisor, appServerControlRuntime, updateService, settingsService, authService, shareGrantService, shutdownService, mainBrowserService, sessionLayoutStateService, managerBarQueueService, tmuxLayoutBridge, browserUiBridge);
        var appServerControlHandler = new AppServerControlWebSocketHandler(sessionManager, sessionSupervisor, app.Services.GetRequiredService<SessionAppServerControlRuntimeService>(), app.Services.GetRequiredService<SessionCodexHandoffService>(), app.Services.GetRequiredService<AiCliProfileService>(), authService, shutdownService);
        var settingsHandler = new SettingsWebSocketHandler(settingsService, updateService, authService, shutdownService);
        var gitHandler = new GitWebSocketHandler(gitWatcher, settingsService, authService, shutdownService, sessionManager);
        var browserHandler = new BrowserWebSocketHandler(
            browserCommandService,
            browserPreviewRegistry,
            webPreviewService,
            settingsService,
            authService,
            shutdownService);
        var hubMuxHandler = app.Services.GetRequiredService<HubMuxWebSocketHandler>();

        app.Use(async (context, next) =>
        {
            if (!context.Request.Path.StartsWithSegments("/ws", StringComparison.Ordinal))
            {
                await next(context);
                return;
            }

            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                return;
            }

            var path = context.Request.Path.Value ?? "";

            if (path == "/ws/state")
            {
                await stateHandler.HandleAsync(context);
                return;
            }

            if (path == "/ws/share/state")
            {
                await stateHandler.HandleAsync(context);
                return;
            }

            if (path == "/ws/settings")
            {
                await settingsHandler.HandleAsync(context);
                return;
            }

            if (path == "/ws/git")
            {
                await gitHandler.HandleAsync(context);
                return;
            }

            if (path == "/ws/app-server-control")
            {
                await appServerControlHandler.HandleAsync(context);
                return;
            }

            if (path == "/ws/mux")
            {
                await muxHandler.HandleAsync(context);
                return;
            }

            if (path == "/ws/share/mux")
            {
                await muxHandler.HandleAsync(context);
                return;
            }

            if (path == "/ws/browser")
            {
                await browserHandler.HandleAsync(context);
                return;
            }

            if (path == "/ws/hub/mux")
            {
                await hubMuxHandler.HandleAsync(context);
                return;
            }

            context.Response.StatusCode = 404;
        });
    }
}

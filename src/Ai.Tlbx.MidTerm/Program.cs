using System.Diagnostics;
using System.Globalization;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Git;
using Ai.Tlbx.MidTerm.Services.Tmux;
using Ai.Tlbx.MidTerm.Services.Tmux.Commands;
using Ai.Tlbx.MidTerm.Settings;
using Ai.Tlbx.MidTerm.Startup;

using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Services.Updates;
using Ai.Tlbx.MidTerm.Services.Certificates;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.WebPreview;
using Ai.Tlbx.MidTerm.Services.Share;
using Ai.Tlbx.MidTerm.Services.Security;
using Ai.Tlbx.MidTerm.Services.Power;
using Ai.Tlbx.MidTerm.Services.Hub;
using Ai.Tlbx.MidTerm.Services.Hosting;
using Ai.Tlbx.MidTerm.Services.Spaces;
namespace Ai.Tlbx.MidTerm;

public class Program
{
    private enum DiagLogLevel { Info, Warning, Error }

    private static void WriteEventLog(string message, DiagLogLevel level = DiagLogLevel.Info)
    {
#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            const string source = "MidTerm";
            const string logName = "Application";

            var eventLogType = level switch
            {
                DiagLogLevel.Warning => EventLogEntryType.Warning,
                DiagLogLevel.Error => EventLogEntryType.Error,
                _ => EventLogEntryType.Information
            };

            try
            {
                if (!EventLog.SourceExists(source))
                {
                    EventLog.CreateEventSource(source, logName);
                }

                EventLog.WriteEntry(source, message, eventLogType);
            }
            catch
            {
                try
                {
                    var isService = LogPaths.DetectWindowsServiceMode();
                    var logDir = LogPaths.GetLogDirectory(isService);
                    Directory.CreateDirectory(logDir);
                    var logPath = Path.Combine(logDir, "startup-debug.log");
                    File.AppendAllText(
                        logPath,
                        string.Create(
                            CultureInfo.InvariantCulture,
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [{level}] {message}{Environment.NewLine}"));
                }
                catch
                {
                }
            }
        }
#endif
    }

    private static void WriteEventLogWrapper(string message, bool isError)
    {
        WriteEventLog(message, isError ? DiagLogLevel.Error : DiagLogLevel.Info);
    }

    public static async Task Main(string[] args)
    {
        try
        {
            WriteEventLog("Main: Starting");
            await MainCore(args);
        }
        catch (Exception ex)
        {
            WriteEventLog($"Main: FATAL ERROR - {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", DiagLogLevel.Error);
            throw;
        }
    }

    private static async Task MainCore(string[] args)
    {
        WriteEventLog("MainCore: Checking special commands");

        if (CliCommands.HandleSpecialCommands(args))
        {
            return;
        }

        WriteEventLog("MainCore: Acquiring instance guard");

        var (port, bindAddress) = ArgumentParser.Parse(args);
        var preflightSettings = new SettingsService();
        var instanceIdentity = MidTermInstanceIdentity.Load(preflightSettings.SettingsDirectory, port);
        using var settingsGuard = SingleInstanceGuard.TryAcquire(instanceIdentity.SettingsGuardName, out var settingsGuardInfo);
        if (settingsGuard is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {settingsGuardInfo}");
            Console.ResetColor();
            Console.WriteLine($"Another MidTerm instance is already using settings directory '{preflightSettings.SettingsDirectory}'.");
            Console.WriteLine("Stop the other instance first, or launch this one with a separate MIDTERM_SETTINGS_DIR for isolated dev state.");
            Environment.Exit(1);
            return;
        }

        using var portGuard = SingleInstanceGuard.TryAcquire(instanceIdentity.PortGuardName, out var portGuardInfo);
        if (portGuard is null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"Error: {portGuardInfo}");
            Console.ResetColor();
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"Another MidTerm instance is already running for port {port}."));
            Console.WriteLine("Use a different port or stop the existing instance first.");
            Environment.Exit(1);
            return;
        }

        WriteEventLog("MainCore: Parsing args and creating builder");
        var builder = ServerSetup.CreateBuilder(args, WriteEventLogWrapper);

        WriteEventLog("MainCore: Building app");

        var app = builder.Build();
        var version = CliCommands.GetVersion();

        WriteEventLog("MainCore: Resolving services");

        var settingsService = app.Services.GetRequiredService<SettingsService>();
        var resolvedInstanceIdentity = app.Services.GetRequiredService<MidTermInstanceIdentity>();
        var updateService = app.Services.GetRequiredService<UpdateService>();
        var authService = app.Services.GetRequiredService<AuthService>();
        var shareGrantService = app.Services.GetRequiredService<ShareGrantService>();
        var tempCleanupService = app.Services.GetRequiredService<TempCleanupService>();
        var certInfoService = app.Services.GetRequiredService<CertificateInfoService>();
        var apiKeyService = app.Services.GetRequiredService<ApiKeyService>();

        WriteEventLog($"MainCore: Certificate loaded = {ServerSetup.LoadedCertificate is not null}, IsFallback = {ServerSetup.IsFallbackCertificate}");

        if (ServerSetup.LoadedCertificate is not null)
        {
            certInfoService.SetCertificate(ServerSetup.LoadedCertificate, ServerSetup.IsFallbackCertificate);

            // Clean up old MidTerm certificates from trusted store (Windows only)
            if (!ServerSetup.IsFallbackCertificate)
            {
                CertificateCleanupService.EnsureCertificateTrust(ServerSetup.LoadedCertificate, WriteEventLogWrapper);
            }
        }

        tempCleanupService.CleanupOrphanedFiles();

        var settings = settingsService.Load();
        var logDirectory = LogPaths.GetLogDirectory(settingsService.IsRunningAsService);
        Log.Initialize("mt", logDirectory, LogSeverity.Error);
        Log.SetupCrashHandlers();
        Log.Info(() => string.Create(CultureInfo.InvariantCulture, $"MidTerm server starting (instance={resolvedInstanceIdentity.GetShortInstanceId()}, port={port})"));

        // Validate security state and log any warnings (informational only - does not block)
        var securityStatusService = app.Services.GetRequiredService<SecurityStatusService>();
        var securityStatus = securityStatusService.GetStatus();
        foreach (var warning in securityStatus.Warnings)
        {
            Log.Warn(() => string.Create(CultureInfo.InvariantCulture, $"SECURITY: {warning}"));
            WriteEventLog(string.Create(CultureInfo.InvariantCulture, $"Security Warning: {warning}"), DiagLogLevel.Warning);
        }

        WelcomeScreen.LogStartupStatus(settingsService, settings, port, bindAddress,
            ServerSetup.LoadedCertificate, ServerSetup.IsFallbackCertificate);

        var browserPreviewOriginService = app.Services.GetRequiredService<BrowserPreviewOriginService>();
        var browserPreviewRegistry = app.Services.GetRequiredService<BrowserPreviewRegistry>();
        var browserPreviewOwnerService = app.Services.GetRequiredService<BrowserPreviewOwnerService>();
        ServerSetup.ConfigureMiddleware(
            app,
            settingsService,
            authService,
            shareGrantService,
            browserPreviewOriginService,
            browserPreviewRegistry);

        MidtermDirectory.Initialize(port, authService);

        var sessionManager = app.Services.GetRequiredService<TtyHostSessionManager>();
        var layoutStateService = app.Services.GetRequiredService<SessionLayoutStateService>();
        var muxManager = app.Services.GetRequiredService<TtyHostMuxConnectionManager>();
        var sessionTelemetry = app.Services.GetRequiredService<SessionTelemetryService>();
        var managerBarQueueService = app.Services.GetRequiredService<ManagerBarQueueService>();
        var agentFeed = app.Services.GetRequiredService<SessionAgentFeedService>();
        var sessionSupervisor = app.Services.GetRequiredService<SessionSupervisorService>();
        var aiCliProfileService = app.Services.GetRequiredService<AiCliProfileService>();
        var workerSessionRegistry = app.Services.GetRequiredService<WorkerSessionRegistryService>();
        var historyService = app.Services.GetRequiredService<HistoryService>();
        var spaceService = app.Services.GetRequiredService<SpaceService>();
        var sessionPathAllowlistService = app.Services.GetRequiredService<SessionPathAllowlistService>();
        var gitWatcher = app.Services.GetRequiredService<GitWatcherService>();
        GitCommandRunner.Configure(settings.RunAsUser, settingsService.IsRunningAsService);
        var commandService = app.Services.GetRequiredService<CommandService>();
        var sleepInhibitorService = app.Services.GetRequiredService<SystemSleepInhibitorService>();
        sleepInhibitorService.UpdateEnabled(settings.KeepSystemAwakeWithActiveSessions);
        var sleepInhibitorStateListenerId = sessionManager.AddStateListener(() =>
            sleepInhibitorService.UpdateSessionCount(sessionManager.GetAllSessions().Count));

        // Tmux compatibility layer (conditional on setting)
        TmuxCommandDispatcher? tmuxDispatcher = null;
        TmuxLayoutBridge? tmuxLayoutBridge = null;

        if (settings.TmuxCompatibility)
        {
            TmuxLog.Initialize(logDirectory);
            TmuxScriptWriter.WriteScript(port);
            sessionManager.ConfigureTmux(port, authService.CreateSessionToken, TmuxScriptWriter.ScriptDirectory);
            var tmuxPaneMapper = new TmuxPaneMapper(sessionManager);
            sessionManager.OnSessionCreated += (sid, idx) => tmuxPaneMapper.RegisterSession(sid, idx);
            sessionManager.OnSessionClosed += sid => tmuxPaneMapper.UnregisterSession(sid);
            var tmuxTargetResolver = new TmuxTargetResolver(tmuxPaneMapper);
            var tmuxFormatter = new TmuxFormatter(tmuxPaneMapper, sessionManager);
            tmuxLayoutBridge = new TmuxLayoutBridge();
            layoutStateService.OnChanged += () =>
            {
                var snapshot = layoutStateService.GetSnapshot(sessionManager.GetAllSessions().Select(s => s.Id));
                tmuxLayoutBridge.UpdateLayout(snapshot.Root);
            };
            var tmuxSessionCommands = new SessionCommands(sessionManager, tmuxPaneMapper, tmuxFormatter);
            var tmuxIoCommands = new IoCommands(sessionManager, tmuxTargetResolver, tmuxFormatter);
            var tmuxPaneCommands = new PaneCommands(sessionManager, tmuxPaneMapper, tmuxTargetResolver, tmuxLayoutBridge);
            var tmuxWindowCommands = new WindowCommands(sessionManager, tmuxTargetResolver, tmuxLayoutBridge, tmuxPaneCommands);
            var tmuxConfigCommands = new ConfigCommands();
            var tmuxMiscCommands = new MiscCommands(tmuxPaneCommands);
            tmuxDispatcher = new TmuxCommandDispatcher(
                tmuxSessionCommands, tmuxIoCommands, tmuxPaneCommands,
                tmuxWindowCommands, tmuxConfigCommands, tmuxMiscCommands);
        }

        // Browser control (agent-driven web preview interaction)
        BrowserLog.Initialize(logDirectory);
        var browserCommandService = app.Services.GetRequiredService<BrowserCommandService>();
        var browserUiBridge = app.Services.GetRequiredService<BrowserUiBridge>();
        BrowserScriptWriter.WriteScript(port);

        static void SyncMidtermDirectoryForCwd(string? cwd)
        {
            MidtermDirectory.TryEnsureForCwd(cwd);
        }

        sessionManager.OnSessionCreated += (sessionId, _) =>
        {
            var cwd = sessionManager.GetSession(sessionId)?.CurrentDirectory;
            SyncMidtermDirectoryForCwd(cwd);
        };

        sessionManager.OnForegroundChanged += (sessionId, payload) =>
        {
            agentFeed.NoteForeground(sessionId, payload);
            SyncMidtermDirectoryForCwd(payload.Cwd);
            var session = sessionManager.GetSession(sessionId);
            if (session is not null && !string.IsNullOrEmpty(payload.Name) && !string.IsNullOrEmpty(payload.Cwd))
            {
                historyService.RecordEntry(
                    session.ShellType,
                    payload.Name,
                    payload.CommandLine,
                    payload.Cwd,
                    launchOrigin: sessionManager.GetLaunchOrigin(session.Id));
            }
        };

        sessionManager.OnForegroundChanged += (sessionId, payload) =>
        {
            if (!string.IsNullOrEmpty(payload.Cwd))
            {
                _ = gitWatcher.RegisterSessionAsync(sessionId, payload.Cwd);
            }
        };

        sessionManager.OnCwdChanged += (sessionId, cwd) =>
        {
            SyncMidtermDirectoryForCwd(cwd);
            _ = gitWatcher.RegisterSessionAsync(sessionId, cwd);
        };

        sessionManager.OnOutput += (sessionId, _, _, _, data) =>
        {
            sessionTelemetry.RecordOutput(sessionId, data.Span);
        };

        sessionManager.OnSessionClosed += sessionId =>
        {
            sessionPathAllowlistService.ClearSession(sessionId);
            gitWatcher.UnregisterSession(sessionId);
            shareGrantService.RevokeBySession(sessionId);
            sessionTelemetry.ClearSession(sessionId);
            managerBarQueueService.RemoveSession(sessionId);
            workerSessionRegistry.Forget(sessionId);
            agentFeed.Forget(sessionId);
        };

        settingsService.AddSettingsListener(newSettings =>
        {
            var (isValid, _) = UserValidationService.ValidateRunAsUser(newSettings.RunAsUser);
            if (isValid)
            {
                sessionManager.UpdateRunAsUser(newSettings.RunAsUser, newSettings.RunAsUserSid);
                GitCommandRunner.Configure(newSettings.RunAsUser, settingsService.IsRunningAsService);
            }
            else
            {
                Log.Warn(() => $"Settings: Ignoring invalid RunAsUser from file: {newSettings.RunAsUser}");
            }

            sleepInhibitorService.UpdateEnabled(newSettings.KeepSystemAwakeWithActiveSessions);
        });

        var shutdownService = app.Services.GetRequiredService<ShutdownService>();
        var lifetime = app.Services.GetRequiredService<IHostApplicationLifetime>();
        var cleanupStarted = 0;

        _ = EndpointSetup.DetectGitAsync();
        EndpointSetup.DetectCodeSigning();

        AuthEndpoints.MapAuthEndpoints(app, settingsService, authService);
        SecurityEndpoints.MapSecurityEndpoints(app, securityStatusService, apiKeyService);
        EndpointSetup.MapBootstrapEndpoints(app, sessionManager, updateService, settingsService, version);
        EndpointSetup.MapSystemEndpoints(app, sessionManager, updateService, settingsService, version);
        ShareEndpoints.MapShareEndpoints(app, shareGrantService, sessionManager, settingsService);
        var clipboardService = app.Services.GetRequiredService<ClipboardService>();
        var webPreviewService = app.Services.GetRequiredService<WebPreviewService>();
        var appServerControlRuntime = app.Services.GetRequiredService<SessionAppServerControlRuntimeService>();
        var codexHandoff = app.Services.GetRequiredService<SessionCodexHandoffService>();
        var providerResumeCatalog = app.Services.GetRequiredService<ProviderResumeCatalogService>();
        var agentVibe = app.Services.GetRequiredService<SessionAgentVibeService>();
        SessionApiEndpoints.MapSessionEndpoints(
            app,
            sessionManager,
            layoutStateService,
            managerBarQueueService,
            clipboardService,
            updateService,
            webPreviewService,
            sessionTelemetry,
            agentFeed,
            sessionSupervisor,
            appServerControlRuntime,
            codexHandoff,
            providerResumeCatalog,
            agentVibe,
            aiCliProfileService,
            workerSessionRegistry);
        SpaceEndpoints.MapSpaceEndpoints(
            app,
            spaceService,
            sessionManager,
            agentFeed,
            sessionSupervisor,
            appServerControlRuntime,
            workerSessionRegistry);
        SessionLayoutEndpoints.MapSessionLayoutEndpoints(app, sessionManager, layoutStateService);
        if (tmuxDispatcher is not null && tmuxLayoutBridge is not null)
        {
            TmuxEndpoints.MapTmuxEndpoints(
                app,
                tmuxDispatcher,
                tmuxLayoutBridge,
                sessionManager,
                layoutStateService);
        }
        TmuxEndpoints.MapSessionInputEndpoint(app, sessionManager);
        HistoryEndpoints.MapHistoryEndpoints(app, historyService, sessionManager);
        FileEndpoints.MapFileEndpoints(app, sessionManager, sessionPathAllowlistService, settingsService, gitWatcher);
        GitEndpoints.MapGitEndpoints(app, gitWatcher, sessionManager);
        CommandEndpoints.MapCommandEndpoints(app, commandService, sessionManager);
        HubEndpoints.MapHubEndpoints(app, app.Services.GetRequiredService<HubService>());
        WebPreviewEndpoints.MapWebPreviewEndpoints(app, webPreviewService, sessionManager, browserCommandService);
        BrowserEndpoints.MapBrowserEndpoints(
            app,
            browserCommandService,
            browserPreviewRegistry,
            browserPreviewOwnerService,
            browserPreviewOriginService,
            sessionManager,
            webPreviewService,
            browserUiBridge);
        var mainBrowserService = app.Services.GetRequiredService<MainBrowserService>();
        EndpointSetup.MapWebSocketMiddleware(
            app,
            sessionManager,
            muxManager,
            sessionSupervisor,
            appServerControlRuntime,
            updateService,
            settingsService,
            authService,
            shareGrantService,
            shutdownService,
            mainBrowserService,
            layoutStateService,
            managerBarQueueService,
            gitWatcher,
            browserCommandService,
            browserPreviewRegistry,
            webPreviewService,
            tmuxLayoutBridge,
            browserUiBridge);

        lifetime.ApplicationStarted.Register(() =>
        {
            Log.Info(() => string.Create(CultureInfo.InvariantCulture, $"Server fully operational - listening on https://{bindAddress}:{port}"));
        });

        lifetime.ApplicationStopping.Register(() =>
        {
            Log.Info(() => "Shutdown requested, signaling components...");
            shutdownService.SignalShutdown();
        });

        shutdownService.Token.Register(() =>
        {
            DelayedActionScheduler.Schedule(TimeSpan.FromSeconds(10), () =>
            {
                if (shutdownService.IsShuttingDown)
                {
                    Log.Error(() => "Shutdown timeout exceeded (10s), forcing exit");
                    Environment.Exit(1);
                }
            });
        });

        WelcomeScreen.PrintWelcomeBanner(port, bindAddress, settingsService, version);

        await sessionManager.DiscoverExistingSessionsAsync(shutdownService.Token);
        foreach (var session in sessionManager.GetAllSessions())
        {
            var persistedRepos = sessionManager.GetPersistedSessionExtraGitRepos(session.Id);
            if (persistedRepos.Length > 0)
            {
                await gitWatcher.RestoreSessionExtraReposAsync(session.Id, persistedRepos);
            }
        }
        await spaceService.ReconcileSessionBindingsAsync(sessionManager, shutdownService.Token);
        managerBarQueueService.PruneToValidSessions(sessionManager.GetAllSessions().Select(s => s.Id));
        managerBarQueueService.Start();
        var layoutSnapshot = layoutStateService.PruneToValidSessions(sessionManager.GetAllSessions().Select(s => s.Id));
        tmuxLayoutBridge?.UpdateLayout(layoutSnapshot.Root);
        await appServerControlRuntime.DiscoverExistingSessionsAsync(sessionManager, shutdownService.Token);
        sleepInhibitorService.UpdateSessionCount(sessionManager.GetAllSessions().Count);

        async Task CleanupAsync()
        {
            if (Interlocked.Exchange(ref cleanupStarted, 1) != 0)
            {
                return;
            }

            Log.Info(() => "Disposing managers...");

            try
            {
                sessionManager.RemoveStateListener(sleepInhibitorStateListenerId);
                using var cleanupCts = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                await muxManager.DisposeAsync().AsTask().WaitAsync(cleanupCts.Token);
                await sessionManager.DisposeAsync().AsTask().WaitAsync(cleanupCts.Token);
            }
            catch (OperationCanceledException)
            {
                Log.Warn(() => "Cleanup timed out after 8 seconds");
            }
            catch (Exception ex)
            {
                Log.Warn(() => $"Cleanup error: {ex.Message}");
            }
            finally
            {
                sleepInhibitorService.Dispose();
                gitWatcher.Dispose();
                TmuxLog.Shutdown();
                TmuxScriptWriter.Cleanup();
                BrowserLog.Shutdown();
                BrowserScriptWriter.Cleanup();
                tempCleanupService.CleanupAllMidTermFiles();
                Log.Shutdown();
                shutdownService.Dispose();
            }
        }

        WriteEventLog(string.Create(CultureInfo.InvariantCulture, $"MainCore: Starting server on https://{bindAddress}:{port}"));

        try
        {
            app.Urls.Add(string.Create(CultureInfo.InvariantCulture, $"https://{bindAddress}:{port}"));
            browserPreviewOriginService.ApplyUrls(app, bindAddress);
            WelcomeScreen.RunWithPortErrorHandling(app, port, bindAddress, WriteEventLogWrapper);
        }
        finally
        {
            await CleanupAsync();
        }
    }
}

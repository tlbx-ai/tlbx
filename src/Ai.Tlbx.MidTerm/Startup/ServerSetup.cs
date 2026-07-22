using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using Ai.Tlbx.MidTerm.Common.Shells;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Settings;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;

using Ai.Tlbx.MidTerm.Services.Updates;
using Ai.Tlbx.MidTerm.Services.StaticFiles;
using Ai.Tlbx.MidTerm.Services.Certificates;
using Ai.Tlbx.MidTerm.Services.WebPreview;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Services.Git;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Hosting;
using Ai.Tlbx.MidTerm.Services.Share;
using Ai.Tlbx.MidTerm.Services.Security;
using Ai.Tlbx.MidTerm.Services.Power;
using Ai.Tlbx.MidTerm.Services.Hub;
using Ai.Tlbx.MidTerm.Services.Spaces;
using Microsoft.AspNetCore.ResponseCompression;

namespace Ai.Tlbx.MidTerm.Startup;

public static class ServerSetup
{
    public static X509Certificate2? LoadedCertificate { get; private set; }
    public static bool IsFallbackCertificate { get; private set; }

    public static WebApplicationBuilder CreateBuilder(string[] args, Action<string, bool>? writeEventLog = null)
    {
        writeEventLog?.Invoke("CreateBuilder: Starting", false);

        var runtimeOptions = ArgumentParser.ParseOptions(args);
        runtimeOptions.ApplyProcessEnvironment();

        var builder = WebApplication.CreateSlimBuilder(args);

#if WINDOWS
        writeEventLog?.Invoke("CreateBuilder: Configuring Windows service", false);
        builder.Host.UseWindowsService();
#endif

        writeEventLog?.Invoke("CreateBuilder: Loading settings", false);

        var settingsService = new SettingsService();
        var settings = settingsService.Load();

        writeEventLog?.Invoke($"CreateBuilder: Settings loaded - CertPath={settings.CertificatePath}, KeyProtection={settings.KeyProtection}, IsService={settingsService.IsRunningAsService}", false);

        builder.WebHost.UseKestrelHttpsConfiguration();

        writeEventLog?.Invoke("CreateBuilder: Configuring Kestrel", false);

        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;

            writeEventLog?.Invoke("ConfigureKestrel: Loading certificate", false);

            var cert = CertificateSetup.LoadOrGenerateCertificate(settings, settingsService, writeEventLog);
            if (cert is null)
            {
                writeEventLog?.Invoke("ConfigureKestrel: Certificate load failed, using fallback", true);
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine("Warning: Using emergency fallback certificate.");
                Console.WriteLine("         Run 'mt --generate-cert' to create a proper certificate.");
                Console.ResetColor();
                cert = CertificateGenerator.GenerateSelfSigned(["localhost"], ["127.0.0.1"], useEcdsa: true);
                IsFallbackCertificate = true;
            }

            writeEventLog?.Invoke($"ConfigureKestrel: Certificate loaded - Subject={cert.Subject}, HasPrivateKey={cert.HasPrivateKey}", false);

            LoadedCertificate?.Dispose();
            LoadedCertificate = cert;

            options.ConfigureHttpsDefaults(httpsOptions =>
            {
                httpsOptions.ServerCertificate = cert;

                httpsOptions.SslProtocols = System.Security.Authentication.SslProtocols.Tls12
                                            | System.Security.Authentication.SslProtocols.Tls13;

                if (!OperatingSystem.IsWindows())
                {
                    httpsOptions.OnAuthenticate = (context, sslOptions) =>
                    {
#pragma warning disable CA1416
                        sslOptions.CipherSuitesPolicy = new System.Net.Security.CipherSuitesPolicy(
                        [
                            System.Net.Security.TlsCipherSuite.TLS_AES_256_GCM_SHA384,
                            System.Net.Security.TlsCipherSuite.TLS_AES_128_GCM_SHA256,
                            System.Net.Security.TlsCipherSuite.TLS_CHACHA20_POLY1305_SHA256,
                            System.Net.Security.TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_256_GCM_SHA384,
                            System.Net.Security.TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_256_GCM_SHA384,
                            System.Net.Security.TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_AES_128_GCM_SHA256,
                            System.Net.Security.TlsCipherSuite.TLS_ECDHE_RSA_WITH_AES_128_GCM_SHA256,
                            System.Net.Security.TlsCipherSuite.TLS_ECDHE_ECDSA_WITH_CHACHA20_POLY1305_SHA256,
                            System.Net.Security.TlsCipherSuite.TLS_ECDHE_RSA_WITH_CHACHA20_POLY1305_SHA256,
                        ]);
#pragma warning restore CA1416
                    };
                }
            });
        });
        builder.Logging.SetMinimumLevel(Microsoft.Extensions.Logging.LogLevel.Warning);
        builder.Logging.AddFilter("Microsoft.Extensions.Hosting.Internal.Host", Microsoft.Extensions.Logging.LogLevel.Critical);

        builder.Services.ConfigureHttpJsonOptions(options =>
        {
            options.SerializerOptions.TypeInfoResolverChain.Insert(0, AppJsonContext.Default);
        });

        builder.Services.AddSingleton(settingsService);
        builder.Services.AddSingleton(runtimeOptions);
        builder.Services.AddSingleton(runtimeOptions.ServiceIdentity);
        builder.Services.AddSingleton(sp =>
        {
            return new ServerBindingInfo(runtimeOptions.Port, runtimeOptions.BindAddress);
        });
        builder.Services.AddSingleton(sp =>
        {
            var binding = sp.GetRequiredService<ServerBindingInfo>();
            return MidTermInstanceIdentity.Load(settingsService.SettingsDirectory, binding.Port);
        });
        builder.Services.AddSingleton<ShellRegistry>();
        builder.Services.AddSingleton<UpdateService>();
        builder.Services.AddSingleton<AuthService>();
        builder.Services.AddSingleton<ShareGrantService>();
        builder.Services.AddSingleton<TempCleanupService>();
        builder.Services.AddSingleton<CertificateInfoService>();
        builder.Services.AddSingleton<SecurityStatusService>();
        builder.Services.AddSingleton<ApiKeyService>();
        builder.Services.AddSingleton<IPowerShellCommandRunner, WindowsPowerShellCommandRunner>();
        builder.Services.AddSingleton<WindowsFirewallService>();
        builder.Services.AddSingleton<MainBrowserService>();
        builder.Services.AddSingleton<BackgroundImageService>();
        builder.Services.AddSingleton<ClipboardService>();
        builder.Services.AddSingleton<SystemSleepInhibitorService>();
        builder.Services.AddSingleton<SessionControlStateService>();
        builder.Services.AddSingleton<TerminalSizeControlService>();
        builder.Services.AddSingleton<SessionUpdateStateService>();
        builder.Services.AddSingleton<SessionLayoutStateService>();
        builder.Services.AddSingleton<SessionTelemetryService>();
        builder.Services.AddSingleton<SessionHeatService>();
        builder.Services.AddSingleton<IManagerBarQueueRuntime, ManagerBarQueueRuntime>();
        builder.Services.AddSingleton<ManagerBarQueueService>();
        builder.Services.AddSingleton<AiCliProfileService>();
        builder.Services.AddSingleton<AiCliCapabilityService>();
        builder.Services.AddSingleton<SessionForegroundProcessService>();
        builder.Services.AddSingleton<SessionAgentFeedService>();
        builder.Services.AddSingleton<SessionAppServerControlHostRuntimeService>();
        builder.Services.AddSingleton<SessionSupervisorService>();
        builder.Services.AddSingleton<SessionAppServerControlRuntimeService>();
        builder.Services.AddSingleton<ISessionAppServerControlHeatSource>(static services =>
            services.GetRequiredService<SessionAppServerControlRuntimeService>());
        builder.Services.AddSingleton<SessionCodexHandoffService>();
        builder.Services.AddSingleton<ProviderResumeCatalogService>();
        builder.Services.AddSingleton<SessionAgentVibeService>();
        builder.Services.AddSingleton<WorkerSessionRegistryService>();
        builder.Services.AddSingleton<TtyHostSessionManager>(_ =>
            new TtyHostSessionManager(
                runAsUser: settings.RunAsUser,
                runAsUserSid: settings.RunAsUserSid,
                isServiceMode: settingsService.IsRunningAsService,
                sessionControlStateService: _.GetRequiredService<SessionControlStateService>(),
                sessionLayoutStateService: _.GetRequiredService<SessionLayoutStateService>(),
                instanceIdentity: _.GetRequiredService<MidTermInstanceIdentity>(),
                foregroundProcessService: _.GetRequiredService<SessionForegroundProcessService>(),
                settingsService: _.GetRequiredService<SettingsService>()));
        builder.Services.AddSingleton<TtyHostMuxConnectionManager>();
        builder.Services.AddSingleton<HistoryService>();
        builder.Services.AddSingleton<InputHistoryService>();
        builder.Services.AddSingleton<ControlPlaneService>();
        builder.Services.AddSingleton<SpaceService>();
        builder.Services.AddSingleton<SessionPathAllowlistService>();
        builder.Services.AddSingleton<GitWatcherService>();
        builder.Services.AddSingleton<CommandService>();
        builder.Services.AddSingleton<ShutdownService>();
        builder.Services.AddSingleton<HubService>();
        builder.Services.AddSingleton<HubMuxWebSocketHandler>();
        builder.Services.AddSingleton<HubStateWebSocketHandler>();
        builder.Services.AddSingleton(sp =>
        {
            return BrowserPreviewOriginService.Create(runtimeOptions.Port, runtimeOptions.BindAddress);
        });
        builder.Services.AddSingleton<BrowserPreviewRegistry>();
        builder.Services.AddSingleton<BrowserPreviewOwnerService>();
        builder.Services.AddSingleton<BrowserCommandService>();
        builder.Services.AddSingleton<BrowserUiBridge>();
        builder.Services.AddSingleton<WebPreviewService>(sp =>
        {
            var cookiesDir = Path.Combine(settingsService.SettingsDirectory, "cookies");
            return new WebPreviewService(
                runtimeOptions.Port,
                sp.GetRequiredService<BrowserPreviewOriginService>(),
                cookiesDir);
        });

        builder.Services.AddResponseCompression(options =>
        {
            options.EnableForHttps = true;
            options.MimeTypes = ["application/json", "text/plain"];
        });

        return builder;
    }

    public static void ConfigureStaticFiles(WebApplication app)
    {
        var sourceDevMode = IsSourceDevLaunchMode();
        var sourceDevAssetVersion = sourceDevMode
            ? string.Create(CultureInfo.InvariantCulture, $"dev-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}")
            : null;
#if DEBUG
        var configuredSourceWebRoot = Environment.GetEnvironmentVariable("MIDTERM_SOURCE_WWWROOT");
        var wwwrootPath = !string.IsNullOrWhiteSpace(configuredSourceWebRoot)
            ? configuredSourceWebRoot
            : Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "wwwroot");
        IFileProvider fileProvider = Directory.Exists(wwwrootPath)
            ? new PhysicalFileProvider(Path.GetFullPath(wwwrootPath))
            : new EmbeddedWebRootFileProvider(Assembly.GetExecutingAssembly(), "Ai.Tlbx.MidTerm");
        var useCompressedFiles = false;
#else
        IFileProvider fileProvider = new EmbeddedWebRootFileProvider(
            Assembly.GetExecutingAssembly(),
            "Ai.Tlbx.MidTerm");
        var useCompressedFiles = true;
#endif

        // Rewrite clean URLs and consolidate icon requests
        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value;
            if (path == "/trust" || path == "/login")
            {
                context.Request.Path = path + ".html";
            }
            else if (path == "/swagger")
            {
                context.Request.Path = "/swagger/index.html";
            }
            else if (path == "/shared" || (path?.StartsWith("/shared/", StringComparison.Ordinal) ?? false))
            {
                context.Request.Path = "/index.html";
            }
            else if (path == "/apple-touch-icon.png")
            {
                context.Request.Path = "/android-chrome-192x192.png";
            }
            await next();
        });

        app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = fileProvider });

        if (sourceDevMode && fileProvider is PhysicalFileProvider)
        {
            app.Use(async (context, next) =>
            {
                if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
                {
                    await next();
                    return;
                }

                var path = context.Request.Path.Value ?? string.Empty;
                if (!StaticAssetCacheHeaders.IsHtmlEntryPoint(path))
                {
                    await next();
                    return;
                }

                var fileInfo = fileProvider.GetFileInfo(path);
                if (!fileInfo.Exists)
                {
                    await next();
                    return;
                }

                await using var stream = fileInfo.CreateReadStream();
                using var reader = new StreamReader(stream);
                var html = await reader.ReadToEndAsync(context.RequestAborted);
                var stampedHtml = StaticAssetCacheHeaders.StampHtmlAssetUrls(
                    html,
                    sourceDevAssetVersion ?? "dev");

                context.Response.StatusCode = StatusCodes.Status200OK;
                context.Response.ContentType = "text/html; charset=utf-8";
                context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                context.Response.Headers.Pragma = "no-cache";
                await context.Response.WriteAsync(stampedHtml, context.RequestAborted);
            });
        }

        // In release builds, serve pre-compressed .br files for text assets
        if (useCompressedFiles)
        {
            app.UseMiddleware<CompressedStaticFilesMiddleware>(fileProvider);
        }

        var contentTypeProvider = new FileExtensionContentTypeProvider();
        contentTypeProvider.Mappings[".ico"] = "image/x-icon";
        contentTypeProvider.Mappings[".webmanifest"] = "application/manifest+json";
        contentTypeProvider.Mappings[".br"] = "application/octet-stream";
        contentTypeProvider.Mappings[".woff"] = "font/woff";
        contentTypeProvider.Mappings[".woff2"] = "font/woff2";
        contentTypeProvider.Mappings[".ttf"] = "font/ttf";
        contentTypeProvider.Mappings[".eot"] = "application/vnd.ms-fontobject";
        contentTypeProvider.Mappings[".zip"] = "application/zip";

        app.UseStaticFiles(new StaticFileOptions
        {
            FileProvider = fileProvider,
            ContentTypeProvider = contentTypeProvider,
            OnPrepareResponse = ctx =>
            {
                var path = ctx.Context.Request.Path.Value ?? "";
                var isFont = StaticAssetCacheHeaders.IsFontAsset(path);

                if (isFont)
                {
                    ctx.Context.Response.Headers.CacheControl = "public, max-age=31536000, immutable";
                    ctx.Context.Response.Headers["Access-Control-Allow-Origin"] = "*";
                }
                else
                {
#if DEBUG
                    ctx.Context.Response.Headers.Remove("ETag");
                    ctx.Context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
                    ctx.Context.Response.Headers.Pragma = "no-cache";
#else
                    // Let StaticFileMiddleware keep its built-in per-file validators.
                    ctx.Context.Response.Headers.CacheControl = StaticAssetCacheHeaders.GetCacheControl(path);
#endif
                }
            }
        });

    }

    internal static bool IsSourceDevLaunchMode()
    {
        return string.Equals(
            Environment.GetEnvironmentVariable("MIDTERM_LAUNCH_MODE"),
            "source-dev",
            StringComparison.OrdinalIgnoreCase);
    }

    public static void ConfigureMiddleware(
        WebApplication app,
        SettingsService settingsService,
        AuthService authService,
        ShareGrantService shareGrantService,
        BrowserPreviewOriginService previewOriginService,
        BrowserPreviewRegistry previewRegistry)
    {
        app.UseResponseCompression();

        // HSTS middleware - always enabled (HTTPS only)
        app.Use(async (context, next) =>
        {
            context.Response.Headers.StrictTransportSecurity = "max-age=31536000; includeSubDomains";
            await next();
        });

        app.Use(async (context, next) =>
        {
            var path = context.Request.Path.Value ?? "/";
            if (previewOriginService.IsPreviewRequest(context)
                && previewOriginService.ShouldBlockPath(path)
                && !WebPreviewProxyMiddleware.ShouldProxyPreviewLeak(context.Request, path))
            {
                context.Response.StatusCode = 404;
                return;
            }

            await next();
        });

        // Auth middleware must run BEFORE static files so unauthenticated users get redirected to login
        AuthMiddleware.ConfigureAuthMiddleware(
            app,
            settingsService,
            authService,
            shareGrantService,
            previewOriginService,
            previewRegistry);

        // WebSockets must be enabled before the web preview proxy middleware so that
        // context.WebSockets.IsWebSocketRequest is true for proxied WebSocket upgrades
        app.UseWebSockets(new WebSocketOptions
        {
            KeepAliveInterval = TimeSpan.FromSeconds(30)
        });

        // Web preview reverse proxy — after auth, before security headers (short-circuits for /webpreview/*)
        app.UseMiddleware<WebPreviewProxyMiddleware>();

        // Security headers middleware
        app.Use(async (context, next) =>
        {
            var headers = context.Response.Headers;
            headers["X-Frame-Options"] = "SAMEORIGIN";
            headers["X-Content-Type-Options"] = "nosniff";
            headers["Referrer-Policy"] = "strict-origin-when-cross-origin";

            headers.ContentSecurityPolicy = BuildContentSecurityPolicy(
                previewOriginService.GetOrigin(context.Request));

            await next();
        });

        ConfigureStaticFiles(app);
    }

    internal static string BuildContentSecurityPolicy(string? previewOrigin = null)
    {
        var frameSources = new List<string> { "'self'", "blob:", "data:" };
        if (!string.IsNullOrWhiteSpace(previewOrigin))
        {
            frameSources.Add(previewOrigin);
        }

        return "default-src 'self'; " +
               "script-src 'self'; " +
               "worker-src 'self' blob:; " +
               "style-src 'self' 'unsafe-inline'; " +
               "img-src 'self' data:; " +
               "font-src 'self' data:; " +
               "connect-src 'self' ws: wss: https://api.github.com https://api.tlbx.ai https://midterm.tlbx.ai; " +
               $"frame-src {string.Join(' ', frameSources)}; " +
               "frame-ancestors 'self'";
    }
}

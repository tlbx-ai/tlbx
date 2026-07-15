using System.Globalization;
using System.Security.Cryptography.X509Certificates;
using System.Net.Sockets;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Settings;

using Ai.Tlbx.MidTerm.Services.Updates;
namespace Ai.Tlbx.MidTerm.Startup;

public static class WelcomeScreen
{
    public static void PrintWelcomeBanner(int port, string bindAddress, SettingsService settingsService, string version)
    {
        var settings = settingsService.Load();

        Console.WriteLine();

        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(@"          _______________");
        Console.WriteLine(@"         /______________/|");
        Console.Write(@"        |       (");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.Write("o");
        Console.ForegroundColor = ConsoleColor.White;
        Console.WriteLine(@")      ||");
        Console.WriteLine(@"        | \            /||");
        Console.WriteLine(@"        |  \__________/ |/");
        Console.WriteLine(@"        '---------------'");
        Console.WriteLine();
        Console.WriteLine("              tlbx");
        Console.WriteLine("     browser control station");
        Console.WriteLine();
        Console.Write("  ");
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine("https://tlbx.ai  |  github.com/tlbx-ai/tlbx");

        Console.ResetColor();
        Console.WriteLine();

        var platform = OperatingSystem.IsWindows() ? "Windows"
            : OperatingSystem.IsMacOS() ? "macOS"
            : OperatingSystem.IsLinux() ? "Linux"
            : "Unknown";

        Console.WriteLine($"  Version:  {version}");
        Console.WriteLine($"  Platform: {platform}");
        Console.WriteLine($"  Shell:    {settings.DefaultShell}");
        Console.Write($"  Mode:     ");
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine("Service (subprocess per terminal)");
        Console.ResetColor();
        Console.WriteLine();

        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Listening on https://{bindAddress}:{port}"));
        Console.WriteLine();

        switch (settingsService.LoadStatus)
        {
            case SettingsLoadStatus.LoadedFromFile:
                Console.WriteLine($"  Settings: Loaded from {settingsService.SettingsPath}");
                break;
            case SettingsLoadStatus.ErrorFallbackToDefault:
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  Settings: Error loading {settingsService.SettingsPath}");
                Console.WriteLine($"            {settingsService.LoadError}");
                Console.WriteLine($"            Using default settings");
                Console.ResetColor();
                break;
            default:
                Console.WriteLine($"  Settings: Using defaults (no settings file)");
                break;
        }

        var isNetworkBound = bindAddress != "127.0.0.1" && bindAddress != "localhost";
        var hasNoPassword = string.IsNullOrEmpty(settings.PasswordHash) || !settings.AuthenticationEnabled;
        if (isNetworkBound && hasNoPassword)
        {
            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("  WARNING: Listening on network interface without authentication!");
            Console.WriteLine("           Set a password in settings to secure access.");
            Console.ResetColor();
        }

        Console.WriteLine();
    }

    public static void LogStartupStatus(
        SettingsService settingsService,
        MidTermSettings settings,
        int port,
        string bindAddress,
        X509Certificate2? loadedCertificate,
        bool isFallbackCertificate)
    {
        var settingsStatus = settingsService.LoadStatus switch
        {
            SettingsLoadStatus.LoadedFromFile => $"loaded from {settingsService.SettingsPath}",
            SettingsLoadStatus.MigratedFromOld => $"migrated from {settingsService.SettingsPath}.old",
            SettingsLoadStatus.ErrorFallbackToDefault => $"ERROR loading {settingsService.SettingsPath}: {settingsService.LoadError}",
            _ => "using defaults (no settings file)"
        };
        Log.Info(() => $"Settings: {settingsStatus}");

        Log.Info(() => $"Mode: {(settingsService.IsRunningAsService ? "Service" : "User")}");

        var hasPassword = !string.IsNullOrEmpty(settings.PasswordHash);
        var authEnabled = settings.AuthenticationEnabled;
        if (hasPassword && authEnabled)
        {
            Log.Info(() => "Authentication: enabled (password configured)");
        }
        else if (hasPassword && !authEnabled)
        {
            Log.Warn(() => "Authentication: DISABLED (password exists but auth is disabled)");
        }
        else if (!hasPassword && authEnabled)
        {
            Log.Warn(() => "Authentication: MISCONFIGURED (auth enabled but no password set)");
        }
        else
        {
            var isNetworkBound = bindAddress != "127.0.0.1" && bindAddress != "localhost";
            if (isNetworkBound)
            {
                Log.Warn(() => "Authentication: DISABLED - server exposed on network without password!");
            }
            else
            {
                Log.Info(() => "Authentication: disabled (localhost only)");
            }
        }

        if (loadedCertificate is not null)
        {
            if (isFallbackCertificate)
            {
                Log.Warn(() => "Certificate: using emergency fallback (in-memory generated)");
            }
            else
            {
                var certPath = settings.CertificatePath ?? "unknown";
                var keyProtection = settings.KeyProtection == KeyProtectionMethod.OsProtected ? "OS-protected" : "legacy PFX";
                Log.Info(() => $"Certificate: loaded from {certPath} ({keyProtection})");
            }
        }
        else
        {
            Log.Error(() => "Certificate: FAILED to load - HTTPS will not work!");
        }

        Log.Info(() => string.Create(CultureInfo.InvariantCulture, $"Binding: https://{bindAddress}:{port}"));
    }

    public static void RunWithPortErrorHandling(
        WebApplication app,
        int port,
        string bindAddress,
        Action<string, bool>? writeEventLog = null)
    {
        writeEventLog?.Invoke(string.Create(CultureInfo.InvariantCulture, $"RunWithPortErrorHandling: About to call app.Run with configured URLs (main https://{bindAddress}:{port})"), false);

        try
        {
            app.Run();
            writeEventLog?.Invoke("RunWithPortErrorHandling: app.Run completed normally", false);
        }
        catch (Exception ex) when (TryGetPortBindSocketException(ex, out var socketEx) &&
            IsPortBindFailure(socketEx.SocketErrorCode))
        {
            var launchMode = Environment.GetEnvironmentVariable("MIDTERM_LAUNCH_MODE");
            var manualCommand = launchMode == "npx"
                ? "npx @tlbx-ai/midterm -- --port 2001"
                : "mt --port 2001";

            writeEventLog?.Invoke(
                string.Create(CultureInfo.InvariantCulture, $"RunWithPortErrorHandling: Failed to bind https://{bindAddress}:{port} ({socketEx.SocketErrorCode})"),
                true);
            Log.Error(() => string.Create(CultureInfo.InvariantCulture, $"Failed to bind https://{bindAddress}:{port}: {socketEx.SocketErrorCode}. Exiting."));

            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  Error: tlbx could not bind to https://{bindAddress}:{port}."));
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("  The port is already in use, reserved, or blocked by OS permissions.");
            Console.WriteLine();
            Console.WriteLine("  Try one of the following:");
            if (launchMode == "npx")
            {
                Console.WriteLine("    - Run npx without --port to let the launcher choose a free port");
            }
            Console.WriteLine($"    - Use a different port manually: {manualCommand}");
            Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"    - Close the application using port {port}"));
            Console.WriteLine();
            Environment.Exit(1);
        }
        catch (Exception ex)
        {
            writeEventLog?.Invoke($"RunWithPortErrorHandling: UNEXPECTED ERROR - {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", true);
            throw;
        }
    }

    private static bool TryGetPortBindSocketException(Exception ex, out SocketException socketException)
    {
        if (ex is SocketException directSocketException)
        {
            socketException = directSocketException;
            return true;
        }

        if (ex is IOException ioException && ioException.InnerException is SocketException innerSocketException)
        {
            socketException = innerSocketException;
            return true;
        }

        if (ex.InnerException is SocketException nestedSocketException)
        {
            socketException = nestedSocketException;
            return true;
        }

        socketException = null!;
        return false;
    }

    private static bool IsPortBindFailure(SocketError socketErrorCode)
    {
        return socketErrorCode == SocketError.AddressAlreadyInUse ||
            socketErrorCode == SocketError.AccessDenied;
    }
}

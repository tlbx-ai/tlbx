using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Settings;

using Ai.Tlbx.MidTerm.Services.Updates;
using Ai.Tlbx.MidTerm.Services.Secrets;
namespace Ai.Tlbx.MidTerm.Startup;

public static class CliCommands
{
    public static bool HandleSpecialCommands(string[] args)
    {
        var runtimeOptions = ArgumentParser.ParseOptions(args);
        runtimeOptions.ApplyProcessEnvironment();

        var clipboardWriteImageIdx = Array.IndexOf(args, "--clipboard-write-image");
        if (clipboardWriteImageIdx >= 0)
        {
            HandleClipboardWriteImage(args, clipboardWriteImageIdx);
            return true;
        }

        if (args.Contains("--check-update", StringComparer.Ordinal))
        {
            using var updateService = new UpdateService();
            var update = updateService.CheckForUpdateAsync().GetAwaiter().GetResult();
            if (update is not null && update.Available)
            {
                Console.WriteLine($"Update available: {update.CurrentVersion} -> {update.LatestVersion}");
                Console.WriteLine($"Download: {update.ReleaseUrl}");
            }
            else
            {
                Console.WriteLine($"You are running the latest version ({updateService.CurrentVersion})");
            }
            return true;
        }

        if (args.Contains("--update", StringComparer.Ordinal) || args.Contains("--apply-update", StringComparer.Ordinal))
        {
            using var updateService = new UpdateService();
            Console.WriteLine("Checking for updates...");
            var update = updateService.CheckForUpdateAsync().GetAwaiter().GetResult();

            if (update is null || !update.Available)
            {
                Console.WriteLine($"You are running the latest version ({updateService.CurrentVersion})");
                return true;
            }

            Console.WriteLine($"Downloading {update.LatestVersion}...");
            var extractedDir = updateService.DownloadUpdateAsync().GetAwaiter().GetResult();

            if (string.IsNullOrEmpty(extractedDir))
            {
                Console.WriteLine("Failed to download update.");
                return true;
            }

            Console.WriteLine("Applying update...");
            var settingsService = new SettingsService();
            var scriptPath = UpdateScriptGenerator.GenerateUpdateScript(
                extractedDir,
                UpdateService.GetCurrentBinaryPath(),
                settingsService.SettingsDirectory,
                update.Type);
            UpdateScriptGenerator.ExecuteUpdateScript(scriptPath);
            Console.WriteLine("Update script started. Exiting...");
            return true;
        }

        if (args.Contains("--version", StringComparer.Ordinal) || args.Contains("-v", StringComparer.Ordinal))
        {
            Console.WriteLine(GetVersion());
            return true;
        }

        if (args.Contains("--help", StringComparer.Ordinal) || args.Contains("-h", StringComparer.Ordinal))
        {
            PrintHelp();
            return true;
        }

        if (args.Contains("--hash-password", StringComparer.Ordinal))
        {
            string password;
            if (Console.IsInputRedirected)
            {
                password = Console.ReadLine() ?? "";
            }
            else
            {
                Console.Error.Write("Enter password: ");
                password = ReadPasswordMasked();
            }

            if (string.IsNullOrEmpty(password))
            {
                Console.Error.WriteLine("Error: Password cannot be empty");
                Environment.Exit(1);
            }

            // Use static hash method to avoid AuthService constructor side effects
            // (which tries to save session secrets and can fail on macOS under sudo)
            Console.WriteLine(AuthService.HashPasswordStatic(password));
            return true;
        }

        var writeSecretIdx = Array.IndexOf(args, "--write-secret");
        if (writeSecretIdx >= 0)
        {
            if (writeSecretIdx + 1 >= args.Length)
            {
                Console.Error.WriteLine("Error: --write-secret requires a key name");
                Console.Error.WriteLine("Usage: mt --write-secret <key> [--service-mode]");
                Console.Error.WriteLine("Keys: password_hash, session_secret, certificate_password");
                Environment.Exit(1);
            }

            var keyArg = args[writeSecretIdx + 1];
            var secretKey = keyArg switch
            {
                "password_hash" => SecretKeys.PasswordHash,
                "session_secret" => SecretKeys.SessionSecret,
                "certificate_password" => SecretKeys.CertificatePassword,
                _ => null
            };

            if (secretKey is null)
            {
                Console.Error.WriteLine($"Error: Unknown secret key '{keyArg}'");
                Console.Error.WriteLine("Valid keys: password_hash, session_secret, certificate_password");
                Environment.Exit(1);
            }

            string value;
            if (Console.IsInputRedirected)
            {
                value = Console.ReadLine() ?? "";
            }
            else
            {
                Console.Error.Write($"Enter {keyArg}: ");
                value = ReadPasswordMasked();
            }

            if (string.IsNullOrEmpty(value))
            {
                Console.Error.WriteLine("Error: Value cannot be empty");
                Environment.Exit(1);
            }

            var serviceMode = args.Contains("--service-mode", StringComparer.Ordinal);
            ISecretStorage secretStorage;
            if (serviceMode)
            {
                var settingsDir = runtimeOptions.SettingsDirectory;
                if (string.IsNullOrWhiteSpace(settingsDir))
                {
                    if (OperatingSystem.IsWindows())
                    {
                        var programData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
                        settingsDir = Path.Combine(programData, "MidTerm");
                    }
                    else
                    {
                        settingsDir = "/usr/local/etc/midterm";
                    }
                }

                secretStorage = SecretStorageFactory.Create(settingsDir, isServiceMode: true);
            }
            else
            {
                var settingsService = new SettingsService();
                secretStorage = settingsService.SecretStorage;
            }

            secretStorage.SetSecret(secretKey, value);
            Console.WriteLine($"Secret '{keyArg}' stored successfully");
            return true;
        }

        if (args.Contains("--generate-cert", StringComparer.Ordinal))
        {
            var force = args.Contains("--force", StringComparer.Ordinal);
            var serviceMode = args.Contains("--service-mode", StringComparer.Ordinal);
            CertificateSetup.GenerateCertificateCommand(force, serviceMode);
            return true;
        }

        return false;
    }

    private static void HandleClipboardWriteImage(string[] args, int clipboardWriteImageIdx)
    {
        if (clipboardWriteImageIdx + 1 >= args.Length)
        {
            Console.Error.WriteLine("Error: --clipboard-write-image requires a file path");
            Environment.Exit(1);
        }

        var filePath = args[clipboardWriteImageIdx + 1];
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            Console.Error.WriteLine("Error: Clipboard source file not found");
            Environment.Exit(1);
        }

        var mimeType = TryGetFlagValue(args, "--mime");
        var success = WriteClipboardImageAsync(filePath, mimeType).GetAwaiter().GetResult();
        if (!success)
        {
            Environment.Exit(1);
        }
    }

    private static async Task<bool> WriteClipboardImageAsync(string filePath, string? mimeType)
    {
        var resolvedMimeType = ResolveMimeType(filePath, mimeType);
        if (resolvedMimeType is null)
        {
            Console.Error.WriteLine("Error: Unsupported clipboard image type");
            return false;
        }

        if (OperatingSystem.IsWindows())
        {
            return await WriteClipboardImageWindowsAsync(filePath);
        }

        if (OperatingSystem.IsMacOS())
        {
            return await WriteClipboardImageMacOsDirectAsync(filePath);
        }

        if (OperatingSystem.IsLinux())
        {
            return await WriteClipboardImageLinuxAsync(filePath, resolvedMimeType);
        }

        Console.Error.WriteLine("Error: Clipboard image paste is not supported on this OS");
        return false;
    }

    private static async Task<bool> WriteClipboardImageWindowsAsync(string filePath)
    {
        var escapedPath = filePath.Replace("'", "''", StringComparison.Ordinal);
        var script =
            "Add-Type -AssemblyName System.Windows.Forms; " +
            "Add-Type -AssemblyName System.Drawing; " +
            $"$path = '{escapedPath}'; " +
            "$image = [System.Drawing.Image]::FromFile($path); " +
            "try { " +
            "  $data = New-Object System.Windows.Forms.DataObject; " +
            "  $data.SetData([System.Windows.Forms.DataFormats]::Bitmap, $image); " +
            "  $files = New-Object System.Collections.Specialized.StringCollection; " +
            "  [void]$files.Add($path); " +
            "  $data.SetFileDropList($files); " +
            "  [System.Windows.Forms.Clipboard]::SetDataObject($data, $true); " +
            "} finally { " +
            "  $image.Dispose(); " +
            "}";

        var encodedScript = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var result = await RunProcessCaptureAsync("powershell.exe", ["-NoProfile", "-STA", "-EncodedCommand", encodedScript]);
        if (result.Started && result.ExitCode == 0)
        {
            return true;
        }

        var error = string.IsNullOrWhiteSpace(result.Stderr)
            ? (result.Started ? string.Create(CultureInfo.InvariantCulture, $"powershell.exe exited with code {result.ExitCode}") : result.Error)
            : result.Stderr.Trim();
        Console.Error.WriteLine(error);
        return false;
    }

    private static async Task<bool> WriteClipboardImageMacOsDirectAsync(string filePath)
    {
        var escapedPath = filePath.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
        var script = $"set the clipboard to (read (POSIX file \"{escapedPath}\") as picture)";
        var result = await RunProcessCaptureAsync("/usr/bin/osascript", ["-e", script]);
        if (result.Started && result.ExitCode == 0)
        {
            return true;
        }

        var error = string.IsNullOrWhiteSpace(result.Stderr)
            ? (result.Started ? string.Create(CultureInfo.InvariantCulture, $"/usr/bin/osascript exited with code {result.ExitCode}") : result.Error)
            : result.Stderr.Trim();
        Console.Error.WriteLine(error);
        return false;
    }

    private static async Task<bool> WriteClipboardImageLinuxAsync(string filePath, string mimeType)
    {
        var waylandResult = await RunProcessCaptureAsync(
            "wl-copy",
            ["--type", mimeType],
            standardInputFilePath: filePath);
        if (waylandResult.Started && waylandResult.ExitCode == 0)
        {
            return true;
        }

        var xclipResult = await RunProcessCaptureAsync(
            "xclip",
            ["-selection", "clipboard", "-t", mimeType, "-i", filePath]);
        if (xclipResult.Started && xclipResult.ExitCode == 0)
        {
            return true;
        }

        var error = !string.IsNullOrWhiteSpace(xclipResult.Stderr)
            ? xclipResult.Stderr.Trim()
            : !string.IsNullOrWhiteSpace(waylandResult.Stderr)
                ? waylandResult.Stderr.Trim()
                : xclipResult.Started
                    ? string.Create(CultureInfo.InvariantCulture, $"xclip exited with code {xclipResult.ExitCode}")
                    : waylandResult.Started
                        ? string.Create(CultureInfo.InvariantCulture, $"wl-copy exited with code {waylandResult.ExitCode}")
                        : xclipResult.Error;
        Console.Error.WriteLine(error);
        return false;
    }

    private static async Task<(bool Started, int ExitCode, string Stdout, string Stderr, string Error)> RunProcessCaptureAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInputFilePath = null)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = fileName,
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    RedirectStandardInput = standardInputFilePath is not null
                }
            };

            foreach (var argument in arguments)
            {
                process.StartInfo.ArgumentList.Add(argument);
            }

            process.Start();

            if (standardInputFilePath is not null)
            {
                await using var inputFile = File.OpenRead(standardInputFilePath);
                await inputFile.CopyToAsync(process.StandardInput.BaseStream);
                await process.StandardInput.FlushAsync();
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            var stdout = await process.StandardOutput.ReadToEndAsync();
            var stderr = await process.StandardError.ReadToEndAsync();
            return (true, process.ExitCode, stdout, stderr, string.Empty);
        }
        catch (Exception ex)
        {
            return (false, -1, string.Empty, string.Empty, ex.Message);
        }
    }

    private static string? ResolveMimeType(string filePath, string? mimeType)
    {
        if (!string.IsNullOrWhiteSpace(mimeType) &&
            mimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeMimeType(mimeType);
        }

        return Path.GetExtension(filePath).ToLowerInvariant() switch
        {
            ".png" => "image/png",
            ".jpg" => "image/jpeg",
            ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".bmp" => "image/bmp",
            ".webp" => "image/webp",
            ".tif" => "image/tiff",
            ".tiff" => "image/tiff",
            _ => null
        };
    }

    private static string NormalizeMimeType(string mimeType)
    {
        var normalized = mimeType.Trim().ToLowerInvariant();
        return normalized == "image/jpg" ? "image/jpeg" : normalized;
    }

    private static string? TryGetFlagValue(string[] args, string flag)
    {
        var idx = Array.IndexOf(args, flag);
        if (idx < 0 || idx + 1 >= args.Length)
        {
            return null;
        }

        return args[idx + 1];
    }

    public static void PrintHelp()
    {
        Console.WriteLine($"MidTerm {GetVersion()} - Web-based Terminal Multiplexer");
        Console.WriteLine();
        Console.WriteLine("Usage: mt [options]");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --port <port>       Set listening port (default: 2000)");
        Console.WriteLine("  --bind <address>    Set bind address (default: 0.0.0.0)");
        Console.WriteLine("  --version, -v       Show version");
        Console.WriteLine("  --help, -h          Show this help");
        Console.WriteLine("  --hash-password     Hash a password (reads from stdin)");
        Console.WriteLine("  --write-secret <k>  Store secret (reads value from stdin)");
        Console.WriteLine("                      Keys: password_hash, session_secret, certificate_password");
        Console.WriteLine("  --generate-cert     Generate HTTPS certificate (add --service-mode for service install)");
        Console.WriteLine("  --apply-update      Download and apply latest update");
        Console.WriteLine();
        Console.WriteLine("Password Recovery:");
        Console.WriteLine("  If you forget your password:");
        Console.WriteLine("  1. Stop the MidTerm service");
        Console.WriteLine("  2. Edit settings.json (location shown on startup)");
        Console.WriteLine("  3. Set \"authenticationEnabled\" to false");
        Console.WriteLine("  4. Restart MidTerm");
        Console.WriteLine("  5. Set new password in Settings > Security");
        Console.WriteLine();
        Console.WriteLine("Settings locations:");
        Console.WriteLine("  Service: %ProgramData%\\MidTerm\\settings.json (Windows)");
        Console.WriteLine("           /usr/local/etc/midterm/settings.json (Unix)");
        Console.WriteLine("  User:    ~/.midterm/settings.json");
    }

    public static string ReadPasswordMasked()
    {
        var password = new System.Text.StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                Console.Error.WriteLine();
                break;
            }
            if (key.Key == ConsoleKey.Backspace && password.Length > 0)
            {
                password.Length--;
                Console.Error.Write("\b \b");
            }
            else if (!char.IsControl(key.KeyChar))
            {
                password.Append(key.KeyChar);
                Console.Error.Write('*');
            }
        }
        return password.ToString();
    }

    public static string GetVersion()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "1.0.0";

        var plusIndex = version.IndexOf('+', StringComparison.Ordinal);
        return plusIndex > 0 ? version[..plusIndex] : version;
    }
}

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using Ai.Tlbx.MidTerm.Common.Logging;

namespace Ai.Tlbx.MidTerm.TtyHost;

internal static class LocalClipboard
{
    private const string MtBinaryPathEnvironmentVariable = "MT_BINARY_PATH";
    private const string MacOsClipboardLabelPrefix = "ai.tlbx.midterm.clipboard.set.";
    private static readonly Regex MacOsExitCodeRegex = new(
        @"\blast exit code = (?<code>-?\d+)\b",
        RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));

    [DllImport("libc", EntryPoint = "geteuid")]
    private static extern uint geteuid();

    public static async Task<(bool Success, string? Error)> SetImageAsync(string filePath, string? mimeType)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            return (false, "Clipboard source file not found");
        }

        var resolvedMimeType = ResolveMimeType(filePath, mimeType);
        if (resolvedMimeType is null)
        {
            return (false, "Unsupported clipboard image type");
        }

        if (OperatingSystem.IsWindows())
        {
            return await SetImageWindowsAsync(filePath, resolvedMimeType);
        }

        if (OperatingSystem.IsMacOS())
        {
            return await SetImageMacOsAsync(filePath, resolvedMimeType);
        }

        if (OperatingSystem.IsLinux())
        {
            return await SetImageLinuxAsync(filePath, resolvedMimeType);
        }

        return (false, "Clipboard image paste is not supported on this OS");
    }

    [SupportedOSPlatform("windows")]
    private static async Task<(bool Success, string? Error)> SetImageWindowsAsync(string filePath, string mimeType)
    {
        var mtBinaryPath = GetMidTermBinaryPath();
        if (string.IsNullOrWhiteSpace(mtBinaryPath) || !File.Exists(mtBinaryPath))
        {
            return (false, "tlbx helper binary not found");
        }

        return await RunProcessAsync(mtBinaryPath, BuildClipboardHelperArguments(filePath, mimeType));
    }

    [SupportedOSPlatform("macos")]
    private static async Task<(bool Success, string? Error)> SetImageMacOsAsync(string filePath, string mimeType)
    {
        var mtBinaryPath = GetMidTermBinaryPath();
        if (string.IsNullOrWhiteSpace(mtBinaryPath) || !File.Exists(mtBinaryPath))
        {
            return (false, "tlbx helper binary not found");
        }

        var directResult = await RunProcessAsync(mtBinaryPath, BuildClipboardHelperArguments(filePath, mimeType), logFailures: false);
        if (directResult.Success)
        {
            return directResult;
        }

        return await SetImageMacOsViaGuiLaunchAgentAsync(filePath, mimeType, mtBinaryPath);
    }

    private static async Task<(bool Success, string? Error)> SetImageLinuxAsync(string filePath, string mimeType)
    {
        var mtBinaryPath = GetMidTermBinaryPath();
        if (string.IsNullOrWhiteSpace(mtBinaryPath) || !File.Exists(mtBinaryPath))
        {
            return (false, "tlbx helper binary not found");
        }

        return await RunProcessAsync(mtBinaryPath, BuildClipboardHelperArguments(filePath, mimeType));
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

    [SupportedOSPlatform("macos")]
    private static async Task<(bool Success, string? Error)> SetImageMacOsViaGuiLaunchAgentAsync(string filePath, string mimeType, string mtBinaryPath)
    {
        var uid = geteuid();
        if (uid == 0)
        {
            return (false, "Clipboard access requires a user GUI session");
        }

        var label = $"{MacOsClipboardLabelPrefix}{Guid.NewGuid():N}";
        var tempDir = Path.Combine(Path.GetTempPath(), "midterm-launchagents");
        Directory.CreateDirectory(tempDir);

        var plistPath = Path.Combine(tempDir, $"{label}.plist");
        var stdoutPath = Path.Combine(tempDir, $"{label}.stdout.log");
        var stderrPath = Path.Combine(tempDir, $"{label}.stderr.log");

        var plist = BuildMacOsLaunchAgentPlist(
            label,
            BuildClipboardHelperProgramArguments(filePath, mimeType, mtBinaryPath),
            stdoutPath,
            stderrPath);

        await File.WriteAllTextAsync(plistPath, plist, Encoding.UTF8);

        await RunProcessAsync("launchctl", ["bootout", $"gui/{uid}/{label}"], logFailures: false);

        var bootstrapResult = await RunProcessCaptureAsync("launchctl", ["bootstrap", $"gui/{uid}", plistPath]);
        if (!bootstrapResult.Started || bootstrapResult.ExitCode != 0)
        {
            if (!string.IsNullOrWhiteSpace(bootstrapResult.Stderr))
            {
                Log.Warn(() => $"[Clipboard] macOS GUI bootstrap failed: {bootstrapResult.Stderr.Trim()}");
            }

            TryDeleteFile(plistPath);
            return (false, string.IsNullOrWhiteSpace(bootstrapResult.Stderr)
                ? "launchctl bootstrap failed"
                : bootstrapResult.Stderr.Trim());
        }

        try
        {
            var exitCode = await WaitForMacOsLaunchAgentExitCodeAsync(uid, label, TimeSpan.FromSeconds(5));
            if (exitCode == 0)
            {
                return (true, null);
            }

            var stderrText = TryReadText(stderrPath);
            if (!string.IsNullOrWhiteSpace(stderrText))
            {
                Log.Warn(() => $"[Clipboard] macOS GUI helper failed: {stderrText.Trim()}");
            }

            return (false, string.IsNullOrWhiteSpace(stderrText)
                ? "launchctl clipboard helper failed"
                : stderrText.Trim());
        }
        finally
        {
            await RunProcessAsync("launchctl", ["bootout", $"gui/{uid}/{label}"], logFailures: false);
            TryDeleteFile(plistPath);
            TryDeleteFile(stdoutPath);
            TryDeleteFile(stderrPath);
        }
    }

    [SupportedOSPlatform("macos")]
    private static async Task<int?> WaitForMacOsLaunchAgentExitCodeAsync(
        uint uid,
        string label,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var result = await RunProcessCaptureAsync("launchctl", ["print", $"gui/{uid}/{label}"]);
            if (result.Started && result.ExitCode == 0)
            {
                var match = MacOsExitCodeRegex.Match(result.Stdout);
                if (match.Success &&
                    int.TryParse(match.Groups["code"].Value, CultureInfo.InvariantCulture, out var parsedExit))
                {
                    return parsedExit;
                }
            }

            await Task.Delay(100);
        }

        return null;
    }

    [SupportedOSPlatform("macos")]
    private static string BuildMacOsLaunchAgentPlist(
        string label,
        IReadOnlyList<string> programArguments,
        string stdoutPath,
        string stderrPath)
    {
        var argsBuilder = new StringBuilder();
        foreach (var argument in programArguments)
        {
            argsBuilder.Append("        <string>");
            argsBuilder.Append(EscapeXml(argument));
            argsBuilder.AppendLine("</string>");
        }

        return $$"""
<?xml version="1.0" encoding="UTF-8"?>
<!DOCTYPE plist PUBLIC "-//Apple//DTD PLIST 1.0//EN" "http://www.apple.com/DTDs/PropertyList-1.0.dtd">
<plist version="1.0">
<dict>
    <key>Label</key>
    <string>{{EscapeXml(label)}}</string>
    <key>ProgramArguments</key>
    <array>
{{argsBuilder}}    </array>
    <key>RunAtLoad</key>
    <true/>
    <key>StandardOutPath</key>
    <string>{{EscapeXml(stdoutPath)}}</string>
    <key>StandardErrorPath</key>
    <string>{{EscapeXml(stderrPath)}}</string>
</dict>
</plist>
""";
    }

    private static string? GetMidTermBinaryPath()
    {
        var envPath = Environment.GetEnvironmentVariable(MtBinaryPathEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(envPath))
        {
            return envPath;
        }

        var currentExe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(currentExe))
        {
            return null;
        }

        var dir = Path.GetDirectoryName(currentExe);
        if (string.IsNullOrWhiteSpace(dir))
        {
            return null;
        }

        var exeName = OperatingSystem.IsWindows() ? "mt.exe" : "mt";
        var sameDirPath = Path.Combine(dir, exeName);
        if (File.Exists(sameDirPath))
        {
            return sameDirPath;
        }

        return sameDirPath;
    }

    private static string[] BuildClipboardHelperArguments(string filePath, string mimeType)
    {
        return ["--clipboard-write-image", filePath, "--mime", mimeType];
    }

    private static string[] BuildClipboardHelperProgramArguments(string filePath, string mimeType, string mtBinaryPath)
    {
        return [mtBinaryPath, .. BuildClipboardHelperArguments(filePath, mimeType)];
    }

    private static string EscapeXml(string value)
    {
        return value
            .Replace("&", "&amp;", StringComparison.Ordinal)
            .Replace("<", "&lt;", StringComparison.Ordinal)
            .Replace(">", "&gt;", StringComparison.Ordinal)
            .Replace("\"", "&quot;", StringComparison.Ordinal)
            .Replace("'", "&apos;", StringComparison.Ordinal);
    }

    private static string? TryReadText(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch
        {
            return null;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best effort cleanup.
        }
    }

    private static async Task<(bool Success, string? Error)> RunProcessAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInputFilePath = null,
        bool logFailures = true)
    {
        var result = await RunProcessCaptureAsync(fileName, arguments, standardInputFilePath);
        if (result.Started && result.ExitCode == 0)
        {
            return (true, null);
        }

        if (!logFailures)
        {
            return (false, result.Stderr);
        }

        if (!result.Started)
        {
            Log.Error(() => $"[Clipboard] Set failed ({fileName}): {result.Stderr}");
            return (false, result.Stderr);
        }

        var error = string.IsNullOrWhiteSpace(result.Stderr)
            ? string.Create(CultureInfo.InvariantCulture, $"{fileName} exited with code {result.ExitCode}")
            : result.Stderr.Trim();
        Log.Warn(() => string.Create(CultureInfo.InvariantCulture, $"[Clipboard] Command failed ({fileName}, exit {result.ExitCode}): {error}"));
        return (false, error);
    }

    private static async Task<(bool Started, int ExitCode, string Stdout, string Stderr)> RunProcessCaptureAsync(
        string fileName,
        IReadOnlyList<string> arguments,
        string? standardInputFilePath = null)
    {
        try
        {
            using var process = new System.Diagnostics.Process
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
            return (true, process.ExitCode, stdout, stderr);
        }
        catch (Exception ex)
        {
            return (false, -1, string.Empty, ex.Message);
        }
    }
}

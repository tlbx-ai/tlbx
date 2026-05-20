using System.Globalization;
using Ai.Tlbx.MidTerm.Common.Logging;

namespace Ai.Tlbx.MidTerm.Services.Tmux;

/// <summary>
/// Writes tmux shim scripts to a fixed directory at startup.
/// On Unix: creates /tmp/midterm-bin/tmux (shell script).
/// On Windows: creates tmux.ps1 + tmux.cmd + tmux in %TEMP%\midterm-bin.
/// The shim scripts null-delimit args and POST them to /api/tmux.
/// Uses a fixed path so existing terminal sessions survive mt restarts.
/// </summary>
public static class TmuxScriptWriter
{
    private static string? _scriptDirectory;

    /// <summary>
    /// The directory containing the tmux script. Used to prepend to PATH.
    /// </summary>
    public static string? ScriptDirectory => _scriptDirectory;

    /// <summary>
    /// Write platform-appropriate tmux shim script(s) and set <see cref="ScriptDirectory"/>.
    /// </summary>
    public static void WriteScript(int port)
    {
        if (OperatingSystem.IsWindows())
        {
            WriteWindowsScript(port);
            return;
        }

        WriteUnixScript(port);
    }

    private static void WriteUnixScript(int port)
    {
        var dir = "/tmp/midterm-bin";
        var endpointUrl = string.Create(CultureInfo.InvariantCulture, $"https://localhost:{port}/api/tmux");

        try
        {
            Directory.CreateDirectory(dir);
            var scriptPath = Path.Combine(dir, "tmux");

            var script = $"""
                #!/bin/sh
                printf '%s\0' "$@" | curl -sfk -b "mm-session=$MT_TOKEN" \
                  -H "X-Tmux-Pane: $TMUX_PANE" --data-binary @- \
                  "{endpointUrl}" 2>/dev/null
                """;

            File.WriteAllText(scriptPath, script);

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(scriptPath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            _scriptDirectory = dir;
            Log.Info(() => string.Create(CultureInfo.InvariantCulture, $"TmuxScriptWriter: Created tmux script at {scriptPath}"));
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"TmuxScriptWriter: Failed to write Unix script: {ex.Message}");
        }
    }

    private static void WriteWindowsScript(int port)
    {
        var dir = Path.Combine(Path.GetTempPath(), "midterm-bin");
        var endpointUrl = string.Create(CultureInfo.InvariantCulture, $"https://localhost:{port}/api/tmux");

        try
        {
            Directory.CreateDirectory(dir);

            var ps1Path = Path.Combine(dir, "tmux.ps1");
            var ps1Script = BuildWindowsPowerShellScript(endpointUrl);
            File.WriteAllText(ps1Path, ps1Script);

            var cmdPath = Path.Combine(dir, "tmux.cmd");
            var cmdScript = """
                @echo off
                pwsh -NoProfile -NoLogo -File "%~dp0tmux.ps1" %*
                """;
            File.WriteAllText(cmdPath, cmdScript);

            WriteWindowsExecutableShim(dir, endpointUrl);

            // Bash-compatible script (no extension) for Git Bash / MSYS2 / WSL
            var bashPath = Path.Combine(dir, "tmux");
            var bashScript = $"""
                #!/bin/sh
                printf '%s\0' "$@" | curl -sfk -b "mm-session=$MT_TOKEN" \
                  -H "X-Tmux-Pane: $TMUX_PANE" --data-binary @- \
                  "{endpointUrl}" 2>/dev/null
                """;
            File.WriteAllText(bashPath, bashScript.Replace("\r\n", "\n", StringComparison.Ordinal));

            _scriptDirectory = dir;
            Log.Info(() => string.Create(CultureInfo.InvariantCulture, $"TmuxScriptWriter: Created tmux scripts at {dir}"));
        }
        catch (Exception ex)
        {
            Log.Warn(() => $"TmuxScriptWriter: Failed to write Windows script: {ex.Message}");
        }
    }

    internal static string BuildWindowsPowerShellScript(string endpointUrl)
    {
        return $$"""
                $body = [System.IO.MemoryStream]::new()
                foreach ($a in $args) {
                    $b = [System.Text.Encoding]::UTF8.GetBytes($a)
                    $body.Write($b, 0, $b.Length)
                    $body.WriteByte(0)
                }
                $tmp = Join-Path $env:TEMP "mt-tmux-$PID.bin"
                [System.IO.File]::WriteAllBytes($tmp, $body.ToArray())
                try {
                    & curl.exe -sfk -b "mm-session=$env:MT_TOKEN" -H "X-Tmux-Pane: $env:TMUX_PANE" --data-binary "@$tmp" "{{endpointUrl}}" 2>$null
                } finally {
                    Remove-Item $tmp -ErrorAction SilentlyContinue
                }
                exit $LASTEXITCODE
                """;
    }

    private static void WriteWindowsExecutableShim(string dir, string endpointUrl)
    {
        var shimPath = ResolveWindowsExecutableShimPath();
        if (shimPath is null)
        {
            Log.Warn(() => "TmuxScriptWriter: mttmux.exe not found; non-shell process launches may not resolve tmux");
            return;
        }

        File.Copy(shimPath, Path.Combine(dir, "tmux.exe"), overwrite: true);
        var shimDirectory = Path.GetDirectoryName(shimPath);
        if (shimDirectory is not null)
        {
            foreach (var supportFile in Directory.EnumerateFiles(shimDirectory, "mttmux.*"))
            {
                File.Copy(supportFile, Path.Combine(dir, Path.GetFileName(supportFile)), overwrite: true);
            }
        }
        File.WriteAllText(Path.Combine(dir, "tmux.endpoint"), endpointUrl);
    }

    private static string? ResolveWindowsExecutableShimPath()
    {
        var fileName = OperatingSystem.IsWindows() ? "mttmux.exe" : "mttmux";
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.GetFullPath(Path.Combine(
                AppContext.BaseDirectory,
                "..", "..", "..", "..",
                "Ai.Tlbx.MidTerm.TmuxShim", "bin", "Debug", "net10.0",
                fileName))
        };

        return candidates.FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// Delete the shim script directory. Called during server shutdown.
    /// </summary>
    public static void Cleanup()
    {
        if (_scriptDirectory is null)
        {
            return;
        }

        try
        {
            if (Directory.Exists(_scriptDirectory))
            {
                Directory.Delete(_scriptDirectory, recursive: true);
                Log.Info(() => string.Create(CultureInfo.InvariantCulture, $"TmuxScriptWriter: Cleaned up {_scriptDirectory}"));
            }
        }
        catch
        {
            // Best-effort cleanup
        }

        _scriptDirectory = null;
    }
}

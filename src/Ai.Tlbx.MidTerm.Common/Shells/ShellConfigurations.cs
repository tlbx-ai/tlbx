using Ai.Tlbx.MidTerm.Common.Identity;

namespace Ai.Tlbx.MidTerm.Common.Shells;

/// <summary>
/// Configuration interface for shell types supported by MidTerm.
/// </summary>
public interface IShellConfiguration
{
    ShellType ShellType { get; }
    string DisplayName { get; }
    string ExecutablePath { get; }
    string[] Arguments { get; }
    bool SupportsOsc7 { get; }

    Dictionary<string, string> GetEnvironmentVariables();
    bool IsAvailable();
}

/// <summary>
/// Base class for shell configurations with common environment setup and path resolution.
/// </summary>
public abstract class ShellConfigurationBase : IShellConfiguration
{
    private static readonly string[] InheritedEnvironmentVariablesToStrip =
    [
        "NO_COLOR",
        "TMUX",
        "TMUX_PANE",
        "DOTNET_STARTUP_HOOKS",
        "DOTNET_WATCH",
        "DOTNET_WATCH_ITERATION",
        "DOTNET_WATCH_SUPPRESS_BROWSER_REFRESH",
        "DOTNET_WATCH_SUPPRESS_EMOJIS",
        "DOTNET_WATCH_RESTART_ON_RUDE_EDIT",
        "DOTNET_MODIFIABLE_ASSEMBLIES",
        "ASPNETCORE_HOSTINGSTARTUPASSEMBLIES",
        TerminalEnvironmentOverrides.OverrideKeysEnvironmentVariable
    ];

    public abstract ShellType ShellType { get; }
    public abstract string DisplayName { get; }
    public abstract string ExecutablePath { get; }
    public abstract string[] Arguments { get; }
    public abstract bool SupportsOsc7 { get; }

    /// <summary>
    /// Configure environment variables for the shell process.
    ///
    /// TERMINAL CAPABILITY ADVERTISEMENT:
    /// All platforms set TERM=xterm-256color and COLORTERM=truecolor. This matches what
    /// iTerm2, WezTerm, and Alacritty do. xterm.js supports full 24-bit color, and ConPTY
    /// on Windows supports all VT sequences including 24-bit SGR (since Windows 10 1903+).
    ///
    /// Color forcing:
    /// - FORCE_COLOR=3
    /// - CLICOLOR=1 / CLICOLOR_FORCE=1
    /// - PY_COLORS=1
    /// - CLAUDE_CODE_TMUX_TRUECOLOR=1
    /// NO_COLOR is stripped from inherited environment to prevent accidental ANSI suppression.
    ///
    /// TERM=xterm-256color is chosen over xterm-direct because:
    /// - xterm-256color is universally available in terminfo on all systems
    /// - SSH forwards TERM to remote hosts, giving them at least 256 colors
    /// - Apps that want truecolor check COLORTERM anyway (vim, bat, delta, fish, etc.)
    ///
    /// WINDOWS-SPECIFIC:
    /// - WT_SESSION/WT_PROFILE_ID: Set for Claude Code TUI image support detection
    /// - TEMP/TMP: Normalized to long paths (8.3 short names break Claude Code path matching)
    /// </summary>
    public virtual Dictionary<string, string> GetEnvironmentVariables()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (System.Collections.DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && !string.IsNullOrEmpty(key))
            {
                env[key] = entry.Value?.ToString() ?? string.Empty;
            }
        }

        StripPoisonedInheritedEnvironment(env);

        env["TERM"] = "xterm-256color";
        env["COLORTERM"] = "truecolor";
        env["TERM_PROGRAM"] = "tlbx";
        // Advertise the supported Kitty graphics transport without requiring a nonportable
        // terminfo entry or changing tlbx's identity. This is a real terminal, not tmux:
        // automatic helper discovery (including Codex) need not execute our PATH shim.
        env["KITTY_WINDOW_ID"] = Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        env["FORCE_COLOR"] = "3";
        env["CLICOLOR"] = "1";
        env["CLICOLOR_FORCE"] = "1";
        env["PY_COLORS"] = "1";
        env["CLAUDE_CODE_TMUX_TRUECOLOR"] = "1";

        if (OperatingSystem.IsWindows())
        {
            env["WT_SESSION"] = Guid.NewGuid().ToString();
            env["WT_PROFILE_ID"] = "{" + Guid.NewGuid().ToString() + "}";
            NormalizeTempPaths(env);
        }
        else
        {
            var resolved = ResolveExecutablePath();
            if (resolved is not null)
            {
                env["SHELL"] = resolved;
            }
        }

        env["LANG"] = "en_US.UTF-8";
        env["LC_ALL"] = "en_US.UTF-8";
        env["MSYS"] = "enable_pcon";

        return env;
    }

    private static void StripPoisonedInheritedEnvironment(Dictionary<string, string> env)
    {
        foreach (var variableName in InheritedEnvironmentVariablesToStrip)
        {
            env.Remove(variableName);
        }
    }

    private static void NormalizeTempPaths(Dictionary<string, string> env)
    {
        // If TEMP/TMP contain a tilde (~), they're using 8.3 short names.
        // Reconstruct from LOCALAPPDATA which Windows always provides as long path.
        if (!env.TryGetValue("LOCALAPPDATA", out var localAppData) || string.IsNullOrEmpty(localAppData))
        {
            return;
        }

        var longTempPath = Path.Combine(localAppData, "Temp");
        if (!Directory.Exists(longTempPath))
        {
            return;
        }

        if (env.TryGetValue("TEMP", out var temp) && temp.AsSpan().Contains("~", StringComparison.Ordinal))
        {
            env["TEMP"] = longTempPath;
        }

        if (env.TryGetValue("TMP", out var tmp) && tmp.AsSpan().Contains("~", StringComparison.Ordinal))
        {
            env["TMP"] = longTempPath;
        }
    }

    public virtual bool IsAvailable()
    {
        var resolved = ResolveExecutablePath();
        return resolved is not null;
    }

    protected string? ResolveExecutablePath()
    {
        if (Path.IsPathRooted(ExecutablePath) && File.Exists(ExecutablePath))
        {
            return ExecutablePath;
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (pathEnv is null)
        {
            return null;
        }

        var extensions = OperatingSystem.IsWindows()
            ? new[] { ".exe", ".cmd", ".bat", "" }
            : new[] { "" };

        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            foreach (var ext in extensions)
            {
                var candidate = ExecutablePath.EndsWith(ext, StringComparison.OrdinalIgnoreCase)
                    ? Path.Combine(dir, ExecutablePath)
                    : Path.Combine(dir, ExecutablePath + ext);

                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }

        return null;
    }
}

/// <summary>PowerShell 7 (pwsh) shell configuration with OSC-7 CWD tracking.</summary>
public sealed class PwshShellConfiguration : ShellConfigurationBase
{
    private const string StartupScript =
        "[Console]::OutputEncoding=[Console]::InputEncoding=[Text.UTF8Encoding]::new();" +
        "function prompt{$e=[char]27;$b=[char]7;$p='/'+($PWD.Path-replace'\\\\','/');\"$e]7;file://$env:COMPUTERNAME$p$b\"+\"PS $PWD> \"}";

    public override ShellType ShellType => ShellType.Pwsh;
    public override string DisplayName => "PowerShell 7";
    public override string ExecutablePath => "pwsh";
    public override bool SupportsOsc7 => true;
    public override string[] Arguments => ["-NoLogo", "-NoExit", "-Command", StartupScript];
}

/// <summary>Windows PowerShell 5.x shell configuration with OSC-7 CWD tracking.</summary>
public sealed class PowerShellShellConfiguration : ShellConfigurationBase
{
    private const string StartupScript =
        "[Console]::OutputEncoding=[Console]::InputEncoding=[Text.UTF8Encoding]::new();" +
        "function prompt{$e=[char]27;$b=[char]7;$p='/'+($PWD.Path-replace'\\\\','/');\"$e]7;file://$env:COMPUTERNAME$p$b\"+\"PS $PWD> \"}";

    public override ShellType ShellType => ShellType.PowerShell;
    public override string DisplayName => "Windows PowerShell";
    public override string ExecutablePath => "powershell";
    public override bool SupportsOsc7 => true;
    public override string[] Arguments => ["-NoLogo", "-NoExit", "-Command", StartupScript];

    public override bool IsAvailable()
    {
        return OperatingSystem.IsWindows() && base.IsAvailable();
    }
}

/// <summary>Windows Command Prompt (cmd.exe) shell configuration.</summary>
public sealed class CmdShellConfiguration : ShellConfigurationBase
{
    public override ShellType ShellType => ShellType.Cmd;
    public override string DisplayName => "Command Prompt";
    public override string ExecutablePath => "cmd.exe";
    public override bool SupportsOsc7 => false;
    public override string[] Arguments => [];

    public override bool IsAvailable()
    {
        return OperatingSystem.IsWindows() && base.IsAvailable();
    }
}

/// <summary>Bash shell configuration for Linux/macOS with OSC-7 CWD tracking.</summary>
public sealed class BashShellConfiguration : ShellConfigurationBase
{
    public override ShellType ShellType => ShellType.Bash;
    public override string DisplayName => "Bash";
    public override string ExecutablePath => "bash";
    public override bool SupportsOsc7 => true;
    public override string[] Arguments => ["-l"];

    public override Dictionary<string, string> GetEnvironmentVariables()
    {
        var env = base.GetEnvironmentVariables();
        env["PROMPT_COMMAND"] = "printf '\\e]7;file://%s%s\\a' \"$HOSTNAME\" \"$PWD\"";
        return env;
    }

    public override bool IsAvailable()
    {
        return (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && base.IsAvailable();
    }
}

/// <summary>Zsh shell configuration for Linux/macOS with OSC-7 CWD tracking.</summary>
public sealed class ZshShellConfiguration : ShellConfigurationBase
{
    public override ShellType ShellType => ShellType.Zsh;
    public override string DisplayName => "Zsh";
    public override string ExecutablePath => "zsh";
    public override bool SupportsOsc7 => true;
    public override string[] Arguments => ["-l"];

    // Zsh init script placed at $ZDOTDIR/.zshenv.
    // Restores ZDOTDIR so user's config loads from $HOME, then registers a one-shot
    // precmd hook that adds xterm key bindings (Home, End, Delete) after all init
    // files have been sourced. This fixes Home/End on macOS where zsh doesn't bind
    // them by default (Mac keyboards lack these keys).
    private const string ZshEnvScript =
        """
        ZDOTDIR="${_MT_ORIG_ZDOTDIR:-$HOME}"
        unset _MT_ORIG_ZDOTDIR
        [[ -f "$ZDOTDIR/.zshenv" ]] && . "$ZDOTDIR/.zshenv"
        if [[ -o interactive ]]; then
          __midterm_setup_keys() {
            bindkey '^[[H' beginning-of-line 2>/dev/null
            bindkey '^[OH' beginning-of-line 2>/dev/null
            bindkey '^[[F' end-of-line 2>/dev/null
            bindkey '^[OF' end-of-line 2>/dev/null
            bindkey '^[[3~' delete-char 2>/dev/null
            precmd_functions=(${precmd_functions:#__midterm_setup_keys})
            unfunction __midterm_setup_keys 2>/dev/null
          }
          precmd_functions+=(__midterm_setup_keys)
        fi
        """;

    public override Dictionary<string, string> GetEnvironmentVariables()
    {
        var env = base.GetEnvironmentVariables();
        env["PROMPT"] = "%{$(print -Pn \"\\e]7;file://%m%~\\a\")%}%(?..[%?] )%n@%m %~ %# ";

        var initDir = EnsureZshInitDir(env);
        if (initDir is not null)
        {
            env["_MT_ORIG_ZDOTDIR"] = env.TryGetValue("ZDOTDIR", out var orig) ? orig : "";
            env["ZDOTDIR"] = initDir;
        }

        return env;
    }

    private static string? EnsureZshInitDir(Dictionary<string, string> env)
    {
        try
        {
            var home = env.TryGetValue("HOME", out var h) && !string.IsNullOrEmpty(h)
                ? h
                : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

            var settingsDir = TlbxProductIdentity.SelectSettingsDirectory(
                Path.Combine(home, TlbxProductIdentity.UserSettingsDirectoryName),
                Path.Combine(home, TlbxProductIdentity.LegacyUserSettingsDirectoryName));
            var initDir = Path.Combine(settingsDir, "zsh-init");
            Directory.CreateDirectory(initDir);

            var zshenvPath = Path.Combine(initDir, ".zshenv");
            if (!File.Exists(zshenvPath) || File.ReadAllText(zshenvPath) != ZshEnvScript)
            {
                File.WriteAllText(zshenvPath, ZshEnvScript);
            }

            return initDir;
        }
        catch
        {
            return null;
        }
    }

    public override bool IsAvailable()
    {
        return (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS()) && base.IsAvailable();
    }
}

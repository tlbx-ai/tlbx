using Ai.Tlbx.MidTerm.Common.Shells;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

[Collection(PathSensitiveEnvironmentCollection.Name)]
public sealed class ShellConfigurationsTests
{
    [Fact]
    public void GetEnvironmentVariables_StripsDotnetWatchPoisoningFromInheritedShellEnvironment()
    {
        using var startupHooks = new EnvironmentVariableScope("DOTNET_STARTUP_HOOKS", @"Q:\fake\dotnet-watch-startup-hook.dll");
        using var watchFlag = new EnvironmentVariableScope("DOTNET_WATCH", "1");
        using var watchIteration = new EnvironmentVariableScope("DOTNET_WATCH_ITERATION", "7");
        using var modifiableAssemblies = new EnvironmentVariableScope("DOTNET_MODIFIABLE_ASSEMBLIES", "debug");
        using var hostingStartupAssemblies = new EnvironmentVariableScope("ASPNETCORE_HOSTINGSTARTUPASSEMBLIES", "Microsoft.AspNetCore.Watch.BrowserRefresh");
        using var noColor = new EnvironmentVariableScope("NO_COLOR", "1");
        using var terminalOverrideKeys = new EnvironmentVariableScope(TerminalEnvironmentOverrides.OverrideKeysEnvironmentVariable, "TERM");

        var env = new PwshShellConfiguration().GetEnvironmentVariables();

        Assert.DoesNotContain("DOTNET_STARTUP_HOOKS", env.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("DOTNET_WATCH", env.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("DOTNET_WATCH_ITERATION", env.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("DOTNET_MODIFIABLE_ASSEMBLIES", env.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("ASPNETCORE_HOSTINGSTARTUPASSEMBLIES", env.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain("NO_COLOR", env.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.DoesNotContain(TerminalEnvironmentOverrides.OverrideKeysEnvironmentVariable, env.Keys, StringComparer.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetEnvironmentVariables_AdvertisesAndForcesRichColorSupport()
    {
        var env = new PwshShellConfiguration().GetEnvironmentVariables();

        Assert.Equal("xterm-256color", env["TERM"]);
        Assert.Equal("truecolor", env["COLORTERM"]);
        Assert.Equal("tlbx", env["TERM_PROGRAM"]);
        Assert.Equal("3", env["FORCE_COLOR"]);
        Assert.Equal("1", env["CLICOLOR"]);
        Assert.Equal("1", env["CLICOLOR_FORCE"]);
        Assert.Equal("1", env["PY_COLORS"]);
        Assert.Equal("1", env["CLAUDE_CODE_TMUX_TRUECOLOR"]);
    }

    [Fact]
    public void ApplyMarkedOverrides_ReappliesUserTerminalEnvironmentLast()
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["TERM"] = "xterm-256color",
            ["CLAUDE_CODE_TMUX_TRUECOLOR"] = "1",
            [TerminalEnvironmentOverrides.OverrideKeysEnvironmentVariable] = "TERM\nCLAUDE_CODE_TMUX_TRUECOLOR"
        };

        TerminalEnvironmentOverrides.ApplyMarkedOverrides(
            env,
            "TERM\nCLAUDE_CODE_TMUX_TRUECOLOR",
            key => key switch
            {
                "TERM" => "xterm-direct",
                "CLAUDE_CODE_TMUX_TRUECOLOR" => "0",
                _ => null
            });

        Assert.Equal("xterm-direct", env["TERM"]);
        Assert.Equal("0", env["CLAUDE_CODE_TMUX_TRUECOLOR"]);
        Assert.DoesNotContain(TerminalEnvironmentOverrides.OverrideKeysEnvironmentVariable, env.Keys, StringComparer.Ordinal);
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _originalValue);
        }
    }
}

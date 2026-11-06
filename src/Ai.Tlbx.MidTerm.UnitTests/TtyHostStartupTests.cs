using Ai.Tlbx.MidTerm.Common.Shells;
using System.Text;
using System.Diagnostics;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

[Collection(TimingSensitiveCollection.Name)]
public sealed class TtyHostStartupTests
{
    [Theory]
    [InlineData("Pwsh")]
    [InlineData("PowerShell")]
    public async Task PowerShellFirstReadLine_PreservesExactCommandAndRestoresOriginalHook(string shellName)
    {
        var shell = new ShellRegistry().GetConfiguration(Enum.Parse<ShellType>(shellName));
        if (!shell.IsAvailable())
        {
            Assert.Skip($"{shellName} is not installed on this platform.");
        }

        const string command = "node 'a path/ä_日本.js' --flag=\"quotes & ; $value\"\nWrite-Output 'second line'";
        var launch = TtyHost.Program.BuildShellLaunch(shell, command);
        var script = "function global:PSConsoleHostReadLine {'original-hook'};" + launch.Arguments[^1] +
            ";$first=PSConsoleHostReadLine;$second=PSConsoleHostReadLine;" +
            "[Console]::WriteLine('RESULT:'+ [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($first)));" +
            "[Console]::WriteLine('RESTORED:'+ $second)";
        var start = new ProcessStartInfo(shell.ExecutablePath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "-NoProfile", "-NonInteractive", "-Command", script })
        {
            start.ArgumentList.Add(argument);
        }

        using var process = Process.Start(start)!;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var outputTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var errorTask = process.StandardError.ReadToEndAsync(timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }

        var output = await outputTask;
        Assert.True(process.ExitCode == 0, await errorTask);
        Assert.Contains("RESULT:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(command)), output, StringComparison.Ordinal);
        Assert.Contains("RESTORED:original-hook", output, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Pwsh")]
    [InlineData("PowerShell")]
    public void BuildShellLaunch_SuppliesPowerShellBookmarkThroughFirstReadLine(string shellName)
    {
        var shell = new ShellRegistry().GetConfiguration(Enum.Parse<ShellType>(shellName));

        var launch = TtyHost.Program.BuildShellLaunch(shell, "codex --yolo");

        Assert.Empty(launch.DeferredInput.ToArray());
        Assert.Equal("-Command", launch.Arguments[^2]);
        Assert.Equal("-NoExit", launch.Arguments[1]);
        Assert.DoesNotContain("codex --yolo", launch.Arguments[^1], StringComparison.Ordinal);
        Assert.StartsWith(shell.Arguments[^1] + ";", launch.Arguments[^1], StringComparison.Ordinal);
        Assert.Contains("Function:global:PSConsoleHostReadLine", launch.Arguments[^1], StringComparison.Ordinal);
        Assert.Contains(Convert.ToBase64String(Encoding.UTF8.GetBytes("codex --yolo")), launch.Arguments[^1], StringComparison.Ordinal);
    }

    [Fact]
    public void BuildShellLaunch_UsesPersistentCmdCommand()
    {
        var shell = new ShellRegistry().GetConfigurationByName("Cmd")!;

        var launch = TtyHost.Program.BuildShellLaunch(shell, "codex --yolo");

        Assert.Empty(launch.DeferredInput.ToArray());
        Assert.Equal(["/K", "codex --yolo"], launch.Arguments);
    }

    [Fact]
    public void InputReadinessDetector_RecognizesSplitBracketedPasteSequence()
    {
        var detector = new TtyHost.TerminalInputReadinessDetector(ShellType.Bash);

        Assert.Null(detector.Observe("\u001b[?20"u8));
        Assert.Equal(
            TtyHost.TerminalInputReadiness.BracketedPaste,
            detector.Observe("04h"u8));
        Assert.Equal(
            TtyHost.TerminalInputReadiness.BracketedPaste,
            detector.Observe(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void InputReadinessDetector_RecognizesBashPromptWithoutBracketedPaste()
    {
        var detector = new TtyHost.TerminalInputReadinessDetector(ShellType.Bash);

        Assert.Null(detector.Observe("Welcome\r\nuser@host:~/repos/"u8));
        Assert.Equal(
            TtyHost.TerminalInputReadiness.Prompt,
            detector.Observe("Jpa$ "u8));
    }

    [Fact]
    public void InputReadinessDetector_DoesNotTreatStartupTextAsPrompt()
    {
        var detector = new TtyHost.TerminalInputReadinessDetector(ShellType.Bash);

        Assert.Null(detector.Observe("Loading profile >\r\nstill starting"u8));
    }
}

using System.Diagnostics;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Browser;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

[Collection(PathSensitiveEnvironmentCollection.Name)]
public sealed class TlbxCliScriptWriterTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "tlbx-cli-tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SourceInstance_PreservesSupervisorHelpersAndKeepsItsOwnCredentials()
    {
        Directory.CreateDirectory(_tempDir);
        TlbxDirectory.WriteInstanceCliScripts(_tempDir, 2000, "supervisor-token", false);
        var names = new[] { "tlbx_cli.ps1", "tlbx_cli.sh", "tlbx_graphs.ps1", "tlbx_graphs.sh" };
        var originals = names.ToDictionary(name => name, name => File.ReadAllText(Path.Combine(_tempDir, name)), StringComparer.Ordinal);
        TlbxDirectory.WriteInstanceCliScripts(_tempDir, 2100, "source-token", true);
        TlbxDirectory.WriteInstanceCliScripts(_tempDir, 2100, "refreshed-source-token", true);
        foreach (var name in names)
        {
            Assert.Equal(originals[name], File.ReadAllText(Path.Combine(_tempDir, name)));
            var source = File.ReadAllText(Path.Combine(_tempDir, "instances", "2100", name));
            Assert.Contains("2100", source, StringComparison.Ordinal);
            Assert.Contains("refreshed-source-token", source, StringComparison.Ordinal);
            Assert.DoesNotContain("supervisor-token", source, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void WriteScripts_BashBootstrapProducesValidJsonWithSlashCommands()
    {
        var bashPath = ResolveBashPath();
        if (bashPath is null)
        {
            return;
        }

        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2000, "test-token");

        var scriptPath = ToBashPath(Path.Combine(_tempDir, "tlbx_cli.sh"));
        var startInfo = new ProcessStartInfo(bashPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(
            "source \"$1\"; _MJ() { printf '%s' \"$2\"; }; " +
            "mt_bootstrap 'review \"worker\"' 'Q:/repo with space' codex status compact");
        startInfo.ArgumentList.Add("midterm-test");
        startInfo.ArgumentList.Add(scriptPath);

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, error);
        using var json = JsonDocument.Parse(output);
        var root = json.RootElement;
        Assert.Equal("review \"worker\"", root.GetProperty("name").GetString());
        Assert.Equal("Q:/repo with space", root.GetProperty("workingDirectory").GetString());
        Assert.Equal("codex", root.GetProperty("profile").GetString());
        var slashCommands = root.GetProperty("slashCommands");
        Assert.Equal(2, slashCommands.GetArrayLength());
        Assert.Equal("status", slashCommands[0].GetString());
        Assert.Equal("compact", slashCommands[1].GetString());
    }

    [Fact]
    public void WriteScripts_BashRunIsolatedKeepsChildBytesOutOfCallerAndPreservesArgv()
    {
        var bashPath = ResolveBashPath();
        if (bashPath is null)
        {
            return;
        }

        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2000, "test-token");
        var childPath = Path.Combine(_tempDir, "isolated-child.sh");
        File.WriteAllText(childPath,
            "sleep 0.15\n" +
            "printf 'stdout-sentinel\\n'\n" +
            "printf 'arg1=<%s>\\n' \"$1\"\n" +
            "printf 'arg2=<%s>\\n' \"$2\"\n" +
            "printf 'arg3=<%s>\\n' \"$3\"\n" +
            "if IFS= read -r line; then printf 'stdin=<%s>\\n' \"$line\"; else printf 'stdin=<eof>\\n'; fi\n" +
            "printf 'stderr-sentinel\\n' >&2\n");

        var startInfo = new ProcessStartInfo(bashPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add("source \"$1\"; mt_run_isolated \"$2\" \"$3\" \"$4\" \"$5\" \"$6\"");
        startInfo.ArgumentList.Add("midterm-test");
        startInfo.ArgumentList.Add(ToBashPath(Path.Combine(_tempDir, "tlbx_cli.sh")));
        startInfo.ArgumentList.Add(ToBashPath(bashPath));
        startInfo.ArgumentList.Add(ToBashPath(childPath));
        startInfo.ArgumentList.Add("two words");
        startInfo.ArgumentList.Add("quote\"value");
        startInfo.ArgumentList.Add("slash and space\\");

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, error);
        Assert.DoesNotContain("stdout-sentinel", output, StringComparison.Ordinal);
        Assert.DoesNotContain("stderr-sentinel", error, StringComparison.Ordinal);
        using var receipt = JsonDocument.Parse(output);
        var root = receipt.RootElement;
        Assert.True(root.GetProperty("pid").GetInt32() > 0);
        var runId = root.GetProperty("runId").GetString()!;
        var stdoutPath = Path.Combine(_tempDir, "runs", runId, "stdout.log");
        var stderrPath = Path.Combine(_tempDir, "runs", runId, "stderr.log");

        var childOutput = WaitForFileContent(stdoutPath, content => content.Contains("stdin=<eof>", StringComparison.Ordinal));
        var childError = WaitForFileContent(stderrPath, content => content.Contains("stderr-sentinel", StringComparison.Ordinal));
        Assert.Contains("stdout-sentinel", childOutput, StringComparison.Ordinal);
        Assert.Contains("arg1=<two words>", childOutput, StringComparison.Ordinal);
        Assert.Contains("arg2=<quote\"value>", childOutput, StringComparison.Ordinal);
        Assert.Contains("arg3=<slash and space\\>", childOutput, StringComparison.Ordinal);
        Assert.Equal("stderr-sentinel", childError.Trim());
        Assert.EndsWith($"/runs/{runId}/stdout.log", root.GetProperty("stdoutPath").GetString()!.Replace('\\', '/'), StringComparison.Ordinal);
        Assert.EndsWith($"/runs/{runId}/stderr.log", root.GetProperty("stderrPath").GetString()!.Replace('\\', '/'), StringComparison.Ordinal);
    }

    [Fact]
    public void WriteScripts_PowerShellRunIsolatedKeepsChildBytesOutOfCallerAndPreservesArgv()
    {
        var powershellPath = ResolvePowerShellPath();
        if (powershellPath is null)
        {
            return;
        }

        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2000, "test-token");
        var childPath = Path.Combine(_tempDir, "isolated-child.ps1");
        File.WriteAllText(childPath,
            "param([Parameter(ValueFromRemainingArguments=$true)][string[]]$InputArgs)\n" +
            "Start-Sleep -Milliseconds 150\n" +
            "[Console]::Out.WriteLine('stdout-sentinel')\n" +
            "$InputArgs | ForEach-Object -Begin { $index = 0 } -Process { [Console]::Out.WriteLine(('arg{0}=<{1}>' -f (++$index), $_)) }\n" +
            "[Console]::Out.WriteLine(('stdin-length={0}' -f ([Console]::In.ReadToEnd().Length)))\n" +
            "[Console]::Error.WriteLine('stderr-sentinel')\n");

        var startInfo = new ProcessStartInfo(powershellPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(_tempDir, "tlbx_cli.ps1"));
        startInfo.ArgumentList.Add("mt_run_isolated");
        startInfo.ArgumentList.Add(powershellPath);
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-NonInteractive");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(childPath);
        startInfo.ArgumentList.Add("two words");
        startInfo.ArgumentList.Add("quote\"value");
        startInfo.ArgumentList.Add("slash and space\\");

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, error);
        Assert.DoesNotContain("stdout-sentinel", output, StringComparison.Ordinal);
        Assert.DoesNotContain("stderr-sentinel", error, StringComparison.Ordinal);
        using var receipt = JsonDocument.Parse(output);
        var root = receipt.RootElement;
        Assert.True(root.GetProperty("pid").GetInt32() > 0);
        var runId = root.GetProperty("runId").GetString()!;
        var stdoutPath = root.GetProperty("stdoutPath").GetString()!;
        var stderrPath = root.GetProperty("stderrPath").GetString()!;
        Assert.Equal(Path.Combine(_tempDir, "runs", runId, "stdout.log"), stdoutPath);
        Assert.Equal(Path.Combine(_tempDir, "runs", runId, "stderr.log"), stderrPath);

        var childOutput = WaitForFileContent(stdoutPath, content => content.Contains("stdin-length=0", StringComparison.Ordinal));
        var childError = WaitForFileContent(stderrPath, content => content.Contains("stderr-sentinel", StringComparison.Ordinal));
        Assert.Contains("stdout-sentinel", childOutput, StringComparison.Ordinal);
        Assert.Contains("arg1=<two words>", childOutput, StringComparison.Ordinal);
        Assert.Contains("arg2=<quote\"value>", childOutput, StringComparison.Ordinal);
        Assert.Contains("arg3=<slash and space\\>", childOutput, StringComparison.Ordinal);
        Assert.Equal("stderr-sentinel", childError.Trim());
    }

    [Fact]
    public void WriteScripts_PowerShellRunIsolated_RemovesExpiredCompletedRuns()
    {
        var powershellPath = ResolvePowerShellPath();
        if (powershellPath is null)
        {
            return;
        }

        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2000, "test-token");
        var expiredRun = Path.Combine(_tempDir, "runs", "expired-run");
        Directory.CreateDirectory(expiredRun);
        Directory.SetLastWriteTimeUtc(expiredRun, DateTime.UtcNow.AddDays(-15));

        var startInfo = new ProcessStartInfo(powershellPath)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-File");
        startInfo.ArgumentList.Add(Path.Combine(_tempDir, "tlbx_cli.ps1"));
        startInfo.ArgumentList.Add("mt_run_isolated");
        startInfo.ArgumentList.Add(powershellPath);
        startInfo.ArgumentList.Add("-NoProfile");
        startInfo.ArgumentList.Add("-Command");
        startInfo.ArgumentList.Add("exit 0");

        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.True(process.ExitCode == 0, error + output);
        Assert.False(Directory.Exists(expiredRun));

        var shell = File.ReadAllText(Path.Combine(_tempDir, "tlbx_cli.sh"));
        var powershell = File.ReadAllText(Path.Combine(_tempDir, "tlbx_cli.ps1"));
        Assert.Contains("max_completed_runs=100", shell, StringComparison.Ordinal);
        Assert.Contains("max_completed_kib=1048576", shell, StringComparison.Ordinal);
        Assert.Contains("$maxCompletedRuns = 100", powershell, StringComparison.Ordinal);
        Assert.Contains("$maxCompletedBytes = 1GB", powershell, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
        }
    }

    private static string? ResolveBashPath()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? [@"C:\Program Files\Git\bin\bash.exe", @"C:\Program Files\Git\usr\bin\bash.exe"]
            : ["/bin/bash", "/usr/bin/bash"];

        return candidates.FirstOrDefault(File.Exists);
    }

    private static string? ResolvePowerShellPath()
    {
        string[] candidates = OperatingSystem.IsWindows()
            ? [@"C:\Program Files\PowerShell\7\pwsh.exe"]
            : ["/usr/bin/pwsh", "/usr/local/bin/pwsh"];

        var fixedPath = candidates.FirstOrDefault(File.Exists);
        if (fixedPath is not null)
        {
            return fixedPath;
        }

        var executableName = OperatingSystem.IsWindows() ? "pwsh.exe" : "pwsh";
        return (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(path => Path.Combine(path, executableName))
            .FirstOrDefault(File.Exists);
    }

    private static string WaitForFileContent(string path, Func<string, bool> completed)
    {
        var timeout = Stopwatch.StartNew();
        string content = string.Empty;
        while (timeout.Elapsed < TimeSpan.FromSeconds(10))
        {
            if (File.Exists(path))
            {
                try
                {
                    content = File.ReadAllText(path);
                    if (completed(content))
                    {
                        return content;
                    }
                }
                catch (IOException)
                {
                }
            }

            Thread.Sleep(25);
        }

        Assert.Fail($"Timed out waiting for isolated run artifact '{path}'. Last content: {content}");
        return content;
    }

    private static string ToBashPath(string path)
    {
        if (!OperatingSystem.IsWindows())
        {
            return path;
        }

        var normalized = path.Replace('\\', '/');
        return normalized.Length >= 3 && normalized[1] == ':'
            ? $"/{char.ToLowerInvariant(normalized[0])}/{normalized[3..]}"
            : normalized;
    }
}

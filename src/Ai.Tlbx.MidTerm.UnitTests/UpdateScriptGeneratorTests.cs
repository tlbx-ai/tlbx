using Ai.Tlbx.MidTerm.Models.Update;
using Ai.Tlbx.MidTerm.Services.Updates;
using System.Diagnostics;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class UpdateScriptGeneratorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _extractedDir;
    private readonly string _settingsDir;
    private readonly string _currentBinaryPath;

    public UpdateScriptGeneratorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"midterm_update_script_{Guid.NewGuid():N}");
        _extractedDir = Path.Combine(_tempDir, "extracted");
        _settingsDir = Path.Combine(_tempDir, "settings");
        _currentBinaryPath = OperatingSystem.IsWindows()
            ? Path.Combine(_tempDir, "bin", "mt.exe")
            : Path.Combine(_tempDir, "bin", "mt");

        Directory.CreateDirectory(_extractedDir);
        Directory.CreateDirectory(_settingsDir);
        Directory.CreateDirectory(Path.GetDirectoryName(_currentBinaryPath)!);
        File.WriteAllText(_currentBinaryPath, "binary");
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

    [Fact]
    public void GenerateUpdateScript_PathsWithSingleQuotes_AreEscaped()
    {
        var extracted = Path.Combine(_tempDir, "O'Brien", "extract");
        var current = OperatingSystem.IsWindows()
            ? Path.Combine(_tempDir, "O'Brien", "bin", "mt.exe")
            : Path.Combine(_tempDir, "O'Brien", "bin", "mt");
        var settings = Path.Combine(_tempDir, "O'Brien", "settings");

        var scriptText = ReadScript(
            UpdateScriptGenerator.GenerateUpdateScript(
                extracted,
                current,
                settings,
                UpdateType.Full,
                deleteSourceAfter: true));

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("O''Brien", scriptText, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("O'\\''Brien", scriptText, StringComparison.Ordinal);
        }
    }

    private static string ReadScript(string path)
    {
        return File.ReadAllText(path);
    }

    [Fact]
    public async Task WindowsHelpersPreserveMcpDuringReplacementAndRollback()
    {
        if (!OperatingSystem.IsWindows()) Assert.Skip("Requires Windows executable image locks and process command lines.");
        var cancellationToken = TestContext.Current.CancellationToken;
        var fixture = Path.Combine(_tempDir, "fixture");
        Directory.CreateDirectory(fixture);
        await File.WriteAllTextAsync(Path.Combine(fixture, "mt.csproj"), """
            <Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><OutputType>Exe</OutputType><TargetFramework>net10.0</TargetFramework><ImplicitUsings>enable</ImplicitUsings></PropertyGroup></Project>
            """, cancellationToken);
        await File.WriteAllTextAsync(Path.Combine(fixture, "Program.cs"), """
            Console.WriteLine("ready");
            string line;
            while ((line = Console.ReadLine()) is not null) { Console.WriteLine("reply:" + line); }
            """, cancellationToken);
        var output = Path.Combine(fixture, "out");
        await RunAsync(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet",
            ["build", Path.Combine(fixture, "mt.csproj"), "-o", output, "--nologo", "-p:UseSharedCompilation=false"]);
        foreach (var file in Directory.GetFiles(output)) File.Copy(file, Path.Combine(Path.GetDirectoryName(_currentBinaryPath)!, Path.GetFileName(file)), overwrite: true);
        var unrelatedDir = Path.Combine(_tempDir, "unrelated");
        Directory.CreateDirectory(unrelatedDir);
        foreach (var file in Directory.GetFiles(output)) File.Copy(file, Path.Combine(unrelatedDir, Path.GetFileName(file)));
        var payload = Path.Combine(_extractedDir, "mt.exe");
        var original = await File.ReadAllBytesAsync(_currentBinaryPath, cancellationToken);
        await File.WriteAllBytesAsync(payload, [.. original, 42], cancellationToken);
        await File.WriteAllBytesAsync(_currentBinaryPath + ".bak", original, cancellationToken);
        using var adapter = StartProbe(_currentBinaryPath, "mcp --stdio");
        using var quotedAdapter = StartProbe(_currentBinaryPath, "\"mcp\" --stdio");
        using var server = StartProbe(_currentBinaryPath, "--label mcp");
        using var unrelated = StartProbe(Path.Combine(unrelatedDir, "mt.exe"), "--port 1234");
        var probes = new[] { adapter, quotedAdapter, server, unrelated };
        try
        {
            foreach (var probe in probes) Assert.Equal("ready", await probe.StandardOutput.ReadLineAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(20), cancellationToken));
            var generated = UpdateScriptGenerator.GenerateUpdateScript(_extractedDir, _currentBinaryPath, _settingsDir, UpdateType.WebOnly, deleteSourceAfter: false);
            var helper = Path.Combine(_tempDir, "helpers.ps1");
            await File.WriteAllTextAsync(helper, """
                param($Generated, $CurrentMt, $NewMt, $LogFile, $Action)
                $ErrorActionPreference='Stop'
                $InstallDir=Split-Path -Parent $CurrentMt
                $ast=[System.Management.Automation.Language.Parser]::ParseFile($Generated,[ref]$null,[ref]$null)
                $names=@('Log','IsMcpAdapterProcess','KillProcessByPath','VerifyCopy','SafeCopy','InstallMtBinary','RemoveInactiveMcpImages')
                $functions=$ast.FindAll({param($n) $n -is [System.Management.Automation.Language.FunctionDefinitionAst] -and $n.Name -in $names},$true)
                foreach($function in $functions){Invoke-Expression $function.Extent.Text}
                if($Action -eq 'apply'){KillProcessByPath $CurrentMt -PreserveMcp;InstallMtBinary}
                elseif($Action -eq 'rollback'){InstallMtBinary "$CurrentMt.bak"}
                else{RemoveInactiveMcpImages}
                "UPDATER_HELPERS_OK"
                """, cancellationToken);
            string[] ScriptArgs(string action) => ["-NoProfile", "-File", helper, generated, _currentBinaryPath, payload, Path.Combine(_tempDir, "helper.log"), action];
            await RunAsync("pwsh", ScriptArgs("apply"));
            Assert.True(server.HasExited);
            Assert.False(unrelated.HasExited);
            Assert.Equal(original.Length + 1, new FileInfo(_currentBinaryPath).Length);
            foreach (var probe in new[] { adapter, quotedAdapter })
            {
                Assert.False(probe.HasExited);
                await probe.StandardInput.WriteLineAsync("after-update".AsMemory(), cancellationToken);
                await probe.StandardInput.FlushAsync(cancellationToken);
                Assert.Equal("reply:after-update", await probe.StandardOutput.ReadLineAsync(cancellationToken).AsTask().WaitAsync(TimeSpan.FromSeconds(5), cancellationToken));
            }
            await RunAsync("pwsh", ScriptArgs("rollback"));
            Assert.Equal(original, await File.ReadAllBytesAsync(_currentBinaryPath, cancellationToken));
            Assert.False(adapter.HasExited);
            Assert.False(quotedAdapter.HasExited);
            adapter.StandardInput.Close();
            quotedAdapter.StandardInput.Close();
            await adapter.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await quotedAdapter.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(5), cancellationToken);
            await RunAsync("pwsh", ScriptArgs("cleanup"));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(_currentBinaryPath)!, "mt.exe.mcp-*.old"));
        }
        finally
        {
            foreach (var probe in probes) if (!probe.HasExited) { probe.Kill(entireProcessTree: true); await probe.WaitForExitAsync(CancellationToken.None); }
        }
    }

    private static Process StartProbe(string path, string arguments)
    {
        return Process.Start(new ProcessStartInfo(path, arguments)
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true
        }) ?? throw new InvalidOperationException("Could not start the owned updater fixture.");
    }

    private static async Task RunAsync(string path, string[] arguments)
    {
        var cancellationToken = TestContext.Current.CancellationToken;
        var info = new ProcessStartInfo(path) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in arguments) info.ArgumentList.Add(argument);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start updater protocol verification.");
        var stdout = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        try { await process.WaitForExitAsync(cancellationToken).WaitAsync(TimeSpan.FromSeconds(90), cancellationToken); }
        catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); throw; }
        Assert.True(process.ExitCode == 0, (await stdout) + (await stderr));
    }

}

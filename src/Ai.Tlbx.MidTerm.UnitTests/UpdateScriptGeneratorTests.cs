using Ai.Tlbx.MidTerm.Models.Update;
using Ai.Tlbx.MidTerm.Services.Updates;
using System.Text.RegularExpressions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class UpdateScriptGeneratorTests : IDisposable
{
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(1);
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
    public void GenerateUpdateScript_RetainsPreviousResultUntilAtomicCompletionAndRecordsRollback()
    {
        var scriptText = ReadScript(
            UpdateScriptGenerator.GenerateUpdateScript(
                _extractedDir,
                _currentBinaryPath,
                _settingsDir,
                UpdateType.Full,
                deleteSourceAfter: true));

        Assert.Contains("rollbackAttempted", scriptText, StringComparison.Ordinal);

        if (OperatingSystem.IsWindows())
        {
            Assert.DoesNotContain("Remove-Item $ResultFile", scriptText, StringComparison.Ordinal);
            Assert.Contains("$tempResultFile = \"$ResultFile.new\"", scriptText, StringComparison.Ordinal);
            Assert.Contains("Move-Item -Path $tempResultFile -Destination $ResultFile -Force", scriptText, StringComparison.Ordinal);
            Assert.Contains("WriteResult $false $errorMessage '' $rollbackAttempted", scriptText, StringComparison.Ordinal);
        }
        else
        {
            Assert.DoesNotContain("rm -f \"$RESULT_FILE\"", scriptText, StringComparison.Ordinal);
            Assert.Contains("temp_result_file=\"$RESULT_FILE.new\"", scriptText, StringComparison.Ordinal);
            Assert.Contains("mv -f \"$temp_result_file\" \"$RESULT_FILE\"", scriptText, StringComparison.Ordinal);
            Assert.Contains("ROLLBACK_ATTEMPTED=true", scriptText, StringComparison.Ordinal);
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

    [Fact]
    public void GenerateUpdateScript_FullUpdate_StopsMtAgentHostProcesses()
    {
        var scriptText = ReadScript(
            UpdateScriptGenerator.GenerateUpdateScript(
                _extractedDir,
                _currentBinaryPath,
                _settingsDir,
                UpdateType.Full,
                deleteSourceAfter: true));

        if (OperatingSystem.IsWindows())
        {
            Assert.Contains("KillProcessByPath $CurrentAgentHost", scriptText, StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("kill_process_by_path \"$CURRENT_AGENTHOST\"", scriptText, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void GenerateUpdateScript_WindowsFullUpdate_InstallsAndRollsBackConptyRuntime()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var scriptText = ReadScript(
            UpdateScriptGenerator.GenerateUpdateScript(
                _extractedDir,
                _currentBinaryPath,
                _settingsDir,
                UpdateType.Full,
                deleteSourceAfter: true));

        Assert.Contains("$ConptyRuntimeFiles", scriptText, StringComparison.Ordinal);
        Assert.Contains("conpty.dll", scriptText, StringComparison.Ordinal);
        Assert.Contains("x64\\OpenConsole.exe", scriptText, StringComparison.Ordinal);
        Assert.Contains("THIRD-PARTY-LICENSES.txt", scriptText, StringComparison.Ordinal);
        Assert.Contains("SafeCopy $newRuntimePath $currentRuntimePath $relativePath", scriptText, StringComparison.Ordinal);
        Assert.Contains("$backupRuntimePath", scriptText, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateUpdateScript_Windows_OnlyControlsServiceThatOwnsUpdatedBinary()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var scriptText = ReadScript(
            UpdateScriptGenerator.GenerateUpdateScript(
                _extractedDir,
                _currentBinaryPath,
                _settingsDir,
                UpdateType.Full,
                deleteSourceAfter: true));

        Assert.Contains("function GetOwnedService", scriptText, StringComparison.Ordinal);
        Assert.Contains("Get-CimInstance Win32_Service", scriptText, StringComparison.Ordinal);
        Assert.Contains("this update owns '$CurrentMt'", scriptText, StringComparison.Ordinal);
        Assert.Equal(2, Regex.Count(scriptText, @"\$service\s*=\s*GetOwnedService", RegexOptions.IgnoreCase, RegexTimeout));
    }

    [Fact]
    public void GenerateUpdateScript_Linux_StoresBackupsOutsideInstallDirectory()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var scriptText = ReadScript(
            UpdateScriptGenerator.GenerateUpdateScript(
                _extractedDir,
                _currentBinaryPath,
                _settingsDir,
                UpdateType.Full,
                deleteSourceAfter: true));

        Assert.Contains("BACKUP_DIR=", scriptText, StringComparison.Ordinal);
        Assert.Contains("$BACKUP_DIR/mt.bak", scriptText, StringComparison.Ordinal);
        Assert.DoesNotContain("$CURRENT_MT.bak", scriptText, StringComparison.Ordinal);
    }

    [Fact]
    public void GenerateUpdateScript_Linux_ValidatesStagedPayloadBeforeStoppingService()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var scriptText = ReadScript(
            UpdateScriptGenerator.GenerateUpdateScript(
                _extractedDir,
                _currentBinaryPath,
                _settingsDir,
                UpdateType.Full,
                deleteSourceAfter: true));

        Assert.Contains("Update source directory: $EXTRACTED_DIR", scriptText, StringComparison.Ordinal);
        Assert.Contains("describe_source_file \"$NEW_MT\" \"mt\"", scriptText, StringComparison.Ordinal);
        Assert.Contains("describe_source_file \"$NEW_VERSION_JSON\" \"version.json\"", scriptText, StringComparison.Ordinal);
    }

    private static string ReadScript(string path)
    {
        return File.ReadAllText(path);
    }

    private static bool RegexIsMatch(string input, string pattern)
    {
        return Regex.IsMatch(input, pattern, RegexOptions.IgnoreCase, RegexTimeout);
    }
}

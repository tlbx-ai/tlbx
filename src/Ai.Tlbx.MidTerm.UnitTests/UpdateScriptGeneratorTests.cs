using Ai.Tlbx.MidTerm.Models.Update;
using Ai.Tlbx.MidTerm.Services.Updates;
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

}

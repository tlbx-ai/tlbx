namespace Ai.Tlbx.MidTerm.UnitTests;

internal sealed class FakeClaudePathScope : IDisposable
{
    private readonly string? _originalPath;
    private readonly string? _originalConfigDirectory;

    private FakeClaudePathScope(string root, string fakeClaudeBin, string? originalPath, string? originalConfigDirectory)
    {
        Root = root;
        FakeClaudeBin = fakeClaudeBin;
        _originalPath = originalPath;
        _originalConfigDirectory = originalConfigDirectory;
    }

    public string Root { get; }
    public string FakeClaudeBin { get; }
    public string ConfigDirectory => Path.Combine(Root, ".claude-config");
    public string ExecutablePath => Path.Combine(FakeClaudeBin, OperatingSystem.IsWindows() ? "claude.exe" : "claude");

    public static FakeClaudePathScope Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "tlbx-fake-claude-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var executablePath = TestExecutablePathResolver.ResolveExecutablePath(AppContext.BaseDirectory, "Ai.Tlbx.MidTerm.FakeClaude", "claude");
        var bin = Path.GetDirectoryName(executablePath) ?? throw new InvalidOperationException("Fake Claude output directory was not found.");
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalConfigDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        Environment.SetEnvironmentVariable("PATH", bin + Path.PathSeparator + originalPath);
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(root, ".claude-config"));
        return new FakeClaudePathScope(root, bin, originalPath, originalConfigDirectory);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", _originalConfigDirectory);
        try { Directory.Delete(Root, recursive: true); } catch { }
    }
}

namespace Ai.Tlbx.MidTerm.UnitTests;

internal sealed class FakeGrokPathScope : IDisposable
{
    private readonly string? _originalPath;

    private FakeGrokPathScope(string root, string fakeGrokBin, string? originalPath)
    {
        Root = root;
        FakeGrokBin = fakeGrokBin;
        _originalPath = originalPath;
    }

    public string Root { get; }

    public string FakeGrokBin { get; }

    public string ExecutablePath =>
        Path.Combine(FakeGrokBin, OperatingSystem.IsWindows() ? "grok.exe" : "grok");

    public static FakeGrokPathScope Create()
    {
        var root = Path.Combine(Path.GetTempPath(), "midterm-fake-grok-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);

        var executablePath = TestExecutablePathResolver.ResolveExecutablePath(
            AppContext.BaseDirectory,
            "Ai.Tlbx.MidTerm.FakeGrok",
            "grok");
        var fakeGrokBin = Path.GetDirectoryName(executablePath)
            ?? throw new InvalidOperationException($"Could not determine fake Grok output directory from '{executablePath}'.");

        var originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", fakeGrokBin + Path.PathSeparator + originalPath);
        return new FakeGrokPathScope(root, fakeGrokBin, originalPath);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
        }
    }
}

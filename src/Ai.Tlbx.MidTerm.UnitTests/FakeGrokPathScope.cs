namespace Ai.Tlbx.MidTerm.UnitTests;

internal sealed class FakeGrokPathScope : IDisposable
{
    private readonly string? _originalPath;
    private readonly string? _originalCapturePath;
    private readonly string? _originalDefaultModel;

    private FakeGrokPathScope(
        string root,
        string fakeGrokBin,
        string capturePath,
        string? originalPath,
        string? originalCapturePath,
        string? originalDefaultModel)
    {
        Root = root;
        FakeGrokBin = fakeGrokBin;
        CapturePath = capturePath;
        _originalPath = originalPath;
        _originalCapturePath = originalCapturePath;
        _originalDefaultModel = originalDefaultModel;
    }

    public string Root { get; }

    public string FakeGrokBin { get; }

    public string CapturePath { get; }

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
        var capturePath = Path.Combine(root, "fake-grok-launch.json");
        var originalCapturePath = Environment.GetEnvironmentVariable("MIDTERM_FAKE_GROK_CAPTURE_PATH");
        var originalDefaultModel = Environment.GetEnvironmentVariable("MIDTERM_APP_SERVER_CONTROL_GROK_DEFAULT_MODEL");
        Environment.SetEnvironmentVariable("PATH", fakeGrokBin + Path.PathSeparator + originalPath);
        Environment.SetEnvironmentVariable("MIDTERM_FAKE_GROK_CAPTURE_PATH", capturePath);
        Environment.SetEnvironmentVariable("MIDTERM_APP_SERVER_CONTROL_GROK_DEFAULT_MODEL", "grok-build-0.1");
        return new FakeGrokPathScope(root, fakeGrokBin, capturePath, originalPath, originalCapturePath, originalDefaultModel);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("PATH", _originalPath);
        Environment.SetEnvironmentVariable("MIDTERM_FAKE_GROK_CAPTURE_PATH", _originalCapturePath);
        Environment.SetEnvironmentVariable("MIDTERM_APP_SERVER_CONTROL_GROK_DEFAULT_MODEL", _originalDefaultModel);
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch
        {
        }
    }
}

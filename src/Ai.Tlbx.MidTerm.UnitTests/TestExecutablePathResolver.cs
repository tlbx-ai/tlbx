namespace Ai.Tlbx.MidTerm.UnitTests;

internal static class TestExecutablePathResolver
{
    public static string ResolveExecutablePath(string baseDirectory, string projectDirectoryName, string executableBaseName)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", ".."));
        var executableName = OperatingSystem.IsWindows() ? executableBaseName + ".exe" : executableBaseName;
        var preferredConfiguration = baseDirectory.Contains($"{Path.DirectorySeparatorChar}Release{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
            ? "Release"
            : baseDirectory.Contains($"{Path.DirectorySeparatorChar}Debug{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase)
                ? "Debug"
                : null;
        var configurations = preferredConfiguration is null
            ? new[] { "Debug", "Release" }
            : new[] { preferredConfiguration, string.Equals(preferredConfiguration, "Release", StringComparison.Ordinal) ? "Debug" : "Release" };

        foreach (var configuration in configurations)
        {
            var candidates = new[]
            {
                Path.Combine(repoRoot, "src", projectDirectoryName, "bin", configuration, "net11.0", "win-x64", executableName),
                Path.Combine(repoRoot, "src", projectDirectoryName, "bin", configuration, "net11.0", executableName)
            };
            var resolved = candidates.FirstOrDefault(File.Exists);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        throw new FileNotFoundException($"Could not resolve '{executableName}' build output for test project '{projectDirectoryName}'.");
    }
}

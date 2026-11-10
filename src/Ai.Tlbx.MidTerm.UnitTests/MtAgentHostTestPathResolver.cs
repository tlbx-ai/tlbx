namespace Ai.Tlbx.MidTerm.UnitTests;

internal static class MtAgentHostTestPathResolver
{
    public static string ResolveAgentHostDll(string baseDirectory)
    {
        var repoRoot = Path.GetFullPath(Path.Combine(baseDirectory, "..", "..", "..", "..", ".."));
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
                Path.Combine(repoRoot, "src", "Ai.Tlbx.MidTerm.AgentHost", "bin", configuration, "net11.0", "win-x64", "mtagenthost.dll"),
                Path.Combine(repoRoot, "src", "Ai.Tlbx.MidTerm.AgentHost", "bin", configuration, "net11.0", "win-x64", "Ai.Tlbx.MidTerm.AgentHost.dll"),
                Path.Combine(repoRoot, "src", "Ai.Tlbx.MidTerm.AgentHost", "bin", configuration, "net11.0", "mtagenthost.dll"),
                Path.Combine(repoRoot, "src", "Ai.Tlbx.MidTerm.AgentHost", "bin", configuration, "net11.0", "Ai.Tlbx.MidTerm.AgentHost.dll")
            };
            var resolved = candidates.FirstOrDefault(File.Exists);
            if (!string.IsNullOrWhiteSpace(resolved))
            {
                return resolved;
            }
        }

        throw new FileNotFoundException("Could not resolve an mtagenthost build output for tests.");
    }
}

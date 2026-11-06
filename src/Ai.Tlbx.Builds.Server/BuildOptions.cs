using System.Text.RegularExpressions;
namespace Ai.Tlbx.Builds.Server;

public sealed record BuildOptions(string StorageRoot, Uri PublicOrigin, string RepositoryId, string RepositoryOwnerId)
{
    public const long MaxAssetBytes = 1024L * 1024 * 1024;
    public static BuildOptions FromEnvironment()
    {
        string Required(string key) => Environment.GetEnvironmentVariable(key) is { Length: > 0 } value
            ? value : throw new InvalidOperationException($"Missing {key}");
        return new(Required("BUILDS_STORAGE_ROOT"), new Uri(Required("BUILDS_PUBLIC_ORIGIN")),
            Required("BUILDS_REPOSITORY_ID"), Required("BUILDS_REPOSITORY_OWNER_ID"));
    }
    public void Validate()
    {
        if (!Path.IsPathFullyQualified(StorageRoot) || PublicOrigin.Scheme != "https" || PublicOrigin.Host != "builds.tlbx.ai" || !PublicOrigin.IsDefaultPort ||
            !string.IsNullOrEmpty(PublicOrigin.UserInfo) || PublicOrigin.AbsolutePath != "/" ||
            PublicOrigin.Query.Length != 0 || PublicOrigin.Fragment.Length != 0 ||
            !Regex.IsMatch(RepositoryId, "^[0-9]{1,20}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking) ||
            !Regex.IsMatch(RepositoryOwnerId, "^[0-9]{1,20}$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
            throw new InvalidOperationException("Invalid builds configuration");
    }
}

public sealed record ReleaseAsset(string Name, string Sha256, long Size);
public sealed record PublishRequest(string SourceCommit, string ReleaseNotes, ReleaseAsset[] Assets);
public sealed record CompletedRelease(string Tag, PublishRequest Publication);
public sealed record PublisherIdentity(string Sha, bool IsBackfill);
public sealed class BuildProtocolException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}

public readonly record struct ReleaseIdentity(string Tag, Version Version, bool IsDev)
{
    public string Channel => IsDev ? "dev" : "main";
    public string Number => Tag[1..];
    public static ReleaseIdentity Parse(string tag)
    {
        if (tag.Length > 64 || !Regex.IsMatch(tag, @"^v(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)(-dev)?$", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking))
            throw new BuildProtocolException(400, "Invalid release tag");
        var dev = tag.EndsWith("-dev", StringComparison.Ordinal);
        if (!System.Version.TryParse(tag[1..].Replace("-dev", "", StringComparison.Ordinal), out var version))
            throw new BuildProtocolException(400, "Invalid release version");
        return new(tag, version, dev);
    }
}

public static class AssetNames
{
    public static readonly string[] Rids = ["win-x64", "win-x86", "linux-x64", "linux-arm64", "osx-x64", "osx-arm64"];
    public static string Archive(string rid) => $"mt-{rid}." + (rid.StartsWith("win-", StringComparison.Ordinal) ? "zip" : "tar.gz");
    public static string Sbom(string rid) => $"mt-{rid}.spdx.json";
    public static string Rid(string name) => Rids.FirstOrDefault(r => name == Archive(r) || name == Sbom(r))
        ?? throw new BuildProtocolException(400, "Unknown release asset");
}

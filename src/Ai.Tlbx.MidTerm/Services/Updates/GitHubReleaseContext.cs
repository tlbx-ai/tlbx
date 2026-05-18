using System.Text.Json.Serialization;

namespace Ai.Tlbx.MidTerm.Services.Updates;

[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(GitHubAsset))]
[JsonSerializable(typeof(GitHubTag))]
[JsonSerializable(typeof(List<GitHubRelease>))]
[JsonSerializable(typeof(List<GitHubTag>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
internal partial class GitHubReleaseContext : JsonSerializerContext
{
}

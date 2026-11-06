using System.Text.Json.Serialization;

namespace Ai.Tlbx.MidTerm.Services.Updates;

[JsonSerializable(typeof(GitHubRelease))]
[JsonSerializable(typeof(GitHubAsset))]
[JsonSerializable(typeof(List<GitHubRelease>))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
internal partial class GitHubReleaseContext : JsonSerializerContext
{
}

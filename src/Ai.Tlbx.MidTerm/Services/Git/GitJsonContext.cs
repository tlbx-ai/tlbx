using System.Text.Json.Serialization;
using Ai.Tlbx.MidTerm.Models.Git;

namespace Ai.Tlbx.MidTerm.Services.Git;

[JsonSerializable(typeof(System.Text.Json.Nodes.JsonObject))]
[JsonSerializable(typeof(GitStatusResponse))]
[JsonSerializable(typeof(GitRepoBinding))]
[JsonSerializable(typeof(GitRepoBinding[]))]
[JsonSerializable(typeof(GitRepoListResponse))]
[JsonSerializable(typeof(GitRepoBindRequest))]
[JsonSerializable(typeof(GitRepoRefreshRequest))]
[JsonSerializable(typeof(GitFileEntry))]
[JsonSerializable(typeof(GitFileEntry[]))]
[JsonSerializable(typeof(GitLogEntry))]
[JsonSerializable(typeof(GitLogEntry[]))]
[JsonSerializable(typeof(GitDiffViewResponse))]
[JsonSerializable(typeof(GitDiffFileView))]
[JsonSerializable(typeof(GitDiffFileView[]))]
[JsonSerializable(typeof(GitDiffHunk))]
[JsonSerializable(typeof(GitDiffHunk[]))]
[JsonSerializable(typeof(GitDiffLine))]
[JsonSerializable(typeof(GitDiffLine[]))]
[JsonSerializable(typeof(GitCommitDetailsResponse))]
[JsonSerializable(typeof(GitWsMessage))]
[JsonSerializable(typeof(GitDebugResponse))]
[JsonSerializable(typeof(GitDebugSessionInfo))]
[JsonSerializable(typeof(GitDebugSessionInfo[]))]
[JsonSerializable(typeof(GitCommandLog))]
[JsonSerializable(typeof(GitOperationRequest))]
[JsonSerializable(typeof(GitOperationResponse))]
[JsonSerializable(typeof(GitOperationReceipt))]
[JsonSerializable(typeof(GitWorkspaceResponse))]
[JsonSerializable(typeof(GitConflictView))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
public partial class GitJsonContext : JsonSerializerContext
{
}

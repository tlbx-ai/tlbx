using System.Text.RegularExpressions;
using Ai.Tlbx.MidTerm.Models.Git;

namespace Ai.Tlbx.MidTerm.Services.Git;

internal static partial class GitWorkspaceOperations
{
    internal static bool Handles(string action) => action is "branch-switch" or "branch-create" or "fetch" or "pull" or "push" or "merge" or "merge-abort" or "stash-create" or "stash-apply" or "stash-pop" or "stash-drop" or "resolve-conflict";
    [GeneratedRegex(@"(https?://)[^/\s@]+@", RegexOptions.CultureInvariant | RegexOptions.NonBacktracking)]
    private static partial Regex Credentials();

    internal static async Task<GitOperationResponse> ExecuteAsync(GitCommandRunner.GitRepositoryInfo info, GitOperationRequest request, CancellationToken ct)
    {
        var workspace = await GitWorkspaceQuery.ReadAsync(info.RepoRoot, ct).ConfigureAwait(false);
        if (request.ExpectedHead != workspace.HeadHash || request.ExpectedIndexVersion != workspace.IndexVersion || request.ExpectedBranch != workspace.CurrentBranch)
            throw new GitOperationException(409, "workspace_changed", "The repository changed. Refresh before this operation.");
        var status = await GitCommandRunner.GetStatusAsync(info.RepoRoot).ConfigureAwait(false);
        if (status.ReadState != "ready") throw new GitOperationException(409, "status_unavailable", "Git status could not be read. Refresh before changing this repository.");
        if (request.ExpectedHead != status.HeadHash || request.ExpectedIndexVersion != status.IndexVersion || request.ExpectedBranch != status.Branch)
            throw new GitOperationException(409, "workspace_changed", "The repository changed. Refresh before this operation.");
        if (workspace.MergeInProgress && request.Action is not ("merge-abort" or "resolve-conflict"))
            throw new GitOperationException(409, "merge_in_progress", "Finish or abort the current merge first.");
        if (status.Conflicted.Length > 0 && request.Action is not ("merge-abort" or "resolve-conflict"))
            throw new GitOperationException(409, "conflict_unresolved", "Resolve conflicted files first.");
        if (request.Action == "resolve-conflict") return await GitConflictResolution.ExecuteAsync(info, request, status, ct).ConfigureAwait(false);
        var args = await ArgumentsAsync(info.RepoRoot, request, workspace, status, ct).ConfigureAwait(false);
        var environment = new Dictionary<string, string?>(StringComparer.Ordinal)
        { ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "never", ["GIT_MERGE_AUTOEDIT"] = "no", ["GIT_EDITOR"] = "true" };
        var command = await GitCommandRunner.ExecuteAsync(info.RepoRoot, args, environment, ct).ConfigureAwait(false);
        var after = await GitCommandRunner.GetStatusAsync(info.RepoRoot).ConfigureAwait(false);
        var result = new GitOperationResponse { OperationId = request.OperationId, RepoRoot = info.RepoRoot, Action = request.Action, State = "completed", Status = after };
        if (command.ExitCode != 0)
        {
            result.State = command.ExitCode < 0 ? "unknown" : after.Conflicted.Length > 0 ? "conflicted" : "error";
            result.StatusCode = command.ExitCode < 0 ? 504 : after.Conflicted.Length > 0 ? 409 : 422;
            result.ErrorCode = command.ExitCode < 0 ? "outcome_unknown" : after.Conflicted.Length > 0 ? "conflict_requires_resolution" : "git_failed";
            var message = string.IsNullOrWhiteSpace(command.Stderr) ? command.Stdout : command.Stderr;
            var redacted = Credentials().Replace(message, "$1<redacted>@");
            result.Detail = redacted[..Math.Min(redacted.Length, 16000)];
        }
        try { result.Workspace = await GitWorkspaceQuery.ReadAsync(info.RepoRoot, ct).ConfigureAwait(false); }
        catch (IOException) { /* The confirmed command result remains visible if the metadata refresh fails. */ }
        return result;
    }

    private static async Task<string[]> ArgumentsAsync(string root, GitOperationRequest request, GitWorkspaceResponse workspace, GitStatusResponse status, CancellationToken ct)
    {
        switch (request.Action)
        {
            case "branch-switch":
                var branch = workspace.Branches.FirstOrDefault(branch => !branch.IsRemote && branch.Name == request.Branch)
                    ?? throw new GitOperationException(409, "branch_unavailable", "Select an existing local branch.");
                return ["switch", "--no-guess", "--", branch.Name];
            case "branch-create":
                await ValidateBranchAsync(root, request.Branch, ct).ConfigureAwait(false);
                return ["switch", "--no-guess", "-c", request.Branch!];
            case "fetch":
                ValidateRemote(request, workspace); return ["fetch", "--", request.Remote!];
            case "pull":
                RequireClean(status); ValidateRemote(request, workspace); await ValidateBranchAsync(root, request.RemoteBranch, ct).ConfigureAwait(false);
                return ["pull", "--ff-only", "--no-rebase", "--no-edit", "--", request.Remote!, request.RemoteBranch!];
            case "push":
                ValidateRemote(request, workspace); await ValidateBranchAsync(root, request.RemoteBranch, ct).ConfigureAwait(false);
                if (!workspace.Branches.Any(branch => branch.IsCurrent)) throw new GitOperationException(409, "detached_head", "Create or switch to a local branch before pushing.");
                return ["push", "--porcelain", "--set-upstream", "--", request.Remote!, "HEAD:refs/heads/" + request.RemoteBranch!];
            case "merge":
                RequireClean(status);
                var source = workspace.Branches.FirstOrDefault(branch => branch.Ref == request.Branch)
                    ?? throw new GitOperationException(409, "branch_unavailable", "Select an existing branch to merge.");
                return ["merge", "--no-edit", "--", source.Ref];
            case "merge-abort":
                if (!workspace.MergeInProgress) throw new GitOperationException(409, "no_merge", "No merge is in progress.");
                return ["merge", "--abort"];
            case "stash-create":
                if (status.Modified.Length + status.Untracked.Length + status.Staged.Length == 0) throw new GitOperationException(409, "nothing_to_stash", "There are no changes to stash.");
                return ["stash", "push", "--include-untracked", "-m", request.Message ?? "tlbx working changes"];
            case "stash-apply": case "stash-pop": case "stash-drop":
                if (request.Action != "stash-drop") RequireClean(status);
                var stash = workspace.Stashes.FirstOrDefault(stash => stash.Hash == request.StashHash)
                    ?? throw new GitOperationException(409, "stash_changed", "The selected stash is unavailable. Refresh the stash list.");
                return request.Action == "stash-drop" ? ["stash", "drop", "--", stash.Ref]
                    : ["stash", request.Action == "stash-pop" ? "pop" : "apply", "--index", "--", stash.Ref];
            default: throw new GitOperationException(400, "invalid_action", "Unsupported workspace operation.");
        }
    }
    private static void ValidateRemote(GitOperationRequest request, GitWorkspaceResponse workspace)
    {
        if (!workspace.Remotes.Any(remote => remote.Name == request.Remote)) throw new GitOperationException(400, "remote_required", "Select a configured remote explicitly.");
    }
    private static void RequireClean(GitStatusResponse status)
    {
        if (status.Modified.Length + status.Untracked.Length + status.Staged.Length > 0) throw new GitOperationException(409, "dirty_working_tree", "Commit or stash current changes before this operation.");
    }
    internal static async Task ValidateBranchAsync(string root, string? name, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name) || name.StartsWith('-', StringComparison.Ordinal) || name.AsSpan().IndexOfAny('\0', '\r', '\n') >= 0)
            throw new GitOperationException(400, "invalid_branch", "Enter a valid branch name.");
        var result = await GitCommandRunner.ExecuteAsync(root, ["check-ref-format", "--branch", name], ct).ConfigureAwait(false);
        if (result.ExitCode != 0 || result.Stdout.TrimEnd('\r', '\n') != name) throw new GitOperationException(400, "invalid_branch", "Enter a valid literal branch name.");
    }
}

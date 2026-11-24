namespace Ai.Tlbx.MidTerm.Models.Git;

public sealed class GitStatusResponse
{
    public string ReadState { get; set; } = "ready";
    public string? ErrorCode { get; set; }
    public string? ErrorDetail { get; set; }
    public DateTimeOffset? ReadAt { get; set; }
    public string Label { get; set; } = "";
    public string Role { get; set; } = "";
    public string Source { get; set; } = "";
    public bool IsPrimary { get; set; }
    public string Branch { get; set; } = "";
    public string HeadHash { get; set; } = "";
    public string IndexVersion { get; set; } = "";
    public int Ahead { get; set; }
    public int Behind { get; set; }
    public GitFileEntry[] Staged { get; set; } = [];
    public GitFileEntry[] Modified { get; set; } = [];
    public GitFileEntry[] Untracked { get; set; } = [];
    public GitFileEntry[] Conflicted { get; set; } = [];
    public GitLogEntry[] RecentCommits { get; set; } = [];
    public int StashCount { get; set; }
    public string RepoRoot { get; set; } = "";
    public int TotalAdditions { get; set; }
    public int TotalDeletions { get; set; }
    public bool FilesOmitted { get; set; }
    public int? StagedCount { get; set; }
    public int? ModifiedCount { get; set; }
    public int? UntrackedCount { get; set; }
    public int? ConflictedCount { get; set; }
    public int? RecentCommitsCount { get; set; }

    internal GitStatusResponse ToSummary()
    {
        if (FilesOmitted)
        {
            return this;
        }

        var copy = (GitStatusResponse)MemberwiseClone();
        copy.FilesOmitted = true;
        copy.StagedCount = Staged.Length;
        copy.ModifiedCount = Modified.Length;
        copy.UntrackedCount = Untracked.Length;
        copy.ConflictedCount = Conflicted.Length;
        copy.RecentCommitsCount = RecentCommits.Length;
        copy.Staged = [];
        copy.Modified = [];
        copy.Untracked = [];
        copy.Conflicted = [];
        copy.RecentCommits = [];
        return copy;
    }

    internal GitStatusResponse WithReadFailure(GitStatusResponse failure)
    {
        var copy = (GitStatusResponse)MemberwiseClone();
        copy.ReadState = "stale"; copy.ErrorCode = failure.ErrorCode; copy.ErrorDetail = failure.ErrorDetail;
        return copy;
    }

    internal GitStatusResponse WithBindingMetadata(string label, string role, string source, bool isPrimary)
    {
        var copy = (GitStatusResponse)MemberwiseClone();
        copy.Label = label; copy.Role = role; copy.Source = source; copy.IsPrimary = isPrimary;
        return copy;
    }
}

public sealed class GitRepoBinding
{
    public string RepoRoot { get; set; } = "";
    public string Label { get; set; } = "";
    public string Role { get; set; } = "";
    public string Source { get; set; } = "";
    public bool IsPrimary { get; set; }
    public GitStatusResponse? Status { get; set; }
}

public sealed class GitRepoListResponse
{
    public GitRepoBinding[] Repos { get; set; } = [];
}

public sealed class GitRepoBindRequest
{
    public string SessionId { get; set; } = "";
    public string Path { get; set; } = "";
    public string? Label { get; set; }
    public string? Role { get; set; }
}

public sealed class GitRepoRefreshRequest
{
    public string SessionId { get; set; } = "";
    public string? RepoRoot { get; set; }
}

public sealed class GitFileEntry
{
    public string Path { get; set; } = "";
    public string Status { get; set; } = "";
    public string? OriginalPath { get; set; }
    public int Additions { get; set; }
    public int Deletions { get; set; }
}

public sealed class GitLogEntry
{
    public string Hash { get; set; } = "";
    public string ShortHash { get; set; } = "";
    public string Message { get; set; } = "";
    public string Author { get; set; } = "";
    public string Date { get; set; } = "";
}

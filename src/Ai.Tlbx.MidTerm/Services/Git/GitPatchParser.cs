using Ai.Tlbx.MidTerm.Models.Git;

namespace Ai.Tlbx.MidTerm.Services.Git;

internal sealed class GitCommitMetadata
{
    public string Hash { get; init; } = "";
    public string ShortHash { get; init; } = "";
    public string Subject { get; init; } = "";
    public string Body { get; init; } = "";
    public string Author { get; init; } = "";
    public string AuthoredDate { get; init; } = "";
    public string CommittedDate { get; init; } = "";
    public string[] ParentHashes { get; init; } = [];
}

internal static class GitPatchParser
{
    internal static GitDiffViewResponse ParseDiff(string scope, string patch, bool isTruncated)
    {
        return new GitDiffViewResponse
        {
            Scope = scope,
            Title = scope.Equals("staged", StringComparison.OrdinalIgnoreCase) ? "Staged diff" : "Working tree diff",
            IsTruncated = isTruncated,
            Files = ParseFiles(patch, isTruncated)
        };
    }

    internal static GitCommitDetailsResponse ParseCommitDetails(
        GitCommitMetadata metadata,
        string patch,
        bool isTruncated,
        int parentIndex = 0)
    {
        var files = ParseFiles(patch, isTruncated);

        return new GitCommitDetailsResponse
        {
            Hash = metadata.Hash,
            ShortHash = metadata.ShortHash,
            Subject = metadata.Subject,
            Body = metadata.Body,
            Author = metadata.Author,
            AuthoredDate = metadata.AuthoredDate,
            CommittedDate = metadata.CommittedDate,
            ParentHashes = metadata.ParentHashes,
            IsRootCommit = metadata.ParentHashes.Length == 0,
            ComparedParentIndex = metadata.ParentHashes.Length == 0 ? null : parentIndex,
            ComparedParentHash = metadata.ParentHashes.Length == 0 ? null : metadata.ParentHashes[parentIndex],
            TotalAdditions = files.Sum(file => file.Additions),
            TotalDeletions = files.Sum(file => file.Deletions),
            IsTruncated = isTruncated,
            Files = files
        };
    }

    private static GitDiffFileView[] ParseFiles(string patch, bool isTruncated)
    {
        if (string.IsNullOrWhiteSpace(patch))
        {
            return [];
        }

        var files = new List<GitDiffFileView>();
        GitDiffFileView? currentFile = null;
        List<GitDiffHunk>? currentHunks = null;
        GitDiffHunk? currentHunk = null;
        List<GitDiffLine>? currentLines = null;
        string? oldPath = null;
        string? newPath = null;

        foreach (var rawLine in NormalizeLines(patch))
        {
            // A blank context line starts with a space; the split terminator is not a patch line.
            if (rawLine.Length == 0) continue;
            if (rawLine.StartsWith("diff --git ", StringComparison.Ordinal))
            {
                FinalizeCurrentFile(files, ref currentFile, ref currentHunks, ref currentHunk, ref currentLines);

                currentFile = CreateFileView(rawLine);
                currentHunks = [];
                oldPath = null;
                newPath = null;
                continue;
            }

            if (currentFile is null)
            {
                continue;
            }

            if (rawLine.StartsWith("rename from ", StringComparison.Ordinal))
            {
                currentFile.Status = "renamed";
                currentFile.OriginalPath = NormalizePath(rawLine["rename from ".Length..]);
                continue;
            }

            if (rawLine.StartsWith("rename to ", StringComparison.Ordinal))
            {
                currentFile.Status = "renamed";
                currentFile.Path = NormalizePath(rawLine["rename to ".Length..]);
                continue;
            }

            if (rawLine.StartsWith("copy from ", StringComparison.Ordinal))
            {
                currentFile.Status = "copied";
                currentFile.OriginalPath = NormalizePath(rawLine["copy from ".Length..]);
                continue;
            }

            if (rawLine.StartsWith("copy to ", StringComparison.Ordinal))
            {
                currentFile.Status = "copied";
                currentFile.Path = NormalizePath(rawLine["copy to ".Length..]);
                continue;
            }

            if (rawLine.StartsWith("new file mode ", StringComparison.Ordinal))
            {
                currentFile.Status = "added";
                continue;
            }

            if (rawLine.StartsWith("deleted file mode ", StringComparison.Ordinal))
            {
                currentFile.Status = "deleted";
                continue;
            }

            if (rawLine.StartsWith("Binary files ", StringComparison.Ordinal) ||
                rawLine.StartsWith("GIT binary patch", StringComparison.Ordinal))
            {
                currentFile.IsBinary = true;
                continue;
            }

            if (rawLine.StartsWith("--- ", StringComparison.Ordinal))
            {
                oldPath = NormalizePath(rawLine[4..], stripDiffPrefix: true);
                currentFile.OriginalPath = string.IsNullOrEmpty(oldPath) ? null : oldPath;

                if (currentFile.Status == "deleted" && !string.IsNullOrEmpty(oldPath))
                {
                    currentFile.Path = oldPath;
                }

                continue;
            }

            if (rawLine.StartsWith("+++ ", StringComparison.Ordinal))
            {
                newPath = NormalizePath(rawLine[4..], stripDiffPrefix: true);
                if (!string.IsNullOrEmpty(newPath))
                {
                    currentFile.Path = newPath;
                }
                else if (currentFile.Status == "added" && !string.IsNullOrEmpty(oldPath))
                {
                    currentFile.Path = oldPath;
                }

                continue;
            }

            if (rawLine.StartsWith("@@", StringComparison.Ordinal))
            {
                if (currentHunk is not null && currentLines is not null)
                {
                    currentHunk.Lines = currentLines.ToArray();
                    currentHunks?.Add(currentHunk);
                }

                currentHunk = new GitDiffHunk { Header = rawLine };
                currentLines = [];
                continue;
            }

            if (currentHunk is null || currentLines is null)
            {
                continue;
            }

            if (rawLine.Length > 0 && rawLine[0] == '+')
            {
                currentLines.Add(new GitDiffLine { Kind = "add", Text = rawLine });
                currentFile.Additions++;
            }
            else if (rawLine.Length > 0 && rawLine[0] == '-')
            {
                currentLines.Add(new GitDiffLine { Kind = "del", Text = rawLine });
                currentFile.Deletions++;
            }
            else if (rawLine.StartsWith(@"\ No newline", StringComparison.Ordinal))
            {
                currentLines.Add(new GitDiffLine { Kind = "meta", Text = rawLine });
            }
            else
            {
                currentLines.Add(new GitDiffLine { Kind = "context", Text = rawLine });
            }
        }

        FinalizeCurrentFile(files, ref currentFile, ref currentHunks, ref currentHunk, ref currentLines);

        if (isTruncated && files.Count > 0)
        {
            files[^1].IsTruncated = true;
        }

        foreach (var file in files) GitDiffProjection.Apply(file);
        return files.ToArray();
    }

    private static GitDiffFileView CreateFileView(string diffHeader)
    {
        const string prefix = "diff --git ";
        var header = diffHeader.StartsWith(prefix, StringComparison.Ordinal)
            ? diffHeader[prefix.Length..]
            : diffHeader;
        var separatorIndex = header.IndexOf(" b/", StringComparison.Ordinal);
        var oldPath = separatorIndex >= 0 ? NormalizePath(header[..separatorIndex], stripDiffPrefix: true) : "";
        var newPath = separatorIndex >= 0 ? NormalizePath(header[(separatorIndex + 1)..], stripDiffPrefix: true) : "";

        return new GitDiffFileView
        {
            Path = newPath,
            OriginalPath = oldPath,
            Status = "modified"
        };
    }

    private static IEnumerable<string> NormalizeLines(string value)
    {
        return value.Replace("\r\n", "\n", StringComparison.Ordinal)
            .Split('\n', StringSplitOptions.None);
    }

    private static string NormalizePath(string value, bool stripDiffPrefix = false)
    {
        var trimmed = value.EndsWith('\t', StringComparison.Ordinal) ? value[..^1] : value;
        if (string.Equals(trimmed, "/dev/null", StringComparison.Ordinal))
        {
            return string.Empty;
        }

        if (trimmed.Length > 1 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            trimmed = DecodeQuotedPath(trimmed[1..^1]);
        }

        if (stripDiffPrefix && (trimmed.StartsWith("a/", StringComparison.Ordinal) || trimmed.StartsWith("b/", StringComparison.Ordinal)))
        {
            trimmed = trimmed[2..];
        }

        return trimmed;
    }

    private static string DecodeQuotedPath(string value)
    {
        var bytes = new List<byte>();
        for (var i = 0; i < value.Length;)
        {
            if (value[i] != '\\')
            {
                var end = value.IndexOf('\\', i, StringComparison.Ordinal);
                if (end < 0) end = value.Length;
                bytes.AddRange(System.Text.Encoding.UTF8.GetBytes(value[i..end]));
                i = end;
                continue;
            }
            i++;
            if (i == value.Length) { bytes.Add((byte)'\\'); break; }
            if (value[i] is >= '0' and <= '7')
            {
                var number = 0;
                for (var count = 0; count < 3 && i < value.Length && value[i] is >= '0' and <= '7'; count++, i++)
                    number = number * 8 + value[i] - '0';
                bytes.Add((byte)number);
            }
            else
            {
                bytes.Add(value[i++] switch
                {
                    'a' => 7, 'b' => 8, 't' => 9, 'n' => 10, 'v' => 11, 'f' => 12,
                    'r' => 13, '\\' => (byte)'\\', '"' => (byte)'"', var character => (byte)character
                });
            }
        }
        return System.Text.Encoding.UTF8.GetString(bytes.ToArray());
    }

    private static void FinalizeCurrentFile(
        List<GitDiffFileView> files,
        ref GitDiffFileView? currentFile,
        ref List<GitDiffHunk>? currentHunks,
        ref GitDiffHunk? currentHunk,
        ref List<GitDiffLine>? currentLines)
    {
        if (currentFile is null)
        {
            return;
        }

        if (currentHunk is not null && currentLines is not null)
        {
            currentHunk.Lines = currentLines.ToArray();
            currentHunks?.Add(currentHunk);
        }

        currentFile.Hunks = currentHunks?.ToArray() ?? [];
        if (string.IsNullOrEmpty(currentFile.Path))
        {
            currentFile.Path = currentFile.OriginalPath ?? "";
        }

        if (string.Equals(currentFile.OriginalPath, currentFile.Path, StringComparison.Ordinal))
        {
            currentFile.OriginalPath = null;
        }

        files.Add(currentFile);
        currentFile = null;
        currentHunks = null;
        currentHunk = null;
        currentLines = null;
    }
}

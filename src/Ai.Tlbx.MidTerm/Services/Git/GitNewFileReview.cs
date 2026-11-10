using System.Globalization;
using System.Text;
using Ai.Tlbx.MidTerm.Models.Git;

namespace Ai.Tlbx.MidTerm.Services.Git;

internal static class GitNewFileReview
{
    internal static async Task<GitDiffViewResponse> ReadAsync(string root, string relative, CancellationToken ct)
    {
        if (Path.IsPathRooted(relative) || relative.AsSpan().IndexOf('\0') >= 0 ||
            relative.Split('/').Any(part => part is "." or ".."))
            throw new UnauthorizedAccessException("Select a file inside the repository.");
        var full = Path.GetFullPath(Path.Combine(root, relative));
        if (!FileService.IsWithinDirectory(full, root)) throw new UnauthorizedAccessException();
        var info = new FileInfo(full);
        var file = new GitDiffFileView
        {
            Path = relative, Status = "untracked", IsNewFile = true,
            MimeType = FileService.GetMimeType(relative), HasLineStats = false
        };
        if (info.LinkTarget is { } target)
        {
            file.SizeBytes = Encoding.UTF8.GetByteCount(target);
            file.MimeType = "inode/symlink"; file.Encoding = "utf8";
            SetText(file, target, truncated: false);
        }
        else
        {
            var physical = FileDocumentService.ResolvePath(full);
            if (!FileService.IsWithinDirectory(physical, FileDocumentService.ResolvePath(root)))
                throw new UnauthorizedAccessException();
            file.SizeBytes = new FileInfo(physical).Length;
            var isText = FileService.CheckIsText(physical, file.SizeBytes.Value)
                ?? throw new IOException("The selected file could not be inspected.");
            file.IsBinary = !isText;
            if (isText)
            {
                try
                {
                    var document = await new FileDocumentService().ReadAsync(physical, full: false, ct).ConfigureAwait(false);
                    file.SizeBytes = document.Size; file.Encoding = document.Encoding;
                    file.ContentVersion = document.Version;
                    SetText(file, document.Content, document.IsTruncated);
                }
                catch (FileDocumentException error) when (error.Code == "unsupported_encoding")
                { file.IsBinary = true; file.Encoding = "binary"; }
            }
        }
        return new GitDiffViewResponse { Scope = "worktree", Title = relative, IsTruncated = file.IsTruncated, Files = [file] };
    }

    private static void SetText(GitDiffFileView file, string text, bool truncated)
    {
        file.HasLineStats = !truncated; file.IsTruncated = truncated;
        if (text.Length == 0) return;
        var lines = text.Split('\n').ToList();
        var endsWithNewline = text.EndsWith('\n', StringComparison.Ordinal);
        if (endsWithNewline) lines.RemoveAt(lines.Count - 1);
        file.Additions = lines.Count;
        var patchLines = lines.Select(line => new GitDiffLine { Kind = "add", Text = "+" + line }).ToList();
        if (!endsWithNewline && !truncated) patchLines.Add(new GitDiffLine { Kind = "meta", Text = @"\ No newline at end of file" });
        file.Hunks = [new GitDiffHunk { Header = "@@ -0,0 +1," + file.Additions.ToString(CultureInfo.InvariantCulture) + " @@", Lines = patchLines.ToArray() }];
        GitDiffProjection.Apply(file);
    }
}

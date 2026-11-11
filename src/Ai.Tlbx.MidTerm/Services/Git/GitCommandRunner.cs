using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Security.Cryptography;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Models.Git;

using Ai.Tlbx.MidTerm.Services.Sessions;
namespace Ai.Tlbx.MidTerm.Services.Git;

internal static class GitCommandRunner
{
    internal sealed record GitRepositoryInfo(string RepoRoot, string GitDir, string CommonGitDir);
    internal sealed record GitWorktreeInfo(
        string Path,
        string? Head,
        string? Branch,
        bool IsDetached,
        bool IsLocked,
        bool IsPrunable);

    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan CloneTimeout = TimeSpan.FromMinutes(10);
    private const int MaxPatchOutputChars = 160_000;
    private static string? _runAsUser;
    private static bool _isServiceMode;

    internal static void Configure(string? runAsUser, bool isServiceMode)
    {
        _runAsUser = runAsUser;
        _isServiceMode = isServiceMode;
    }

    private static readonly object _logLock = new();
    private static (string Args, string WorkingDir, int ExitCode, string Stdout, string Stderr, DateTime Timestamp)? _lastCommand;

    internal static GitCommandLog? GetLastCommandLog()
    {
        lock (_logLock)
        {
            if (_lastCommand is not var (args, dir, exit, stdout, stderr, ts)) return null;
            return new GitCommandLog
            {
                Args = args,
                WorkingDir = dir,
                ExitCode = exit,
                Stdout = stdout.Length > 500 ? stdout[..500] + "..." : stdout,
                Stderr = stderr.Length > 500 ? stderr[..500] + "..." : stderr,
                Timestamp = ts.ToString("O", CultureInfo.InvariantCulture)
            };
        }
    }

    private static void RecordCommand(string workingDir, string[] args, int exitCode, string stdout, string stderr)
    {
        lock (_logLock)
        {
            _lastCommand = ($"git {string.Join(' ', args)}", workingDir, exitCode, stdout, stderr, DateTime.UtcNow);
        }
    }

    internal static async Task<string?> GetGitVersionAsync()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "git",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };
            psi.ArgumentList.Add("--version");

            using var cts = new CancellationTokenSource(CommandTimeout);
            var output = await Process.RunAndCaptureTextAsync(psi, cts.Token);
            if (output.ExitStatus.Canceled || output.ExitStatus.ExitCode != 0) return null;
            var stdout = output.StandardOutput;
            // Match StreamReader's UTF-8 BOM handling for version wrappers.
            if (stdout.Length > 0 && stdout[0] == '\uFEFF') stdout = stdout[1..];
            return stdout.Trim();
        }
        catch
        {
            return null;
        }
    }

    internal static async Task<string?> GetRepoRootAsync(string workingDir)
    {
        var (exitCode, stdout, _) = await RunGitAsync(workingDir, "rev-parse", "--show-toplevel");
        if (exitCode != 0) return null;
        var root = stdout.TrimEnd('\r', '\n');
        return string.IsNullOrEmpty(root) ? null : root;
    }

    internal static async Task<GitRepositoryInfo?> GetRepositoryInfoAsync(string workingDir)
    {
        var repoRoot = await GetRepoRootAsync(workingDir).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(repoRoot))
        {
            return null;
        }

        var gitDir = await GetResolvedGitPathAsync(workingDir, "rev-parse", "--git-dir").ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(gitDir))
        {
            return null;
        }

        var commonGitDir = await GetResolvedGitPathAsync(workingDir, "rev-parse", "--git-common-dir").ConfigureAwait(false)
            ?? gitDir;

        return new GitRepositoryInfo(
            Path.TrimEndingDirectorySeparator(FileDocumentService.ResolvePath(repoRoot)),
            Path.TrimEndingDirectorySeparator(FileDocumentService.ResolvePath(gitDir)),
            Path.TrimEndingDirectorySeparator(FileDocumentService.ResolvePath(commonGitDir)));
    }

    internal static async Task<IReadOnlyList<GitWorktreeInfo>> ListWorktreesAsync(string workingDir)
    {
        var (exitCode, stdout, _) = await RunGitAsync(workingDir, "worktree", "list", "--porcelain");
        if (exitCode != 0)
        {
            return [];
        }

        var result = new List<GitWorktreeInfo>();
        string? path = null;
        string? head = null;
        string? branch = null;
        var detached = false;
        var locked = false;
        var prunable = false;

        void Flush()
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            result.Add(new GitWorktreeInfo(
                Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
                head,
                branch,
                detached,
                locked,
                prunable));
            path = null;
            head = null;
            branch = null;
            detached = false;
            locked = false;
            prunable = false;
        }

        foreach (var rawLine in stdout.Split('\n'))
        {
            var line = rawLine.TrimEnd('\r');
            if (string.IsNullOrWhiteSpace(line))
            {
                Flush();
                continue;
            }

            if (line.StartsWith("worktree ", StringComparison.Ordinal))
            {
                Flush();
                path = line["worktree ".Length..];
                continue;
            }

            if (line.StartsWith("HEAD ", StringComparison.Ordinal))
            {
                head = line["HEAD ".Length..];
                continue;
            }

            if (line.StartsWith("branch ", StringComparison.Ordinal))
            {
                var fullRef = line["branch ".Length..];
                branch = fullRef.StartsWith("refs/heads/", StringComparison.Ordinal)
                    ? fullRef["refs/heads/".Length..]
                    : fullRef;
                continue;
            }

            if (string.Equals(line, "detached", StringComparison.Ordinal))
            {
                detached = true;
                continue;
            }

            if (line.StartsWith("locked", StringComparison.Ordinal))
            {
                locked = true;
                continue;
            }

            if (line.StartsWith("prunable", StringComparison.Ordinal))
            {
                prunable = true;
            }
        }

        Flush();
        return result;
    }

    internal static async Task<int> GetWorktreeChangeCountAsync(string workingDir)
    {
        var (exitCode, stdout, _) = await RunGitAsync(workingDir, "status", "--porcelain");
        if (exitCode != 0)
        {
            return 0;
        }

        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    internal static async Task<GitStatusResponse> GetStatusAsync(string repoRoot)
    {
        var response = new GitStatusResponse { RepoRoot = repoRoot };
        var (exitCode, stdout, stderr) = await RunGitAsync(repoRoot, "status", "--porcelain=v2", "-z", "-b", "--ahead-behind", "--untracked-files=all");

        if (exitCode != 0)
        {
            Log.Verbose(() => $"[Git] status failed: {stderr}");
            response.ReadState = stderr.Contains("not a git repository", StringComparison.OrdinalIgnoreCase) ? "no-repo" : "error";
            response.ErrorCode = response.ReadState == "no-repo" ? "no_repository" : "git_read_failed";
            response.ErrorDetail = stderr[..Math.Min(stderr.Length, 16000)];
            return response;
        }

        response = ParseStatus(repoRoot, stdout);
        response.IndexVersion = await GetIndexVersionAsync(repoRoot).ConfigureAwait(false);
        response.ReadAt = DateTimeOffset.UtcNow;
        return response;
    }

    internal static async Task<string?> GetCommittedHeadAsync(string root)
    {
        var (code, output, _) = await RunGitAsync(root, "rev-parse", "--verify", "HEAD^{commit}").ConfigureAwait(false);
        var hash = output.TrimEnd('\r', '\n');
        return code == 0 && hash.Length is 40 or 64 && hash.All(char.IsAsciiHexDigit) ? hash : null;
    }

    internal static GitStatusResponse ParseStatus(string repoRoot, string stdout)
    {
        var response = new GitStatusResponse { RepoRoot = repoRoot };
        var staged = new List<GitFileEntry>();
        var modified = new List<GitFileEntry>();
        var untracked = new List<GitFileEntry>();
        var conflicted = new List<GitFileEntry>();

        var records = stdout.Split('\0', StringSplitOptions.RemoveEmptyEntries);
        for (var index = 0; index < records.Length; index++)
        {
            var line = records[index];
            if (line.StartsWith("# branch.head ", StringComparison.Ordinal))
            {
                response.Branch = line["# branch.head ".Length..];
            }
            else if (line.StartsWith("# branch.oid ", StringComparison.Ordinal))
            {
                var oid = line["# branch.oid ".Length..];
                response.HeadHash = oid == "(initial)" ? "" : oid;
            }
            else if (line.StartsWith("# branch.ab ", StringComparison.Ordinal))
            {
                var parts = line["# branch.ab ".Length..].Split(' ');
                if (parts.Length >= 2)
                {
                    if (int.TryParse(parts[0], CultureInfo.InvariantCulture, out var ahead)) response.Ahead = ahead;
                    if (int.TryParse(parts[1], CultureInfo.InvariantCulture, out var behind)) response.Behind = Math.Abs(behind);
                }
            }
            else if (line.StartsWith("1 ", StringComparison.Ordinal) || line.StartsWith("2 ", StringComparison.Ordinal))
            {
                var original = line.StartsWith("2 ", StringComparison.Ordinal) && index + 1 < records.Length ? records[++index] : null;
                ParseChangedEntry(line, original, staged, modified);
            }
            else if (line.StartsWith("u ", StringComparison.Ordinal))
            {
                ParseUnmergedEntry(line, conflicted);
            }
            else if (line.StartsWith("? ", StringComparison.Ordinal))
            {
                untracked.Add(new GitFileEntry
                {
                    Path = line[2..],
                    Status = "untracked"
                });
            }
        }

        response.Staged = staged.ToArray();
        response.Modified = modified.ToArray();
        response.Untracked = untracked.ToArray();
        response.Conflicted = conflicted.ToArray();
        return response;
    }

    internal static async Task<GitLogEntry[]> GetLogAsync(string repoRoot, int count = 20, int skip = 0, string? head = null)
    {
        if (count < 1 || count > 200 || skip < 0) throw new IOException("Invalid Git history page.");
        if (head is not null && (head.Length is not (40 or 64) || !head.All(Uri.IsHexDigit))) throw new IOException("Invalid Git history snapshot.");
        var revision = head ?? await GetCommittedHeadAsync(repoRoot).ConfigureAwait(false);
        if (revision is null) return [];
        var (exitCode, stdout, stderr) = await RunGitAsync(repoRoot, "log", "--format=%H%x00%h%x00%s%x00%an%x00%ai%x00",
            "-n", count.ToString(CultureInfo.InvariantCulture), "--skip=" + skip.ToString(CultureInfo.InvariantCulture), revision, "--").ConfigureAwait(false);
        if (exitCode != 0) throw new IOException("Git history could not be read. " + stderr);
        var lines = stdout.Split('\0');
        List<GitLogEntry> entries = [with(capacity: Math.Min(count, lines.Length / 5))];

        for (var i = 0; i + 4 < lines.Length; i += 5)
        {
            entries.Add(new GitLogEntry
            {
                Hash = lines[i].TrimStart('\r', '\n'),
                ShortHash = lines[i + 1],
                Message = lines[i + 2],
                Author = lines[i + 3],
                Date = lines[i + 4]
            });
        }

        return entries.ToArray();
    }

    internal static async Task<int> GetStashCountAsync(string repoRoot)
    {
        var (exitCode, stdout, _) = await RunGitAsync(repoRoot, "stash", "list");
        if (exitCode != 0) return 0;
        var trimmed = stdout.Trim();
        return string.IsNullOrEmpty(trimmed) ? 0 : trimmed.Split('\n').Length;
    }

    internal static Task<(int ExitCode, string Stdout, string Stderr)> CloneAsync(
        string workingDir,
        string repositoryUrl,
        string destinationDirectoryName)
    {
        return RunGitAsync(
            workingDir,
            ["clone", "--", repositoryUrl, destinationDirectoryName],
            CloneTimeout);
    }

    internal static async Task<string[]> GetTrackedAndUntrackedPathsAsync(string repoRoot)
    {
        var (exitCode, stdout, _) = await RunGitAsync(repoRoot, "ls-files", "-co", "--exclude-standard");
        if (exitCode != 0)
        {
            return [];
        }

        return stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    internal static async Task<HashSet<string>?> GetIgnoredPathsAsync(string repoRoot, IReadOnlyList<string> paths)
    {
        var comparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
        var ignored = new HashSet<string>(comparer);
        var offset = 0;
        while (offset < paths.Count)
        {
            var batch = new List<string>();
            var chars = 0;
            while (offset < paths.Count && (batch.Count == 0 || chars + paths[offset].Length < 6000))
            { var path = paths[offset++]; batch.Add(path); chars += path.Length + 3; }
            var args = new[] { "-c", "core.quotepath=false", "check-ignore", "--verbose", "--non-matching", "--" }.Concat(batch).ToArray();
            var (code, stdout, _) = await RunGitAsync(repoRoot, args);
            if (code is not 0 and not 1) return null;
            var records = stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            if (records.Length != batch.Count) return null;
            // One verbose record per input, including nonmatches. C-quoted control characters
            // remain within one record, so input order avoids lossy filename decoding.
            for (var i = 0; i < records.Length; i++)
                if (!records[i].StartsWith("::\t", StringComparison.Ordinal)) ignored.Add(batch[i]);
        }
        return ignored;
    }

    internal static async Task<Dictionary<string, (int Additions, int Deletions)>> GetNumStatAsync(string repoRoot, bool staged)
    {
        var result = new Dictionary<string, (int, int)>(StringComparer.Ordinal);

        var args = staged ? new[] { "diff", "--cached", "--numstat", "-z" } : ["diff", "--numstat", "-z"];
        var (_, output, _) = await RunGitAsync(repoRoot, args).ConfigureAwait(false);
        ParseNumStat(result, output);

        return result;
    }

    private static void ParseNumStat(Dictionary<string, (int Additions, int Deletions)> result, string output)
    {
        var records = output.Split('\0');
        for (var i = 0; i < records.Length; i++)
        {
            var parts = records[i].Split('\t', 3);
            if (parts.Length != 3) continue;
            var path = parts[2];
            if (path.Length == 0 && i + 2 < records.Length)
            {
                // -z encodes renames as stats + NUL + original + NUL + destination.
                path = records[i + 2]; i += 2;
            }
            if (parts[0] == "-" || parts[1] == "-") continue;

            if (!int.TryParse(parts[0], CultureInfo.InvariantCulture, out var additions)) continue;
            if (!int.TryParse(parts[1], CultureInfo.InvariantCulture, out var deletions)) continue;

            if (result.TryGetValue(path, out var existing))
            {
                result[path] = (existing.Additions + additions, existing.Deletions + deletions);
            }
            else
            {
                result[path] = (additions, deletions);
            }
        }
    }

    internal static async Task<string> GetDiffAsync(string repoRoot, string path, bool staged)
    {
        var args = staged
            ? new[] { "--literal-pathspecs", "diff", "--cached", "--", path }
            : new[] { "--literal-pathspecs", "diff", "--", path };
        var (_, stdout, _) = await RunGitAsync(repoRoot, args);
        return stdout;
    }

    internal static async Task<(string Patch, bool IsTruncated)> GetDiffPatchAsync(
        string repoRoot,
        string path,
        bool staged,
        string? originalPath = null,
        bool untracked = false)
    {
        var args = new List<string> { "--literal-pathspecs", "diff", "--no-ext-diff", "--no-textconv", "--unified=3", "--no-color", "--src-prefix=a/", "--dst-prefix=b/", "--no-relative" };
        if (untracked) args.Add("--no-index");
        else { args.Add("--find-renames"); if (staged) args.Add("--cached"); }
        args.Add("--");
        if (untracked) args.Add("/dev/null");
        args.Add(path);
        if (!untracked && originalPath is not null && originalPath != path) args.Add(originalPath);
        var (exitCode, stdout, _) = await RunGitAsync(repoRoot, args.ToArray());
        if (exitCode != 0 && !(untracked && exitCode == 1))
        {
            return (string.Empty, false);
        }

        return TrimOutput(stdout, MaxPatchOutputChars);
    }

    internal static async Task<GitCommitMetadata?> GetCommitMetadataAsync(string repoRoot, string hash)
    {
        var (resolvedCode, resolved, _) = await RunGitAsync(repoRoot, "rev-parse", "--verify", "--end-of-options", hash + "^{commit}").ConfigureAwait(false);
        if (resolvedCode != 0) return null;
        hash = resolved.TrimEnd('\r', '\n');
        if (hash.Length is not (40 or 64) || !hash.All(char.IsAsciiHexDigit)) return null;
        const string format = "%H%x00%h%x00%s%x00%b%x00%an%x00%aI%x00%cI%x00%P";
        var (exitCode, stdout, _) = await RunGitAsync(repoRoot, "show", "--no-patch", $"--format={format}", hash, "--");
        if (exitCode != 0)
        {
            return null;
        }

        var parts = stdout.TrimEnd('\r', '\n').Split('\0');
        if (parts.Length < 8)
        {
            return null;
        }

        return new GitCommitMetadata
        {
            Hash = parts[0],
            ShortHash = parts[1],
            Subject = parts[2],
            Body = parts[3].TrimEnd(),
            Author = parts[4],
            AuthoredDate = parts[5],
            CommittedDate = parts[6],
            ParentHashes = parts[7]
                .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        };
    }

    internal static async Task<(string Patch, bool IsTruncated)> GetCommitPatchAsync(string repoRoot, string hash, string? parentHash)
    {
        var args = parentHash is null
            ? new[] { "diff-tree", "--root", "-r", "--no-commit-id", "-p", "--find-renames", "--unified=3", "--no-ext-diff", "--no-textconv", "--no-color", "--src-prefix=a/", "--dst-prefix=b/", "--no-relative", hash, "--" }
            : ["diff", "--find-renames", "--unified=3", "--no-ext-diff", "--no-textconv", "--no-color", "--src-prefix=a/", "--dst-prefix=b/", "--no-relative", parentHash, hash, "--"];
        var (exitCode, stdout, stderr) = await RunGitAsync(repoRoot, args).ConfigureAwait(false);
        if (exitCode != 0)
        {
            throw new IOException("Unable to read the selected commit comparison. " + stderr[..Math.Min(stderr.Length, 16000)]);
        }

        return TrimOutput(stdout, MaxPatchOutputChars);
    }

    internal static Task<(int ExitCode, string Stdout, string Stderr)> RunGitInDirectoryAsync(string workingDir, params string[] args)
    {
        return RunGitAsync(workingDir, args);
    }

    private static void ParseChangedEntry(string line, string? originalPath, List<GitFileEntry> staged, List<GitFileEntry> modified)
    {
        var count = line.StartsWith("2 ", StringComparison.Ordinal) ? 10 : 9;
        var parts = line.Split(' ', count, StringSplitOptions.None);
        if (parts.Length != count) return;

        var xy = parts[1];
        if (xy.Length < 2) return;

        var indexStatus = xy[0];
        var worktreeStatus = xy[1];

        var filePath = parts[^1];

        if (indexStatus != '.')
        {
            staged.Add(new GitFileEntry
            {
                Path = filePath,
                Status = MapStatusChar(indexStatus),
                OriginalPath = originalPath
            });
        }

        if (worktreeStatus != '.')
        {
            modified.Add(new GitFileEntry
            {
                Path = filePath,
                Status = MapStatusChar(worktreeStatus),
                OriginalPath = originalPath
            });
        }
    }

    private static void ParseUnmergedEntry(string line, List<GitFileEntry> conflicted)
    {
        var parts = line.Split(' ', 11, StringSplitOptions.None);
        if (parts.Length != 11) return;

        conflicted.Add(new GitFileEntry
        {
            Path = parts[^1],
            Status = "conflicted"
        });
    }

    private static string MapStatusChar(char c) => c switch
    {
        'M' => "modified",
        'A' => "added",
        'D' => "deleted",
        'R' => "renamed",
        'C' => "copied",
        'T' => "type-changed",
        'U' => "unmerged",
        _ => c.ToString()
    };

    private static async Task<string?> GetResolvedGitPathAsync(string workingDir, params string[] args)
    {
        var (exitCode, stdout, _) = await RunGitAsync(workingDir, args).ConfigureAwait(false);
        if (exitCode != 0)
        {
            return null;
        }

        var rawPath = stdout.TrimEnd('\r', '\n');
        if (string.IsNullOrWhiteSpace(rawPath))
        {
            return null;
        }

        return Path.IsPathRooted(rawPath)
            ? rawPath
            : Path.GetFullPath(Path.Combine(workingDir, rawPath));
    }

    internal static Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
        string workingDir, IReadOnlyList<string> args, CancellationToken ct) =>
        ExecuteAsync(workingDir, args, null, ct);

    internal static Task<(int ExitCode, string Stdout, string Stderr)> ExecuteAsync(
        string workingDir, IReadOnlyList<string> args, IReadOnlyDictionary<string, string?>? environment, CancellationToken ct) =>
        RunGitAsync(workingDir, args, TimeSpan.FromMinutes(2), environment, ct);

    internal static async Task<string> GetIndexPathAsync(string workingDir)
    {
        return await GetResolvedGitPathAsync(workingDir, "rev-parse", "--git-path", "index").ConfigureAwait(false)
            ?? throw new GitOperationException(404, "no_repository", "The repository index is unavailable.");
    }

    internal static async Task<string> GetIndexVersionAsync(string workingDir)
    {
        var index = await GetIndexPathAsync(workingDir).ConfigureAwait(false);
        return await HashIndexAsync(index).ConfigureAwait(false);
    }

    internal static async Task<string> HashIndexAsync(string path)
    {
        if (!File.Exists(path)) return "absent";
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 65536, FileOptions.Asynchronous);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream).ConfigureAwait(false));
    }

    private static Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(string workingDir, params string[] args)
    {
        return RunGitAsync(workingDir, args, CommandTimeout);
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitAsync(
        string workingDir,
        IReadOnlyList<string> args,
        TimeSpan timeout,
        IReadOnlyDictionary<string, string?>? environment = null,
        CancellationToken cancellation = default)
    {
        var fullArgs = new string[args.Count + 3];
        fullArgs[0] = "--no-optional-locks";
        fullArgs[1] = "-C";
        fullArgs[2] = workingDir;
        for (var i = 0; i < args.Count; i++)
        {
            fullArgs[i + 3] = args[i];
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        cts.CancelAfter(timeout);
        // A server inherited from a Git hook must not address that hook's repository.
        // Only explicit internal overrides (such as our transaction index) may redirect Git.
        var repositoryEnvironment = new Dictionary<string, string?>(StringComparer.Ordinal)
        {
            ["GIT_DIR"] = null, ["GIT_WORK_TREE"] = null, ["GIT_COMMON_DIR"] = null,
            ["GIT_INDEX_FILE"] = null, ["GIT_OBJECT_DIRECTORY"] = null,
            ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = null, ["GIT_NAMESPACE"] = null,
            ["GIT_PREFIX"] = null, ["GIT_CONFIG"] = null, ["GIT_CONFIG_PARAMETERS"] = null,
            ["GIT_CONFIG_COUNT"] = null, ["GIT_CEILING_DIRECTORIES"] = null,
            ["GIT_DISCOVERY_ACROSS_FILESYSTEM"] = null,
            ["GIT_LITERAL_PATHSPECS"] = null, ["GIT_GLOB_PATHSPECS"] = null,
            ["GIT_NOGLOB_PATHSPECS"] = null, ["GIT_ICASE_PATHSPECS"] = null
        };
        if (environment is not null)
            foreach (var pair in environment) repositoryEnvironment[pair.Key] = pair.Value;

        try
        {
#if WINDOWS
            if (_isServiceMode && OperatingSystem.IsWindows())
            {
                using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
                var result = identity.ImpersonationLevel != System.Security.Principal.TokenImpersonationLevel.None
                    ? await TtyHostSpawner.RunCommandAsCurrentIdentityAsync("git", fullArgs, AppContext.BaseDirectory, repositoryEnvironment, cts.Token)
                    : await TtyHostSpawner.RunCommandAsUserAsync("git", fullArgs, AppContext.BaseDirectory, _runAsUser, cts.Token, environmentOverrides: repositoryEnvironment);
                RecordCommand(workingDir, fullArgs, result.ExitCode, result.Stdout, result.Stderr);
                return result;
            }
#endif

            return await RunGitDirectAsync(workingDir, fullArgs, repositoryEnvironment, cts.Token);
        }
        catch (OperationCanceledException)
        {
            Log.Warn(() => $"[Git] Command timed out: git {string.Join(' ', args)}");
            return (-1, "", "Command timed out");
        }
        catch (Exception ex)
        {
            Log.Error(() => $"[Git] Command failed: {ex.Message}");
            return (-1, "", ex.Message);
        }
    }

    private static async Task<(int ExitCode, string Stdout, string Stderr)> RunGitDirectAsync(
        string workingDir, string[] args, IReadOnlyDictionary<string, string?>? environment, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8
        };

        foreach (var arg in args)
        {
            psi.ArgumentList.Add(arg);
        }
        if (environment is not null)
            foreach (var pair in environment)
                if (pair.Value is null) psi.Environment.Remove(pair.Key);
                else psi.Environment[pair.Key] = pair.Value;

        using var process = Process.Start(psi);
        if (process is null)
        {
            Log.Error(() => "[Git] Failed to start git process");
            return (-1, "", "Failed to start git process");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync(ct);
        var stderrTask = process.StandardError.ReadToEndAsync(ct);

        try { await process.WaitForExitAsync(ct); }
        catch (OperationCanceledException)
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        var stdout = await stdoutTask;
        var stderr = await stderrTask;

        RecordCommand(workingDir, args, process.ExitCode, stdout, stderr);
        return (process.ExitCode, stdout, stderr);
    }

    private static (string Text, bool IsTruncated) TrimOutput(string text, int maxChars)
    {
        if (text.Length <= maxChars)
        {
            return (text, false);
        }

        return (text[..maxChars], true);
    }
}

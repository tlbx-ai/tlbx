using Ai.Tlbx.MidTerm.Models.Files;
using Ai.Tlbx.MidTerm.Services.Git;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services;

public static class FileEndpoints
{
    public static void MapFileEndpoints(
        WebApplication app,
        TtyHostSessionManager sessionManager,
        SessionPathAllowlistService allowlistService,
        SettingsService settingsService,
        GitWatcherService gitWatcher)
    {
        var fileService = new FileService(sessionManager, allowlistService);

        app.MapGet("/api/files/picker/home", () =>
        {
            var settings = settingsService.Load();
            var homePath = LauncherPathResolver.ResolveHomePath(settings);
            var startPath = LauncherPathResolver.ResolveStartPath(settings);
            return Results.Json(
                new LauncherPathResponse
                {
                    Path = homePath,
                    HomePath = homePath,
                    StartPath = startPath
                },
                AppJsonContext.Default.LauncherPathResponse);
        });

        app.MapGet("/api/files/picker/roots", () =>
        {
            var roots = GetLauncherRootEntries().ToArray();
            return Results.Json(
                new LauncherDirectoryListResponse
                {
                    Path = string.Empty,
                    ParentPath = null,
                    Entries = roots
                },
                AppJsonContext.Default.LauncherDirectoryListResponse);
        });

        app.MapGet("/api/files/picker/directories", (string path) =>
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Results.BadRequest("Path is required");
            }

            if (!TryNormalizeExistingDirectory(path, out var fullPath, out var errorResult))
            {
                return errorResult!;
            }

            try
            {
                var entries = Directory.EnumerateDirectories(fullPath)
                    .Select(directory => new LauncherDirectoryEntry
                    {
                        Name = Path.GetFileName(directory),
                        FullPath = directory,
                        IsRoot = false
                    })
                    .OrderBy(entry => entry.Name, StringComparer.OrdinalIgnoreCase)
                    .ToArray();

                var parentPath = Directory.GetParent(fullPath)?.FullName;
                return Results.Json(
                    new LauncherDirectoryListResponse
                    {
                        Path = fullPath,
                        ParentPath = parentPath,
                        Entries = entries
                    },
                    AppJsonContext.Default.LauncherDirectoryListResponse);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.StatusCode(403);
            }
            catch (IOException ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapGet("/api/files/picker/writable", (string path) =>
        {
            if (!TryNormalizeExistingDirectory(path, out var fullPath, out var errorResult))
            {
                return errorResult!;
            }

            return Results.Json(
                new LauncherDirectoryAccessResponse
                {
                    Path = fullPath,
                    CanWrite = IsLauncherDirectoryWritable(fullPath)
                },
                AppJsonContext.Default.LauncherDirectoryAccessResponse);
        });

        app.MapPost("/api/files/picker/folders", (LauncherCreateDirectoryRequest request) =>
        {
            if (!TryNormalizeExistingDirectory(request.ParentPath, out var parentPath, out var errorResult))
            {
                return errorResult!;
            }

            if (!IsLauncherDirectoryWritable(parentPath))
            {
                return Results.Text("Directory is not writable", statusCode: StatusCodes.Status403Forbidden);
            }

            if (!TryValidateLauncherDirectoryName(request.Name, out var directoryName, out var validationError))
            {
                return Results.Text(validationError, statusCode: StatusCodes.Status400BadRequest);
            }

            var targetPath = Path.GetFullPath(Path.Combine(parentPath, directoryName));
            if (!FileService.IsWithinDirectory(targetPath, parentPath))
            {
                return Results.Text(
                    "Folder name must stay within the selected directory",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (Directory.Exists(targetPath) || File.Exists(targetPath))
            {
                return Results.Text(
                    "A file or folder with that name already exists",
                    statusCode: StatusCodes.Status409Conflict);
            }

            try
            {
                Directory.CreateDirectory(targetPath);
                return Results.Json(
                    new LauncherDirectoryMutationResponse
                    {
                        Path = targetPath
                    },
                    AppJsonContext.Default.LauncherDirectoryMutationResponse);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            }
            catch (IOException ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPost("/api/files/picker/clone", async (LauncherCloneRepositoryRequest request, CancellationToken ct) =>
        {
            if (!TryNormalizeExistingDirectory(request.ParentPath, out var parentPath, out var errorResult))
            {
                return errorResult!;
            }

            if (!IsLauncherDirectoryWritable(parentPath))
            {
                return Results.Text("Directory is not writable", statusCode: StatusCodes.Status403Forbidden);
            }

            if (!TryResolveCloneDirectoryName(request.RepositoryUrl, out var directoryName))
            {
                return Results.Text("Repository URL is invalid", statusCode: StatusCodes.Status400BadRequest);
            }

            var repositoryUrl = request.RepositoryUrl.Trim();
            var targetPath = Path.GetFullPath(Path.Combine(parentPath, directoryName));
            if (!FileService.IsWithinDirectory(targetPath, parentPath))
            {
                return Results.Text(
                    "Repository destination must stay within the selected directory",
                    statusCode: StatusCodes.Status400BadRequest);
            }

            if (Directory.Exists(targetPath) || File.Exists(targetPath))
            {
                return Results.Text(
                    "A file or folder with that name already exists",
                    statusCode: StatusCodes.Status409Conflict);
            }

            try
            {
                ct.ThrowIfCancellationRequested();
                var (exitCode, stdout, stderr) = await GitCommandRunner.CloneAsync(
                    parentPath,
                    repositoryUrl,
                    directoryName);
                if (exitCode != 0)
                {
                    var failureText = string.IsNullOrWhiteSpace(stderr)
                        ? (string.IsNullOrWhiteSpace(stdout) ? "Git clone failed" : stdout.Trim())
                        : stderr.Trim();
                    return Results.Text(
                        failureText,
                        statusCode: exitCode < 0 ? StatusCodes.Status500InternalServerError : StatusCodes.Status400BadRequest);
                }

                return Results.Json(
                    new LauncherDirectoryMutationResponse
                    {
                        Path = targetPath
                    },
                    AppJsonContext.Default.LauncherDirectoryMutationResponse);
            }
            catch (OperationCanceledException)
            {
                return Results.Text("Git clone was cancelled", statusCode: 499);
            }
        });

        app.MapPost("/api/files/register", (FileRegisterRequest request) =>
        {
            if (string.IsNullOrEmpty(request.SessionId))
            {
                return Results.BadRequest("sessionId is required");
            }

            if (!fileService.IsSessionValid(request.SessionId))
            {
                return Results.BadRequest("Invalid session");
            }

            fileService.RegisterPaths(request.SessionId, request.Paths);
            return Results.Ok();
        });

        app.MapPost("/api/files/check", async (FileCheckRequest request, string? sessionId) =>
        {
            var results = new Dictionary<string, FilePathInfo>(StringComparer.Ordinal);
            var workingDir = await fileService.GetSessionWorkingDirectoryAsync(sessionId);

            foreach (var path in request.Paths)
            {
                if (!string.IsNullOrEmpty(sessionId) &&
                    !fileService.IsPathAccessible(sessionId, path, workingDir))
                {
                    results[path] = new FilePathInfo { Exists = false };
                    continue;
                }

                results[path] = FileService.GetFileInfo(path);
            }

            return Results.Json(
                new FileCheckResponse { Results = results },
                AppJsonContext.Default.FileCheckResponse);
        });

        app.MapGet("/api/files/list", async (string path, string? sessionId) =>
        {
            if (!FileService.ValidatePath(path, out var errorResult))
            {
                return errorResult!;
            }

            var workingDir = await fileService.GetSessionWorkingDirectoryAsync(sessionId);
            if (!string.IsNullOrEmpty(sessionId) &&
                !fileService.IsPathAccessible(sessionId, path, workingDir))
            {
                return Results.StatusCode(403);
            }

            var fullPath = Path.GetFullPath(path);

            if (!Directory.Exists(fullPath))
            {
                return Results.NotFound("Directory not found");
            }

            try
            {
                var entries = new List<DirectoryEntry>();

                foreach (var dir in Directory.EnumerateDirectories(fullPath))
                {
                    try
                    {
                        var dirInfo = new DirectoryInfo(dir);
                        entries.Add(new DirectoryEntry
                        {
                            Name = dirInfo.Name,
                            IsDirectory = true,
                            Modified = dirInfo.LastWriteTimeUtc
                        });
                    }
                    catch { }
                }

                foreach (var file in Directory.EnumerateFiles(fullPath))
                {
                    try
                    {
                        var fileInfo = new FileInfo(file);
                        entries.Add(new DirectoryEntry
                        {
                            Name = fileInfo.Name,
                            IsDirectory = false,
                            Size = fileInfo.Length,
                            Modified = fileInfo.LastWriteTimeUtc,
                            MimeType = FileService.GetMimeType(fileInfo.Name)
                        });
                    }
                    catch { }
                }

                entries = entries
                    .OrderByDescending(e => e.IsDirectory)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                return Results.Json(
                    new DirectoryListResponse { Path = fullPath, Entries = entries.ToArray() },
                    AppJsonContext.Default.DirectoryListResponse);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.StatusCode(403);
            }
            catch (IOException ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapPut("/api/files/save", async (FileSaveRequest request, string? sessionId, CancellationToken ct) =>
        {
            if (!FileService.ValidatePath(request.Path, out var errorResult))
            {
                return errorResult!;
            }

            var workingDir = await fileService.GetSessionWorkingDirectoryAsync(sessionId);
            if (!string.IsNullOrEmpty(sessionId) &&
                !fileService.IsPathAccessible(sessionId, request.Path, workingDir))
            {
                return Results.StatusCode(403);
            }

            var fullPath = Path.GetFullPath(request.Path);

            if (!File.Exists(fullPath))
            {
                return Results.NotFound("File not found");
            }

            try
            {
                await File.WriteAllTextAsync(fullPath, request.Content, ct);
                var fileInfo = new FileInfo(fullPath);
                return Results.Json(
                    new FileSaveResponse { Success = true, Size = fileInfo.Length },
                    AppJsonContext.Default.FileSaveResponse);
            }
            catch (UnauthorizedAccessException)
            {
                return Results.StatusCode(403);
            }
            catch (IOException ex)
            {
                return Results.Problem(ex.Message);
            }
        });

        app.MapGet("/api/files/view", async (string path, string? sessionId) =>
        {
            return await ServeFileAsync(path, inline: true, sessionId, fileService);
        });

        app.MapGet("/api/files/download", async (string path, string? sessionId) =>
        {
            return await ServeFileAsync(path, inline: false, sessionId, fileService);
        });

        app.MapGet("/api/files/resolve", async (string sessionId, string path, bool deep = false, CancellationToken ct = default) =>
        {
            if (string.IsNullOrEmpty(sessionId) || string.IsNullOrEmpty(path) || path.Contains("..", StringComparison.Ordinal))
            {
                return Results.Json(new FileResolveResponse { Exists = false }, AppJsonContext.Default.FileResolveResponse);
            }

            var session = await sessionManager.GetSessionFreshAsync(sessionId, ct);
            var cwd = session?.CurrentDirectory;
            if (string.IsNullOrEmpty(cwd) || !Directory.Exists(cwd))
            {
                return Results.Json(new FileResolveResponse { Exists = false }, AppJsonContext.Default.FileResolveResponse);
            }

            foreach (var tryPath in FileService.GetSlashVariants(path))
            {
                var exactPath = Path.GetFullPath(Path.Combine(cwd, tryPath));
                if (FileService.IsWithinDirectory(exactPath, cwd) && (File.Exists(exactPath) || Directory.Exists(exactPath)))
                {
                    fileService.RegisterPath(sessionId, exactPath);
                    return Results.Json(FileService.BuildResolveResponse(exactPath), AppJsonContext.Default.FileResolveResponse);
                }
            }

            if (deep)
            {
                foreach (var tryPath in FileService.GetSlashVariants(path))
                {
                    var found = FileService.SearchTree(cwd, tryPath, maxDepth: 5);
                    if (found is not null && FileService.IsWithinDirectory(found, cwd))
                    {
                        fileService.RegisterPath(sessionId, found);
                        return Results.Json(FileService.BuildResolveResponse(found), AppJsonContext.Default.FileResolveResponse);
                    }
                }
            }

            return Results.Json(new FileResolveResponse { Exists = false }, AppJsonContext.Default.FileResolveResponse);
        });

        app.MapGet("/api/files/tree", async (string path, string? sessionId, int depth) =>
        {
            if (string.IsNullOrWhiteSpace(path) || path.Contains("..", StringComparison.Ordinal))
            {
                return Results.BadRequest("Invalid path");
            }

            var fullPath = Path.GetFullPath(path);
            if (!Directory.Exists(fullPath))
            {
                return Results.NotFound("Directory not found");
            }

            if (!string.IsNullOrEmpty(sessionId))
            {
                var workingDir = await fileService.GetSessionWorkingDirectoryAsync(sessionId);
                if (!string.IsNullOrEmpty(workingDir) && !FileService.IsWithinDirectory(fullPath, workingDir))
                {
                    return Results.StatusCode(403);
                }
            }

            var isGitRepo = false;
            string? repoRoot = null;
            HashSet<string>? gitFiles = null;
            Dictionary<string, string>? gitStatusMap = null;

            try
            {
                repoRoot = await GitCommandRunner.GetRepoRootAsync(fullPath);
                if (!string.IsNullOrEmpty(repoRoot))
                {
                    isGitRepo = true;
                    gitFiles = new HashSet<string>(
                        await GitCommandRunner.GetTrackedAndUntrackedPathsAsync(repoRoot),
                        StringComparer.OrdinalIgnoreCase);

                    if (!string.IsNullOrEmpty(sessionId))
                    {
                        var registeredRepo = gitWatcher.GetRepoRoot(sessionId);
                        if (!string.Equals(registeredRepo, repoRoot, StringComparison.OrdinalIgnoreCase))
                        {
                            await gitWatcher.RegisterSessionAsync(sessionId, fullPath);
                        }

                        var cachedStatus = gitWatcher.GetCachedStatus(sessionId);
                        if (cachedStatus is null)
                        {
                            await gitWatcher.RefreshStatusAsync(repoRoot);
                            cachedStatus = gitWatcher.GetCachedStatus(sessionId);
                        }

                        if (cachedStatus is not null)
                        {
                            gitStatusMap = GitFileStatusMapBuilder.Build(cachedStatus);
                        }
                    }
                }
            }
            catch
            {
            }

            var entries = new List<FileTreeEntry>();

            try
            {
                foreach (var dir in Directory.EnumerateDirectories(fullPath))
                {
                    var dirName = Path.GetFileName(dir);

                    if (isGitRepo && gitFiles is not null)
                    {
                        var relativePath = Path.GetRelativePath(repoRoot!, dir).Replace('\\', '/');
                        if (!gitFiles.Contains(relativePath) && dirName != ".git")
                        {
                            var prefix = relativePath + "/";
                            if (!gitFiles.Any(f => f.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
                            {
                                continue;
                            }
                        }
                        if (dirName == ".git") continue;
                    }
                    else
                    {
                        if (FileService.SkipDirectories.Contains(dirName)) continue;
                    }

                    entries.Add(new FileTreeEntry
                    {
                        Name = dirName,
                        FullPath = dir,
                        IsDirectory = true,
                        GitStatus = gitStatusMap is not null
                            && gitStatusMap.TryGetValue(Path.GetRelativePath(repoRoot!, dir).Replace('\\', '/'), out var badge)
                            ? badge
                            : null
                    });
                }

                foreach (var file in Directory.EnumerateFiles(fullPath))
                {
                    var fileName = Path.GetFileName(file);

                    if (isGitRepo && gitFiles is not null)
                    {
                        var relativePath = Path.GetRelativePath(repoRoot!, file).Replace('\\', '/');
                        if (!gitFiles.Contains(relativePath)) continue;
                    }

                    try
                    {
                        var fileInfo = FileService.GetFileInfo(file);
                        if (!fileInfo.Exists || fileInfo.IsDirectory)
                        {
                            continue;
                        }

                        entries.Add(new FileTreeEntry
                        {
                            Name = fileName,
                            FullPath = file,
                            IsDirectory = false,
                            Size = fileInfo.Size,
                            MimeType = fileInfo.MimeType,
                            IsText = fileInfo.IsText,
                            GitStatus = gitStatusMap is not null
                                && gitStatusMap.TryGetValue(Path.GetRelativePath(repoRoot!, file).Replace('\\', '/'), out var badge)
                                ? badge
                                : null
                        });
                    }
                    catch { }
                }

                entries = entries
                    .OrderByDescending(e => e.IsDirectory)
                    .ThenBy(e => e.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            catch (UnauthorizedAccessException)
            {
                return Results.StatusCode(403);
            }

            var response = new FileTreeResponse
            {
                Path = fullPath,
                Entries = entries.ToArray(),
                IsGitRepo = isGitRepo
            };

            return Results.Json(response, AppJsonContext.Default.FileTreeResponse);
        });
    }

    private static async Task<IResult> ServeFileAsync(
        string path,
        bool inline,
        string? sessionId,
        FileService fileService)
    {
        if (!FileService.ValidatePath(path, out var errorResult))
        {
            return errorResult!;
        }

        var workingDir = await fileService.GetSessionWorkingDirectoryAsync(sessionId);
        if (!string.IsNullOrEmpty(sessionId) &&
            !fileService.IsPathAccessible(sessionId, path, workingDir))
        {
            return Results.StatusCode(403);
        }

        var fullPath = Path.GetFullPath(path);

        if (!File.Exists(fullPath))
        {
            return Results.NotFound("File not found");
        }

        try
        {
            var fileInfo = new FileInfo(fullPath);
            var mimeType = FileService.GetMimeType(fileInfo.Name);
            var fileName = fileInfo.Name;

            return Results.File(
                fullPath,
                mimeType,
                fileDownloadName: inline ? null : fileName,
                enableRangeProcessing: true);
        }
        catch (UnauthorizedAccessException)
        {
            return Results.StatusCode(403);
        }
        catch (IOException ex)
        {
            return Results.Problem(ex.Message);
        }
    }

    private static IEnumerable<LauncherDirectoryEntry> GetLauncherRootEntries()
    {
        if (OperatingSystem.IsWindows())
        {
            return DriveInfo.GetDrives()
                .Select(static drive => new LauncherDirectoryEntry
                {
                    Name = drive.Name,
                    FullPath = drive.RootDirectory.FullName,
                    IsRoot = true
                })
                .OrderBy(static entry => entry.FullPath, StringComparer.OrdinalIgnoreCase);
        }

        return new[]
        {
            new LauncherDirectoryEntry
            {
                Name = "/",
                FullPath = "/",
                IsRoot = true
            }
        };
    }

    private static bool TryNormalizeExistingDirectory(
        string path,
        out string fullPath,
        out IResult? errorResult)
    {
        fullPath = string.Empty;
        errorResult = null;

        path = NormalizeLauncherPath(path);
        if (string.IsNullOrWhiteSpace(path))
        {
            errorResult = Results.BadRequest("Path is required");
            return false;
        }

        try
        {
            fullPath = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            errorResult = Results.BadRequest("Invalid path");
            return false;
        }

        if (!Directory.Exists(fullPath))
        {
            errorResult = Results.NotFound("Directory not found");
            return false;
        }

        return true;
    }

    internal static bool IsLauncherDirectoryWritable(string path)
    {
        try
        {
            var probePath = Path.Combine(path, $".tlbx-write-test-{Guid.NewGuid():N}.tmp");
            using (var stream = new FileStream(
                       probePath,
                       new FileStreamOptions
                       {
                           Mode = FileMode.CreateNew,
                           Access = FileAccess.Write,
                           Share = FileShare.None,
                           Options = FileOptions.DeleteOnClose
                       }))
            {
                stream.WriteByte(0);
            }

            if (File.Exists(probePath))
            {
                File.Delete(probePath);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    internal static bool TryValidateLauncherDirectoryName(
        string? name,
        out string normalizedName,
        out string error)
    {
        normalizedName = (name ?? string.Empty).Trim();
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(normalizedName))
        {
            error = "Folder name is required";
            return false;
        }

        if (normalizedName is "." or ".." ||
            Path.IsPathRooted(normalizedName) ||
            normalizedName.Contains(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal) ||
            normalizedName.Contains(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal))
        {
            error = "Use a single folder name without path separators";
            return false;
        }

        if (normalizedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            error = "Folder name contains invalid characters";
            return false;
        }

        return true;
    }

    internal static bool TryResolveCloneDirectoryName(string? repositoryUrl, out string directoryName)
    {
        directoryName = string.Empty;
        var trimmed = (repositoryUrl ?? string.Empty).Trim().TrimEnd('/', '\\');
        if (string.IsNullOrWhiteSpace(trimmed))
        {
            return false;
        }

        string candidate;
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri))
        {
            candidate = Path.GetFileName(uri.AbsolutePath.TrimEnd('/'));
            if (string.IsNullOrWhiteSpace(candidate))
            {
                return false;
            }
        }
        else
        {
            var separatorIndex = Math.Max(
                trimmed.LastIndexOf('/'),
                trimmed.LastIndexOf(':'));
            candidate = separatorIndex >= 0 ? trimmed[(separatorIndex + 1)..] : trimmed;
        }

        if (candidate.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
        {
            candidate = candidate[..^4];
        }

        return TryValidateLauncherDirectoryName(candidate, out directoryName, out _);
    }

    private static string NormalizeLauncherPath(string path)
    {
        var trimmed = path.Trim();
        if (trimmed.Length >= 2 &&
            ((trimmed[0] == '"' && trimmed[^1] == '"') ||
             (trimmed[0] == '\'' && trimmed[^1] == '\'')))
        {
            trimmed = trimmed[1..^1];
        }

        return Environment.ExpandEnvironmentVariables(trimmed);
    }
}

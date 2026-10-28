using Ai.Tlbx.MidTerm.Models.Files;
using Ai.Tlbx.MidTerm.Services.Git;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Settings;
using System.Threading.Channels;

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
        var fileService = new FileService(sessionManager, allowlistService, gitWatcher);
        var documents = new FileDocumentService();
        FileOperationEndpoints.Map(app, fileService, settingsService);
        var readers = app.MapGroup("/api/files").AddEndpointFilter(new FileSessionIdentityFilter(fileService, settingsService));

        app.MapGet("/api/files/events", async (HttpContext context, string path, string sessionId) =>
        {
            var (resolved, error) = await ResolveFileAccessAsync(path, sessionId, fileService);
            if (error is not null) { await error.ExecuteAsync(context); return; }
            if (!Directory.Exists(resolved)) { context.Response.StatusCode = 404; return; }
            var ct = context.RequestAborted;
            var changes = Channel.CreateBounded<bool>(new BoundedChannelOptions(1)
            { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = false });
            using var watcher = new FileSystemWatcher(resolved!)
            { IncludeSubdirectories = true, NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size };
            FileSystemEventHandler changed = (_, _) => changes.Writer.TryWrite(true);
            RenamedEventHandler renamed = (_, _) => changes.Writer.TryWrite(true);
            ErrorEventHandler overflow = (_, _) => changes.Writer.TryWrite(true);
            watcher.Changed += changed; watcher.Created += changed; watcher.Deleted += changed;
            watcher.Renamed += renamed; watcher.Error += overflow;
            watcher.EnableRaisingEvents = true;
            context.Response.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            try
            {
                await context.Response.WriteAsync("event: ready\ndata: 1\n\n", ct); await context.Response.Body.FlushAsync(ct);
                while (await changes.Reader.WaitToReadAsync(ct))
                {
                    await Task.Delay(180, ct);
                    while (changes.Reader.TryRead(out _)) { }
                    await context.Response.WriteAsync("event: changed\ndata: 1\n\n", ct); await context.Response.Body.FlushAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        });

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

        readers.MapPost("/register", (FileRegisterRequest request) =>
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

        readers.MapPost("/check", async (FileCheckRequest request, string? sessionId) =>
        {
            var results = new Dictionary<string, FilePathInfo>(StringComparer.Ordinal);
            foreach (var path in request.Paths)
            {
                var (resolved, accessError) = await ResolveFileAccessAsync(path, sessionId, fileService);
                if (accessError is not null)
                {
                    results[path] = new FilePathInfo { Exists = false };
                    continue;
                }

                results[path] = FileService.GetFileInfo(resolved!);
            }

            return Results.Json(
                new FileCheckResponse { Results = results },
                AppJsonContext.Default.FileCheckResponse);
        });

        readers.MapGet("/navigation", async (string path, string? sessionId, bool includeDetectedRoots = true) =>
        {
            var (_, error) = await ResolveFileAccessAsync(path, sessionId, fileService);
            if (error is not null) return error;
            var cwd = await fileService.GetSessionWorkingDirectoryAsync(sessionId);
            try
            {
                var navigation = FileNavigationService.Describe(path, candidate =>
                    string.IsNullOrEmpty(sessionId) || fileService.IsPathAccessible(sessionId, candidate, cwd));
                navigation.WorkingDirectory = cwd;
                if (!string.IsNullOrEmpty(sessionId)) navigation.Roots = fileService.GetNavigationRoots(sessionId, cwd, includeDetectedRoots);
                return Results.Json(navigation, AppJsonContext.Default.FileNavigationResponse);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return DocumentError(ex, sessionId, fileService); }
        }).Produces<FileNavigationResponse>();

        readers.MapGet("/list", async (string path, string? sessionId, bool showHidden = false, bool showIgnored = true) =>
        {
            var (resolved, error) = await ResolveFileAccessAsync(path, sessionId, fileService);
            if (error is not null) return error;
            var fullPath = resolved!;

            if (!Directory.Exists(fullPath))
            {
                return Results.NotFound("Directory not found");
            }

            try
            {
                var listing = await FileDirectoryService.ReadAsync(fullPath, showHidden, showIgnored, async candidate =>
                {
                    var (accessible, candidateError) = await ResolveFileAccessAsync(candidate, sessionId, fileService);
                    return candidateError is null ? accessible : null;
                });
                var entries = listing.Entries.Select(entry => new DirectoryEntry
                { Name = entry.Name, IsDirectory = entry.IsDirectory, Size = entry.Size, Modified = entry.Modified,
                    MimeType = entry.MimeType, IsHidden = entry.IsHidden, IsIgnored = entry.IsIgnored }).ToArray();

                if (!string.IsNullOrEmpty(sessionId)) fileService.RegisterPaths(sessionId, [Path.GetFullPath(path), fullPath]);

                return Results.Json(
                    new DirectoryListResponse { Path = Path.GetFullPath(path), Entries = entries, Visibility = listing.Visibility },
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

        readers.MapGet("/document", async (string path, string? sessionId, bool full = false, long offset = 0, CancellationToken ct = default) =>
        {
            var (resolved, error) = await ResolveFileAccessAsync(path, sessionId, fileService);
            if (error is not null) return error;
            try
            {
                var document = await documents.ReadAsync(resolved!, full, ct, offset);
                document.Path = path;
                if (sessionId is not null) { fileService.RegisterPath(sessionId, path); fileService.RegisterPath(sessionId, resolved!); }
                return Results.Json(document, AppJsonContext.Default.FileDocumentResponse);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return DocumentError(ex, sessionId, fileService); }
        }).Produces<FileDocumentResponse>().Produces<FileDocumentErrorResponse>(409);

        readers.MapPut("/save", async (FileSaveRequest request, string? sessionId, CancellationToken ct) =>
        {
            var (resolved, error) = await ResolveFileAccessAsync(request.Path, sessionId, fileService);
            if (error is not null) return error;
            try
            {
                var saved = await documents.SaveAsync(resolved!, request.Content, request.ExpectedVersion, ct);
                return Results.Json(saved, AppJsonContext.Default.FileSaveResponse);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            { return DocumentError(ex, sessionId, fileService); }
        }).WithMetadata(new Microsoft.AspNetCore.Mvc.RequestSizeLimitAttribute(64 * 1024 * 1024))
            .Produces<FileSaveResponse>().Produces<FileDocumentErrorResponse>(409);

        readers.MapGet("/view", async (HttpContext context, string path, string? sessionId) =>
        {
            return await ServeFileAsync(context, path, inline: true, sessionId, fileService);
        });

        readers.MapGet("/download", async (HttpContext context, string path, string? sessionId) =>
        {
            return await ServeFileAsync(context, path, inline: false, sessionId, fileService);
        });

        readers.MapGet("/resolve", async (string sessionId, string path, bool deep = false, CancellationToken ct = default) =>
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
                if (File.Exists(exactPath) || Directory.Exists(exactPath))
                {
                    var (accessiblePath, accessError) = await ResolveFileAccessAsync(exactPath, sessionId, fileService);
                    if (accessError is not null) continue;
                    fileService.RegisterPath(sessionId, exactPath);
                    return Results.Json(FileService.BuildResolveResponse(exactPath, accessiblePath), AppJsonContext.Default.FileResolveResponse);
                }
            }

            if (deep)
            {
                foreach (var tryPath in FileService.GetSlashVariants(path))
                {
                    var found = FileService.SearchTree(cwd, tryPath, maxDepth: 5);
                    if (found is not null)
                    {
                        var (accessiblePath, accessError) = await ResolveFileAccessAsync(found, sessionId, fileService);
                        if (accessError is not null) continue;
                        fileService.RegisterPath(sessionId, found);
                        return Results.Json(FileService.BuildResolveResponse(found, accessiblePath), AppJsonContext.Default.FileResolveResponse);
                    }
                }
            }

            return Results.Json(new FileResolveResponse { Exists = false }, AppJsonContext.Default.FileResolveResponse);
        });

        readers.MapGet("/tree", async (string path, string? sessionId, int depth, bool showHidden = false, bool showIgnored = true) =>
        {
            var (resolved, error) = await ResolveFileAccessAsync(path, sessionId, fileService);
            if (error is not null) return error;
            var fullPath = resolved!;
            if (!Directory.Exists(fullPath))
            {
                return Results.NotFound("Directory not found");
            }

            var isGitRepo = false;
            string? repoRoot = null;
            Dictionary<string, string>? gitStatusMap = null;

            try
            {
                repoRoot = await GitCommandRunner.GetRepoRootAsync(fullPath);
                if (!string.IsNullOrEmpty(repoRoot))
                {
                    isGitRepo = true;
                    if (!string.IsNullOrEmpty(sessionId))
                    {
                        var cachedStatus = gitWatcher.GetCachedStatus(sessionId, repoRoot);
                        // The Git watcher owns status refreshes. Folder names must not wait
                        // for its refresh gate, history, stash count or diff statistics.
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

            (FileTreeEntry[] Entries, FileVisibilitySummary Visibility) listing;

            try
            {
                listing = await FileDirectoryService.ReadAsync(fullPath, showHidden, showIgnored, async candidate =>
                {
                    var (accessible, candidateError) = await ResolveFileAccessAsync(candidate, sessionId, fileService);
                    return candidateError is null ? accessible : null;
                });
                foreach (var entry in listing.Entries)
                    if (gitStatusMap is not null && repoRoot is not null &&
                        gitStatusMap.TryGetValue(Path.GetRelativePath(repoRoot, entry.FullPath).Replace('\\', '/'), out var badge))
                        entry.GitStatus = badge;
            }
            catch (UnauthorizedAccessException)
            {
                return Results.StatusCode(403);
            }

            var response = new FileTreeResponse
            {
                Path = fullPath,
                Entries = listing.Entries,
                IsGitRepo = isGitRepo,
                Visibility = listing.Visibility
            };
            if (!string.IsNullOrEmpty(sessionId)) fileService.RegisterPaths(sessionId, [Path.GetFullPath(path), fullPath]);

            return Results.Json(response, AppJsonContext.Default.FileTreeResponse);
        });
    }

    private static async Task<(string? Path, IResult? Error)> ResolveFileAccessAsync(string path, string? sessionId, FileService files)
    {
        if (!FileService.ValidatePath(path, out var error)) return (null, error);
        var cwd = await files.GetSessionWorkingDirectoryAsync(sessionId);
        if (!string.IsNullOrEmpty(sessionId) && !files.IsSessionValid(sessionId))
            return (null, DocumentError(new FileDocumentException(404, "session_closed", "The file's session is no longer available."), sessionId, files));
        if (!string.IsNullOrEmpty(sessionId) && !files.IsPathAccessible(sessionId, path, cwd))
            return (null, DocumentError(new UnauthorizedAccessException(), sessionId, files));
        try
        {
            var resolved = FileDocumentService.ResolvePath(path);
            if (!string.IsNullOrEmpty(sessionId) && !files.IsPathAccessible(sessionId, resolved, cwd))
                return (null, DocumentError(new UnauthorizedAccessException(), sessionId, files));
            return (resolved, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { return (null, DocumentError(ex, sessionId, files)); }
    }

    internal static IResult DocumentError(Exception exception, string? sessionId, FileService files)
    {
        var status = 500;
        var response = new FileDocumentErrorResponse { Code = "io_error", Message = "The file operation failed. Retry or keep a copy of your draft." };
        switch (exception)
        {
            case FileDocumentException document:
                status = document.StatusCode;
                response.Code = document.Code;
                response.Message = document.Message;
                response.CurrentVersion = document.CurrentVersion;
                response.RecoveryPath = document.RecoveryPath;
                if (sessionId is not null && document.RecoveryPath is not null) files.RegisterPath(sessionId, document.RecoveryPath);
                break;
            case FileNotFoundException or DirectoryNotFoundException:
                status = 404; response.Code = "deleted"; response.Message = "The file no longer exists. Your unsaved draft has not been written.";
                break;
            case UnauthorizedAccessException:
                status = 403; response.Code = "access_denied"; response.Message = "You do not have permission to write or read this file.";
                break;
            case ArgumentException:
                status = 400; response.Code = "invalid_path"; response.Message = "The supplied path or name is invalid.";
                break;
            case SessionCallerIdentityException:
                status = 403; response.Code = "session_identity"; response.Message = "The session owner could not be verified. No operation will be retried automatically.";
                break;
            case System.ComponentModel.Win32Exception native when native.NativeErrorCode == 5:
                status = 403; response.Code = "access_denied"; response.Message = "You do not have permission to modify this entry.";
                break;
            case IOException io when (io.HResult & 0xFFFF) is 32 or 33:
                status = 423; response.Code = "busy"; response.Message = "Another process is using the file. Retry when it is available.";
                break;
            case IOException io when (io.HResult & 0xFFFF) is 39 or 112:
                status = 507; response.Code = "disk_full"; response.Message = "There is not enough free space to save the file.";
                break;
        }
        return Results.Json(response, AppJsonContext.Default.FileDocumentErrorResponse, statusCode: status);
    }

    private static async Task<IResult> ServeFileAsync(
        HttpContext context,
        string path,
        bool inline,
        string? sessionId,
        FileService fileService)
    {
        var (resolved, error) = await ResolveFileAccessAsync(path, sessionId, fileService);
        if (error is not null) return error;
        var fullPath = resolved!;

        if (!File.Exists(fullPath))
        {
            return Results.NotFound("File not found");
        }

        try
        {
            return CreateFileResponse(context, fullPath, Path.GetFileName(path), inline);
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

    internal static IResult CreateFileResponse(HttpContext context, string fullPath, string fileName, bool inline)
    {
        var mimeType = FileService.GetMimeType(fileName);
        if (inline && mimeType is "text/html" or "application/xhtml+xml" or "image/svg+xml"
            or "text/xml" or "application/xml")
        {
            // User files are documents, never trusted application pages. Sandboxing
            // blocks scripts and gives an embedded active document an opaque origin.
            context.Response.Headers.ContentSecurityPolicy = "sandbox";
            context.Response.Headers.XContentTypeOptions = "nosniff";
        }

        return Results.File(fullPath, mimeType,
            fileDownloadName: inline ? null : fileName, enableRangeProcessing: true);
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

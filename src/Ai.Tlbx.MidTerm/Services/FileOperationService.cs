using System.ComponentModel;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Ai.Tlbx.MidTerm.Models.Files;
using Ai.Tlbx.MidTerm.Services.Git;

namespace Ai.Tlbx.MidTerm.Services;

/// <summary>Real filesystem mutations with durable receipts and recoverable deletion.</summary>
public sealed class FileOperationService(string storageDirectory)
{
    private readonly string _storageBase = Path.GetFullPath(Path.Combine(storageDirectory, "file-recovery"));
    private readonly string _storage = Path.GetFullPath(Path.Combine(storageDirectory, "file-recovery"));
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private static readonly StringComparer Paths = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
    private static readonly HashSet<string> Actions = new(StringComparer.Ordinal) { "createFile", "createFolder", "rename", "move", "copy", "duplicate", "delete" };
    private readonly Dictionary<string, string?> _gitStageDirectories = new(Paths);
    private readonly Dictionary<string, string> _gitFallbackDirectories = new(Paths);
    internal void GrantStorageOwner(string sid) => FileRecoveryStorage.EnsurePrivate(_storage, sid);
    private FileOperationService(string directory, string owner) : this(directory)
    {
        if (!owner.StartsWith("S-", StringComparison.Ordinal) || owner.Any(character => character is not '-' and not 'S' && !char.IsAsciiDigit(character)))
            throw new ArgumentException("Invalid recovery owner.", nameof(owner));
        _storage = Path.Combine(_storageBase, "owners", owner);
    }
    internal FileOperationService ForOwner(string sid) => new(storageDirectory, sid);

    public async Task<FileOperationResponse> ExecuteAsync(FileOperationRequest request, Func<string, bool> accessible,
        IEnumerable<string> protectedRoots, CancellationToken ct)
    {
        ValidateRequest(request);
        var id = Guid.Parse(request.RequestId).ToString("N");
        var requestJson = JsonSerializer.Serialize(request, FileOperationJsonContext.Default.FileOperationRequest);
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(requestJson)));
        await Gate.WaitAsync(ct);
        try
        {
            FileRecoveryStorage.EnsurePrivate(_storage);
            _gitStageDirectories.Clear();
            _gitFallbackDirectories.Clear();
            var receiptPath = Path.Combine(_storage, "operations", id + ".json");
            if (File.Exists(receiptPath))
            {
                var old = Read<OperationReceipt>(receiptPath, FileOperationJsonContext.Default.OperationReceipt);
                if (old.Digest != digest) throw Failure(409, "request_reused", "This request ID belongs to another operation.");
                RequireReceiptAuthority(old, accessible);
                return old.Response;
            }
            var roots = protectedRoots.Select(Path.GetFullPath).Select(FileDocumentService.ResolvePath).ToHashSet(Paths);
            var sources = request.Paths.Select(path => RequireEntry(path, accessible, roots, request.Action != "copy" && request.Action != "duplicate")).Distinct(Paths).ToArray();
            var logicalSources = request.Paths.GroupBy(path => RequireEntry(path, accessible, roots, false), Paths)
                .ToDictionary(group => group.Key, group => Path.GetFullPath(group.First()), Paths);
            var destination = request.DestinationDirectory is null ? null : RequireDirectory(request.DestinationDirectory, accessible);
            foreach (var source in sources) RequireOutsideRecovery(source, includesChildren: !FileEntryLinks.IsLink(source));
            if (destination is not null) RequireOutsideRecovery(destination, includesChildren: false);
            if (request.Action is "createFile" or "createFolder" or "copy" or "move" && destination is null)
                throw Failure(400, "destination_required", "Select a destination directory.");
            var response = new FileOperationResponse { OperationId = id, Action = request.Action };
            var authorityPaths = sources.ToList();
            if (destination is not null) authorityPaths.Add(Path.Combine(destination, request.Name ?? "operation-receipt"));
            var receipt = new OperationReceipt { Digest = digest, AuthorityPaths = authorityPaths.ToArray(), Response = response };
            Write(receiptPath, receipt, FileOperationJsonContext.Default.OperationReceipt);
            var results = new List<FileOperationItem>();
            var coveringParents = CoveringParents(sources);
            var topLevel = sources.Where(path => !coveringParents.ContainsKey(path)).ToArray();
            var completed = new Dictionary<string, FileOperationItem>(Paths);
            foreach (var source in request.Action is "createFile" or "createFolder" ? new string?[] { null } : topLevel)
            {
                FileOperationItem item;
                try
                {
                    ct.ThrowIfCancellationRequested();
                    item = await ApplyAsync(request, source, destination, accessible, roots, ct);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FileDocumentException or Win32Exception or OperationCanceledException)
                { item = ErrorItem(source, ex); }
                if (source is not null) item.SourcePath = logicalSources[source];
                if (item.Path is not null)
                {
                    var logicalParent = request.DestinationDirectory ?? Path.GetDirectoryName(logicalSources[source!])!;
                    item.Path = Path.Combine(Path.GetFullPath(logicalParent), Path.GetFileName(item.Path));
                }
                results.Add(item);
                if (source is not null) completed[source] = item;
                response.Items = results.ToArray(); Write(receiptPath, receipt, FileOperationJsonContext.Default.OperationReceipt);
            }
            foreach (var source in sources.Except(topLevel, Paths))
            {
                var parent = coveringParents[source];
                var item = completed[parent];
                results.Add(new FileOperationItem { SourcePath = logicalSources[source], Success = item.Success, Code = item.Success ? "included_in_folder" : item.Code,
                    Message = item.Message, Path = item.Path is null ? null : Path.Combine(item.Path, Path.GetRelativePath(parent, source)), RecoveryId = item.RecoveryId });
            }
            response.Items = results.ToArray(); response.Complete = true;
            Write(receiptPath, receipt, FileOperationJsonContext.Default.OperationReceipt);
            return response;
        }
        finally { Gate.Release(); }
    }

    private static Dictionary<string, string> CoveringParents(string[] sources)
    {
        var directories = sources.Where(path => {
            var attributes = File.GetAttributes(path);
            return (attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint)) == FileAttributes.Directory;
        }).ToHashSet(Paths);
        var covered = new Dictionary<string, string>(Paths);
        foreach (var source in sources)
        {
            var parent = Path.GetDirectoryName(source);
            while (parent is not null)
            {
                if (directories.Contains(parent)) covered[source] = parent;
                parent = Path.GetDirectoryName(parent);
            }
        }
        return covered;
    }

    private async Task<FileOperationItem> ApplyAsync(FileOperationRequest request, string? source, string? directory,
        Func<string, bool> accessible, HashSet<string> roots, CancellationToken ct)
    {
        if (source is not null) RequireEntry(source, accessible, roots, request.Action is not ("copy" or "duplicate"));
        if (request.Action == "delete")
        {
            var recovery = await RecoverAsync(source!, "delete", accessible, ct);
            return new() { SourcePath = source, Success = true, RecoveryId = recovery.Id };
        }
        var parent = directory ?? Path.GetDirectoryName(source!)!;
        parent = RequireDirectory(parent, accessible);
        var name = request.Name ?? Path.GetFileName(source!);
        ValidateName(name);
        var target = Path.Combine(parent, name);
        var conflict = request.Action == "duplicate" ? "keepBoth" : request.Conflict;
        var exactTarget = source is not null && (StringComparer.Ordinal.Equals(source, target) ||
            (Paths.Equals(source, target) || request.Action == "rename" && Paths.Equals(Path.GetDirectoryName(source), parent)) &&
            FileEntryLinks.Exists(target) && Directory.EnumerateFileSystemEntries(parent).Any(entry => StringComparer.Ordinal.Equals(entry, target)));
        var caseOnly = source is not null && request.Action == "rename" && !StringComparer.Ordinal.Equals(source, target) &&
            Paths.Equals(Path.GetDirectoryName(source), parent) && FileEntryLinks.Exists(target) && !exactTarget;
        if (conflict == "keepBoth") target = UniqueName(target);
        else if (!caseOnly && source is not null && Paths.Equals(source, target) &&
            (StringComparer.Ordinal.Equals(source, target) || !exactTarget)) throw Failure(409, "same_path", "Source and destination are the same entry.");
        if (source is not null && FileEntryLinks.IsDirectory(source) && !FileEntryLinks.IsLink(source) && FileService.IsWithinDirectory(target, source))
            throw Failure(400, "recursive_destination", "A folder cannot be copied or moved inside itself.");
        if (source is not null && FileEntryLinks.Exists(target) && FileEntryLinks.IsDirectory(target) && !FileEntryLinks.IsLink(target) && FileService.IsWithinDirectory(source, target))
            throw Failure(400, "recursive_destination", "A folder containing the source cannot be replaced by that source.");
        RequireParent(target, accessible);
        RequireOutsideRecovery(target, includesChildren: false);
        if (roots.Contains(target)) throw Failure(403, "protected_root", "A session or repository root cannot be replaced.");
        RecoveryRecord? replaced = null;
        if (FileEntryLinks.Exists(target) && !caseOnly)
        {
            if (conflict != "replace") throw Failure(409, "name_conflict", "A file or folder with that name already exists.");
            replaced = await RecoverAsync(target, "replace", accessible, ct);
        }
        try
        {
            if (request.Action is "createFile" or "createFolder") await CreateAsync(target, request.Action == "createFolder", ct);
            else if (request.Action is "copy" or "duplicate") await CopyPublishedAsync(source!, target, ct);
            else if (caseOnly) RenameCase(source!, target);
            else await MoveAsync(source!, target, accessible, ct);
            return new() { SourcePath = source, Path = target, Success = true, RecoveryId = replaced?.Id };
        }
        catch
        {
            if (replaced is not null && !FileEntryLinks.Exists(target))
                await RestoreStoredAsync(replaced, target, CancellationToken.None);
            throw;
        }
    }

    private async Task<RecoveryRecord> RecoverAsync(string original, string kind, Func<string, bool> accessible, CancellationToken ct)
    {
        FileEntryCopy.RequireDeletable(original);
        var id = Guid.CreateVersion7().ToString("N");
        var record = new RecoveryRecord { Id = id, OriginalPath = original, Kind = kind, CreatedAt = DateTimeOffset.UtcNow,
            IsDirectory = FileEntryLinks.IsDirectory(original), IsLink = FileEntryLinks.IsLink(original),
            OriginalAttributes = File.GetAttributes(original),
            ParentMetadata = FileDirectoryMetadata.CaptureParents(original, accessible),
            StagePath = StageName(Path.GetDirectoryName(original)!), StoredPath = Path.Combine(_storage, "entries", id, "content") };
        var recordPath = RecoveryPath(id);
        Write(recordPath, record, FileOperationJsonContext.Default.RecoveryRecord);
        Directory.CreateDirectory(Path.GetDirectoryName(record.StoredPath)!);
        try
        {
            // A rename on the same volume preserves ownership, ACLs, links and all file streams.
            record.StagePath = record.StoredPath;
            Write(recordPath, record, FileOperationJsonContext.Default.RecoveryRecord);
            MoveEntry(original, record.StoredPath);
            record.Fingerprint = await FileEntryCopy.FingerprintAsync(record.StoredPath, RecoveryContainers(), ct);
            record.State = "stored"; Write(recordPath, record, FileOperationJsonContext.Default.RecoveryRecord);
            return record;
        }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 17 or 18)
        {
            // Keep the original filesystem object on its own volume when central storage differs.
            var sourceParent = Path.GetDirectoryName(original)!;
            var repository = await GitCommandRunner.GetRepositoryInfoAsync(sourceParent);
            var recoveryParent = repository?.GitDir ?? sourceParent;
            var fallbackParent = repository is null ? sourceParent : Path.GetDirectoryName(repository.RepoRoot) ?? sourceParent;
            if (FileService.IsWithinDirectory(recoveryParent, original)) recoveryParent = sourceParent;
            var container = Path.Combine(recoveryParent, ".tlbx-recovery-" + id);
            FileRecoveryStorage.EnsurePrivate(container);
            if (OperatingSystem.IsWindows()) File.SetAttributes(container, File.GetAttributes(container) | FileAttributes.Hidden);
            record.StoredPath = Path.Combine(container, "content"); record.StagePath = record.StoredPath;
            Write(recordPath, record, FileOperationJsonContext.Default.RecoveryRecord);
            try
            {
                try { MoveEntry(original, record.StoredPath); }
                catch (IOException moveError) when ((moveError.HResult & 0xFFFF) is 17 or 18 && !Paths.Equals(recoveryParent, fallbackParent))
                {
                    // External worktree metadata can reside on another filesystem too.
                    RemoveEmptyRecoveryContainer(record.StoredPath);
                    container = Path.Combine(fallbackParent, ".tlbx-recovery-" + id);
                    FileRecoveryStorage.EnsurePrivate(container);
                    if (OperatingSystem.IsWindows()) File.SetAttributes(container, File.GetAttributes(container) | FileAttributes.Hidden);
                    record.StoredPath = Path.Combine(container, "content"); record.StagePath = record.StoredPath;
                    Write(recordPath, record, FileOperationJsonContext.Default.RecoveryRecord);
                    MoveEntry(original, record.StoredPath);
                }
                record.Fingerprint = await FileEntryCopy.FingerprintAsync(record.StoredPath, RecoveryContainers(), ct);
                record.State = "stored"; Write(recordPath, record, FileOperationJsonContext.Default.RecoveryRecord);
                return record;
            }
            catch
            {
                if (!FileEntryLinks.Exists(original) && FileEntryLinks.Exists(record.StoredPath)) MoveEntry(record.StoredPath, original);
                throw;
            }
        }
        catch
        {
            if (!FileEntryLinks.Exists(original) && FileEntryLinks.Exists(record.StoredPath)) MoveEntry(record.StoredPath, original);
            throw;
        }
    }

    public async Task<FileOperationItem> RestoreAsync(FileRestoreRequest request, Func<string, bool> accessible, CancellationToken ct)
    {
        var id = ParseId(request.RecoveryId);
        if (request.Conflict is not ("fail" or "keepBoth")) throw Failure(400, "invalid_conflict", "Restore supports cancel or keeping both entries.");
        await Gate.WaitAsync(ct);
        try
        {
            var record = Read<RecoveryRecord>(RecoveryPath(id), FileOperationJsonContext.Default.RecoveryRecord);
            RequireRecoveryParent(record.OriginalPath, accessible, create: false);
            if (record.State == "restored") return new() { Success = true, Path = record.RestoredPath, RecoveryId = id };
            var target = request.Conflict == "keepBoth" ? UniqueName(record.OriginalPath) : record.OriginalPath;
            if (FileEntryLinks.Exists(target)) throw Failure(409, "name_conflict", "The original name is occupied. Keep both to restore under another name.");
            RequireRecoveryParent(target, accessible, create: true, record.ParentMetadata);
            var oldPayload = RecoveryPayload(record);
            if (record.State == "stored") await RestoreStoredAsync(record, target, ct);
            else if (FileEntryLinks.Exists(RecoveryPayload(record)))
            {
                MoveEntry(RecoveryPayload(record), target);
                if (!record.IsLink) File.SetAttributes(target, record.OriginalAttributes);
            }
            else throw Failure(404, "recovery_missing", "This recovery entry is no longer available.");
            record.State = "restored"; record.RestoredPath = target;
            Write(RecoveryPath(id), record, FileOperationJsonContext.Default.RecoveryRecord);
            RemoveEmptyRecoveryContainer(oldPayload);
            return new() { Success = true, Path = target, RecoveryId = id };
        }
        finally { Gate.Release(); }
    }

    public FileOperationResponse GetOperation(string id, Func<string, bool> accessible)
    {
        var receipt = Read<OperationReceipt>(Path.Combine(_storage, "operations", ParseId(id) + ".json"), FileOperationJsonContext.Default.OperationReceipt);
        RequireReceiptAuthority(receipt, accessible); return receipt.Response;
    }

    private static void RequireReceiptAuthority(OperationReceipt receipt, Func<string, bool> accessible)
    {
        foreach (var path in receipt.AuthorityPaths.Concat(receipt.Response.Items.Select(item => item.Path).OfType<string>()))
        {
            var parent = Path.GetDirectoryName(path) ?? throw new UnauthorizedAccessException();
            if (!accessible(path) || !accessible(FileDocumentService.ResolvePath(parent))) throw new UnauthorizedAccessException();
        }
    }

    public FileRecoveryResponse ListRecovery(string directory, string? cursor, Func<string, bool> accessible)
    {
        var logicalDirectory = Path.GetFullPath(directory);
        directory = RequireDirectory(directory, accessible);
        var records = Path.Combine(_storage, "records");
        if (!Directory.Exists(records)) return new();
        var entries = Directory.EnumerateFiles(records, "*.json")
            .Select(path => Read<RecoveryRecord>(path, FileOperationJsonContext.Default.RecoveryRecord))
            .Where(record => record.State != "restored" && FileService.IsWithinDirectory(record.OriginalPath, directory) &&
                CanRestore(record, accessible) && (cursor is null || StringComparer.Ordinal.Compare(record.Id, cursor) < 0))
            .OrderByDescending(record => record.Id, StringComparer.Ordinal).Take(51).ToArray();
        return new() { Entries = entries.Take(50).Select(record => new FileRecoveryEntry { Id = record.Id, OriginalPath = Path.Combine(logicalDirectory, Path.GetRelativePath(directory, record.OriginalPath)),
            Kind = record.Kind, CreatedAt = record.CreatedAt, IsDirectory = record.IsDirectory, IsLink = record.IsLink }).ToArray(),
            NextCursor = entries.Length > 50 ? entries[49].Id : null };
    }

    private static bool CanRestore(RecoveryRecord record, Func<string, bool> accessible)
    {
        try { RequireRecoveryParent(record.OriginalPath, accessible, create: false); return FileEntryLinks.Exists(RecoveryPayload(record)); }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or FileDocumentException) { return false; }
    }

    private async Task RestoreStoredAsync(RecoveryRecord record, string target, CancellationToken ct)
    {
        var payload = RecoveryPayload(record);
        if (record.Fingerprint != await FileEntryCopy.FingerprintAsync(payload, RecoveryContainers(), ct))
            throw Failure(409, "recovery_changed", "The recovery copy has changed. It has been kept for inspection.");
        ct.ThrowIfCancellationRequested(); MoveEntry(payload, target);
    }

    private async Task CopyPublishedAsync(string source, string target, CancellationToken ct, FileAttributes? rootAttributes = null)
    {
        var parent = Path.GetDirectoryName(target)!;
        var temporaryParent = await GitStageDirectoryAsync(parent);
        if (temporaryParent is not null && FileEntryLinks.IsDirectory(source) && !FileEntryLinks.IsLink(source) && FileService.IsWithinDirectory(temporaryParent, source))
            temporaryParent = null;
        try { await CopyPublishedInAsync(source, target, temporaryParent ?? parent, rootAttributes, ct); }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 17 or 18 && temporaryParent is not null && !Paths.Equals(temporaryParent, parent))
        {
            // Publish from the destination volume if worktree metadata is elsewhere.
            await CopyPublishedInAsync(source, target, _gitFallbackDirectories[parent], rootAttributes, ct);
        }
    }

    private async Task CopyPublishedInAsync(string source, string target, string parent, FileAttributes? rootAttributes, CancellationToken ct)
    {
        var container = StageName(parent);
        FileRecoveryStorage.EnsurePrivate(container);
        var stage = Path.Combine(container, "content");
        try
        {
            await FileEntryCopy.CopyAsync(source, stage, RecoveryContainers(), ct);
            if (await FileEntryCopy.FingerprintAsync(source, RecoveryContainers(), ct) != await FileEntryCopy.FingerprintAsync(stage, ct))
                throw Failure(409, "source_changed", "The source changed while it was copied. Nothing was published.");
            ct.ThrowIfCancellationRequested();
            if (rootAttributes is { } attributes && !FileEntryLinks.IsLink(stage)) File.SetAttributes(stage, attributes);
            MoveEntry(stage, target);
        }
        finally
        {
            if (FileEntryLinks.Exists(container)) FileEntryCopy.RemoveOwnedStage(container, clearReadOnly: true);
        }
    }

    private async Task<string?> GitStageDirectoryAsync(string directory)
    {
        if (_gitStageDirectories.TryGetValue(directory, out var existing)) return existing;
        var info = await GitCommandRunner.GetRepositoryInfoAsync(directory);
        var gitDirectory = info?.GitDir;
        _gitStageDirectories[directory] = gitDirectory;
        _gitFallbackDirectories[directory] = info is null ? directory : Path.GetDirectoryName(info.RepoRoot) ?? directory;
        return gitDirectory;
    }

    private static async Task CreateAsync(string target, bool directory, CancellationToken ct)
    {
        var stage = StageName(Path.GetDirectoryName(target)!);
        try
        {
            ct.ThrowIfCancellationRequested();
            if (directory) Directory.CreateDirectory(stage);
            else { await using var file = new FileStream(stage, FileMode.CreateNew, FileAccess.Write, FileShare.None); file.Flush(flushToDisk: true); }
            ct.ThrowIfCancellationRequested(); FileEntryCopy.Move(stage, target);
        }
        finally { if (FileEntryLinks.Exists(stage)) FileEntryCopy.RemoveOwnedStage(stage, clearReadOnly: true); }
    }

    private async Task MoveAsync(string source, string target, Func<string, bool> accessible, CancellationToken ct)
    {
        try { MoveEntry(source, target); return; }
        catch (IOException ex) when ((ex.HResult & 0xFFFF) is 17 or 18) { }
        // Publish on the destination volume, then retain the original object in recovery.
        FileEntryCopy.RequireDeletable(source);
        await CopyPublishedAsync(source, target, ct);
        var recovery = await RecoverAsync(source, "move", accessible, ct);
        if (recovery.Fingerprint == await FileEntryCopy.FingerprintAsync(target, RecoveryContainers(), ct)) return;
        await RestoreStoredAsync(recovery, source, CancellationToken.None);
        throw Failure(409, "source_changed", "The source changed while moving. Its current version is back at the original location; the copied destination was retained.");
    }

    private void RenameCase(string source, string target)
    {
        var stage = StageName(Path.GetDirectoryName(source)!); MoveEntry(source, stage);
        try { MoveEntry(stage, target); }
        catch { MoveEntry(stage, source); throw; }
    }

    private static string RecoveryPayload(RecoveryRecord record) =>
        FileEntryLinks.Exists(record.StoredPath) ? record.StoredPath :
        record.PendingStoredPath is { } pending && FileEntryLinks.Exists(pending) ? pending : record.StagePath;

    private HashSet<string> RecoveryContainers()
    {
        var containers = new HashSet<string>(Paths);
        var records = Path.Combine(_storage, "records");
        if (!Directory.Exists(records)) return containers;
        foreach (var path in Directory.EnumerateFiles(records, "*.json"))
        {
            var record = Read<RecoveryRecord>(path, FileOperationJsonContext.Default.RecoveryRecord);
            var parent = Path.GetDirectoryName(RecoveryPayload(record));
            if (record.State != "restored" && parent is not null && Path.GetFileName(parent) == ".tlbx-recovery-" + record.Id)
                containers.Add(parent);
        }
        return containers;
    }

    private void MoveEntry(string source, string target)
    {
        var records = Path.Combine(_storage, "records");
        var relocated = new List<RecoveryRecord>();
        if (FileEntryLinks.IsDirectory(source) && !FileEntryLinks.IsLink(source) && Directory.Exists(records))
        {
            foreach (var path in Directory.EnumerateFiles(records, "*.json"))
            {
                var record = Read<RecoveryRecord>(path, FileOperationJsonContext.Default.RecoveryRecord);
                var payload = RecoveryPayload(record);
                if (record.State == "restored" || !FileService.IsWithinDirectory(payload, source)) continue;
                // Persist both candidate locations before the rename: either is recoverable after a crash.
                record.StoredPath = payload; record.StagePath = payload;
                record.PendingStoredPath = Path.Combine(target, Path.GetRelativePath(source, payload));
                Write(path, record, FileOperationJsonContext.Default.RecoveryRecord); relocated.Add(record);
            }
        }
        try { FileEntryCopy.Move(source, target); }
        catch
        {
            foreach (var record in relocated)
            {
                record.PendingStoredPath = null;
                Write(RecoveryPath(record.Id), record, FileOperationJsonContext.Default.RecoveryRecord);
            }
            throw;
        }
        // Keep the durable candidate pair. A later move normalizes it from the existing payload;
        // no fallible metadata write should turn a committed rename into an apparent failure.
    }

    private static void RemoveEmptyRecoveryContainer(string payload)
    {
        var parent = Path.GetDirectoryName(payload);
        if (parent is null || !Path.GetFileName(parent).StartsWith(".tlbx-recovery-", StringComparison.Ordinal) ||
            !Guid.TryParseExact(Path.GetFileName(parent)[15..], "N", out _) || !Directory.Exists(parent)) return;
        if (!Directory.EnumerateFileSystemEntries(parent).Any()) Directory.Delete(parent, recursive: false);
    }

    private static string RequireEntry(string path, Func<string, bool> accessible, HashSet<string> roots, bool mutate)
    {
        var physical = RequireParent(path, accessible);
        if (!FileEntryLinks.Exists(physical)) throw new FileNotFoundException("The selected entry no longer exists.");
        if (mutate && roots.Contains(physical)) throw Failure(403, "protected_root", "A session or repository root cannot be renamed, moved or deleted.");
        return physical;
    }

    private static string RequireDirectory(string path, Func<string, bool> accessible)
    {
        if (!Path.IsPathFullyQualified(path)) throw Failure(400, "invalid_path", "An absolute directory path is required.");
        var logical = Path.GetFullPath(path); var physical = FileDocumentService.ResolvePath(logical);
        if (!accessible(logical) || !accessible(physical)) throw new UnauthorizedAccessException();
        if (!Directory.Exists(physical)) throw new DirectoryNotFoundException("The destination directory no longer exists.");
        return physical;
    }

    private static string RequireParent(string path, Func<string, bool> accessible)
    {
        if (!Path.IsPathFullyQualified(path)) throw Failure(400, "invalid_path", "An absolute entry path is required.");
        var logical = Path.GetFullPath(path);
        var parent = Path.GetDirectoryName(logical) ?? throw Failure(403, "protected_root", "Filesystem roots cannot be mutated.");
        if (!accessible(logical)) throw new UnauthorizedAccessException();
        return Path.Combine(RequireDirectory(parent, accessible), Path.GetFileName(logical));
    }

    private static void RequireRecoveryParent(string path, Func<string, bool> accessible, bool create, FileDirectoryMetadata[]? metadata = null)
    {
        if (!accessible(path)) throw new UnauthorizedAccessException();
        var parent = Path.GetDirectoryName(path) ?? throw new UnauthorizedAccessException();
        var missing = new Stack<string>();
        while (!Directory.Exists(parent))
        {
            if (!accessible(parent)) throw new UnauthorizedAccessException();
            missing.Push(Path.GetFileName(parent));
            parent = Path.GetDirectoryName(parent) ?? throw new UnauthorizedAccessException();
        }
        var current = RequireDirectory(parent, accessible);
        while (missing.TryPop(out var name))
        {
            current = Path.Combine(current, name);
            if (!accessible(current)) throw new UnauthorizedAccessException();
            if (create)
            {
                Directory.CreateDirectory(current);
                try { metadata?.FirstOrDefault(entry => Paths.Equals(entry.Path, current))?.Apply(current); }
                catch { if (!Directory.EnumerateFileSystemEntries(current).Any()) Directory.Delete(current); throw; }
                current = RequireDirectory(current, accessible);
            }
        }
    }

    private static void ValidateRequest(FileOperationRequest request)
    {
        _ = ParseId(request.RequestId);
        if (!Actions.Contains(request.Action)) throw Failure(400, "invalid_action", "Unknown file operation.");
        if (request.Conflict is not ("fail" or "keepBoth" or "replace")) throw Failure(400, "invalid_conflict", "Unknown conflict decision.");
        if (request.Paths is null || request.Paths.Length > 1000 || request.Paths.Any(string.IsNullOrWhiteSpace)) throw Failure(400, "invalid_selection", "Select at most 1000 entries.");
        if (request.Action is "createFile" or "createFolder" or "rename") ValidateName(request.Name ?? string.Empty);
        if (request.Action is "createFile" or "createFolder" && request.Paths.Length != 0) throw Failure(400, "invalid_selection", "Creation does not accept source entries.");
        if (request.Action == "rename" && request.Paths.Length != 1) throw Failure(400, "single_entry_required", "Rename one selected entry.");
        if (request.Action is not ("createFile" or "createFolder") && request.Paths.Length == 0) throw Failure(400, "empty_selection", "Select at least one entry.");
    }

    private static void ValidateName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || Path.IsPathRooted(name))
            throw Failure(400, "invalid_name", "Enter a single valid filename or folder name.");
        if (OperatingSystem.IsWindows())
        {
            var stem = name.Split('.')[0].ToUpperInvariant();
            if (name.EndsWith('.', StringComparison.Ordinal) || name.EndsWith(' ', StringComparison.Ordinal) || stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$" ||
                (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && char.IsDigit(stem[3])))
                throw Failure(400, "invalid_name", "This name is reserved by Windows.");
        }
    }

    private static string UniqueName(string target)
    {
        if (!FileEntryLinks.Exists(target)) return target;
        var parent = Path.GetDirectoryName(target)!;
        var directory = FileEntryLinks.IsDirectory(target);
        var extension = directory ? string.Empty : Path.GetExtension(target);
        var stem = directory ? Path.GetFileName(target) : Path.GetFileNameWithoutExtension(target);
        for (var index = 2; index <= 10_000; index++)
        {
            var candidate = Path.Combine(parent, $"{stem} ({index.ToString(System.Globalization.CultureInfo.InvariantCulture)}){extension}");
            if (!FileEntryLinks.Exists(candidate)) return candidate;
        }
        throw Failure(409, "name_conflict", "Too many copies use this name. Enter another name.");
    }

    private static FileOperationItem ErrorItem(string? source, Exception exception)
    {
        var code = exception switch { FileDocumentException file => file.Code, UnauthorizedAccessException => "access_denied",
            FileNotFoundException or DirectoryNotFoundException => "deleted", OperationCanceledException => "cancelled",
            Win32Exception native when native.NativeErrorCode == 5 => "access_denied",
            IOException io when (io.HResult & 0xFFFF) is 32 or 33 => "busy",
            IOException io when (io.HResult & 0xFFFF) is 39 or 112 => "disk_full", _ => "io_error" };
        return new() { SourcePath = source, Success = false, Code = code, Message = exception.Message };
    }

    private static string ParseId(string value) => Guid.TryParse(value, out var id) && id != Guid.Empty ? id.ToString("N") :
        throw Failure(400, "invalid_operation_id", "A unique operation ID is required.");
    private void RequireOutsideRecovery(string path, bool includesChildren)
    {
        if (FileService.IsWithinDirectory(path, _storageBase) || (includesChildren && FileService.IsWithinDirectory(_storageBase, path)))
            throw Failure(403, "recovery_storage", "Operation receipts and recovery storage cannot be modified through file operations.");
    }
    private string RecoveryPath(string id) => Path.Combine(_storage, "records", id + ".json");
    private static string StageName(string parent) => Path.Combine(parent, ".tlbx-ops-" + Guid.NewGuid().ToString("N"));
    private static FileDocumentException Failure(int status, string code, string message) => new(status, code, message);

    private static T Read<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type) =>
        JsonSerializer.Deserialize(File.ReadAllText(path), type) ?? throw new IOException("Invalid operation receipt.");
    private static void Write<T>(string path, T value, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> type)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { JsonSerializer.Serialize(file, value, type); file.Flush(flushToDisk: true); }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed class OperationReceipt
{
    public string Digest { get; set; } = string.Empty;
    public string[] AuthorityPaths { get; set; } = [];
    public FileOperationResponse Response { get; set; } = new();
}

internal sealed class RecoveryRecord
{
    public string Id { get; set; } = string.Empty;
    public string OriginalPath { get; set; } = string.Empty;
    public string StoredPath { get; set; } = string.Empty;
    public string? PendingStoredPath { get; set; }
    public string StagePath { get; set; } = string.Empty;
    public string? RestoredPath { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string State { get; set; } = "prepared";
    public string? Fingerprint { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public bool IsDirectory { get; set; }
    public bool IsLink { get; set; }
    public FileAttributes OriginalAttributes { get; set; }
    public FileDirectoryMetadata[] ParentMetadata { get; set; } = [];
}

[JsonSerializable(typeof(FileOperationRequest))]
[JsonSerializable(typeof(OperationReceipt))]
[JsonSerializable(typeof(RecoveryRecord))]
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
internal partial class FileOperationJsonContext : JsonSerializerContext;

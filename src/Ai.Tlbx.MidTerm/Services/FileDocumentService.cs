using System.Security.Cryptography;
using System.Text;
using Ai.Tlbx.MidTerm.Common.Logging;
using Ai.Tlbx.MidTerm.Models.Files;

namespace Ai.Tlbx.MidTerm.Services;

/// <summary>Reads versioned text snapshots and conditionally publishes complete documents.</summary>
public sealed class FileDocumentService
{
    public const int PreviewBytes = 512 * 1024;
    public const int MaxEditableBytes = 32 * 1024 * 1024;
    // Fixed stripes bound synchronization memory independently of the number of browsed files.
    private static readonly SemaphoreSlim[] WriteGates = Enumerable.Range(0, 64)
        .Select(static _ => new SemaphoreSlim(1, 1)).ToArray();
    private static readonly StringComparer PathComparer = OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;

    public static string ResolvePath(string path) => ResolvePath(path, 0);

    private static string ResolvePath(string path, int depth)
    {
        if (depth > 40) throw new IOException("Too many symbolic link targets while resolving the path.");
        var full = Path.GetFullPath(path);
        var root = Path.GetPathRoot(full)!;
        var current = root;
        foreach (var part in full[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            FileSystemInfo entry = Directory.Exists(current) ? new DirectoryInfo(current) : new FileInfo(current);
            if (entry.LinkTarget is not null)
            {
                var target = entry.ResolveLinkTarget(returnFinalTarget: true)?.FullName
                    ?? throw new FileNotFoundException("The symbolic link target no longer exists.", current);
                // Link targets may themselves use aliases in a parent component (/var on macOS).
                current = ResolvePath(target, depth + 1);
            }
        }
        return current;
    }

    public async Task<FileDocumentResponse> ReadAsync(string path, bool full, CancellationToken ct, long offset = 0)
    {
        var resolved = ResolvePath(path);
        await using var stream = OpenRead(resolved);
        var size = stream.Length;
        if (offset < 0 || offset > size || (full && offset != 0))
            throw new FileDocumentException(400, "invalid_offset", "The text page position is invalid. Return to the first page.");
        var header = new byte[checked((int)Math.Min(4, size))];
        await stream.ReadExactlyAsync(header, ct);
        var codec = DetectEncoding(header);
        var complete = offset == 0 && (size <= PreviewBytes || (full && size <= MaxEditableBytes));
        var start = offset == 0 ? codec.PreambleLength : offset;
        stream.Position = start;
        var count = checked((int)Math.Min(size - start, complete ? size : PreviewBytes - (offset == 0 ? codec.PreambleLength : 0)));
        var bytes = new byte[count];
        await stream.ReadExactlyAsync(bytes, ct);
        var more = start + bytes.Length < size;
        var content = Decode(bytes, codec.Encoding, more);
        // Keep a CRLF pair together, just as an incomplete multibyte code point.
        if (more && content.EndsWith('\r', StringComparison.Ordinal)) content = content[..^1];
        var next = start + codec.Encoding.GetByteCount(content);
        var truncated = offset > 0 || next < size;
        var version = !truncated ? Version(resolved, codec.Encoding.GetPreamble().Concat(bytes).ToArray()) : null;
        return new FileDocumentResponse
        {
            Path = path,
            Content = NormalizeNewlines(content),
            Version = version,
            Size = size,
            Encoding = codec.Name,
            LineEnding = DetectLineEnding(content),
            IsTruncated = truncated,
            CanLoadFull = size <= MaxEditableBytes && truncated,
            ReadOnly = truncated || (File.GetAttributes(resolved) & FileAttributes.ReadOnly) != 0,
            Offset = offset,
            NextOffset = next,
            PreviewBytes = next - offset,
            EditLimitBytes = MaxEditableBytes
        };
    }

    public async Task<FileSaveResponse> SaveAsync(string path, string content, string expectedVersion, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(expectedVersion))
            throw new FileDocumentException(428, "version_required", "Reload the complete file before saving.");
        var resolved = ResolvePath(path);
        var gate = WriteGates[(uint)PathComparer.GetHashCode(resolved) % WriteGates.Length];
        await gate.WaitAsync(ct);
        try { return await SaveLockedAsync(resolved, content, expectedVersion, ct); }
        finally { gate.Release(); }
    }

    private static async Task<FileSaveResponse> SaveLockedAsync(string path, string content, string expectedVersion, CancellationToken ct)
    {
        await using var original = OpenRead(path);
        if (original.Length > MaxEditableBytes)
            throw new FileDocumentException(413, "too_large", "This file is too large to edit as a complete document.");
        if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
            throw new UnauthorizedAccessException("The file is read-only.");
        var bytes = new byte[checked((int)original.Length)];
        await original.ReadExactlyAsync(bytes, ct);
        var currentVersion = Version(path, bytes);
        var codec = DetectEncoding(bytes);
        var originalText = Decode(bytes.AsSpan(codec.PreambleLength), codec.Encoding, false);
        var lineEnding = DetectLineEnding(originalText);
        var normalized = NormalizeNewlines(content);
        var encodedText = lineEnding == "crlf" ? normalized.Replace("\n", "\r\n", StringComparison.Ordinal) : normalized;
        var encoded = codec.Encoding.GetBytes(encodedText);
        var wanted = codec.Encoding.GetPreamble().Concat(encoded).ToArray();
        if (wanted.Length > MaxEditableBytes)
            throw new FileDocumentException(413, "too_large", "The edited document exceeds the editing size limit.");
        // Repeating a completed save is harmless and does not modify timestamps.
        if (wanted.AsSpan().SequenceEqual(bytes))
            return SaveResult(currentVersion, bytes.Length, codec.Name, lineEnding);
        if (!string.Equals(expectedVersion, currentVersion, StringComparison.Ordinal))
            throw Changed(currentVersion);

        var folder = Path.GetDirectoryName(path)!;
        var nonce = Guid.NewGuid().ToString("N");
        var temporary = Path.Combine(folder, $".tlbx-save-{nonce}.tmp");
        var recovery = Path.Combine(folder, $".tlbx-previous-{nonce}.bak");
        var newerRecovery = Path.Combine(folder, $".tlbx-concurrent-{nonce}.bak");
        var keepRecovery = false;
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 16384, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await output.WriteAsync(wanted, ct);
                await output.FlushAsync(ct);
                output.Flush(flushToDisk: true);
            }
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(temporary, File.GetUnixFileMode(path));
            var beforePublish = await ComputeVersionAsync(path, path, ct);
            if (!string.Equals(beforePublish, currentVersion, StringComparison.Ordinal)) throw Changed(beforePublish);
            ct.ThrowIfCancellationRequested();
            // Cancellation is honored before publication; the replacement itself is indivisible.
            keepRecovery = true;
            File.Replace(temporary, path, recovery);
            var replacedVersion = await ComputeVersionAsync(recovery, path, CancellationToken.None);
            if (!string.Equals(replacedVersion, currentVersion, StringComparison.Ordinal))
            {
                // A competing rename slipped between verification and publication. Keep both
                // versions while restoring the bytes actually replaced instead of losing them.
                File.Replace(recovery, path, newerRecovery);
                throw new FileDocumentException(409, "changed_during_save", "The file changed during saving. A recovery copy was retained.",
                    await ComputeVersionAsync(path, path, CancellationToken.None), newerRecovery);
            }
            await using var published = OpenRead(path);
            if (published.Length != wanted.Length)
                throw new FileDocumentException(409, "changed_during_save", "Another process changed the published file. Your draft and the previous version were retained.", null, recovery);
            var publishedBytes = new byte[wanted.Length];
            await published.ReadExactlyAsync(publishedBytes, CancellationToken.None);
            var savedVersion = Version(path, publishedBytes);
            if (!publishedBytes.AsSpan().SequenceEqual(wanted))
                throw new FileDocumentException(409, "changed_during_save", "Another process changed the published file. Your draft and the previous version were retained.", savedVersion, recovery);
            keepRecovery = false;
            return SaveResult(savedVersion, wanted.Length, codec.Name, lineEnding == "mixed" ? "lf" : lineEnding);
        }
        catch (IOException ex) when (ex is not FileDocumentException && File.Exists(recovery))
        {
            throw new FileDocumentException(500, "save_failed", "Saving failed. The previous file was retained for recovery.", null, recovery, ex);
        }
        finally
        {
            DeleteTemporary(temporary);
            if (!keepRecovery) { DeleteTemporary(recovery); DeleteTemporary(newerRecovery); }
        }
    }

    private static FileStream OpenRead(string path) => new(path, FileMode.Open, FileAccess.Read,
        FileShare.Read | FileShare.Delete, 16384, FileOptions.Asynchronous | FileOptions.SequentialScan);

    private static FileSaveResponse SaveResult(string version, long size, string encoding, string lineEnding) =>
        new() { Success = true, Size = size, Version = version, Encoding = encoding, LineEnding = lineEnding };

    private static FileDocumentException Changed(string version) =>
        new(409, "changed", "The file changed on disk. Compare or reload it before saving.", version);

    private static IncrementalHash StartVersion(string path)
    {
        var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var identity = OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;
        hash.AppendData(Encoding.UTF8.GetBytes(identity));
        hash.AppendData([0]);
        return hash;
    }

    private static string Version(string path, byte[] bytes)
    {
        using var hash = StartVersion(path);
        hash.AppendData(bytes);
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static async Task<string> ComputeVersionAsync(string path, string identityPath, CancellationToken ct)
    {
        await using var stream = OpenRead(path);
        using var hash = StartVersion(identityPath);
        var buffer = new byte[16384];
        int count;
        while ((count = await stream.ReadAsync(buffer, ct)) > 0) hash.AppendData(buffer.AsSpan(0, count));
        return Convert.ToHexStringLower(hash.GetHashAndReset());
    }

    private static (Encoding Encoding, string Name, int PreambleLength) DetectEncoding(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE, 0, 0 })) return (new UTF32Encoding(false, true, true), "UTF-32 LE", 4);
        if (bytes.StartsWith(new byte[] { 0, 0, 0xFE, 0xFF })) return (new UTF32Encoding(true, true, true), "UTF-32 BE", 4);
        if (bytes.StartsWith(new byte[] { 0xFF, 0xFE })) return (new UnicodeEncoding(false, true, true), "UTF-16 LE", 2);
        if (bytes.StartsWith(new byte[] { 0xFE, 0xFF })) return (new UnicodeEncoding(true, true, true), "UTF-16 BE", 2);
        if (bytes.StartsWith(new byte[] { 0xEF, 0xBB, 0xBF })) return (new UTF8Encoding(true, true), "UTF-8 BOM", 3);
        return (new UTF8Encoding(false, true), "UTF-8", 0);
    }

    private static string Decode(ReadOnlySpan<byte> bytes, Encoding encoding, bool partial)
    {
        try
        {
            var chars = new char[encoding.GetMaxCharCount(bytes.Length)];
            encoding.GetDecoder().Convert(bytes, chars, flush: !partial, out _, out var count, out _);
            var text = new string(chars, 0, count);
            if (text.Contains('\0', StringComparison.Ordinal)) throw new DecoderFallbackException("Binary content.");
            return text;
        }
        catch (DecoderFallbackException ex)
        {
            throw new FileDocumentException(422, "unsupported_encoding", "This file cannot be edited as Unicode text. Use the binary preview or download it.", null, null, ex);
        }
    }

    private static string NormalizeNewlines(string text) => text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');

    private static string DetectLineEnding(string text)
    {
        var lf = text.Count(static ch => ch == '\n');
        var cr = text.Count(static ch => ch == '\r');
        if (cr == 0) return "lf";
        var pairs = 0;
        for (var i = 1; i < text.Length; i++) if (text[i - 1] == '\r' && text[i] == '\n') pairs++;
        return pairs == lf && pairs == cr ? "crlf" : "mixed";
    }

    private static void DeleteTemporary(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { Log.Warn(() => $"Could not remove file-save temporary '{path}': {ex.Message}"); }
    }
}

public sealed class FileDocumentException(int statusCode, string code, string message,
    string? currentVersion = null, string? recoveryPath = null, Exception? inner = null) : IOException(message, inner)
{
    public int StatusCode { get; } = statusCode;
    public string Code { get; } = code;
    public string? CurrentVersion { get; } = currentVersion;
    public string? RecoveryPath { get; } = recoveryPath;
}

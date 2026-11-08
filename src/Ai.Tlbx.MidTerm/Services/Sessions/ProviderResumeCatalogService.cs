using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ai.Tlbx.MidTerm.Common.Text;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Settings;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed partial class ProviderResumeCatalogService
{
    internal const int MaxScannedFiles = 200;
    internal const int MaxCandidates = 60;
    internal const int HeadBytes = 128 * 1024;
    internal const int TailBytes = 128 * 1024;
    private const int MaxCachedTranscripts = 2048;
    private const int TitleLength = 100;
    private const int PreviewLength = 240;

    private readonly Func<string, string?> _resolveProfileDirectory;
    private readonly Func<string?> _resolveConfiguredCodexHome;
    private readonly ConcurrentDictionary<string, CachedTranscript> _cache = new(StringComparer.OrdinalIgnoreCase);

    public ProviderResumeCatalogService(SettingsService settingsService)
        : this(
            provider => ResolveProviderProfileDirectory(settingsService.Load(), provider),
            () => ResolveConfiguredCodexHome(settingsService.Load()))
    {
        _nativeCodex = (cwd, ct) => ReadNativeCodexAsync(settingsService.Load(),
            settingsService.SettingsDirectory, cwd, TtyHostSpawner.TryStartRedirectedProcess,
            IsCatalogRunningAsSystem, ct);
    }

    internal ProviderResumeCatalogService(
        Func<string, string?> resolveProfileDirectory,
        Func<string?>? resolveConfiguredCodexHome = null,
        Func<string?, CancellationToken, Task<IReadOnlyList<ProviderResumeCatalogEntryDto>?>>? nativeCodex = null)
    {
        _resolveProfileDirectory = resolveProfileDirectory;
        _resolveConfiguredCodexHome = resolveConfiguredCodexHome ?? (static () => null);
        _nativeCodex = nativeCodex;
    }

    /// <summary>
    /// The Codex runtime applies the owner-configured Codex environment variables after the
    /// profile defaults, so a fully qualified CODEX_HOME there is where its threads are stored.
    /// The service process environment is deliberately not consulted.
    /// </summary>
    internal static string? ResolveConfiguredCodexHome(MidTermSettings settings)
    {
        var variables = TerminalEnvironmentVariableParser.Parse(settings.CodexEnvironmentVariables);
        return variables is not null &&
               variables.TryGetValue("CODEX_HOME", out var value) &&
               value?.Trim() is { Length: > 0 } codexHome &&
               Path.IsPathFullyQualified(codexHome)
            ? codexHome
            : null;
    }

    internal static string? ResolveProviderProfileDirectory(MidTermSettings settings, string provider)
    {
        if (!OperatingSystem.IsWindows())
        {
            return LauncherPathResolver.ResolveHomePath(settings);
        }

        var configuredProfile = AppServerControlHostEnvironmentResolver.ResolveConfiguredWindowsProfileDirectory(settings);
        var executablePath = AiCliCommandLocator.FindExecutableInPath(provider, configuredProfile);
        return AppServerControlHostEnvironmentResolver.ResolvePreferredWindowsProfileDirectory(settings, executablePath);
    }

    public IReadOnlyList<ProviderResumeCatalogEntryDto> GetCandidates(
        string provider,
        string? workingDirectory,
        bool includeAllDirectories,
        CancellationToken ct = default)
    {
        var normalizedProvider = (provider ?? string.Empty).Trim().ToLowerInvariant();
        var isCodex = normalizedProvider == AiCliProfileService.CodexProfile;
        if (!isCodex && normalizedProvider != AiCliProfileService.ClaudeProfile)
        {
            return [];
        }

        var home = _resolveProfileDirectory(normalizedProvider);
        if (string.IsNullOrWhiteSpace(home))
        {
            return [];
        }

        var root = isCodex
            ? Path.Combine(_resolveConfiguredCodexHome() ?? Path.Combine(home, ".codex"), "sessions")
            : Path.Combine(home, ".claude", "projects");
        var directoryFilter = includeAllDirectories ? null : NormalizePathOrNull(workingDirectory);
        if (!Directory.Exists(root) || (!includeAllDirectories && directoryFilter is null))
        {
            return [];
        }

        var candidates = new List<ProviderResumeCatalogEntryDto>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in EnumerateTranscripts(root, excludeSubagents: !isCodex)
                     .OrderByDescending(static file => file.LastWriteTimeUtc)
                     .Take(MaxScannedFiles))
        {
            ct.ThrowIfCancellationRequested();
            var metadata = GetMetadata(file, isCodex);
            if (metadata is null ||
                (directoryFilter is not null &&
                 !string.Equals(NormalizePathOrNull(metadata.WorkingDirectory), directoryFilter, StringComparison.OrdinalIgnoreCase)) ||
                !seen.Add(metadata.SessionId))
            {
                continue;
            }

            candidates.Add(new ProviderResumeCatalogEntryDto
            {
                Provider = normalizedProvider,
                SessionId = metadata.SessionId,
                WorkingDirectory = metadata.WorkingDirectory,
                Title = metadata.Title,
                PreviewText = metadata.PreviewText,
                UpdatedAtUtc = metadata.UpdatedAtUtc
            });
            if (candidates.Count >= MaxCandidates)
            {
                break;
            }
        }

        return candidates.OrderByDescending(static candidate => candidate.UpdatedAtUtc).ToList();
    }

    private static IEnumerable<FileInfo> EnumerateTranscripts(string root, bool excludeSubagents)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        var files = new DirectoryInfo(root).EnumerateFiles("*.jsonl", options);
        return excludeSubagents
            ? files.Where(static file => !file.FullName
                .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Contains("subagents", StringComparer.OrdinalIgnoreCase))
            : files;
    }

    private TranscriptMetadata? GetMetadata(FileInfo file, bool isCodex)
    {
        if (_cache.TryGetValue(file.FullName, out var cached) &&
            cached.Length == file.Length &&
            cached.LastWriteTimeUtc == file.LastWriteTimeUtc)
        {
            return cached.Metadata;
        }

        var window = ReadTranscriptWindow(file.FullName);
        var metadata = window is null
            ? null
            : isCodex
                ? ParseCodex(window, file.LastWriteTimeUtc)
                : ParseClaude(window, file);
        if (_cache.Count >= MaxCachedTranscripts)
        {
            _cache.Clear();
        }

        _cache[file.FullName] = new CachedTranscript(file.Length, file.LastWriteTimeUtc, metadata);
        return metadata;
    }

    internal static TranscriptWindow? ReadTranscriptWindow(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var headLength = (int)Math.Min(HeadBytes, length);
            var head = ReadLines(stream, 0, headLength, dropTrailingPartial: length > headLength);
            var tailStart = Math.Max(headLength, length - TailBytes);
            var tail = tailStart < length
                ? ReadLines(stream, tailStart, (int)(length - tailStart), dropTrailingPartial: false)
                : [];
            return new TranscriptWindow(head, tail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static List<string> ReadLines(FileStream stream, long offset, int count, bool dropTrailingPartial)
    {
        var lines = new List<string>();
        var startsMidLine = offset > 0;
        var readOffset = startsMidLine ? offset - 1 : offset;
        var readCount = startsMidLine ? count + 1 : count;
        var buffer = ArrayPool<byte>.Shared.Rent(readCount);
        try
        {
            stream.Position = readOffset;
            stream.ReadExactly(buffer, 0, readCount);
            var span = buffer.AsSpan(0, readCount);
            if (startsMidLine)
            {
                var firstBreak = span.IndexOf((byte)'\n');
                span = firstBreak < 0 ? [] : span[(firstBreak + 1)..];
            }

            while (!span.IsEmpty)
            {
                var lineBreak = span.IndexOf((byte)'\n');
                if (lineBreak < 0)
                {
                    if (!dropTrailingPartial)
                    {
                        lines.Add(Encoding.UTF8.GetString(span).TrimEnd('\r'));
                    }

                    break;
                }

                lines.Add(Encoding.UTF8.GetString(span[..lineBreak]).TrimEnd('\r'));
                span = span[(lineBreak + 1)..];
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return lines;
    }

    private static TranscriptMetadata? ParseClaude(TranscriptWindow window, FileInfo file)
    {
        string? sessionId = null;
        string? workingDirectory = null;
        string? summary = null;
        string? firstPrompt = null;
        string? lastPrompt = null;
        var updatedAtUtc = file.LastWriteTimeUtc;
        foreach (var line in window.AllLines)
        {
            using var json = TryParse(line);
            if (json is null)
            {
                continue;
            }

            var root = json.RootElement;
            sessionId ??= TryGetString(root, "sessionId");
            workingDirectory ??= TryGetString(root, "cwd");
            updatedAtUtc = Later(updatedAtUtc, root);
            if (TryGetString(root, "type") == "summary" && TryGetString(root, "summary") is { } parsedSummary)
            {
                summary = parsedSummary;
            }

            if (TryReadClaudeUserMessage(root) is { } prompt)
            {
                firstPrompt ??= prompt;
                lastPrompt = prompt;
            }
        }

        sessionId ??= Guid.TryParse(Path.GetFileNameWithoutExtension(file.Name), out _)
            ? Path.GetFileNameWithoutExtension(file.Name)
            : null;
        return sessionId is null || workingDirectory is null
            ? null
            : CreateMetadata(sessionId, workingDirectory, summary ?? firstPrompt, lastPrompt, updatedAtUtc);
    }

    private static TranscriptMetadata? ParseCodex(TranscriptWindow window, DateTime lastWriteTimeUtc)
    {
        string? sessionId = null;
        string? workingDirectory = null;
        string? firstPrompt = null;
        string? lastPrompt = null;
        var updatedAtUtc = lastWriteTimeUtc;
        foreach (var line in window.AllLines)
        {
            using var json = TryParse(line);
            if (json is null)
            {
                continue;
            }

            var root = json.RootElement;
            updatedAtUtc = Later(updatedAtUtc, root);
            if (sessionId is null &&
                TryGetString(root, "type") == "session_meta" &&
                root.TryGetProperty("payload", out var meta) &&
                meta.ValueKind == JsonValueKind.Object)
            {
                sessionId = TryGetString(meta, "id");
                workingDirectory = TryGetString(meta, "cwd");
                updatedAtUtc = Later(updatedAtUtc, meta);
                continue;
            }

            if (TryReadCodexUserMessage(root) is { } prompt)
            {
                firstPrompt ??= prompt;
                lastPrompt = prompt;
            }
        }

        return sessionId is null || workingDirectory is null
            ? null
            : CreateMetadata(sessionId, workingDirectory, firstPrompt, lastPrompt, updatedAtUtc);
    }

    private static TranscriptMetadata CreateMetadata(
        string sessionId,
        string workingDirectory,
        string? titleSource,
        string? lastPrompt,
        DateTime updatedAtUtc,
        string? plainTitle = null)
    {
        // A provider's own thread name is plain text, never Markdown to strip.
        var title = NormalizeDisplayText(plainTitle, TitleLength)
                    ?? NormalizeDisplayText(ReadRichDisplayText(titleSource, preferProse: true), TitleLength)
                    ?? BuildDirectoryLabel(workingDirectory);
        var preview = NormalizeDisplayText(ReadRichDisplayText(lastPrompt, preferProse: false), PreviewLength);
        if (preview is not null && NormalizeDisplayText(preview, TitleLength) == title)
        {
            preview = null;
        }

        return new TranscriptMetadata(
            sessionId,
            workingDirectory,
            title,
            preview,
            DateTime.SpecifyKind(updatedAtUtc, DateTimeKind.Utc));
    }

    private static string? TryReadClaudeUserMessage(JsonElement root)
    {
        if (TryGetString(root, "type") != "user" ||
            (root.TryGetProperty("isMeta", out var isMeta) && isMeta.ValueKind == JsonValueKind.True) ||
            !root.TryGetProperty("message", out var message) ||
            message.ValueKind != JsonValueKind.Object ||
            !message.TryGetProperty("content", out var content))
        {
            return null;
        }

        return content.ValueKind == JsonValueKind.String
            ? RealPromptOrNull(content.GetString())
            : JoinTextParts(content, "text");
    }

    private static string? TryReadCodexUserMessage(JsonElement root)
    {
        if (!root.TryGetProperty("payload", out var payload) || payload.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var rootType = TryGetString(root, "type");
        var payloadType = TryGetString(payload, "type");
        if (rootType == "event_msg" && payloadType == "user_message")
        {
            return RealPromptOrNull(TryGetString(payload, "message"));
        }

        return rootType == "response_item" &&
               payloadType == "message" &&
               TryGetString(payload, "role") == "user" &&
               payload.TryGetProperty("content", out var content)
            ? JoinTextParts(content, "input_text")
            : null;
    }

    private static string? JoinTextParts(JsonElement content, string textType)
    {
        if (content.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        using var items = content.EnumerateArray();
        foreach (var item in items)
        {
            if (item.ValueKind == JsonValueKind.Object &&
                TryGetString(item, "type") == textType &&
                TryGetString(item, "text") is { } text)
            {
                parts.Add(text);
            }
        }

        return RealPromptOrNull(string.Join(" ", parts));
    }

    private static string? RealPromptOrNull(string? text)
    {
        return string.IsNullOrWhiteSpace(text) || SyntheticPromptPattern().IsMatch(text) ? null : text;
    }

    [GeneratedRegex(
        @"^\s*(?:<(?:environment_context|user_instructions|command-name|command-message|command-args|local-command-stdout|local-command-stderr|system-reminder|user-prompt-submit-hook)\b|# AGENTS\.md instructions|Caveat: The messages below were generated)",
        RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SyntheticPromptPattern();

    private static DateTime Later(DateTime current, JsonElement element)
    {
        return TryGetString(element, "timestamp") is { } text &&
               DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var timestamp) &&
               timestamp.UtcDateTime > current
            ? timestamp.UtcDateTime
            : current;
    }

    private static JsonDocument? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        try
        {
            var document = JsonDocument.Parse(line);
            if (document.RootElement.ValueKind == JsonValueKind.Object)
            {
                return document;
            }

            document.Dispose();
            return null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? TryGetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String &&
               property.GetString() is { } value &&
               !string.IsNullOrWhiteSpace(value)
            ? value
            : null;
    }

    private static string? NormalizePathOrNull(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        try
        {
            return Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        }
    }

    private static string BuildDirectoryLabel(string workingDirectory)
    {
        var leaf = Path.GetFileName(workingDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrWhiteSpace(leaf) ? workingDirectory : leaf;
    }

    internal static string? NormalizeDisplayText(string? text, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var builder = new StringBuilder(Math.Min(text.Length, maxLength + 1));
        var pendingSpace = false;
        foreach (var character in text)
        {
            if (char.IsWhiteSpace(character) || char.IsControl(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
            if (builder.Length > maxLength)
            {
                Utf16Text.Truncate(builder, maxLength - 1);
                return builder.ToString().TrimEnd() + "…";
            }
        }

        return builder.Length == 0 ? null : builder.ToString();
    }

    internal sealed record TranscriptWindow(IReadOnlyList<string> Head, IReadOnlyList<string> Tail)
    {
        public IEnumerable<string> AllLines => Head.Concat(Tail);
    }

    private sealed record TranscriptMetadata(
        string SessionId,
        string WorkingDirectory,
        string Title,
        string? PreviewText,
        DateTime UpdatedAtUtc);

    private readonly record struct CachedTranscript(long Length, DateTime LastWriteTimeUtc, TranscriptMetadata? Metadata);
}

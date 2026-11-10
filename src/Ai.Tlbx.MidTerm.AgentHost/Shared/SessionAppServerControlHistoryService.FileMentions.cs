using System.Globalization;
using System.Text.RegularExpressions;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Models.Files;

namespace Ai.Tlbx.MidTerm.Services.Sessions;

public sealed partial class SessionAppServerControlHistoryService
{
    private const int MaxInlineMentionsPerField = 24;
    private const int MaxInlineImagePreviewsPerEntry = 6;
    private static readonly TimeSpan RegexMatchTimeout = TimeSpan.FromMilliseconds(250);

    private static readonly Regex UnixAbsolutePathPattern = new(
        @"(?:^|[\s""'`(\[{<])(?<path>/(?:[^/:""'`<>|()\r\n\s]+/)*(?:[^/:""'`<>|()\r\n\s]+)?/?)(?=$|[\s""'`)\]}>.,;!?]|:(?=\d))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexMatchTimeout);

    private static readonly Regex WindowsAbsolutePathPattern = new(
        @"(?:^|[\s""'`(\[{<])(?<path>[A-Za-z]:[\\/](?:[^<>:""/\\|?*()\r\n\s]+[\\/])*(?:[^<>:""/\\|?*()\r\n\s]+)?[\\/]?)(?=$|[\s""'`)\]}>.,;!?]|:(?=\d))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexMatchTimeout);

    private static readonly Regex UncAbsolutePathPattern = new(
        @"(?:^|[\s""'`(\[{<])(?<path>\\\\[^\\/\r\n\s]+[\\/]+[^\\/\r\n\s]+(?:[\\/]+[^\\/\r\n\s]+)*[\\/]?)(?=$|[\s""'`)\]}>.,;!?]|:(?=\d))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexMatchTimeout);

    private static readonly Regex QuotedAbsolutePathPattern = new(
        "(?:[\"'`])(?<path>(?:\\\\\\\\[^\\\\/\\r\\n]+[\\\\/]+[^\\\\/\\r\\n]+(?:[\\\\/]+[^\\\\/\\r\\n]+)*[\\\\/]?|[A-Za-z]:[\\\\/](?:[^<>:\"/\\\\|?*\\r\\n]+[\\\\/])*(?:[^<>:\"/\\\\|?*\\r\\n]+)?[\\\\/]?|/(?:[^/:\"'`<>|\\r\\n]+/)*(?:[^/:\"'`<>|\\r\\n]+)?/?))(?:[\"'`])",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexMatchTimeout);

    private static readonly Regex RelativePathPattern = new(
        @"(?:^|[\s""'`([{<])(?<path>(?:\.\.?[/\\])?(?:[\w.@-]+[/\\])*[\w.@-]+\.[A-Za-z][A-Za-z0-9]{0,14})",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexMatchTimeout);

    private static readonly Regex FolderPathPattern = new(
        @"(?:^|[\s""'`([{<])(?<path>(?:\.\.?[/\\])?(?:[\w.@-]+[/\\])+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexMatchTimeout);

    private static readonly string[] KnownExtensionlessFileNames =
    [
        "Dockerfile",
        "Makefile",
        "Vagrantfile",
        "Gemfile",
        "Rakefile",
        "Procfile",
        "Justfile",
        "Taskfile",
        "Brewfile",
        "Podfile",
        "Fastfile",
        "Appfile",
        "LICENSE",
        "LICENCE",
        "CHANGELOG",
        "README",
        "CONTRIBUTING",
        "AUTHORS",
        ".gitignore",
        ".gitattributes",
        ".gitmodules",
        ".editorconfig",
        ".dockerignore",
        ".eslintignore",
        ".prettierignore",
        ".npmignore",
        ".env",
        ".env.local",
        ".env.production",
        ".env.development",
        ".prettierrc",
        ".eslintrc",
        ".babelrc",
        ".browserslistrc"
    ];

    private static readonly Regex KnownFilePattern = new(
        $@"(?:^|[\s""'`(\[{{<])(?<path>(?:[\w.@-]+[/\\])*(?:{string.Join("|", KnownExtensionlessFileNames
            .OrderByDescending(static value => value.Length)
            .Select(Regex.Escape))}))",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexMatchTimeout);

    private static readonly Regex LeadingLineInfoPattern = new(
        @"^:(\d+)(?::(\d+))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexMatchTimeout);

    private static readonly Regex TrailingLineInfoPattern = new(
        @":(\d+)(?::(\d+))?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexMatchTimeout);

    private static readonly Regex UrlSchemePattern = new(
        @"^[a-z][a-z0-9+.-]*://",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexMatchTimeout);

    private static readonly Regex IpAddressWithOptionalPortPattern = new(
        @"^\d{1,3}(?:\.\d{1,3}){3}(?::\d+)?(?:/|$)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexMatchTimeout);

    private static readonly Regex TrailingPortPattern = new(
        @":\d+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexMatchTimeout);

    private static readonly Regex HostPortPattern = new(
        @"^[A-Za-z0-9.-]+$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexMatchTimeout);

    private static readonly Regex TldPattern = new(
        @"^[A-Za-z]{2,24}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexMatchTimeout);

    private static readonly Regex VersionLikeNumberPattern = new(
        @"^\d+\.\d+(?:\.\d+)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexMatchTimeout);

    private static readonly Regex WindowsDrivePrefixPattern = new(
        @"^[A-Za-z]:",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexMatchTimeout);

    private static readonly Regex UppercaseIdentifierPattern = new(
        @"^[A-Z][A-Za-z0-9_]{5,}$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant,
        RegexMatchTimeout);

    private static readonly HashSet<string> CommonTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "org", "net", "io", "co", "dev", "app", "ai", "edu", "gov", "me", "us", "uk", "de", "fr", "jp",
        "cn", "ru", "br", "au", "ca", "in", "nl", "it", "es", "ch", "se", "no", "fi", "dk", "pl", "tv", "xyz",
        "site", "online", "tech", "store", "blog", "cloud", "info", "biz", "pro", "name", "today", "live",
        "news", "world", "media"
    };

    private static readonly HashSet<char> TrailingTrimChars = ['.', ',', ';', '!', '?', '"', '\'', '`'];
    private static readonly HashSet<string> ImageExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".avif", ".bmp", ".gif", ".ico", ".jpeg", ".jpg", ".png", ".svg", ".tif", ".tiff", ".webp"
    };

    private readonly TtyHostSessionManager? _sessionManager;
    private readonly SessionPathAllowlistService? _allowlistService;

    private AppServerControlHistoryItem CloneHistoryEntry(string sessionId, AppServerControlHistoryItem source)
    {
        var enriched = EnrichHistoryEntry(sessionId, source);
        return new AppServerControlHistoryItem
        {
            EntryId = enriched.EntryId,
            Order = enriched.Order,
            EstimatedHeightPx = EstimateHistoryEntryHeightPx(enriched),
            Kind = enriched.Kind,
            TurnId = enriched.TurnId,
            ItemId = enriched.ItemId,
            RequestId = enriched.RequestId,
            Status = enriched.Status,
            ItemType = enriched.ItemType,
            Title = enriched.Title,
            CommandText = enriched.CommandText,
            ToolPresentation = AppServerControlToolPresentationProtocol.Clone(enriched.ToolPresentation),
            Body = enriched.Body,
            Attachments = CloneAttachments(enriched.Attachments),
            FileMentions = CloneInlineFileReferences(enriched.FileMentions),
            ImagePreviews = CloneInlineImagePreviews(enriched.ImagePreviews),
            Questions = enriched.Questions.Select(CloneQuestion).ToList(),
            Answers = enriched.Answers.Select(CloneAnsweredQuestion).ToList(),
            Streaming = enriched.Streaming,
            CreatedAt = enriched.CreatedAt,
            UpdatedAt = enriched.UpdatedAt
        };
    }

    private AppServerControlHistoryItem EnrichHistoryEntry(string sessionId, AppServerControlHistoryItem entry)
    {
        var nextSignature = string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{(entry.Streaming ? 1 : 0)}\u001f{entry.Title}\u001f{entry.Body}\u001f{entry.CommandText}");
        if (string.Equals(entry.EnrichmentSourceSignature, nextSignature, StringComparison.Ordinal))
        {
            return entry;
        }

        if (entry.Streaming)
        {
            entry.FileMentions = [];
            entry.ImagePreviews = [];
            entry.EnrichmentSourceSignature = nextSignature;
            return entry;
        }

        var workingDirectory = ResolveSessionWorkingDirectory(sessionId);
        var fileMentions = new List<AppServerControlInlineFileReference>(MaxInlineMentionsPerField);
        fileMentions.AddRange(CollectFieldFileMentions(sessionId, "title", entry.Title, workingDirectory));
        fileMentions.AddRange(CollectFieldFileMentions(sessionId, "body", entry.Body, workingDirectory));
        fileMentions.AddRange(CollectFieldFileMentions(sessionId, "commandText", entry.CommandText, workingDirectory));

        entry.FileMentions = fileMentions;
        entry.ImagePreviews = fileMentions
            .Where(static mention => mention.Exists &&
                                     !mention.IsDirectory &&
                                     !string.IsNullOrWhiteSpace(mention.ResolvedPath) &&
                                     IsImageReference(mention))
            .GroupBy(static mention => mention.ResolvedPath!, StringComparer.OrdinalIgnoreCase)
            .Select(static group =>
            {
                var mention = group.First();
                return new AppServerControlInlineImagePreview
                {
                    DisplayPath = mention.DisplayText,
                    ResolvedPath = mention.ResolvedPath ?? string.Empty,
                    MimeType = mention.MimeType
                };
            })
            .Take(MaxInlineImagePreviewsPerEntry)
            .ToList();
        entry.EnrichmentSourceSignature = nextSignature;
        return entry;
    }

    private string? ResolveSessionWorkingDirectory(string sessionId)
    {
        var workingDirectory = _sessionManager?.GetSession(sessionId)?.CurrentDirectory;
        return string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory.Trim();
    }

    private List<AppServerControlInlineFileReference> CollectFieldFileMentions(
        string sessionId,
        string field,
        string? text,
        string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var candidates = CollectFileMentionCandidates(text);
        if (candidates.Count == 0)
        {
            return [];
        }

        var mentions = new List<AppServerControlInlineFileReference>(Math.Min(MaxInlineMentionsPerField, candidates.Count));
        var seenDisplayTexts = new HashSet<string>(StringComparer.Ordinal);
        foreach (var candidate in candidates)
        {
            if (mentions.Count >= MaxInlineMentionsPerField)
            {
                break;
            }

            if (!seenDisplayTexts.Add(candidate.DisplayText))
            {
                continue;
            }

            var reference = new AppServerControlInlineFileReference
            {
                Field = field,
                DisplayText = candidate.DisplayText,
                Path = candidate.Path,
                PathKind = candidate.PathKind,
                Line = candidate.Line,
                Column = candidate.Column
            };

            if (TryResolveFileReference(sessionId, candidate.Path, candidate.PathKind, workingDirectory, out var resolvedPath, out var info))
            {
                reference.ResolvedPath = resolvedPath;
                reference.Exists = info.Exists;
                reference.IsDirectory = info.IsDirectory;
                reference.MimeType = string.IsNullOrWhiteSpace(info.MimeType) ? null : info.MimeType;
            }

            mentions.Add(reference);
        }

        return mentions;
    }

    private List<FileMentionCandidate> CollectFileMentionCandidates(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var candidates = new List<FileMentionCandidate>();
        var nextPriority = 0;
        var hasSlash = text.Contains('/', StringComparison.Ordinal);
        var hasBackslash = text.Contains('\\', StringComparison.Ordinal);
        var hasColon = text.Contains(':', StringComparison.Ordinal);
        var hasDot = text.Contains('.', StringComparison.Ordinal);
        var hasQuote = text.IndexOfAny(['"', '\'', '`']) >= 0;

        if (hasQuote && (hasSlash || hasBackslash))
        {
            CollectRegexMentionCandidates(candidates, text, QuotedAbsolutePathPattern, "absolute", shouldReject: static _ => false, ref nextPriority);
        }

        if (hasBackslash)
        {
            CollectRegexMentionCandidates(candidates, text, UncAbsolutePathPattern, "absolute", shouldReject: static _ => false, ref nextPriority);
        }

        if (hasColon && (hasSlash || hasBackslash))
        {
            CollectRegexMentionCandidates(candidates, text, WindowsAbsolutePathPattern, "absolute", shouldReject: static _ => false, ref nextPriority);
        }

        if (hasSlash)
        {
            CollectRegexMentionCandidates(candidates, text, UnixAbsolutePathPattern, "absolute", shouldReject: static _ => false, ref nextPriority);
        }

        if (hasDot)
        {
            CollectRegexMentionCandidates(candidates, text, RelativePathPattern, "relative", ShouldRejectRelativeMatch, ref nextPriority);
        }

        if (hasSlash || hasBackslash)
        {
            CollectRegexMentionCandidates(candidates, text, FolderPathPattern, "relative", ShouldRejectFolderMatch, ref nextPriority);
        }

        CollectRegexMentionCandidates(candidates, text, KnownFilePattern, "relative", ShouldRejectKnownFileMatch, ref nextPriority);
        return FilterAndSortMentionCandidates(candidates);
    }

    private static void CollectRegexMentionCandidates(
        List<FileMentionCandidate> candidates,
        string text,
        Regex pattern,
        string pathKind,
        Func<string, bool> shouldReject,
        ref int nextPriority)
    {
        foreach (Match match in pattern.Matches(text))
        {
            if (!match.Success)
            {
                continue;
            }

            var group = match.Groups["path"];
            if (!group.Success || group.Length == 0)
            {
                continue;
            }

            var normalizedPath = NormalizePathCandidate(group.Value);
            if (string.IsNullOrWhiteSpace(normalizedPath) || shouldReject(normalizedPath))
            {
                continue;
            }

            var start = group.Index;
            var end = group.Index + group.Length;
            var (suffix, line, column) = ParseLineInfoSuffix(text, end);
            var displayText = suffix.Length == 0 ? group.Value : group.Value + suffix;
            candidates.Add(new FileMentionCandidate(start, end + suffix.Length, displayText, normalizedPath, pathKind, line, column, nextPriority++));
        }
    }

    private static List<FileMentionCandidate> FilterAndSortMentionCandidates(List<FileMentionCandidate> candidates)
    {
        if (candidates.Count == 0)
        {
            return [];
        }

        var filtered = new List<FileMentionCandidate>(candidates.Count);
        foreach (var candidate in candidates.OrderBy(static candidate => candidate.Priority))
        {
            if (candidate.Start >= candidate.End)
            {
                continue;
            }

            var overlapsExisting = false;
            foreach (var existing in filtered)
            {
                if (candidate.Start < existing.End && candidate.End > existing.Start)
                {
                    overlapsExisting = true;
                    break;
                }
            }

            if (!overlapsExisting)
            {
                filtered.Add(candidate);
            }
        }

        return filtered
            .OrderBy(static candidate => candidate.Start)
            .ThenBy(static candidate => candidate.End)
            .ToList();
    }

    private bool TryResolveFileReference(
        string sessionId,
        string path,
        string pathKind,
        string? workingDirectory,
        out string? resolvedPath,
        out FilePathInfo info)
    {
        resolvedPath = null;
        info = new FilePathInfo { Exists = false };

        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            if (string.Equals(pathKind, "absolute", StringComparison.Ordinal) || Path.IsPathRooted(path))
            {
                foreach (var candidate in FileService.GetSlashVariants(path))
                {
                    var fullPath = Path.GetFullPath(candidate);
                    var fileInfo = FileService.GetFileInfo(fullPath);
                    if (!fileInfo.Exists)
                    {
                        continue;
                    }

                    RegisterAccessibleResolvedPath(sessionId, workingDirectory, fullPath);
                    resolvedPath = fullPath;
                    info = fileInfo;
                    return true;
                }

                return false;
            }

            if (string.IsNullOrWhiteSpace(workingDirectory) || !Directory.Exists(workingDirectory))
            {
                return false;
            }

            foreach (var relativeCandidate in FileService.GetSlashVariants(path))
            {
                var exactPath = Path.GetFullPath(Path.Combine(workingDirectory, relativeCandidate));
                if (!FileService.IsWithinDirectory(exactPath, workingDirectory))
                {
                    continue;
                }

                var exactInfo = FileService.GetFileInfo(exactPath);
                if (!exactInfo.Exists)
                {
                    continue;
                }

                resolvedPath = exactPath;
                info = exactInfo;
                return true;
            }

            foreach (var relativeCandidate in FileService.GetSlashVariants(path))
            {
                var found = FileService.SearchTree(workingDirectory, relativeCandidate, maxDepth: 5);
                if (string.IsNullOrWhiteSpace(found) || !FileService.IsWithinDirectory(found, workingDirectory))
                {
                    continue;
                }

                var foundInfo = FileService.GetFileInfo(found);
                if (!foundInfo.Exists)
                {
                    continue;
                }

                resolvedPath = found;
                info = foundInfo;
                return true;
            }
        }
        catch
        {
        }

        return false;
    }

    private void RegisterAccessibleResolvedPath(string sessionId, string? workingDirectory, string resolvedPath)
    {
        if (_allowlistService is null || string.IsNullOrWhiteSpace(sessionId))
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(workingDirectory) &&
            FileService.IsWithinDirectory(resolvedPath, workingDirectory))
        {
            return;
        }

        _allowlistService.RegisterPath(sessionId, resolvedPath);
    }

    private static bool IsImageReference(AppServerControlInlineFileReference mention)
    {
        if (!string.IsNullOrWhiteSpace(mention.MimeType) &&
            mention.MimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return !string.IsNullOrWhiteSpace(mention.ResolvedPath) &&
               ImageExtensions.Contains(Path.GetExtension(mention.ResolvedPath));
    }

    private static (string Suffix, int? Line, int? Column) ParseLineInfoSuffix(string source, int offset)
    {
        if (offset < 0 || offset >= source.Length)
        {
            return (string.Empty, null, null);
        }

        var match = LeadingLineInfoPattern.Match(source[offset..]);
        if (!match.Success)
        {
            return (string.Empty, null, null);
        }

        return (
            match.Value,
            int.TryParse(match.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var line) ? line : null,
            int.TryParse(match.Groups[2].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var column) ? column : null);
    }

    private static string NormalizePathCandidate(string path)
    {
        var normalized = (path ?? string.Empty).Trim();
        while (normalized.Length > 1)
        {
            var last = normalized[^1];
            if (TrailingTrimChars.Contains(last))
            {
                normalized = normalized[..^1];
                continue;
            }

            if (last == ':' && TryMatchTrailingLineInfo(normalized, out var lineInfoLength))
            {
                normalized = normalized[..^lineInfoLength];
                continue;
            }

            if (TryTrimUnbalancedBracket(normalized, out var trimmed))
            {
                normalized = trimmed;
                continue;
            }

            break;
        }

        return normalized.TrimEnd();
    }

    private static bool TryMatchTrailingLineInfo(string value, out int length)
    {
        var match = TrailingLineInfoPattern.Match(value);
        if (!match.Success)
        {
            length = 0;
            return false;
        }

        length = match.Length;
        return true;
    }

    private static bool TryTrimUnbalancedBracket(string value, out string trimmed)
    {
        trimmed = value;
        if (value.Length == 0)
        {
            return false;
        }

        return value[^1] switch
        {
            ')' when CountChar(value, ')') > CountChar(value, '(') => TrimTrailingChar(value, out trimmed),
            ']' when CountChar(value, ']') > CountChar(value, '[') => TrimTrailingChar(value, out trimmed),
            '}' when CountChar(value, '}') > CountChar(value, '{') => TrimTrailingChar(value, out trimmed),
            _ => false
        };
    }

    private static bool TrimTrailingChar(string value, out string trimmed)
    {
        trimmed = value[..^1];
        return true;
    }

    private static int CountChar(string input, char target)
    {
        var count = 0;
        foreach (var value in input)
        {
            if (value == target)
            {
                count += 1;
            }
        }

        return count;
    }

    private static bool IsLikelyUrlOrDomain(string value)
    {
        var normalized = value.Trim();
        if (normalized.Length == 0)
        {
            return false;
        }

        if (UrlSchemePattern.IsMatch(normalized))
        {
            return true;
        }

        if (normalized.StartsWith("mailto:", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("tel:", StringComparison.OrdinalIgnoreCase) ||
            normalized.StartsWith("www.", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (IpAddressWithOptionalPortPattern.IsMatch(normalized))
        {
            return true;
        }

        var slashIndex = normalized.IndexOf("/", StringComparison.Ordinal);
        var prefix = slashIndex >= 0 ? normalized[..slashIndex] : normalized;
        var hostPort = TrailingPortPattern.Replace(prefix, string.Empty);
        if (hostPort.Contains('\\', StringComparison.Ordinal) || !hostPort.Contains('.', StringComparison.Ordinal))
        {
            return false;
        }

        if (!HostPortPattern.IsMatch(hostPort))
        {
            return false;
        }

        var labels = hostPort.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (labels.Length < 2 || labels.Any(static label => label.StartsWith('-', StringComparison.Ordinal) || label.EndsWith('-', StringComparison.Ordinal)))
        {
            return false;
        }

        var tld = labels[^1];
        return TldPattern.IsMatch(tld) &&
               CommonTlds.Contains(tld);
    }

    private static bool IsLikelyFalsePositive(string value)
    {
        var normalized = NormalizePathCandidate(value);
        if (string.IsNullOrWhiteSpace(normalized))
        {
            return true;
        }

        if (IsLikelyUrlOrDomain(normalized) ||
            VersionLikeNumberPattern.IsMatch(normalized))
        {
            return true;
        }

        if (new[]
            {
                "e.g.",
                "i.e.",
                "etc.",
                "vs.",
                "inc.",
                "ltd.",
                "co."
            }.Contains(normalized, StringComparer.OrdinalIgnoreCase))
        {
            return true;
        }

        if (!normalized.Contains('/', StringComparison.Ordinal) && !normalized.Contains('\\', StringComparison.Ordinal))
        {
            var dotCount = normalized.Count(static valueChar => valueChar == '.');
            if (dotCount >= 4)
            {
                return true;
            }

            if (dotCount >= 1)
            {
                var extension = normalized.Split('.').LastOrDefault();
                if (!string.IsNullOrWhiteSpace(extension) &&
                    extension.Length >= 5 &&
                    char.IsUpper(extension[0]))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool ShouldRejectRelativeMatch(string value)
    {
        var normalized = NormalizePathCandidate(value);
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.StartsWith("/", StringComparison.Ordinal) ||
            WindowsDrivePrefixPattern.IsMatch(normalized) ||
            normalized.StartsWith(@"\\", StringComparison.Ordinal) ||
            IsLikelyUrlOrDomain(normalized))
        {
            return true;
        }

        return IsLikelyFalsePositive(normalized);
    }

    private static bool ShouldRejectFolderMatch(string value)
    {
        var normalized = NormalizePathCandidate(value);
        if (string.IsNullOrWhiteSpace(normalized) ||
            WindowsDrivePrefixPattern.IsMatch(normalized) ||
            normalized.StartsWith(@"\\", StringComparison.Ordinal) ||
            IsLikelyUrlOrDomain(normalized))
        {
            return true;
        }

        var withoutTrailingSlash = normalized.TrimEnd('/', '\\');
        return !withoutTrailingSlash.Contains('/', StringComparison.Ordinal) &&
               !withoutTrailingSlash.Contains('\\', StringComparison.Ordinal) &&
               UppercaseIdentifierPattern.IsMatch(withoutTrailingSlash);
    }

    private static bool ShouldRejectKnownFileMatch(string value)
    {
        var normalized = NormalizePathCandidate(value);
        return string.IsNullOrWhiteSpace(normalized) ||
               normalized.StartsWith("/", StringComparison.Ordinal) ||
               WindowsDrivePrefixPattern.IsMatch(normalized) ||
               normalized.StartsWith(@"\\", StringComparison.Ordinal) ||
               IsLikelyUrlOrDomain(normalized);
    }

    private static List<AppServerControlInlineFileReference> CloneInlineFileReferences(IReadOnlyList<AppServerControlInlineFileReference>? source)
    {
        if (source is null || source.Count == 0)
        {
            return [];
        }

        return source.Select(static mention => new AppServerControlInlineFileReference
        {
            Field = mention.Field,
            DisplayText = mention.DisplayText,
            Path = mention.Path,
            PathKind = mention.PathKind,
            ResolvedPath = mention.ResolvedPath,
            Exists = mention.Exists,
            IsDirectory = mention.IsDirectory,
            MimeType = mention.MimeType,
            Line = mention.Line,
            Column = mention.Column
        }).ToList();
    }

    private static List<AppServerControlInlineImagePreview> CloneInlineImagePreviews(IReadOnlyList<AppServerControlInlineImagePreview>? source)
    {
        if (source is null || source.Count == 0)
        {
            return [];
        }

        return source.Select(static preview => new AppServerControlInlineImagePreview
        {
            DisplayPath = preview.DisplayPath,
            ResolvedPath = preview.ResolvedPath,
            MimeType = preview.MimeType
        }).ToList();
    }

    private readonly record struct FileMentionCandidate(
        int Start,
        int End,
        string DisplayText,
        string Path,
        string PathKind,
        int? Line,
        int? Column,
        int Priority);
}

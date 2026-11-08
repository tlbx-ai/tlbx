using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Settings;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class ProviderResumeCatalogServiceTests : IDisposable
{
    private readonly string _profile = Path.Combine(Path.GetTempPath(), "tlbx-resume-" + Guid.NewGuid().ToString("N"));
    private readonly string _workspace = Path.Combine(Path.GetTempPath(), "tlbx-resume-workspace");
    private int _fileCounter;

    public void Dispose()
    {
        if (Directory.Exists(_profile))
        {
            Directory.Delete(_profile, recursive: true);
        }
    }

    [Fact]
    public void ConfiguredServiceUserProfileWinsOverTheProcessAndExecutableProfiles()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        var expected = AppServerControlHostEnvironmentResolver.ResolveWindowsProfileDirectory(null, sid);
        Assert.False(string.IsNullOrWhiteSpace(expected));
        var publicProfile = Path.Combine(Path.GetPathRoot(expected!)!, "Users", "Public");
        var settings = new MidTermSettings { RunAsUser = Environment.UserName, RunAsUserSid = sid };

        var resolved = AppServerControlHostEnvironmentResolver.ResolvePreferredWindowsProfileDirectory(
            settings,
            Path.Combine(publicProfile, "AppData", "Roaming", "npm", "codex.cmd"));

        Assert.Equal(expected, resolved, ignoreCase: true);
        Assert.Equal(expected, ProviderResumeCatalogService.ResolveProviderProfileDirectory(settings, "codex"), ignoreCase: true);
        Assert.DoesNotContain("systemprofile", resolved!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WithoutAConfiguredUserTheProviderExecutableOwnerProfileIsUsedBeforeTheProcessProfile()
    {
        if (!OperatingSystem.IsWindows()) return;
        var executableOwnerProfile = Path.Combine(Path.GetPathRoot(Environment.SystemDirectory)!, "Users", "Public");
        if (!Directory.Exists(executableOwnerProfile)) return;

        var resolved = AppServerControlHostEnvironmentResolver.ResolvePreferredWindowsProfileDirectory(
            new MidTermSettings(),
            Path.Combine(executableOwnerProfile, "AppData", "Roaming", "npm", "claude.cmd"));

        Assert.Equal(executableOwnerProfile, resolved, ignoreCase: true);
    }

    [Fact]
    public void CatalogReadsOnlyTheResolvedProfileAndPassesProviderIdsThroughUnchanged()
    {
        var codexId = "019a1b2c-3d4e-7f00-8a9b-0123456789ab";
        var claudeId = Guid.NewGuid().ToString();
        WriteCodex(codexId, _workspace, "Fix the resume picker");
        WriteClaude(claudeId, _workspace, "Explain the scroll anchor");
        string? requestedProvider = null;
        var catalog = new ProviderResumeCatalogService(provider =>
        {
            requestedProvider = provider;
            return _profile;
        });

        var codex = Assert.Single(catalog.GetCandidates("Codex", _workspace, includeAllDirectories: false, ct: TestContext.Current.CancellationToken));
        Assert.Equal("codex", requestedProvider);
        Assert.Equal(codexId, codex.SessionId);
        Assert.Equal("Fix the resume picker", codex.Title);

        var claude = Assert.Single(catalog.GetCandidates("claude", _workspace, includeAllDirectories: false, ct: TestContext.Current.CancellationToken));
        Assert.Equal(claudeId, claude.SessionId);
        Assert.Equal("Explain the scroll anchor", claude.Title);

        Assert.Empty(new ProviderResumeCatalogService(_ => null).GetCandidates("codex", _workspace, includeAllDirectories: true, ct: TestContext.Current.CancellationToken));
    }

    [Fact]
    public void BrokenJsonLinesAreSkippedWithoutHidingOtherConversations()
    {
        var id = Guid.NewGuid().ToString();
        WriteClaudeLines(id, [
            "{not json",
            ClaudeUser(id, _workspace, "Survives broken neighbours", "2026-11-01T10:00:00Z"),
            "\u0000\u0001garbage",
            "[1,2,3]",
            "{\"type\":\"user\",\"message\":{\"content\":42}}",
        ]);
        WriteClaudeLines(Guid.NewGuid().ToString(), ["only", "garbage {", "}"]);
        var catalog = new ProviderResumeCatalogService(_ => _profile);

        var entry = Assert.Single(catalog.GetCandidates("claude", null, includeAllDirectories: true, ct: TestContext.Current.CancellationToken));
        Assert.Equal(id, entry.SessionId);
        Assert.Equal("Survives broken neighbours", entry.Title);
    }

    [Fact]
    public void LargeHistoriesAreReadFromBoundedHeadAndTailWindows()
    {
        var id = Guid.NewGuid().ToString();
        var lines = new List<string>
        {
            CodexMeta(id, _workspace, "2026-11-01T08:00:00Z"),
            CodexEvent("<environment_context>cwd</environment_context>", "2026-11-01T08:00:01Z"),
            CodexEvent("First real prompt", "2026-11-01T08:00:02Z"),
        };
        var filler = JsonSerializer.Serialize(new { type = "response_item", payload = new { type = "reasoning", text = new string('x', 4000) } });
        lines.AddRange(Enumerable.Repeat(filler, 1500));
        var tailTime = DateTime.UtcNow.AddMinutes(-1);
        tailTime = new DateTime(tailTime.Ticks - (tailTime.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
        lines.Add(CodexEvent("Latest prompt from the tail", tailTime.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture)));
        var path = WriteCodexLines(id, lines);
        Assert.True(new FileInfo(path).Length > 20L * (ProviderResumeCatalogService.HeadBytes + ProviderResumeCatalogService.TailBytes));

        var window = ProviderResumeCatalogService.ReadTranscriptWindow(path);
        Assert.NotNull(window);
        Assert.True(window.AllLines.Sum(static line => Encoding.UTF8.GetByteCount(line) + 1)
                    <= ProviderResumeCatalogService.HeadBytes + ProviderResumeCatalogService.TailBytes);

        var entry = Assert.Single(new ProviderResumeCatalogService(_ => _profile).GetCandidates("codex", _workspace, false, ct: TestContext.Current.CancellationToken));
        Assert.Equal(id, entry.SessionId);
        Assert.Equal("First real prompt", entry.Title);
        Assert.Equal("Latest prompt from the tail", entry.PreviewText);
        Assert.Equal(tailTime, entry.UpdatedAtUtc);
    }

    [Fact]
    public void CatalogIsBoundedToTheNewestTranscriptsAndCandidateLimit()
    {
        var unrelated = Path.Combine(Path.GetTempPath(), "tlbx-resume-elsewhere");
        var oldestMatch = WriteCodex(Guid.NewGuid().ToString(), _workspace, "Too old to be scanned", age: TimeSpan.FromDays(30));
        for (var index = 0; index < ProviderResumeCatalogService.MaxScannedFiles; index++)
        {
            WriteCodex(Guid.NewGuid().ToString(), unrelated, FormattableString.Invariant($"Other folder {index}"), age: TimeSpan.FromMinutes(10 + index));
        }

        var catalog = new ProviderResumeCatalogService(_ => _profile);
        Assert.Empty(catalog.GetCandidates("codex", _workspace, includeAllDirectories: false, ct: TestContext.Current.CancellationToken));
        Assert.True(File.Exists(oldestMatch));

        var all = catalog.GetCandidates("codex", null, includeAllDirectories: true, ct: TestContext.Current.CancellationToken);
        Assert.Equal(ProviderResumeCatalogService.MaxCandidates, all.Count);
        Assert.Equal(all.OrderByDescending(static entry => entry.UpdatedAtUtc).Select(static entry => entry.SessionId), all.Select(static entry => entry.SessionId), StringComparer.Ordinal);
    }

    [Fact]
    public void JustFinishedConversationsAreListedWithoutAModificationTimeCooldown()
    {
        var id = Guid.NewGuid().ToString();
        WriteCodex(id, _workspace, "Finished a moment ago", age: TimeSpan.Zero);

        var entry = Assert.Single(new ProviderResumeCatalogService(_ => _profile).GetCandidates("codex", _workspace, false, ct: TestContext.Current.CancellationToken));
        Assert.Equal(id, entry.SessionId);
    }

    [Fact]
    public void ConfiguredCodexHomeReplacesTheProfileCodexDirectory()
    {
        var codexHome = Path.Combine(_profile, "custom-codex-home");
        var id = Guid.NewGuid().ToString();
        WriteTranscript(
            Path.Combine(codexHome, "sessions", "2026", "11", "08", $"rollout-{id}.jsonl"),
            [CodexMeta(id, _workspace, "2026-11-08T08:00:00Z"), CodexResponseUser("From the configured home")],
            null);
        WriteCodex(Guid.NewGuid().ToString(), _workspace, "From the profile default");

        var entry = Assert.Single(new ProviderResumeCatalogService(_ => _profile, () => codexHome)
            .GetCandidates("codex", _workspace, false, ct: TestContext.Current.CancellationToken));
        Assert.Equal(id, entry.SessionId);
    }

    [Fact]
    public void OnlyAFullyQualifiedConfiguredCodexHomeIsUsed()
    {
        var absolute = Path.Combine(_profile, "codex-home");
        Assert.Equal(absolute, ProviderResumeCatalogService.ResolveConfiguredCodexHome(
            new MidTermSettings { CodexEnvironmentVariables = $"OTHER=1\nCODEX_HOME={absolute}" }));
        Assert.Null(ProviderResumeCatalogService.ResolveConfiguredCodexHome(
            new MidTermSettings { CodexEnvironmentVariables = "CODEX_HOME=relative\\codex" }));
        Assert.Null(ProviderResumeCatalogService.ResolveConfiguredCodexHome(new MidTermSettings()));
    }

    [Fact]
    public void DisplayTextIsCompactedStrippedOfControlCharactersAndTruncated()
    {
        Assert.Equal("a b c", ProviderResumeCatalogService.NormalizeDisplayText("  a\r\n\tb\u0007 c  ", 20));
        var truncated = ProviderResumeCatalogService.NormalizeDisplayText(new string('y', 50), 10);
        Assert.Equal(10, truncated!.Length);
        Assert.EndsWith("…", truncated, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    public void RichFirstPromptsPreferReadableProseBeforeTruncation(string provider)
    {
        const string prompt = """
            | Provider | Wert |
            | --- | --- |
            | Codex | 42 |
            [Image #1]
            ![attachment](https://example.invalid/image.png)
            ## **Repair the resume picker**
            Keep `Q:\src\__init__.py` and [the error](https://example.invalid/error).
            """;
        var id = Guid.NewGuid().ToString();
        if (provider == "codex") WriteCodex(id, _workspace, prompt);
        else WriteClaude(id, _workspace, prompt);

        var entry = Assert.Single(new ProviderResumeCatalogService(_ => _profile)
            .GetCandidates(provider, _workspace, false, ct: TestContext.Current.CancellationToken));
        Assert.Equal(id, entry.SessionId);
        Assert.Equal("Repair the resume picker", entry.Title);
        Assert.Contains("Provider · Wert Codex · 42", entry.PreviewText, StringComparison.Ordinal);
        Assert.Contains(@"Q:\src\__init__.py", entry.PreviewText, StringComparison.Ordinal);
        Assert.Contains("the error", entry.PreviewText, StringComparison.Ordinal);
        Assert.DoesNotContain("Image", entry.PreviewText!, StringComparison.Ordinal);
        Assert.DoesNotContain("https://", entry.PreviewText!, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("codex")]
    [InlineData("claude")]
    public void TableOnlyPromptsKeepCellValuesAndAttachmentOnlyPromptsUseTheFolder(string provider)
    {
        var tableId = Guid.NewGuid().ToString();
        var imageId = Guid.NewGuid().ToString();
        const string table = "|Name|Value|\n|---|---:|\n|Pen|19|";
        const string image = "[Image 1]\n![capture](https://example.invalid/image.png)";
        if (provider == "codex")
        {
            WriteCodex(tableId, _workspace, table);
            WriteCodex(imageId, _workspace, image);
        }
        else
        {
            WriteClaude(tableId, _workspace, table);
            WriteClaude(imageId, _workspace, image);
        }
        var entries = new ProviderResumeCatalogService(_ => _profile)
            .GetCandidates(provider, _workspace, false, ct: TestContext.Current.CancellationToken);
        Assert.Equal("Name · Value Pen · 19", Assert.Single(entries, entry => entry.SessionId == tableId).Title);
        Assert.Equal(Path.GetFileName(_workspace), Assert.Single(entries, entry => entry.SessionId == imageId).Title);
    }

    [Fact]
    public void RichDisplayCleanupPreservesEscapedPipeValuesAndTheLatestRealError()
    {
        var id = Guid.NewGuid().ToString();
        WriteCodexLines(id, [
            CodexMeta(id, _workspace, "2026-11-01T08:00:00Z"),
            CodexResponseUser("|Name|Value|\n|---|---|\n|Path|a\\|b|"),
            CodexResponseUser("HTTP 409: <literal_error> Keep __init__.py and code `E_ACCESS`"),
        ]);
        var entry = Assert.Single(new ProviderResumeCatalogService(_ => _profile)
            .GetCandidates("codex", _workspace, false, ct: TestContext.Current.CancellationToken));
        Assert.Equal("Name · Value Path · a|b", entry.Title);
        Assert.Equal("HTTP 409: <literal_error> Keep __init__.py and code E_ACCESS", entry.PreviewText);
    }
    [Fact]
    public void InlineImageMarkersSeparateTheSurroundingWords()
    {
        var id = Guid.NewGuid().ToString();
        WriteCodex(id, _workspace, "Before[Image 1]After");
        var entry = Assert.Single(new ProviderResumeCatalogService(_ => _profile)
            .GetCandidates("codex", _workspace, false, ct: TestContext.Current.CancellationToken));
        Assert.Equal("Before After", entry.Title);
    }
    private string WriteCodex(string id, string cwd, string prompt, TimeSpan? age = null)
    {
        return WriteCodexLines(id, [CodexMeta(id, cwd, "2026-11-01T08:00:00Z"), CodexResponseUser(prompt)], age);
    }

    private string WriteCodexLines(string id, IEnumerable<string> lines, TimeSpan? age = null)
    {
        var directory = Path.Combine(_profile, ".codex", "sessions", "2026", "11", "01");
        return WriteTranscript(Path.Combine(directory, FormattableString.Invariant($"rollout-{_fileCounter++:D4}-{id}.jsonl")), lines, age);
    }

    private void WriteClaude(string id, string cwd, string prompt)
    {
        WriteClaudeLines(id, [ClaudeUser(id, cwd, prompt, "2026-11-01T10:00:00Z")]);
    }

    private void WriteClaudeLines(string id, IEnumerable<string> lines)
    {
        WriteTranscript(Path.Combine(_profile, ".claude", "projects", "workspace", $"{id}.jsonl"), lines, null);
    }

    private static string WriteTranscript(string path, IEnumerable<string> lines, TimeSpan? age)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, string.Join('\n', lines) + "\n");
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow - (age ?? TimeSpan.FromMinutes(5)));
        return path;
    }

    private static string CodexMeta(string id, string cwd, string timestamp)
    {
        return JsonSerializer.Serialize(new { timestamp, type = "session_meta", payload = new { id, cwd, timestamp } });
    }

    private static string CodexResponseUser(string text)
    {
        return JsonSerializer.Serialize(new
        {
            type = "response_item",
            payload = new { type = "message", role = "user", content = new[] { new { type = "input_text", text } } }
        });
    }

    private static string CodexEvent(string message, string timestamp)
    {
        return JsonSerializer.Serialize(new { timestamp, type = "event_msg", payload = new { type = "user_message", message } });
    }

    private static string ClaudeUser(string sessionId, string cwd, string text, string timestamp)
    {
        return JsonSerializer.Serialize(new { type = "user", sessionId, cwd, timestamp, message = new { role = "user", content = text } });
    }
}

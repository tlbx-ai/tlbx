using System.Diagnostics;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Settings;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

[Collection(PathSensitiveEnvironmentCollection.Name)]
public sealed class ProviderResumeNativeCodexTests
{
    [Fact]
    public void NativeMetadataUsesProviderTimeAndNameWithoutMislabelingFirstPromptAsLatest()
    {
        var cwd = Path.GetFullPath("workspace");
        var json = JsonSerializer.Serialize(new[]
        {
            new { id = "older", cwd, name = (string?)null, preview = "First older prompt", latestUserTexts = Array.Empty<string>(), updatedAt = 1 },
            new { id = "newer", cwd, name = (string?)"User title", preview = "First newer prompt", latestUserTexts = new[] { "<environment_context>technical</environment_context>", "Latest **real** prompt" }, updatedAt = 2 }
        });
        var rows = ProviderResumeCatalogService.ParseNativeCodexCatalog(json, cwd);
        Assert.Equal(new[] { "newer", "older" }, rows.Select(row => row.SessionId), StringComparer.Ordinal);
        Assert.Equal("User title", rows[0].Title);
        Assert.Equal("Latest real prompt", rows[0].PreviewText);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(2).UtcDateTime, rows[0].UpdatedAtUtc);
        Assert.Equal("First older prompt", rows[1].Title);
        Assert.Null(rows[1].PreviewText);
        Assert.Empty(ProviderResumeCatalogService.ParseNativeCodexCatalog(json, Path.GetFullPath("other")));
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[{}]")]
    [InlineData("null")]
    public void MalformedHostOutputIsRejected(string json) =>
        Assert.Throws<InvalidDataException>(() => ProviderResumeCatalogService.ParseNativeCodexCatalog(json, null));

    [Fact]
    public void DirectoryScopeUsesHostFilesystemCaseSemantics()
    {
        var cwd = Path.GetFullPath("case-sensitive-workspace");
        var json = JsonSerializer.Serialize(new[] { new { id = "own", cwd, preview = "Title", updatedAt = 1 } });
        var exact = ProviderResumeCatalogService.ParseNativeCodexCatalog(json, cwd);
        Assert.Single(exact);
        var changedCase = ProviderResumeCatalogService.ParseNativeCodexCatalog(json, cwd.ToUpperInvariant());
        Assert.Equal(OperatingSystem.IsWindows() ? 1 : 0, changedCase.Count);
    }

    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData(null)]
    public void UnnamedNativeThreadUsesFirstPromptTitleWithoutFakingLatest(string? name)
    {
        var json = JsonSerializer.Serialize(new[] { new { id = "own", cwd = Path.GetFullPath("workspace"),
            name, preview = "Native first **prompt**", updatedAt = 1 } });
        var row = Assert.Single(ProviderResumeCatalogService.ParseNativeCodexCatalog(json, null));
        Assert.Equal("Native first prompt", row.Title);
        Assert.Null(row.PreviewText);
    }

    [Fact]
    public async Task NativeRefreshDoesNotReuseFileCacheOrAnotherDirectoryResultAndClaudeStaysIndependent()
    {
        var calls = new List<string?>();
        var catalog = new ProviderResumeCatalogService(_ => null, nativeCodex: (cwd, _) =>
        {
            calls.Add(cwd);
            IReadOnlyList<ProviderResumeCatalogEntryDto> rows = [new() { Provider = "codex", SessionId = "native-" + calls.Count }];
            return Task.FromResult<IReadOnlyList<ProviderResumeCatalogEntryDto>?>(rows);
        });
        var ct = TestContext.Current.CancellationToken;
        Assert.Equal("native-1", Assert.Single(await catalog.GetCandidatesAsync("codex", "one", false, ct)).SessionId);
        Assert.Equal("native-2", Assert.Single(await catalog.GetCandidatesAsync("codex", "one", false, ct)).SessionId);
        Assert.Equal("native-3", Assert.Single(await catalog.GetCandidatesAsync("codex", "two", false, ct)).SessionId);
        await catalog.GetCandidatesAsync("codex", null, true, ct);
        Assert.Equal(new string?[] { Path.GetFullPath("one"), Path.GetFullPath("one"), Path.GetFullPath("two"), null }, calls, StringComparer.Ordinal);
        Assert.Empty(await catalog.GetCandidatesAsync("claude", null, true, ct));
        Assert.Equal(4, calls.Count);
    }

    [Fact]
    public async Task NativeFailureFallsBackToBoundedJsonlWithoutChangingClaude()
    {
        using var fake = FakeCodexPathScope.Create();
        var sessions = Path.Combine(fake.Root, ".codex", "sessions");
        Directory.CreateDirectory(sessions);
        await File.WriteAllTextAsync(Path.Combine(sessions, "own.jsonl"),
            """
            {"type":"session_meta","payload":{"id":"fallback-native-id","cwd":"Q:\\own"}}
            {"type":"event_msg","payload":{"type":"user_message","message":"Fallback prompt"}}
            """, TestContext.Current.CancellationToken);
        var catalog = new ProviderResumeCatalogService(_ => fake.Root,
            nativeCodex: (_, _) => Task.FromResult<IReadOnlyList<ProviderResumeCatalogEntryDto>?>(null));
        Assert.Equal("fallback-native-id", Assert.Single(await catalog.GetCandidatesAsync("codex", null, true, TestContext.Current.CancellationToken)).SessionId);
    }

    [Fact]
    public async Task CancelledWaiterDoesNotLaunchAnotherCatalogOrPoisonTheNextRefresh()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var catalog = new ProviderResumeCatalogService(_ => null, nativeCodex: async (_, ct) =>
        {
            calls++;
            started.TrySetResult();
            await release.Task.WaitAsync(ct);
            return Array.Empty<ProviderResumeCatalogEntryDto>();
        });
        var first = catalog.GetCandidatesAsync("codex", null, true, TestContext.Current.CancellationToken);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => catalog.GetCandidatesAsync("codex", null, true, cancelled.Token));
        Assert.Equal(1, calls);
        release.SetResult();
        await first;
        await catalog.GetCandidatesAsync("codex", null, true, TestContext.Current.CancellationToken);
        Assert.Equal(2, calls);
    }

    [Fact]
    public void SystemRequiresExplicitConfiguredIdentityAndInteractiveSidMustMatch()
    {
        var settings = new MidTermSettings();
        Assert.False(ProviderResumeCatalogService.CanLaunchNativeCatalog(settings, true));
        Assert.True(ProviderResumeCatalogService.CanLaunchNativeCatalog(settings, false));
        settings.RunAsUser = "own";
        settings.RunAsUserSid = "S-1-own";
        Assert.True(ProviderResumeCatalogService.CanLaunchNativeCatalog(settings, true));
        Assert.True(ProviderResumeCatalogService.MatchesConfiguredWindowsUser(settings, "S-1-own", "PC\\own"));
        Assert.False(ProviderResumeCatalogService.MatchesConfiguredWindowsUser(settings, "S-1-other", "PC\\own"));
        settings.RunAsUserSid = null;
        Assert.True(ProviderResumeCatalogService.MatchesConfiguredWindowsUser(settings, "any", "PC\\own"));
        Assert.False(ProviderResumeCatalogService.MatchesConfiguredWindowsUser(settings, "any", "PC\\other"));
    }

    [Fact]
    public void SidOnlyNativeCatalogUsesThatProfilesRegistryIdentity()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        var expected = AppServerControlHostEnvironmentResolver.ResolveWindowsProfileDirectory(null, sid);
        Assert.False(string.IsNullOrWhiteSpace(expected));
        Assert.Equal(expected, ProviderResumeCatalogService.ResolveNativeCodexProfileDirectory(new MidTermSettings { RunAsUserSid = sid }));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("helper-error")]
    [InlineData("helper-invalid")]
    [InlineData("helper-hang")]
    public async Task OneShotLaunchCarriesConfiguredScopeAndAlwaysCleansUp(string? scenario)
    {
        using var fake = FakeCodexPathScope.Create();
        var settings = new MidTermSettings
        {
            RunAsUser = Environment.UserName,
            CodexEnvironmentVariables = "CODEX_HOME=" + Path.Combine(fake.Root, "native-home")
        };
        if (OperatingSystem.IsWindows())
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            settings.RunAsUserSid = identity.User!.Value;
        }
        int? pid = null;
        bool Launch(string file, IReadOnlyList<string> args, string cwd,
            IReadOnlyDictionary<string, string?>? environment, IReadOnlyList<string>? path,
            string? user, string? sid, out TtyHostSpawner.RedirectedProcessHandle? handle, out string? failure)
        {
            Assert.Equal(fake.ExecutablePath, file);
            Assert.Equal(new[] { "--codex-thread-list" }, args, StringComparer.Ordinal);
            Assert.Equal(settings.RunAsUser, user);
            Assert.Equal(settings.RunAsUserSid, sid);
            Assert.Equal(settings.CodexEnvironmentVariables, environment!["MIDTERM_APP_SERVER_CONTROL_CODEX_ENVIRONMENT_VARIABLES"]);
            Assert.Equal(cwd, environment["HOME"]);
            Assert.False(environment.ContainsKey("MIDTERM_OWNER_TOKEN"));
            var start = new ProcessStartInfo(file)
            {
                WorkingDirectory = cwd,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args) start.ArgumentList.Add(arg);
            start.Environment["MIDTERM_FAKE_CODEX_CATALOG_SCENARIO"] = scenario;
            var launched = TtyHostSpawner.RedirectedProcessHandle.TryStartOwnedDirect(start, out handle, out failure);
            pid = handle?.Process.Id;
            return launched;
        }
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        if (scenario == "helper-hang")
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ProviderResumeCatalogService.ReadNativeCodexAsync(
                settings, fake.Root, null, Launch, () => true, timeout.Token, () => fake.ExecutablePath));
        else
        {
            var rows = await ProviderResumeCatalogService.ReadNativeCodexAsync(settings, fake.Root, null, Launch,
                () => true, timeout.Token, () => fake.ExecutablePath);
            if (scenario is null) Assert.Equal("native-helper-id", Assert.Single(rows!).SessionId);
            else Assert.Null(rows);
        }
        Assert.NotNull(pid);
        try { using var process = Process.GetProcessById(pid.Value); Assert.True(process.HasExited); }
        catch (ArgumentException) { }
    }
}

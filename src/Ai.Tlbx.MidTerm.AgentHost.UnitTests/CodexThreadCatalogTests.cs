using System.Diagnostics;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Process;
using Ai.Tlbx.MidTerm.UnitTests;
using Xunit;

namespace Ai.Tlbx.MidTerm.AgentHost.UnitTests;

[Collection(PathSensitiveEnvironmentCollection.Name)]
public sealed class CodexThreadCatalogTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OneShotEntryPointAppliesConfiguredHomeOrExitsCleanlyWhenCliMissing(bool missingCli)
    {
        using var fake = FakeCodexPathScope.Create();
        var start = new ProcessStartInfo("dotnet")
        {
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = fake.Root
        };
        start.ArgumentList.Add(MtAgentHostTestPathResolver.ResolveAgentHostDll(AppContext.BaseDirectory));
        start.ArgumentList.Add("--codex-thread-list");
        start.ArgumentList.Add("--cwd");
        start.ArgumentList.Add(fake.Root);
        var configuredHome = Path.Combine(fake.Root, "explicit-home");
        start.Environment["MIDTERM_APP_SERVER_CONTROL_CODEX_ENVIRONMENT_VARIABLES"] = "CODEX_HOME=" + configuredHome +
            "\nPATH=" + (missingCli ? fake.Root : fake.FakeCodexBin);
        start.Environment["PATH"] = fake.Root; // Discovery must use the configured provider PATH.
        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var stderr = BoundedProcessText.DrainTailAsync(process.StandardError, timeout.Token);
        try
        {
            process.StandardInput.Close();
            var output = new BoundedProcessText(process.StandardOutput, 1024 * 1024);
            var line = await output.ReadLineAsync(timeout.Token);
            Assert.Null(await output.ReadLineAsync(timeout.Token));
            await process.WaitForExitAsync(timeout.Token);
            var error = await stderr;
            if (missingCli)
            {
                Assert.Equal(1, process.ExitCode);
                Assert.Null(line);
                Assert.Equal("Codex catalog unavailable.", error.Trim());
                Assert.False(File.Exists(fake.CapturePath));
            }
            else
            {
                Assert.Equal(0, process.ExitCode);
                using var result = JsonDocument.Parse(line!);
                Assert.Equal(1, result.RootElement.GetArrayLength());
                using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(fake.CapturePath, timeout.Token));
                Assert.Equal(configuredHome, capture.RootElement.GetProperty("CodexHome").GetString());
                AssertExited(capture.RootElement.GetProperty("ProcessId").GetInt32());
            }
        }
        finally { await BoundedProcessText.StopAsync(process); }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("noisy")]
    [InlineData("turns-error")]
    [InlineData("turns-hang")]
    [InlineData("turns-overflow")]
    public async Task CatalogUsesReadOnlyProtocolAndReleasesOwnedChild(string? scenario)
    {
        using var fake = FakeCodexPathScope.Create();
        var start = CodexAppServerControlAgentRuntime.CreateCodexAppServerStartInfo(fake.ExecutablePath, fake.Root, null);
        start.Environment["MIDTERM_FAKE_CODEX_CATALOG_SCENARIO"] = scenario;
        start.Environment["CODEX_HOME"] = Path.Combine(fake.Root, "configured-home");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var json = await CodexThreadCatalogCommand.QueryAsync(start, fake.Root, timeout.Token);
        using var result = JsonDocument.Parse(json);
        Assert.Equal(1, result.RootElement.GetArrayLength());
        var row = result.RootElement[0];
        Assert.Equal("catalog-native-thread", row.GetProperty("id").GetString());
        Assert.Equal("Native first prompt", row.GetProperty("preview").GetString());
        var latest = row.GetProperty("latestUserTexts");
        if (scenario is "turns-error" or "turns-hang" or "turns-overflow") Assert.Equal(0, latest.GetArrayLength());
        else
        {
            Assert.Equal(1, latest.GetArrayLength());
            Assert.Equal("Native latest real user prompt", latest[0].GetString());
        }
        using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(fake.CapturePath, timeout.Token));
        var root = capture.RootElement;
        Assert.Equal(Path.Combine(fake.Root, "configured-home"), root.GetProperty("CodexHome").GetString());
        Assert.True(root.GetProperty("InitializeExperimentalApi").GetBoolean());
        using var methods = root.GetProperty("Methods").EnumerateArray();
        Assert.Equal(new[] { "initialize", "initialized", "thread/list", "thread/turns/list" },
            methods.Select(value => value.GetString()), StringComparer.Ordinal);
        var list = root.GetProperty("CatalogListParams");
        Assert.True(list.GetProperty("useStateDbOnly").GetBoolean());
        Assert.Equal("updated_at", list.GetProperty("sortKey").GetString());
        Assert.Equal(fake.Root, list.GetProperty("cwd").GetString());
        Assert.Equal(60, list.GetProperty("limit").GetInt32());
        var turns = root.GetProperty("CatalogTurnsParams");
        Assert.Equal("catalog-native-thread", turns.GetProperty("threadId").GetString());
        Assert.Equal("desc", turns.GetProperty("sortDirection").GetString());
        Assert.Equal("full", turns.GetProperty("itemsView").GetString());
        Assert.Equal(1, turns.GetProperty("limit").GetInt32());
        AssertExited(root.GetProperty("ProcessId").GetInt32());
    }

    [Theory]
    [InlineData("many", 8)]
    [InlineData("many-slow", 0)]
    public async Task FullMetadataPageSurvivesOptionalPreviewBudget(string scenario, int previews)
    {
        using var fake = FakeCodexPathScope.Create();
        var start = CodexAppServerControlAgentRuntime.CreateCodexAppServerStartInfo(fake.ExecutablePath, fake.Root, null);
        start.Environment["MIDTERM_FAKE_CODEX_CATALOG_SCENARIO"] = scenario;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        using var result = JsonDocument.Parse(await CodexThreadCatalogCommand.QueryAsync(start, null, timeout.Token));
        Assert.Equal(60, result.RootElement.GetArrayLength());
        for (var index = 0; index < 60; index++)
        {
            var row = result.RootElement[index];
            Assert.Equal("Native first prompt", row.GetProperty("preview").GetString());
            Assert.Equal(index < previews ? 1 : 0, row.GetProperty("latestUserTexts").GetArrayLength());
        }
        using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(fake.CapturePath, timeout.Token));
        AssertExited(capture.RootElement.GetProperty("ProcessId").GetInt32());
    }

    [Theory]
    [InlineData("hang")]
    [InlineData("error")]
    [InlineData("malformed")]
    [InlineData("overflow")]
    public async Task FailureAndCancellationAlwaysReapTheOwnedChild(string scenario)
    {
        using var fake = FakeCodexPathScope.Create();
        var start = CodexAppServerControlAgentRuntime.CreateCodexAppServerStartInfo(fake.ExecutablePath, fake.Root, null);
        start.Environment["MIDTERM_FAKE_CODEX_CATALOG_SCENARIO"] = scenario;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var error = await Record.ExceptionAsync(() => CodexThreadCatalogCommand.QueryAsync(start, null, timeout.Token));
        Assert.NotNull(error);
        if (scenario == "hang") Assert.IsAssignableFrom<OperationCanceledException>(error);
        else if (scenario == "malformed") Assert.IsAssignableFrom<JsonException>(error);
        else if (scenario == "overflow") Assert.IsType<InvalidDataException>(error);
        else Assert.IsAssignableFrom<IOException>(error);
        using var capture = JsonDocument.Parse(await File.ReadAllTextAsync(fake.CapturePath, TestContext.Current.CancellationToken));
        AssertExited(capture.RootElement.GetProperty("ProcessId").GetInt32());
        using var methods = capture.RootElement.GetProperty("Methods").EnumerateArray();
        Assert.DoesNotContain("thread/start", methods.Select(value => value.GetString()), StringComparer.Ordinal);
    }

    [Fact]
    public async Task StdioReaderBoundsUnterminatedLinesAndRetainsOnlyErrorTail()
    {
        using var stdout = new StringReader(new string('x', 100));
        var reader = new BoundedProcessText(stdout, 99);
        await Assert.ThrowsAsync<InvalidDataException>(() => reader.ReadLineAsync(TestContext.Current.CancellationToken));
        using var stderr = new StringReader(new string('x', 8192) + "end");
        var tail = await BoundedProcessText.DrainTailAsync(stderr, TestContext.Current.CancellationToken);
        Assert.Equal(4096, tail.Length);
        Assert.EndsWith("end", tail, StringComparison.Ordinal);
    }

    private static void AssertExited(int pid)
    {
        try { using var process = Process.GetProcessById(pid); Assert.True(process.HasExited); }
        catch (ArgumentException) { /* Reaped process has no remaining OS entry. */ }
    }
}

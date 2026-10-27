using System.Text.Json;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class SessionWorkspaceTests
{
    [Fact]
    public void CodexStatusRequiresAnUnambiguousActualStatusBlock()
    {
        const string id = "11111111-2222-4333-8444-555555555555";
        const string status = "/status\r\n >_ OpenAI Codex (v0.164.0)\r\n Directory: Q:\\work\r\n Session: ";
        Assert.Equal(id, SessionUpdateStateService.TryExtractCodexStatusResumeId(status + id + "\r\n"));
        Assert.Null(SessionUpdateStateService.TryExtractCodexStatusResumeId("Session: " + id));
        Assert.Null(SessionUpdateStateService.TryExtractCodexStatusResumeId(status + id + "\r\n Session: " + id));
    }

    [Theory]
    [InlineData("To continue this session, run codex resume ")]
    [InlineData("To continue this session, run:\r\n  codex resume ")]
    [InlineData("To resume this session run:\n  codex resume ")]
    public void CodexExitHintSupportsMultilineCliOutput(string marker)
    {
        const string id = "11111111-2222-4333-8444-555555555555";
        Assert.Equal(id, SessionUpdateStateService.TryExtractCodexExitResumeId(marker + id + "\r\nOr run codex resume and select a title."));
    }

    [Fact]
    public void RestoredLayoutRemapsSessionIdsAndKeepsFailedLeavesForRetry()
    {
        var original = new SessionLayoutState
        {
            FocusedSessionId = "old-a",
            Root = new LayoutNode { Type = "split", Direction = "horizontal", Children =
            [new LayoutNode { Type = "leaf", SessionId = "old-a" }, new LayoutNode { Type = "leaf", SessionId = "failed-b" }] }
        };
        var restored = SessionUpdateStateService.RemapWorkspaceLayout(original,
            new Dictionary<string, string>(StringComparer.Ordinal) { ["old-a"] = "new-a" });
        Assert.Equal("new-a", restored.FocusedSessionId);
        Assert.Equal("horizontal", restored.Root!.Direction);
        Assert.Equal(new[] { "new-a", "failed-b" }, restored.Root.Children!.Select(static node => node.SessionId));
        Assert.Equal("old-a", original.Root!.Children![0].SessionId);
    }

    [Theory]
    [InlineData("node", "node codex.js --yolo", "codex", false, true)]
    [InlineData("pwsh", "pwsh.exe", null, false, true)]
    [InlineData("shell", null, "shell", false, true)]
    [InlineData(null, null, null, false, true)]
    [InlineData("node", null, null, false, false)]
    [InlineData("python", "python server.py", null, false, false)]
    [InlineData("claude", "claude", "claude", false, false)]
    [InlineData(null, null, null, true, false)]
    public void SavePreflightRejectsProcessesWithoutReliableRecovery(string? name, string? command,
        string? identity, bool controller, bool expected)
    {
        Assert.Equal(expected, SessionUpdateStateService.CanSaveWorkspaceSession(new SessionDecorationState
        {
            ForegroundName = name, ForegroundCommandLine = command,
            ForegroundProcessIdentity = identity, AppServerControlOnly = controller
        }));
    }

    [Fact]
    public async Task WorkspaceStateIsSeparateFromUpdateStateAndRejectsCorruptSaves()
    {
        var directory = Path.Combine(Path.GetTempPath(), "tlbx-workspace-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var ct = TestContext.Current.CancellationToken;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "state.json"), "{\"kind\":\"web\"}", ct);
            var workspace = new SessionUpdateStateService(directory, "saved-workspace.json");
            Assert.Null(await workspace.ReadSavedStateAsync(ct));
            await File.WriteAllTextAsync(Path.Combine(directory, "saved-workspace.json"),
                "{\"kind\":\"full\",\"sessions\":[{\"sessionId\":\"12345678\",\"topic\":\"Resume me\"}]}", ct);
            Assert.Equal("Resume me", (await workspace.ReadSavedStateAsync(ct))!.Sessions.Single().Topic);
            Assert.Equal("web", (await new SessionUpdateStateService(directory).ReadSavedStateAsync(ct))!.Kind);
            await File.WriteAllTextAsync(Path.Combine(directory, "saved-workspace.json"), "broken", ct);
            await Assert.ThrowsAsync<JsonException>(() => workspace.ReadSavedStateAsync(ct));
        }
        finally
        {
            File.Delete(Path.Combine(directory, "state.json"));
            File.Delete(Path.Combine(directory, "saved-workspace.json"));
            Directory.Delete(directory);
        }
    }
}

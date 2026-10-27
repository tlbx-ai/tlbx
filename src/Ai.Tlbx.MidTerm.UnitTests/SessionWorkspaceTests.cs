using System.Text.Json;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class SessionWorkspaceTests
{
    [Theory]
    [InlineData("node", "node codex.js --yolo", "codex", false, true)]
    [InlineData("pwsh", "pwsh.exe", null, false, true)]
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
        try
        {
            await File.WriteAllTextAsync(Path.Combine(directory, "state.json"), "{\"kind\":\"web\"}");
            var workspace = new SessionUpdateStateService(directory, "saved-workspace.json");
            Assert.Null(await workspace.ReadSavedStateAsync());
            await File.WriteAllTextAsync(Path.Combine(directory, "saved-workspace.json"),
                "{\"kind\":\"full\",\"sessions\":[{\"sessionId\":\"12345678\",\"topic\":\"Resume me\"}]}");
            Assert.Equal("Resume me", (await workspace.ReadSavedStateAsync())!.Sessions.Single().Topic);
            Assert.Equal("web", (await new SessionUpdateStateService(directory).ReadSavedStateAsync())!.Kind);
            await File.WriteAllTextAsync(Path.Combine(directory, "saved-workspace.json"), "broken");
            await Assert.ThrowsAsync<JsonException>(() => workspace.ReadSavedStateAsync());
        }
        finally
        {
            File.Delete(Path.Combine(directory, "state.json"));
            File.Delete(Path.Combine(directory, "saved-workspace.json"));
            Directory.Delete(directory);
        }
    }
}

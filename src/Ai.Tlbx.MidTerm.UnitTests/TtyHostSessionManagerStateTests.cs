using System.Collections.Concurrent;
using System.IO;
using System.Reflection;
using System.Text;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Models.Git;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TtyHostSessionManagerStateTests
{
    [Fact]
    public async Task SetBookmarkId_UnknownSession_ReturnsFalse()
    {
        await using var manager = CreateManager();

        var ok = manager.SetBookmarkId("missing", "bookmark-1");

        Assert.False(ok);
    }

    [Fact]
    public async Task SetBookmarkId_ExistingSession_PopulatesSessionListBookmark()
    {
        await using var manager = CreateManager();
        AddCachedSession(manager, "s1");

        var ok = manager.SetBookmarkId("s1", "history-123");

        Assert.True(ok);
        var dto = manager.GetSessionList().Sessions.Single(s => s.Id == "s1");
        Assert.Equal("history-123", dto.BookmarkId);
    }

    [Fact]
    public async Task ClearBookmarksByHistoryId_RemovesMatchingBookmarksOnly()
    {
        await using var manager = CreateManager();
        AddCachedSession(manager, "s1");
        AddCachedSession(manager, "s2");
        AddCachedSession(manager, "s3");
        manager.SetBookmarkId("s1", "history-a");
        manager.SetBookmarkId("s2", "history-b");
        manager.SetBookmarkId("s3", "history-a");

        var removed = manager.ClearBookmarksByHistoryId("history-a");

        Assert.Equal(2, removed);
        var list = manager.GetSessionList().Sessions.ToDictionary(s => s.Id, s => s.BookmarkId, StringComparer.Ordinal);
        Assert.Null(list["s1"]);
        Assert.Equal("history-b", list["s2"]);
        Assert.Null(list["s3"]);
    }

    [Fact]
    public async Task ClearBookmarksByHistoryId_Whitespace_ReturnsZero()
    {
        await using var manager = CreateManager();
        AddCachedSession(manager, "s1");
        manager.SetBookmarkId("s1", "history-a");

        Assert.Equal(0, manager.ClearBookmarksByHistoryId(" "));
        Assert.Equal("history-a", manager.GetSessionList().Sessions.Single().BookmarkId);
    }

    [Fact]
    public async Task SetSessionNotes_ExistingSession_PopulatesSessionListNotes()
    {
        await using var manager = CreateManager();
        AddCachedSession(manager, "s1");

        var ok = manager.SetSessionNotes("s1", "investigate resize\nfollow-up");

        Assert.True(ok);
        var dto = manager.GetSessionList().Sessions.Single(s => s.Id == "s1");
        Assert.Equal("investigate resize\nfollow-up", dto.Notes);
    }

    [Fact]
    public async Task SetSessionTopic_ExistingSession_PopulatesSessionListTopic()
    {
        await using var manager = CreateManager();
        AddCachedSession(manager, "s1");

        var ok = manager.SetSessionTopic("s1", "DAI test worker");

        Assert.True(ok);
        var dto = manager.GetSessionList().Sessions.Single(s => s.Id == "s1");
        Assert.Equal("DAI test worker", dto.Topic);
    }

    [Fact]
    public async Task SetSessionTopic_NormalizesWhitespaceAndLength()
    {
        await using var manager = CreateManager();
        AddCachedSession(manager, "s1");

        manager.SetSessionTopic("s1", string.Concat("one", Environment.NewLine, new string('x', 140)));

        var dto = manager.GetSessionList().Sessions.Single(s => s.Id == "s1");
        Assert.NotNull(dto.Topic);
        Assert.Equal(120, dto.Topic.Length);
        Assert.StartsWith("one x", dto.Topic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task SetSessionTopic_BlankClearsTopic()
    {
        await using var manager = CreateManager();
        AddCachedSession(manager, "s1");
        manager.SetSessionTopic("s1", "keep this");

        var ok = manager.SetSessionTopic("s1", " ");

        Assert.True(ok);
        var dto = manager.GetSessionList().Sessions.Single(s => s.Id == "s1");
        Assert.Null(dto.Topic);
    }

    [Fact]
    public async Task ApplyDiscoveredHostMetadata_PopulatesSessionListTopic()
    {
        await using var manager = CreateManager();
        var info = AddCachedSession(manager, "s1");
        info.Topic = "MidTerm metadata persistence";

        InvokeApplyDiscoveredHostMetadata(manager, info);

        var dto = manager.GetSessionList().Sessions.Single(s => s.Id == "s1");
        Assert.Equal("MidTerm metadata persistence", dto.Topic);
    }

    [Fact]
    public async Task SetSessionExtraGitReposMetadata_StoresOnlyExtraRepos()
    {
        await using var manager = CreateManager();
        AddCachedSession(manager, "s1");

        var ok = manager.SetSessionExtraGitReposMetadata("s1",
        [
            new GitRepoBinding { RepoRoot = @"Q:\repos\Jpa", Label = "Jpa", Role = "cwd", Source = "auto", IsPrimary = true },
            new GitRepoBinding { RepoRoot = @"Q:\repos\MidTerm", Label = "MidTerm", Role = "target", Source = "manual", IsPrimary = false }
        ]);

        Assert.True(ok);
        var repos = manager.GetPersistedSessionExtraGitRepos("s1");
        var repo = Assert.Single(repos);
        Assert.Equal(@"Q:\repos\MidTerm", repo.RepoRoot);
        Assert.Equal("MidTerm", repo.Label);
        Assert.Equal("target", repo.Role);
        Assert.Equal("manual", repo.Source);
    }

    [Fact]
    public async Task SetSessionNotes_NormalizesToFiveLines()
    {
        await using var manager = CreateManager();
        AddCachedSession(manager, "s1");

        manager.SetSessionNotes("s1", "one\ntwo\nthree\nfour\nfive\nsix");

        var dto = manager.GetSessionList().Sessions.Single(s => s.Id == "s1");
        Assert.Equal("one\ntwo\nthree\nfour\nfive", dto.Notes);
    }

    [Fact]
    public async Task SetSessionNotes_BlankClearsNotes()
    {
        await using var manager = CreateManager();
        AddCachedSession(manager, "s1");
        manager.SetSessionNotes("s1", "keep this");

        var ok = manager.SetSessionNotes("s1", " ");

        Assert.True(ok);
        var dto = manager.GetSessionList().Sessions.Single(s => s.Id == "s1");
        Assert.Null(dto.Notes);
    }

    [Fact]
    public async Task SetAgentControlled_UnknownSession_ReturnsFalse()
    {
        await using var manager = CreateManager();

        var ok = manager.SetAgentControlled("missing", true);

        Assert.False(ok);
    }

    [Fact]
    public async Task SetAgentControlled_ExistingSession_PopulatesSessionListFlag()
    {
        await using var manager = CreateManager();
        AddCachedSession(manager, "s1");

        var ok = manager.SetAgentControlled("s1", true);

        Assert.True(ok);
        var dto = manager.GetSessionList().Sessions.Single(s => s.Id == "s1");
        Assert.True(dto.AgentControlled);
    }

    [Fact]
    public async Task SetAgentControlled_TmuxFamily_CascadesToParentAndSiblings()
    {
        await using var manager = CreateManager();
        AddCachedSession(manager, "root");
        AddCachedSession(manager, "child-a");
        AddCachedSession(manager, "child-b");
        manager.SetTmuxParent("child-a", "root");
        manager.SetTmuxParent("child-b", "root");

        var ok = manager.SetAgentControlled("child-a", true);

        Assert.True(ok);
        var flags = manager.GetSessionList().Sessions.ToDictionary(s => s.Id, s => s.AgentControlled, StringComparer.Ordinal);
        Assert.True(flags["root"]);
        Assert.True(flags["child-a"]);
        Assert.True(flags["child-b"]);
    }

    [Fact]
    public async Task SetTmuxParent_AgentControlledParent_SeedsChildFlag()
    {
        await using var manager = CreateManager();
        AddCachedSession(manager, "root");
        AddCachedSession(manager, "child-a");
        manager.SetAgentControlled("root", true);

        manager.SetTmuxParent("child-a", "root");

        var dto = manager.GetSessionList().Sessions.Single(s => s.Id == "child-a");
        Assert.True(dto.AgentControlled);
    }

    [Fact]
    public async Task GetSessionList_PopulatesForegroundDisplayAndIdentity()
    {
        await using var manager = CreateManager();
        var session = AddCachedSession(manager, "s1");
        session.ForegroundName = "node.exe";
        session.ForegroundCommandLine = "\"C:\\Program Files\\nodejs\\node.exe\" \"C:\\repo\\node_modules\\@openai\\codex\\bin\\codex.js\" --yolo";

        var dto = manager.GetSessionList().Sessions.Single(s => s.Id == "s1");

        Assert.Equal("codex --yolo", dto.ForegroundDisplayName);
        Assert.Equal("codex", dto.ForegroundProcessIdentity);
    }

    [Fact]
    public async Task SetAgentControlled_PersistsAcrossManagerRestart()
    {
        var stateDir = CreateTempDirectory();
        try
        {
            await using (var manager = CreateManager(new SessionControlStateService(stateDir)))
            {
                AddCachedSession(manager, "s1");
                Assert.True(manager.SetAgentControlled("s1", true));
            }

            await using var restartedManager = CreateManager(new SessionControlStateService(stateDir));
            AddCachedSession(restartedManager, "s1");

            var dto = restartedManager.GetSessionList().Sessions.Single(s => s.Id == "s1");
            Assert.True(dto.AgentControlled);
        }
        finally
        {
            Directory.Delete(stateDir, recursive: true);
        }
    }

    [Fact]
    public async Task CloseSessionAsync_RemovesPersistedAgentControlState()
    {
        var stateDir = CreateTempDirectory();
        try
        {
            await using (var manager = CreateManager(new SessionControlStateService(stateDir)))
            {
                AddCachedSession(manager, "s1");
                AddDisconnectedClient(manager, "s1");
                Assert.True(manager.SetAgentControlled("s1", true));

                var closed = await manager.CloseSessionAsync("s1");
                Assert.True(closed);
            }

            await using var restartedManager = CreateManager(new SessionControlStateService(stateDir));
            AddCachedSession(restartedManager, "s1");

            var dto = restartedManager.GetSessionList().Sessions.Single(s => s.Id == "s1");
            Assert.False(dto.AgentControlled);
        }
        finally
        {
            Directory.Delete(stateDir, recursive: true);
        }
    }

    [Fact]
    public async Task SetAppServerControlOnly_PersistsAcrossManagerRestart()
    {
        var stateDir = CreateTempDirectory();
        try
        {
            await using (var manager = CreateManager(new SessionControlStateService(stateDir)))
            {
                AddCachedSession(manager, "s1");
                Assert.True(manager.SetAppServerControlOnly("s1", true));
            }

            await using var restartedManager = CreateManager(new SessionControlStateService(stateDir));
            AddCachedSession(restartedManager, "s1");

            var dto = restartedManager.GetSessionList().Sessions.Single(s => s.Id == "s1");
            Assert.True(dto.AppServerControlOnly);
        }
        finally
        {
            Directory.Delete(stateDir, recursive: true);
        }
    }

    [Fact]
    public async Task SetProfileHint_PersistsAcrossManagerRestart()
    {
        var stateDir = CreateTempDirectory();
        try
        {
            await using (var manager = CreateManager(new SessionControlStateService(stateDir)))
            {
                AddCachedSession(manager, "s1");
                Assert.True(manager.SetProfileHint("s1", "codex"));
            }

            await using var restartedManager = CreateManager(new SessionControlStateService(stateDir));
            AddCachedSession(restartedManager, "s1");

            var dto = restartedManager.GetSessionList().Sessions.Single(s => s.Id == "s1");
            Assert.Equal("codex", dto.ProfileHint);
        }
        finally
        {
            Directory.Delete(stateDir, recursive: true);
        }
    }

    [Fact]
    public async Task SetAppServerControlResumeThreadId_PersistsAcrossManagerRestart()
    {
        var stateDir = CreateTempDirectory();
        try
        {
            await using (var manager = CreateManager(new SessionControlStateService(stateDir)))
            {
                AddCachedSession(manager, "s1");
                Assert.True(manager.SetAppServerControlResumeThreadId("s1", "thread-resume-123"));
            }

            await using var restartedManager = CreateManager(new SessionControlStateService(stateDir));
            AddCachedSession(restartedManager, "s1");

            var dto = restartedManager.GetSessionList().Sessions.Single(s => s.Id == "s1");
            Assert.Equal("thread-resume-123", dto.AppServerControlResumeThreadId);
        }
        finally
        {
            Directory.Delete(stateDir, recursive: true);
        }
    }

    [Fact]
    public async Task SetLaunchOrigin_PersistsAcrossManagerRestart()
    {
        var stateDir = CreateTempDirectory();
        try
        {
            await using (var manager = CreateManager(new SessionControlStateService(stateDir)))
            {
                AddCachedSession(manager, "s1");
                Assert.True(manager.SetLaunchOrigin("s1", SessionLaunchOrigins.Space));
            }

            await using var restartedManager = CreateManager(new SessionControlStateService(stateDir));
            AddCachedSession(restartedManager, "s1");

            Assert.Equal(SessionLaunchOrigins.Space, restartedManager.GetLaunchOrigin("s1"));
            var dto = restartedManager.GetSessionList().Sessions.Single(s => s.Id == "s1");
            Assert.False(dto.IsAdHoc);
        }
        finally
        {
            Directory.Delete(stateDir, recursive: true);
        }
    }

    [Fact]
    public async Task GetSessionList_SpaceLaunchOriginWithoutSpaceId_IsNotAdHoc()
    {
        await using var manager = CreateManager();
        AddCachedSession(manager, "s1");

        Assert.True(manager.SetLaunchOrigin("s1", SessionLaunchOrigins.Space));

        var dto = manager.GetSessionList().Sessions.Single(s => s.Id == "s1");
        Assert.False(dto.IsAdHoc);
    }

    [Fact]
    public async Task GetSessionList_AdHocLaunchOriginWithWorkspacePath_RemainsAdHoc()
    {
        await using var manager = CreateManager();
        AddCachedSession(manager, "s1");

        Assert.True(manager.SetLaunchOrigin("s1", SessionLaunchOrigins.AdHoc));
        Assert.True(manager.SetWorkspacePath("s1", @"Q:\repos\MidTerm"));

        var dto = manager.GetSessionList().Sessions.Single(s => s.Id == "s1");
        Assert.True(dto.IsAdHoc);
        Assert.Null(dto.SpaceId);
        Assert.Equal(@"Q:\repos\MidTerm", dto.WorkspacePath);
    }

    [Fact]
    public async Task SetSessionNameAsync_AutoMode_StoresTerminalTitleOnly()
    {
        await using var manager = CreateManager();
        var info = AddCachedSession(manager, "s1");
        info.Name = "Manual Name";
        info.ManuallyNamed = true;
        AddDisconnectedClient(manager, "s1");

        var ok = await manager.SetSessionNameAsync("s1", "Terminal Title", isManual: false);

        Assert.True(ok);
        Assert.Equal("Terminal Title", info.TerminalTitle);
        Assert.Equal("Manual Name", info.Name);
        Assert.True(info.ManuallyNamed);
    }

    [Fact]
    public async Task SetSessionNameAsync_AutoMode_WhitespaceClearsTerminalTitle()
    {
        await using var manager = CreateManager();
        var info = AddCachedSession(manager, "s1");
        info.TerminalTitle = "Old";
        AddDisconnectedClient(manager, "s1");

        var ok = await manager.SetSessionNameAsync("s1", "   ", isManual: false);

        Assert.True(ok);
        Assert.Null(info.TerminalTitle);
    }

    [Fact]
    public async Task SetSessionNameAsync_AutoMode_ShellPathClearsTerminalTitle()
    {
        await using var manager = CreateManager();
        var info = AddCachedSession(manager, "s1");
        AddDisconnectedClient(manager, "s1");

        var ok = await manager.SetSessionNameAsync("s1", @"C:\Program Files\PowerShell\7\pwsh.exe", isManual: false);

        Assert.True(ok);
        Assert.Null(info.TerminalTitle);
    }

    [Fact]
    public async Task SetSessionNameAsync_ManualMode_WhenSetNameFails_DoesNotUpdateName()
    {
        await using var manager = CreateManager();
        var info = AddCachedSession(manager, "s1");
        info.Name = "Before";
        AddDisconnectedClient(manager, "s1");

        var ok = await manager.SetSessionNameAsync("s1", "After", isManual: true);

        Assert.False(ok);
        Assert.Equal("Before", info.Name);
    }

    [Fact]
    public async Task OscTitleSequence_BelTerminator_UpdatesTerminalTitle()
    {
        await using var manager = CreateManager();
        var info = AddCachedSession(manager, "s1");
        var data = Encoding.UTF8.GetBytes("\u001b]2;Build Running\u0007");

        InvokeHandleClientOutput(manager, "s1", data);

        Assert.Equal("Build Running", info.TerminalTitle);
    }

    [Fact]
    public async Task OscTitleSequence_StTerminator_UpdatesTerminalTitle()
    {
        await using var manager = CreateManager();
        var info = AddCachedSession(manager, "s1");
        var data = Encoding.UTF8.GetBytes("\u001b]0;Window Name\u001b\\");

        InvokeHandleClientOutput(manager, "s1", data);

        Assert.Equal("Window Name", info.TerminalTitle);
    }

    [Fact]
    public async Task OscTitleSequence_ShellExecutablePath_ClearsTerminalTitle()
    {
        await using var manager = CreateManager();
        var info = AddCachedSession(manager, "s1");
        var data = Encoding.UTF8.GetBytes("\u001b]2;C:\\Program Files\\PowerShell\\7\\pwsh.exe\u0007");

        InvokeHandleClientOutput(manager, "s1", data);

        Assert.Null(info.TerminalTitle);
    }

    [Fact]
    public async Task OscCwdSequence_FileUri_UpdatesCurrentDirectoryAndFiresEvent()
    {
        await using var manager = CreateManager();
        var info = AddCachedSession(manager, "s1");
        var seen = new List<string>();
        manager.OnCwdChanged += (_, cwd) => seen.Add(cwd);

        var data = Encoding.UTF8.GetBytes("\u001b]7;file://localhost/C:/Repo%20One\u0007");
        InvokeHandleClientOutput(manager, "s1", data);

        var expected = OperatingSystem.IsWindows() ? @"C:\Repo One" : "/C:/Repo One";
        Assert.Equal(expected, info.CurrentDirectory);
        Assert.Single(seen);
        Assert.Equal(expected, seen[0]);
    }

    [Fact]
    public async Task OscCwdSequence_NonFileUri_IsIgnored()
    {
        await using var manager = CreateManager();
        var info = AddCachedSession(manager, "s1");
        info.CurrentDirectory = @"C:\existing";
        var calls = 0;
        manager.OnCwdChanged += (_, _) => calls++;

        var data = Encoding.UTF8.GetBytes("\u001b]7;https://example.com/repo\u0007");
        InvokeHandleClientOutput(manager, "s1", data);

        Assert.Equal(@"C:\existing", info.CurrentDirectory);
        Assert.Equal(0, calls);
    }

    [Fact]
    public async Task OscCwdSequence_SameDirectoryDifferentCase_DoesNotFireDuplicateEvent()
    {
        await using var manager = CreateManager();
        var info = AddCachedSession(manager, "s1");
        info.CurrentDirectory = OperatingSystem.IsWindows() ? @"C:\Repo One" : "/C:/Repo One";
        var calls = 0;
        manager.OnCwdChanged += (_, _) => calls++;

        var data = Encoding.UTF8.GetBytes("\u001b]7;file://localhost/c:/repo%20one\u0007");
        InvokeHandleClientOutput(manager, "s1", data);

        Assert.Equal(0, calls);
    }

    [Fact]
    public void MergeCachedFields_PreservesMtOwnedAndSparseFields()
    {
        var refreshed = new SessionInfo
        {
            Id = "s1",
            Name = null,
            CurrentDirectory = null,
            ForegroundPid = null,
            ForegroundName = null,
            ForegroundCommandLine = null
        };
        var existing = new SessionInfo
        {
            Id = "s1",
            Name = "User Name",
            TerminalTitle = "Terminal Name",
            ManuallyNamed = true,
            CurrentDirectory = @"C:\Repo",
            ForegroundPid = 1234,
            ForegroundName = "dotnet",
            ForegroundCommandLine = "dotnet test"
        };

        InvokeMergeCachedFields(refreshed, existing);

        Assert.True(refreshed.ManuallyNamed);
        Assert.Equal("Terminal Name", refreshed.TerminalTitle);
        Assert.Equal("User Name", refreshed.Name);
        Assert.Equal(@"C:\Repo", refreshed.CurrentDirectory);
        Assert.Equal(1234, refreshed.ForegroundPid);
        Assert.Equal("dotnet", refreshed.ForegroundName);
        Assert.Equal("dotnet test", refreshed.ForegroundCommandLine);
    }

    [Fact]
    public void ApplyStartupWorkingDirectoryFallback_SeedsMissingCurrentDirectory()
    {
        var info = new SessionInfo
        {
            Id = "s1",
            CurrentDirectory = null
        };

        InvokeApplyStartupWorkingDirectoryFallback(info, @"C:\Repo");

        Assert.Equal(@"C:\Repo", info.CurrentDirectory);
    }

    [Fact]
    public void ApplyStartupWorkingDirectoryFallback_DoesNotOverrideReportedCurrentDirectory()
    {
        var info = new SessionInfo
        {
            Id = "s1",
            CurrentDirectory = @"C:\Actual"
        };

        InvokeApplyStartupWorkingDirectoryFallback(info, @"C:\Requested");

        Assert.Equal(@"C:\Actual", info.CurrentDirectory);
    }

    [Fact]
    public async Task HandleClientForegroundChanged_ForwardsUnchangedForegroundPayloadWithoutStateFanout()
    {
        await using var manager = CreateManager();
        var info = AddCachedSession(manager, "s1");
        info.ForegroundPid = 1234;
        info.ForegroundName = "customproc";
        info.ForegroundCommandLine = "customproc";
        info.ForegroundDisplayName = "customproc";
        info.ForegroundProcessIdentity = "customproc";
        info.CurrentDirectory = @"C:\Repo";

        var foregroundEvents = 0;
        var stateEvents = 0;
        manager.OnForegroundChanged += (_, _) => foregroundEvents++;
        var listenerId = manager.AddStateListener(() => stateEvents++);

        try
        {
            InvokeHandleClientForegroundChanged(manager, "s1", new ForegroundChangePayload
            {
                Pid = 1234,
                Name = "customproc",
                CommandLine = "customproc",
                Cwd = @"C:\Repo"
            });
        }
        finally
        {
            manager.RemoveStateListener(listenerId);
        }

        Assert.Equal(1, foregroundEvents);
        Assert.Equal(0, stateEvents);
    }

    private static TtyHostSessionManager CreateManager(SessionControlStateService? sessionControlStateService = null)
    {
        return new TtyHostSessionManager(
            expectedVersion: "1.0.0",
            minCompatibleVersion: "1.0.0",
            sessionControlStateService: sessionControlStateService);
    }

    private static string CreateTempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "midterm-session-control-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static SessionInfo AddCachedSession(TtyHostSessionManager manager, string sessionId)
    {
        var info = new SessionInfo
        {
            Id = sessionId,
            Pid = 42,
            HostPid = 43,
            ShellType = "Pwsh",
            CreatedAt = DateTime.UtcNow,
            IsRunning = true
        };

        var cache = GetField<ConcurrentDictionary<string, SessionInfo>>(manager, "_sessionCache");
        cache[sessionId] = info;
        return info;
    }

    private static void AddDisconnectedClient(TtyHostSessionManager manager, string sessionId)
    {
        var clients = GetField<ConcurrentDictionary<string, TtyHostClient>>(manager, "_clients");
        clients[sessionId] = new TtyHostClient(sessionId, hostPid: 999999);
    }

    private static void InvokeHandleClientOutput(TtyHostSessionManager manager, string sessionId, byte[] output)
    {
        var method = typeof(TtyHostSessionManager).GetMethod(
            "HandleClientOutput",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        method.Invoke(manager, [sessionId, 0UL, 120, 30, new ReadOnlyMemory<byte>(output)]);
    }

    private static void InvokeHandleClientForegroundChanged(
        TtyHostSessionManager manager,
        string sessionId,
        ForegroundChangePayload payload)
    {
        var method = typeof(TtyHostSessionManager).GetMethod(
            "HandleClientForegroundChanged",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        method.Invoke(manager, [sessionId, payload]);
    }

    private static void InvokeMergeCachedFields(SessionInfo refreshed, SessionInfo existing)
    {
        var method = typeof(TtyHostSessionManager).GetMethod(
            "MergeCachedFields",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        method.Invoke(null, [refreshed, existing]);
    }

    private static void InvokeApplyStartupWorkingDirectoryFallback(SessionInfo info, string? workingDirectory)
    {
        var method = typeof(TtyHostSessionManager).GetMethod(
            "ApplyStartupWorkingDirectoryFallback",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        method.Invoke(null, [info, workingDirectory]);
    }

    private static void InvokeApplyDiscoveredHostMetadata(TtyHostSessionManager manager, SessionInfo info)
    {
        var method = typeof(TtyHostSessionManager).GetMethod(
            "ApplyDiscoveredHostMetadata",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        method.Invoke(manager, [info]);
    }

    private static T GetField<T>(TtyHostSessionManager manager, string name)
    {
        var field = typeof(TtyHostSessionManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (T)field.GetValue(manager)!;
    }
}

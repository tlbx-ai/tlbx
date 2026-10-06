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
    [Theory]
    [InlineData("node C:\\tools\\@openai\\codex\\bin\\codex.js --yolo", true)]
    [InlineData("node C:\\tools\\unrelated.js", false)]
    public async Task CliProofUsesCanonicalProcessIdentityAfterHostReconnect(string commandLine, bool isCodex)
    {
        await using var manager = CreateManager();
        var info = AddCachedSession(manager, "first001");
        info.ForegroundPid = Environment.ProcessId;
        info.ForegroundName = "node";
        info.ForegroundCommandLine = commandLine;
        // Preserved hosts omit mt-owned classification in their GetInfo snapshot.
        Assert.Null(info.ForegroundProcessIdentity);
        Assert.Equal(isCodex, manager.GetSessionList().Sessions.Single().ForegroundProcessIdentity == "codex");
        var cli = new SessionCliContextService(manager);
        const string root = "01a10e58-53de-74a2-bc92-95f64070cd3c";
        var proof = cli.Resolve(root, root, Environment.ProcessId).Split('\n')[0];
        InvokeHandleClientOutput(manager, info.Id, Encoding.UTF8.GetBytes(proof));
        var resolution = cli.Resolve(root, root, Environment.ProcessId);
        if (isCodex) Assert.StartsWith(info.Id + ":", resolution, StringComparison.Ordinal);
        else Assert.Contains("TLBX_CONTEXT_UNBOUND", resolution, StringComparison.Ordinal);
    }


    [Fact]
    public async Task SetSessionExtraGitReposMetadataAsync_DoesNotMutateCacheWithoutHostAck()
    {
        await using var manager = CreateManager();
        AddCachedSession(manager, "s1");

        var ok = await manager.SetSessionExtraGitReposMetadataAsync("s1",
        [
            new TtyHostGitRepoMetadata { RepoRoot = @"Q:\repos\tlbx", Label = "tlbx", Role = "target", Source = "manual" }
        ]);

        Assert.False(ok);
        Assert.Empty(manager.GetPersistedSessionExtraGitRepos("s1"));
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

    [Theory]
    [InlineData(30, 31)]
    [InlineData(100, 99)]
    public async Task ExecuteRedrawPulseAsync_PulsesOneRowAndRestoresCanonicalGeometry(
        int canonicalRows,
        int expectedPulseRows)
    {
        var calls = new List<(int Cols, int Rows, bool CanBeCanceled)>();
        using var cancellation = new CancellationTokenSource();

        var success = await TtyHostSessionManager.ExecuteRedrawPulseAsync(
            120,
            canonicalRows,
            (cols, rows, token) =>
            {
                calls.Add((cols, rows, token.CanBeCanceled));
                return Task.FromResult(true);
            },
            cancellation.Token);

        Assert.True(success);
        Assert.Equal(
            [(120, expectedPulseRows, true), (120, canonicalRows, false)],
            calls);
    }

    [Fact]
    public async Task ExecuteRedrawPulseAsync_WhenPulseFails_StillRestoresCanonicalGeometry()
    {
        var calls = new List<(int Cols, int Rows)>();

        var success = await TtyHostSessionManager.ExecuteRedrawPulseAsync(
            120,
            30,
            (cols, rows, _) =>
            {
                calls.Add((cols, rows));
                return Task.FromResult(calls.Count > 1);
            },
            CancellationToken.None);

        Assert.False(success);
        Assert.Equal([(120, 31), (120, 30)], calls);
    }

    [Fact]
    public async Task RedrawSessionAsync_UnknownSession_DoesNotRetainResizeState()
    {
        await using var manager = CreateManager();

        var success = await manager.RedrawSessionAsync("not-a-session");

        Assert.False(success);
        var gates = GetField<ConcurrentDictionary<string, SemaphoreSlim>>(manager, "_resizeGates");
        Assert.Empty(gates);
    }

    [Fact]
    public async Task CloseSessionAsync_WaitsForResizeTransactionThenRemovesItsGate()
    {
        await using var manager = CreateManager();
        AddCachedSession(manager, "s1");
        AddDisconnectedClient(manager, "s1");
        Assert.False(await manager.ResizeSessionAsync("s1", 120, 30));

        var gates = GetField<ConcurrentDictionary<string, SemaphoreSlim>>(manager, "_resizeGates");
        var resizeGate = gates["s1"];
        await resizeGate.WaitAsync();
        var closeTask = manager.CloseSessionAsync("s1");
        try
        {
            Assert.False(closeTask.IsCompleted);
        }
        finally
        {
            resizeGate.Release();
        }

        Assert.True(await closeTask);
        Assert.False(gates.ContainsKey("s1"));
    }

    [Fact]
    public async Task HandleClientOutput_DuringRedraw_ForwardsCanonicalGeometry()
    {
        await using var manager = CreateManager();
        var forwarded = new List<(int Cols, int Rows)>();
        manager.OnOutput += (_, _, cols, rows, _) => forwarded.Add((cols, rows));

        var overrides = GetField<ConcurrentDictionary<string, TtyHostSessionManager.TerminalDimensions>>(
            manager,
            "_redrawDimensionOverrides");
        overrides["s1"] = new TtyHostSessionManager.TerminalDimensions(120, 30);

        InvokeHandleClientOutput(manager, "s1", [], cols: 120, rows: 31);

        Assert.Equal([(120, 30)], forwarded);
    }

    [Fact]
    public async Task CacheRefreshedSessionInfo_DuringRedraw_PreservesCanonicalGeometry()
    {
        await using var manager = CreateManager();
        var existing = AddCachedSession(manager, "s1");
        existing.Cols = 120;
        existing.Rows = 30;

        var overrides = GetField<ConcurrentDictionary<string, TtyHostSessionManager.TerminalDimensions>>(
            manager,
            "_redrawDimensionOverrides");
        overrides["s1"] = new TtyHostSessionManager.TerminalDimensions(120, 30);
        var pulseSnapshot = new SessionInfo { Id = "s1", Cols = 120, Rows = 31 };

        InvokeCacheRefreshedSessionInfo(manager, "s1", pulseSnapshot);

        Assert.Equal(120, pulseSnapshot.Cols);
        Assert.Equal(30, pulseSnapshot.Rows);
        Assert.Same(pulseSnapshot, manager.GetSession("s1"));
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

    private static void InvokeHandleClientOutput(
        TtyHostSessionManager manager,
        string sessionId,
        byte[] output,
        int cols = 120,
        int rows = 30)
    {
        var method = typeof(TtyHostSessionManager).GetMethod(
            "HandleClientOutput",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        method.Invoke(manager, [sessionId, 0UL, cols, rows, new ReadOnlyMemory<byte>(output)]);
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

    private static void InvokeCacheRefreshedSessionInfo(
        TtyHostSessionManager manager,
        string sessionId,
        SessionInfo refreshed)
    {
        var method = typeof(TtyHostSessionManager).GetMethod(
            "CacheRefreshedSessionInfo",
            BindingFlags.Instance | BindingFlags.NonPublic)!;

        method.Invoke(manager, [sessionId, refreshed]);
    }

    private static void InvokeMergeCachedFields(SessionInfo refreshed, SessionInfo existing)
    {
        var method = typeof(TtyHostSessionManager).GetMethod(
            "MergeCachedFields",
            BindingFlags.Static | BindingFlags.NonPublic)!;

        method.Invoke(null, [refreshed, existing]);
    }

    private static T GetField<T>(TtyHostSessionManager manager, string name)
    {
        var field = typeof(TtyHostSessionManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (T)field.GetValue(manager)!;
    }
}

using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.AgentHost.UnitTests;

public sealed class HistoryReplayTests : IDisposable
{
    private const string Session = "replay";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tlbx-replay-tests", Guid.NewGuid().ToString("N"));
    private readonly SessionAppServerControlHistoryService _history;

    public HistoryReplayTests() => _history = new(storeDirectory: _directory, enableScreenLogging: false);

    [Fact]
    public void BufferedSessionFallbackNoticeCannotReplaceReadyProviderIdentityButExplicitThreadMetadataCan()
    {
        var ready = Event("session.ready", "");
        ready.ThreadId = "provider/opaque!id";
        ready.SessionState = new() { State = "ready", StateLabel = "Ready" };
        var bound = Event("thread.started", "");
        bound.ThreadId = ready.ThreadId;
        bound.ThreadState = new() { ProviderThreadId = ready.ThreadId, State = "active", StateLabel = "Active" };
        var buffered = Event("agent.state", "");
        buffered.ThreadId = Session;
        buffered.RuntimeNoticeOnly = new() { Message = "MCP startup completed before provider binding" };

        _history.AppendInitialReplay([ready, bound, buffered]);

        var snapshot = _history.GetSnapshotWindow(Session)!;
        Assert.Equal(ready.ThreadId, snapshot.Thread.ThreadId);
        Assert.Equal("ready", snapshot.Session.State);
        Assert.Single(snapshot.Notices);
        Assert.Empty(snapshot.History);
        _history.Append(buffered);
        Assert.Equal(ready.ThreadId, _history.GetSnapshotWindow(Session)!.Thread.ThreadId);

        var replacement = Event("thread.started", "");
        replacement.ThreadId = Session; // Explicit metadata, not the header, selects the new identity.
        replacement.ThreadState = new() { ProviderThreadId = "next-provider:opaque", State = "active" };
        _history.Append(replacement);
        Assert.Equal("next-provider:opaque", _history.GetSnapshotWindow(Session)!.Thread.ThreadId);
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public void InitialReplayRetainsArchiveButPublishesOneBoundedTailWithPendingExternalIds(int count)
    {
        var events = new List<AppServerControlProviderEvent> { Started("past") };
        for (var i = 0; i < count; i++) events.Add(Item("past", $"tool-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}", "command_execution", "completed", "Completed command"));
        events.Add(Completed("past"));
        events.Add(Started("current"));
        events.Add(Item("current", "external/active", "command_execution", "running", "Current work"));
        var pending = Event("request.opened", "older");
        pending.RequestId = "external?approval";
        pending.RequestOpened = new() { RequestType = "command_execution_approval" };
        events.Add(pending);
        var usage = Event("agent.metadata.updated", "current");
        usage.AgentMetadataUpdated = new() { Usage = new() { InputTokens = 123456 } };
        events.Add(usage);
        using var patches = _history.SubscribeHistoryPatches(Session, TestContext.Current.CancellationToken);
        using var raw = _history.SubscribeProviderEvents(Session, 0, TestContext.Current.CancellationToken);

        _history.AppendInitialReplay(events);

        Assert.True(patches.Reader.TryRead(out var patch));
        Assert.False(patches.Reader.TryRead(out _));
        Assert.False(raw.Reader.TryRead(out _)); // Initial history is imported, not replayed as live raw events.
        Assert.Equal(events.Count, patch!.LatestSequence);
        Assert.Equal(count + 2, patch.HistoryCount);
        Assert.Equal(80, patch.HistoryUpserts.Count);
        Assert.True(patch.ItemUpserts.Count <= 81);
        Assert.Contains(patch.ItemUpserts, item => item.ItemId == "external/active");
        Assert.Contains(patch.RequestUpserts, request => request.RequestId == "external?approval" && request.State == "open");
        Assert.Equal(123456, patch.Session.Usage!.InputTokens);
        Assert.Equal("current", patch.CurrentTurn.TurnId);
        var json = JsonSerializer.Serialize(patch, AppServerControlHostJsonContext.Default.AppServerControlHistoryPatch);
        Assert.True(json.Length < 500_000);
        var first = _history.GetSnapshotWindow(Session, 0, 1)!;
        Assert.Equal(count + 2, first.HistoryCount);
        Assert.Equal("tool:tool-0", first.History[0].EntryId);
        Assert.Equal($"tool:tool-{(count / 2).ToString(System.Globalization.CultureInfo.InvariantCulture)}", _history.GetSnapshotWindow(Session, count / 2, 1)!.History[0].EntryId);

        var resolved = Event("request.resolved", "older");
        resolved.RequestId = pending.RequestId;
        resolved.RequestResolved = new() { Decision = "approved" };
        _history.Append(resolved);
        Assert.True(patches.Reader.TryRead(out var live));
        Assert.Single(live!.RequestUpserts);
        Assert.NotEqual("open", live.RequestUpserts[0].State, StringComparer.Ordinal);
        Assert.True(raw.Reader.TryRead(out var liveRaw));
        Assert.Equal(resolved.EventId, liveRaw!.EventId);
        Assert.Equal(events.Count + 1, live.LatestSequence);
    }

    [Fact]
    public void ManyCompletedTurnsKeepTheirOwnUserOrderAndOnlyFinalTurnStreams()
    {
        const int turns = 10_000;
        var events = new List<AppServerControlProviderEvent>();
        for (var i = 0; i < turns; i++)
        {
            var turn = $"turn-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            events.Add(Started(turn));
            events.Add(Item(turn, $"user-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}", "user_message", "completed", $"question-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
            events.Add(Item(turn, $"tool-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}", "command_execution", "completed", "done"));
            events.Add(Assistant(turn, $"assistant-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}", $"answer-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}"));
            events.Add(Completed(turn));
        }
        _history.AppendInitialReplay(events);
        var last = _history.GetSnapshotWindow(Session, turns * 3 - 3, 3)!;
        Assert.Equal(turns * 3, last.HistoryCount);
        Assert.Equal(new[] { "user", "tool", "assistant" }, last.History.Select(row => row.Kind), StringComparer.Ordinal);
        Assert.Equal($"answer-{(turns - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)}", last.Streams.AssistantText);
        Assert.All(last.History, row => Assert.False(row.Streaming));
        Assert.Equal("user:turn-0", _history.GetSnapshotWindow(Session, 0, 1)!.History[0].EntryId);
    }

    [Fact]
    public void ReplayKeepsProvisionalAdoptionUserPromotionAndReplacementStreamOrder()
    {
        var provisional = Event("content.delta", "turn");
        provisional.ContentDelta = new() { StreamKind = "command_output", Delta = "provisional output" };
        var user = Item("turn", "local-user:turn", "user_message", "completed", "Question");
        _history.AppendInitialReplay([
            Started("turn"), provisional,
            Item("turn", "native/tool", "command_execution", "completed", "native result"), user,
            Item("turn", "native/user", "user_message", "completed", "Question"),
            Assistant("turn", "a", "first"), Assistant("turn", "b", "second"),
            Assistant("turn", "a", "FIRST"), Completed("turn")
        ]);
        var snapshot = _history.GetSnapshot(Session)!;
        Assert.Equal("user:turn", snapshot.History[0].EntryId);
        Assert.Equal("tool:native/tool", Assert.Single(snapshot.History.Where(row => row.Kind == "tool")).EntryId);
        Assert.DoesNotContain(snapshot.History, row => row.EntryId.Contains("command_output:", StringComparison.Ordinal));
        Assert.Equal("FIRSTsecond", snapshot.Streams.AssistantText);
        Assert.All(snapshot.History, row => Assert.False(row.Streaming));
        Assert.Equal(snapshot.History.Count, snapshot.History.Select(row => row.Order).Distinct().Count());
        Assert.Single(snapshot.History.Where(row => row.Kind == "user"));
        Assert.Contains("native/user", Assert.Single(snapshot.Items.Where(item => item.ItemType == "user_message")).AliasItemIds, StringComparer.Ordinal);
    }

    [Fact]
    public void StreamTailBudgetPreservesCanonicalBodiesAndReplacementPosition()
    {
        const int budget = 262144;
        const string marker = "... earlier output omitted ...\n";
        var events = new List<AppServerControlProviderEvent> { Started("turn") };
        var bodies = Enumerable.Range(0, 100).Select(i => $"message-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}:" + new string('x', 3990)).ToArray();
        for (var i = 0; i < bodies.Length; i++) events.Add(Assistant("turn", $"a-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}", bodies[i]));
        bodies[99] = "replaced final message";
        events.Add(Assistant("turn", "a-99", bodies[99]));
        _history.AppendInitialReplay(events);
        var allText = string.Concat(bodies);
        var expected = marker + allText[^(budget - marker.Length)..];
        var snapshot = _history.GetSnapshot(Session)!;
        Assert.Equal(expected, snapshot.Streams.AssistantText);
        Assert.Equal(100, snapshot.HistoryCount);
        Assert.Equal(bodies[0], snapshot.History[0].Body);
        Assert.Equal(bodies[99], snapshot.History[^1].Body);
    }

    [Fact]
    public void TurnIndexesFollowPatchReorderRemovalResetAndPersistedRecovery()
    {
        _history.AppendInitialReplay([Started("turn"), Assistant("turn", "a", "A"), Assistant("turn", "b", "B")]);
        var snapshot = _history.GetSnapshot(Session)!;
        var a = snapshot.History.Single(row => row.EntryId == "assistant:a");
        var b = snapshot.History.Single(row => row.EntryId == "assistant:b");
        a.Order = b.Order + 1;
        _history.ApplyHistoryPatch(new()
        {
            SessionId = Session, LatestSequence = snapshot.LatestSequence + 1,
            CurrentTurn = snapshot.CurrentTurn, HistoryUpserts = [a], HistoryRemovals = [b.EntryId]
        });
        _history.Append(Assistant("turn", "a", "updated"));
        Assert.Equal("updated", _history.GetSnapshot(Session)!.Streams.AssistantText);

        // Import/reset must discard the derived indices as well as canonical rows.
        _history.ImportHistoryWindow(new() { SessionId = Session, LatestSequence = 100 });
        _history.AppendInitialReplay([Started("new"), Assistant("new", "c", "C"), Completed("new")]);
        var reset = _history.GetSnapshot(Session)!;
        Assert.Equal("assistant:c", Assert.Single(reset.History).EntryId);
        Assert.Equal("C", reset.Streams.AssistantText);

        // Exercise the actual cold-state loader with the same persisted DTO shape.
        var recoveryDirectory = Path.Combine(_directory, "recovery");
        Directory.CreateDirectory(recoveryDirectory);
        File.WriteAllText(Path.Combine(recoveryDirectory, Session + ".json"), JsonSerializer.Serialize(new
        {
            nextSequence = reset.LatestSequence,
            state = new
            {
                reset.Provider, reset.Session, reset.Thread, reset.CurrentTurn, reset.QuickSettings, reset.Streams,
                historyEntries = reset.History, reset.Items, reset.Requests, reset.Notices,
                toolRenderStates = Array.Empty<object>(), nextHistoryOrder = 2
            }
        }, JsonSerializerOptions.Web));
        var recovered = new SessionAppServerControlHistoryService(storeDirectory: recoveryDirectory, enableScreenLogging: false);
        try
        {
            recovered.Append(Assistant("new", "c", "Recovered"));
            Assert.Equal("Recovered", recovered.GetSnapshot(Session)!.Streams.AssistantText);
            recovered.Append(Completed("new"));
            Assert.False(Assert.Single(recovered.GetSnapshot(Session)!.History).Streaming);
        }
        finally { recovered.Forget(Session); }
    }

    [Fact]
    public async Task GoalAndAcknowledgedDefaultsSurviveBoundedInitialReplayWindowsPatchesAndReload()
    {
        var events = new List<AppServerControlProviderEvent> { Started("past") };
        for (var i = 0; i < 100; i++)
            events.Add(Item("past", $"tool-{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}", "command_execution", "completed", "Completed command"));
        events.Add(Completed("past"));
        var goal = Event("thread.goal.changed", "past");
        goal.GoalUpdated = new() { ThreadId = "thread", Confirmation = "confirmed",
            Goal = new() { Objective = "Keep", Status = "active", Iterations = 1, LastReason = "Waiting" } };
        events.Add(goal);
        var settings = Event("quick-settings.updated", "past");
        settings.QuickSettingsUpdated = new() { Model = "accepted-model", PermissionMode = "auto", AcknowledgedFields = ["model", "permissionMode"] };
        events.Add(settings);
        using var patches = _history.SubscribeHistoryPatches(Session, TestContext.Current.CancellationToken);
        _history.AppendInitialReplay(events);
        Assert.True(patches.Reader.TryRead(out var patch));
        Assert.False(patches.Reader.TryRead(out _));
        Assert.Equal(100, patch!.HistoryCount);
        Assert.True(patch.HistoryUpserts.Count <= 80);
        Assert.Equal("Keep", patch.Thread.Goal!.Goal!.Objective);
        Assert.Contains("permissionMode", patch.QuickSettings.AcknowledgedFields, StringComparer.Ordinal);
        goal.GoalUpdated.Goal!.Objective = "mutated source";
        settings.QuickSettingsUpdated.AcknowledgedFields.Clear();
        patch.QuickSettings.AcknowledgedFields.Clear();
        Assert.Contains("model", _history.GetSnapshotWindow(Session, 0, 1)!.QuickSettings.AcknowledgedFields, StringComparer.Ordinal);
        patch.Thread.Goal.Goal.Objective = "mutated patch";
        Assert.Equal("Keep", _history.GetSnapshotWindow(Session, 0, 1)!.Thread.Goal!.Goal!.Objective);
        Assert.Equal("Keep", _history.GetSnapshotWindow(Session, 99, 1)!.Thread.Goal!.Goal!.Objective);
        var cloneDirectory = Path.Combine(_directory, "import");
        var imported = new SessionAppServerControlHistoryService(storeDirectory: cloneDirectory, enableScreenLogging: false);
        try
        {
            imported.ImportHistoryWindow(_history.GetSnapshotWindow(Session, 0, 1)!);
            Assert.Equal("Keep", imported.GetSnapshot(Session)!.Thread.Goal!.Goal!.Objective);
            Assert.Contains("model", imported.GetSnapshot(Session)!.QuickSettings.AcknowledgedFields, StringComparer.Ordinal);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            while (!File.Exists(Path.Combine(_directory, Session + ".json"))) await Task.Delay(20, timeout.Token);
            var loaded = new SessionAppServerControlHistoryService(storeDirectory: _directory, enableScreenLogging: false);
            try
            {
                var restored = loaded.GetSnapshotWindow(Session, 0, 1)!;
                Assert.Equal("Keep", restored.Thread.Goal!.Goal!.Objective);
                Assert.Equal(1, restored.Thread.Goal.Goal.Iterations);
                Assert.Equal("auto", restored.QuickSettings.PermissionMode);
                Assert.Contains("permissionMode", restored.QuickSettings.AcknowledgedFields, StringComparer.Ordinal);
                _history.Append(new() { SessionId = Session, Provider = "codex", ThreadId = "thread", Type = "thread.goal.changed",
                    GoalUpdated = new() { ThreadId = "thread", Confirmation = "confirmed", Goal = null } });
                Assert.True(patches.Reader.TryRead(out var cleared));
                imported.ApplyHistoryPatch(cleared!);
                Assert.Null(imported.GetSnapshot(Session)!.Thread.Goal!.Goal);
                Assert.Contains("model", imported.GetSnapshot(Session)!.QuickSettings.AcknowledgedFields, StringComparer.Ordinal);
            }
            finally { loaded.Forget(Session); }
        }
        finally { imported.Forget(Session); }
    }

    private static AppServerControlProviderEvent Event(string type, string turn) => new()
    {
        SessionId = Session, Provider = "codex", ThreadId = "thread", TurnId = turn,
        EventId = Guid.NewGuid().ToString("N"), Type = type, CreatedAt = DateTimeOffset.UtcNow
    };

    private static AppServerControlProviderEvent Started(string turn)
    {
        var e = Event("turn.started", turn);
        e.TurnStarted = new() { IsReplay = true };
        return e;
    }

    private static AppServerControlProviderEvent Completed(string turn)
    {
        var e = Event("turn.completed", turn);
        e.TurnCompleted = new() { State = "completed" };
        return e;
    }

    private static AppServerControlProviderEvent Item(string turn, string id, string type, string status, string detail)
    {
        var e = Event("item.completed", turn);
        e.ItemId = id;
        e.Item = new() { ItemType = type, Status = status, Detail = detail };
        return e;
    }

    private static AppServerControlProviderEvent Assistant(string turn, string id, string text)
    {
        var e = Event("content.delta", turn);
        e.ItemId = id;
        e.ContentDelta = new() { StreamKind = "assistant_text", Delta = text, Replace = true };
        return e;
    }

    public void Dispose()
    {
        _history.Forget(Session);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

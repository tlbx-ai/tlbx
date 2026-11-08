using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.AgentHost.UnitTests;

public sealed class HistoryReplayTests : IDisposable
{
    private const string Session = "replay";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tlbx-replay-tests", Guid.NewGuid().ToString("N"));
    private readonly SessionAppServerControlHistoryService _history;
    private readonly List<SessionAppServerControlHistoryService> _recovered = [];

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
    public void GoalAndAcknowledgedDefaultsSurviveBoundedInitialReplayWindowsPatchesAndReload()
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
            _history.FlushPersistence(Session);
            var loaded = Recover();
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

    [Fact]
    public void JournalRecoversActiveToolAccumulatorRequestsGoalAndSettingsAcrossColdCheckpoints()
    {
        var goal = Event("thread.goal.changed", "turn");
        goal.GoalUpdated = new() { ThreadId = "thread", Confirmation = "confirmed", Goal = new() { Objective = "Keep going", Status = "active" } };
        var settings = Event("quick-settings.updated", "turn");
        settings.QuickSettingsUpdated = new() { Model = "accepted", PermissionMode = "manual", AcknowledgedFields = ["model", "permissionMode"] };
        var request = Event("request.opened", "turn");
        request.RequestId = "external/approval";
        request.RequestOpened = new() { RequestType = "mcp_tool_approval", Detail = new string('x', 6000) };
        var notice = Event("runtime.notice", "turn");
        notice.RuntimeNoticeOnly = new() { Message = "Retained notice" };
        _history.AppendInitialReplay([Started("turn"), Item("turn", "tool", "command_execution", "running", "Run marker"),
            Output("tool", "first\npar"), goal, settings, request, notice]);
        _history.FlushPersistence(Session);
        var directory = CopyStore(_directory);
        var recovered = OpenStore(directory);
        AssertSnapshotsEqual(_history, recovered);
        Assert.Equal(6000, Assert.Single(recovered.GetSnapshot(Session)!.Requests).Detail!.Length);

        var continuation = Output("tool", "tial\nlast\n");
        _history.Append(continuation);
        recovered.Append(continuation);
        var decision = Event("request.resolved", "turn");
        decision.RequestId = request.RequestId;
        decision.RequestResolved = new() { Decision = "accept" };
        _history.Append(decision);
        recovered.Append(decision);
        AssertSnapshotsEqual(_history, recovered);
        Assert.Equal(2048, Assert.Single(recovered.GetSnapshot(Session)!.Requests).Detail!.Length);
        Assert.Contains("partial", recovered.GetSnapshot(Session)!.History.Single(row => row.Kind == "tool").Body, StringComparison.Ordinal);
        recovered.FlushPersistence(Session);

        var checkpointed = Recover(directory);
        AssertSnapshotsEqual(recovered, checkpointed);
        var completed = Item("turn", "tool", "command_execution", "completed", "Finished");
        recovered.Append(completed);
        checkpointed.Append(completed);
        AssertSnapshotsEqual(recovered, checkpointed);
        Assert.False(checkpointed.GetSnapshot(Session)!.History.Single(row => row.Kind == "tool").Streaming);
    }

    [Fact]
    public void SameSequenceImportPatchResetAndBacklogHaveTheirOwnDurableOrder()
    {
        var window = new AppServerControlHistoryWindowResponse
        {
            SessionId = Session, LatestSequence = 100, Provider = "codex",
            History = [new() { EntryId = "original", Order = 7, Kind = "assistant", Body = "old" }]
        };
        _history.ImportHistoryWindow(window);
        _history.ApplyHistoryPatch(new() { SessionId = Session, LatestSequence = 100, Provider = "codex",
            HistoryRemovals = ["original"], HistoryUpserts = [new() { EntryId = "replacement", Order = 2, Kind = "assistant", Body = "new" }] });
        window.History[0].Body = "mutated caller";
        var backlog = Event("agent.state", "turn");
        backlog.Sequence = 90;
        _history.ImportProviderEvents(Session, 100, [backlog]);
        _history.FlushPersistence(Session);
        var recovered = Recover();
        Assert.Equal(100, recovered.GetSnapshot(Session)!.LatestSequence);
        Assert.Equal("new", Assert.Single(recovered.GetSnapshot(Session)!.History).Body);
        using (var subscription = recovered.SubscribeProviderEvents(Session, 0, TestContext.Current.CancellationToken))
        {
            Assert.True(subscription.Reader.TryRead(out var retained));
            Assert.Equal(90, retained!.Sequence);
            Assert.False(subscription.Reader.TryRead(out _));
        }

        _history.ImportHistoryWindow(new() { SessionId = Session, LatestSequence = 100 });
        _history.AppendInitialReplay([Started("next"), Assistant("next", "fresh", "fresh")]);
        _history.FlushPersistence(Session);
        var reset = Recover();
        AssertSnapshotsEqual(_history, reset);
        Assert.Equal("fresh", Assert.Single(reset.GetSnapshot(Session)!.History).Body);
        Assert.Equal(102, reset.GetSnapshot(Session)!.LatestSequence);
    }

    [Theory]
    [InlineData("header")]
    [InlineData("payload")]
    [InlineData("digest")]
    public void OnlyIncompleteTerminalFrameIsDiscardedAndNextSaveIsReadable(string cut)
    {
        _history.Append(Assistant("turn", "first", "first"));
        _history.FlushPersistence(Session);
        var boundary = new FileInfo(JournalPath).Length;
        _history.Append(Assistant("turn", "second", "second"));
        _history.FlushPersistence(Session);
        var directory = CopyStore(_directory);
        var path = Path.Combine(directory, Session + ".journal");
        using (var file = File.OpenWrite(path)) file.SetLength(cut switch
        {
            "header" => boundary + 3,
            "payload" => boundary + 10,
            _ => file.Length - 1
        });
        var recovered = OpenStore(directory);
        Assert.Equal("first", Assert.Single(recovered.GetSnapshot(Session)!.History).Body);
        Assert.False(File.Exists(path));
        recovered.Append(Assistant("turn", "third", "third"));
        recovered.FlushPersistence(Session);
        AssertSnapshotsEqual(recovered, Recover(directory));
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("header")]
    [InlineData("formatVersion")]
    [InlineData("recordNumber")]
    [InlineData("sessionId")]
    public void CompleteCorruptUnsupportedOrOutOfScopeJournalFailsWithoutReplacingStore(string defect)
    {
        _history.Append(Assistant("turn", "first", "first"));
        _history.FlushPersistence(Session);
        var directory = CopyStore(_directory);
        var path = Path.Combine(directory, Session + ".journal");
        var bytes = File.ReadAllBytes(path);
        if (defect == "checksum") bytes[^1] ^= 1;
        else if (defect == "header") bytes[4] ^= 1;
        else
        {
            var length = BinaryPrimitives.ReadInt32LittleEndian(bytes);
            var record = JsonNode.Parse(bytes.AsSpan(8, length))!.AsObject();
            record[defect] = defect switch { "sessionId" => JsonValue.Create("other"), "recordNumber" => JsonValue.Create(2), _ => JsonValue.Create(99) };
            bytes = Frame(JsonSerializer.SerializeToUtf8Bytes(record, JsonSerializerOptions.Web));
        }
        File.WriteAllBytes(path, bytes);
        var recovered = OpenStore(directory);
        Assert.Throws<InvalidDataException>(() => recovered.GetSnapshot(Session));
        Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.False(File.Exists(Path.Combine(directory, Session + ".json")));
    }

    [Fact]
    public void CheckpointWatermarkSurvivesCrashBeforeOldJournalDeletion()
    {
        _history.AppendInitialReplay([Started("turn"), Item("turn", "tool", "command_execution", "running", "Marker"), Output("tool", "first\npartial")]);
        _history.FlushPersistence(Session);
        var oldJournal = File.ReadAllBytes(JournalPath);
        var directory = CopyStore(_directory);
        var recovered = OpenStore(directory);
        AssertSnapshotsEqual(_history, recovered); // Cold load committed a full checkpoint.
        File.WriteAllBytes(Path.Combine(directory, Session + ".journal"), oldJournal); // Crash before delete.
        AssertSnapshotsEqual(recovered, Recover(directory));
    }

    [Fact]
    public void FailedColdCheckpointLeavesJournalRecoverableForNextAttempt()
    {
        _history.Append(Assistant("turn", "first", "first"));
        _history.FlushPersistence(Session);
        var directory = CopyStore(_directory);
        var journal = Path.Combine(directory, Session + ".journal");
        var bytes = File.ReadAllBytes(journal);
        var blockedTemp = Path.Combine(directory, Session + ".json.tmp");
        Directory.CreateDirectory(blockedTemp);
        var recovered = OpenStore(directory);
        var failure = Record.Exception(() => recovered.GetSnapshot(Session));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(bytes, File.ReadAllBytes(journal));
        Directory.Delete(blockedTemp);
        AssertSnapshotsEqual(_history, recovered);
        Assert.False(File.Exists(journal));
    }

    [Fact]
    public void ActualFileOpenFailureCoalescesFurtherDeltasAndResumesJournalAfterRecoveryCheckpoint()
    {
        Directory.CreateDirectory(JournalPath); // A real IO failure, not a mocked writer exception.
        _history.Append(Assistant("turn", "first", "first"));
        var failure = Record.Exception(() => _history.FlushPersistence(Session));
        Assert.True(failure is IOException or UnauthorizedAccessException);
        Assert.Equal(0, _history.GetPendingPersistenceBytes(Session));
        _history.Append(Assistant("turn", "second", "second"));
        for (var i = 0; i < 20; i++) _history.Append(Assistant("turn", "second", new string('x', 6000) + i.ToString(System.Globalization.CultureInfo.InvariantCulture)));
        Assert.Equal(0, _history.GetPendingPersistenceBytes(Session));
        Directory.Delete(JournalPath);
        _history.FlushPersistence(Session);
        Assert.True(File.Exists(Path.Combine(_directory, Session + ".json")));
        Assert.False(File.Exists(JournalPath));
        AssertSnapshotsEqual(_history, Recover());
        _history.Append(Assistant("turn", "third", "Journal resumed"));
        _history.FlushPersistence(Session);
        Assert.InRange(new FileInfo(JournalPath).Length, 1, 8192);
        AssertSnapshotsEqual(_history, Recover());
    }

    [Fact]
    public async Task ForgetRetiresScheduledWriterBeforeReusingSameSessionAndDeletesUnloadedStore()
    {
        _history.Append(Assistant("turn", "forgotten", "forgotten"));
        _history.Forget(Session);
        Assert.False(File.Exists(JournalPath));
        _history.Append(Assistant("new", "kept", "kept"));
        _history.FlushPersistence(Session);
        await Task.Delay(450, TestContext.Current.CancellationToken); // Let the old scheduled generation wake.
        var recovered = Recover();
        Assert.Equal("kept", Assert.Single(recovered.GetSnapshot(Session)!.History).Body);
        _history.Forget(Session);
        // Forget must also erase a persisted session that this service never loaded.
        File.WriteAllText(JournalPath, "unloaded fixture");
        _history.Forget(Session);
        await Task.Delay(250, TestContext.Current.CancellationToken);
        Assert.False(File.Exists(JournalPath));
        Assert.False(File.Exists(Path.Combine(_directory, Session + ".json")));
    }

    private string JournalPath => Path.Combine(_directory, Session + ".journal");

    private SessionAppServerControlHistoryService Recover(string? source = null) => OpenStore(CopyStore(source ?? _directory));

    private SessionAppServerControlHistoryService OpenStore(string directory)
    {
        var service = new SessionAppServerControlHistoryService(storeDirectory: directory, enableScreenLogging: false);
        _recovered.Add(service);
        return service;
    }

    private string CopyStore(string source)
    {
        var destination = Path.Combine(_directory, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(destination);
        foreach (var path in Directory.GetFiles(source)) File.Copy(path, Path.Combine(destination, Path.GetFileName(path)));
        return destination;
    }

    private static void AssertSnapshotsEqual(SessionAppServerControlHistoryService expected, SessionAppServerControlHistoryService actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected.GetSnapshot(Session), JsonSerializerOptions.Web),
            JsonSerializer.Serialize(actual.GetSnapshot(Session), JsonSerializerOptions.Web));

    private static AppServerControlProviderEvent Output(string id, string text)
    {
        var e = Event("content.delta", "turn");
        e.ItemId = id;
        e.ContentDelta = new() { StreamKind = "command_output", Delta = text };
        return e;
    }

    private static byte[] Frame(byte[] payload)
    {
        var bytes = new byte[8 + payload.Length + 32];
        BinaryPrimitives.WriteInt32LittleEndian(bytes, payload.Length);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(4), ~payload.Length);
        payload.CopyTo(bytes, 8);
        SHA256.HashData(payload).CopyTo(bytes, 8 + payload.Length);
        return bytes;
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
        foreach (var service in _recovered) service.Forget(Session);
        _history.Forget(Session);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

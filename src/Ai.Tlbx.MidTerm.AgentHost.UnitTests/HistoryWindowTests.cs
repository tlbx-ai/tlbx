using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.AgentHost.UnitTests;

public sealed class HistoryWindowTests : IDisposable
{
    private const string SessionId = "bounded-history";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tlbx-history-window-tests", Guid.NewGuid().ToString("N"));
    private readonly List<SessionAppServerControlHistoryService> _services = [];

    [Theory]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public void WindowClonesOnlyItsSliceDependenciesAndActuallyActiveWork(int totalCount)
    {
        var source = CreateHistory(totalCount);
        source.Requests[0].State = "open";
        source.Items.Add(new() { ItemId = "active", TurnId = "turn", Status = "running", Title = "Current work" });
        source.Session.Usage = new() { InputTokens = 123456, OutputTokens = 6789 };
        source.Streams.AssistantText = "Live text outside the browse window";
        var service = SeedPersisted(source);

        var window = service.GetSnapshotWindow(SessionId, 5000, 7)!;

        Assert.Equal(totalCount, window.HistoryCount);
        Assert.Equal(5000, window.HistoryWindowStart);
        Assert.Equal(5007, window.HistoryWindowEnd);
        Assert.Equal(Enumerable.Range(5000, 7).Select(i => $"entry-{i}"), window.History.Select(row => row.EntryId));
        Assert.Equal(8, window.Items.Count);
        Assert.Equal(8, window.Requests.Count);
        Assert.Contains(window.Items, item => item.ItemId == "active");
        Assert.Contains(window.Requests, request => request.RequestId == "request-0" && request.State == "open");
        Assert.DoesNotContain(window.Items, item => item.ItemId == "item-4999");
        Assert.Equal(123456, window.Session.Usage!.InputTokens);
        Assert.Equal(source.Streams.AssistantText, window.Streams.AssistantText);
        var json = JsonSerializer.Serialize(window, AppServerControlHostJsonContext.Default.AppServerControlHistoryWindowResponse);
        Assert.True(json.Length < 50_000, $"Small-window JSON unexpectedly retained archive-sized data: {json.Length}");
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(7, parsed.RootElement.GetProperty("history").GetArrayLength());
        var tail = service.GetSnapshotWindow(SessionId, totalCount - 1, 1)!;
        Assert.Equal(totalCount, tail.HistoryCount);
        Assert.Equal($"entry-{totalCount - 1}", Assert.Single(tail.History).EntryId);
        Assert.Equal(80, service.GetSnapshotWindow(SessionId)!.History.Count);
    }

    [Fact]
    public void FullSnapshotRemainsExplicitlyCompleteAndWindowPreservesAliasDependencies()
    {
        var source = CreateHistory(30);
        source.Items[10].AliasItemIds.Add("native-alias");
        source.History[10].ItemId = "native-alias";
        source.Notices = Enumerable.Range(0, 64).Select(i => new AppServerControlRuntimeNotice { EventId = $"notice-{i}" }).ToList();
        var service = SeedPersisted(source);
        var window = service.GetSnapshotWindow(SessionId, 10, 1)!;
        Assert.Equal("item-10", Assert.Single(window.Items).ItemId);
        Assert.Equal("request-10", Assert.Single(window.Requests).RequestId);
        Assert.Equal(64, window.Notices.Count);
        var full = service.GetSnapshot(SessionId)!;
        Assert.Equal(30, full.History.Count);
        Assert.Equal(30, full.Items.Count);
        Assert.Equal(30, full.Requests.Count);
        Assert.Equal(30, source.History.Count);
        var recovered = new SessionAppServerControlHistoryService(storeDirectory: _directory, enableScreenLogging: false);
        _services.Add(recovered);
        Assert.Equal(30, recovered.GetSnapshot(SessionId)!.Items.Count);
        Assert.Equal("item-10", Assert.Single(recovered.GetSnapshotWindow(SessionId, 10, 1)!.Items).ItemId);
        window.Items[0].AliasItemIds.Clear();
        Assert.Contains("native-alias", service.GetSnapshotWindow(SessionId, 10, 1)!.Items[0].AliasItemIds);
    }

    [Fact]
    public void IndexStaysConsistentAcrossReplacementReorderRemovalAndReset()
    {
        var service = SeedPersisted(CreateHistory(5));
        service.ApplyHistoryPatch(new()
        {
            SessionId = SessionId, LatestSequence = 2,
            HistoryRemovals = ["entry-1"],
            HistoryUpserts = [
                new() { EntryId = "entry-4", Order = 0, Body = "moved" },
                new() { EntryId = "entry-new", Order = 3, Body = "inserted" },
                new() { EntryId = "entry-0", Order = 1, Body = "replaced" }
            ]
        });
        var full = service.GetSnapshot(SessionId)!;
        Assert.Equal(new[] { "entry-4", "entry-0", "entry-2", "entry-new", "entry-3" }, full.History.Select(row => row.EntryId));
        Assert.Equal("replaced", full.History[1].Body);
        Assert.Equal(full.History.Skip(1).Take(2).Select(row => row.EntryId),
            service.GetSnapshotWindow(SessionId, 1, 2)!.History.Select(row => row.EntryId));
        service.ImportHistoryWindow(CreateHistory(2));
        var reset = service.GetSnapshotWindow(SessionId, 0, 10)!;
        Assert.Equal(new[] { "entry-0", "entry-1" }, reset.History.Select(row => row.EntryId));
        Assert.Equal(2, reset.HistoryCount);
        Assert.Equal(2, reset.Items.Count);
        Assert.DoesNotContain(reset.Items, item => item.ItemId == "item-4");
        service.ImportHistoryWindow(new() { SessionId = SessionId });
        Assert.Empty(service.GetSnapshotWindow(SessionId, 0, 10)!.History);
        Assert.Equal(0, service.GetSnapshot(SessionId)!.HistoryCount);
    }

    [Fact]
    public void PatchResolutionAndTurnCompletionRetireOnlyDerivedLiveIndexes()
    {
        var source = CreateHistory(10);
        source.Requests[0].State = "open";
        source.Items[0].Status = "running";
        var service = SeedPersisted(source);
        Assert.Equal(2, service.GetSnapshotWindow(SessionId, 5, 1)!.Items.Count);
        Assert.Equal(2, service.GetSnapshotWindow(SessionId, 5, 1)!.Requests.Count);
        service.ApplyHistoryPatch(new()
        {
            SessionId = SessionId, LatestSequence = 2,
            CurrentTurn = new() { TurnId = "turn", State = "completed" },
            RequestUpserts = [new() { RequestId = "request-0", State = "resolved" }]
        });
        var window = service.GetSnapshotWindow(SessionId, 5, 1)!;
        Assert.Single(window.Items);
        Assert.Single(window.Requests);
        Assert.Equal(10, service.GetSnapshot(SessionId)!.Items.Count);
        Assert.Equal(10, service.GetSnapshot(SessionId)!.Requests.Count);
    }

    [Fact]
    public void ProviderPromotionMaintainsIndexAndLivePatchContainsOnlyTouchedSummaries()
    {
        var source = CreateHistory(3);
        foreach (var row in source.History) row.TurnId = "turn";
        var service = SeedPersisted(source);
        service.Append(new()
        {
            SessionId = SessionId, EventId = "late-user", TurnId = "turn", ItemId = "turn",
            CreatedAt = DateTimeOffset.UtcNow,
            ContentDelta = new() { StreamKind = "user_text", Delta = "A late canonical user row" }
        });
        Assert.Equal("user:turn", service.GetSnapshotWindow(SessionId, 0, 1)!.History[0].EntryId);
        Assert.Equal(new long[] { 1, 2, 3, 4 }, service.GetSnapshot(SessionId)!.History.Select(row => row.Order));
        using var subscription = service.SubscribeHistoryPatches(SessionId);
        service.Append(new()
        {
            SessionId = SessionId, EventId = "new-tool", TurnId = "turn", ItemId = "new-tool",
            CreatedAt = DateTimeOffset.UtcNow,
            Item = new() { ItemType = "command_execution", Status = "completed", Detail = "done" }
        });
        Assert.True(subscription.Reader.TryRead(out var patch));
        Assert.Single(patch!.ItemUpserts);
        Assert.Single(patch.HistoryUpserts);
        Assert.Empty(patch.ItemRemovals); // Browser eviction must never masquerade as canonical deletion.
        Assert.Equal(5, service.GetSnapshot(SessionId)!.HistoryCount);
        Assert.Equal("new-tool", service.GetSnapshotWindow(SessionId, 4, 1)!.Items[0].ItemId);
    }

    private SessionAppServerControlHistoryService SeedPersisted(AppServerControlHistoryWindowResponse source)
    {
        Directory.CreateDirectory(_directory);
        // Read-only large fixtures avoid background persistence work during structural window assertions.
        File.WriteAllText(Path.Combine(_directory, SessionId + ".json"), JsonSerializer.Serialize(new
        {
            nextSequence = 1,
            state = new
            {
                source.Provider, source.Session, source.Thread, source.CurrentTurn, source.QuickSettings, source.Streams,
                historyEntries = source.History, source.Items, source.Requests, source.Notices,
                toolRenderStates = Array.Empty<object>(),
                nextHistoryOrder = source.History.Count
            }
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var service = new SessionAppServerControlHistoryService(storeDirectory: _directory, enableScreenLogging: false);
        _services.Add(service);
        return service;
    }

    private static AppServerControlHistoryWindowResponse CreateHistory(int count)
    {
        var source = new AppServerControlHistoryWindowResponse
        {
            SessionId = SessionId, LatestSequence = 1, Provider = "codex", HistoryCount = count,
            HistoryWindowEnd = count, CurrentTurn = new() { TurnId = "turn", State = "running" }
        };
        for (var i = 0; i < count; i++)
        {
            source.History.Add(new() { EntryId = $"entry-{i}", Order = i + 1, ItemId = $"item-{i}", RequestId = $"request-{i}", Body = "Canonical content" });
            source.Items.Add(new() { ItemId = $"item-{i}", TurnId = "turn", Status = "completed", Detail = "Completed tool in the same active turn" });
            source.Requests.Add(new() { RequestId = $"request-{i}", TurnId = "turn", State = "resolved" });
        }
        return source;
    }

    public void Dispose()
    {
        // All paths are private to this fixture; the read-only large fixtures launch no persistence writer.
        foreach (var service in _services) service.Forget(SessionId);
        if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
    }
}

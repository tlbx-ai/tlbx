using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class AppServerControlBridgeCacheTests
{
    private const string ActiveId = "external/item@opaque";
    private const string PendingId = "foreign?request/opaque";

    [Theory]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public void RollingWindowsAndFullLivePatchesNeverAccumulateCompletedSummaries(int count)
    {
        var source = LegacyWindow(count, 100, 8);
        var cached = SessionAppServerControlHostRuntimeService.AcceptCachedHistoryWindow(null, source);
        Assert.Equal(9, cached.Items.Count);
        Assert.Equal(9, cached.Requests.Count);
        Assert.Equal(count + 1, source.Items.Count);
        Assert.Equal(count + 1, source.Requests.Count);

        for (var step = 0; step < 32; step++)
        {
            var incoming = Window(count + step, 200 + step * 8, 8);
            incoming.LatestSequence = cached.LatestSequence + 1;
            cached = SessionAppServerControlHostRuntimeService.AcceptCachedHistoryWindow(cached, incoming);
            var patch = Patch(cached);
            patch.HistoryCount++;
            patch.HistoryUpserts = [new() { EntryId = $"off-window-{step}", Order = count + step + 1, ItemId = $"completed-{step}" }];
            patch.ItemUpserts = [new() { ItemId = $"completed-{step}", TurnId = "turn/current", Status = "completed" }];
            patch.RequestUpserts = [new() { RequestId = $"resolved-{step}", State = "resolved" }];

            cached = SessionAppServerControlHostRuntimeService.ApplyCachedHistoryPatch(cached, patch);

            Assert.Equal(8, cached.History.Count);
            Assert.Equal(9, cached.Items.Count);
            Assert.Equal(9, cached.Requests.Count);
            Assert.DoesNotContain(cached.Items, item => item.ItemId == $"completed-{step}");
            Assert.Contains(cached.Items, item => item.ItemId == ActiveId);
            Assert.Contains(cached.Requests, request => request.RequestId == PendingId && request.TurnId == "older/external-turn");
            Assert.Equal(patch.HistoryCount, cached.HistoryCount);
            Assert.Single(patch.HistoryUpserts); // The cache projection must not rewrite the fanout payload.
            Assert.Single(patch.ItemUpserts);
            Assert.Empty(patch.ItemRemovals);
            Assert.Empty(patch.RequestRemovals);
            var json = JsonSerializer.Serialize(cached, AppServerControlHostJsonContext.Default.AppServerControlHistoryWindowResponse);
            Assert.True(json.Length < 30_000);
            Assert.DoesNotContain("ARCHIVE_DETAIL", json, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public void MetadataOnlyWindowsAndInitialPatchesDoNotSerializeArchivedSummaryLists(int count)
    {
        var source = LegacyWindow(count, 0, 0);
        var cached = SessionAppServerControlHostRuntimeService.AcceptCachedHistoryWindow(null, source);
        var firstPatch = Patch(source);
        firstPatch.ItemUpserts = source.Items;
        firstPatch.RequestUpserts = source.Requests;
        var patchOnly = SessionAppServerControlHostRuntimeService.ApplyCachedHistoryPatch(null, firstPatch);
        foreach (var projection in new[] { cached, patchOnly })
        {
            Assert.Empty(projection.History);
            Assert.Equal(ActiveId, Assert.Single(projection.Items).ItemId);
            Assert.Equal(PendingId, Assert.Single(projection.Requests).RequestId);
            Assert.Equal(count, projection.HistoryCount);
            Assert.Equal("thread/opaque", projection.Thread.ThreadId);
            Assert.Equal("Global assistant stream", projection.Streams.AssistantText);
            Assert.Equal(777321, projection.Session.Usage!.InputTokens);
            var json = JsonSerializer.Serialize(projection, AppServerControlHostJsonContext.Default.AppServerControlHistoryWindowResponse);
            Assert.True(json.Length < 10_000);
            Assert.DoesNotContain("ARCHIVE_DETAIL", json, StringComparison.Ordinal);
            using var parsed = JsonDocument.Parse(json);
            Assert.Equal(1, parsed.RootElement.GetProperty("items").GetArrayLength());
            Assert.Equal(1, parsed.RootElement.GetProperty("requests").GetArrayLength());
        }
        Assert.Equal(count + 1, firstPatch.ItemUpserts.Count);
        Assert.Equal(count + 1, firstPatch.RequestUpserts.Count);
    }

    [Theory]
    [InlineData(10_000)]
    [InlineData(100_000)]
    public void OffWindowRequestCompletionAndTurnCompletionCannotBeUndoneByStaleResponses(int count)
    {
        var incoming = Window(count, 100, 5);
        var cached = SessionAppServerControlHostRuntimeService.AcceptCachedHistoryWindow(null, incoming);
        var settled = Patch(cached);
        settled.RequestUpserts = [new() { RequestId = PendingId, State = "resolved" }];
        settled.ItemUpserts = [new() { ItemId = ActiveId, TurnId = "turn/current", Status = "completed" }];
        cached = SessionAppServerControlHostRuntimeService.ApplyCachedHistoryPatch(cached, settled);
        Assert.Equal(5, cached.Items.Count);
        Assert.Equal(5, cached.Requests.Count);
        Assert.DoesNotContain(cached.Items, item => item.ItemId == ActiveId);
        Assert.DoesNotContain(cached.Requests, request => request.RequestId == PendingId);
        var working = Patch(cached);
        working.ItemUpserts = [new() { ItemId = "external/next", TurnId = "turn/current", Status = "running" }];
        cached = SessionAppServerControlHostRuntimeService.ApplyCachedHistoryPatch(cached, working);
        Assert.Equal(6, cached.Items.Count);
        var completedTurn = Patch(cached);
        completedTurn.CurrentTurn = new() { TurnId = "turn/current", State = "completed" };
        cached = SessionAppServerControlHostRuntimeService.ApplyCachedHistoryPatch(cached, completedTurn);
        Assert.Equal(5, cached.Items.Count);
        Assert.Same(cached, SessionAppServerControlHostRuntimeService.AcceptCachedHistoryWindow(cached, incoming));
        var stale = Patch(incoming);
        stale.LatestSequence = incoming.LatestSequence;
        stale.RequestUpserts = [new() { RequestId = PendingId, State = "open" }];
        Assert.Same(cached, SessionAppServerControlHostRuntimeService.ApplyCachedHistoryPatch(cached, stale));

        var sameRevisionWindow = Window(count, 400, 5);
        sameRevisionWindow.LatestSequence = cached.LatestSequence;
        sameRevisionWindow.CurrentTurn.State = "completed";
        sameRevisionWindow.Requests.RemoveAll(request => request.RequestId == PendingId);
        cached = SessionAppServerControlHostRuntimeService.AcceptCachedHistoryWindow(cached, sameRevisionWindow);
        Assert.Equal(400, cached.HistoryWindowStart); // Equal sequence is a valid different browse window.
        Assert.Equal(5, cached.Items.Count);
        Assert.Equal(5, cached.Requests.Count);
    }

    [Fact]
    public void RetainedRowsKeepOpaqueAliasDependenciesAndResolvedRequestsUntilTheyLeaveTheWindow()
    {
        var source = Window(1000, 10, 1);
        source.History[0].ItemId = "native-alias/opaque";
        source.Items[0].AliasItemIds = ["native-alias/opaque"];
        var cached = SessionAppServerControlHostRuntimeService.AcceptCachedHistoryWindow(null, source);
        Assert.Contains(cached.Items, item => item.ItemId == "item-10");
        var patch = Patch(cached);
        patch.HistoryUpserts = [new() { EntryId = "entry-10", Order = 11, ItemId = "rekeyed/opaque", RequestId = "resolved/external" }];
        patch.ItemUpserts = [new() { ItemId = "rekeyed/opaque", Status = "completed" }];
        patch.RequestUpserts = [new() { RequestId = "resolved/external", State = "resolved" }];
        cached = SessionAppServerControlHostRuntimeService.ApplyCachedHistoryPatch(cached, patch);
        Assert.Contains(cached.Items, item => item.ItemId == "rekeyed/opaque");
        Assert.DoesNotContain(cached.Items, item => item.ItemId == "item-10");
        Assert.Contains(cached.Requests, request => request.RequestId == "resolved/external");
        Assert.Equal("native-alias/opaque", source.History[0].ItemId);
        Assert.Equal("rekeyed/opaque", patch.HistoryUpserts[0].ItemId);
        Assert.Empty(patch.ItemRemovals);
        var removed = Patch(cached);
        removed.HistoryCount--;
        removed.HistoryRemovals = ["entry-10"];
        cached = SessionAppServerControlHostRuntimeService.ApplyCachedHistoryPatch(cached, removed);
        Assert.Empty(cached.History);
        Assert.Equal(ActiveId, Assert.Single(cached.Items).ItemId);
        Assert.Equal(PendingId, Assert.Single(cached.Requests).RequestId);
    }

    private static AppServerControlHistoryPatch Patch(AppServerControlHistoryWindowResponse source) => new()
    {
        SessionId = source.SessionId, Provider = source.Provider, LatestSequence = source.LatestSequence + 1,
        HistoryCount = source.HistoryCount, Session = source.Session, Thread = source.Thread,
        CurrentTurn = source.CurrentTurn, QuickSettings = source.QuickSettings, Streams = source.Streams
    };

    private static AppServerControlHistoryWindowResponse LegacyWindow(int total, int start, int count)
    {
        var source = Window(total, start, count);
        source.Items = Enumerable.Range(0, total).Select(i => Item(i, i >= start && i < start + count)).ToList();
        source.Requests = Enumerable.Range(0, total).Select(i => Request(i)).ToList();
        AddLiveDependencies(source);
        return source;
    }

    private static AppServerControlHistoryWindowResponse Window(int total, int start, int count)
    {
        var source = new AppServerControlHistoryWindowResponse
        {
            SessionId = "session", Provider = "codex", LatestSequence = 1, HistoryCount = total,
            HistoryWindowStart = start, HistoryWindowEnd = start + count,
            HasOlderHistory = start > 0, HasNewerHistory = start + count < total,
            Thread = new() { ThreadId = "thread/opaque" },
            CurrentTurn = new() { TurnId = "turn/current", State = "running" },
            Session = new() { Usage = new() { InputTokens = 777321 } },
            Streams = new() { AssistantText = "Global assistant stream" }
        };
        for (var i = start; i < start + count; i++)
        {
            source.History.Add(new() { EntryId = $"entry-{i}", Order = i + 1, ItemId = $"item-{i}", RequestId = $"request-{i}", Body = "retained canonical row" });
            source.Items.Add(Item(i, true));
            source.Requests.Add(Request(i));
        }
        AddLiveDependencies(source);
        return source;
    }

    private static void AddLiveDependencies(AppServerControlHistoryWindowResponse source)
    {
        source.Items.Add(new() { ItemId = ActiveId, TurnId = "turn/current", Status = "in_progress", Detail = "Live work" });
        source.Requests.Add(new() { RequestId = PendingId, TurnId = "older/external-turn", Kind = "interview", State = "open", Detail = "External pending request" });
    }

    private static AppServerControlItemSummary Item(int i, bool retained) => new()
    {
        ItemId = $"item-{i}", TurnId = "turn/current", Status = "completed",
        Detail = retained ? "window summary" : "ARCHIVE_DETAIL"
    };

    private static AppServerControlRequestSummary Request(int i) => new()
    {
        RequestId = $"request-{i}", State = "resolved", TurnId = "turn/current"
    };
}

using System.Globalization;
using System.Linq;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class SessionAppServerControlHistoryServiceTests
{
    [Fact]
    public void GetSnapshot_ReducesCanonicalAppServerControlEventsIntoRenderState()
    {
        var service = new SessionAppServerControlHistoryService();

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "e1",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            CreatedAt = ParseUtc("2026-03-20T14:00:00Z"),
            Type = "session.started",
            SessionState = new AppServerControlProviderSessionStatePayload
            {
                State = "starting",
                StateLabel = "Starting",
                Reason = "Booting"
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "e2",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            CreatedAt = ParseUtc("2026-03-20T14:00:01Z"),
            Type = "turn.started",
            TurnStarted = new AppServerControlProviderTurnStartedPayload
            {
                Model = "gpt-5.3-codex",
                Effort = "medium"
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "e3",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "local-user:turn-1",
            CreatedAt = ParseUtc("2026-03-20T14:00:01.5000000Z"),
            Type = "item.completed",
            Item = new AppServerControlProviderItemPayload
            {
                ItemType = "user_message",
                Status = "completed",
                Title = "User message",
                Detail = "Inspect this screenshot.",
                Attachments =
                [
                    new AppServerControlAttachmentReference
                    {
                        Kind = "image",
                        Path = "Q:/repo/.midterm/uploads/screenshot.png",
                        MimeType = "image/png",
                        DisplayName = "screenshot.png"
                    }
                ]
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "e4",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            ItemId = "item-1",
            CreatedAt = ParseUtc("2026-03-20T14:00:02Z"),
            Type = "content.delta",
            ContentDelta = new AppServerControlProviderContentDeltaPayload
            {
                StreamKind = "assistant_text",
                Delta = "hello"
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "e5",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            RequestId = "req-1",
            CreatedAt = ParseUtc("2026-03-20T14:00:03Z"),
            Type = "user-input.requested",
            UserInputRequested = new AppServerControlProviderUserInputRequestedPayload
            {
                Questions =
                [
                    new AppServerControlQuestion
                    {
                        Id = "q1",
                        Header = "Mode",
                        Question = "Pick mode",
                        Options = [new AppServerControlQuestionOption { Label = "A", Description = "alpha" }]
                    }
                ]
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "e5-resolved",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            RequestId = "req-1",
            CreatedAt = ParseUtc("2026-03-20T14:00:03.5000000Z"),
            Type = "user-input.resolved",
            UserInputResolved = new AppServerControlProviderUserInputResolvedPayload
            {
                Answers =
                [
                    new AppServerControlAnsweredQuestion
                    {
                        QuestionId = "q1",
                        Answers = ["A"]
                    }
                ]
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "e6",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            ItemId = "item-2",
            CreatedAt = ParseUtc("2026-03-20T14:00:03Z"),
            Type = "content.delta",
            ContentDelta = new AppServerControlProviderContentDeltaPayload
            {
                StreamKind = "reasoning_text",
                Delta = "thinking"
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "e7",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            ItemId = "item-3",
            CreatedAt = ParseUtc("2026-03-20T14:00:03Z"),
            Type = "content.delta",
            ContentDelta = new AppServerControlProviderContentDeltaPayload
            {
                StreamKind = "command_output",
                Delta = "npm test"
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "e8",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            RequestId = "approval-1",
            CreatedAt = ParseUtc("2026-03-20T14:00:03Z"),
            Type = "request.opened",
            RequestOpened = new AppServerControlProviderRequestOpenedPayload
            {
                RequestType = "command_execution_approval",
                RequestTypeLabel = "Command approval",
                Detail = "npm test"
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "e9",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            RequestId = "approval-1",
            CreatedAt = ParseUtc("2026-03-20T14:00:04Z"),
            Type = "request.resolved",
            RequestResolved = new AppServerControlProviderRequestResolvedPayload
            {
                RequestType = "command_execution_approval",
                Decision = "accept"
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "e10",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            CreatedAt = ParseUtc("2026-03-20T14:00:05Z"),
            Type = "diff.updated",
            DiffUpdated = new AppServerControlProviderDiffUpdatedPayload
            {
                UnifiedDiff = "--- a\n+++ b"
            }
        });

        var snapshot = service.GetSnapshot("s1");

        Assert.NotNull(snapshot);
        Assert.Equal("codex", snapshot!.Provider);
        Assert.Equal("starting", snapshot.Session.State);
        Assert.Equal("turn-1", snapshot.CurrentTurn.TurnId);
        Assert.Equal("hello", snapshot.Streams.AssistantText);
        Assert.Equal("thinking", snapshot.Streams.ReasoningText);
        Assert.Equal("npm test", snapshot.Streams.CommandOutput);
        Assert.Equal("--- a\n+++ b", snapshot.Streams.UnifiedDiff);
        Assert.Contains(
            snapshot.Items,
            item => item.ItemType == "user_message" &&
                    item.Detail == "Inspect this screenshot." &&
                    item.Attachments.Count == 1 &&
                    item.Attachments[0].DisplayName == "screenshot.png");
        Assert.Equal(2, snapshot.Requests.Count);
        Assert.Contains(
            snapshot.Requests,
            request => request.Kind == "interview" &&
                       request.Questions.Count == 1 &&
                       request.Answers.Count == 1);
        Assert.Contains(snapshot.Requests, request => request.Kind == "command_execution_approval" && request.Decision == "accept");
        Assert.Contains(
            snapshot.History,
            entry => entry.ItemType == "interview" &&
                     entry.RequestId == "req-1" &&
                     entry.Questions.Count == 1 &&
                     entry.Answers.Count == 1 &&
                     entry.Answers[0].Answers.SequenceEqual(["A"], StringComparer.Ordinal));
    }

    [Fact]
    public void GetEvents_FiltersBySequence()
    {
        var service = new SessionAppServerControlHistoryService();

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "e1",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            CreatedAt = DateTimeOffset.UtcNow,
            Type = "session.started"
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "e2",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            CreatedAt = DateTimeOffset.UtcNow,
            Type = "session.ready"
        });

        var all = service.GetProviderEvents("s1");
        var afterFirst = service.GetProviderEvents("s1", 1);

        Assert.Equal(2, all.LatestSequence);
        Assert.Equal(2, all.Events.Count);
        Assert.Single(afterFirst.Events);
        Assert.Equal("e2", afterFirst.Events[0].EventId);
    }

    [Fact]
    public void GetSnapshotWindow_ReturnsTailWindowMetadata()
    {
        var service = new SessionAppServerControlHistoryService();

        for (var i = 0; i < 12; i += 1)
        {
            service.Append(new AppServerControlProviderEvent
            {
                EventId = string.Create(CultureInfo.InvariantCulture, $"e{i}"),
                SessionId = "s-window",
                Provider = "codex",
                ThreadId = "thread-window",
                ItemId = string.Create(CultureInfo.InvariantCulture, $"item-{i}"),
                CreatedAt = ParseUtc("2026-03-20T14:00:00Z").AddSeconds(i),
                Type = "item.completed",
                Item = new AppServerControlProviderItemPayload
                {
                    ItemType = i % 2 == 0 ? "user_message" : "assistant_message",
                    Status = "completed",
                    Title = "Message",
                    Detail = string.Create(CultureInfo.InvariantCulture, $"entry-{i}")
                }
            });
        }

        var snapshot = service.GetSnapshotWindow("s-window", count: 5);

        Assert.NotNull(snapshot);
        Assert.Equal(12, snapshot!.HistoryCount);
        Assert.Equal(7, snapshot.HistoryWindowStart);
        Assert.Equal(12, snapshot.HistoryWindowEnd);
        Assert.True(snapshot.HasOlderHistory);
        Assert.False(snapshot.HasNewerHistory);
        Assert.Equal(5, snapshot.History.Count);
        Assert.All(snapshot.History, entry => Assert.True(entry.EstimatedHeightPx > 0));
        Assert.Equal("entry-7", snapshot.History[0].Body);
        Assert.Equal("entry-11", snapshot.History[^1].Body);
    }

    [Fact]
    public void GetSnapshotWindow_UsesViewportWidthForEstimatedHistoryHeights()
    {
        var service = new SessionAppServerControlHistoryService();
        var longBody = string.Join(' ', Enumerable.Repeat("variable-height-history", 80));

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "e-width-1",
            SessionId = "s-width",
            Provider = "codex",
            ThreadId = "thread-width",
            ItemId = "item-width-1",
            CreatedAt = ParseUtc("2026-03-20T14:00:00Z"),
            Type = "item.completed",
            Item = new AppServerControlProviderItemPayload
            {
                ItemType = "assistant_message",
                Status = "completed",
                Title = "Assistant message",
                Detail = longBody
            }
        });

        var narrowSnapshot = service.GetSnapshotWindow("s-width", 0, 1, viewportWidth: 320);
        var wideSnapshot = service.GetSnapshotWindow("s-width", 0, 1, viewportWidth: 1280);

        Assert.NotNull(narrowSnapshot);
        Assert.NotNull(wideSnapshot);
        Assert.True(narrowSnapshot!.History[0].EstimatedHeightPx > wideSnapshot!.History[0].EstimatedHeightPx);
    }

    [Fact]
    public void GetEvents_RetainsOnlyRawSourceMetadata()
    {
        var service = new SessionAppServerControlHistoryService();

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "raw-1",
            SessionId = "s-raw",
            Provider = "codex",
            ThreadId = "thread-raw",
            CreatedAt = ParseUtc("2026-04-07T08:00:00Z"),
            Type = "session.started",
            Raw = new AppServerControlProviderEventRaw
            {
                Source = "codex",
                Method = "session.started",
                PayloadOmitted = true
            }
        });

        var retained = Assert.Single(service.GetProviderEvents("s-raw").Events);

        Assert.NotNull(retained.Raw);
        Assert.Equal("codex", retained.Raw!.Source);
        Assert.Equal("session.started", retained.Raw.Method);
        Assert.True(retained.Raw.PayloadOmitted);
    }

    [Fact]
    public void GetSnapshot_TracksQuickSettingsUpdates()
    {
        var service = new SessionAppServerControlHistoryService();

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "settings-1",
            SessionId = "s-settings",
            Provider = "codex",
            ThreadId = "thread-settings",
            CreatedAt = ParseUtc("2026-03-30T08:00:00Z"),
            Type = "quick-settings.updated",
            QuickSettingsUpdated = new AppServerControlQuickSettingsPayload
            {
                Model = "gpt-5.4",
                Effort = "high",
                PlanMode = "on",
                PermissionMode = "auto"
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "turn-settings-1",
            SessionId = "s-settings",
            Provider = "codex",
            ThreadId = "thread-settings",
            TurnId = "turn-1",
            CreatedAt = ParseUtc("2026-03-30T08:00:01Z"),
            Type = "turn.started",
            TurnStarted = new AppServerControlProviderTurnStartedPayload()
        });

        var snapshot = service.GetSnapshot("s-settings");
        var events = service.GetProviderEvents("s-settings");

        Assert.NotNull(snapshot);
        Assert.Equal("gpt-5.4", snapshot!.QuickSettings.Model);
        Assert.Equal("high", snapshot.QuickSettings.Effort);
        Assert.Equal("gpt-5.4", snapshot.CurrentTurn.Model);
        Assert.Equal("high", snapshot.CurrentTurn.Effort);
        Assert.Equal("on", snapshot.QuickSettings.PlanMode);
        Assert.Equal("auto", snapshot.QuickSettings.PermissionMode);
        Assert.Contains(
            events.Events,
            appServerControlEvent => appServerControlEvent.Type == "quick-settings.updated" &&
                         appServerControlEvent.QuickSettingsUpdated is not null &&
                         appServerControlEvent.QuickSettingsUpdated.PlanMode == "on" &&
                         appServerControlEvent.QuickSettingsUpdated.PermissionMode == "auto");
    }

    [Fact]
    public void GetSnapshot_ResetsStreamingBuffersWhenANewTurnStarts()
    {
        var service = new SessionAppServerControlHistoryService();

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "t1-start",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            CreatedAt = ParseUtc("2026-03-22T01:00:00Z"),
            Type = "turn.started",
            TurnStarted = new AppServerControlProviderTurnStartedPayload()
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "t1-delta",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            CreatedAt = ParseUtc("2026-03-22T01:00:01Z"),
            Type = "content.delta",
            ContentDelta = new AppServerControlProviderContentDeltaPayload
            {
                StreamKind = "assistant_text",
                Delta = "first turn"
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "t1-complete",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            CreatedAt = ParseUtc("2026-03-22T01:00:02Z"),
            Type = "turn.completed",
            TurnCompleted = new AppServerControlProviderTurnCompletedPayload
            {
                State = "completed",
                StateLabel = "Completed"
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "t2-start",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-2",
            CreatedAt = ParseUtc("2026-03-22T01:00:03Z"),
            Type = "turn.started",
            TurnStarted = new AppServerControlProviderTurnStartedPayload()
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "t2-delta",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-2",
            CreatedAt = ParseUtc("2026-03-22T01:00:04Z"),
            Type = "content.delta",
            ContentDelta = new AppServerControlProviderContentDeltaPayload
            {
                StreamKind = "assistant_text",
                Delta = "second turn"
            }
        });

        var snapshot = service.GetSnapshot("s1");

        Assert.NotNull(snapshot);
        Assert.Equal("turn-2", snapshot!.CurrentTurn.TurnId);
        Assert.Equal("second turn", snapshot.Streams.AssistantText);
    }

    [Fact]
    public void GetSnapshot_ReconcilesProviderUserMessageWithSubmittedLocalUserTurn()
    {
        var service = new SessionAppServerControlHistoryService();

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "local-user",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "local-user:turn-1",
            CreatedAt = ParseUtc("2026-03-22T02:00:00Z"),
            Type = "item.completed",
            Item = new AppServerControlProviderItemPayload
            {
                ItemType = "user_message",
                Status = "completed",
                Title = "User message",
                Detail = "which working dir are you in"
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "provider-user",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "provider-item-1",
            CreatedAt = ParseUtc("2026-03-22T02:00:01Z"),
            Type = "item.started",
            Item = new AppServerControlProviderItemPayload
            {
                ItemType = "usermessage",
                Status = "in_progress",
                Title = "Tool started",
                Detail = string.Empty
            }
        });

        var snapshot = service.GetSnapshot("s1");

        Assert.NotNull(snapshot);
        var userItems = snapshot!.Items.Where(item => item.ItemType == "user_message").ToList();
        var userItem = Assert.Single(userItems);
        Assert.Equal("local-user:turn-1", userItem.ItemId);
        Assert.Equal("turn-1", userItem.TurnId);
        Assert.Equal("completed", userItem.Status);
        Assert.Equal("which working dir are you in", userItem.Detail);
    }

    [Fact]
    public void GetSnapshot_PreservesEarlierTurnsWhenALaterTurnCompletes()
    {
        var service = new SessionAppServerControlHistoryService();

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "turn-1-start",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            CreatedAt = ParseUtc("2026-03-27T10:00:00Z"),
            Type = "turn.started",
            TurnStarted = new AppServerControlProviderTurnStartedPayload()
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "turn-1-user",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "local-user:turn-1",
            CreatedAt = ParseUtc("2026-03-27T10:00:01Z"),
            Type = "item.completed",
            Item = new AppServerControlProviderItemPayload
            {
                ItemType = "user_message",
                Status = "completed",
                Detail = "first question"
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "turn-1-assistant",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            CreatedAt = ParseUtc("2026-03-27T10:00:02Z"),
            Type = "content.delta",
            ContentDelta = new AppServerControlProviderContentDeltaPayload
            {
                StreamKind = "assistant_text",
                Delta = "first answer"
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "turn-1-complete",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            CreatedAt = ParseUtc("2026-03-27T10:00:03Z"),
            Type = "turn.completed",
            TurnCompleted = new AppServerControlProviderTurnCompletedPayload
            {
                State = "completed",
                StateLabel = "Completed"
            }
        });

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "turn-2-start",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-2",
            CreatedAt = ParseUtc("2026-03-27T10:01:00Z"),
            Type = "turn.started",
            TurnStarted = new AppServerControlProviderTurnStartedPayload()
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "turn-2-user",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-2",
            ItemId = "local-user:turn-2",
            CreatedAt = ParseUtc("2026-03-27T10:01:01Z"),
            Type = "item.completed",
            Item = new AppServerControlProviderItemPayload
            {
                ItemType = "user_message",
                Status = "completed",
                Detail = "second question"
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "turn-2-assistant-delta",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-2",
            CreatedAt = ParseUtc("2026-03-27T10:01:02Z"),
            Type = "content.delta",
            ContentDelta = new AppServerControlProviderContentDeltaPayload
            {
                StreamKind = "assistant_text",
                Delta = "second answer"
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "turn-2-assistant-final",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-2",
            ItemId = "assistant-item-2",
            CreatedAt = ParseUtc("2026-03-27T10:01:03Z"),
            Type = "item.completed",
            Item = new AppServerControlProviderItemPayload
            {
                ItemType = "assistant_message",
                Status = "completed",
                Detail = "second answer final"
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "turn-2-complete",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-2",
            CreatedAt = ParseUtc("2026-03-27T10:01:04Z"),
            Type = "turn.completed",
            TurnCompleted = new AppServerControlProviderTurnCompletedPayload
            {
                State = "completed",
                StateLabel = "Completed"
            }
        });

        var snapshot = service.GetSnapshot("s1");

        Assert.NotNull(snapshot);
        var transcript = snapshot!.History;
        Assert.Equal(4, transcript.Count);
        Assert.Collection(
            transcript,
            entry =>
            {
                Assert.Equal("user", entry.Kind);
                Assert.Equal("turn-1", entry.TurnId);
                Assert.Equal("first question", entry.Body);
            },
            entry =>
            {
                Assert.Equal("assistant", entry.Kind);
                Assert.Equal("turn-1", entry.TurnId);
                Assert.Equal("first answer", entry.Body);
            },
            entry =>
            {
                Assert.Equal("user", entry.Kind);
                Assert.Equal("turn-2", entry.TurnId);
                Assert.Equal("second question", entry.Body);
            },
            entry =>
            {
                Assert.Equal("assistant", entry.Kind);
                Assert.Equal("turn-2", entry.TurnId);
                Assert.Equal("assistant-stream:turn-2", entry.EntryId);
                Assert.Equal("second answer final", entry.Body);
                Assert.False(entry.Streaming);
                Assert.Equal("completed", entry.Status);
            });
    }

    [Fact]
    public void GetSnapshot_PromotesLateUserRowsToTheStartOfTheirTurn()
    {
        var service = new SessionAppServerControlHistoryService();

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "turn-1-start",
            SessionId = "s-order",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            CreatedAt = ParseUtc("2026-04-06T10:00:00Z"),
            Type = "turn.started",
            TurnStarted = new AppServerControlProviderTurnStartedPayload()
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "turn-1-assistant",
            SessionId = "s-order",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            CreatedAt = ParseUtc("2026-04-06T10:00:01Z"),
            Type = "content.delta",
            ContentDelta = new AppServerControlProviderContentDeltaPayload
            {
                StreamKind = "assistant_text",
                Delta = "first answer"
            }
        });

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "turn-2-start",
            SessionId = "s-order",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-2",
            CreatedAt = ParseUtc("2026-04-06T10:01:00Z"),
            Type = "turn.started",
            TurnStarted = new AppServerControlProviderTurnStartedPayload()
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "turn-2-user",
            SessionId = "s-order",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-2",
            ItemId = "local-user:turn-2",
            CreatedAt = ParseUtc("2026-04-06T10:01:01Z"),
            Type = "item.completed",
            Item = new AppServerControlProviderItemPayload
            {
                ItemType = "user_message",
                Status = "completed",
                Detail = "second question"
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "turn-2-assistant",
            SessionId = "s-order",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-2",
            CreatedAt = ParseUtc("2026-04-06T10:01:02Z"),
            Type = "content.delta",
            ContentDelta = new AppServerControlProviderContentDeltaPayload
            {
                StreamKind = "assistant_text",
                Delta = "second answer"
            }
        });

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "turn-1-user-late",
            SessionId = "s-order",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "provider-user-1",
            CreatedAt = ParseUtc("2026-04-06T10:01:03Z"),
            Type = "item.completed",
            Item = new AppServerControlProviderItemPayload
            {
                ItemType = "user_message",
                Status = "completed",
                Detail = "first question"
            }
        });

        var snapshot = service.GetSnapshot("s-order");

        Assert.NotNull(snapshot);
        Assert.Collection(
            snapshot!.History,
            entry =>
            {
                Assert.Equal("user", entry.Kind);
                Assert.Equal("turn-1", entry.TurnId);
                Assert.Equal("first question", entry.Body);
            },
            entry =>
            {
                Assert.Equal("assistant", entry.Kind);
                Assert.Equal("turn-1", entry.TurnId);
                Assert.Equal("first answer", entry.Body);
            },
            entry =>
            {
                Assert.Equal("user", entry.Kind);
                Assert.Equal("turn-2", entry.TurnId);
                Assert.Equal("second question", entry.Body);
            },
            entry =>
            {
                Assert.Equal("assistant", entry.Kind);
                Assert.Equal("turn-2", entry.TurnId);
                Assert.Equal("second answer", entry.Body);
            });
    }

    [Fact]
    public void GetSnapshot_PreservesAssistantChronologyWhenFinalItemArrivesAfterToolWork()
    {
        var service = new SessionAppServerControlHistoryService();

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "turn-start",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            CreatedAt = ParseUtc("2026-03-27T11:00:00Z"),
            Type = "turn.started",
            TurnStarted = new AppServerControlProviderTurnStartedPayload()
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "assistant-delta-1",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            CreatedAt = ParseUtc("2026-03-27T11:00:01Z"),
            Type = "content.delta",
            ContentDelta = new AppServerControlProviderContentDeltaPayload
            {
                StreamKind = "assistant_text",
                Delta = "I will inspect the directory first."
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "tool-start",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "tool-1",
            CreatedAt = ParseUtc("2026-03-27T11:00:02Z"),
            Type = "item.started",
            Item = new AppServerControlProviderItemPayload
            {
                ItemType = "command",
                Status = "in_progress",
                Title = "List files",
                Detail = "Get-ChildItem"
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "assistant-final",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "assistant-item-1",
            CreatedAt = ParseUtc("2026-03-27T11:00:03Z"),
            Type = "item.completed",
            Item = new AppServerControlProviderItemPayload
            {
                ItemType = "assistant_message",
                Status = "completed",
                Detail = "Here is the final table."
            }
        });

        var snapshot = service.GetSnapshot("s1");

        Assert.NotNull(snapshot);
        var assistantEntries = snapshot!.History.Where(entry => entry.Kind == "assistant").ToList();
        Assert.Single(assistantEntries);
        Assert.Collection(
            snapshot.History.Where(entry => entry.Kind is "assistant" or "tool"),
            entry =>
            {
                Assert.Equal("assistant", entry.Kind);
                Assert.Equal("assistant-stream:turn-1", entry.EntryId);
                Assert.Equal("Here is the final table.", entry.Body);
                Assert.False(entry.Streaming);
                Assert.Equal("completed", entry.Status);
            },
            entry =>
            {
                Assert.Equal("tool", entry.Kind);
                Assert.Equal("tool:tool-1", entry.EntryId);
            });
    }

    [Fact]
    public void GetSnapshot_MergesToolLifecycleIntoSingleHistoryRow()
    {
        var service = new SessionAppServerControlHistoryService();

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "tool-start",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "tool-1",
            CreatedAt = ParseUtc("2026-03-27T12:00:00Z"),
            Type = "item.started",
            Item = new AppServerControlProviderItemPayload
            {
                ItemType = "command",
                Status = "in_progress",
                Title = "Run tests",
                Detail = "npm test"
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "tool-output",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "tool-1",
            CreatedAt = ParseUtc("2026-03-27T12:00:01Z"),
            Type = "content.delta",
            ContentDelta = new AppServerControlProviderContentDeltaPayload
            {
                StreamKind = "command_output",
                Delta = "All green"
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "tool-complete",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "tool-1",
            CreatedAt = ParseUtc("2026-03-27T12:00:02Z"),
            Type = "item.completed",
            Item = new AppServerControlProviderItemPayload
            {
                ItemType = "command",
                Status = "completed",
                Title = "Run tests",
                Detail = "npm test"
            }
        });

        var snapshot = service.GetSnapshot("s1");

        Assert.NotNull(snapshot);
        var toolEntries = snapshot!.History.Where(entry => entry.Kind == "tool").ToList();
        var tool = Assert.Single(toolEntries);
        Assert.Equal("tool:tool-1", tool.EntryId);
        Assert.Equal("Ran command", tool.Title);
        Assert.NotNull(tool.ToolPresentation);
        Assert.Equal("npm test", tool.ToolPresentation!.Subject);
        Assert.Equal("All green", tool.ToolPresentation.Evidence);
        Assert.False(tool.Streaming);
    }

    [Fact]
    public async Task Subscribe_ReplaysBacklogAndStreamsFutureEvents()
    {
        var service = new SessionAppServerControlHistoryService();
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "e1",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            CreatedAt = DateTimeOffset.UtcNow,
            Type = "session.started"
        });

        using var subscription = service.SubscribeProviderEvents("s1", afterSequence: 0);
        Assert.True(await subscription.Reader.WaitToReadAsync());
        Assert.True(subscription.Reader.TryRead(out var backlogEvent));
        Assert.Equal("e1", backlogEvent!.EventId);

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "e2",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            CreatedAt = DateTimeOffset.UtcNow,
            Type = "session.ready"
        });

        Assert.True(await subscription.Reader.WaitToReadAsync());
        Assert.True(subscription.Reader.TryRead(out var liveEvent));
        Assert.Equal("e2", liveEvent!.EventId);
        Assert.Equal(2, liveEvent.Sequence);
    }

    [Fact]
    public async Task SubscribeDeltas_StreamsCanonicalAssistantUpdates()
    {
        var service = new SessionAppServerControlHistoryService();

        using var subscription = service.SubscribeHistoryPatches("s-delta");

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "turn-start",
            SessionId = "s-delta",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            CreatedAt = ParseUtc("2026-03-28T10:00:00Z"),
            Type = "turn.started",
            TurnStarted = new AppServerControlProviderTurnStartedPayload
            {
                Model = "gpt-5.4",
                Effort = "high"
            }
        });

        Assert.True(await subscription.Reader.WaitToReadAsync());
        Assert.True(subscription.Reader.TryRead(out _));

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "assistant-delta",
            SessionId = "s-delta",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "assistant-1",
            CreatedAt = ParseUtc("2026-03-28T10:00:01Z"),
            Type = "content.delta",
            ContentDelta = new AppServerControlProviderContentDeltaPayload
            {
                StreamKind = "assistant_text",
                Delta = "Hello"
            }
        });

        Assert.True(await subscription.Reader.WaitToReadAsync());
        Assert.True(subscription.Reader.TryRead(out var delta));
        Assert.NotNull(delta);
        Assert.Equal("s-delta", delta!.SessionId);
        Assert.Equal(2, delta.LatestSequence);
        Assert.Equal("running", delta.CurrentTurn.State);
        Assert.Equal("Hello", delta.Streams.AssistantText);
        Assert.Equal(1, delta.HistoryCount);
        var historyEntry = Assert.Single(delta.HistoryUpserts);
        Assert.Equal("assistant:assistant-1", historyEntry.EntryId);
        Assert.Equal("assistant", historyEntry.Kind);
        Assert.Equal("Hello", historyEntry.Body);
        Assert.True(historyEntry.Streaming);
    }

    [Fact]
    public void GetSnapshot_SummarizesCommandAndFileReadOutputForScreenUse()
    {
        var service = new SessionAppServerControlHistoryService();

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "turn-start",
            SessionId = "s-screen",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            CreatedAt = ParseUtc("2026-03-29T09:00:00Z"),
            Type = "turn.started",
            TurnStarted = new AppServerControlProviderTurnStartedPayload()
        });

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "cmd-start",
            SessionId = "s-screen",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "cmd-1",
            CreatedAt = ParseUtc("2026-03-29T09:00:01Z"),
            Type = "item.started",
            Item = new AppServerControlProviderItemPayload
            {
                ItemType = "command_execution",
                Status = "in_progress",
                Title = "Tool started",
                Detail = ".midterm/AGENTS.md",
                ToolPresentation = new AppServerControlToolPresentation
                {
                    Category = "read",
                    Label = "Read file",
                    ToolName = "Read",
                    Subject = ".midterm/AGENTS.md",
                    Paths = [".midterm/AGENTS.md"]
                }
            }
        });

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "cmd-out",
            SessionId = "s-screen",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "cmd-1",
            CreatedAt = ParseUtc("2026-03-29T09:00:02Z"),
            Type = "content.delta",
            ContentDelta = new AppServerControlProviderContentDeltaPayload
            {
                StreamKind = "command_output",
                Delta = string.Join(
                    '\n',
                    Enumerable.Range(1, 12).Select(index => string.Create(CultureInfo.InvariantCulture, $"line {index}")))
            }
        });

        var snapshot = service.GetSnapshot("s-screen");
        Assert.NotNull(snapshot);
        var toolEntry = Assert.Single(snapshot!.History, entry => entry.Kind == "tool");
        Assert.Equal("Read file", toolEntry.Title);
        Assert.Equal("command_output", toolEntry.ItemType);
        Assert.Contains(".midterm/AGENTS.md", toolEntry.Body, StringComparison.Ordinal);
        Assert.Contains("line 1", toolEntry.Body, StringComparison.Ordinal);
        Assert.Contains("line 4", toolEntry.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("line 5", toolEntry.Body, StringComparison.Ordinal);
        Assert.Contains("8 lines omitted", toolEntry.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSnapshot_PreservesCommandTextWhenCommandOutputTailOmitsEarlierContent()
    {
        var service = new SessionAppServerControlHistoryService();

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "cmd-start",
            SessionId = "s-command-tail",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "cmd-1",
            CreatedAt = ParseUtc("2026-04-08T18:00:00Z"),
            Type = "item.started",
            Item = new AppServerControlProviderItemPayload
            {
                ItemType = "command_execution",
                Status = "in_progress",
                Title = "Tool started",
                Detail = "codex -m gpt-5.4",
                ToolPresentation = new AppServerControlToolPresentation
                {
                    Category = "command",
                    Label = "Ran command",
                    Subject = "codex -m gpt-5.4"
                }
            }
        });

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "cmd-out",
            SessionId = "s-command-tail",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "cmd-1",
            CreatedAt = ParseUtc("2026-04-08T18:00:01Z"),
            Type = "content.delta",
            ContentDelta = new AppServerControlProviderContentDeltaPayload
            {
                StreamKind = "command_output",
                Delta = string.Join(
                    '\n',
                    Enumerable.Range(1, 32).Select(index => string.Create(CultureInfo.InvariantCulture, $"line {index} {new string('x', 520)}")))
            }
        });

        var snapshot = service.GetSnapshot("s-command-tail");

        Assert.NotNull(snapshot);
        var toolEntry = Assert.Single(snapshot!.History, entry => entry.Kind == "tool");
        Assert.Equal("command_output", toolEntry.ItemType);
        Assert.Equal("codex -m gpt-5.4", toolEntry.ToolPresentation?.Subject);
        Assert.Contains('\n', toolEntry.Body);
        Assert.Contains("line ", toolEntry.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSnapshot_KeepsSeparateCommandOutputHistoryPerCommandWhenStreamsLackItemIds()
    {
        var service = new SessionAppServerControlHistoryService();

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "turn-start",
            SessionId = "s-multi-command",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            CreatedAt = ParseUtc("2026-04-05T00:00:00Z"),
            Type = "turn.started",
            TurnStarted = new AppServerControlProviderTurnStartedPayload()
        });

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "cmd-1-start",
            SessionId = "s-multi-command",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "cmd-1",
            CreatedAt = ParseUtc("2026-04-05T00:00:01Z"),
            Type = "item.started",
            Item = new AppServerControlProviderItemPayload
            {
                ItemType = "command_execution",
                Status = "in_progress",
                Title = "Tool started",
                Detail = "git describe --tags --abbrev=0"
            }
        });

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "cmd-1-out",
            SessionId = "s-multi-command",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            CreatedAt = ParseUtc("2026-04-05T00:00:02Z"),
            Type = "content.delta",
            ContentDelta = new AppServerControlProviderContentDeltaPayload
            {
                StreamKind = "command_output",
                Delta = "v9.0.15-dev"
            }
        });

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "cmd-2-start",
            SessionId = "s-multi-command",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "cmd-2",
            CreatedAt = ParseUtc("2026-04-05T00:00:03Z"),
            Type = "item.started",
            Item = new AppServerControlProviderItemPayload
            {
                ItemType = "command_execution",
                Status = "in_progress",
                Title = "Tool started",
                Detail = "git show --stat --summary --format=fuller -n 1 HEAD"
            }
        });

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "cmd-2-out",
            SessionId = "s-multi-command",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            CreatedAt = ParseUtc("2026-04-05T00:00:04Z"),
            Type = "content.delta",
            ContentDelta = new AppServerControlProviderContentDeltaPayload
            {
                StreamKind = "command_output",
                Delta = "commit f1b5a5d5"
            }
        });

        var snapshot = service.GetSnapshot("s-multi-command");
        Assert.NotNull(snapshot);

        var toolEntries = snapshot!.History
            .Where(entry => entry.Kind == "tool")
            .OrderBy(entry => entry.Order)
            .ToList();

        Assert.Equal(2, toolEntries.Count);
        Assert.Equal("tool:cmd-1", toolEntries[0].EntryId);
        Assert.Equal("tool:cmd-2", toolEntries[1].EntryId);
        Assert.Contains("git describe --tags --abbrev=0", toolEntries[0].Body, StringComparison.Ordinal);
        Assert.Contains("v9.0.15-dev", toolEntries[0].Body, StringComparison.Ordinal);
        Assert.Contains("git show --stat --summary --format=fuller -n 1 HEAD", toolEntries[1].Body, StringComparison.Ordinal);
        Assert.Contains("commit f1b5a5d5", toolEntries[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSnapshot_KeepsCommandOutputTailAfterCommandExecutionCompletesAndLaterCommandsStart()
    {
        var service = new SessionAppServerControlHistoryService();

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "turn-start",
            SessionId = "s-command-tail",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            CreatedAt = ParseUtc("2026-04-06T09:00:00Z"),
            Type = "turn.started",
            TurnStarted = new AppServerControlProviderTurnStartedPayload()
        });

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "cmd-1-start",
            SessionId = "s-command-tail",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "cmd-1",
            CreatedAt = ParseUtc("2026-04-06T09:00:01Z"),
            Type = "item.started",
            Item = new AppServerControlProviderItemPayload
            {
                ItemType = "command_execution",
                Status = "in_progress",
                Title = "Tool started",
                Detail = "git describe --tags --abbrev=0"
            }
        });

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "cmd-1-out",
            SessionId = "s-command-tail",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "cmd-1",
            CreatedAt = ParseUtc("2026-04-06T09:00:02Z"),
            Type = "content.delta",
            ContentDelta = new AppServerControlProviderContentDeltaPayload
            {
                StreamKind = "command_output",
                Delta = "v9.0.16-dev"
            }
        });

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "cmd-1-complete",
            SessionId = "s-command-tail",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "cmd-1",
            CreatedAt = ParseUtc("2026-04-06T09:00:03Z"),
            Type = "item.completed",
            Item = new AppServerControlProviderItemPayload
            {
                ItemType = "command_execution",
                Status = "completed",
                Title = "Command",
                Detail = "git describe --tags --abbrev=0"
            }
        });

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "cmd-2-start",
            SessionId = "s-command-tail",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "cmd-2",
            CreatedAt = ParseUtc("2026-04-06T09:00:04Z"),
            Type = "item.started",
            Item = new AppServerControlProviderItemPayload
            {
                ItemType = "command_execution",
                Status = "in_progress",
                Title = "Tool started",
                Detail = "git show --stat -n 1 HEAD"
            }
        });

        var snapshot = service.GetSnapshot("s-command-tail");
        Assert.NotNull(snapshot);

        var toolEntries = snapshot!.History
            .Where(entry => entry.Kind == "tool")
            .OrderBy(entry => entry.Order)
            .ToList();

        Assert.Equal(2, toolEntries.Count);
        Assert.Equal("tool:cmd-1", toolEntries[0].EntryId);
        Assert.Equal("command_execution", toolEntries[0].ItemType);
        Assert.Contains("git describe --tags --abbrev=0", toolEntries[0].Body, StringComparison.Ordinal);
        Assert.Contains("v9.0.16-dev", toolEntries[0].Body, StringComparison.Ordinal);
        Assert.Equal("tool:cmd-2", toolEntries[1].EntryId);
        Assert.Equal("command_execution", toolEntries[1].ItemType);
        Assert.Contains("git show --stat -n 1 HEAD", toolEntries[1].Body, StringComparison.Ordinal);
    }

    [Fact]
    public void GetSnapshot_AdoptsProvisionalCommandOutputEntryWhenCompletionArrivesAfterOutput()
    {
        var service = new SessionAppServerControlHistoryService();

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "turn-start",
            SessionId = "s-command-adopt",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            CreatedAt = ParseUtc("2026-04-06T10:00:00Z"),
            Type = "turn.started",
            TurnStarted = new AppServerControlProviderTurnStartedPayload()
        });

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "cmd-out",
            SessionId = "s-command-adopt",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            CreatedAt = ParseUtc("2026-04-06T10:00:01Z"),
            Type = "content.delta",
            ContentDelta = new AppServerControlProviderContentDeltaPayload
            {
                StreamKind = "command_output",
                Delta = "## dev...origin/dev"
            }
        });

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "cmd-complete",
            SessionId = "s-command-adopt",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "cmd-1",
            CreatedAt = ParseUtc("2026-04-06T10:00:02Z"),
            Type = "item.completed",
            Item = new AppServerControlProviderItemPayload
            {
                ItemType = "command_execution",
                Status = "completed",
                Title = "Tool completed",
                Detail = "git status --short --branch"
            }
        });

        var snapshot = service.GetSnapshot("s-command-adopt");
        Assert.NotNull(snapshot);

        var toolEntry = Assert.Single(snapshot!.History, entry => entry.Kind == "tool");
        Assert.Equal("tool:cmd-1", toolEntry.EntryId);
        Assert.Equal("cmd-1", toolEntry.ItemId);
        Assert.Equal("command_execution", toolEntry.ItemType);
        Assert.Contains("git status --short --branch", toolEntry.Body, StringComparison.Ordinal);
        Assert.Contains("## dev...origin/dev", toolEntry.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("tool:command_output", snapshot.History.Select(entry => entry.EntryId), StringComparer.Ordinal);
    }

    [Fact]
    public void Append_WritesGuidNamedScreenLogWithRenderHintsInDevMode()
    {
        var storeDirectory = Path.Combine(Path.GetTempPath(), "midterm-appServerControl-history-tests", Guid.NewGuid().ToString("N"));
        var screenLogDirectory = Path.Combine(Path.GetTempPath(), "midterm-appServerControl-screen-log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeDirectory);
        Directory.CreateDirectory(screenLogDirectory);

        try
        {
            var service = new SessionAppServerControlHistoryService(
                storeDirectory: storeDirectory,
                enableScreenLogging: true,
                screenLogDirectory: screenLogDirectory);

            service.Append(new AppServerControlProviderEvent
            {
                EventId = "turn-start",
                SessionId = "s-log",
                Provider = "codex",
                ThreadId = "thread-1",
                TurnId = "turn-1",
                CreatedAt = ParseUtc("2026-03-28T11:00:00Z"),
                Type = "turn.started",
                TurnStarted = new AppServerControlProviderTurnStartedPayload
                {
                    Model = "gpt-5.4",
                    Effort = "high"
                }
            });

            service.Append(new AppServerControlProviderEvent
            {
                EventId = "tool-output",
                SessionId = "s-log",
                Provider = "codex",
                ThreadId = "thread-1",
                TurnId = "turn-1",
                ItemId = "tool-1",
                CreatedAt = ParseUtc("2026-03-28T11:00:01Z"),
                Type = "item.completed",
                Item = new AppServerControlProviderItemPayload
                {
                ItemType = "command_output",
                Status = "completed",
                Title = "git diff --stat",
                ToolPresentation = new AppServerControlToolPresentation
                {
                    Category = "command",
                    Label = "Ran command",
                    Subject = "git diff --stat",
                    Evidence = string.Join(
                        '\n',
                        Enumerable.Range(1, 6).Select(
                            index => string.Create(CultureInfo.InvariantCulture, $"line {index}: tool output"))),
                    EvidenceKind = "output",
                    TotalLineCount = 10,
                    OmittedLineCount = 4
                }
                }
            });

            Assert.NotNull(Assert.Single(service.GetSnapshot("s-log")!.History).ToolPresentation);

            var logPath = Assert.Single(Directory.GetFiles(screenLogDirectory, "*.appServerControllog.jsonl"));
            Assert.Matches(@"[0-9a-f]{32}\.appServerControllog\.jsonl$", Path.GetFileName(logPath));

            var lines = File.ReadAllLines(logPath);
            Assert.True(lines.Length >= 3);

            using var header = JsonDocument.Parse(lines[0]);
            Assert.Equal("midterm-appServerControl-screen-log-v1", header.RootElement.GetProperty("format").GetString());
            Assert.Equal("header", header.RootElement.GetProperty("recordType").GetString());
            Assert.Equal("s-log", header.RootElement.GetProperty("sessionId").GetString());

            using var delta = JsonDocument.Parse(lines[^1]);
            Assert.Equal("screen_delta", delta.RootElement.GetProperty("recordType").GetString());
            Assert.Equal(2, delta.RootElement.GetProperty("latestSequence").GetInt64());
            var historyUpserts = delta.RootElement.GetProperty("historyUpserts");
            Assert.Equal(1, historyUpserts.GetArrayLength());
            var toolEntry = historyUpserts[0];
            Assert.Equal("tool", toolEntry.GetProperty("kind").GetString());
            Assert.Equal("Tool", toolEntry.GetProperty("label").GetString());
            Assert.Equal("tool", toolEntry.GetProperty("renderMode").GetString());
            Assert.False(toolEntry.GetProperty("collapsedByDefault").GetBoolean());
            Assert.Equal("command", toolEntry.GetProperty("toolPresentation").GetProperty("category").GetString());
            Assert.Contains("line 6: tool output", toolEntry.GetProperty("body").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(storeDirectory))
            {
                Directory.Delete(storeDirectory, recursive: true);
            }

            if (Directory.Exists(screenLogDirectory))
            {
                Directory.Delete(screenLogDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void GetSnapshot_ReloadsCanonicalStateFromPersistedStoreWithoutReplayingEventBacklog()
    {
        var storeDirectory = Path.Combine(Path.GetTempPath(), "midterm-appServerControl-history-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeDirectory);

        try
        {
            var service = new SessionAppServerControlHistoryService(storeDirectory: storeDirectory);

            service.Append(new AppServerControlProviderEvent
            {
                EventId = "persist-session",
                SessionId = "s-persist",
                Provider = "codex",
                ThreadId = "thread-persist",
                CreatedAt = ParseUtc("2026-04-07T08:10:00Z"),
                Type = "session.started",
                SessionState = new AppServerControlProviderSessionStatePayload
                {
                    State = "starting",
                    StateLabel = "Starting",
                    Reason = "Booting"
                }
            });
            service.Append(new AppServerControlProviderEvent
            {
                EventId = "persist-user",
                SessionId = "s-persist",
                Provider = "codex",
                ThreadId = "thread-persist",
                TurnId = "turn-persist",
                ItemId = "user:turn-persist",
                CreatedAt = ParseUtc("2026-04-07T08:10:01Z"),
                Type = "item.completed",
                Item = new AppServerControlProviderItemPayload
                {
                    ItemType = "user_message",
                    Status = "completed",
                    Title = "User message",
                    Detail = "Persist this canonical App Server Controller history."
                }
            });

            var storePath = Path.Combine(storeDirectory, $"{Uri.EscapeDataString("s-persist")}.json");
            Assert.True(
                WaitForCondition(() => File.Exists(storePath) && new FileInfo(storePath).Length > 0),
                "Expected canonical App Server Controller state store to be written.");

            var reloaded = new SessionAppServerControlHistoryService(storeDirectory: storeDirectory);
            var snapshot = reloaded.GetSnapshot("s-persist");
            var events = reloaded.GetProviderEvents("s-persist");

            Assert.NotNull(snapshot);
            Assert.Equal(2, snapshot!.LatestSequence);
            Assert.Contains(
                snapshot.History,
                entry => entry.Kind == "user" &&
                         entry.Body.Contains("Persist this canonical App Server Controller history.", StringComparison.Ordinal));
            Assert.Equal(2, events.LatestSequence);
            Assert.Empty(events.Events);
            Assert.Single(Directory.GetFiles(storeDirectory, "*.json"));
            Assert.Empty(Directory.GetFiles(storeDirectory, "*.ndjson"));
        }
        finally
        {
            if (Directory.Exists(storeDirectory))
            {
                Directory.Delete(storeDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void Append_WritesDiffScreenLogUsingDiffRenderHintsInsteadOfRawGitPreamble()
    {
        var storeDirectory = Path.Combine(Path.GetTempPath(), "midterm-appServerControl-history-tests", Guid.NewGuid().ToString("N"));
        var screenLogDirectory = Path.Combine(Path.GetTempPath(), "midterm-appServerControl-screen-log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeDirectory);
        Directory.CreateDirectory(screenLogDirectory);

        try
        {
            var service = new SessionAppServerControlHistoryService(
                storeDirectory: storeDirectory,
                enableScreenLogging: true,
                screenLogDirectory: screenLogDirectory);

            service.Append(new AppServerControlProviderEvent
            {
                EventId = "diff-updated",
                SessionId = "s-diff-log",
                Provider = "codex",
                ThreadId = "thread-1",
                TurnId = "turn-1",
                CreatedAt = ParseUtc("2026-04-01T00:43:33Z"),
                Type = "diff.updated",
                DiffUpdated = new AppServerControlProviderDiffUpdatedPayload
                {
                    UnifiedDiff = string.Join(
                        '\n',
                        new[]
                        {
                            "diff --git a/report.md b/report.md",
                            "new file mode 100644",
                            "index 0000000..1111111",
                            "--- /dev/null",
                            "+++ b/report.md",
                            "@@ -0,0 +1,3 @@",
                            "+line 1",
                            "+line 2",
                            "+line 3"
                        })
                }
            });

            var logPath = Assert.Single(Directory.GetFiles(screenLogDirectory, "*.appServerControllog.jsonl"));
            var lastLine = File.ReadLines(logPath).Last();
            using var delta = JsonDocument.Parse(lastLine);
            var historyUpserts = delta.RootElement.GetProperty("historyUpserts");
            Assert.Equal(1, historyUpserts.GetArrayLength());
            var diffEntry = historyUpserts[0];

            Assert.Equal("diff", diffEntry.GetProperty("kind").GetString());
            Assert.Equal("diff", diffEntry.GetProperty("renderMode").GetString());
            Assert.False(diffEntry.GetProperty("collapsedByDefault").GetBoolean());
            Assert.Equal("report.md", diffEntry.GetProperty("preview").GetString());
            Assert.DoesNotContain("diff --git", diffEntry.GetProperty("body").GetString(), StringComparison.Ordinal);
            Assert.Contains("@@ -0,0 +1,3 @@", diffEntry.GetProperty("body").GetString(), StringComparison.Ordinal);
        }
        finally
        {
            if (Directory.Exists(storeDirectory))
            {
                Directory.Delete(storeDirectory, recursive: true);
            }

            if (Directory.Exists(screenLogDirectory))
            {
                Directory.Delete(screenLogDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void Append_CompactsHugeToolOutputAcrossEventsSnapshotAndScreenLog()
    {
        var storeDirectory = Path.Combine(Path.GetTempPath(), "midterm-appServerControl-history-tests", Guid.NewGuid().ToString("N"));
        var screenLogDirectory = Path.Combine(Path.GetTempPath(), "midterm-appServerControl-screen-log-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(storeDirectory);
        Directory.CreateDirectory(screenLogDirectory);

        try
        {
            var service = new SessionAppServerControlHistoryService(
                storeDirectory: storeDirectory,
                enableScreenLogging: true,
                screenLogDirectory: screenLogDirectory);

            service.Append(new AppServerControlProviderEvent
            {
                EventId = "cmd-start",
                SessionId = "s-huge",
                Provider = "codex",
                ThreadId = "thread-1",
                TurnId = "turn-1",
                ItemId = "cmd-1",
                CreatedAt = ParseUtc("2026-03-29T11:00:00Z"),
                Type = "item.started",
                Item = new AppServerControlProviderItemPayload
                {
                ItemType = "command_execution",
                Status = "in_progress",
                Title = "Tool started",
                Detail = "src/Ai.Tlbx.MidTerm/Program.cs",
                ToolPresentation = new AppServerControlToolPresentation
                {
                    Category = "read",
                    Label = "Read file",
                    Subject = "src/Ai.Tlbx.MidTerm/Program.cs",
                    Paths = ["src/Ai.Tlbx.MidTerm/Program.cs"]
                }
                }
            });

            var giantLine = new string('x', 120_000);
            service.Append(new AppServerControlProviderEvent
            {
                EventId = "cmd-output",
                SessionId = "s-huge",
                Provider = "codex",
                ThreadId = "thread-1",
                TurnId = "turn-1",
                ItemId = "cmd-1",
                CreatedAt = ParseUtc("2026-03-29T11:00:01Z"),
                Type = "content.delta",
                ContentDelta = new AppServerControlProviderContentDeltaPayload
                {
                    StreamKind = "command_output",
                    Delta = giantLine
                }
            });

            var events = service.GetProviderEvents("s-huge");
            var retainedOutputEvent = Assert.Single(events.Events, evt => evt.ContentDelta?.StreamKind == "command_output");
            Assert.True(retainedOutputEvent.ContentDelta!.Delta.Length < 4_000);

            var snapshot = service.GetSnapshot("s-huge");
            Assert.NotNull(snapshot);
            Assert.True(snapshot!.Streams.CommandOutput.Length < 20_000);

            var toolEntry = Assert.Single(snapshot.History, entry => entry.Kind == "tool");
            Assert.True(toolEntry.Body.Length <= 4_096);
            Assert.Contains("Read file", toolEntry.Title, StringComparison.Ordinal);
            Assert.EndsWith("…", toolEntry.ToolPresentation?.Evidence, StringComparison.Ordinal);

            var logPath = Assert.Single(Directory.GetFiles(screenLogDirectory, "*.appServerControllog.jsonl"));
            var lastLine = File.ReadLines(logPath).Last();
            Assert.True(lastLine.Length < 20_000);
        }
        finally
        {
            if (Directory.Exists(storeDirectory))
            {
                Directory.Delete(storeDirectory, recursive: true);
            }

            if (Directory.Exists(screenLogDirectory))
            {
                Directory.Delete(screenLogDirectory, recursive: true);
            }
        }
    }

    [Fact]
    public void GetSnapshot_ProjectsInformationalRuntimeNoticesIntoHistoryHistory()
    {
        var service = new SessionAppServerControlHistoryService();

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "e-runtime-info",
            SessionId = "s-runtime-info",
            Provider = "codex",
            ThreadId = "thread-1",
            CreatedAt = ParseUtc("2026-04-04T10:00:00Z"),
            Type = "model.rerouted",
            RuntimeMessage = new AppServerControlProviderRuntimeMessagePayload
            {
                Message = "Codex rerouted the model from gpt-5.4 to gpt-5.4-mini.",
                Detail = "Service tier fallback"
            }
        });

        var snapshot = service.GetSnapshot("s-runtime-info");

        Assert.NotNull(snapshot);
        var notice = Assert.Single(snapshot!.Notices);
        Assert.Equal("model.rerouted", notice.Type);

        var transcriptEntry = Assert.Single(snapshot.History);
        Assert.Equal("system", transcriptEntry.Kind);
        Assert.Equal("Model rerouted", transcriptEntry.Title);
        Assert.Contains("rerouted the model", transcriptEntry.Body, StringComparison.Ordinal);
        Assert.Contains("Service tier fallback", transcriptEntry.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GetSnapshot_KeepsTelemetryNoticesOutOfConversationHistory()
    {
        var service = new SessionAppServerControlHistoryService();
        using var subscription = service.SubscribeHistoryPatches("s-runtime-telemetry");

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "event-telemetry",
            SessionId = "s-runtime-telemetry",
            Provider = "codex",
            ThreadId = "thread-1",
            CreatedAt = ParseUtc("2026-04-04T10:00:00Z"),
            Type = "thread.token-usage.updated",
            RuntimeNoticeOnly = new AppServerControlProviderRuntimeMessagePayload
            {
                Message = "Codex context window updated.",
                Detail = "Used 100 tokens, window 258400"
            }
        });

        var snapshot = service.GetSnapshot("s-runtime-telemetry");

        Assert.NotNull(snapshot);
        Assert.Single(snapshot!.Notices);
        Assert.Empty(snapshot.History);

        Assert.True(await subscription.Reader.WaitToReadAsync());
        Assert.True(subscription.Reader.TryRead(out var patch));
        Assert.Empty(patch!.HistoryUpserts);
        Assert.Equal("Codex context window updated.", Assert.Single(patch.NoticeUpserts).Message);
    }

    [Fact]
    public void ProviderEventJson_PreservesTimelineSuppressionAcrossProcessBoundary()
    {
        var source = new AppServerControlProviderEvent
        {
            EventId = "event-telemetry-json",
            SessionId = "s-runtime-telemetry",
            Provider = "codex",
            ThreadId = "thread-1",
            CreatedAt = ParseUtc("2026-04-04T10:00:00Z"),
            Type = "thread.token-usage.updated",
            RuntimeNoticeOnly = new AppServerControlProviderRuntimeMessagePayload
            {
                Message = "Codex context window updated."
            }
        };

        var json = JsonSerializer.Serialize(
            source,
            AppServerControlProviderEventJsonContext.Default.AppServerControlProviderEvent);
        var roundTripped = JsonSerializer.Deserialize(
            json,
            AppServerControlProviderEventJsonContext.Default.AppServerControlProviderEvent);

        Assert.Equal("Codex context window updated.", roundTripped?.RuntimeNoticeOnly?.Message);
    }

    [Fact]
    public void GetSnapshot_SanitizesAndDeduplicatesRuntimeNoticeHistoryBody()
    {
        var service = new SessionAppServerControlHistoryService();

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "e-runtime-sanitize",
            SessionId = "s-runtime-sanitize",
            Provider = "codex",
            ThreadId = "thread-1",
            CreatedAt = ParseUtc("2026-04-08T10:05:57Z"),
            Type = "runtime.warning",
            RuntimeMessage = new AppServerControlProviderRuntimeMessagePayload
            {
                Message = "\u001b[2m2026-04-08T10:05:57.503361Z ERROR codex_core::tools::router: error=apply_patch verification failed\u001b[0m",
                Detail = "\u001b[2m2026-04-08T10:05:57.503361Z ERROR codex_core::tools::router: error=apply_patch verification failed\u001b[0m"
            }
        });

        var snapshot = service.GetSnapshot("s-runtime-sanitize");

        Assert.NotNull(snapshot);
        var transcriptEntry = Assert.Single(snapshot!.History);
        Assert.Equal("system", transcriptEntry.Kind);
        Assert.DoesNotContain('\u001b', transcriptEntry.Body);
        Assert.DoesNotContain("[0m", transcriptEntry.Body, StringComparison.Ordinal);
        Assert.Equal(
            "2026-04-08T10:05:57.503361Z ERROR codex_core::tools::router: error=apply_patch verification failed",
            transcriptEntry.Body);

        var runtimeNotice = Assert.Single(snapshot.Notices);
        Assert.Equal(transcriptEntry.Body, runtimeNotice.Message);
        Assert.Equal(transcriptEntry.Body, runtimeNotice.Detail);
    }

    [Fact]
    public void GetSnapshot_RendersReasoningAndPlanItemsUsingDedicatedHistoryKinds()
    {
        var service = new SessionAppServerControlHistoryService();

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "task-start",
            SessionId = "s-task",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "task-1",
            CreatedAt = ParseUtc("2026-04-04T10:10:00Z"),
            Type = "item.started",
            Item = new AppServerControlProviderItemPayload
            {
                ItemType = "reasoning",
                Status = "in_progress",
                Title = "Reasoning",
                Detail = "Inspecting the workspace."
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "task-complete",
            SessionId = "s-task",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "task-1",
            CreatedAt = ParseUtc("2026-04-04T10:10:02Z"),
            Type = "item.completed",
            Item = new AppServerControlProviderItemPayload
            {
                ItemType = "reasoning",
                Status = "completed",
                Title = "Reasoning completed",
                Detail = "Workspace inspection complete."
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "plan-item",
            SessionId = "s-task",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "plan-1",
            CreatedAt = ParseUtc("2026-04-04T10:10:03Z"),
            Type = "item.completed",
            Item = new AppServerControlProviderItemPayload
            {
                ItemType = "plan",
                Status = "completed",
                Title = "Plan completed",
                Detail = "1. Inspect\n2. Patch\n3. Verify"
            }
        });

        var snapshot = service.GetSnapshot("s-task");

        Assert.NotNull(snapshot);
        Assert.Contains(snapshot!.History, entry => entry.Kind == "reasoning" && entry.ItemId == "task-1");
        Assert.Contains(snapshot.History, entry => entry.Kind == "plan" && entry.ItemId == "plan-1");
    }

    [Fact]
    public void GetEvents_PreservesCanonicalTaskPayloads()
    {
        var service = new SessionAppServerControlHistoryService();

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "task-progress",
            SessionId = "s-task-events",
            Provider = "codex",
            ThreadId = "thread-1",
            TurnId = "turn-1",
            ItemId = "task-1",
            CreatedAt = ParseUtc("2026-04-15T08:10:00Z"),
            Type = "task.progress",
            Task = new AppServerControlProviderTaskPayload
            {
                TaskId = "task-1",
                Status = "waiting",
                Description = "Waited for background terminal  npm run lint",
                Summary = "Waiting for background terminal completion",
                LastToolName = "npm run lint"
            }
        });

        var providerEvents = service.GetProviderEvents("s-task-events");

        var taskEvent = Assert.Single(providerEvents.Events);
        Assert.Equal("task.progress", taskEvent.Type);
        Assert.NotNull(taskEvent.Task);
        Assert.Equal("task-1", taskEvent.Task!.TaskId);
        Assert.Equal("waiting", taskEvent.Task.Status);
        Assert.Equal("Waited for background terminal  npm run lint", taskEvent.Task.Description);
        Assert.Equal("npm run lint", taskEvent.Task.LastToolName);
    }

    [Fact]
    public void GetSnapshot_EnrichesHistoryEntriesWithClickableFileMentionsAndImagePreviews()
    {
        var service = new SessionAppServerControlHistoryService();
        var tempRoot = Path.Combine(Path.GetTempPath(), "midterm-appServerControl-inline-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var imagePath = Path.Combine(tempRoot, "preview.png");
            File.WriteAllText(imagePath, "preview");

            var folderPath = Path.Combine(tempRoot, "docs");
            Directory.CreateDirectory(folderPath);

            service.Append(new AppServerControlProviderEvent
            {
                EventId = "e-inline-file",
                SessionId = "s-inline-file",
                Provider = "codex",
                ThreadId = "thread-1",
                TurnId = "turn-1",
                ItemId = "assistant-1",
                CreatedAt = ParseUtc("2026-04-08T12:00:00Z"),
                Type = "item.completed",
                Item = new AppServerControlProviderItemPayload
                {
                    ItemType = "assistant_message",
                    Status = "completed",
                    Title = $"Edited {imagePath}",
                    Detail = $"Inspect {imagePath} and {folderPath}{Path.DirectorySeparatorChar}."
                }
            });

            var snapshot = service.GetSnapshot("s-inline-file");

            Assert.NotNull(snapshot);
            var transcriptEntry = Assert.Single(snapshot!.History);
            Assert.Contains(
                transcriptEntry.FileMentions,
                mention => mention.Field == "body" &&
                           mention.DisplayText.Contains("preview.png", StringComparison.Ordinal) &&
                           mention.Exists &&
                           string.Equals(mention.ResolvedPath, imagePath, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                transcriptEntry.FileMentions,
                mention => mention.Field == "body" &&
                           mention.DisplayText.Contains("docs", StringComparison.Ordinal) &&
                           mention.Exists &&
                           mention.IsDirectory);
            Assert.Contains(
                transcriptEntry.ImagePreviews,
                preview => string.Equals(preview.ResolvedPath, imagePath, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void GetSnapshot_DefersFileMentionEnrichmentForStreamingHistoryEntriesUntilTheySettle()
    {
        var service = new SessionAppServerControlHistoryService();
        var tempRoot = Path.Combine(Path.GetTempPath(), "midterm-appServerControl-inline-streaming-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var imagePath = Path.Combine(tempRoot, "preview.png");
            File.WriteAllText(imagePath, "preview");
            var detail = $"Inspect {imagePath}";

            service.Append(new AppServerControlProviderEvent
            {
                EventId = "turn-start",
                SessionId = "s-inline-streaming",
                Provider = "codex",
                ThreadId = "thread-1",
                TurnId = "turn-1",
                CreatedAt = ParseUtc("2026-04-09T00:58:12Z"),
                Type = "turn.started",
                TurnStarted = new AppServerControlProviderTurnStartedPayload()
            });
            service.Append(new AppServerControlProviderEvent
            {
                EventId = "assistant-delta",
                SessionId = "s-inline-streaming",
                Provider = "codex",
                ThreadId = "thread-1",
                TurnId = "turn-1",
                CreatedAt = ParseUtc("2026-04-09T00:58:13Z"),
                Type = "content.delta",
                ContentDelta = new AppServerControlProviderContentDeltaPayload
                {
                    StreamKind = "assistant_text",
                    Delta = detail
                }
            });

            var streamingSnapshot = service.GetSnapshot("s-inline-streaming");

            Assert.NotNull(streamingSnapshot);
            var streamingEntry = Assert.Single(streamingSnapshot!.History);
            Assert.True(streamingEntry.Streaming);
            Assert.Empty(streamingEntry.FileMentions);
            Assert.Empty(streamingEntry.ImagePreviews);

            service.Append(new AppServerControlProviderEvent
            {
                EventId = "assistant-final",
                SessionId = "s-inline-streaming",
                Provider = "codex",
                ThreadId = "thread-1",
                TurnId = "turn-1",
                ItemId = "assistant-1",
                CreatedAt = ParseUtc("2026-04-09T00:58:14Z"),
                Type = "item.completed",
                Item = new AppServerControlProviderItemPayload
                {
                    ItemType = "assistant_message",
                    Status = "completed",
                    Detail = detail
                }
            });

            var settledSnapshot = service.GetSnapshot("s-inline-streaming");

            Assert.NotNull(settledSnapshot);
            var settledEntry = Assert.Single(settledSnapshot!.History);
            Assert.False(settledEntry.Streaming);
            Assert.Contains(
                settledEntry.FileMentions,
                mention => mention.Field == "body" &&
                           string.Equals(mention.ResolvedPath, imagePath, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(
                settledEntry.ImagePreviews,
                preview => string.Equals(preview.ResolvedPath, imagePath, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void HasHistory_TracksWhetherCanonicalAppServerControlEventsExist()
    {
        var service = new SessionAppServerControlHistoryService();

        Assert.False(service.HasHistory("s1"));

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "e1",
            SessionId = "s1",
            Provider = "codex",
            ThreadId = "thread-1",
            CreatedAt = DateTimeOffset.UtcNow,
            Type = "session.started"
        });

        Assert.True(service.HasHistory("s1"));
        Assert.False(service.HasHistory("s2"));
    }

    [Fact]
    public void GetSnapshot_PreservesCanonicalAgentStateAndAgentErrorRuntimeItemTypes()
    {
        var service = new SessionAppServerControlHistoryService();

        service.Append(new AppServerControlProviderEvent
        {
            EventId = "agent-state",
            SessionId = "s-agent-runtime",
            Provider = "codex",
            ThreadId = "thread-1",
            CreatedAt = ParseUtc("2026-04-09T08:10:00Z"),
            Type = "agent.state",
            RuntimeMessage = new AppServerControlProviderRuntimeMessagePayload
            {
                Message = "codex_apps starting."
            }
        });
        service.Append(new AppServerControlProviderEvent
        {
            EventId = "agent-error",
            SessionId = "s-agent-runtime",
            Provider = "codex",
            ThreadId = "thread-1",
            CreatedAt = ParseUtc("2026-04-09T08:10:01Z"),
            Type = "agent.error",
            RuntimeMessage = new AppServerControlProviderRuntimeMessagePayload
            {
                Message = "[features].collab is deprecated. Use [features].multi_agent instead."
            }
        });

        var snapshot = service.GetSnapshot("s-agent-runtime");

        Assert.NotNull(snapshot);
        Assert.Collection(
            snapshot!.History.OrderBy(entry => entry.Order),
            entry =>
            {
                Assert.Equal("system", entry.Kind);
                Assert.Equal("agent_state", entry.ItemType);
                Assert.Equal("Agent state", entry.Title);
                Assert.Equal("codex_apps starting.", entry.Body);
            },
            entry =>
            {
                Assert.Equal("notice", entry.Kind);
                Assert.Equal("agent_error", entry.ItemType);
                Assert.Equal("Agent error", entry.Title);
                Assert.Equal("[features].collab is deprecated. Use [features].multi_agent instead.", entry.Body);
            });
    }

    private static bool WaitForCondition(Func<bool> predicate, int attempts = 80, int delayMilliseconds = 25)
    {
        for (var i = 0; i < attempts; i += 1)
        {
            if (predicate())
            {
                return true;
            }

            Thread.Sleep(delayMilliseconds);
        }

        return predicate();
    }

    private static DateTimeOffset ParseUtc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);
}

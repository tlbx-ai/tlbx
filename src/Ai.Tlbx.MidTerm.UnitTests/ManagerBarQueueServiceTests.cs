using Ai.Tlbx.MidTerm.Common.Protocol;
using System.Globalization;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Settings;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class ManagerBarQueueServiceTests : IAsyncDisposable
{
    private readonly string _stateDir;
    private readonly FakeTimeProvider _timeProvider;

    public ManagerBarQueueServiceTests()
    {
        _stateDir = Path.Combine(Path.GetTempPath(), "midterm-manager-bar-queue-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_stateDir);
        _timeProvider = new FakeTimeProvider(DateTimeOffset.Parse("2026-04-04T12:00:00Z", CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Enqueue_PersistsAcrossRestart()
    {
        var runtime = new FakeRuntime(["session-1"]);
        await using (var initial = new ManagerBarQueueService(_stateDir, runtime, _timeProvider))
        {
            var entry = initial.Enqueue("session-1", new ManagerBarButton
            {
                Id = "build",
                Label = "Build",
                ActionType = "single",
                Prompts = ["dotnet build"],
                Trigger = new ManagerBarTrigger
                {
                    Kind = "repeatCount",
                    RepeatCount = 3
                }
            });

            Assert.NotNull(entry);
            Assert.Single(initial.GetSnapshot(["session-1"]));
        }

        await using var restarted = new ManagerBarQueueService(_stateDir, runtime, _timeProvider);
        var snapshot = Assert.Single(restarted.GetSnapshot(["session-1"]));
        Assert.Equal("session-1", snapshot.SessionId);
        Assert.NotNull(snapshot.Action);
        Assert.Equal("Build", snapshot.Action!.Label);
        Assert.Equal("repeatCount", snapshot.Action.Trigger.Kind);
        Assert.Equal("pendingCooldown", snapshot.Phase);
    }

    [Fact]
    public async Task EnqueuePrompt_PersistsAcrossRestart()
    {
        var runtime = new FakeRuntime(["session-1"]);
        await using (var initial = new ManagerBarQueueService(_stateDir, runtime, _timeProvider))
        {
            var entry = initial.EnqueuePrompt("session-1", new AppServerControlTurnRequest
            {
                Text = "Summarize the diff.",
                Attachments = [],
                TerminalReplay =
                [
                    new AppServerControlTerminalReplayStep
                    {
                        Kind = "text",
                        Text = "Summarize ",
                        UseBracketedPaste = true
                    },
                    new AppServerControlTerminalReplayStep
                    {
                        Kind = "image",
                        Path = "Q:/repo/.midterm/uploads/image.png",
                        MimeType = "image/png"
                    }
                ]
            });

            Assert.NotNull(entry);
            Assert.Single(initial.GetSnapshot(["session-1"]));
        }

        await using var restarted = new ManagerBarQueueService(_stateDir, runtime, _timeProvider);
        var snapshot = Assert.Single(restarted.GetSnapshot(["session-1"]));
        Assert.Equal("prompt", snapshot.Kind);
        Assert.Equal("Summarize the diff.", snapshot.Turn?.Text);
        Assert.NotNull(snapshot.Turn);
        Assert.Equal(2, snapshot.Turn!.TerminalReplay.Count);
        Assert.True(snapshot.Turn.TerminalReplay[0].UseBracketedPaste);
        Assert.Equal("image", snapshot.Turn.TerminalReplay[1].Kind);
        Assert.Equal("Q:/repo/.midterm/uploads/image.png", snapshot.Turn.TerminalReplay[1].Path);
        Assert.Null(snapshot.Action);
        Assert.Equal("pendingCooldown", snapshot.Phase);
    }

    [Fact]
    public async Task EnqueuePromptAt_PersistsScheduledRunTimeAcrossRestart()
    {
        var runtime = new FakeRuntime(["session-1"]);
        var runAt = _timeProvider.GetUtcNow().AddMinutes(5);
        await using (var initial = new ManagerBarQueueService(_stateDir, runtime, _timeProvider))
        {
            var entry = initial.EnqueuePromptAt(
                "session-1",
                new AppServerControlTurnRequest
                {
                    Text = "check release status"
                },
                runAt);

            Assert.NotNull(entry);
            Assert.Equal("pendingInterval", entry!.Phase);
            Assert.Equal(runAt, entry.NextRunAt);
            Assert.Single(initial.GetSnapshot(["session-1"]));
        }

        await using var restarted = new ManagerBarQueueService(_stateDir, runtime, _timeProvider);
        var snapshot = Assert.Single(restarted.GetSnapshot(["session-1"]));
        Assert.Equal("prompt", snapshot.Kind);
        Assert.Equal("check release status", snapshot.Turn?.Text);
        Assert.Equal("pendingInterval", snapshot.Phase);
        Assert.Equal(runAt, snapshot.NextRunAt);
    }

    [Fact]
    public async Task SubmitPromptAsync_FastTracksTerminalPromptWhenQueueEmptyAndHeatIsLow()
    {
        var runtime = new FakeRuntime(["session-1"])
        {
            CurrentHeat = 0.1
        };

        await using var service = new ManagerBarQueueService(_stateDir, runtime, _timeProvider);

        var (accepted, entry) = await service.SubmitPromptAsync(
            "session-1",
            new AppServerControlTurnRequest
            {
                Text = "status"
            });

        Assert.True(accepted);
        Assert.Null(entry);
        Assert.Equal(["status"], runtime.SentPrompts);
        Assert.Empty(service.GetSnapshot(["session-1"]));
    }

    [Fact]
    public async Task SubmitPromptAsync_QueuesTerminalPromptWhenHeatIsHigh()
    {
        var runtime = new FakeRuntime(["session-1"])
        {
            CurrentHeat = 0.8
        };

        await using var service = new ManagerBarQueueService(_stateDir, runtime, _timeProvider);

        var (accepted, entry) = await service.SubmitPromptAsync(
            "session-1",
            new AppServerControlTurnRequest
            {
                Text = "status"
            });

        Assert.True(accepted);
        Assert.NotNull(entry);
        Assert.Empty(runtime.SentPrompts);
        Assert.Single(service.GetSnapshot(["session-1"]));
    }

    [Fact]
    public async Task DispatchPromptDirectAsync_IgnoresHeatAndNeverCreatesAHeuristicQueueEntry()
    {
        var runtime = new FakeRuntime(["session-1"])
        {
            CurrentHeat = 1.0,
            LastOutputAt = _timeProvider.GetUtcNow()
        };

        await using var service = new ManagerBarQueueService(_stateDir, runtime, _timeProvider);

        var accepted = await service.DispatchPromptDirectAsync(
            "session-1",
            new AppServerControlTurnRequest { Text = "run exact verification" });

        Assert.True(accepted);
        Assert.Equal(["run exact verification"], runtime.SentPrompts);
        Assert.Empty(service.GetSnapshot(["session-1"]));
    }

    [Fact]
    public async Task SubmitPromptAsync_QueuesTerminalPromptWhenRecentOutputHasNotSettled()
    {
        var runtime = new FakeRuntime(["session-1"])
        {
            CurrentHeat = 0,
            LastOutputAt = _timeProvider.GetUtcNow()
        };

        await using var service = new ManagerBarQueueService(_stateDir, runtime, _timeProvider);

        var (accepted, entry) = await service.SubmitPromptAsync(
            "session-1",
            new AppServerControlTurnRequest
            {
                Text = "status"
            });

        Assert.True(accepted);
        Assert.NotNull(entry);
        Assert.Empty(runtime.SentPrompts);
        Assert.Single(service.GetSnapshot(["session-1"]));
    }

    [Fact]
    public async Task SubmitPromptAsync_FastTracksAppServerControlPromptWhenTurnHasReturnedToUser()
    {
        var runtime = new FakeRuntime(["session-1"])
        {
            UsesTurnQueueValue = true,
            TurnQueueReady = true
        };

        await using var service = new ManagerBarQueueService(_stateDir, runtime, _timeProvider);

        var (accepted, entry) = await service.SubmitPromptAsync(
            "session-1",
            new AppServerControlTurnRequest
            {
                Text = "queued turn"
            });

        Assert.True(accepted);
        Assert.Null(entry);
        var turn = Assert.Single(runtime.SentTurns);
        Assert.Equal("queued turn", turn.Text);
        Assert.Empty(service.GetSnapshot(["session-1"]));
    }

    [Fact]
    public async Task SubmitPromptAsync_QueuesAppServerControlPromptWhenTurnIsStillRunning()
    {
        var runtime = new FakeRuntime(["session-1"])
        {
            UsesTurnQueueValue = true,
            TurnQueueReady = false
        };

        await using var service = new ManagerBarQueueService(_stateDir, runtime, _timeProvider);

        var (accepted, entry) = await service.SubmitPromptAsync(
            "session-1",
            new AppServerControlTurnRequest
            {
                Text = "queued turn"
            });

        Assert.True(accepted);
        Assert.NotNull(entry);
        Assert.Empty(runtime.SentTurns);
        Assert.Single(service.GetSnapshot(["session-1"]));
    }

    [Fact]
    public async Task SubmitActionAsync_FastTracksImmediateAppServerControlActionWhenTurnHasReturnedToUser()
    {
        var runtime = new FakeRuntime(["session-1"])
        {
            UsesTurnQueueValue = true,
            TurnQueueReady = true
        };

        await using var service = new ManagerBarQueueService(_stateDir, runtime, _timeProvider);

        var (accepted, entry) = await service.SubmitActionAsync(
            "session-1",
            new ManagerBarButton
            {
                Label = "Status",
                ActionType = "single",
                Prompts = ["status"],
                Trigger = new ManagerBarTrigger
                {
                    Kind = "fireAndForget"
                }
            });

        Assert.True(accepted);
        Assert.Null(entry);
        Assert.Equal(["status"], runtime.SentPrompts);
        Assert.Empty(service.GetSnapshot(["session-1"]));
    }

    [Fact]
    public async Task SubmitActionAsync_QueuesImmediateAppServerControlActionWhenTurnIsStillRunning()
    {
        var runtime = new FakeRuntime(["session-1"])
        {
            UsesTurnQueueValue = true,
            TurnQueueReady = false
        };

        await using var service = new ManagerBarQueueService(_stateDir, runtime, _timeProvider);

        var (accepted, entry) = await service.SubmitActionAsync(
            "session-1",
            new ManagerBarButton
            {
                Label = "Status",
                ActionType = "single",
                Prompts = ["status"],
                Trigger = new ManagerBarTrigger
                {
                    Kind = "fireAndForget"
                }
            });

        Assert.True(accepted);
        Assert.NotNull(entry);
        Assert.Equal("automation", entry.Kind);
        Assert.Equal("Status", entry.Action?.Label);
        Assert.Empty(runtime.SentPrompts);
        var snapshot = Assert.Single(service.GetSnapshot(["session-1"]));
        Assert.Equal("Status", snapshot.Action?.Label);
        Assert.Equal("fireAndForget", snapshot.Action?.Trigger.Kind);
    }

    [Fact]
    public async Task GetSnapshot_FiltersQueueEntriesToValidSessions()
    {
        var runtime = new FakeRuntime(["session-1", "session-2"]);
        await using var service = new ManagerBarQueueService(_stateDir, runtime, _timeProvider);

        service.Enqueue("session-1", new ManagerBarButton
        {
            Label = "One",
            Prompts = ["echo one"],
            Trigger = new ManagerBarTrigger { Kind = "onCooldown" }
        });
        service.Enqueue("session-2", new ManagerBarButton
        {
            Label = "Two",
            Prompts = ["echo two"],
            Trigger = new ManagerBarTrigger { Kind = "repeatInterval", RepeatEveryValue = 5 }
        });

        var filtered = service.GetSnapshot(["session-2"]);

        var entry = Assert.Single(filtered);
        Assert.Equal("session-2", entry.SessionId);
        Assert.NotNull(entry.Action);
        Assert.Equal("Two", entry.Action!.Label);
    }

    [Fact]
    public async Task Enqueue_DeduplicatesBurstRequestsForSameAction()
    {
        var runtime = new FakeRuntime(["session-1"]);
        await using var service = new ManagerBarQueueService(_stateDir, runtime, _timeProvider);
        var action = CreateRepeatCountAction();

        var first = service.Enqueue("session-1", action);
        var second = service.Enqueue("session-1", action);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.Equal(first!.QueueId, second!.QueueId);
        Assert.Single(service.GetSnapshot(["session-1"]));
    }

    [Fact]
    public async Task Enqueue_AllowsSameActionAgainAfterDedupWindowExpires()
    {
        var runtime = new FakeRuntime(["session-1"]);
        await using var service = new ManagerBarQueueService(_stateDir, runtime, _timeProvider);
        var action = CreateRepeatCountAction();

        var first = service.Enqueue("session-1", action);
        _timeProvider.Advance(TimeSpan.FromSeconds(2));
        var second = service.Enqueue("session-1", action);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotEqual(first!.QueueId, second!.QueueId);
        Assert.Equal(2, service.GetSnapshot(["session-1"]).Count);
    }

    [Fact]
    public async Task ProcessLoop_SendsPromptQueueInOrderAndRequiresHeatRearmBetweenItems()
    {
        var runtime = new FakeRuntime(["session-1"]);
        await using var service = new ManagerBarQueueService(_stateDir, runtime, _timeProvider);
        service.Start();

        service.EnqueuePrompt("session-1", new AppServerControlTurnRequest { Text = "first" });
        service.EnqueuePrompt("session-1", new AppServerControlTurnRequest { Text = "second" });

        await Task.Delay(1200);

        Assert.Equal(["first"], runtime.SentPrompts);

        runtime.LastOutputAt = _timeProvider.GetUtcNow();
        await Task.Delay(1200);
        Assert.Equal(["first"], runtime.SentPrompts);

        _timeProvider.Advance(TimeSpan.FromSeconds(6));
        await Task.Delay(1200);
        Assert.Equal(["first", "second"], runtime.SentPrompts);
    }

    [Fact]
    public async Task ProcessLoop_WaitsForRecentTerminalOutputToSettleBeforeDispatchingQueuedPrompt()
    {
        var runtime = new FakeRuntime(["session-1"])
        {
            CurrentHeat = 0,
            LastOutputAt = _timeProvider.GetUtcNow()
        };

        await using var service = new ManagerBarQueueService(_stateDir, runtime, _timeProvider);
        service.Start();

        service.EnqueuePrompt("session-1", new AppServerControlTurnRequest { Text = "status" });

        await Task.Delay(1200);
        Assert.Empty(runtime.SentPrompts);

        _timeProvider.Advance(TimeSpan.FromSeconds(6));
        await Task.Delay(1200);

        Assert.Equal(["status"], runtime.SentPrompts);
    }

    [Fact]
    public async Task ProcessLoop_WaitsUntilScheduledPromptRunTime()
    {
        var runtime = new FakeRuntime(["session-1"]);
        await using var service = new ManagerBarQueueService(_stateDir, runtime, _timeProvider);
        service.Start();

        service.EnqueuePromptAt(
            "session-1",
            new AppServerControlTurnRequest { Text = "scheduled status" },
            _timeProvider.GetUtcNow().AddSeconds(5));

        await Task.Delay(1200);
        Assert.Empty(runtime.SentPrompts);

        _timeProvider.Advance(TimeSpan.FromSeconds(6));
        await Task.Delay(1200);

        Assert.Equal(["scheduled status"], runtime.SentPrompts);
        Assert.Empty(service.GetSnapshot(["session-1"]));
    }

    [Fact]
    public async Task ProcessLoop_WaitsForAppServerControlTurnToReturnBeforeSendingQueuedTurn()
    {
        var runtime = new FakeRuntime(["session-1"])
        {
            UsesTurnQueueValue = true,
            TurnQueueReady = false
        };

        await using var service = new ManagerBarQueueService(_stateDir, runtime, _timeProvider);
        service.Start();

        service.EnqueuePrompt("session-1", new AppServerControlTurnRequest { Text = "queued turn" });

        await Task.Delay(1200);
        Assert.Empty(runtime.SentTurns);

        runtime.TurnQueueReady = true;
        await Task.Delay(1200);

        Assert.Single(runtime.SentTurns);
        Assert.Equal("queued turn", runtime.SentTurns[0].Text);
    }

    public async ValueTask DisposeAsync()
    {
        try
        {
            if (Directory.Exists(_stateDir))
            {
                Directory.Delete(_stateDir, recursive: true);
            }
        }
        catch
        {
        }

        await ValueTask.CompletedTask;
    }

    private static ManagerBarButton CreateRepeatCountAction()
    {
        return new ManagerBarButton
        {
            Id = "build",
            Label = "Build",
            ActionType = "single",
            Prompts = ["dotnet build"],
            Trigger = new ManagerBarTrigger
            {
                Kind = "repeatCount",
                RepeatCount = 3
            }
        };
    }

    private sealed class FakeRuntime : IManagerBarQueueRuntime
    {
        private readonly HashSet<string> _sessionIds;
        public double CurrentHeat { get; set; }
        public DateTimeOffset? LastOutputAt { get; set; }
        public bool UsesTurnQueueValue { get; set; }
        public bool TurnQueueReady { get; set; } = true;
        public List<string> SentPrompts { get; } = [];
        public List<AppServerControlTurnRequest> SentTurns { get; } = [];

        public FakeRuntime(IEnumerable<string> sessionIds)
        {
            _sessionIds = new HashSet<string>(sessionIds, StringComparer.Ordinal);
        }

        public IReadOnlyCollection<string> GetActiveSessionIds()
        {
            return _sessionIds.ToArray();
        }

        public bool SessionExists(string sessionId)
        {
            return _sessionIds.Contains(sessionId);
        }

        public SessionHeatSnapshot GetHeatSnapshot(string sessionId)
        {
            return new SessionHeatSnapshot
            {
                CurrentHeat = CurrentHeat,
                LastOutputAt = LastOutputAt
            };
        }

        public bool UsesTurnQueue(string sessionId)
        {
            return UsesTurnQueueValue;
        }

        public bool IsTurnQueueReady(string sessionId)
        {
            return TurnQueueReady;
        }

        public Task SendPromptAsync(string sessionId, string prompt, CancellationToken cancellationToken)
        {
            SentPrompts.Add(prompt);
            return Task.CompletedTask;
        }

        public Task SendTurnAsync(string sessionId, AppServerControlTurnRequest request, CancellationToken cancellationToken)
        {
            if (!UsesTurnQueueValue)
            {
                if (!string.IsNullOrWhiteSpace(request.Text))
                {
                    SentPrompts.Add(request.Text);
                }

                return Task.CompletedTask;
            }

            SentTurns.Add(new AppServerControlTurnRequest
            {
                Text = request.Text,
                Model = request.Model,
                Effort = request.Effort,
                PlanMode = request.PlanMode,
                PermissionMode = request.PermissionMode,
                TerminalReplay = request.TerminalReplay
                    .Select(static step => new AppServerControlTerminalReplayStep
                    {
                        Kind = step.Kind,
                        Text = step.Text,
                        Path = step.Path,
                        MimeType = step.MimeType,
                        UseBracketedPaste = step.UseBracketedPaste
                    })
                    .ToList(),
                Attachments = request.Attachments
                    .Select(static attachment => new AppServerControlAttachmentReference
                    {
                        Kind = attachment.Kind,
                        Path = attachment.Path,
                        MimeType = attachment.MimeType,
                        DisplayName = attachment.DisplayName
                    })
                    .ToList()
            });
            return Task.CompletedTask;
        }
    }
}

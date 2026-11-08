using System.Globalization;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class AppServerControlHostProtocolTests
{
    [Fact]
    public void ConfigurePatchRoundTripsExplicitNullWithoutInventingOmittedFields()
    {
        const string json = """
            {"commandId":"configure-1","sessionId":"s","type":"runtime.configure",
             "configure":{"expectedThreadId":"thread","expectedTurnId":null,
                          "settings":{"serviceTier":null,"effort":"high"}}}
            """;
        var command = JsonSerializer.Deserialize(json, AppServerControlHostJsonContext.Default.AppServerControlHostCommandEnvelope)!;
        Assert.Null(command.Configure!.ExpectedTurnId);
        Assert.Equal(JsonValueKind.Null, command.Configure.Settings["serviceTier"].ValueKind);
        Assert.False(command.Configure.Settings.ContainsKey("model"));
        var serialized = JsonSerializer.Serialize(command, AppServerControlHostJsonContext.Default.AppServerControlHostCommandEnvelope);
        using var document = JsonDocument.Parse(serialized);
        var patch = document.RootElement.GetProperty("configure").GetProperty("settings");
        Assert.Equal(JsonValueKind.Null, patch.GetProperty("serviceTier").ValueKind);
        Assert.False(patch.TryGetProperty("model", out _));
    }

    [Fact]
    public void ConfigureAckRetainsSeparateReadbackControlAckAndPartialScopes()
    {
        using var actual = JsonDocument.Parse("\"provider-normalized-model\"");
        var result = new AppServerControlHostCommandResultEnvelope
        {
            Configured = new()
            {
                Status = "partial", ThreadStatus = "applied", TurnStatus = "targetUnavailable",
                EffectiveSettings = new(StringComparer.Ordinal) { ["model"] = actual.RootElement.Clone() },
                DeferredFields = ["collaborationMode"]
            }
        };
        var json = JsonSerializer.Serialize(result, AppServerControlHostJsonContext.Default.AppServerControlHostCommandResultEnvelope);
        var decoded = JsonSerializer.Deserialize(json, AppServerControlHostJsonContext.Default.AppServerControlHostCommandResultEnvelope)!.Configured!;
        Assert.Equal("targetUnavailable", decoded.TurnStatus);
        Assert.Equal("provider-normalized-model", decoded.EffectiveSettings["model"].GetString());
        Assert.Empty(decoded.ConfirmedSettings);
        Assert.Equal(["collaborationMode"], decoded.DeferredFields);
    }

    [Fact]
    public void AppServerControlHostProtocol_UsesV3AsTheOnlyCurrentVersion()
    {
        Assert.Equal("app-server-control-host-v3", AppServerControlHostProtocol.CurrentVersion);
    }

    [Fact]
    public void AppServerControlHostCommandEnvelope_RoundTripsCanonicalTurnRequest()
    {
        var command = new AppServerControlHostCommandEnvelope
        {
            ProtocolVersion = AppServerControlHostProtocol.CurrentVersion,
            CommandId = "cmd-1",
            SessionId = "session-1",
            Type = "turn.start",
            StartTurn = new AppServerControlTurnRequest
            {
                Text = "Inspect the diff and continue.",
                Model = "gpt-5.3-codex",
                Effort = "medium",
                PlanMode = AppServerControlQuickSettings.PlanModeOn,
                PermissionMode = AppServerControlQuickSettings.PermissionModeAuto,
                FastMode = AppServerControlQuickSettings.FastModeOn,
                Attachments =
                [
                    new AppServerControlAttachmentReference
                    {
                        Kind = "image",
                        Path = "Q:/repo/.midterm/uploads/screen.png",
                        MimeType = "image/png",
                        DisplayName = "screen.png"
                    }
                ]
            }
        };

        var json = JsonSerializer.Serialize(command, AppServerControlHostJsonContext.Default.AppServerControlHostCommandEnvelope);
        var roundTrip = JsonSerializer.Deserialize(json, AppServerControlHostJsonContext.Default.AppServerControlHostCommandEnvelope);

        Assert.NotNull(roundTrip);
        Assert.Equal(AppServerControlHostProtocol.CurrentVersion, roundTrip!.ProtocolVersion);
        Assert.Equal("turn.start", roundTrip.Type);
        Assert.NotNull(roundTrip.StartTurn);
        Assert.Equal("Inspect the diff and continue.", roundTrip.StartTurn!.Text);
        Assert.Equal("gpt-5.3-codex", roundTrip.StartTurn.Model);
        Assert.Equal(AppServerControlQuickSettings.PlanModeOn, roundTrip.StartTurn.PlanMode);
        Assert.Equal(AppServerControlQuickSettings.PermissionModeAuto, roundTrip.StartTurn.PermissionMode);
        Assert.Equal(AppServerControlQuickSettings.FastModeOn, roundTrip.StartTurn.FastMode);
        Assert.Single(roundTrip.StartTurn.Attachments);
        Assert.Equal("image", roundTrip.StartTurn.Attachments[0].Kind);
        Assert.Equal("Q:/repo/.midterm/uploads/screen.png", roundTrip.StartTurn.Attachments[0].Path);
    }

    [Fact]
    public void AppServerControlHostCommandEnvelope_RoundTripsAttachPointMetadata()
    {
        var command = new AppServerControlHostCommandEnvelope
        {
            ProtocolVersion = AppServerControlHostProtocol.CurrentVersion,
            CommandId = "cmd-attach-remote",
            SessionId = "session-remote-1",
            Type = "runtime.attach",
            AttachRuntime = new AppServerControlAttachRuntimeRequest
            {
                SessionId = "session-remote-1",
                Provider = "codex",
                WorkingDirectory = "Q:/repo",
                AttachPoint = new SessionAgentAttachPoint
                {
                    Provider = SessionAgentAttachPoint.CodexProvider,
                    TransportKind = SessionAgentAttachPoint.CodexAppServerWebSocketTransport,
                    Endpoint = "ws://127.0.0.1:4513/",
                    SharedRuntime = true,
                    Source = "foreground-command-line.remote",
                    PreferredThreadId = "thread-abc123"
                }
            }
        };

        var json = JsonSerializer.Serialize(command, AppServerControlHostJsonContext.Default.AppServerControlHostCommandEnvelope);
        var roundTrip = JsonSerializer.Deserialize(json, AppServerControlHostJsonContext.Default.AppServerControlHostCommandEnvelope);

        Assert.NotNull(roundTrip);
        Assert.NotNull(roundTrip!.AttachRuntime);
        Assert.NotNull(roundTrip.AttachRuntime!.AttachPoint);
        Assert.Equal("ws://127.0.0.1:4513/", roundTrip.AttachRuntime.AttachPoint!.Endpoint);
        Assert.Equal("thread-abc123", roundTrip.AttachRuntime.AttachPoint.PreferredThreadId);
    }

    [Fact]
    public void SyntheticAppServerControlHostFlow_ProducesCanonicalSnapshotThroughIngress()
    {
        var history = new SessionAppServerControlHistoryService();
        var host = new SyntheticAppServerControlAgentHost();

        var attachResult = host.Handle(new AppServerControlHostCommandEnvelope
        {
            ProtocolVersion = AppServerControlHostProtocol.CurrentVersion,
            CommandId = "cmd-attach",
            SessionId = "session-1",
            Type = "runtime.attach",
            AttachRuntime = new AppServerControlAttachRuntimeRequest
            {
                SessionId = "session-1",
                Provider = "codex",
                WorkingDirectory = "Q:/repo"
            }
        });
        foreach (var appServerControlEvent in attachResult.Events)
        {
            history.Append(appServerControlEvent);
        }

        var turnResult = host.Handle(new AppServerControlHostCommandEnvelope
        {
            ProtocolVersion = AppServerControlHostProtocol.CurrentVersion,
            CommandId = "cmd-turn",
            SessionId = "session-1",
            Type = "turn.start",
            StartTurn = new AppServerControlTurnRequest
            {
                Text = "Continue with the implementation."
            }
        });
        foreach (var appServerControlEvent in turnResult.Events)
        {
            history.Append(appServerControlEvent);
        }

        var resolveResult = host.Handle(new AppServerControlHostCommandEnvelope
        {
            ProtocolVersion = AppServerControlHostProtocol.CurrentVersion,
            CommandId = "cmd-resolve",
            SessionId = "session-1",
            Type = "request.resolve",
            ResolveRequest = new AppServerControlRequestResolutionCommand
            {
                RequestId = "approval-1",
                Decision = "accept"
            }
        });
        foreach (var appServerControlEvent in resolveResult.Events)
        {
            history.Append(appServerControlEvent);
        }

        var snapshot = history.GetSnapshot("session-1");

        Assert.NotNull(snapshot);
        Assert.Equal("codex", snapshot!.Provider);
        Assert.Equal("ready", snapshot.Session.State);
        Assert.Equal("Active", snapshot.Thread.StateLabel);
        Assert.Equal("turn-1", snapshot.CurrentTurn.TurnId);
        Assert.Equal("Assistant says hi.", snapshot.Streams.AssistantText);
        Assert.Equal("Thinking hard.", snapshot.Streams.ReasoningText);
        Assert.Equal("1. inspect\n2. patch", snapshot.Streams.PlanText);
        Assert.Equal("dotnet test", snapshot.Streams.CommandOutput);
        Assert.Equal("--- a/app.cs\n+++ b/app.cs", snapshot.Streams.UnifiedDiff);
        Assert.Equal("gpt-5.3-codex", snapshot.QuickSettings.Model);
        Assert.Equal("medium", snapshot.QuickSettings.Effort);
        Assert.Equal(AppServerControlQuickSettings.PlanModeOn, snapshot.QuickSettings.PlanMode);
        Assert.Equal(AppServerControlQuickSettings.PermissionModeManual, snapshot.QuickSettings.PermissionMode);
        Assert.Contains(snapshot.Requests, request => request.RequestId == "approval-1" && request.Decision == "accept");
    }

    private sealed class SyntheticAppServerControlAgentHost
    {
        public AppServerControlHostHello Hello { get; } = new()
        {
            ProtocolVersion = AppServerControlHostProtocol.CurrentVersion,
            HostKind = "mtagenthost",
            HostVersion = "synthetic",
            Providers = ["codex", "claude"],
            Capabilities = ["attach", "turn.start", "request.resolve"]
        };

        public SyntheticCommandResult Handle(AppServerControlHostCommandEnvelope command)
        {
            return command.Type switch
            {
                "runtime.attach" => Attach(command),
                "turn.start" => StartTurn(command),
                "request.resolve" => ResolveRequest(command),
                _ => throw new InvalidOperationException($"Unsupported synthetic command '{command.Type}'.")
            };
        }

        private static SyntheticCommandResult Attach(AppServerControlHostCommandEnvelope command)
        {
            return new SyntheticCommandResult(
                new AppServerControlHostCommandResultEnvelope
                {
                    ProtocolVersion = AppServerControlHostProtocol.CurrentVersion,
                    CommandId = command.CommandId,
                    SessionId = command.SessionId,
                    Status = "accepted",
                    Accepted = new AppServerControlCommandAcceptedResponse
                    {
                        SessionId = command.SessionId,
                        Status = "accepted"
                    }
                },
                [
                    Envelope(command.SessionId, new AppServerControlProviderEvent
                    {
                        EventId = "evt-attach-1",
                        SessionId = command.SessionId,
                        Provider = "codex",
                        ThreadId = "thread-1",
                        CreatedAt = ParseUtc("2026-03-20T15:00:00Z"),
                        Type = "session.started",
                        SessionState = new AppServerControlProviderSessionStatePayload
                        {
                            State = "starting",
                            StateLabel = "Starting",
                            Reason = "Synthetic attach"
                        }
                    }),
                    Envelope(command.SessionId, new AppServerControlProviderEvent
                    {
                        EventId = "evt-attach-2",
                        SessionId = command.SessionId,
                        Provider = "codex",
                        ThreadId = "thread-1",
                        CreatedAt = ParseUtc("2026-03-20T15:00:01Z"),
                        Type = "thread.started",
                        ThreadState = new AppServerControlProviderThreadStatePayload
                        {
                            State = "active",
                            StateLabel = "Active",
                            ProviderThreadId = "thread-1"
                        }
                    }),
                    Envelope(command.SessionId, new AppServerControlProviderEvent
                    {
                        EventId = "evt-attach-3",
                        SessionId = command.SessionId,
                        Provider = "codex",
                        ThreadId = "thread-1",
                        CreatedAt = ParseUtc("2026-03-20T15:00:02Z"),
                        Type = "session.ready",
                        SessionState = new AppServerControlProviderSessionStatePayload
                        {
                            State = "ready",
                            StateLabel = "Ready",
                            Reason = "Synthetic host attached"
                        }
                    })
                ]);
        }

        private static SyntheticCommandResult StartTurn(AppServerControlHostCommandEnvelope command)
        {
            return new SyntheticCommandResult(
                new AppServerControlHostCommandResultEnvelope
                {
                    ProtocolVersion = AppServerControlHostProtocol.CurrentVersion,
                    CommandId = command.CommandId,
                    SessionId = command.SessionId,
                    Status = "accepted",
                    TurnStarted = new AppServerControlTurnStartResponse
                    {
                        SessionId = command.SessionId,
                        Provider = "codex",
                        ThreadId = "thread-1",
                        TurnId = "turn-1",
                        Status = "accepted",
                        QuickSettings = new AppServerControlQuickSettingsSummary
                        {
                            Model = "gpt-5.3-codex",
                            Effort = "medium",
                            PlanMode = AppServerControlQuickSettings.PlanModeOn,
                            PermissionMode = AppServerControlQuickSettings.PermissionModeManual
                        }
                    }
                },
                [
                    Envelope(command.SessionId, new AppServerControlProviderEvent
                    {
                        EventId = "evt-turn-settings-1",
                        SessionId = command.SessionId,
                        Provider = "codex",
                        ThreadId = "thread-1",
                        CreatedAt = ParseUtc("2026-03-20T15:00:02.5000000Z"),
                        Type = "quick-settings.updated",
                        QuickSettingsUpdated = new AppServerControlQuickSettingsPayload
                        {
                            Model = "gpt-5.3-codex",
                            Effort = "medium",
                            PlanMode = AppServerControlQuickSettings.PlanModeOn,
                            PermissionMode = AppServerControlQuickSettings.PermissionModeManual
                        }
                    }),
                    Envelope(command.SessionId, new AppServerControlProviderEvent
                    {
                        EventId = "evt-turn-1",
                        SessionId = command.SessionId,
                        Provider = "codex",
                        ThreadId = "thread-1",
                        TurnId = "turn-1",
                        CreatedAt = ParseUtc("2026-03-20T15:00:03Z"),
                        Type = "turn.started",
                        TurnStarted = new AppServerControlProviderTurnStartedPayload
                        {
                            Model = "gpt-5.3-codex",
                            Effort = "medium"
                        }
                    }),
                    Envelope(command.SessionId, new AppServerControlProviderEvent
                    {
                        EventId = "evt-turn-2",
                        SessionId = command.SessionId,
                        Provider = "codex",
                        ThreadId = "thread-1",
                        TurnId = "turn-1",
                        ItemId = "item-1",
                        CreatedAt = ParseUtc("2026-03-20T15:00:04Z"),
                        Type = "content.delta",
                        ContentDelta = new AppServerControlProviderContentDeltaPayload
                        {
                            StreamKind = "assistant_text",
                            Delta = "Assistant says hi."
                        }
                    }),
                    Envelope(command.SessionId, new AppServerControlProviderEvent
                    {
                        EventId = "evt-turn-3",
                        SessionId = command.SessionId,
                        Provider = "codex",
                        ThreadId = "thread-1",
                        TurnId = "turn-1",
                        ItemId = "item-2",
                        CreatedAt = ParseUtc("2026-03-20T15:00:05Z"),
                        Type = "content.delta",
                        ContentDelta = new AppServerControlProviderContentDeltaPayload
                        {
                            StreamKind = "reasoning_text",
                            Delta = "Thinking hard."
                        }
                    }),
                    Envelope(command.SessionId, new AppServerControlProviderEvent
                    {
                        EventId = "evt-turn-4",
                        SessionId = command.SessionId,
                        Provider = "codex",
                        ThreadId = "thread-1",
                        TurnId = "turn-1",
                        CreatedAt = ParseUtc("2026-03-20T15:00:06Z"),
                        Type = "plan.completed",
                        PlanCompleted = new AppServerControlProviderPlanCompletedPayload
                        {
                            PlanMarkdown = "1. inspect\n2. patch"
                        }
                    }),
                    Envelope(command.SessionId, new AppServerControlProviderEvent
                    {
                        EventId = "evt-turn-5",
                        SessionId = command.SessionId,
                        Provider = "codex",
                        ThreadId = "thread-1",
                        TurnId = "turn-1",
                        ItemId = "tool-1",
                        CreatedAt = ParseUtc("2026-03-20T15:00:07Z"),
                        Type = "content.delta",
                        ContentDelta = new AppServerControlProviderContentDeltaPayload
                        {
                            StreamKind = "command_output",
                            Delta = "dotnet test"
                        }
                    }),
                    Envelope(command.SessionId, new AppServerControlProviderEvent
                    {
                        EventId = "evt-turn-6",
                        SessionId = command.SessionId,
                        Provider = "codex",
                        ThreadId = "thread-1",
                        TurnId = "turn-1",
                        CreatedAt = ParseUtc("2026-03-20T15:00:08Z"),
                        Type = "diff.updated",
                        DiffUpdated = new AppServerControlProviderDiffUpdatedPayload
                        {
                            UnifiedDiff = "--- a/app.cs\n+++ b/app.cs"
                        }
                    }),
                    Envelope(command.SessionId, new AppServerControlProviderEvent
                    {
                        EventId = "evt-turn-7",
                        SessionId = command.SessionId,
                        Provider = "codex",
                        ThreadId = "thread-1",
                        TurnId = "turn-1",
                        RequestId = "approval-1",
                        CreatedAt = ParseUtc("2026-03-20T15:00:09Z"),
                        Type = "request.opened",
                        RequestOpened = new AppServerControlProviderRequestOpenedPayload
                        {
                            RequestType = "command_execution_approval",
                            RequestTypeLabel = "Command approval",
                            Detail = "Run dotnet test"
                        }
                    }),
                    Envelope(command.SessionId, new AppServerControlProviderEvent
                    {
                        EventId = "evt-turn-8",
                        SessionId = command.SessionId,
                        Provider = "codex",
                        ThreadId = "thread-1",
                        TurnId = "turn-1",
                        CreatedAt = ParseUtc("2026-03-20T15:00:10Z"),
                        Type = "turn.completed",
                        TurnCompleted = new AppServerControlProviderTurnCompletedPayload
                        {
                            State = "completed",
                            StateLabel = "Completed"
                        }
                    }),
                    Envelope(command.SessionId, new AppServerControlProviderEvent
                    {
                        EventId = "evt-turn-9",
                        SessionId = command.SessionId,
                        Provider = "codex",
                        ThreadId = "thread-1",
                        CreatedAt = ParseUtc("2026-03-20T15:00:11Z"),
                        Type = "session.state.changed",
                        SessionState = new AppServerControlProviderSessionStatePayload
                        {
                            State = "ready",
                            StateLabel = "Ready",
                            Reason = "Turn completed"
                        }
                    })
                ]);
        }

        private static SyntheticCommandResult ResolveRequest(AppServerControlHostCommandEnvelope command)
        {
            return new SyntheticCommandResult(
                new AppServerControlHostCommandResultEnvelope
                {
                    ProtocolVersion = AppServerControlHostProtocol.CurrentVersion,
                    CommandId = command.CommandId,
                    SessionId = command.SessionId,
                    Status = "accepted",
                    Accepted = new AppServerControlCommandAcceptedResponse
                    {
                        SessionId = command.SessionId,
                        Status = "accepted",
                        RequestId = command.ResolveRequest?.RequestId
                    }
                },
                [
                    Envelope(command.SessionId, new AppServerControlProviderEvent
                    {
                        EventId = "evt-resolve-1",
                        SessionId = command.SessionId,
                        Provider = "codex",
                        ThreadId = "thread-1",
                        TurnId = "turn-1",
                        RequestId = command.ResolveRequest!.RequestId,
                        CreatedAt = ParseUtc("2026-03-20T15:00:12Z"),
                        Type = "request.resolved",
                        RequestResolved = new AppServerControlProviderRequestResolvedPayload
                        {
                            RequestType = "command_execution_approval",
                            Decision = command.ResolveRequest.Decision
                        }
                    })
                ]);
        }

        private static AppServerControlProviderEvent Envelope(string sessionId, AppServerControlProviderEvent appServerControlEvent)
        {
            appServerControlEvent.SessionId = sessionId;
            return appServerControlEvent;
        }
    }

    private static DateTimeOffset ParseUtc(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture);

    private sealed record SyntheticCommandResult(
        AppServerControlHostCommandResultEnvelope Result,
        IReadOnlyList<AppServerControlProviderEvent> Events);
}










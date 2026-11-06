using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using Ai.Tlbx.MidTerm.AgentHost;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

[Collection(PathSensitiveEnvironmentCollection.Name)]
public sealed class MtAgentHostClaudeIntegrationTests
{
    [Fact]
    public async Task ClaudeHistory_RestoresNativeMessagesAndToolsWithoutCurrentModelOrInteractiveRequests()
    {
        using var document = JsonDocument.Parse("""
            {"messages":[
              {"type":"user","uuid":"user-a","timestamp":"2026-10-16T10:00:00Z","message":{"content":"First question"}},
              {"type":"assistant","uuid":"a","message":{"id":"native-a","content":[
                {"type":"text","text":"Checking"},{"type":"tool_use","id":"tool-a","name":"Bash","input":{"command":"pwd"}}]}},
              {"type":"user","uuid":"tool-result","message":{"content":[{"type":"tool_result","tool_use_id":"tool-a","content":"Q:/work"}]}},
              {"type":"assistant","uuid":"b","message":{"id":"native-b","content":[{"type":"text","text":"Answer one"}]}},
              {"type":"assistant","uuid":"child","parent_tool_use_id":"tool-a","message":{"content":[{"type":"text","text":"Private subagent"}]}},
              {"type":"user","uuid":"user-b","message":{"content":[{"type":"text","text":"Second question"}]}},
              {"type":"assistant","uuid":"c","message":{"id":"native-c","content":[{"type":"text","text":"Answer two"},
                {"type":"tool_use","id":"plan-tool","name":"ExitPlanMode","input":{"plan":"Restored plan"}}]}},
              {"type":"user","uuid":"plan-result","message":{"content":[{"type":"tool_result","tool_use_id":"plan-tool","is_error":true,"content":"Wait for review"}]}}
            ]}
            """);
        var events = new List<AppServerControlProviderEvent>();
        await using var runtime = new ClaudeAppServerControlAgentRuntime(events.Add);
        runtime.ReplayClaudeHistory(document.RootElement);
        var turns = events.Where(e => e.TurnStarted is not null).ToArray();
        Assert.Equal(2, turns.Length);
        Assert.All(turns, e => { Assert.True(e.TurnStarted!.IsReplay); Assert.Null(e.TurnStarted.Model); Assert.Null(e.TurnStarted.Effort); });
        Assert.Equal(DateTimeOffset.Parse("2026-10-16T10:00:00Z", CultureInfo.InvariantCulture), turns[0].CreatedAt);
        Assert.Equal(2, events.Count(e => e.Item?.ItemType == "user_message"));
        Assert.Contains(events, e => e.ItemId == "native-b" && e.TurnId == "claude-history:user-a");
        Assert.Contains(events, e => e.ItemId == "native-c" && e.TurnId == "claude-history:user-b");
        Assert.Contains(events, e => e.ItemId == "tool-a" && e.Item?.Status == "completed");
        Assert.Contains(events, e => e.ItemId == "plan-tool" && e.Item?.Status == "completed" && e.Item.Title == "Plan review");
        Assert.Contains(events, e => e.PlanCompleted?.PlanMarkdown == "Restored plan" && e.PlanCompleted.IsProposal);
        Assert.DoesNotContain(events, e => e.UserInputRequested is not null || e.Item?.Detail == "Private subagent");
        Assert.Equal(2, events.Count(e => e.TurnCompleted?.StopReason == "history_import"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [InlineData(false, true)]
    [InlineData(false, true, true)]
    [InlineData(false, true, false, true)]
    public async Task MtAgentHost_KeepsClaudeSteeringAliveAcrossQueuedSdkResults(bool streamingAbort, bool interrupt = false, bool queuedAtInterrupt = false, bool compactFailure = false)
    {
        using var fakeClaude = FakeClaudePathScope.Create();
        using var process = StartAgentHost(ResolveAgentHostPath());
        const string sessionId = "claude-steer";
        var patches = new Queue<AppServerControlHostHistoryPatchEnvelope>();
        try
        {
            _ = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "attach-steer", SessionId = sessionId, Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = sessionId, Provider = "claude", RuntimeKind = "claude-agent-sdk",
                    ExecutablePath = fakeClaude.ExecutablePath, WorkingDirectory = fakeClaude.Root
                }
            });
            Assert.Equal("accepted", (await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, patches, "attach-steer")).Status);
            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "start-steer", SessionId = sessionId, Type = "turn.start",
                StartTurn = new AppServerControlTurnRequest { Text = compactFailure ? "Hold native Claude turn for compact failure." : queuedAtInterrupt ? "Hold native Claude turn for queued cancellation." : streamingAbort ? "Hold native Claude turn for streaming abort." : "Hold native Claude turn for steering." }
            });
            var started = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, patches, "start-steer");
            var turnId = Assert.IsType<string>(started.TurnStarted?.TurnId);
            _ = await AppServerControlHostTestClient.WaitForHistoryWindowAsync(process.StandardOutput, process.StandardInput, patches,
                sessionId, h => h.Streams.AssistantText == "Initial answer.", TimeSpan.FromSeconds(10), count: 96);
            if (interrupt)
            {
                await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
                {
                    CommandId = "stop-stale", SessionId = sessionId, Type = "turn.interrupt",
                    InterruptTurn = new AppServerControlInterruptRequest { TurnId = "previous-turn" }
                });
                Assert.Equal("rejected", (await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, patches, "stop-stale")).Status);
                await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
                {
                    CommandId = "stop-active", SessionId = sessionId, Type = "turn.interrupt",
                    InterruptTurn = new AppServerControlInterruptRequest { TurnId = turnId }
                });
                Assert.Equal("accepted", (await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, patches, "stop-active")).Status);
                await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
                {
                    CommandId = "after-stop", SessionId = sessionId, Type = "turn.start",
                    StartTurn = new AppServerControlTurnRequest { Text = "After native stop" }
                });
                var next = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, patches, "after-stop");
                var nextTurnId = next.TurnStarted?.TurnId;
                var resumed = await AppServerControlHostTestClient.WaitForHistoryWindowAsync(process.StandardOutput, process.StandardInput,
                    patches, sessionId, h => h.CurrentTurn.TurnId == nextTurnId && h.CurrentTurn.State == "completed",
                    TimeSpan.FromSeconds(10), count: 96);
                Assert.Contains(resumed.History, item => item.Kind == "assistant" && item.TurnId == nextTurnId && item.Body == "After native stop.");
                await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
                {
                    CommandId = "compact-native", SessionId = sessionId, Type = "thread.compact"
                });
                var compact = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, patches, "compact-native");
                Assert.Equal("accepted", compact.Status);
                var compacted = await AppServerControlHostTestClient.WaitForHistoryWindowAsync(process.StandardOutput, process.StandardInput,
                    patches, sessionId, h => h.CurrentTurn.TurnId == compact.TurnStarted!.TurnId && h.CurrentTurn.State == (compactFailure ? "failed" : "completed"),
                    TimeSpan.FromSeconds(10), count: 96);
                Assert.Contains(compacted.History, item => item.Kind == "user" && item.Body == "/compact");
                Assert.Contains(compacted.History, item => item.Kind == "assistant" && item.Body == (compactFailure ? "Error during compaction: simulated native failure." : "Native compaction completed."));
                Assert.NotNull(compacted.CurrentTurn.StartedAt);
                Assert.Equal(compactFailure ? 124 : 11, compacted.Session.Usage?.ContextUsedTokens);
                if (compactFailure) Assert.Contains("simulated native failure", compacted.Session.LastError, StringComparison.Ordinal);
                return;
            }
            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "steer-active", SessionId = sessionId, Type = "turn.steer",
                SteerTurn = new AppServerControlSteerRequest { Text = "Append the steering answer.", ExpectedTurnId = turnId }
            });
            var steered = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, patches, "steer-active");
            Assert.Equal("accepted", steered.Status);
            Assert.Equal(turnId, steered.Accepted?.TurnId);
            var completed = await AppServerControlHostTestClient.WaitForHistoryWindowAsync(process.StandardOutput, process.StandardInput,
                patches, sessionId, h => h.CurrentTurn.TurnId == turnId && h.CurrentTurn.State == "completed",
                TimeSpan.FromSeconds(10), count: 96);
            var replies = completed.History.Where(item => item.Kind == "assistant" && item.TurnId == turnId).ToArray();
            Assert.Equal(new[] { "Initial answer.", "Steered final answer." }, replies.Select(item => item.Body), StringComparer.Ordinal);
            Assert.Equal(new[] { "native-original", "native-steered" }, replies.Select(item => item.ItemId), StringComparer.Ordinal);
            Assert.Equal(2, completed.History.Count(item => item.Kind == "user" && item.TurnId == turnId));
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            _ = await process.StandardError.ReadToEndAsync(Xunit.TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(Xunit.TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task MtAgentHost_UsesClaudeNativeCatalogueEffortChangesAndPlanReview()
    {
        using var fakeClaude = FakeClaudePathScope.Create();
        using var process = StartAgentHost(ResolveAgentHostPath());
        const string sessionId = "claude-native-catalogue";
        var patches = new Queue<AppServerControlHostHistoryPatchEnvelope>();
        try
        {
            _ = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            var attach = new AppServerControlHostCommandEnvelope
            {
                CommandId = "attach-native-claude",
                SessionId = sessionId,
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = sessionId,
                    Provider = "claude",
                    RuntimeKind = "claude-agent-sdk",
                    ExecutablePath = fakeClaude.ExecutablePath,
                    WorkingDirectory = fakeClaude.Root
                }
            };
            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, attach);
            var attached = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, patches, attach.CommandId);
            Assert.True(attached.Status == "accepted", attached.Message ?? $"Claude attach returned {attached.Status}.");
            var initial = await AppServerControlHostTestClient.GetHistoryWindowAsync(
                process.StandardOutput, process.StandardInput, patches, sessionId, count: 96);
            Assert.Equal("Claude", initial.Session.AgentName);
            Assert.Equal("claude-test", Assert.Single(initial.QuickSettings.ModelOptions).Value);
            Assert.Equal(2, Assert.Single(initial.QuickSettings.ModelOptions).EffortOptions?.Count);
            Assert.Contains(initial.Session.AvailableCommands, command => command.Name == "review" && command.InputHint == "<file>");
            Assert.Contains(initial.Notices, notice => notice.Detail?.Contains("0.3.300", StringComparison.Ordinal) == true);
            var iteration = 0;
            foreach (var effort in new string?[] { "low", "high", null })
            {
                var commandId = FormattableString.Invariant($"claude-plan-{++iteration}");
                await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
                {
                    CommandId = commandId,
                    SessionId = sessionId,
                    Type = "turn.start",
                    StartTurn = new AppServerControlTurnRequest
                    {
                        Text = iteration == 3 ? "Ask native question." : "Plan acceptance only.", Model = "claude-test", Effort = effort,
                        PlanMode = AppServerControlQuickSettings.PlanModeOn
                    }
                });
                var started = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, patches, commandId);
                Assert.Equal("accepted", started.Status);
                if (iteration == 3)
                {
                    var pending = await AppServerControlHostTestClient.WaitForHistoryWindowAsync(process.StandardOutput, process.StandardInput,
                        patches, sessionId, h => h.Requests.Any(request => request.Kind == "interview" && request.State == "open"),
                        TimeSpan.FromSeconds(10), count: 96);
                    var request = Assert.Single(pending.Requests, request => request.Kind == "interview" && request.State == "open");
                    var question = Assert.Single(request.Questions);
                    Assert.True(question.MultiSelect);
                    Assert.True(question.AllowCustomAnswer);
                    await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
                    {
                        CommandId = "answer-claude-question", SessionId = sessionId, Type = "user-input.resolve",
                        ResolveUserInput = new AppServerControlUserInputResolutionCommand
                        {
                            RequestId = request.RequestId,
                            Answers = [new AppServerControlAnsweredQuestion { QuestionId = question.Id, Answers = ["Blue", "Violet"] }]
                        }
                    });
                    Assert.Equal("accepted", (await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, patches, "answer-claude-question")).Status);
                }
                var completed = await AppServerControlHostTestClient.WaitForHistoryWindowAsync(
                    process.StandardOutput, process.StandardInput, patches, sessionId,
                    h => h.CurrentTurn.TurnId == started.TurnStarted?.TurnId && h.CurrentTurn.State == "completed", TimeSpan.FromSeconds(15), count: 96);
                Assert.Equal(effort, completed.CurrentTurn.Effort);
                Assert.Equal("claude-test", Assert.Single(completed.QuickSettings.ModelOptions).Value);
                Assert.Equal(new[] { "low", "high" }, completed.QuickSettings.EffortOptions.Select(option => option.Value), StringComparer.Ordinal);
                if (iteration == 3) Assert.Contains("answer=Blue, Violet", AppServerControlHostTestClient.CollectAssistantText(completed), StringComparison.Ordinal);
                else Assert.Contains(completed.History, item => item.TurnId == completed.CurrentTurn.TurnId && item.ItemType == "proposed_plan" && item.Body.Contains("TLBX_CLAUDE_PLAN_FAKE", StringComparison.Ordinal));
                Assert.DoesNotContain(completed.Requests, request => request.State == "open");
                if (iteration != 3) Assert.Contains(completed.Items, item => item.Title == "Plan review" && item.Status == "completed");
                Assert.DoesNotContain(completed.Session.AvailableCommands, command => command.Name == "exit");
                var usage = Assert.IsType<AppServerControlUsageSummary>(completed.Session.Usage);
                Assert.Equal(119, usage.ContextUsedTokens);
                Assert.Equal(200000, usage.ContextWindowTokens);
                Assert.Equal(15000 * iteration, usage.InputTokens);
                Assert.Equal(100 * iteration, usage.OutputTokens);
                Assert.Equal(5000 * iteration, usage.CachedReadTokens);
                Assert.Null(usage.ReasoningTokens);
                Assert.Equal(0.01m * iteration, usage.CostAmount);
                Assert.Equal("USD", usage.CostCurrency);
                attach.CommandId = FormattableString.Invariant($"claude-reattach-{iteration}");
                await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, attach);
                Assert.Equal("accepted", (await AppServerControlHostTestClient.ReadResultAsync(
                    process.StandardOutput, patches, attach.CommandId)).Status);
            }
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            _ = await process.StandardError.ReadToEndAsync(Xunit.TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(Xunit.TestContext.Current.CancellationToken);
        }
    }

    [Fact]
    public async Task MtAgentHost_DrivesClaudeAgentSdkAcrossApprovedMultiTurnSession()
    {
        using var fakeClaude = FakeClaudePathScope.Create();
        using var process = StartAgentHost(ResolveAgentHostPath());
        var sessionId = "session-claude-sdk-" + Guid.NewGuid().ToString("N");
        var resumeThreadId = Guid.NewGuid().ToString();
        var pendingPatches = new Queue<AppServerControlHostHistoryPatchEnvelope>();
        var imagePath = Path.Combine(fakeClaude.Root, "pasted-image.png");
        var imageBytes = Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII=");
        await File.WriteAllBytesAsync(imagePath, imageBytes, Xunit.TestContext.Current.CancellationToken);
        string? lastApprovalRequestId = null;

        try
        {
            var hello = await AppServerControlHostTestClient.ReadHelloAsync(process.StandardOutput);
            Assert.Contains("claude-agent-sdk", hello.Providers, StringComparer.Ordinal);

            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "attach",
                SessionId = sessionId,
                Type = "runtime.attach",
                AttachRuntime = new AppServerControlAttachRuntimeRequest
                {
                    SessionId = sessionId,
                    Provider = "claude",
                    RuntimeKind = "claude-agent-sdk",
                    ExecutablePath = fakeClaude.ExecutablePath,
                    WorkingDirectory = fakeClaude.Root,
                    ResumeThreadId = resumeThreadId
                }
            });
            var attach = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, "attach");
            Assert.Equal("accepted", attach.Status);

            var providerThreadId = string.Empty;
            for (var turnNumber = 1; turnNumber <= 2; turnNumber++)
            {
                var turnNumberText = turnNumber.ToString(CultureInfo.InvariantCulture);
                var commandId = $"turn-{turnNumberText}";
                await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
                {
                    CommandId = commandId,
                    SessionId = sessionId,
                    Type = "turn.start",
                    StartTurn = new AppServerControlTurnRequest
                    {
                        Text = $"Inspect turn {turnNumberText}.",
                        Attachments = turnNumber == 1
                            ?
                            [
                                new AppServerControlAttachmentReference
                                {
                                    Kind = "image",
                                    Path = imagePath,
                                    MimeType = "image/png",
                                    DisplayName = "pasted-image.png"
                                }
                            ]
                            : [],
                        PermissionMode = AppServerControlQuickSettings.PermissionModeManual
                    }
                });
                var started = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, commandId);
                Assert.Equal("accepted", started.Status);

                var approvalWindow = await AppServerControlHostTestClient.WaitForHistoryWindowAsync(
                    process.StandardOutput,
                    process.StandardInput,
                    pendingPatches,
                    sessionId,
                    window => window.Requests.Any(request => request.State == "open"),
                    TimeSpan.FromSeconds(20),
                    count: 128);
                var approval = Assert.Single(approvalWindow.Requests, request => request.State == "open");
                Assert.Equal("tool_approval", approval.Kind);
                lastApprovalRequestId = approval.RequestId;

                await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
                {
                    CommandId = $"approve-{turnNumberText}",
                    SessionId = sessionId,
                    Type = "request.resolve",
                    ResolveRequest = new AppServerControlRequestResolutionCommand
                    {
                        RequestId = approval.RequestId,
                        Decision = "accept"
                    }
                });
                var resolved = await AppServerControlHostTestClient.ReadResultAsync(process.StandardOutput, pendingPatches, $"approve-{turnNumberText}");
                Assert.Equal("accepted", resolved.Status);

                var completed = await AppServerControlHostTestClient.WaitForHistoryWindowAsync(
                    process.StandardOutput,
                    process.StandardInput,
                    pendingPatches,
                    sessionId,
                    window => window.CurrentTurn.State == "completed",
                    TimeSpan.FromSeconds(20),
                    count: 192);
                Assert.Contains($"Fake Claude SDK reply {turnNumberText}", AppServerControlHostTestClient.CollectAssistantText(completed), StringComparison.Ordinal);
                if (turnNumber == 1)
                {
                    Assert.Contains(
                        $"[image image/png {imageBytes.Length.ToString(CultureInfo.InvariantCulture)} bytes]",
                        AppServerControlHostTestClient.CollectAssistantText(completed),
                        StringComparison.Ordinal);
                }
                Assert.Contains(completed.Requests, request => request.RequestId == approval.RequestId && request.Decision == "accept");
                if (turnNumber == 1)
                {
                    providerThreadId = completed.Thread.ThreadId;
                    Assert.Equal(resumeThreadId, providerThreadId);
                }
                else Assert.Equal(providerThreadId, completed.Thread.ThreadId);
            }

            Assert.False(string.IsNullOrWhiteSpace(lastApprovalRequestId));
            await AppServerControlHostTestClient.WriteCommandAsync(process.StandardInput, new AppServerControlHostCommandEnvelope
            {
                CommandId = "approve-already-resolved",
                SessionId = sessionId,
                Type = "request.resolve",
                ResolveRequest = new AppServerControlRequestResolutionCommand
                {
                    RequestId = lastApprovalRequestId,
                    Decision = "accept"
                }
            });
            var duplicateResolution = await AppServerControlHostTestClient.ReadResultAsync(
                process.StandardOutput,
                pendingPatches,
                "approve-already-resolved");
            Assert.Equal("rejected", duplicateResolution.Status);
            Assert.Contains("was not found", duplicateResolution.Message, StringComparison.Ordinal);
        }
        finally
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
            _ = await process.StandardError.ReadToEndAsync(Xunit.TestContext.Current.CancellationToken);
            await process.WaitForExitAsync(Xunit.TestContext.Current.CancellationToken);
        }
    }

    private static Process StartAgentHost(string hostDll)
    {
        var isNativeExecutable = string.Equals(Path.GetExtension(hostDll), ".exe", StringComparison.OrdinalIgnoreCase);
        var startInfo = new ProcessStartInfo
        {
            FileName = isNativeExecutable ? hostDll : "dotnet",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!isNativeExecutable)
        {
            startInfo.ArgumentList.Add(hostDll);
        }
        startInfo.ArgumentList.Add("--stdio");
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Failed to start mtagenthost.");
    }

    private static string ResolveAgentHostPath()
    {
        var overridePath = Environment.GetEnvironmentVariable("MIDTERM_TEST_AGENTHOST_PATH");
        return !string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath)
            ? overridePath
            : MtAgentHostTestPathResolver.ResolveAgentHostDll(AppContext.BaseDirectory);
    }
}

using System.Text;
using System.Globalization;
using Ai.Tlbx.MidTerm.Common.Protocol;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class SessionApiEndpointsTests
{
    [Fact]
    public void TryGetInputBytes_TextAppendNewline_UsesCarriageReturn()
    {
        var request = new SessionInputRequest
        {
            Text = "Write-Output test",
            AppendNewline = true
        };

        var ok = SessionApiEndpoints.TryGetInputBytes(request, out var data, out var error);

        Assert.True(ok);
        Assert.Equal("", error);
        Assert.Equal("Write-Output test\r", Encoding.UTF8.GetString(data));
    }

    [Fact]
    public void TryGetInputBytes_Base64AppendNewline_UsesCarriageReturn()
    {
        var request = new SessionInputRequest
        {
            Base64 = Convert.ToBase64String([0x41, 0x42]),
            AppendNewline = true
        };

        var ok = SessionApiEndpoints.TryGetInputBytes(request, out var data, out var error);

        Assert.True(ok);
        Assert.Equal("", error);
        Assert.Equal([0x41, 0x42, 0x0D], data);
    }

    [Fact]
    public void TryGetPasteInputBytes_NormalizesClipboardNewlinesForRawShellPaste()
    {
        var request = new SessionPasteRequest
        {
            Text = "Write-Output 'A'\nWrite-Output 'B'\n"
        };

        var ok = SessionApiEndpoints.TryGetPasteInputBytes(request, out var data, out var error);

        Assert.True(ok);
        Assert.Equal("", error);
        Assert.Equal("Write-Output 'A'\rWrite-Output 'B'\r", Encoding.UTF8.GetString(data));
    }

    [Fact]
    public void TryGetPasteInputBytes_WrapsBracketedPasteAfterSanitizingControls()
    {
        var request = new SessionPasteRequest
        {
            Text = "\u001b[200~codex\nclaude\u001b[201~",
            BracketedPaste = true
        };

        var ok = SessionApiEndpoints.TryGetPasteInputBytes(request, out var data, out var error);

        Assert.True(ok);
        Assert.Equal("", error);
        Assert.Equal("\u001b[200~codex\rclaude\u001b[201~", Encoding.UTF8.GetString(data));
    }

    [Fact]
    public void TryGetPasteInputBytes_QuotesFilePathBeforePasteNormalization()
    {
        var request = new SessionPasteRequest
        {
            Text = "Q:\\repo\\file name.txt",
            IsFilePath = true
        };

        var ok = SessionApiEndpoints.TryGetPasteInputBytes(request, out var data, out var error);

        Assert.True(ok);
        Assert.Equal("", error);
        Assert.Equal("\"Q:\\repo\\file name.txt\"", Encoding.UTF8.GetString(data));
    }

    [Fact]
    public void TryGetKeyInputBytes_TranslatesNamedKeys()
    {
        var request = new SessionKeyInputRequest
        {
            Keys = ["Up", "Enter"]
        };

        var ok = SessionApiEndpoints.TryGetKeyInputBytes(request, out var data, out var error);

        Assert.True(ok);
        Assert.Equal("", error);
        Assert.Equal([0x1B, 0x5B, 0x41, 0x0D], data);
    }

    [Fact]
    public void TryGetKeyInputBytes_RejectsEmptyNonLiteralKeys()
    {
        var request = new SessionKeyInputRequest
        {
            Keys = ["Enter", ""]
        };

        var ok = SessionApiEndpoints.TryGetKeyInputBytes(request, out var data, out var error);

        Assert.False(ok);
        Assert.Equal("Keys cannot be empty.", error);
        Assert.Empty(data);
    }

    [Fact]
    public void TryGetPromptInputSequence_DefaultsToEnterSubmitWithoutInterrupt()
    {
        var request = new SessionPromptRequest
        {
            Text = "status"
        };

        var ok = SessionApiEndpoints.TryGetPromptInputSequence(
            request,
            out var interruptData,
            out var promptData,
            out var submitData,
            out var interruptDelayMs,
            out var submitDelayMs,
            out var error);

        Assert.True(ok);
        Assert.Equal("", error);
        Assert.Null(interruptData);
        Assert.Equal("status", Encoding.UTF8.GetString(promptData));
        Assert.Equal([0x0D], submitData);
        Assert.Equal(150, interruptDelayMs);
        Assert.Equal(300, submitDelayMs);
    }

    [Fact]
    public void TryGetPromptInputSequence_InterruptFirst_UsesConfiguredKeySequences()
    {
        var request = new SessionPromptRequest
        {
            Text = "continue",
            InterruptFirst = true,
            InterruptDelayMs = 25,
            SubmitDelayMs = 50
        };

        var ok = SessionApiEndpoints.TryGetPromptInputSequence(
            request,
            out var interruptData,
            out var promptData,
            out var submitData,
            out var interruptDelayMs,
            out var submitDelayMs,
            out var error);

        Assert.True(ok);
        Assert.Equal("", error);
        Assert.NotNull(interruptData);
        Assert.Equal([(byte)0x03], interruptData);
        Assert.Equal("continue", Encoding.UTF8.GetString(promptData));
        Assert.Equal([0x0D], submitData);
        Assert.Equal(25, interruptDelayMs);
        Assert.Equal(50, submitDelayMs);
    }

    [Fact]
    public void TryGetPromptInputSequence_RejectsNegativeDelays()
    {
        var request = new SessionPromptRequest
        {
            Text = "status",
            SubmitDelayMs = -1
        };

        var ok = SessionApiEndpoints.TryGetPromptInputSequence(
            request,
            out var interruptData,
            out var promptData,
            out var submitData,
            out var interruptDelayMs,
            out var submitDelayMs,
            out var error);

        Assert.False(ok);
        Assert.Equal("Delay values cannot be negative.", error);
        Assert.Null(interruptData);
        Assert.Empty(promptData);
        Assert.Empty(submitData);
        Assert.Equal(0, interruptDelayMs);
        Assert.Equal(0, submitDelayMs);
    }

    [Fact]
    public void TryBuildWorkerAutoResumePlan_UsesRegisteredWorkerWhenShellFallback()
    {
        var registry = new WorkerSessionRegistryService();
        registry.Register("s1", AiCliProfileService.CodexProfile, "codex --yolo", ["/model"], 900, 220);

        var session = new SessionInfoDto
        {
            Id = "s1",
            ShellType = "Pwsh",
            ForegroundName = "pwsh",
            Supervisor = new SessionSupervisorInfoDto
            {
                State = SessionSupervisorService.ShellState
            }
        };

        var ok = SessionApiEndpoints.TryBuildWorkerAutoResumePlan(
            "s1",
            new SessionPromptRequest
            {
                Text = "continue work"
            },
            session,
            new AiCliProfileService(),
            registry,
            out var plan);

        Assert.True(ok);
        Assert.Equal("codex --yolo", plan.LaunchCommand);
        Assert.Equal(AiCliProfileService.CodexProfile, plan.Profile);
        Assert.Equal(["/model"], plan.SlashCommands);
        Assert.Equal(900, plan.LaunchDelayMs);
        Assert.Equal(220, plan.SlashCommandDelayMs);
    }

    [Fact]
    public void TryBuildWorkerAutoResumePlan_FallsBackToProfileDefaultLaunchCommand()
    {
        var session = new SessionInfoDto
        {
            Id = "s2",
            ShellType = "Pwsh",
            ForegroundName = "pwsh",
            Supervisor = new SessionSupervisorInfoDto
            {
                State = SessionSupervisorService.ShellState
            }
        };

        var ok = SessionApiEndpoints.TryBuildWorkerAutoResumePlan(
            "s2",
            new SessionPromptRequest
            {
                Text = "continue work",
                Profile = AiCliProfileService.CodexProfile
            },
            session,
            new AiCliProfileService(),
            new WorkerSessionRegistryService(),
            out var plan);

        Assert.True(ok);
        Assert.Equal("codex --yolo", plan.LaunchCommand);
        Assert.Equal(AiCliProfileService.CodexProfile, plan.Profile);
        Assert.Empty(plan.SlashCommands);
        Assert.Equal(1200, plan.LaunchDelayMs);
    }

    [Fact]
    public void TryBuildWorkerAutoResumePlan_DoesNothingWhenSessionIsNotShell()
    {
        var session = new SessionInfoDto
        {
            Id = "s3",
            ShellType = "Pwsh",
            ForegroundName = "node",
            ForegroundCommandLine = "codex --yolo",
            Supervisor = new SessionSupervisorInfoDto
            {
                State = SessionSupervisorService.IdlePromptState
            }
        };

        var ok = SessionApiEndpoints.TryBuildWorkerAutoResumePlan(
            "s3",
            new SessionPromptRequest
            {
                Text = "continue work",
                Profile = AiCliProfileService.CodexProfile
            },
            session,
            new AiCliProfileService(),
            new WorkerSessionRegistryService(),
            out var plan);

        Assert.False(ok);
        Assert.Equal(string.Empty, plan.LaunchCommand);
    }

    [Fact]
    public void TryBuildWorkerAutoResumePlan_DoesNothingWhenSessionIsAppServerControlOnly()
    {
        var session = new SessionInfoDto
        {
            Id = "s4",
            ShellType = "Pwsh",
            ForegroundName = "pwsh",
            AppServerControlOnly = true,
            ProfileHint = AiCliProfileService.CodexProfile,
            Supervisor = new SessionSupervisorInfoDto
            {
                State = SessionSupervisorService.ShellState
            }
        };

        var ok = SessionApiEndpoints.TryBuildWorkerAutoResumePlan(
            "s4",
            new SessionPromptRequest
            {
                Text = "continue work"
            },
            session,
            new AiCliProfileService(),
            new WorkerSessionRegistryService(),
            out var plan);

        Assert.False(ok);
        Assert.Equal(string.Empty, plan.LaunchCommand);
    }

    [Fact]
    public void DetectProfile_UsesProfileHintBeforeShellFallback()
    {
        var profile = new AiCliProfileService().DetectProfile(new SessionInfoDto
        {
            ShellType = "Pwsh",
            ForegroundName = "pwsh",
            ProfileHint = AiCliProfileService.ClaudeProfile
        });

        Assert.Equal(AiCliProfileService.ClaudeProfile, profile);
    }

    [Fact]
    public void GetPreferredClipboardProcessId_PrefersHostPid()
    {
        var session = new SessionInfo
        {
            Pid = 41,
            HostPid = 99
        };

        var preferred = SessionApiEndpoints.GetPreferredClipboardProcessId(session);

        Assert.Equal(99, preferred);
    }

    [Fact]
    public void GetPreferredClipboardProcessId_FallsBackToSessionPid()
    {
        var session = new SessionInfo
        {
            Pid = 41,
            HostPid = 0
        };

        var preferred = SessionApiEndpoints.GetPreferredClipboardProcessId(session);

        Assert.Equal(41, preferred);
    }

    [Fact]
    public async Task TrySetClipboardImageAsync_SkipsFallbackWhenSessionScopedSetterSucceeds()
    {
        var fallbackCalled = false;

        var ok = await SessionApiEndpoints.TrySetClipboardImageAsync(
            _ => Task.FromResult(true),
            _ =>
            {
                fallbackCalled = true;
                return Task.FromResult(true);
            });

        Assert.True(ok);
        Assert.False(fallbackCalled);
    }

    [Fact]
    public async Task TrySetClipboardImageAsync_UsesFallbackWhenSessionScopedSetterFails()
    {
        var fallbackCalled = false;

        var ok = await SessionApiEndpoints.TrySetClipboardImageAsync(
            _ => Task.FromResult(false),
            _ =>
            {
                fallbackCalled = true;
                return Task.FromResult(true);
            });

        Assert.True(ok);
        Assert.True(fallbackCalled);
    }

    [Fact]
    public async Task SessionPromptPlanExecutor_ExecutesInterruptPromptSubmitAndFollowupsInOrder()
    {
        var plan = new SessionApiEndpoints.SessionPromptExecutionPlan(
            InterruptData: [(byte)0x03],
            PromptData: Encoding.UTF8.GetBytes("status"),
            SubmitData: [0x0D],
            InterruptDelayMs: 25,
            SubmitDelayMs: 50,
            FollowupSubmitCount: 2,
            FollowupSubmitDelayMs: 75);

        var steps = new List<string>();

        await SessionPromptPlanExecutor.ExecuteAsync(
            plan,
            (data, _) =>
            {
                steps.Add(FormattableString.Invariant($"send:{Convert.ToHexString(data)}"));
                return Task.CompletedTask;
            },
            (delayMs, _) =>
            {
                steps.Add(FormattableString.Invariant($"delay:{delayMs}"));
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(
            [
                "send:03",
                "delay:25",
                $"send:{Convert.ToHexString(Encoding.UTF8.GetBytes("status"))}",
                "delay:50",
                "send:0D",
                "delay:75",
                "send:0D",
                "delay:75",
                "send:0D"
            ],
            steps);
    }

    [Fact]
    public async Task SessionPromptPlanExecutor_SkipsInterruptAndDelayStepsWhenNotRequested()
    {
        var plan = new SessionApiEndpoints.SessionPromptExecutionPlan(
            InterruptData: null,
            PromptData: Encoding.UTF8.GetBytes("go"),
            SubmitData: [0x0D],
            InterruptDelayMs: 0,
            SubmitDelayMs: 0,
            FollowupSubmitCount: 0,
            FollowupSubmitDelayMs: 0);

        var steps = new List<string>();

        await SessionPromptPlanExecutor.ExecuteAsync(
            plan,
            (data, _) =>
            {
                steps.Add(FormattableString.Invariant($"send:{Encoding.UTF8.GetString(data)}"));
                return Task.CompletedTask;
            },
            (delayMs, _) =>
            {
                steps.Add(FormattableString.Invariant($"delay:{delayMs}"));
                return Task.CompletedTask;
            },
            CancellationToken.None);

        Assert.Equal(["send:go", "send:\r"], steps);
    }
}

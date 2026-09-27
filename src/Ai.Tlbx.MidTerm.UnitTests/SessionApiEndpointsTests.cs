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

}

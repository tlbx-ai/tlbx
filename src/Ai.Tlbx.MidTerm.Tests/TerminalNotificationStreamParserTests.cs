using System.Text;
using Ai.Tlbx.MidTerm.Models.Sessions;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Settings;
using Xunit;

namespace Ai.Tlbx.MidTerm.Tests;

public sealed class TerminalNotificationStreamParserTests
{
    [Fact]
    public void Parse_RecognizesBelOsc9TmuxAndOsc777AcrossFrames()
    {
        var parser = new TerminalNotificationStreamParser();
        var notifications = new List<TerminalNotificationMessage>();

        notifications.AddRange(parser.Parse("session", [0x07, 0x1B, (byte)']', (byte)'9', (byte)';']));
        notifications.AddRange(parser.Parse("session", Encoding.UTF8.GetBytes("Agent turn complete\a")));
        notifications.AddRange(parser.Parse(
            "session",
            Encoding.UTF8.GetBytes("\x1bPtmux;\x1b\x1b]9;Approval requested\a\x1b\\")));
        notifications.AddRange(parser.Parse(
            "session",
            Encoding.UTF8.GetBytes("\x1b]777;notify;Claude Code;Waiting; for input\x1b\\")));

        Assert.Collection(
            notifications,
            item => AssertMessage(item, "bel"),
            item => AssertMessage(item, "osc9", body: "Agent turn complete"),
            item => AssertMessage(item, "osc9", body: "Approval requested"),
            item => AssertMessage(item, "osc777", "Claude Code", "Waiting; for input"));
    }

    [Fact]
    public void Parse_HandlesC1SequencesAndDoesNotCountOscTerminatorsAsBell()
    {
        var parser = new TerminalNotificationStreamParser();
        var bytes = new byte[] { 0x9D }
            .Concat(Encoding.UTF8.GetBytes("9;C1 notification"))
            .Append((byte)0x9C)
            .Concat(Encoding.UTF8.GetBytes("\x1b]9;BEL terminator\a"))
            .ToArray();

        var notifications = parser.Parse("session", bytes);

        Assert.Equal(2, notifications.Count);
        Assert.All(notifications, item => Assert.Equal("osc9", item.Protocol));
    }

    [Fact]
    public void Parse_RecognizesOscAtEveryByteFragmentBoundary()
    {
        var sequence = Encoding.UTF8.GetBytes("\x1b]9;Fertig ✓\x1b\\");

        for (var split = 0; split <= sequence.Length; split++)
        {
            var parser = new TerminalNotificationStreamParser();
            var notifications = parser.Parse("session", sequence.AsSpan(0, split)).ToList();
            notifications.AddRange(parser.Parse("session", sequence.AsSpan(split)));

            var notification = Assert.Single(notifications);
            AssertMessage(notification, "osc9", body: "Fertig ✓");
        }
    }

    [Fact]
    public void Parse_IgnoresProgressControlMalformedAndOversizedOscPayloads()
    {
        var parser = new TerminalNotificationStreamParser();
        var data = string.Concat(
            "\x1b]9;4;1;50\x1b\\",
            "\x1b]9;9;file:///workspace\a",
            "\x1b]777;progress;Build;50\a",
            "\x1b]777;notify;missing-body-separator\a",
            "\x1b]9;", new string('x', 5000), "\a",
            "\x1b]9;valid after discard\a");

        var notifications = parser.Parse("session", Encoding.UTF8.GetBytes(data));

        var notification = Assert.Single(notifications);
        AssertMessage(notification, "osc9", body: "valid after discard");
    }

    [Fact]
    public void Parse_SanitizesAndLimitsTerminalControlledText()
    {
        var parser = new TerminalNotificationStreamParser();
        var data = $"\x1b]777;notify;Build\n\u202e;done \x1b[31m{new string('x', 300)}\x1b\\";

        var notification = Assert.Single(parser.Parse("session", Encoding.UTF8.GetBytes(data)));

        Assert.Equal("Build", notification.Title);
        Assert.NotNull(notification.Body);
        Assert.DoesNotContain("\x1b", notification.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("\u202e", notification.Body, StringComparison.Ordinal);
        Assert.EndsWith("…", notification.Body, StringComparison.Ordinal);
    }

    [Fact]
    public void Telemetry_EmitsOnlyLiveParsedEventsAndKeepsBellAccounting()
    {
        var telemetry = new SessionTelemetryService();
        var received = new List<TerminalNotificationMessage>();
        telemetry.TerminalNotificationReceived += received.Add;

        telemetry.RecordOutput("session", Encoding.UTF8.GetBytes("\x1b]9;done\a\a"));
        var snapshot = telemetry.GetSnapshot("session");
        _ = telemetry.GetActivity("session", 10, 10);

        Assert.Equal(2, received.Count);
        Assert.Equal(["osc9", "bel"], received.Select(item => item.Protocol), StringComparer.Ordinal);
        Assert.Equal(1, snapshot.TotalBellCount);
    }

    [Fact]
    public void Telemetry_PublishesSanitizedForcedAdHocNotification()
    {
        var telemetry = new SessionTelemetryService();
        TerminalNotificationMessage? received = null;
        telemetry.TerminalNotificationReceived += notification => received = notification;

        var published = telemetry.TryPublishAdHocNotification(
            "session",
            " tlbx\n",
            "Release \x1b[31mcomplete",
            NotificationPrioritySetting.Important);

        Assert.True(published);
        Assert.NotNull(received);
        AssertMessage(received, "cli", "tlbx", "Release complete");
        Assert.True(received.Force);
        Assert.Equal(NotificationPrioritySetting.Important, received.Priority);
        Assert.False(telemetry.TryPublishAdHocNotification("session", "tlbx", "   "));
    }

    private static void AssertMessage(
        TerminalNotificationMessage message,
        string protocol,
        string? title = null,
        string? body = null)
    {
        Assert.Equal("terminal-notification", message.Type);
        Assert.Equal("session", message.SessionId);
        Assert.Equal(protocol, message.Protocol);
        Assert.Equal(title, message.Title);
        Assert.Equal(body, message.Body);
    }
}

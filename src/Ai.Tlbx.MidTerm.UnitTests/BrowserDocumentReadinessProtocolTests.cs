using Ai.Tlbx.MidTerm.Models.Browser;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.WebPreview;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class BrowserDocumentReadinessProtocolTests
{
    [Theory]
    [InlineData("soft", false)]
    [InlineData("force", false)]
    [InlineData("hard", false)]
    [InlineData("force", true)]
    public async Task BatchReadWaitsForTheNewDocumentReadyReplyWithoutReplayingReload(string mode, bool loseAck)
    {
        var commands = new BrowserCommandService();
        var readyRequested = new TaskCompletionSource<BrowserWsMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var mutations = 0;
        var reads = 0;
        commands.TryRegisterClient("old-socket", "session", "alpha", "frame", message =>
        {
            Assert.Equal("reload", message.Command);
            mutations++;
            if (!loseAck) commands.ReceiveResult(new BrowserWsResult
            {
                Id = message.Id, Success = true, DocumentId = "old-document", NavigationVersionBefore = 0
            }, "old-socket");
            commands.TryRegisterClient("new-socket", "session", "alpha", "frame", next =>
            {
                if (next.Command == "ready") readyRequested.TrySetResult(next);
                else
                {
                    reads++;
                    commands.ReceiveResult(new BrowserWsResult
                    {
                        Id = next.Id, Success = true, Result = "boot-new-document", DocumentId = "new-document"
                    }, "new-socket");
                }
            }, documentId: "new-document");
        }, documentId: "old-document");
        var batch = commands.ExecuteBatchAsync(new BrowserBatchRequest
        {
            SessionId = "session", PreviewName = "alpha", Commands =
            [new() { Command = "reload", Value = mode }, new() { Command = "exec", Value = "__qaBoot" }]
        }, TestContext.Current.CancellationToken);

        var ready = await readyRequested.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.False(batch.IsCompleted);
        Assert.Equal(0, reads);
        commands.ReceiveResult(new BrowserWsResult
        {
            Id = ready.Id, Success = true, DocumentId = "new-document"
        }, "new-socket");
        var result = await batch;

        Assert.True(result.Success, result.Error);
        Assert.Equal("boot-new-document", result.Results[1].Result);
        Assert.Equal(1, mutations);
        Assert.Equal(1, reads);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OldDocumentAndItsReconnectedSocketCannotSatisfyReload(bool reconnect)
    {
        var commands = new BrowserCommandService();
        var mutations = 0;
        var readyReads = 0;
        commands.TryRegisterClient("old-socket", "session", "alpha", "frame", message =>
        {
            mutations++;
            commands.ReceiveResult(new BrowserWsResult { Id = message.Id, Success = true, DocumentId = "old-document" }, "old-socket");
            if (reconnect) commands.TryRegisterClient("reconnected", "session", "alpha", "frame", next =>
            {
                readyReads++;
                commands.ReceiveResult(new BrowserWsResult { Id = next.Id, Success = true, DocumentId = "old-document" }, "reconnected");
            }, documentId: "old-document");
        }, documentId: "old-document");

        var result = await commands.ExecuteCommandAsync(new BrowserCommandRequest
        {
            Command = "reload", SessionId = "session", PreviewName = "alpha", Timeout = 1
        }, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("outcome is unknown", result.Error);
        Assert.Equal(1, mutations);
        Assert.Equal(0, readyReads);
    }

    [Theory]
    [InlineData("navigate", false)]
    [InlineData("navigate", true)]
    [InlineData("click", false)]
    public async Task ProvenFragmentOrSpaTransitionCanRemainOnTheSameDocument(string command, bool currentFragment)
    {
        var commands = new BrowserCommandService();
        var mutations = 0;
        commands.TryRegisterClient("socket", "session", "alpha", "frame", message =>
        {
            if (message.Command != "ready")
            {
                mutations++;
                if (!currentFragment) commands.UpdateClientState("socket", new BrowserWsResult { DocumentId = "document", NavigationVersion = 8 });
            }
            commands.ReceiveResult(new BrowserWsResult
            {
                Id = message.Id, Success = true, DocumentId = "document",
                NavigationVersionBefore = 7, NavigationKind = command == "navigate"
                    ? currentFragment ? "same-document-current" : "same-document" : null
            }, "socket");
        }, documentId: "document");

        var result = await commands.ExecuteCommandAsync(new BrowserCommandRequest
        {
            Command = command, Value = "#next", SessionId = "session", PreviewName = "alpha",
            WaitForNavigation = command == "click"
        }, TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.Error);
        Assert.Equal("document", result.DocumentId);
        Assert.Equal(1, mutations);
    }

    [Theory]
    [InlineData("navigate", 8)]
    [InlineData("click", 7)]
    public async Task FullNavigationOrUnchangedHistoryCannotUseTheOldDocument(string command, long version)
    {
        var commands = new BrowserCommandService();
        commands.TryRegisterClient("socket", "session", "alpha", "frame", message =>
        {
            commands.UpdateClientState("socket", new BrowserWsResult { DocumentId = "document", NavigationVersion = version });
            commands.ReceiveResult(new BrowserWsResult
            {
                Id = message.Id, Success = true, DocumentId = "document", NavigationVersionBefore = 7
            }, "socket");
        }, documentId: "document");

        var result = await commands.ExecuteCommandAsync(new BrowserCommandRequest
        {
            Command = command, Value = "/other", SessionId = "session", PreviewName = "alpha",
            WaitForNavigation = command == "click", Timeout = 1
        }, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("mutation was not repeated", result.Error);
    }

    [Fact]
    public async Task NewDocumentOnAChangedTargetCannotSatisfyTheOriginalNavigation()
    {
        var previews = new WebPreviewService(2000);
        previews.SetTarget("session", "alpha", "https://example.invalid/first");
        var revision = previews.GetPreviewSession("session", "alpha")!.TargetRevision;
        var commands = new BrowserCommandService(webPreviewService: previews);
        var mutations = 0;
        commands.TryRegisterClient("old", "session", "alpha", "frame", message =>
        {
            mutations++;
            commands.ReceiveResult(new BrowserWsResult { Id = message.Id, Success = true, DocumentId = "old" }, "old");
            previews.SetTarget("session", "alpha", "https://example.invalid/replaced");
            commands.TryRegisterClient("new", "session", "alpha", "frame", _ => throw new InvalidOperationException("Must not probe another target."),
                targetRevision: previews.GetPreviewSession("session", "alpha")!.TargetRevision, documentId: "new");
        }, targetRevision: revision, documentId: "old");

        var result = await commands.ExecuteCommandAsync(new BrowserCommandRequest
        {
            Command = "reload", SessionId = "session", PreviewName = "alpha"
        }, TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("target changed", result.Error);
        Assert.Equal(1, mutations);
    }

    [Fact]
    public async Task CancellationAfterDispatchDoesNotRepeatTheMutation()
    {
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var commands = new BrowserCommandService();
        var mutations = 0;
        commands.TryRegisterClient("socket", "session", "alpha", "frame", message =>
        {
            mutations++;
            commands.ReceiveResult(new BrowserWsResult { Id = message.Id, Success = true, DocumentId = "document" }, "socket");
            cancelled.Cancel();
        }, documentId: "document");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => commands.ExecuteCommandAsync(new BrowserCommandRequest
        {
            Command = "reload", SessionId = "session", PreviewName = "alpha"
        }, cancelled.Token));
        Assert.Equal(1, mutations);
    }
}

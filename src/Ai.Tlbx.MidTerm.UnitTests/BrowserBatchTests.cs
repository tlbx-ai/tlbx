using Ai.Tlbx.MidTerm.Models.Browser;
using Ai.Tlbx.MidTerm.Services.Browser;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public class BrowserBatchTests
{
    [Fact]
    public async Task JsonRequestWithoutTimeoutUsesDefaultBudget()
    {
        var request = System.Text.Json.JsonSerializer.Deserialize(
            """{"sessionId":"s","previewName":"default","commands":[{"command":"url"}]}""",
            Ai.Tlbx.MidTerm.Services.AppJsonContext.Default.BrowserBatchRequest)!;
        var service = new BrowserCommandService();
        service.TryRegisterClient("c", "s", "default", "p", msg =>
            service.ReceiveResult(new BrowserWsResult { Id = msg.Id, Success = true, Result = "https://example.com" }, "c"));
        var result = await service.ExecuteBatchAsync(request, CancellationToken.None);
        Assert.True(result.Success, result.Error);
        Assert.Single(result.Results);
    }

    [Fact]
    public async Task ReadinessWakesOnAttachWithoutWaitingForPollInterval()
    {
        var service = new BrowserCommandService();
        var task = service.WaitForControllableAsync("https://example.com", "s", "default",
            timeout: TimeSpan.FromSeconds(5), pollInterval: TimeSpan.FromSeconds(4));
        service.TryRegisterClient("c", "s", "default", "p", _ => { });
        var result = await task.WaitAsync(TimeSpan.FromSeconds(1));
        Assert.True(result.Controllable);
    }

    [Fact]
    public async Task RunsInOrderWithinOuterScopeAndStopsOnFailure()
    {
        var service = new BrowserCommandService();
        var calls = new List<string>();
        service.TryRegisterClient("c", "s", "default", "p", msg =>
        {
            calls.Add(msg.Command);
            service.ReceiveResult(new BrowserWsResult { Id = msg.Id, Success = msg.Command != "click", Result = msg.Value, Error = msg.Command == "click" ? "missing element" : null }, "c");
        });
        var result = await service.ExecuteBatchAsync(new BrowserBatchRequest
        {
            SessionId = "s", PreviewName = "default", Commands =
            [new() { Command = "fill", SessionId = "other", Value = "value" }, new() { Command = "click" }, new() { Command = "exec" }]
        }, CancellationToken.None);
        Assert.Equal(new[] { "fill", "click" }, calls, StringComparer.Ordinal);
        Assert.False(result.Success);
        Assert.Equal(1, result.FailedIndex);
        Assert.Equal("value", result.Results[0].Result);
        Assert.Equal("missing element", result.Results[1].Error);
    }

    [Fact]
    public async Task RejectsInvalidTailBeforeExecutingAnyAction()
    {
        var service = new BrowserCommandService();
        var dispatched = false;
        service.TryRegisterClient("c", "s", "default", "p", _ => dispatched = true);
        var result = await service.ExecuteBatchAsync(new BrowserBatchRequest { SessionId = "s", Commands = [new() { Command = "click" }, new() { Command = "batch" }] }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Empty(result.Results);
        Assert.False(dispatched);
    }

    [Fact]
    public async Task DisconnectNeverReplaysActionOrDispatchesRemainder()
    {
        var service = new BrowserCommandService();
        var calls = 0;
        service.TryRegisterClient("c", "s", "default", "p", _ => { calls++; service.UnregisterClient("c"); });
        var result = await service.ExecuteBatchAsync(new BrowserBatchRequest { SessionId = "s", Commands = [new() { Command = "click" }, new() { Command = "fill" }] }, CancellationToken.None);
        Assert.False(result.Success);
        Assert.Equal(0, result.FailedIndex);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task ScreenshotOptionsAndResultsRemainOrdered()
    {
        var service = new BrowserCommandService();
        var modes = new List<bool>();
        service.TryRegisterClient("c", "s", "default", "p", msg =>
        {
            modes.Add(msg.FullPage);
            service.ReceiveResult(new BrowserWsResult { Id = msg.Id, Success = true, Result = "data:image/png;base64,test" }, "c");
        });
        var result = await service.ExecuteBatchAsync(new BrowserBatchRequest { SessionId = "s", Commands = [new() { Command = "screenshot" }, new() { Command = "screenshot", FullPage = true }] }, CancellationToken.None);
        Assert.True(result.Success);
        Assert.Equal(new[] { false, true }, modes);
        Assert.Equal(new[] { 0, 1 }, result.Results.Select(r => r.Index));
    }
}

using System.Text.Json;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class CodexMcpThreadIdentityTests
{
    private const string Root = "11111111-1111-4111-8111-111111111111";
    private const string Parent = "22222222-2222-4222-8222-222222222222";
    private const string Child = "33333333-3333-4333-8333-333333333333";

    [Fact]
    public async Task ExactNativeSubagentAncestryResolvesRootWithoutUsingForkOrWorkingDirectory()
    {
        var requests = new List<string>();
        var threads = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            [Child] = Thread(Child, Parent), [Parent] = Thread(Parent, Root),
            [Root] = Parse($$"""{"id":"{{Root}}","parentThreadId":null,"forkedFromId":"{{Child}}","cwd":"unrelated"}""")
        };
        var result = await CodexCliIdentityService.ResolveRootThreadAsync(Child, (id, _) =>
        {
            requests.Add(id);
            return Task.FromResult(threads[id]);
        }, TestContext.Current.CancellationToken);
        Assert.Equal(Root, result.RootThreadId);
        Assert.Equal(new[] { Child, Parent, Root }, requests, StringComparer.Ordinal);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"id\":\"33333333-3333-4333-8333-333333333333\"}")]
    [InlineData("{\"id\":\"11111111-1111-4111-8111-111111111111\",\"parentThreadId\":null}")]
    [InlineData("{\"id\":\"33333333-3333-4333-8333-333333333333\",\"parentThreadId\":123}")]
    [InlineData("{\"id\":\"33333333-3333-4333-8333-333333333333\",\"parentThreadId\":\"\"}")]
    public async Task MissingMalformedOrCrossThreadMetadataCannotProduceARoot(string json)
    {
        var result = await CodexCliIdentityService.ResolveRootThreadAsync(Child,
            (_, _) => Task.FromResult(Parse(json)), TestContext.Current.CancellationToken);
        Assert.Null(result.RootThreadId);
        Assert.False(string.IsNullOrEmpty(result.Diagnostic));
    }

    [Fact]
    public async Task CyclicNativeAncestryIsRejectedBeforeReadingTheSameThreadAgain()
    {
        var calls = 0;
        var result = await CodexCliIdentityService.ResolveRootThreadAsync(Child, (_, _) =>
        {
            calls++;
            return Task.FromResult(Thread(Child, Child));
        }, TestContext.Current.CancellationToken);
        Assert.Null(result.RootThreadId);
        Assert.Equal(1, calls);
        Assert.Contains("cycle", result.Diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NativeAncestryReadBudgetIsBoundedEvenForAValidUnendingChain()
    {
        static string Id(int index) => $"00000000-0000-4000-8000-{index:x12}";
        var calls = 0;
        var result = await CodexCliIdentityService.ResolveRootThreadAsync(Id(0), (id, _) =>
        {
            calls++;
            return Task.FromResult(Thread(id, Id(calls)));
        }, TestContext.Current.CancellationToken);
        Assert.Null(result.RootThreadId);
        Assert.Equal(64, calls);
        Assert.Contains("depth", result.Diagnostic, StringComparison.Ordinal);
    }

    private static JsonElement Thread(string id, string parent) =>
        Parse($$"""{"id":"{{id}}","parentThreadId":"{{parent}}"}""");

    private static JsonElement Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}

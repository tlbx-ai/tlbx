using System.Text.Json;
using System.Text.Json.Nodes;
using Ai.Tlbx.MidTerm.Services.Mcp;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TlbxMcpUpdateWorkflowTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AppliesExactlyOnceAndRequiresRestartEvenWhenExpectedVersionAlreadyVisible(bool loseResponse)
    {
        var applies = 0;
        var reads = 0;
        ValueTask<CallToolResult> Invoke(string name, IDictionary<string, JsonElement> args, CancellationToken ct)
        {
            if (name == "update_apply")
            {
                applies++;
                Assert.True(args["detached"].GetBoolean());
                Assert.Equal("literal source & x", args["source"].GetString());
                if (loseResponse) throw new IOException("POST response lost");
                return ValueTask.FromResult(Reply("{}"));
            }
            if (name == "update_status") return ValueTask.FromResult(Status());
            reads++;
            if (loseResponse && reads == 2) throw new HttpRequestException("Server restarting");
            return ValueTask.FromResult(Context(reads < 3 ? "before" : "after", "2"));
        }
        var result = await Run(Invoke, """{"source":"literal source & x","expectedVersion":"2"}""", TestContext.Current.CancellationToken);
        Assert.False(result.IsError);
        Assert.Equal("updated", Field(result, "outcome").GetString());
        Assert.True(Field(result, "restartVerified").GetBoolean());
        Assert.True(Field(result, "expectedVersionVerified").GetBoolean());
        Assert.Equal(!loseResponse, Field(result, "acknowledged").GetBoolean());
        Assert.Equal(1, applies);
        Assert.Equal(3, reads);
    }

    [Fact]
    public async Task RestartWithStaleVersionNeverSucceedsAndCancellationDoesNotReplay()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var applies = 0;
        var reads = 0;
        ValueTask<CallToolResult> Invoke(string name, IDictionary<string, JsonElement> args, CancellationToken ct)
        {
            if (name == "update_apply") { applies++; return ValueTask.FromResult(Reply("{}")); }
            if (name == "update_status") return ValueTask.FromResult(Status());
            if (++reads == 3) stop.Cancel();
            return ValueTask.FromResult(Context(reads == 1 ? "before" : "after", "1"));
        }
        var result = await Run(Invoke, "{}", stop.Token);
        Assert.True(result.IsError);
        Assert.Equal("TLBX_MCP_UPDATE_UNCERTAIN", Field(result, "outcome").GetString());
        Assert.False(Field(result, "expectedVersionVerified").GetBoolean());
        Assert.False(Field(result, "replaySafe").GetBoolean());
        Assert.Equal(1, applies);
    }

    [Fact]
    public async Task ExplicitRejectionIsFailureAndDoesNotAttemptRecovery()
    {
        var applies = 0;
        var reads = 0;
        ValueTask<CallToolResult> Invoke(string name, IDictionary<string, JsonElement> args, CancellationToken ct)
        {
            if (name == "update_apply") { applies++; return ValueTask.FromResult(McpApiResult.Error("rejected", "No update available")); }
            if (name == "update_status") return ValueTask.FromResult(Status());
            reads++;
            return ValueTask.FromResult(Context("before", "1"));
        }
        var result = await Run(Invoke, "{}", TestContext.Current.CancellationToken);
        Assert.Equal("TLBX_MCP_UPDATE_FAILED", Field(result, "outcome").GetString());
        Assert.Equal(1, applies);
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task UnavailableUpdateNeverDispatches()
    {
        var calls = new List<string>();
        ValueTask<CallToolResult> Invoke(string name, IDictionary<string, JsonElement> args, CancellationToken ct)
        {
            calls.Add(name);
            return ValueTask.FromResult(name == "context_get" ? Context("before", "1") : Reply("""{"data":{"available":false,"latestVersion":"1"}}"""));
        }
        var result = await Run(Invoke, "{}", TestContext.Current.CancellationToken);
        Assert.False(result.IsError);
        Assert.Equal("no_update", Field(result, "outcome").GetString());
        Assert.DoesNotContain("update_apply", calls, StringComparer.Ordinal);
    }

    [Fact]
    public async Task OldFailureIsIgnoredAndNewFailureTerminatesRecovery()
    {
        var statusReads = 0;
        var applies = 0;
        ValueTask<CallToolResult> Invoke(string name, IDictionary<string, JsonElement> args, CancellationToken ct)
        {
            if (name == "update_apply") { applies++; return ValueTask.FromResult(Reply("{}")); }
            if (name == "context_get") return ValueTask.FromResult(Context("before", "1"));
            return ValueTask.FromResult(Reply(++statusReads < 3
                ? """{"data":{"available":true,"latestVersion":"2","lastResult":{"found":true,"success":false,"timestamp":"old","message":"old failure"}}}"""
                : """{"data":{"available":true,"latestVersion":"2","lastResult":{"found":true,"success":false,"timestamp":"new","message":"new failure"}}}"""));
        }
        var result = await Run(Invoke, "{}", TestContext.Current.CancellationToken);
        Assert.Equal("TLBX_MCP_UPDATE_FAILED", Field(result, "outcome").GetString());
        Assert.Contains("new failure", Field(result, "message").GetString(), StringComparison.Ordinal);
        Assert.Equal(3, statusReads);
        Assert.Equal(1, applies);
    }

    [Fact]
    public async Task CancellationInsideDispatchReturnsUncertainWithoutRetry()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var applies = 0;
        ValueTask<CallToolResult> Invoke(string name, IDictionary<string, JsonElement> args, CancellationToken ct)
        {
            if (name == "update_apply") { applies++; stop.Cancel(); ct.ThrowIfCancellationRequested(); }
            return ValueTask.FromResult(name == "context_get" ? Context("before", "1") : Status());
        }
        var result = await Run(Invoke, "{}", stop.Token);
        Assert.Equal("TLBX_MCP_UPDATE_UNCERTAIN", Field(result, "outcome").GetString());
        Assert.Equal(1, applies);
    }

    [Fact]
    public async Task ExplicitSourceWithoutExpectedVersionVerifiesRestartButDoesNotClaimVersionVerification()
    {
        var reads = 0;
        ValueTask<CallToolResult> Invoke(string name, IDictionary<string, JsonElement> args, CancellationToken ct)
            => ValueTask.FromResult(name == "context_get" ? Context(++reads == 1 ? "before" : "after", "1") : name == "update_status" ? Status() : Reply("{}"));
        var result = await Run(Invoke, """{"source":"local"}""", TestContext.Current.CancellationToken);
        Assert.False(result.IsError);
        Assert.True(Field(result, "restartVerified").GetBoolean());
        Assert.False(Field(result, "expectedVersionVerified").GetBoolean());
    }

    [Fact]
    public async Task UnsupportedDetachedFalseRejectsBeforeAnyCall()
    {
        ValueTask<CallToolResult> Invoke(string name, IDictionary<string, JsonElement> args, CancellationToken ct)
            => throw new InvalidOperationException("Must not dispatch");
        var result = await Run(Invoke, """{"detached":false}""", TestContext.Current.CancellationToken);
        Assert.Equal("TLBX_MCP_INVALID_ARGUMENT", Field(result, "errorCode").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartNeverMasksFreshFailureEvenWhenVersionMatchesOrWasNotSpecified(bool expectedVersion)
    {
        var reads = 0;
        var statusReads = 0;
        ValueTask<CallToolResult> Invoke(string name, IDictionary<string, JsonElement> args, CancellationToken ct)
        {
            if (name == "context_get") return ValueTask.FromResult(Context(++reads == 1 ? "before" : "after", "1"));
            if (name == "update_apply") return ValueTask.FromResult(Reply("{}"));
            return ValueTask.FromResult(++statusReads == 1 ? Status()
                : Reply("""{"data":{"lastResult":{"found":true,"success":false,"timestamp":"new","message":"Rolled back"}}}"""));
        }
        var result = await Run(Invoke, expectedVersion ? """{"source":"local","expectedVersion":"1"}""" : """{"source":"local"}""", TestContext.Current.CancellationToken);
        Assert.True(result.IsError);
        Assert.Equal("TLBX_MCP_UPDATE_FAILED", Field(result, "outcome").GetString());
        Assert.True(Field(result, "restartVerified").GetBoolean());
    }

    [Fact]
    public async Task ForcedFullStillDispatchesWhenNormalUpdateIsUnavailable()
    {
        var reads = 0;
        var applies = 0;
        ValueTask<CallToolResult> Invoke(string name, IDictionary<string, JsonElement> args, CancellationToken ct)
        {
            if (name == "context_get") return ValueTask.FromResult(Context(++reads == 1 ? "before" : "after", "1"));
            if (name == "update_apply")
            {
                applies++;
                Assert.True(args["forceFull"].GetBoolean());
                return ValueTask.FromResult(Reply("{}"));
            }
            return ValueTask.FromResult(Reply("""{"data":{"available":false,"latestVersion":"1"}}"""));
        }
        var result = await Run(Invoke, """{"forceFull":true}""", TestContext.Current.CancellationToken);
        Assert.False(result.IsError);
        Assert.Equal("updated", Field(result, "outcome").GetString());
        Assert.Equal(1, applies);
    }

    [Fact]
    public async Task MatchingRestartDoesNotSucceedWithoutReadableUpdateResult()
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var reads = 0;
        var statusReads = 0;
        ValueTask<CallToolResult> Invoke(string name, IDictionary<string, JsonElement> args, CancellationToken ct)
        {
            if (name == "context_get") return ValueTask.FromResult(Context(++reads == 1 ? "before" : "after", "2"));
            if (name == "update_apply") return ValueTask.FromResult(Reply("{}"));
            if (++statusReads == 1) return ValueTask.FromResult(Status());
            stop.Cancel();
            return ValueTask.FromResult(McpApiResult.Error("unavailable", "Update result unavailable"));
        }
        var result = await Run(Invoke, "{}", stop.Token);
        Assert.True(result.IsError);
        Assert.Equal("TLBX_MCP_UPDATE_UNCERTAIN", Field(result, "outcome").GetString());
    }

    private static async ValueTask<CallToolResult> Run(Func<string, IDictionary<string, JsonElement>, CancellationToken, ValueTask<CallToolResult>> invoke, string json, CancellationToken ct)
    {
        var element = JsonSerializer.Deserialize<JsonElement>(json);
        using var properties = element.EnumerateObject();
        return await TlbxMcpUpdateWorkflow.InvokeAsync(new CallToolRequestParams
        {
            Name = "update_apply", Arguments = properties.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal)
        }, invoke, ct, TimeSpan.Zero).ConfigureAwait(false);
    }
    private static JsonElement Field(CallToolResult result, string name) => result.StructuredContent!.Value.GetProperty(name);
    private static CallToolResult Context(string id, string version) => McpApiResult.Structured(new JsonObject { ["serverInstanceId"] = id, ["serverVersion"] = version });
    private static CallToolResult Status() => Reply("""{"statusCode":200,"data":{"available":true,"latestVersion":"2"}}""");
    private static CallToolResult Reply(string json) => McpApiResult.Structured(JsonNode.Parse(json)!.AsObject());
}

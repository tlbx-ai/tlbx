using System.Net;
using Ai.Tlbx.MidTerm.Common.Protocol;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ai.Tlbx.MidTerm.Services;
using Ai.Tlbx.MidTerm.Services.Mcp;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Client;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TlbxMcpApiProtocolTests
{
    private static Dictionary<string, JsonElement> Args(string json) => JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
    private static McpApiOperation Operation(string name) => TlbxMcpApiTools.Operations().Single(o => o.Name == name);

    [Fact]
    public void AgentDecisionRequiresExactRequestTurnAndExplicitDecision()
    {
        var operation = Operation("agent_request_decide");
        var args = Args("""{"requestId":"request / Ω","expectedTurnId":"turn / Ω","decision":"decline","targetSessionId":"other"}""");
        var bound = operation.Bind(args, "self");
        Assert.Equal("other", bound.Routes["id"]);
        Assert.Equal("request / Ω", bound.Routes["requestId"]);
        Assert.Equal("decline", Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(bound.Query.Value!)["decision"].ToString());
        Assert.Contains("request%20%2F%20%CE%A9", bound.Path, StringComparison.Ordinal);
        foreach (var name in new[] { "requestId", "expectedTurnId", "decision" })
        {
            var missing = new Dictionary<string, JsonElement>(args);
            missing.Remove(name);
            Assert.Throws<McpApiArgumentException>(() => operation.Bind(missing, "self"));
        }
        var unbound = new Dictionary<string, JsonElement>(args);
        unbound.Remove("targetSessionId");
        Assert.Throws<McpApiArgumentException>(() => operation.Bind(unbound, null));
    }

    [Theory]
    [InlineData("request", "turn", "turn", "open", true)]
    [InlineData("missing", "turn", "turn", "open", false)]
    [InlineData("request", "old-turn", "turn", "open", false)]
    [InlineData("request", "turn", "old-turn", "open", false)]
    [InlineData("request", "turn", "turn", "resolved", false)]
    [InlineData("request", "turn", "turn", "OPEN", false)]
    [InlineData("request", "", "", "open", false)]
    [InlineData("", "turn", "turn", "open", false)]
    public void AgentDecisionRejectsUnavailableStaleAndSettledRequests(
        string requestId, string expectedTurnId, string requestTurnId, string state, bool expected)
    {
        var history = new AppServerControlHistoryWindowResponse
        {
            CurrentTurn = new() { TurnId = "turn" },
            Requests = [new() { RequestId = "request", TurnId = requestTurnId, State = state }]
        };
        Assert.Equal(expected, SessionApiEndpoints.IsOpenAgentRequestInTurn(history, requestId, expectedTurnId));
        Assert.False(SessionApiEndpoints.IsOpenAgentRequestInTurn(null, requestId, expectedTurnId));
    }

    [Fact]
    public void SessionDefaultsAreExplicitAndUnboundOperatorCannotInventSelf()
    {
        Assert.Equal("self", Operation("terminal_prompt").Bind(Args("{\"text\":\"hello\"}"), "self").Routes["id"]);
        Assert.Equal("other", Operation("terminal_prompt").Bind(Args("{\"text\":\"hello\",\"targetSessionId\":\"other\"}"), "self").Routes["id"]);
        Assert.Throws<McpApiArgumentException>(() => Operation("terminal_prompt").Bind(Args("{\"text\":\"hello\"}"), null));
        Assert.Equal("self", JsonNode.Parse(Operation("notification_send").Bind(Args("{\"body\":\"hello\"}"), "self").Body!)!["sessionId"]!.GetValue<string>());
        Assert.Equal("tlbx", JsonNode.Parse(Operation("notification_send").Bind(Args("{\"body\":\"hello\"}"), "self").Body!)!["title"]!.GetValue<string>());
        Assert.Equal("custom", JsonNode.Parse(Operation("notification_send").Bind(Args("{\"body\":\"hello\",\"title\":\"custom\"}"), "self").Body!)!["title"]!.GetValue<string>());
        Assert.Null(JsonNode.Parse(Operation("work_add").Bind(Args("{\"title\":\"task\"}"), null).Body!)!["sessionId"]);
        Assert.Equal("self", JsonNode.Parse(Operation("work_add").Bind(Args("{\"title\":\"task\"}"), "self").Body!)!["sessionId"]!.GetValue<string>());
    }

    [Fact]
    public void OmittedBodyFieldsRemainOmittedAndFixedPromptModeCannotBeOverridden()
    {
        var operation = Operation("board_update");
        var omitted = operation.Bind(Args("{\"boardId\":\"board\"}"), "self");
        var cleared = operation.Bind(Args("{\"boardId\":\"board\",\"title\":null}"), "self");
        Assert.False(JsonNode.Parse(omitted.Body!)!.AsObject().ContainsKey("title"));
        Assert.True(JsonNode.Parse(cleared.Body!)!.AsObject().ContainsKey("title"));
        Assert.Throws<McpApiArgumentException>(() => Operation("terminal_prompt_now").Bind(Args("{\"text\":\"hello\",\"interruptFirst\":false}"), "self"));
        Assert.Throws<McpApiArgumentException>(() => operation.Bind(Args("{\"arbitraryRoute\":\"/admin\"}"), "self"));
    }

    [Fact]
    public void HubAndSchedulingArgumentsUseExistingRoutesAndNativeMilliseconds()
    {
        var remote = Operation("control_events").Bind(Args("{\"machineId\":\"host / ü\",\"after\":123,\"limit\":7}"), null);
        Assert.Equal("/api/hub/machines/{id}/control-plane/events", remote.Pattern);
        Assert.Equal("host / ü", remote.Routes["id"]);
        Assert.Contains("host%20%2F%20%C3%BC", remote.Path, StringComparison.Ordinal);
        Assert.Equal("?after=123&limit=7", remote.Query.Value);
        var scheduled = JsonNode.Parse(Operation("queue_schedule").Bind(Args("{\"delay\":\"2h\",\"interval\":\"1.5s\"}"), "self").Body!)!;
        Assert.Equal(7200000, scheduled["delayMs"]!.GetValue<int>());
        Assert.Equal(1500, scheduled["repeatEveryMs"]!.GetValue<int>());
        Assert.Throws<McpApiArgumentException>(() => Operation("queue_schedule").Bind(Args("{\"delay\":\"999999999d\"}"), "self"));
        Assert.Throws<McpApiArgumentException>(() => Operation("queue_schedule").Bind(Args("{\"interval\":\"0.5s\"}"), "self"));
    }

    [Fact]
    public async Task RealMcpInvokesRegisteredHandlerOnceAndPreservesLiteralPayloadConflictAndBinaryBytes()
    {
        var ct = TestContext.Current.CancellationToken;
        using var leases = new SessionCliContextService(() => [], _ => (1, new DateTime(2026, 10, 30)), TimeProvider.System);
        var token = leases.CreateHostLease("self", 100).Split(':')[1];
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        TlbxMcpEndpoints.AddServices(builder.Services);
        builder.Services.AddSingleton(leases);
        await using var app = builder.Build();
        app.Use(async (context, next) => { RequestAccessContext.SetFullUser(context, true); await next(context); });
        TlbxMcpEndpoints.Map(app);
        var calls = 0;
        string? seen = null;
        app.MapPost("/api/sessions/{id}/input/prompt", async (HttpContext context) =>
        {
            Interlocked.Increment(ref calls);
            Assert.True(RequestAccessContext.HasFullUserAccess(context));
            Assert.Equal("self", context.Request.RouteValues["id"]);
            using var body = await JsonDocument.ParseAsync(context.Request.Body, cancellationToken: context.RequestAborted);
            seen = body.RootElement.GetProperty("text").GetString();
            context.Response.StatusCode = StatusCodes.Status409Conflict;
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{\"error\":\"busy\",\"revision\":42}", context.RequestAborted);
        });
        byte[] bytes = [0, 255, 27, 91, 0, 128, 195, 188];
        app.MapGet("/api/sessions/{id}/buffer", async (HttpContext context) =>
        {
            context.Response.ContentType = "application/octet-stream";
            await context.Response.Body.WriteAsync(bytes, context.RequestAborted);
        });
        await app.StartAsync(ct);
        try
        {
            var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
            await using var transport = new HttpClientTransport(new()
            {
                Endpoint = new Uri(address + TlbxMcpEndpoints.Path),
                AdditionalHeaders = new Dictionary<string, string>(StringComparer.Ordinal) { [SessionCliContextService.LeaseHeader] = token }
            });
            await using var client = await McpClient.CreateAsync(transport, cancellationToken: ct);
            var literal = new string('ü', 70000) + "\0\r\n'\"`$()\\路径";
            var result = await client.CallToolAsync("terminal_prompt", new Dictionary<string, object?>(StringComparer.Ordinal) { ["text"] = literal }, cancellationToken: ct);
            Assert.True(result.IsError);
            Assert.Equal(409, result.StructuredContent!.Value.GetProperty("statusCode").GetInt32());
            Assert.Equal(42, result.StructuredContent.Value.GetProperty("data").GetProperty("revision").GetInt32());
            Assert.Equal(literal, seen);
            Assert.Equal(1, calls);
            var buffer = await client.CallToolAsync("terminal_buffer", cancellationToken: ct);
            Assert.Equal(bytes, Convert.FromBase64String(buffer.StructuredContent!.Value.GetProperty("data").GetProperty("base64").GetString()!));
            var localWait = await client.CallToolAsync("process_wait", new Dictionary<string, object?>(StringComparer.Ordinal)
                { ["statePath"] = "must-not-read-service-files", ["timeoutSeconds"] = 0 }, cancellationToken: ct);
            Assert.True(localWait.IsError);
            Assert.Equal("TLBX_MCP_CLIENT_LOCAL_REQUIRED", localWait.StructuredContent!.Value.GetProperty("errorCode").GetString());
        }
        finally { await app.StopAsync(ct); }
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.WebUtilities;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed partial class TlbxCliScriptWriterTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedCliOptionsAndMcpReachTheSameEndpointWithEquivalentTypedPayloads(bool bash)
    {
        if ((bash ? ResolveBashPath() : ResolvePowerShellPath()) is null) return;
        Directory.CreateDirectory(_tempDir);
        TlbxCliScriptWriter.WriteScripts(_tempDir, 2100, "test-token");
        var requests = new ConcurrentQueue<(string Method, string Path, string Query, string Body)>();
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        await using var app = builder.Build();
        app.Run(async context =>
        {
            if (context.Request.Path == "/api/sessions/process-context")
            {
                await context.Response.WriteAsync("abcdefgh:0123456789abcdef0123456789abcdef", context.RequestAborted);
                return;
            }
            using var reader = new StreamReader(context.Request.Body);
            requests.Enqueue((context.Request.Method, context.Request.Path.ToUriComponent(), context.Request.QueryString.Value ?? "",
                await reader.ReadToEndAsync(context.RequestAborted)));
            context.Response.ContentType = "application/json";
            await context.Response.WriteAsync("{}", context.RequestAborted);
        });
        var cases = RequestParityCases();
        await app.StartAsync(TestContext.Current.CancellationToken);
        var command = bash
            ? "set -e; export CODEX_SESSION_ID=11111111-1111-4111-8111-111111111111 MT_ACP_MODEL=fixture-model MT_ACP_EFFORT=high MT_ACP_PLAN_MODE=plan MT_ACP_PERMISSION_MODE=manual; source \"$1\";\n"
            : "$env:CODEX_SESSION_ID='11111111-1111-4111-8111-111111111111'; $env:MT_ACP_MODEL='fixture-model'; $env:MT_ACP_EFFORT='high'; $env:MT_ACP_PLAN_MODE='plan'; $env:MT_ACP_PERMISSION_MODE='manual'; . $args[0]\n";
        command += string.Join("\n", cases.Select(item => bash ? item.Bash : item.PowerShell));
        var result = await RunContextShellAsync(bash, command, app.Urls.Single(), "deadbeef", TimeSpan.FromSeconds(60),
            Path.Combine(_tempDir, bash ? "tlbx_cli.sh" : "tlbx_cli.ps1"));
        Assert.True(result.ExitCode == 0, result.Error + result.Output);
        Assert.Equal(cases.Length, requests.Count);
        var operations = TlbxMcpApiTools.Operations().ToDictionary(operation => operation.Name, StringComparer.Ordinal);
        Assert.Multiple(cases.Select(item => (Action)(() =>
        {
            Assert.True(requests.TryDequeue(out var actual));
            var operation = operations[item.Tool];
            var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.Arguments)!;
            var mcp = operation.Bind(arguments, "abcdefgh");
            Assert.True(mcp.Method == actual.Method && mcp.Path == actual.Path,
                $"{item.Tool}: CLI {actual.Method} {actual.Path}; MCP {mcp.Method} {mcp.Path}");
            static string[] Query(string query) => QueryHelpers.ParseQuery(query)
                .Select(pair => pair.Key + "=" + (pair.Key is "agentOnly" or "stripAnsi"
                    ? pair.Value.ToString().ToLowerInvariant() : pair.Value.ToString())).Order(StringComparer.Ordinal).ToArray();
            Assert.Equal(Query(actual.Query), Query(mcp.Query.Value ?? ""));
            if (operation.BodyType is { } type)
            {
                // Normalize through the real endpoint DTO, including its defaults.
                // Attribution intentionally differs: CLI publishes its CLI source;
                // MCP uses the shared handler's API attribution unless supplied.
                JsonObject Normalize(string json)
                {
                    var dto = JsonSerializer.Deserialize(json, type)!;
                    var node = JsonSerializer.SerializeToNode(dto, type)!.AsObject();
                    node.Remove("source");
                    // SetSessionTopic treats null and empty as the same clear;
                    // the generated PowerShell/Bash helpers use different forms.
                    if (item.Tool == "session_set_topic" && node["topic"]?.GetValue<string>() == "") node["topic"] = null;
                    return node;
                }
                var cliBody = Normalize(actual.Body);
                var mcpBody = Normalize(mcp.Body!);
                Assert.True(JsonNode.DeepEquals(cliBody, mcpBody),
                    $"{item.Tool}: CLI {cliBody}; MCP {mcpBody}");
            }
        })).ToArray());
    }

    private static (string Tool, string Arguments, string PowerShell, string Bash)[] RequestParityCases() =>
    [
        ("session_create", """{"shell":"pwsh","workingDirectory":"/repo mit 空"}""", "Mt-NewSession -Shell pwsh -Cwd '/repo mit 空'", "mt_new_session pwsh '/repo mit 空'"),
        ("session_set_topic", """{"topic":"Grüße 東京"}""", "Mt-Topic 'Grüße 東京'", "mt_topic 'Grüße 東京'"),
        ("session_set_topic", """{"topic":""}""", "Mt-Topic -Clear", "mt_topic --clear"),
        ("terminal_tail", """{"targetSessionId":"1234abcd","lines":7}""", "Mt-Tail 1234abcd 7", "mt_tail 1234abcd 7"),
        ("terminal_send_text", """{"targetSessionId":"1234abcd","text":"Grüße 東京"}""", "Mt-SendText 1234abcd 'Grüße 東京'", "mt_sendtext 1234abcd 'Grüße 東京'"),
        ("terminal_paste", """{"targetSessionId":"1234abcd","text":"/file mit 空.txt","bracketedPaste":true,"isFilePath":true}""", "Mt-Paste -Bracketed -File 1234abcd '/file mit 空.txt'", "mt_paste --bracketed --file 1234abcd '/file mit 空.txt'"),
        ("session_activity", """{"targetSessionId":"1234abcd","seconds":17,"bellLimit":3}""", "Mt-Activity 1234abcd 17 3", "mt_activity 1234abcd 17 3"),
        ("sessions_attention", """{"agentOnly":false}""", "Mt-Attention -AgentOnly $false", "mt_attention false"),
        ("notification_send", """{"targetSessionId":"1234abcd","body":"Grüße 東京","title":"Hinweis","priority":"important"}""", "Mt-Notify 'Grüße 東京' -Title Hinweis -Priority important -SessionId 1234abcd", "mt_notify --title Hinweis --priority important --session 1234abcd 'Grüße 東京'"),
        ("control_events", """{"machineId":"machine 空","after":123,"limit":7}""", "Mt-Events -After 123 -Limit 7 -MachineId 'machine 空'", "mt_events 123 7 'machine 空'"),
        ("work_list", """{"state":"blocked","kind":"task","targetSessionId":"1234abcd","limit":7}""", "Mt-WorkList -State blocked -Kind task -SessionId 1234abcd -Limit 7", "mt_work_list blocked task 1234abcd 7"),
        ("work_update", """{"workId":"work-1","state":"blocked","summary":"","nextAction":"next","url":"","repositoryPath":"/repo mit 空"}""", "Mt-WorkUpdate -Id work-1 -State blocked -Summary '' -NextAction next -Url '' -RepositoryPath '/repo mit 空'", "mt_work_update work-1 blocked '' next '' '/repo mit 空'"),
        ("checkpoints_list", """{"targetSessionId":"1234abcd","kind":"note","limit":7}""", "Mt-Checkpoints -SessionId 1234abcd -Kind note -Limit 7", "mt_checkpoints 1234abcd note 7"),
        ("input_history_list", """{"targetSessionId":"1234abcd","kind":"paste","limit":7}""", "Mt-InputHistory -SessionId 1234abcd -Kind paste -Limit 7", "mt_input_history 1234abcd paste 7"),
        ("input_history_replay", """{"entryId":"entry-1","targetSessionId":"1234abcd"}""", "Mt-InputHistoryReplay -Id entry-1 -TargetSessionId 1234abcd", "mt_input_history_replay entry-1 1234abcd"),
        ("input_history_clear", """{"targetSessionId":"1234abcd"}""", "Mt-InputHistoryClear -SessionId 1234abcd", "mt_input_history_clear 1234abcd"),
        ("agent_history", """{"targetSessionId":"1234abcd","startIndex":3,"count":7,"viewportWidth":390}""", "Mt-AcpHistory -SessionId 1234abcd -StartIndex 3 -Count 7 -ViewportWidth 390", "mt_acp_history 1234abcd 3 7 390"),
        ("agent_turn", """{"targetSessionId":"1234abcd","text":"Grüße 東京","model":"fixture-model","effort":"high","planMode":"plan","permissionMode":"manual"}""", "Mt-AcpTurn 'Grüße 東京' -SessionId 1234abcd", "mt_acp_turn 1234abcd 'Grüße 東京'"),
        ("agent_interrupt", """{"targetSessionId":"1234abcd","turnId":"turn-1"}""", "Mt-AcpInterrupt -SessionId 1234abcd -TurnId turn-1", "mt_acp_interrupt 1234abcd turn-1"),
        ("agent_steer", """{"targetSessionId":"1234abcd","expectedTurnId":"turn-1","text":"Grüße 東京"}""", "Mt-AcpSteer -SessionId 1234abcd -ExpectedTurnId turn-1 -Text 'Grüße 東京'", "mt_acp_steer 1234abcd turn-1 'Grüße 東京'"),
        ("repo_bind", """{"targetSessionId":"1234abcd","path":"/repo mit 空","role":"target","label":"Grüße 東京"}""", "Mt-Repo -SessionId 1234abcd add '/repo mit 空' target 'Grüße 東京'", "mt_repo --session 1234abcd add '/repo mit 空' target 'Grüße 東京'"),
        ("repo_refresh", """{"targetSessionId":"1234abcd","repoRoot":"/repo mit 空"}""", "Mt-Repo -SessionId 1234abcd refresh '/repo mit 空'", "mt_repo --session 1234abcd refresh '/repo mit 空'"),
        ("repo_remove", """{"targetSessionId":"1234abcd","repoRoot":"/repo mit 空"}""", "Mt-Repo -SessionId 1234abcd rm '/repo mit 空'", "mt_repo --session 1234abcd rm '/repo mit 空'")
    ];
}

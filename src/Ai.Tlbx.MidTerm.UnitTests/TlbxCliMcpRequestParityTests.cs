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
        command += bash
            ? "export MT_AI_PROFILE=codex MT_PROMPT_DELAY_MS=23 MT_PROMPT_INTERRUPT_DELAY_MS=17 MT_PROMPT_INTERRUPT_FIRST=true MT_BOOTSTRAP_LAUNCH_DELAY_MS=19 MT_BOOTSTRAP_SLASH_DELAY_MS=29\n"
            : "$env:MT_AI_PROFILE='codex'; $env:MT_PROMPT_INTERRUPT_DELAY_MS='17'; $env:MT_PROMPT_INTERRUPT_FIRST='true'; $env:MT_BOOTSTRAP_LAUNCH_DELAY_MS='19'; $env:MT_BOOTSTRAP_SLASH_DELAY_MS='29'\n";
        command += string.Join("\n", cases.Select(item => bash ? item.Bash : item.PowerShell));
        var result = await RunContextShellAsync(bash, command, app.Urls.Single(), "deadbeef", TimeSpan.FromSeconds(120),
            Path.Combine(_tempDir, bash ? "tlbx_cli.sh" : "tlbx_cli.ps1"));
        Assert.True(result.ExitCode == 0, result.Error + result.Output);
        Assert.Equal(cases.Length, requests.Count);
        var operations = TlbxMcpApiTools.Operations().ToDictionary(operation => operation.Name, StringComparer.Ordinal);
        // Update apply has dedicated real HTTP restart/recovery checks. Every
        // other CLI counterpart backed by a fixed API route must appear here.
        var fixedCliTools = TlbxMcpCliParity.Commands.SelectMany(requirement => requirement.McpTools)
            .Concat(TlbxMcpCliParity.BoardCommands.Select(requirement => requirement.McpTool))
            .Concat(TlbxMcpCliParity.RepoCommands.Select(requirement => requirement.McpTool))
            .Where(tool => tool != "update_apply" && operations.ContainsKey(tool)).Distinct(StringComparer.Ordinal);
        Assert.All(fixedCliTools, tool => Assert.Contains(cases, item => item.Tool == tool));
        Assert.Multiple(cases.Select(item => (Action)(() =>
        {
            Assert.True(requests.TryDequeue(out var actual));
            var operation = operations[item.Tool];
            var arguments = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(item.Arguments)!;
            var mcp = operation.Bind(arguments, "abcdefgh");
            Assert.True(mcp.Method == actual.Method && mcp.Path == actual.Path,
                $"{item.Tool}: CLI {actual.Method} {actual.Path}; MCP {mcp.Method} {mcp.Path}");
            string[] Query(string query)
            {
                var values = QueryHelpers.ParseQuery(query);
                if (item.Tool == "control_plane_get")
                {
                    // Both ControlPlaneEndpoints and HubEndpoints declare these
                    // defaults; CLI omits them while MCP sends them explicitly.
                    values.TryAdd("workLimit", "100");
                    values.TryAdd("checkpointLimit", "30");
                }
                return values.Select(pair => pair.Key + "=" + (pair.Key is "agentOnly" or "stripAnsi"
                    ? pair.Value.ToString().ToLowerInvariant() : pair.Value.ToString())).Order(StringComparer.Ordinal).ToArray();
            }
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
        ("repo_remove", """{"targetSessionId":"1234abcd","repoRoot":"/repo mit 空"}""", "Mt-Repo -SessionId 1234abcd rm '/repo mit 空'", "mt_repo --session 1234abcd rm '/repo mit 空'"),
        ("repos_list", """{"targetSessionId":"1234abcd"}""", "Mt-Repo -SessionId 1234abcd status", "mt_repo --session 1234abcd status"),
        ("terminal_buffer", """{"targetSessionId":"1234abcd"}""", "Mt-Buffer 1234abcd", "mt_buffer 1234abcd"),
        ("terminal_redraw", """{"targetSessionId":"1234abcd"}""", "Mt-Redraw 1234abcd", "mt_redraw 1234abcd"),
        ("terminal_send_keys", """{"targetSessionId":"1234abcd","keys":["C-a","Escape","Enter"]}""", "Mt-SendKeys 1234abcd C-a Escape Enter", "mt_sendkeys 1234abcd C-a Escape Enter"),
        ("terminal_prompt", """{"targetSessionId":"1234abcd","text":"Grüße 東京","profile":"codex","interruptFirst":true,"interruptDelayMs":17,"submitDelayMs":23}""", "Mt-Prompt 1234abcd 'Grüße 東京' -DelayMs 23", "mt_prompt 1234abcd 'Grüße 東京'"),
        ("terminal_prompt_now", """{"targetSessionId":"1234abcd","text":"Grüße 東京","profile":"codex","interruptDelayMs":17,"submitDelayMs":23}""", "Mt-PromptNow 1234abcd 'Grüße 東京' -DelayMs 23", "mt_prompt_now 1234abcd 'Grüße 東京'"),
        ("terminal_slash", """{"targetSessionId":"1234abcd","text":"compact Grüße","profile":"codex","interruptFirst":true,"interruptDelayMs":17,"submitDelayMs":23}""", "Mt-Slash 1234abcd 'compact Grüße' -DelayMs 23", "mt_slash 1234abcd 'compact Grüße'"),
        ("queue_schedule", """{"targetSessionId":"1234abcd","delay":"2h","turn":{"text":"Grüße 東京"}}""", "Mt-Wake 1234abcd 2h 'Grüße 東京'", "mt_wake 1234abcd 2h 'Grüße 東京'"),
        ("queue_schedule", """{"targetSessionId":"1234abcd","interval":"3s","turn":{"text":"Grüße 東京"}}""", "Mt-Recur 1234abcd 3s 'Grüße 東京'", "mt_recur 1234abcd 3s 'Grüße 東京'"),
        ("queue_list", """{"targetSessionId":"1234abcd"}""", "Mt-Queue 1234abcd", "mt_queue 1234abcd"),
        ("queue_cancel", """{"queueId":"queue-1"}""", "Mt-WakeCancel queue-1", "mt_wake_cancel queue-1"),
        ("session_inject_guidance", """{"targetSessionId":"1234abcd"}""", "Mt-Inject 1234abcd", "mt_inject 1234abcd"),
        ("control_plane_get", """{"machineId":"machine 空"}""", "Mt-ControlPlane -MachineId 'machine 空'", "mt_control_plane 'machine 空'"),
        ("agent_capabilities", """{"machineId":"machine 空"}""", "Mt-AgentCapabilities -MachineId 'machine 空'", "mt_agent_capabilities 'machine 空'"),
        ("agent_dispatch", """{"sessionIds":["1234abcd","5678abcd"],"turn":{"text":"Grüße 東京"}}""", "Mt-Dispatch -SessionId 1234abcd,5678abcd -Text 'Grüße 東京'", "mt_dispatch 1234abcd,5678abcd 'Grüße 東京'"),
        ("work_add", """{"targetSessionId":"1234abcd","kind":"task","title":"Grüße 東京","summary":"summary","nextAction":"next","priority":"high","project":"project","dedupeKey":"dedupe","url":"https://example.invalid/path","repositoryPath":"/repo mit 空"}""", "Mt-WorkAdd -Kind task -Title 'Grüße 東京' -Summary summary -NextAction next -Priority high -Project project -DedupeKey dedupe -SessionId 1234abcd -Url https://example.invalid/path -RepositoryPath '/repo mit 空'", "mt_work_add task 'Grüße 東京' summary next high project dedupe 1234abcd https://example.invalid/path '/repo mit 空'"),
        ("work_delete", """{"workId":"work-1"}""", "Mt-WorkDelete -Id work-1", "mt_work_delete work-1"),
        ("status_publish", """{"targetSessionId":"1234abcd","state":"working","summary":"Grüße 東京","currentTask":"task","nextAction":"next","project":"project","repositoryPath":"/repo mit 空"}""", "Mt-PublishStatus -SessionId 1234abcd -State working -Summary 'Grüße 東京' -CurrentTask task -NextAction next -Project project -RepositoryPath '/repo mit 空'", "mt_publish_status working 'Grüße 東京' task next project 1234abcd '/repo mit 空'"),
        ("status_list", """{"targetSessionId":"1234abcd"}""", "Mt-StatusList -SessionId 1234abcd", "mt_status_list 1234abcd"),
        ("status_clear", """{"targetSessionId":"1234abcd"}""", "Mt-StatusClear -SessionId 1234abcd", "mt_status_clear 1234abcd"),
        ("checkpoint_add", """{"targetSessionId":"1234abcd","kind":"note","summary":"Grüße 東京","details":"details","project":"project","repositoryPath":"/repo mit 空"}""", "Mt-Checkpoint -SessionId 1234abcd -Kind note -Summary 'Grüße 東京' -Details details -Project project -RepositoryPath '/repo mit 空'", "mt_checkpoint note 'Grüße 東京' details project 1234abcd '/repo mit 空'"),
        ("input_history_get", """{"entryId":"entry-1"}""", "Mt-InputHistoryShow -Id entry-1", "mt_input_history_show entry-1"),
        ("input_history_delete", """{"entryId":"entry-1"}""", "Mt-InputHistoryDelete -Id entry-1", "mt_input_history_delete entry-1"),
        ("session_bootstrap", """{"name":"Grüße 東京","workingDirectory":"/repo mit 空","profile":"codex","launchDelayMs":19,"slashCommandDelayMs":29,"slashCommands":["/first","/second"]}""", "Mt-Bootstrap -Name 'Grüße 東京' -Cwd '/repo mit 空' -Profile codex -SlashCommands /first,/second", "mt_bootstrap 'Grüße 東京' '/repo mit 空' codex /first /second"),
        ("agent_session_create", """{"name":"Grüße 東京","workingDirectory":"/repo mit 空","profile":"codex"}""", "Mt-AcpNew -Name 'Grüße 東京' -Cwd '/repo mit 空' -Profile codex", "mt_acp_new 'Grüße 東京' '/repo mit 空' codex"),
        ("agent_compact", """{"targetSessionId":"1234abcd"}""", "Mt-AcpCompact -SessionId 1234abcd", "mt_acp_compact 1234abcd"),
        .. new[] { ("Enter", "enter"), ("C-c", "ctrlc"), ("Escape", "escape"), ("Up", "up"), ("Down", "down"), ("Left", "left"), ("Right", "right") }
            .Select(key => ("terminal_send_keys", "{\"targetSessionId\":\"1234abcd\",\"keys\":[\"" + key.Item1 + "\"]}", "mt_" + key.Item2 + " 1234abcd", "mt_" + key.Item2 + " 1234abcd")),
        .. BoardRequestParityCases()
    ];

    private static (string Tool, string Arguments, string PowerShell, string Bash)[] BoardRequestParityCases()
    {
        const string board = "11111111111111111111111111111111";
        const string item = "22222222222222222222222222222222";
        (string, string, string, string) Case(string tool, string action, string? body = null, bool itemScope = false, bool revision = false)
        {
            var arguments = body is null ? new JsonObject() : JsonNode.Parse(body)!.AsObject();
            var command = "mt_board " + action;
            if (action is not ("list" or "create")) { arguments["boardId"] = board; command += " " + board; }
            if (itemScope) { arguments["itemId"] = item; command += " " + item; }
            if (revision) { arguments["expectedRevision"] = 7; command += " 7"; }
            if (body is not null) command += " '" + body + "'";
            return (tool, arguments.ToJsonString(), command, command);
        }
        return
        [
            Case("boards_list", "list"),
            Case("board_get", "get"),
            Case("board_item_get", "item", itemScope: true),
            Case("board_create", "create", """{"title":"Grüße 東京","workingDirectory":"/repo mit 空","sessionProfile":"codex"}"""),
            Case("board_update", "update", """{"expectedRevision":7,"title":"Grüße 東京","launchCommand":"echo Grüße"}"""),
            Case("board_set_viewport", "viewport", """{"panX":1.25,"panY":-2.5,"zoom":0.75}"""),
            Case("board_delete", "delete", revision: true),
            Case("board_item_add", "add", """{"expectedRevision":7,"item":{"kind":"note","title":"Grüße 東京","text":"note","x":1.25,"y":-2.5}}"""),
            Case("board_item_write", "write", """{"expectedRevision":7,"item":{"kind":"note","title":"Grüße 東京","text":"note","x":1.25,"y":-2.5}}""", itemScope: true),
            Case("board_item_remove", "remove", itemScope: true, revision: true),
            Case("board_browser_command", "browser", """{"command":"fill","selector":"input","value":"Grüße 東京","previewName":"named"}""", itemScope: true),
            Case("board_launch", "launch", """{"requestId":"request-1","expectedRevision":7,"entryId":"entry-1","workingDirectory":"/repo mit 空","launchCommand":"echo Grüße","profile":"codex","appServerControlOnly":false}""", itemScope: true),
            Case("board_launches", "launches")
        ];
    }
}

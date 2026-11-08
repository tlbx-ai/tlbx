using System.Globalization;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Ai.Tlbx.MidTerm.UnitTests;

internal sealed class FakeCodexWebSocketServer : IAsyncDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Task _acceptLoopTask;
    private readonly List<Task> _clientTasks = [];

    private FakeCodexWebSocketServer(
        string endpoint,
        string loadedThreadId,
        string assistantReply,
        bool emitRichHistoryItems,
        bool emitTurnIds,
        bool emitLateDiffAfterCompletion,
        bool emitMcpToolProgress,
        bool emitUnknownAgentNotification,
        bool emitBackgroundTerminalWaitNotification,
        bool emitMcpStartupStatus,
        bool emitProtocolV2Surface,
        int turnCompletionDelayMs)
    {
        Endpoint = endpoint;
        LoadedThreadId = loadedThreadId;
        AssistantReply = assistantReply;
        EmitRichHistoryItems = emitRichHistoryItems;
        EmitTurnIds = emitTurnIds;
        EmitLateDiffAfterCompletion = emitLateDiffAfterCompletion;
        EmitMcpToolProgress = emitMcpToolProgress;
        EmitUnknownAgentNotification = emitUnknownAgentNotification;
        EmitBackgroundTerminalWaitNotification = emitBackgroundTerminalWaitNotification;
        EmitMcpStartupStatus = emitMcpStartupStatus;
        EmitProtocolV2Surface = emitProtocolV2Surface;
        TurnCompletionDelayMs = turnCompletionDelayMs;
        _listener.Prefixes.Add(ToHttpPrefix(endpoint));
        _listener.Start();
        _acceptLoopTask = Task.Run(AcceptLoopAsync, _shutdown.Token);
    }

    public string Endpoint { get; }

    public string LoadedThreadId { get; }

    public string AssistantReply { get; }

    public bool EmitRichHistoryItems { get; }

    public bool EmitTurnIds { get; }

    public bool EmitLateDiffAfterCompletion { get; }

    public bool EmitMcpToolProgress { get; }

    public bool EmitUnknownAgentNotification { get; }

    public bool EmitBackgroundTerminalWaitNotification { get; }

    public bool EmitMcpStartupStatus { get; }

    public bool EmitProtocolV2Surface { get; }

    public int TurnCompletionDelayMs { get; }

    public bool? LastThreadResumeExcludeTurns { get; private set; }

    public string? LastTurnServiceTier { get; private set; }

    public string? LastTurnCollaborationMode { get; private set; }

    public string? LastTurnCollaborationModel { get; private set; }

    public string? LastTurnCollaborationEffort { get; private set; }

    public string? LastTurnText { get; private set; }

    public string? ThreadHistoryJson { get; set; }

    public string? BeforeHistoryNotificationJson { get; set; }

    public string? AfterHistoryNotificationJson { get; set; }

    public int ThreadResumeCount { get; private set; }

    public ConcurrentQueue<JsonElement> ServerRequestResponses { get; } = new();
    public string? ThreadSettingsUpdateJson { get; set; }
    public bool RejectThreadSettingsUpdate { get; set; }
    public Task? ThreadSettingsUpdateGate { get; set; }
    public TaskCompletionSource ThreadSettingsUpdateReceived { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public bool IgnoreGoalReadback { get; set; }

    public int GoalReadCount { get; private set; }

    public bool EmitUsageNotifications { get; set; }

    public bool PaginatedHistory { get; set; }

    public int HistoryPageCount { get; private set; }

    public static FakeCodexWebSocketServer Start(
        string loadedThreadId,
        string assistantReply,
        bool emitRichHistoryItems = false,
        bool emitTurnIds = false,
        bool emitLateDiffAfterCompletion = false,
        bool emitMcpToolProgress = false,
        bool emitUnknownAgentNotification = false,
        bool emitBackgroundTerminalWaitNotification = false,
        bool emitMcpStartupStatus = false,
        bool emitProtocolV2Surface = false,
        int turnCompletionDelayMs = 0)
    {
        var endpoint = string.Create(CultureInfo.InvariantCulture, $"ws://127.0.0.1:{GetFreePort()}/");
        return new FakeCodexWebSocketServer(
            endpoint,
            loadedThreadId,
            assistantReply,
            emitRichHistoryItems,
            emitTurnIds,
            emitLateDiffAfterCompletion,
            emitMcpToolProgress,
            emitUnknownAgentNotification,
            emitBackgroundTerminalWaitNotification,
            emitMcpStartupStatus,
            emitProtocolV2Surface,
            turnCompletionDelayMs);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        _listener.Stop();
        _listener.Close();
        try
        {
            await _acceptLoopTask.ConfigureAwait(false);
        }
        catch
        {
        }

        await Task.WhenAll(_clientTasks.ToArray()).ConfigureAwait(false);
        _shutdown.Dispose();
    }

    private async Task SendProtocolV2SurfaceNotificationsAsync(WebSocket socket, string? turnId = null)
    {
        if (!EmitProtocolV2Surface)
        {
            return;
        }

        await SendJsonAsync(socket, new
        {
            method = "remoteControl/status/changed",
            @params = new
            {
                status = "connected",
                environmentId = "fake-codex-env-1"
            }
        }, _shutdown.Token).ConfigureAwait(false);
        await SendJsonAsync(socket, new
        {
            method = "warning",
            @params = new
            {
                turnId,
                message = "Fake Codex protocol warning."
            }
        }, _shutdown.Token).ConfigureAwait(false);
        await SendJsonAsync(socket, new
        {
            method = "app/list/updated",
            @params = new
            {
                apps = Array.Empty<object>()
            }
        }, _shutdown.Token).ConfigureAwait(false);
    }

    private async Task AcceptLoopAsync()
    {
        try
        {
            while (!_shutdown.IsCancellationRequested)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                if (context.Request.IsWebSocketRequest)
                {
                    var task = HandleClientAsync(context);
                    lock (_clientTasks)
                    {
                        _clientTasks.Add(task);
                    }

                    _ = task.ContinueWith(_ =>
                    {
                        lock (_clientTasks)
                        {
                            _clientTasks.Remove(task);
                        }
                    }, TaskScheduler.Default);
                    continue;
                }

                context.Response.StatusCode = 200;
                context.Response.Close();
            }
        }
        catch
        {
        }
    }

    private async Task HandleClientAsync(HttpListenerContext context)
    {
        var wsContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
        var socket = wsContext.WebSocket;
        try
        {
            while (!_shutdown.IsCancellationRequested && socket.State == WebSocketState.Open)
            {
                var message = await ReceiveMessageAsync(socket, _shutdown.Token).ConfigureAwait(false);
                if (message is null)
                {
                    break;
                }

                using var json = JsonDocument.Parse(message);
                var root = json.RootElement;
                if (!root.TryGetProperty("method", out var methodElement) || methodElement.ValueKind != JsonValueKind.String)
                {
                    if (root.TryGetProperty("id", out _) && (root.TryGetProperty("result", out _) || root.TryGetProperty("error", out _)))
                        ServerRequestResponses.Enqueue(root.Clone());
                    continue;
                }

                var method = methodElement.GetString() ?? string.Empty;
                var id = root.TryGetProperty("id", out var idElement) ? idElement.ToString() : null;
                var @params = root.TryGetProperty("params", out var paramsElement) ? paramsElement : default;

                switch (method)
                {
                    case "initialize" when id is not null:
                        await SendJsonAsync(socket, new
                        {
                            id,
                            result = new
                            {
                                userAgent = "fake-codex/0.116.0",
                                platformFamily = "windows",
                                platformOs = "windows"
                            }
                        }, _shutdown.Token).ConfigureAwait(false);
                        break;

                    case "initialized":
                        break;

                    case "model/list" when id is not null:
                        await SendJsonAsync(socket, new
                        {
                            id,
                            result = new
                            {
                                data = new[]
                                {
                                    new
                                    {
                                        id = "gpt-5.3-codex",
                                        displayName = "GPT-5.3 Codex",
                                        isDefault = true,
                                        supportedReasoningEfforts = new[]
                                        {
                                            new { reasoningEffort = "low" },
                                            new { reasoningEffort = "medium" },
                                            new { reasoningEffort = "high" }
                                        }
                                    }
                                }
                            }
                        }, _shutdown.Token).ConfigureAwait(false);
                        break;

                    case "thread/loaded/list" when id is not null:
                        await SendJsonAsync(socket, new
                        {
                            id,
                            result = new
                            {
                                data = new[] { LoadedThreadId },
                                nextCursor = (string?)null
                            }
                        }, _shutdown.Token).ConfigureAwait(false);
                        break;

                    case "thread/resume" when id is not null:
                        ThreadResumeCount++;
                        LastThreadResumeExcludeTurns =
                            @params.ValueKind == JsonValueKind.Object &&
                            @params.TryGetProperty("excludeTurns", out var excludeTurnsElement) &&
                            excludeTurnsElement.ValueKind is JsonValueKind.True or JsonValueKind.False
                                ? excludeTurnsElement.GetBoolean()
                                : null;
                        await SendJsonAsync(socket, new
                        {
                            id,
                            result = new
                            {
                                thread = new
                                {
                                    id = LoadedThreadId
                                }
                            }
                        }, _shutdown.Token).ConfigureAwait(false);
                        if (EmitMcpStartupStatus)
                        {
                            await SendJsonAsync(socket, new
                            {
                                method = "mcpServer/startupStatus/updated",
                                @params = new
                                {
                                    name = "codex_apps",
                                    status = "starting",
                                    error = (string?)null
                                }
                            }, _shutdown.Token).ConfigureAwait(false);
                        }
                        await SendProtocolV2SurfaceNotificationsAsync(socket).ConfigureAwait(false);
                        break;

                    case "thread/settings/update" when id is not null:
                        ThreadSettingsUpdateReceived.TrySetResult();
                        if (ThreadSettingsUpdateGate is { } gate) await gate.WaitAsync(_shutdown.Token).ConfigureAwait(false);
                        if (RejectThreadSettingsUpdate)
                        {
                            await SendJsonAsync(socket, new { id, error = new { code = -32602, message = "Fixture settings rejection." } }, _shutdown.Token).ConfigureAwait(false);
                            break;
                        }
                        await SendJsonAsync(socket, new { id, result = new { } }, _shutdown.Token).ConfigureAwait(false);
                        if (ThreadSettingsUpdateJson is { } actual)
                        {
                            using var settings = JsonDocument.Parse(actual);
                            await SendJsonAsync(socket, new { method = "thread/settings/updated", @params = new { threadId = LoadedThreadId, threadSettings = settings.RootElement } }, _shutdown.Token).ConfigureAwait(false);
                        }
                        break;

                    case "thread/goal/get" when id is not null:
                        GoalReadCount++;
                        if (!IgnoreGoalReadback)
                        {
                            await SendJsonAsync(socket, new { id, result = new { goal = (object?)null } },
                                _shutdown.Token).ConfigureAwait(false);
                        }
                        break;

                    case "thread/read" when id is not null:
                    case "thread/turns/list" when id is not null:
                        if (PaginatedHistory && method == "thread/read")
                        {
                            if (@params.GetProperty("includeTurns").GetBoolean())
                            {
                                await SendJsonAsync(socket, new { id, error = new { message = "Use paginated history." } },
                                    _shutdown.Token).ConfigureAwait(false);
                            }
                            else
                            {
                                await SendJsonAsync(socket, new { id, result = new { thread = new { id = LoadedThreadId, historyMode = "paginated" } } },
                                    _shutdown.Token).ConfigureAwait(false);
                            }
                            break;
                        }

                        if (method == "thread/turns/list")
                        {
                            HistoryPageCount++;
                            if (!string.Equals(GetString(@params, "itemsView"), "full", StringComparison.Ordinal) ||
                                !string.Equals(GetString(@params, "sortDirection"), "asc", StringComparison.Ordinal))
                            {
                                await SendJsonAsync(socket, new { id, error = new { message = "Full chronological items required." } },
                                    _shutdown.Token).ConfigureAwait(false);
                                break;
                            }

                            if (GetString(@params, "cursor") is null)
                            {
                                using var firstPage = JsonDocument.Parse("""
                                    {"id":"turn-previous","status":"completed","items":[
                                    {"id":"user-previous","type":"userMessage","content":[{"type":"text","text":"Previous request."}]},
                                    {"id":"assistant-previous","type":"agentMessage","text":"Previous response."}]}
                                    """);
                                await SendJsonAsync(socket, new { id, result = new { data = new[] { firstPage.RootElement }, nextCursor = "page-two" } },
                                    _shutdown.Token).ConfigureAwait(false);
                                break;
                            }
                        }

                        if (EmitUsageNotifications)
                        {
                            using var tokens = JsonDocument.Parse("""
                                {"method":"thread/tokenUsage/updated","params":{"threadId":"thread-history","turnId":"turn-history","tokenUsage":{
                                "last":{"totalTokens":1234},"total":{"totalTokens":90123,"inputTokens":89000,"outputTokens":1123,"cachedInputTokens":80000,"cacheWriteInputTokens":0,"reasoningOutputTokens":234},"modelContextWindow":128000}}}
                                """);
                            await SendJsonAsync(socket, tokens.RootElement, _shutdown.Token).ConfigureAwait(false);
                            using var limits = JsonDocument.Parse("""
                                {"method":"account/rateLimits/updated","params":{"rateLimits":{"primary":{"usedPercent":23},"secondary":null}}}
                                """);
                            await SendJsonAsync(socket, limits.RootElement, _shutdown.Token).ConfigureAwait(false);
                        }

                        if (BeforeHistoryNotificationJson is { } beforeHistory)
                        {
                            using var notification = JsonDocument.Parse(beforeHistory);
                            await SendJsonAsync(socket, notification.RootElement, _shutdown.Token).ConfigureAwait(false);
                        }

                        using (var history = JsonDocument.Parse(ThreadHistoryJson ?? "{\"turns\":[]}"))
                        {
                            if (method == "thread/turns/list")
                            {
                                await SendJsonAsync(socket, new { id, result = new { data = history.RootElement.GetProperty("turns"), nextCursor = (string?)null } },
                                    _shutdown.Token).ConfigureAwait(false);
                            }
                            else
                            {
                                await SendJsonAsync(socket, new { id, result = new { thread = history.RootElement } },
                                    _shutdown.Token).ConfigureAwait(false);
                            }
                        }

                        if (AfterHistoryNotificationJson is { } afterHistory)
                        {
                            using var notification = JsonDocument.Parse(afterHistory);
                            await SendJsonAsync(socket, notification.RootElement, _shutdown.Token).ConfigureAwait(false);
                        }
                        break;

                    case "thread/start" when id is not null:
                        await SendJsonAsync(socket, new
                        {
                            id,
                            result = new
                            {
                                thread = new
                                {
                                    id = LoadedThreadId
                                }
                            }
                        }, _shutdown.Token).ConfigureAwait(false);
                        if (EmitMcpStartupStatus)
                        {
                            await SendJsonAsync(socket, new
                            {
                                method = "mcpServer/startupStatus/updated",
                                @params = new
                                {
                                    name = "codex_apps",
                                    status = "starting",
                                    error = (string?)null
                                }
                            }, _shutdown.Token).ConfigureAwait(false);
                        }
                        await SendProtocolV2SurfaceNotificationsAsync(socket).ConfigureAwait(false);
                        break;

                    case "turn/start" when id is not null:
                        if (@params.TryGetProperty("collaborationMode", out var collaboration))
                        {
                            LastTurnCollaborationMode = GetString(collaboration, "mode");
                            var settings = collaboration.GetProperty("settings");
                            LastTurnCollaborationModel = GetString(settings, "model");
                            LastTurnCollaborationEffort = GetString(settings, "reasoning_effort");
                        }
                        LastTurnText = ReadTurnText(@params);
                        LastTurnServiceTier =
                            @params.ValueKind == JsonValueKind.Object &&
                            @params.TryGetProperty("serviceTier", out var serviceTierElement) &&
                            serviceTierElement.ValueKind == JsonValueKind.String
                                ? serviceTierElement.GetString()
                                : null;
                        var turnId = "turn-remote-1";
                        var turnText = ReadTurnText(@params) ?? "Continue from the shared thread.";
                        await SendJsonAsync(socket, new
                        {
                            id,
                            result = new
                            {
                                turn = new
                                {
                                    id = turnId
                                }
                            }
                        }, _shutdown.Token).ConfigureAwait(false);
                        await SendJsonAsync(socket, new
                        {
                            method = "turn/started",
                            @params = new
                            {
                                turn = new
                                {
                                    id = turnId,
                                    model = "gpt-5.3-codex",
                                    effort = "medium"
                                }
                            }
                        }, _shutdown.Token).ConfigureAwait(false);
                        if (EmitRichHistoryItems)
                        {
                            await SendJsonAsync(socket, new
                            {
                                method = "item/started",
                                @params = new
                                {
                                    turnId = EmitTurnIds ? turnId : null,
                                    item = new
                                    {
                                        id = "item-user-1",
                                        type = "userMessage",
                                        content = new[]
                                        {
                                            new
                                            {
                                                type = "text",
                                                text = turnText
                                            }
                                        }
                                    }
                                }
                            }, _shutdown.Token).ConfigureAwait(false);
                            await SendJsonAsync(socket, new
                            {
                                method = "item/completed",
                                @params = new
                                {
                                    turnId = EmitTurnIds ? turnId : null,
                                    item = new
                                    {
                                        id = "item-user-1",
                                        type = "userMessage",
                                        content = new[]
                                        {
                                            new
                                            {
                                                type = "text",
                                                text = turnText
                                            }
                                        }
                                    }
                                }
                            }, _shutdown.Token).ConfigureAwait(false);
                            if (EmitMcpToolProgress)
                            {
                                await SendJsonAsync(socket, new
                                {
                                    method = "item/started",
                                    @params = new
                                    {
                                        turnId = EmitTurnIds ? turnId : null,
                                        item = new
                                        {
                                            id = "item-mcp-1",
                                            type = "mcpToolCall",
                                            title = "grep",
                                            summary = "Searching the repo"
                                        }
                                    }
                                }, _shutdown.Token).ConfigureAwait(false);
                                await SendJsonAsync(socket, new
                                {
                                    method = "item/mcpToolCall/progress",
                                    @params = new
                                    {
                                        turnId = EmitTurnIds ? turnId : null,
                                        itemId = "item-mcp-1",
                                        toolUseId = "item-mcp-1",
                                        toolName = "grep",
                                        summary = "Searching src for AppServerControl runtime events"
                                    }
                                }, _shutdown.Token).ConfigureAwait(false);
                                await SendJsonAsync(socket, new
                                {
                                    method = "item/completed",
                                    @params = new
                                    {
                                        turnId = EmitTurnIds ? turnId : null,
                                        item = new
                                        {
                                            id = "item-mcp-1",
                                            type = "mcpToolCall",
                                            title = "grep",
                                            summary = "Found AppServerControl runtime handlers"
                                        }
                                    }
                                }, _shutdown.Token).ConfigureAwait(false);
                            }
                            await SendJsonAsync(socket, new
                            {
                                method = "item/started",
                                @params = new
                                {
                                    turnId = EmitTurnIds ? turnId : null,
                                    item = new
                                    {
                                        id = "item-command-1",
                                        type = "commandExecution",
                                        command = "pwsh.exe -Command pwd"
                                    }
                                }
                            }, _shutdown.Token).ConfigureAwait(false);
                            if (EmitBackgroundTerminalWaitNotification)
                            {
                                await SendJsonAsync(socket, new
                                {
                                    method = "codex/event/background_terminal_wait",
                                    @params = new
                                    {
                                        turnId = EmitTurnIds ? turnId : null,
                                        itemId = "item-command-1",
                                        msg = new
                                        {
                                            turn_id = turnId,
                                            item_id = "item-command-1",
                                            text = "Waited for background terminal  npm run lint"
                                        }
                                    }
                                }, _shutdown.Token).ConfigureAwait(false);
                            }
                            if (EmitUnknownAgentNotification)
                            {
                                await SendJsonAsync(socket, new
                                {
                                    method = "codex/event/unhandled_notification",
                                    @params = new
                                    {
                                        turnId = EmitTurnIds ? turnId : null,
                                        msg = new
                                        {
                                            turn_id = turnId,
                                            text = "Unhandled codex event for fallback coverage"
                                        }
                                    }
                                }, _shutdown.Token).ConfigureAwait(false);
                            }
                            await SendJsonAsync(socket, new
                            {
                                method = "item/completed",
                                @params = new
                                {
                                    turnId = EmitTurnIds ? turnId : null,
                                    item = new
                                    {
                                        id = "item-command-1",
                                        type = "commandExecution",
                                        command = "pwsh.exe -Command pwd"
                                    }
                                }
                            }, _shutdown.Token).ConfigureAwait(false);
                            await SendJsonAsync(socket, new
                            {
                                method = "item/started",
                                @params = new
                                {
                                    turnId = EmitTurnIds ? turnId : null,
                                    item = new
                                    {
                                        id = "item-agent-1",
                                        type = "agentMessage",
                                        content = new[]
                                        {
                                            new
                                            {
                                                type = "text",
                                                text = AssistantReply
                                            }
                                        }
                                    }
                                }
                            }, _shutdown.Token).ConfigureAwait(false);
                        }
                        if (TurnCompletionDelayMs > 0)
                        {
                            await Task.Delay(TurnCompletionDelayMs, _shutdown.Token).ConfigureAwait(false);
                        }

                        await SendJsonAsync(socket, new
                        {
                            method = "item/agentMessage/delta",
                            @params = new
                            {
                                turnId = EmitTurnIds ? turnId : null,
                                itemId = "item-agent-1",
                                delta = AssistantReply
                            }
                        }, _shutdown.Token).ConfigureAwait(false);
                        if (EmitProtocolV2Surface)
                        {
                            await SendJsonAsync(socket, new
                            {
                                method = "item/fileChange/patchUpdated",
                                @params = new
                                {
                                    turnId,
                                    itemId = "item-file-change-remote-1",
                                    changes = new object[]
                                    {
                                        new
                                        {
                                            path = "protocol-v2.txt",
                                            kind = "update",
                                            diff = "--- a/protocol-v2.txt\n+++ b/protocol-v2.txt\n@@ -1 +1 @@\n-old\n+from patch updated"
                                        }
                                    }
                                }
                            }, _shutdown.Token).ConfigureAwait(false);
                            await SendJsonAsync(socket, new
                            {
                                method = "command/exec/outputDelta",
                                @params = new
                                {
                                    processId = "exec-remote-1",
                                    stream = "stdout",
                                    deltaBase64 = Convert.ToBase64String(Encoding.UTF8.GetBytes("protocol command output")),
                                    capReached = false
                                }
                            }, _shutdown.Token).ConfigureAwait(false);
                            await SendJsonAsync(socket, new
                            {
                                method = "process/exited",
                                @params = new
                                {
                                    processHandle = "exec-remote-1",
                                    exitCode = 0,
                                    stdout = "",
                                    stdoutCapReached = false,
                                    stderr = "",
                                    stderrCapReached = false
                                }
                            }, _shutdown.Token).ConfigureAwait(false);
                        }
                        if (EmitRichHistoryItems)
                        {
                            await SendJsonAsync(socket, new
                            {
                                method = "item/completed",
                                @params = new
                                {
                                    turnId = EmitTurnIds ? turnId : null,
                                    item = new
                                    {
                                        id = "item-agent-1",
                                        type = "agentMessage",
                                        content = new[]
                                        {
                                            new
                                            {
                                                type = "text",
                                                text = AssistantReply
                                            }
                                        }
                                    }
                                }
                            }, _shutdown.Token).ConfigureAwait(false);
                        }
                        await SendJsonAsync(socket, new
                        {
                            method = "turn/completed",
                            @params = new
                            {
                                turn = new
                                {
                                    id = turnId,
                                    status = "completed"
                                }
                            }
                        }, _shutdown.Token).ConfigureAwait(false);
                        if (EmitLateDiffAfterCompletion)
                        {
                            await SendJsonAsync(socket, new
                            {
                                method = "turn/diff/updated",
                                @params = new
                                {
                                    turnId = turnId,
                                    unifiedDiff = "--- a/remote.txt\n+++ b/remote.txt\n@@ -1 +1 @@\n-old\n+new"
                                }
                            }, _shutdown.Token).ConfigureAwait(false);
                        }

                        break;

                    case "turn/steer" when id is not null:
                        await SendJsonAsync(socket, new
                        {
                            id,
                            result = new
                            {
                                turnId = GetString(@params, "expectedTurnId") ?? "turn-remote-1"
                            }
                        }, _shutdown.Token).ConfigureAwait(false);
                        break;

                    case "thread/compact/start" when id is not null:
                        await SendJsonAsync(socket, new { id, result = new { } }, _shutdown.Token).ConfigureAwait(false);
                        break;

                    case "turn/interrupt" when id is not null:
                        await SendJsonAsync(socket, new { id, result = new { } }, _shutdown.Token).ConfigureAwait(false);
                        await SendJsonAsync(socket, new
                        {
                            method = "turn/aborted",
                            @params = new
                            {
                                turnId = GetString(@params, "turnId") ?? "turn-remote-1",
                                reason = "interrupt"
                            }
                        }, _shutdown.Token).ConfigureAwait(false);
                        break;
                }
            }
        }
        catch
        {
        }
        finally
        {
            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                }
            }

            socket.Dispose();
        }
    }

    private static string? GetString(JsonElement element, string propertyName)
    {
        return element.ValueKind == JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;
    }

    private static string? ReadTurnText(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("input", out var input) ||
            input.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parts = new List<string>();
        using var entries = input.EnumerateArray();
        while (entries.MoveNext())
        {
            var entry = entries.Current;
            var text = GetString(entry, "text");
            if (!string.IsNullOrWhiteSpace(text))
            {
                parts.Add(text.Trim());
            }
        }

        return parts.Count == 0 ? null : string.Join("\n\n", parts);
    }

    private static async Task<string?> ReceiveMessageAsync(WebSocket socket, CancellationToken ct)
    {
        var buffer = new byte[8192];
        using var message = new MemoryStream();
        while (true)
        {
            var result = await socket.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            if (result.Count > 0)
            {
                await message.WriteAsync(buffer.AsMemory(0, result.Count), ct).ConfigureAwait(false);
            }

            if (result.EndOfMessage)
            {
                break;
            }
        }

        return Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
    }

    private static Task SendJsonAsync(WebSocket socket, object payload, CancellationToken ct)
    {
        var json = JsonSerializer.Serialize(payload, JsonOptions);
        return socket.SendAsync(Encoding.UTF8.GetBytes(json), WebSocketMessageType.Text, endOfMessage: true, ct);
    }

    private static string ToHttpPrefix(string endpoint)
    {
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri))
        {
            throw new InvalidOperationException($"Invalid websocket endpoint '{endpoint}'.");
        }

        var builder = new UriBuilder(uri)
        {
            Scheme = uri.Scheme == Uri.UriSchemeWss ? Uri.UriSchemeHttps : Uri.UriSchemeHttp
        };
        return builder.Uri.ToString();
    }

    private static int GetFreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}

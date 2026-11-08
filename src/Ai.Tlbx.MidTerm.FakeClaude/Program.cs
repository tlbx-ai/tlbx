using System.Globalization;
using System.Text.Json;

var sessionId = ReadOption(args, "--resume")
                ?? ReadOption(args, "--session-id")
                ?? "claude-session-" + Guid.NewGuid().ToString("N");
var turn = 0;
string? currentEffort = null;
var currentModel = "claude-test";
PendingTool? pending = null;
var waitingForSteer = false;
var steeringAbort = false;
var queuedAtInterrupt = false;
var compactFailure = false;

while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line)
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    using var document = JsonDocument.Parse(line);
    var root = document.RootElement;
    var type = GetString(root, "type");
    if (File.Exists("capture-runtime-contract"))
        await File.AppendAllTextAsync("runtime-contract.jsonl", JsonSerializer.Serialize(new
        {
            type, subtype = GetString(root, "request", "subtype"),
            sessionId = ReadOption(args, "--session-id"), resume = ReadOption(args, "--resume")
        }) + Environment.NewLine).ConfigureAwait(false);


    if (string.Equals(type, "control_request", StringComparison.Ordinal))
    {
        var requestId = GetString(root, "request_id") ?? string.Empty;
        var subtype = GetString(root, "request", "subtype");
        if (subtype == "initialize" && File.Exists("reject-initialize"))
        {
            await WriteJsonAsync(new { type = "control_response", response = new
                { subtype = "error", request_id = requestId, error = "Fixture initialization rejected." } }).ConfigureAwait(false);
            continue;
        }

        if (subtype == "apply_flag_settings" && root.GetProperty("request").TryGetProperty("settings", out var settings) &&
            settings.TryGetProperty("effortLevel", out var effort))
            currentEffort = effort.ValueKind == JsonValueKind.String ? effort.GetString() : null;
        if (subtype == "set_model") currentModel = GetString(root, "request", "model") ?? "claude-test";
        await WriteJsonAsync(new
        {
            type = "control_response",
            response = new
            {
                subtype = "success",
                request_id = requestId,
                response = subtype == "initialize" ? (object)new
                {
                    commands = new[]
                    {
                        new { name = "review", description = "Review changes", argumentHint = "<file>" },
                        new { name = "compact", description = "Compact conversation", argumentHint = "" },
                        new { name = "exit", description = "Exit terminal", argumentHint = "" }
                    },
                    models = new[]
                    {
                        new { value = "claude-test", displayName = "Claude Test", description = "Native test model", supportsEffort = true, supportedEffortLevels = new[] { "low", "high" } }
                    }
                } : subtype == "interrupt" ? new { still_queued = queuedAtInterrupt ? new[] { "queued-message" } : Array.Empty<string>() } : new { }
            }
        }).ConfigureAwait(false);
        if (subtype == "interrupt" && waitingForSteer)
        {
            await Task.Delay(150).ConfigureAwait(false);
            await WriteJsonAsync(new { type = "result", subtype = "success", session_id = sessionId,
                is_error = false, result = "Initial answer.", queued_turn_count = 0,
                terminal_reason = "aborted_streaming" }).ConfigureAwait(false);
            waitingForSteer = false;
        }
        continue;
    }

    if (string.Equals(type, "control_response", StringComparison.Ordinal) &&
        pending is not null &&
        string.Equals(GetString(root, "response", "request_id"), pending.RequestId, StringComparison.Ordinal))
    {
        var resolvedPrompt = pending.Prompt;
        if (resolvedPrompt.Contains("Ask native question", StringComparison.Ordinal))
        {
            var answer = GetString(root, "response", "response", "updatedInput", "answers", "Which acceptance color?");
            if (answer is null) throw new InvalidOperationException("Claude question answers must be native strings.");
            resolvedPrompt += " answer=" + answer;
        }
        await FinishTurnAsync(resolvedPrompt, pending.ImageSummary, pending.ToolId, pending.Turn).ConfigureAwait(false);
        pending = null;
        continue;
    }

    if (!string.Equals(type, "user", StringComparison.Ordinal)) continue;

    turn++;
    var prompt = ReadUserText(root);
    if (waitingForSteer)
    {
        if (GetString(root, "priority") != "now") throw new InvalidOperationException("Active steering must use native priority now.");
        await WriteJsonAsync(new { type = "assistant", session_id = sessionId, message = new { id = "native-original", content = new[] { new { type = "text", text = "Initial answer." } } } }).ConfigureAwait(false);
        await WriteJsonAsync(new { type = "result", subtype = "success", session_id = sessionId, is_error = false, result = "Initial answer.", queued_turn_count = steeringAbort ? 0 : 1, terminal_reason = steeringAbort ? "aborted_streaming" : null }).ConfigureAwait(false);
        await EmitSteerMessageAsync("native-steered", "Steered final answer.").ConfigureAwait(false);
        await WriteJsonAsync(new { type = "assistant", session_id = sessionId, message = new { id = "native-steered", content = new[] { new { type = "text", text = "Steered final answer." } } } }).ConfigureAwait(false);
        await WriteJsonAsync(new { type = "result", subtype = "success", session_id = sessionId, is_error = false, result = "Steered final answer.", queued_turn_count = 0 }).ConfigureAwait(false);
        waitingForSteer = false;
        continue;
    }
    if (prompt == "/compact")
    {
        await WriteJsonAsync(new { type = "system", subtype = "status", status = "compacting", session_id = sessionId }).ConfigureAwait(false);
        if (compactFailure)
        {
            const string error = "Error during compaction: simulated native failure.";
            await WriteJsonAsync(new { type = "assistant", error = "invalid_request", session_id = sessionId,
                message = new { id = "compact-error", usage = new { input_tokens = 0, output_tokens = 0 }, content = new[] { new { type = "text", text = error } } } }).ConfigureAwait(false);
            await WriteJsonAsync(new { type = "system", subtype = "status", status = (string?)null, compact_result = "failed", compact_error = error, session_id = sessionId }).ConfigureAwait(false);
            await WriteJsonAsync(new { type = "result", subtype = "success", session_id = sessionId,
                is_error = false, result = error, queued_turn_count = 0 }).ConfigureAwait(false);
            continue;
        }
        await WriteJsonAsync(new { type = "system", subtype = "compact_boundary", session_id = sessionId,
            compact_metadata = new { trigger = "manual", pre_tokens = 119, post_tokens = 11 } }).ConfigureAwait(false);
        await WriteJsonAsync(new { type = "result", subtype = "success", session_id = sessionId,
            is_error = false, result = "Native compaction completed.", queued_turn_count = 0 }).ConfigureAwait(false);
        continue;
    }
    if (prompt.Contains("After native stop", StringComparison.Ordinal))
    {
        await WriteJsonAsync(new { type = "assistant", session_id = sessionId,
            message = new { id = "native-after-stop", usage = new { input_tokens = 119, output_tokens = 5 }, content = new[] { new { type = "text", text = "After native stop." } } } }).ConfigureAwait(false);
        await WriteJsonAsync(new { type = "result", subtype = "success", session_id = sessionId,
            is_error = false, result = "After native stop.", queued_turn_count = 0 }).ConfigureAwait(false);
        continue;
    }
    if (prompt.Contains("Hold native Claude turn", StringComparison.Ordinal))
    {
        waitingForSteer = true;
        queuedAtInterrupt = prompt.Contains("queued cancellation", StringComparison.Ordinal);
        compactFailure = prompt.Contains("compact failure", StringComparison.Ordinal);
        steeringAbort = prompt.Contains("streaming abort", StringComparison.Ordinal);
        await EmitSteerMessageAsync("native-original", "Initial answer.").ConfigureAwait(false);
        continue;
    }
    var plan = prompt.Contains("Plan acceptance", StringComparison.Ordinal);
    var question = prompt.Contains("Ask native question", StringComparison.Ordinal);
    var toolName = plan ? "ExitPlanMode" : question ? "AskUserQuestion" : "Bash";
    object toolInput = plan ? new { plan = "# TLBX_CLAUDE_PLAN_FAKE\n\n1. Review.\n2. Wait for implementation." }
        : question ? new { questions = new[] { new { question = "Which acceptance color?", header = "Color", multiSelect = true,
            options = new[] { new { label = "Blue", description = "Blue color" }, new { label = "Red", description = "Red color" } } } } }
        : (object)new { command = "pwd" };
    var imageSummary = ReadImageSummary(root);
    var toolId = $"tool-bash-{turn.ToString(CultureInfo.InvariantCulture)}";
    await WriteJsonAsync(new { type = "system", subtype = "init", session_id = sessionId, model = currentModel, effort = currentEffort, terminal_slash_commands = new[] { "exit" } }).ConfigureAwait(false);
    await WriteJsonAsync(new
    {
        type = "stream_event",
        session_id = sessionId,
        @event = new { type = "message_start", message = new { id = $"msg-{turn.ToString(CultureInfo.InvariantCulture)}", role = "assistant", model = "claude-test" } }
    }).ConfigureAwait(false);
    await WriteJsonAsync(new
    {
        type = "stream_event",
        session_id = sessionId,
        @event = new { type = "content_block_start", index = 0, content_block = new { type = "text" } }
    }).ConfigureAwait(false);
    await WriteJsonAsync(new
    {
        type = "stream_event",
        session_id = sessionId,
        @event = new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text = "Claude is inspecting the workspace. " } }
    }).ConfigureAwait(false);
    await WriteJsonAsync(new
    {
        type = "stream_event",
        session_id = sessionId,
        @event = new { type = "content_block_start", index = 1, content_block = new { type = "tool_use", id = toolId, name = toolName, input = toolInput } }
    }).ConfigureAwait(false);

    var permissionRequestId = $"permission-{turn.ToString(CultureInfo.InvariantCulture)}";
    await WriteJsonAsync(new
    {
        type = "control_request",
        request_id = permissionRequestId,
        request = new
        {
            subtype = "can_use_tool",
            tool_name = toolName,
            input = toolInput,
            tool_use_id = toolId,
            permission_suggestions = Array.Empty<object>()
        }
    }).ConfigureAwait(false);
    pending = new PendingTool(permissionRequestId, prompt, imageSummary, toolId, turn);
}

async Task EmitSteerMessageAsync(string id, string text)
{
    await WriteJsonAsync(new { type = "stream_event", session_id = sessionId, @event = new { type = "message_start", message = new { id, model = currentModel } } }).ConfigureAwait(false);
    await WriteJsonAsync(new { type = "stream_event", session_id = sessionId, @event = new { type = "content_block_start", index = 0, content_block = new { type = "text" } } }).ConfigureAwait(false);
    await WriteJsonAsync(new { type = "stream_event", session_id = sessionId, @event = new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text } } }).ConfigureAwait(false);
}

async Task FinishTurnAsync(string prompt, string imageSummary, string toolId, int currentTurn)
{
    await WriteJsonAsync(new
    {
        type = "user",
        session_id = sessionId,
        message = new { content = new object[] { new { type = "tool_result", tool_use_id = toolId, is_error = prompt.Contains("Plan acceptance", StringComparison.Ordinal), content = $"pwd -> {Environment.CurrentDirectory}" } } },
        tool_use_result = new { tool_use_id = toolId, stdout = Environment.CurrentDirectory, stderr = "" }
    }).ConfigureAwait(false);
    var text = $"Fake Claude SDK reply {currentTurn.ToString(CultureInfo.InvariantCulture)}: {prompt}{imageSummary}";
    await WriteJsonAsync(new
    {
        type = "stream_event",
        session_id = sessionId,
        @event = new { type = "content_block_delta", index = 0, delta = new { type = "text_delta", text } }
    }).ConfigureAwait(false);
    await WriteJsonAsync(new
    {
        type = "assistant",
        session_id = sessionId,
        message = new
        {
            usage = new { input_tokens = 12, cache_read_input_tokens = 100, cache_creation_input_tokens = 0, output_tokens = 7 },
            content = new object[] { new { type = "text", text } }
        }
    }).ConfigureAwait(false);
    await WriteJsonAsync(new
    {
        type = "result", subtype = "success", session_id = sessionId, is_error = false, result = text,
        total_cost_usd = 0.01m * currentTurn,
        usage = new { input_tokens = 12, output_tokens = 7 },
        modelUsage = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            [currentModel] = new { inputTokens = 10000 * currentTurn, outputTokens = 100 * currentTurn, cacheReadInputTokens = 5000 * currentTurn, cacheCreationInputTokens = 0, contextWindow = 200000 }
        }
    }).ConfigureAwait(false);
}

static async Task WriteJsonAsync<T>(T payload)
{
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(payload)).ConfigureAwait(false);
    await Console.Out.FlushAsync().ConfigureAwait(false);
}

static string ReadUserText(JsonElement root)
{
    if (!root.TryGetProperty("message", out var message) ||
        !message.TryGetProperty("content", out var content) ||
        content.ValueKind != JsonValueKind.Array) return string.Empty;
    foreach (var block in content.EnumerateArray())
    {
        if (string.Equals(GetString(block, "type"), "text", StringComparison.Ordinal))
            return GetString(block, "text") ?? string.Empty;
    }
    return string.Empty;
}

static string ReadImageSummary(JsonElement root)
{
    if (!root.TryGetProperty("message", out var message) ||
        !message.TryGetProperty("content", out var content) ||
        content.ValueKind != JsonValueKind.Array) return string.Empty;
    foreach (var block in content.EnumerateArray())
    {
        if (!string.Equals(GetString(block, "type"), "image", StringComparison.Ordinal)) continue;
        var mimeType = GetString(block, "source", "media_type") ?? "unknown";
        var base64 = GetString(block, "source", "data") ?? string.Empty;
        var byteCount = Convert.FromBase64String(base64).Length;
        return $" [image {mimeType} {byteCount.ToString(CultureInfo.InvariantCulture)} bytes]";
    }
    return string.Empty;
}

static string? ReadOption(IReadOnlyList<string> values, string name)
{
    var inlinePrefix = name + "=";
    for (var index = 0; index < values.Count; index++)
    {
        // The Agent SDK also emits CLI options as --resume=value and --session-id=value.
        if (values[index].StartsWith(inlinePrefix, StringComparison.Ordinal))
            return values[index][inlinePrefix.Length..];
        if (string.Equals(values[index], name, StringComparison.Ordinal) && index + 1 < values.Count)
            return values[index + 1];
    }
    return null;
}

static string? GetString(JsonElement element, params string[] path)
{
    var current = element;
    foreach (var segment in path)
    {
        if (current.ValueKind == JsonValueKind.Array && int.TryParse(segment, CultureInfo.InvariantCulture, out var index))
        {
            if (index < 0 || index >= current.GetArrayLength()) return null;
            current = current[index];
            continue;
        }
        if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current)) return null;
    }
    return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
}

internal sealed record PendingTool(string RequestId, string Prompt, string ImageSummary, string ToolId, int Turn);

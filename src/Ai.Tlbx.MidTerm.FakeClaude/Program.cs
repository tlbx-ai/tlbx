using System.Globalization;
using System.Text.Json;

var sessionId = ReadOption(args, "--resume")
                ?? ReadOption(args, "--session-id")
                ?? "claude-session-" + Guid.NewGuid().ToString("N");
var turn = 0;
PendingTool? pending = null;

while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } line)
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    using var document = JsonDocument.Parse(line);
    var root = document.RootElement;
    var type = GetString(root, "type");

    if (string.Equals(type, "control_request", StringComparison.Ordinal))
    {
        var requestId = GetString(root, "request_id") ?? string.Empty;
        await WriteJsonAsync(new
        {
            type = "control_response",
            response = new
            {
                subtype = "success",
                request_id = requestId,
                response = new { }
            }
        }).ConfigureAwait(false);
        continue;
    }

    if (string.Equals(type, "control_response", StringComparison.Ordinal) &&
        pending is not null &&
        string.Equals(GetString(root, "response", "request_id"), pending.RequestId, StringComparison.Ordinal))
    {
        await FinishTurnAsync(pending.Prompt, pending.ImageSummary, pending.ToolId, pending.Turn).ConfigureAwait(false);
        pending = null;
        continue;
    }

    if (!string.Equals(type, "user", StringComparison.Ordinal)) continue;

    turn++;
    var prompt = ReadUserText(root);
    var imageSummary = ReadImageSummary(root);
    var toolId = $"tool-bash-{turn.ToString(CultureInfo.InvariantCulture)}";
    await WriteJsonAsync(new { type = "system", subtype = "init", session_id = sessionId }).ConfigureAwait(false);
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
        @event = new { type = "content_block_start", index = 1, content_block = new { type = "tool_use", id = toolId, name = "Bash", input = new { command = "pwd" } } }
    }).ConfigureAwait(false);

    var permissionRequestId = $"permission-{turn.ToString(CultureInfo.InvariantCulture)}";
    await WriteJsonAsync(new
    {
        type = "control_request",
        request_id = permissionRequestId,
        request = new
        {
            subtype = "can_use_tool",
            tool_name = "Bash",
            input = new { command = "pwd" },
            tool_use_id = toolId,
            permission_suggestions = Array.Empty<object>()
        }
    }).ConfigureAwait(false);
    pending = new PendingTool(permissionRequestId, prompt, imageSummary, toolId, turn);
}

async Task FinishTurnAsync(string prompt, string imageSummary, string toolId, int currentTurn)
{
    await WriteJsonAsync(new
    {
        type = "user",
        session_id = sessionId,
        message = new { content = new object[] { new { type = "tool_result", tool_use_id = toolId, content = $"pwd -> {Environment.CurrentDirectory}" } } },
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
        message = new { content = new object[] { new { type = "text", text } } }
    }).ConfigureAwait(false);
    await WriteJsonAsync(new { type = "result", subtype = "success", session_id = sessionId, is_error = false, result = text }).ConfigureAwait(false);
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

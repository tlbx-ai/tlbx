using System.Text.Json;

var sessionId = "grok-session-" + Guid.NewGuid().ToString("N");
var activePromptId = string.Empty;
var capturePath = Environment.GetEnvironmentVariable("MIDTERM_FAKE_GROK_CAPTURE_PATH");
PersistLaunchCapture(capturePath, CreateLaunchCapture());

while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } rawLine)
{
    if (string.IsNullOrWhiteSpace(rawLine))
    {
        continue;
    }

    using var document = JsonDocument.Parse(rawLine);
    var root = document.RootElement;
    var method = GetString(root, "method");
    var id = root.TryGetProperty("id", out var idElement) ? idElement.Clone() : default;

    switch (method)
    {
        case "initialize":
            await WriteResponseAsync(id, new
            {
                protocolVersion = 1,
                agentCapabilities = new
                {
                    loadSession = true,
                    promptCapabilities = new
                    {
                        image = false,
                        audio = false,
                        embeddedContext = true
                    },
                    mcpCapabilities = new
                    {
                        http = true,
                        sse = true
                    }
                },
                authMethods = Array.Empty<object>(),
                _meta = new
                {
                    agentVersion = "fake-grok",
                    modelState = BuildModelState()
                }
            }).ConfigureAwait(false);
            break;
        case "session/new":
            await WriteResponseAsync(id, new
            {
                sessionId,
                models = BuildModelState()
            }).ConfigureAwait(false);
            break;
        case "session/prompt":
            activePromptId = id.ToString();
            var text = ReadPromptText(root);
            await WriteNotificationAsync("session/update", new
            {
                sessionId,
                update = new
                {
                    sessionUpdate = "agent_thought_chunk",
                    content = new
                    {
                        type = "text",
                        text = "Fake Grok is thinking. "
                    }
                }
            }).ConfigureAwait(false);
            await WriteNotificationAsync("session/update", new
            {
                sessionId,
                update = new
                {
                    sessionUpdate = "tool_call",
                    toolCallId = "grok-tool-1",
                    title = "Inspect workspace",
                    kind = "read",
                    status = "pending"
                }
            }).ConfigureAwait(false);
            await WriteNotificationAsync("session/update", new
            {
                sessionId,
                update = new
                {
                    sessionUpdate = "tool_call_update",
                    toolCallId = "grok-tool-1",
                    status = "completed",
                    content = new object[]
                    {
                        new
                        {
                            type = "content",
                            content = new
                            {
                                type = "text",
                                text = "Workspace inspected."
                            }
                        }
                    }
                }
            }).ConfigureAwait(false);

            if (text.Contains("permission", StringComparison.OrdinalIgnoreCase))
            {
                await WriteRequestAsync("grok-permission-1", "session/request_permission", new
                {
                    sessionId,
                    toolCall = new
                    {
                        toolCallId = "grok-tool-2",
                        title = "Run a command"
                    },
                    options = new[]
                    {
                        new { optionId = "allow-once", name = "Allow once", kind = "allow_once" },
                        new { optionId = "reject-once", name = "Reject", kind = "reject_once" }
                    }
                }).ConfigureAwait(false);
            }

            if (text.Contains("interrupt", StringComparison.OrdinalIgnoreCase))
            {
                await Task.Delay(TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                break;
            }

            await WriteNotificationAsync("session/update", new
            {
                sessionId,
                update = new
                {
                    sessionUpdate = "agent_message_chunk",
                    content = new
                    {
                        type = "text",
                        text = "Fake Grok reply. prompt=" + text
                    }
                }
            }).ConfigureAwait(false);
            await WriteResponseAsync(id, new
            {
                stopReason = "end_turn"
            }).ConfigureAwait(false);
            activePromptId = string.Empty;
            break;
        case "session/cancel":
            if (!string.IsNullOrWhiteSpace(activePromptId))
            {
                await WriteResponseAsync(JsonDocument.Parse($"\"{activePromptId}\"").RootElement.Clone(), new
                {
                    stopReason = "cancelled"
                }).ConfigureAwait(false);
                activePromptId = string.Empty;
            }

            break;
        default:
            if (id.ValueKind != JsonValueKind.Undefined)
            {
                await WriteJsonAsync(new
                {
                    jsonrpc = "2.0",
                    id,
                    error = new
                    {
                        code = -32601,
                        message = "Unsupported fake Grok method."
                    }
                }).ConfigureAwait(false);
            }

            break;
    }
}

static object BuildModelState()
{
    return new
    {
        currentModelId = "grok-build-0.1",
        availableModels = new[]
        {
            new { modelId = "grok-build-0.1", name = "grok-build-0.1" },
            new { modelId = "grok-4.3", name = "grok-4.3" }
        }
    };
}

static string ReadPromptText(JsonElement root)
{
    if (!root.TryGetProperty("params", out var parameters) ||
        !parameters.TryGetProperty("prompt", out var prompt) ||
        prompt.ValueKind != JsonValueKind.Array)
    {
        return string.Empty;
    }

    var parts = new List<string>();
    foreach (var block in prompt.EnumerateArray())
    {
        if (block.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
        {
            parts.Add(text.GetString() ?? string.Empty);
        }
    }

    return string.Join(" ", parts).Trim();
}

static async Task WriteRequestAsync(string id, string method, object parameters)
{
    await WriteJsonAsync(new
    {
        jsonrpc = "2.0",
        id,
        method,
        @params = parameters
    }).ConfigureAwait(false);
}

static async Task WriteNotificationAsync(string method, object parameters)
{
    await WriteJsonAsync(new
    {
        jsonrpc = "2.0",
        method,
        @params = parameters
    }).ConfigureAwait(false);
}

static async Task WriteResponseAsync(JsonElement id, object result)
{
    await WriteJsonAsync(new
    {
        jsonrpc = "2.0",
        id,
        result
    }).ConfigureAwait(false);
}

static async Task WriteJsonAsync<T>(T payload)
{
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(payload)).ConfigureAwait(false);
    await Console.Out.FlushAsync().ConfigureAwait(false);
}

static FakeGrokLaunchCapture CreateLaunchCapture()
{
    var args = Environment.GetCommandLineArgs();
    return new FakeGrokLaunchCapture
    {
        ExecutablePath = args.Length > 0 ? args[0] : null,
        Arguments = args.Skip(1).ToArray(),
        ProcessWorkingDirectory = Environment.CurrentDirectory
    };
}

static void PersistLaunchCapture(string? capturePath, FakeGrokLaunchCapture capture)
{
    if (string.IsNullOrWhiteSpace(capturePath))
    {
        return;
    }

    try
    {
        var directory = Path.GetDirectoryName(capturePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(capturePath, JsonSerializer.Serialize(capture));
    }
    catch
    {
    }
}

static string? GetString(JsonElement element, params string[] path)
{
    var current = element;
    foreach (var segment in path)
    {
        if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
        {
            return null;
        }
    }

    return current.ValueKind == JsonValueKind.String ? current.GetString() : null;
}

internal sealed class FakeGrokLaunchCapture
{
    public string? ExecutablePath { get; set; }

    public string[] Arguments { get; set; } = [];

    public string? ProcessWorkingDirectory { get; set; }
}

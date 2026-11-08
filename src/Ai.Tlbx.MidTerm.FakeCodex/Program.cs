using System.Globalization;
using System.Text.Json;
using System.Text;

var threadId = "thread-fake-1";
var turnId = "turn-fake-1";
var approvalRequestId = JsonSerializer.SerializeToElement(101);
var permissionRequestId = JsonSerializer.SerializeToElement(102);
var userInputRequestId = JsonSerializer.SerializeToElement(103);
var userInputIsSecret = false;
var lastAssistant = string.Empty;
var capturePath = Environment.GetEnvironmentVariable("MIDTERM_FAKE_CODEX_CAPTURE_PATH");
var launchCapture = CreateLaunchCapture();
PersistLaunchCapture(capturePath, launchCapture);
await EmitStartupStderrBlockAsync().ConfigureAwait(false);

while (await Console.In.ReadLineAsync().ConfigureAwait(false) is { } rawLine)
{
    var line = rawLine.Trim();
    if (line.Length == 0)
    {
        continue;
    }

    JsonDocument document;
    try
    {
        document = JsonDocument.Parse(line);
    }
    catch (JsonException ex)
    {
        await Console.Error.WriteLineAsync($"fake-codex-json-error:{ex.Message}:hex={Convert.ToHexString(Encoding.UTF8.GetBytes(line))}");
        await Console.Error.FlushAsync();
        continue;
    }

    using (document)
    {
        var root = document.RootElement;
        if (root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String)
        {
            var method = methodElement.GetString();
            switch (method)
            {
                case "initialize":
                    RecordMethod(launchCapture, method);
                    if (root.TryGetProperty("params", out var initializeParams) && initializeParams.ValueKind == JsonValueKind.Object)
                    {
                        launchCapture.InitializeClientName = GetString(initializeParams, "clientInfo", "name");
                        launchCapture.InitializeClientTitle = GetString(initializeParams, "clientInfo", "title");
                        launchCapture.InitializeClientVersion = GetString(initializeParams, "clientInfo", "version");
                        launchCapture.InitializeExperimentalApi = GetBoolean(initializeParams, "capabilities", "experimentalApi");
                    }

                    PersistLaunchCapture(capturePath, launchCapture);
                    await WriteJsonAsync(new
                    {
                        jsonrpc = "2.0",
                        id = root.GetProperty("id").ToString(),
                        result = new { server = "fake-codex" }
                    }).ConfigureAwait(false);
                    continue;
                case "initialized":
                    RecordMethod(launchCapture, method);
                    PersistLaunchCapture(capturePath, launchCapture);
                    continue;
                case "model/list":
                    RecordMethod(launchCapture, method);
                    var modelCursor = root.TryGetProperty("params", out var modelListParams) &&
                                      modelListParams.ValueKind == JsonValueKind.Object
                        ? GetString(modelListParams, "cursor")
                        : null;
                    PersistLaunchCapture(capturePath, launchCapture);
                    await WriteJsonAsync(new
                    {
                        jsonrpc = "2.0",
                        id = root.GetProperty("id").ToString(),
                        result = new
                        {
                            data = modelCursor is null
                                ? new[]
                                {
                                    new
                                    {
                                        id = "gpt-5.3-codex",
                                        displayName = "GPT-5.3 Codex",
                                        isDefault = false,
                                        supportedReasoningEfforts = new[]
                                        {
                                            new { reasoningEffort = "low" },
                                            new { reasoningEffort = "medium" },
                                            new { reasoningEffort = "high" }
                                        }
                                    }
                                }
                                : new[]
                                {
                                    new
                                    {
                                        id = "gpt-5.6-sol",
                                        displayName = "GPT-5.6 Sol",
                                        isDefault = true,
                                        supportedReasoningEfforts = new[]
                                        {
                                            new { reasoningEffort = "medium" },
                                            new { reasoningEffort = "high" }
                                        }
                                    }
                                },
                            nextCursor = modelCursor is null ? "models-page-2" : null
                        }
                    }).ConfigureAwait(false);
                    continue;
                case "thread/start":
                    RecordMethod(launchCapture, method);
                    if (root.TryGetProperty("params", out var threadStartParams) && threadStartParams.ValueKind == JsonValueKind.Object)
                    {
                        launchCapture.ThreadStartCwd = GetString(threadStartParams, "cwd");
                        launchCapture.ThreadStartApprovalPolicy = GetString(threadStartParams, "approvalPolicy");
                        launchCapture.ThreadStartSandbox = GetString(threadStartParams, "sandbox");
                        launchCapture.ThreadStartExperimentalRawEvents = GetBoolean(threadStartParams, "experimentalRawEvents");
                        launchCapture.ThreadStartPersistExtendedHistory = GetBoolean(threadStartParams, "persistExtendedHistory");
                    }

                    PersistLaunchCapture(capturePath, launchCapture);
                    await WriteJsonAsync(new
                    {
                        jsonrpc = "2.0",
                        method = "thread/started",
                        @params = new
                        {
                            thread = new { id = threadId }
                        }
                    }).ConfigureAwait(false);
                    await WriteJsonAsync(new
                    {
                        jsonrpc = "2.0",
                        id = root.GetProperty("id").ToString(),
                        result = new
                        {
                            thread = new { id = threadId }
                        }
                    }).ConfigureAwait(false);
                    continue;
                case "thread/resume":
                    RecordMethod(launchCapture, method);
                    if (root.TryGetProperty("params", out var threadResumeParams) && threadResumeParams.ValueKind == JsonValueKind.Object)
                    {
                        launchCapture.ThreadResumeCwd = GetString(threadResumeParams, "cwd");
                        launchCapture.ThreadResumeThreadId = GetString(threadResumeParams, "threadId");
                        launchCapture.ThreadResumeApprovalPolicy = GetString(threadResumeParams, "approvalPolicy");
                        launchCapture.ThreadResumeSandbox = GetString(threadResumeParams, "sandbox");
                        launchCapture.ThreadResumePersistExtendedHistory = GetBoolean(threadResumeParams, "persistExtendedHistory");
                        launchCapture.ThreadResumeExcludeTurns = GetBoolean(threadResumeParams, "excludeTurns");
                    }

                    PersistLaunchCapture(capturePath, launchCapture);
                    await WriteJsonAsync(new
                    {
                        jsonrpc = "2.0",
                        method = "thread/started",
                        @params = new
                        {
                            thread = new { id = threadId }
                        }
                    }).ConfigureAwait(false);
                    await WriteJsonAsync(new
                    {
                        jsonrpc = "2.0",
                        id = root.GetProperty("id").ToString(),
                        result = new
                        {
                            thread = new { id = threadId }
                        }
                    }).ConfigureAwait(false);
                    continue;
                case "thread/goal/get":
                    RecordMethod(launchCapture, method);
                    PersistLaunchCapture(capturePath, launchCapture);
                    await WriteJsonAsync(new
                    {
                        jsonrpc = "2.0",
                        id = root.GetProperty("id").ToString(),
                        result = new { goal = (object?)null }
                    }).ConfigureAwait(false);
                    continue;
                case "thread/read":
                    await WriteJsonAsync(new
                    {
                        jsonrpc = "2.0",
                        id = root.GetProperty("id").ToString(),
                        result = new { thread = new { id = threadId, turns = Array.Empty<object>() } }
                    }).ConfigureAwait(false);
                    continue;
                case "turn/start":
                {
                    RecordMethod(launchCapture, method);
                    if (root.TryGetProperty("params", out var turnStartParams))
                    {
                        launchCapture.TurnStartModel = GetString(turnStartParams, "model");
                        launchCapture.TurnStartEffort = GetString(turnStartParams, "effort");
                        launchCapture.TurnStartRuntimeContextKind = GetString(
                            turnStartParams,
                            "additionalContext",
                            "tlbx.agent-controller.runtime",
                            "kind");
                        launchCapture.TurnStartRuntimeContextValue = GetString(
                            turnStartParams,
                            "additionalContext",
                            "tlbx.agent-controller.runtime",
                            "value");
                    }
                    PersistLaunchCapture(capturePath, launchCapture);
                    var (imageCount, hasFileRef, textValue) = GetInputStats(root);
                    lastAssistant = $"Fake Codex reply. images={imageCount.ToString(CultureInfo.InvariantCulture)} fileRefs={hasFileRef.ToString().ToLowerInvariant()} text={textValue}";
                    var requestUserInput = textValue.Contains("ask user", StringComparison.OrdinalIgnoreCase);
                    var requestPermission = textValue.Contains("ask permission", StringComparison.OrdinalIgnoreCase);

                    await WriteJsonAsync(new
                    {
                        jsonrpc = "2.0",
                        id = root.GetProperty("id").ToString(),
                        result = new
                        {
                            turn = new { id = turnId }
                        }
                    }).ConfigureAwait(false);
                    await WriteJsonAsync(new
                    {
                        jsonrpc = "2.0",
                        method = "turn/started",
                        @params = new
                        {
                            turn = new
                            {
                                id = turnId,
                                model = "gpt-5.4-codex",
                                effort = "high"
                            }
                        }
                    }).ConfigureAwait(false);
                    await WriteJsonAsync(new
                    {
                        jsonrpc = "2.0",
                        method = "item/reasoning/textDelta",
                        @params = new
                        {
                            itemId = "item-reasoning-1",
                            delta = "Inspecting repo through fake codex."
                        }
                    }).ConfigureAwait(false);
                    await WriteJsonAsync(new
                    {
                        jsonrpc = "2.0",
                        method = "item/started",
                        @params = new
                        {
                            item = new
                            {
                                id = "item-tool-1",
                                type = "command_execution",
                                detail = "git status"
                            }
                        }
                    }).ConfigureAwait(false);
                    await WriteJsonAsync(new
                    {
                        jsonrpc = "2.0",
                        method = "item/commandExecution/outputDelta",
                        @params = new
                        {
                            itemId = "item-tool-1",
                            delta = "On branch dev"
                        }
                    }).ConfigureAwait(false);
                    await WriteJsonAsync(new
                    {
                        jsonrpc = "2.0",
                        method = "item/completed",
                        @params = new
                        {
                            item = new
                            {
                                id = "item-tool-1",
                                type = "command_execution",
                                detail = "git status"
                            }
                        }
                    }).ConfigureAwait(false);
                    await WriteJsonAsync(new
                    {
                        jsonrpc = "2.0",
                        method = "item/agentMessage/delta",
                        @params = new
                        {
                            itemId = "item-msg-1",
                            delta = lastAssistant
                        }
                    }).ConfigureAwait(false);
                    await WriteJsonAsync(new
                    {
                        jsonrpc = "2.0",
                        method = "turn/diff/updated",
                        @params = new
                        {
                            unifiedDiff = "--- a/file.txt\n+++ b/file.txt\n@@\n-old\n+new\n"
                        }
                    }).ConfigureAwait(false);
                    await WriteJsonAsync(new
                    {
                        jsonrpc = "2.0",
                        method = "item/fileChange/patchUpdated",
                        @params = new
                        {
                            turnId,
                            itemId = "item-file-change-1",
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
                    }).ConfigureAwait(false);

                    if (requestUserInput)
                    {
                        userInputIsSecret = textValue.Contains("secret user input", StringComparison.Ordinal);
                        userInputRequestId = textValue.Contains("string RPC IDs", StringComparison.Ordinal)
                            ? JsonSerializer.SerializeToElement("srv-user-input-1")
                            : JsonSerializer.SerializeToElement(103);
                        await WriteJsonAsync(new
                        {
                            jsonrpc = "2.0",
                            id = userInputRequestId,
                            method = "item/tool/requestUserInput",
                            @params = new
                            {
                                itemId = "item-user-input-1",
                                questions = new object[]
                                {
                                    new
                                    {
                                        id = "choice",
                                        header = "Mode",
                                        question = "Pick a mode",
                                        multiSelect = false,
                                        isOther = textValue.Contains("custom user input", StringComparison.Ordinal),
                                        isSecret = userInputIsSecret,
                                        options = userInputIsSecret ? Array.Empty<object>() : new object[]
                                        {
                                            new { label = "Safe", description = "Safe mode" },
                                            new { label = "Fast", description = "Fast mode" }
                                        }
                                    }
                                }
                            }
                        }).ConfigureAwait(false);
                    }
                    else if (requestPermission)
                    {
                        await WriteJsonAsync(new
                        {
                            jsonrpc = "2.0",
                            id = permissionRequestId,
                            method = "item/permissions/requestApproval",
                            @params = new
                            {
                                threadId,
                                turnId,
                                itemId = "item-permission-1",
                                cwd = Directory.GetCurrentDirectory(),
                                reason = "Fake Codex needs expanded permissions.",
                                permissions = new
                                {
                                    network = new { enabled = true },
                                    fileSystem = new { read = Array.Empty<string>(), write = Array.Empty<string>() }
                                }
                            }
                        }).ConfigureAwait(false);
                    }
                    else
                    {
                        await WriteJsonAsync(new
                        {
                            jsonrpc = "2.0",
                            id = approvalRequestId,
                            method = "item/commandExecution/requestApproval",
                            @params = new
                            {
                                itemId = "item-tool-1",
                                item = new
                                {
                                    id = "item-tool-1",
                                    type = "command_execution",
                                    detail = "git status"
                                }
                            }
                        }).ConfigureAwait(false);
                    }

                    continue;
                }
                case "turn/interrupt":
                    await WriteJsonAsync(new
                    {
                        jsonrpc = "2.0",
                        id = root.GetProperty("id").ToString(),
                        result = new { ok = true }
                    }).ConfigureAwait(false);
                    await WriteJsonAsync(new
                    {
                        jsonrpc = "2.0",
                        method = "turn/aborted",
                        @params = new
                        {
                            turnId,
                            reason = "interrupt"
                        }
                    }).ConfigureAwait(false);
                    continue;
            }
        }

        if (root.TryGetProperty("id", out var idElement))
        {
            var id = idElement.ToString();
            if (string.Equals(id, approvalRequestId.ToString(), StringComparison.Ordinal))
            {
                if (idElement.ValueKind != approvalRequestId.ValueKind) throw new InvalidOperationException("Approval RPC id type changed.");
                await WriteJsonAsync(new
                {
                    jsonrpc = "2.0",
                    method = "turn/completed",
                    @params = new
                    {
                        turn = new
                        {
                            id = turnId,
                            status = "completed"
                        }
                    }
                }).ConfigureAwait(false);
                continue;
            }

            if (string.Equals(id, permissionRequestId.ToString(), StringComparison.Ordinal))
            {
                if (idElement.ValueKind != permissionRequestId.ValueKind) throw new InvalidOperationException("Permission RPC id type changed.");
                await WriteJsonAsync(new
                {
                    jsonrpc = "2.0",
                    method = "serverRequest/resolved",
                    @params = new
                    {
                        threadId,
                        requestId = permissionRequestId
                    }
                }).ConfigureAwait(false);
                await WriteJsonAsync(new
                {
                    jsonrpc = "2.0",
                    method = "turn/completed",
                    @params = new
                    {
                        turn = new
                        {
                            id = turnId,
                            status = "completed"
                        }
                    }
                }).ConfigureAwait(false);
                continue;
            }

            if (string.Equals(id, userInputRequestId.ToString(), StringComparison.Ordinal))
            {
                if (idElement.ValueKind != userInputRequestId.ValueKind) throw new InvalidOperationException("User-input RPC id type changed.");
                var answers = root.GetProperty("result").GetProperty("answers").GetProperty("choice").GetProperty("answers")
                    .EnumerateArray()
                    .Select(static answer => answer.GetString())
                    .Where(static answer => !string.IsNullOrWhiteSpace(answer))
                    .ToArray();
                await WriteJsonAsync(new
                {
                    jsonrpc = "2.0", method = "serverRequest/resolved", @params = new { threadId, requestId = userInputRequestId }
                }).ConfigureAwait(false);
                await WriteJsonAsync(new
                {
                    jsonrpc = "2.0",
                    method = "item/completed",
                    @params = new
                    {
                        item = new
                        {
                            id = "item-msg-1",
                            type = "assistant_message",
                            detail = userInputIsSecret
                                ? answers is ["FIXTURE_SECRET_96"] ? "Native secret receipt verified." : throw new InvalidOperationException("Native secret answer was changed.")
                                : $"{lastAssistant} answer={string.Join(",", answers)}"
                        }
                    }
                }).ConfigureAwait(false);
                await WriteJsonAsync(new
                {
                    jsonrpc = "2.0",
                    method = "turn/completed",
                    @params = new
                    {
                        turn = new
                        {
                            id = turnId,
                            status = "completed"
                        }
                    }
                }).ConfigureAwait(false);
                await WriteJsonAsync(new
                {
                    jsonrpc = "2.0", method = "serverRequest/resolved", @params = new { threadId, requestId = userInputRequestId }
                }).ConfigureAwait(false);
            }
        }
    }
}

static FakeCodexLaunchCapture CreateLaunchCapture()
{
    var args = Environment.GetCommandLineArgs();
    return new FakeCodexLaunchCapture
    {
        ExecutablePath = args.Length > 0 ? args[0] : null,
        Arguments = args.Skip(1).ToArray(),
        ProcessWorkingDirectory = Environment.CurrentDirectory,
        UserProfile = Environment.GetEnvironmentVariable("USERPROFILE"),
        Home = Environment.GetEnvironmentVariable("HOME"),
        CodexHome = Environment.GetEnvironmentVariable("CODEX_HOME"),
        AppData = Environment.GetEnvironmentVariable("APPDATA"),
        LocalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA"),
        Path = Environment.GetEnvironmentVariable("PATH")
    };
}

static void RecordMethod(FakeCodexLaunchCapture capture, string? method)
{
    if (!string.IsNullOrWhiteSpace(method))
    {
        capture.Methods.Add(method);
    }
}

static void PersistLaunchCapture(string? capturePath, FakeCodexLaunchCapture capture)
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

static async Task EmitStartupStderrBlockAsync()
{
    var block = Environment.GetEnvironmentVariable("MIDTERM_FAKE_CODEX_STARTUP_STDERR");
    if (string.IsNullOrWhiteSpace(block))
    {
        return;
    }

    var normalized = block.Replace("\r\n", "\n", StringComparison.Ordinal);
    foreach (var line in normalized.Split('\n'))
    {
        await Console.Error.WriteLineAsync(line).ConfigureAwait(false);
    }

    await Console.Error.FlushAsync().ConfigureAwait(false);
}

static async Task WriteJsonAsync<T>(T payload)
{
    await Console.Out.WriteLineAsync(JsonSerializer.Serialize(payload)).ConfigureAwait(false);
    await Console.Out.FlushAsync().ConfigureAwait(false);
}

static (int ImageCount, bool HasFileRef, string TextValue) GetInputStats(JsonElement root)
{
    if (!root.TryGetProperty("params", out var parameters) ||
        !parameters.TryGetProperty("input", out var input) ||
        input.ValueKind != JsonValueKind.Array)
    {
        return (0, false, string.Empty);
    }

    var imageCount = 0;
    var textParts = new List<string>();
    foreach (var item in input.EnumerateArray())
    {
        var type = item.TryGetProperty("type", out var typeElement) && typeElement.ValueKind == JsonValueKind.String
            ? typeElement.GetString()
            : string.Empty;
        if (string.Equals(type, "image", StringComparison.Ordinal))
        {
            imageCount++;
            continue;
        }

        if (string.Equals(type, "text", StringComparison.Ordinal) &&
            item.TryGetProperty("text", out var textElement) &&
            textElement.ValueKind == JsonValueKind.String)
        {
            textParts.Add(textElement.GetString() ?? string.Empty);
        }
    }

    var textValue = string.Join("\n", textParts).Trim();
    var hasFileRef = textValue.Contains("Attached file:", StringComparison.Ordinal) ||
                     textValue.Contains("Attached files (", StringComparison.Ordinal);
    return (imageCount, hasFileRef, textValue);
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

static bool? GetBoolean(JsonElement element, params string[] path)
{
    var current = element;
    foreach (var segment in path)
    {
        if (current.ValueKind != JsonValueKind.Object || !current.TryGetProperty(segment, out current))
        {
            return null;
        }
    }

    return current.ValueKind == JsonValueKind.True || current.ValueKind == JsonValueKind.False
        ? current.GetBoolean()
        : null;
}

internal sealed class FakeCodexLaunchCapture
{
    public string? ExecutablePath { get; set; }

    public string[] Arguments { get; set; } = [];

    public string? ProcessWorkingDirectory { get; set; }

    public string? UserProfile { get; set; }

    public string? Home { get; set; }

    public string? CodexHome { get; set; }

    public string? AppData { get; set; }

    public string? LocalAppData { get; set; }

    public string? Path { get; set; }

    public List<string> Methods { get; set; } = [];

    public string? InitializeClientName { get; set; }

    public string? InitializeClientTitle { get; set; }

    public string? InitializeClientVersion { get; set; }

    public bool? InitializeExperimentalApi { get; set; }

    public string? ThreadStartCwd { get; set; }

    public string? ThreadStartApprovalPolicy { get; set; }

    public string? ThreadStartSandbox { get; set; }

    public bool? ThreadStartExperimentalRawEvents { get; set; }

    public bool? ThreadStartPersistExtendedHistory { get; set; }

    public string? TurnStartModel { get; set; }

    public string? TurnStartEffort { get; set; }

    public string? TurnStartRuntimeContextKind { get; set; }

    public string? TurnStartRuntimeContextValue { get; set; }

    public string? ThreadResumeCwd { get; set; }

    public string? ThreadResumeThreadId { get; set; }

    public string? ThreadResumeApprovalPolicy { get; set; }

    public string? ThreadResumeSandbox { get; set; }

    public bool? ThreadResumePersistExtendedHistory { get; set; }

    public bool? ThreadResumeExcludeTurns { get; set; }
}

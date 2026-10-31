using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>File payload expansion belongs to the native caller, never the service identity.</summary>
internal sealed class TlbxMcpFileCallTool : McpServerTool
{
    internal const string ToolName = "tool_call_from_file";
    internal const int MaximumFileBytes = 30 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal static Tool Definition { get; } = new()
    {
        Name = ToolName,
        Description = "Call one named tool once using a caller-local UTF-8 file (optional UTF-8 BOM). argumentsFile contains its JSON arguments object; alternatively textFile supplies the exact literal value of textArgument, merged with arguments. Relative paths use the native caller cwd; Windows paths remain literal. Maximum file size 30 MiB; underlying tool and transport limits still apply. Replaces CLI @file and piped text without copying payloads through the model. MCP stdin remains protocol input. Requires native stdio; HTTP never reads service-account files. Recursive file calls and workflows are rejected.",
        InputSchema = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false, ["required"] = new JsonArray("toolName"),
            ["properties"] = new JsonObject
            {
                ["toolName"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                ["argumentsFile"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                ["textFile"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                ["textArgument"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                ["arguments"] = new JsonObject { ["type"] = "object" }
            },
            ["oneOf"] = new JsonArray(
                new JsonObject { ["required"] = new JsonArray("argumentsFile"),
                    ["not"] = new JsonObject { ["anyOf"] = new JsonArray(
                        new JsonObject { ["required"] = new JsonArray("textFile") },
                        new JsonObject { ["required"] = new JsonArray("textArgument") },
                        new JsonObject { ["required"] = new JsonArray("arguments") }) } },
                new JsonObject { ["required"] = new JsonArray("textFile", "textArgument"),
                    ["not"] = new JsonObject { ["required"] = new JsonArray("argumentsFile") } })
        }, McpJsonContext.Default.JsonObject),
        Annotations = new ToolAnnotations { ReadOnlyHint = false, IdempotentHint = false, OpenWorldHint = true }
    };

    public override Tool ProtocolTool => Definition;
    public override IReadOnlyList<object> Metadata => [];

    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Failure("TLBX_MCP_CLIENT_LOCAL_REQUIRED",
            "File payloads must be read on the caller machine as its OS user. Use mt mcp --stdio.", false));
    }

    internal static async ValueTask<CallToolResult> InvokeLocalAsync(CallToolRequestParams request,
        Func<string, IDictionary<string, JsonElement>, CancellationToken, ValueTask<CallToolResult>> invoke,
        CancellationToken cancellationToken)
    {
        var dispatched = false;
        try
        {
            var input = request.Arguments ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (input.Keys.Any(key => key is not ("toolName" or "argumentsFile" or "textFile" or "textArgument" or "arguments")))
                throw new ArgumentException("Unknown file-call argument.");
            var tool = RequiredString(input, "toolName");
            if (tool is ToolName or TlbxMcpOutputFileTool.ToolName or "workflow_run") throw new ArgumentException("Recursive file/output wrappers and workflows are not allowed. Wrap tool_call_from_file with tool_call_to_file to combine file input and output.");
            var jsonMode = input.ContainsKey("argumentsFile");
            if (jsonMode && input.Keys.Any(key => key is "textFile" or "textArgument" or "arguments"))
                throw new ArgumentException("argumentsFile cannot be combined with textFile, textArgument or arguments.");
            var path = RequiredString(input, jsonMode ? "argumentsFile" : "textFile");
            var argument = jsonMode ? null : RequiredString(input, "textArgument");
            var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (!jsonMode && input.TryGetValue("arguments", out var supplied)) ReadArguments(supplied, arguments);
            if (argument is not null && arguments.ContainsKey(argument))
                throw new ArgumentException("textArgument already exists in arguments; no value was overwritten.");
            cancellationToken.ThrowIfCancellationRequested();
            var text = await ReadUtf8Async(path, cancellationToken).ConfigureAwait(false);
            if (jsonMode)
            {
                using var document = JsonDocument.Parse(text);
                ReadArguments(document.RootElement, arguments);
            }
            else
            {
                var payload = new JsonObject { ["value"] = text };
                var element = JsonSerializer.SerializeToElement(payload, McpJsonContext.Default.JsonObject);
                arguments.Add(argument!, element.GetProperty("value").Clone());
            }
            cancellationToken.ThrowIfCancellationRequested();
            dispatched = true;
            return await invoke(tool, arguments, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return Failure(dispatched ? "TLBX_MCP_FILE_CALL_UNCERTAIN" : "TLBX_MCP_FILE_CALL_NOT_DISPATCHED",
                dispatched ? "The operation was dispatched once before cancellation. Inspect its outcome before any explicit retry."
                    : "File-call preparation was cancelled; no operation was dispatched.", dispatched);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or JsonException or InvalidOperationException or HttpRequestException or McpException)
        {
            return Failure(dispatched ? "TLBX_MCP_FILE_CALL_UNCERTAIN" : "TLBX_MCP_FILE_CALL_NOT_DISPATCHED",
                dispatched ? "The operation was dispatched once and its result is uncertain. Inspect its outcome before any explicit retry."
                    : error.Message, dispatched);
        }
    }

    private static string RequiredString(IDictionary<string, JsonElement> input, string key)
        => input.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString()! : throw new ArgumentException(key + " must be a nonempty string.");

    private static void ReadArguments(JsonElement value, Dictionary<string, JsonElement> arguments)
    {
        if (value.ValueKind != JsonValueKind.Object) throw new ArgumentException("Tool arguments must be a JSON object.");
        using var properties = value.EnumerateObject();
        foreach (var property in properties)
            if (!arguments.TryAdd(property.Name, property.Value.Clone()))
                throw new ArgumentException("Duplicate tool argument: " + property.Name);
    }

    private static async Task<string> ReadUtf8Async(string path, CancellationToken ct)
    {
        await using var file = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read, FileShare.Read,
            65536, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (file.Length > MaximumFileBytes) throw new IOException("File exceeds the 30 MiB payload limit; no operation was dispatched.");
        using var data = new MemoryStream();
        var buffer = new byte[65536];
        int count;
        while ((count = await file.ReadAsync(buffer, ct).ConfigureAwait(false)) != 0)
        {
            if (data.Length + count > MaximumFileBytes) throw new IOException("File exceeds the 30 MiB payload limit; no operation was dispatched.");
            data.Write(buffer, 0, count);
        }
        var bytes = data.GetBuffer().AsSpan(0, checked((int)data.Length));
        return StrictUtf8.GetString(bytes.StartsWith(new byte[] { 0xef, 0xbb, 0xbf }) ? bytes[3..] : bytes);
    }

    private static CallToolResult Failure(string code, string message, bool dispatched) => McpApiResult.Structured(new JsonObject
    {
        ["errorCode"] = code, ["message"] = message, ["dispatched"] = dispatched, ["replaySafe"] = !dispatched
    }, error: true);
}

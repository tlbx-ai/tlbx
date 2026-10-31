using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>CLI-style output redirection without putting the full tool result in model context.</summary>
internal sealed class TlbxMcpOutputFileTool : McpServerTool
{
    internal const string ToolName = "tool_call_to_file";
    internal static Tool Definition { get; } = new()
    {
        Name = ToolName,
        Description = "Call one tool once and save its complete MCP result as JSON to a new caller-local file, returning only path/size/SHA256 and outcome flags. This redirects large terminal buffers, logs and JSON without model truncation. Destination checks happen before dispatch; existing files are never overwritten. A write failure after dispatch never permits replay. May wrap tool_call_from_file for large input and output together; recursive output captures/workflows are rejected. Requires native stdio; HTTP never writes caller files.",
        InputSchema = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["required"] = new JsonArray("toolName", "path"),
            ["properties"] = new JsonObject
            {
                ["toolName"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                ["arguments"] = new JsonObject { ["type"] = "object" },
                ["path"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 }
            }
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
            "Output redirection writes caller files. Use mt mcp --stdio.", false, false));
    }

    internal static async ValueTask<CallToolResult> InvokeLocalAsync(CallToolRequestParams request,
        Func<string, IDictionary<string, JsonElement>, CancellationToken, ValueTask<CallToolResult>> invoke,
        CancellationToken ct)
    {
        var dispatched = false;
        var responseReceived = false;
        string? temporary = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var input = request.Arguments ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (input.Keys.Any(key => key is not ("toolName" or "arguments" or "path"))) throw new ArgumentException("Unknown output-capture argument.");
            string Required(string name) => input.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new ArgumentException(name + " is required.");
            var tool = Required("toolName");
            if (tool is ToolName or "workflow_run") throw new ArgumentException("Recursive output captures and workflows are not allowed.");
            var arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (input.TryGetValue("arguments", out var supplied))
            {
                if (supplied.ValueKind != JsonValueKind.Object) throw new ArgumentException("arguments must be an object.");
                using var properties = supplied.EnumerateObject();
                foreach (var property in properties)
                    if (!arguments.TryAdd(property.Name, property.Value.Clone())) throw new ArgumentException("Duplicate tool argument.");
            }
            var path = Path.GetFullPath(Required("path"));
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Destination already exists; choose a new file path.");
            var directory = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Destination directory does not exist.");
            temporary = Path.Combine(directory, ".tlbx-mcp-" + Guid.NewGuid().ToString("N") + ".partial");
            CallToolResult result;
            long length;
            byte[] hash;
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                ct.ThrowIfCancellationRequested();
                dispatched = true;
                result = await invoke(tool, arguments, ct).ConfigureAwait(false);
                responseReceived = true;
                await JsonSerializer.SerializeAsync(file, result, McpClientJsonContext.Default.CallToolResult, ct).ConfigureAwait(false);
                await file.FlushAsync(ct).ConfigureAwait(false);
                length = file.Length;
                file.Position = 0;
                hash = await SHA256.HashDataAsync(file, ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: false);
            temporary = null;
            return McpApiResult.Structured(new JsonObject
            {
                ["path"] = path, ["byteLength"] = length, ["sha256"] = Convert.ToHexStringLower(hash),
                ["toolName"] = tool, ["toolIsError"] = result.IsError == true, ["saved"] = true,
                ["dispatched"] = true, ["responseReceived"] = true, ["replaySafe"] = false,
                ["executionHost"] = "native-mcp-caller"
            }, result.IsError == true);
        }
        catch (OperationCanceledException)
        { return Failure("TLBX_MCP_OUTPUT_CANCELLED", "Output capture cancelled.", dispatched, responseReceived); }
        catch (Exception error) when (error is ArgumentException or IOException or UnauthorizedAccessException or
            InvalidOperationException or JsonException or NotSupportedException or HttpRequestException or McpException)
        { return Failure("TLBX_MCP_OUTPUT_FAILED", dispatched ? "Output capture failed after dispatch." : error.Message, dispatched, responseReceived); }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { Console.Error.WriteLine("TLBX_MCP_OUTPUT_CLEANUP: " + temporary + ": " + error.Message); }
        }
    }

    private static CallToolResult Failure(string code, string message, bool dispatched, bool received) => McpApiResult.Structured(new JsonObject
    {
        ["errorCode"] = code, ["message"] = message + (dispatched ? " The tool was dispatched once; inspect its outcome before any retry. No destination was published." : " No tool was dispatched."),
        ["saved"] = false, ["dispatched"] = dispatched, ["responseReceived"] = received, ["replaySafe"] = !dispatched
    }, error: true);
}

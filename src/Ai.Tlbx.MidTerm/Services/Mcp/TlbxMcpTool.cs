using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ai.Tlbx.MidTerm.Services.Sessions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>Explicit schemas and dispatch avoid reflection-based serialization in Native AOT.</summary>
internal sealed class TlbxMcpTool : McpServerTool
{
    // Process identity, deliberately independent of version: same-version reinstalls restart too.
    private static readonly string ServerInstanceId = Guid.NewGuid().ToString("N");
    private readonly Tool _tool;
    private TlbxMcpTool(string name, string description)
    {
        _tool = new Tool
        {
            Name = name,
            Description = description,
            InputSchema = JsonSerializer.SerializeToElement(new JsonObject
            {
                ["type"] = "object",
                ["properties"] = name.StartsWith("context_", StringComparison.Ordinal) ? new JsonObject
                {
                    ["previewName"] = new JsonObject { ["type"] = "string", ["maxLength"] = 128 },
                    ["format"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("json", "text", "bash", "pwsh") }
                } : new JsonObject(),
                ["additionalProperties"] = false
            }, McpJsonContext.Default.JsonObject),
            Annotations = new ToolAnnotations { ReadOnlyHint = true, OpenWorldHint = false }
        };
    }

    public override Tool ProtocolTool => _tool;
    public override IReadOnlyList<object> Metadata => [];

    internal static IEnumerable<McpServerTool> CreateTools() =>
    [
        new TlbxMcpTool("context_get", "Get this request's verified tlbx session and server version. Never infer self from cwd or an inherited session ID."),
        new TlbxMcpTool("context_repair", "Revalidate the live process/runtime identity. Native stdio renews its credential before this check. Does not replay the failed operation; retry it explicitly after successful repair."),
        new TlbxMcpTool("context_export", "Export non-secret verified baseUrl/sessionId/previewName as structured JSON or bash/pwsh/text hints for a child shell. Exported session IDs are hints, never authentication or identity proof. Native child MCP connections need their own verified binding."),
        new TlbxMcpTool("sessions_list", "List live tlbx sessions using the same projection as the CLI. Requires full tlbx authentication and a verified caller or explicit operator mode.")
    ];

    public override ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(Invoke(request));
    }

    private CallToolResult Invoke(RequestContext<CallToolRequestParams> request)
    {
        var services = request.Services!;
        var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext;
        // Revalidate at dispatch as well as HTTP ingress, including process/lease changes.
        if (http is null || TlbxMcpEndpoints.Authorize(http,
            services.GetRequiredService<SessionCliContextService>()) is not null)
            return Error("TLBX_MCP_CONTEXT_EXPIRED", "No tool was executed. Obtain a fresh verified context.");
        var arguments = request.Params?.Arguments;
        if (arguments is not null && arguments.Any(pair => pair.Key is not ("previewName" or "format") ||
            !_tool.Name.StartsWith("context_", StringComparison.Ordinal)))
            return Error("TLBX_MCP_INVALID_ARGUMENT", "Unexpected argument; self is determined by the verified context.");
        var sessionId = http.Items[TlbxMcpEndpoints.SessionItem] as string;
        if (_tool.Name.StartsWith("context_", StringComparison.Ordinal))
        {
            var preview = "default";
            if (arguments?.TryGetValue("previewName", out var previewValue) == true)
            {
                if (previewValue.ValueKind != JsonValueKind.String || previewValue.GetString() is not { Length: > 0 and <= 128 } name)
                    return Error("TLBX_MCP_INVALID_ARGUMENT", "previewName must contain 1-128 characters.");
                preview = name;
            }
            var format = "json";
            if (arguments?.TryGetValue("format", out var formatValue) == true)
            {
                if (formatValue.ValueKind != JsonValueKind.String || formatValue.GetString() is not ("json" or "text" or "bash" or "pwsh"))
                    return Error("TLBX_MCP_INVALID_ARGUMENT", "format must be json, text, bash or pwsh.");
                format = formatValue.GetString()!;
            }
            var baseUrl = http.Request.Scheme + "://" + http.Request.Host + http.Request.PathBase;
            var result = new JsonObject { ["sessionId"] = sessionId,
                ["baseUrl"] = baseUrl, ["previewName"] = preview,
                ["serverVersion"] = Startup.CliCommands.GetVersion(),
                ["serverInstanceId"] = ServerInstanceId,
                ["bindingMode"] = http.Items[TlbxMcpEndpoints.CallerModeItem] as string,
                ["experimental"] = true };
            if (_tool.Name == "context_repair") result["repair"] = "verified";
            if (format != "json") result["export"] = ExportContext(baseUrl, sessionId, preview, format);
            return Structured(result);
        }
        if (_tool.Name == "sessions_list")
        {
            var sessions = SessionApiEndpoints.GetSessionListDto(
                services.GetRequiredService<TtyHostSessionManager>(),
                services.GetRequiredService<SessionSupervisorService>(),
                services.GetRequiredService<SessionAppServerControlRuntimeService>());
            return Structured(JsonSerializer.SerializeToElement(sessions, AppJsonContext.Default.SessionListDto));
        }

        return Error("TLBX_MCP_UNKNOWN_TOOL", "Unknown context operation.");
    }

    internal static string ExportContext(string baseUrl, string? sessionId, string preview, string format)
    {
        static string Bash(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";
        static string Pwsh(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
        return format switch
        {
            "bash" => "export MT_BASE_URL=" + Bash(baseUrl) + "; export MT_SESSION_ID=" + Bash(sessionId ?? "") + "; export MT_PREVIEW_NAME=" + Bash(preview),
            "pwsh" => "$env:MT_BASE_URL=" + Pwsh(baseUrl) + "; $env:MT_SESSION_ID=" + Pwsh(sessionId ?? "") + "; $env:MT_PREVIEW_NAME=" + Pwsh(preview),
            "text" => "baseUrl=" + baseUrl + "\nsessionId=" + sessionId + "\npreviewName=" + preview,
            _ => throw new ArgumentException("Unsupported context export format.", nameof(format))
        };
    }

    internal static CallToolResult Screenshot(string? dataUrl)
    {
        const string prefix = "data:image/png;base64,";
        if (dataUrl is null || !dataUrl.StartsWith(prefix, StringComparison.Ordinal) || dataUrl.Length > 16_000_000)
            return Error("TLBX_MCP_INVALID_IMAGE", "Expected a bounded PNG data URL from the browser.");
        try
        {
            var bytes = Convert.FromBase64String(dataUrl[prefix.Length..]);
            if (!bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }))
                return Error("TLBX_MCP_INVALID_IMAGE", "Browser result is not a PNG.");
            return new CallToolResult { Content = [ImageContentBlock.FromBytes(bytes, "image/png")] };
        }
        catch (FormatException) { return Error("TLBX_MCP_INVALID_IMAGE", "Browser returned invalid base64."); }
    }

    private static CallToolResult Structured(JsonObject value) => Structured(JsonSerializer.SerializeToElement(value, McpJsonContext.Default.JsonObject));
    private static CallToolResult Structured(JsonElement value) => new()
    {
        StructuredContent = value,
        Content = [new TextContentBlock { Text = value.GetRawText() }]
    };
    private static CallToolResult Error(string code, string message) => new()
    {
        IsError = true,
        Content = [new TextContentBlock { Text = code + ": " + message }]
    };
}

[JsonSerializable(typeof(JsonObject))]
internal sealed partial class McpJsonContext : JsonSerializerContext;

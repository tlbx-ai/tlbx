using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Ai.Tlbx.MidTerm.Models.Browser;
using Ai.Tlbx.MidTerm.Services.Browser;
using Ai.Tlbx.MidTerm.Services.Sessions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>Explicit schemas and dispatch avoid reflection-based serialization in Native AOT.</summary>
internal sealed class TlbxMcpTool : McpServerTool
{
    private readonly Tool _tool;
    private TlbxMcpTool(string name, string description, bool browser = false)
    {
        _tool = new Tool
        {
            Name = name,
            Description = description,
            InputSchema = JsonSerializer.SerializeToElement(new JsonObject
            {
                ["type"] = "object",
                ["properties"] = browser ? new JsonObject
                {
                    ["previewName"] = new JsonObject { ["type"] = "string", ["maxLength"] = 128 }
                } : new JsonObject(),
                ["additionalProperties"] = false
            }, McpJsonContext.Default.JsonObject),
            Annotations = new ToolAnnotations { ReadOnlyHint = true, OpenWorldHint = browser }
        };
    }

    public override Tool ProtocolTool => _tool;
    public override IReadOnlyList<object> Metadata => [];

    internal static IEnumerable<McpServerTool> CreateTools() =>
    [
        new TlbxMcpTool("context_get", "Get this request's verified tlbx session and server version. Never infer self from cwd or an inherited session ID."),
        new TlbxMcpTool("sessions_list", "List live tlbx sessions using the same projection as the CLI. Requires full tlbx authentication and a verified calling context."),
        new TlbxMcpTool("browser_snapshot", "Read the existing Dev Browser DOM snapshot for this verified session. Does not open or claim another browser.", true),
        new TlbxMcpTool("browser_screenshot", "Capture the existing Dev Browser for this verified session as an MCP image. Does not open or claim another browser.", true)
    ];

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        var services = request.Services!;
        var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext;
        // Revalidate at dispatch as well as HTTP ingress, including process/lease changes.
        if (http is null || TlbxMcpEndpoints.Authorize(http,
            services.GetRequiredService<SessionCliContextService>()) is not null)
            return Error("TLBX_MCP_CONTEXT_EXPIRED", "No tool was executed. Obtain a fresh verified context.");
        var arguments = request.Params?.Arguments;
        if (arguments is not null && arguments.Any(pair => pair.Key != "previewName" ||
            !_tool.Name.StartsWith("browser_", StringComparison.Ordinal)))
            return Error("TLBX_MCP_INVALID_ARGUMENT", "Unexpected argument; self is determined by the verified context.");
        var sessionId = (string)http.Items[TlbxMcpEndpoints.SessionItem]!;
        if (_tool.Name == "context_get")
            return Structured(new JsonObject { ["sessionId"] = sessionId,
                ["serverVersion"] = Startup.CliCommands.GetVersion(), ["experimental"] = true });
        if (_tool.Name == "sessions_list")
        {
            var sessions = SessionApiEndpoints.GetSessionListDto(
                services.GetRequiredService<TtyHostSessionManager>(),
                services.GetRequiredService<SessionSupervisorService>(),
                services.GetRequiredService<SessionAppServerControlRuntimeService>());
            return Structured(JsonSerializer.SerializeToElement(sessions, AppJsonContext.Default.SessionListDto));
        }

        string? preview = null;
        if (arguments?.TryGetValue("previewName", out var value) == true)
        {
            if (value.ValueKind != JsonValueKind.String || value.GetString() is not { Length: > 0 and <= 128 } name)
                return Error("TLBX_MCP_INVALID_ARGUMENT", "previewName must contain 1-128 characters.");
            preview = name;
        }
        var result = await services.GetRequiredService<BrowserCommandService>().ExecuteCommandAsync(
            new BrowserCommandRequest { Command = _tool.Name == "browser_screenshot" ? "screenshot" : "snapshot",
                SessionId = sessionId, PreviewName = preview, Timeout = 15 }, cancellationToken);
        if (!result.Success) return Error("TLBX_MCP_BROWSER_FAILED", result.Error ?? "Browser command failed.");
        if (_tool.Name == "browser_screenshot") return Screenshot(result.Result);
        var text = result.Result ?? "";
        const int maximumTextLength = 100_000;
        return Structured(new JsonObject { ["sessionId"] = sessionId, ["previewName"] = preview,
            ["snapshot"] = text[..Math.Min(text.Length, maximumTextLength)], ["truncated"] = text.Length > maximumTextLength });
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

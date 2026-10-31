using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ai.Tlbx.MidTerm.Services.Sessions;
using ModelContextProtocol;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>Resource access through tools for clients that attach thread identity only to tools/call.</summary>
internal sealed class TlbxMcpArtifactTools(bool read) : McpServerTool
{
    private readonly Tool _definition = new()
    {
        Name = read ? "artifact_read" : "artifacts_list",
        Description = read
            ? "Read a caller-owned generated artifact as an embedded resource. Accepts only an opaque URI returned by tlbx, never a file path. Optional byte offset/length supports complete large originals in chunks up to 4 MiB."
            : "List this exact authenticated caller's generated artifacts, including complete size, expiry and opaque URI. No sibling thread or arbitrary workspace file access.",
        InputSchema = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["required"] = read ? new JsonArray("uri") : new JsonArray(),
            ["properties"] = read ? new JsonObject
            {
                ["uri"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                ["offset"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
                ["length"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 4194304 }
            } : new JsonObject()
        }, McpJsonContext.Default.JsonObject),
        Annotations = new ToolAnnotations { ReadOnlyHint = true, OpenWorldHint = false }
    };

    public override Tool ProtocolTool => _definition;
    public override IReadOnlyList<object> Metadata => [];
    internal static IEnumerable<McpServerTool> CreateTools() => [new TlbxMcpArtifactTools(false), new TlbxMcpArtifactTools(true)];

    public override ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var services = request.Services!;
        var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext;
        if (http is null || TlbxMcpEndpoints.Authorize(http, services.GetRequiredService<SessionCliContextService>()) is not null)
            return ValueTask.FromResult(McpApiResult.Error("TLBX_MCP_CONTEXT_REQUIRED", "A verified caller is required to access artifacts."));
        try
        {
            var store = services.GetRequiredService<TlbxMcpArtifactStore>();
            var arguments = request.Params?.Arguments;
            if (arguments?.Keys.Any(key => !read || key is not ("uri" or "offset" or "length")) == true)
                throw new ArgumentException("Unknown artifact argument.");
            if (!read)
            {
                var resources = new JsonArray();
                foreach (var resource in store.List(cancellationToken).Resources)
                    resources.Add((JsonNode)new JsonObject { ["uri"] = resource.Uri, ["name"] = resource.Name,
                        ["mimeType"] = resource.MimeType, ["size"] = resource.Size, ["description"] = resource.Description });
                return ValueTask.FromResult(McpApiResult.Structured(new JsonObject { ["resources"] = resources }));
            }
            if (arguments is null || !arguments.TryGetValue("uri", out var uriValue) || uriValue.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(uriValue.GetString()))
                throw new ArgumentException("uri must be an opaque artifact URI returned by tlbx.");
            var uri = uriValue.GetString()!;
            if (arguments.ContainsKey("offset") || arguments.ContainsKey("length"))
            {
                if (uri.Contains('?', StringComparison.Ordinal)) throw new ArgumentException("Supply either URI query or offset/length fields.");
                var offset = Integer("offset", 0, int.MaxValue, 0);
                var length = Integer("length", 1, 4194304, 4194304);
                uri += "?offset=" + offset.ToString(CultureInfo.InvariantCulture) + "&length=" + length.ToString(CultureInfo.InvariantCulture);
            }
            var result = store.Read(uri, cancellationToken);
            return ValueTask.FromResult(new CallToolResult
            {
                Content = result.Contents.Select(content => (ContentBlock)new EmbeddedResourceBlock { Resource = content }).ToList()
            });

            int Integer(string key, int minimum, int maximum, int fallback)
            {
                if (!arguments.TryGetValue(key, out var value)) return fallback;
                if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out var number) || number < minimum || number > maximum)
                    throw new ArgumentException(key + " is outside the supported byte range.");
                return number;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or McpProtocolException)
        {
            return ValueTask.FromResult(McpApiResult.Error("TLBX_MCP_ARTIFACT_UNAVAILABLE", ex.Message));
        }
    }
}

using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ai.Tlbx.MidTerm.Services.Sessions;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>
/// Two-tier MCP catalog: exposes a compact Tier-1 tool surface (&lt; 5,000 tokens)
/// with a dense domain capability index while keeping all subtools discoverable
/// on demand via <c>tools_discover</c> and callable via <c>tool_call</c>,
/// <c>workflow_run</c>, or direct <c>tools/call</c>.
/// </summary>
internal sealed class TlbxMcpCatalog
{
    private static readonly string[] DomainOrder =
    [
        "context",
        "sessions",
        "terminal",
        "agent",
        "repos",
        "browser",
        "boards",
        "control_plane",
        "queue",
        "history",
        "artifacts",
        "local",
        "updates",
        "workflows",
        "other"
    ];

    private static readonly HashSet<string> Tier1DirectNames = new(StringComparer.Ordinal)
    {
        "context_get",
        "sessions_list",
        "session_set_topic",
        "repos_list",
        "repo_bind",
        "agent_session_create",
        "agent_history",
        "agent_turn",
        "terminal_prompt",
        "workflow_run"
    };

    private readonly Dictionary<string, CatalogEntry> _entries;
    private readonly IReadOnlyList<CatalogEntry> _orderedEntries;
    private readonly Dictionary<string, McpServerTool> _allCallable;
    private readonly IReadOnlyList<Tool> _tier1Tools;

    internal TlbxMcpCatalog(IEnumerable<McpServerTool> subtools)
    {
        var list = subtools.ToList();
        var entries = new List<CatalogEntry>(list.Count);
        foreach (var tool in list)
        {
            var name = tool.ProtocolTool.Name;
            var domain = ClassifyDomain(name);
            var tier = Tier1DirectNames.Contains(name) ? 1 : 2;
            entries.Add(new CatalogEntry(name, domain, tier, tool));
        }

        _orderedEntries = entries
            .OrderBy(entry => DomainSortIndex(entry.Domain))
            .ThenBy(entry => entry.Name, StringComparer.Ordinal)
            .ToArray();
        _entries = _orderedEntries.ToDictionary(entry => entry.Name, StringComparer.Ordinal);

        var discoverTool = new TlbxMcpDiscoverTool(this, BuildCapabilityIndex(_orderedEntries));
        var callTool = new TlbxMcpCallTool(this);

        _allCallable = new Dictionary<string, McpServerTool>(StringComparer.Ordinal)
        {
            [TlbxMcpDiscoverTool.ToolName] = discoverTool,
            [TlbxMcpCallTool.ToolName] = callTool
        };
        foreach (var entry in _orderedEntries)
        {
            _allCallable[entry.Name] = entry.ServerTool;
        }

        var tier1 = new List<Tool>(Tier1DirectNames.Count + 2)
        {
            discoverTool.ProtocolTool,
            callTool.ProtocolTool
        };
        foreach (var name in new[]
        {
            "context_get",
            "sessions_list",
            "session_set_topic",
            "repos_list",
            "repo_bind",
            "agent_session_create",
            "agent_history",
            "agent_turn",
            "terminal_prompt",
            "workflow_run"
        })
        {
            if (_entries.TryGetValue(name, out var entry))
            {
                tier1.Add(entry.ServerTool.ProtocolTool);
            }
        }
        _tier1Tools = tier1;
    }

    internal IReadOnlyList<Tool> Tier1Tools => _tier1Tools;
    internal IReadOnlyList<CatalogEntry> Subtools => _orderedEntries;
    internal static IReadOnlyList<string> Domains => DomainOrder;

    internal bool TryGetCallable(string name, out McpServerTool tool) => _allCallable.TryGetValue(name, out tool!);
    internal bool TryGetSubtool(string name, out CatalogEntry entry) => _entries.TryGetValue(name, out entry!);

    internal static string ClassifyDomain(string name)
    {
        if (name.StartsWith("context_", StringComparison.Ordinal)) return "context";
        if (name.StartsWith("session_", StringComparison.Ordinal) || name.StartsWith("sessions_", StringComparison.Ordinal) || name.StartsWith("tmux_", StringComparison.Ordinal)) return "sessions";
        if (name.StartsWith("terminal_", StringComparison.Ordinal)) return "terminal";
        if (name.StartsWith("agent_", StringComparison.Ordinal)) return "agent";
        if (name.StartsWith("repo_", StringComparison.Ordinal) || name.StartsWith("repos_", StringComparison.Ordinal)) return "repos";
        if (name.StartsWith("browser_", StringComparison.Ordinal)) return "browser";
        if (name.StartsWith("board_", StringComparison.Ordinal) || name.StartsWith("boards_", StringComparison.Ordinal)) return "boards";
        if (name.StartsWith("control_", StringComparison.Ordinal) || name.StartsWith("work_", StringComparison.Ordinal)
            || name.StartsWith("status_", StringComparison.Ordinal) || name.StartsWith("checkpoint", StringComparison.Ordinal)
            || name.StartsWith("notification_", StringComparison.Ordinal)) return "control_plane";
        if (name.StartsWith("queue_", StringComparison.Ordinal)) return "queue";
        if (name.StartsWith("input_history_", StringComparison.Ordinal)) return "history";
        if (name.StartsWith("artifact", StringComparison.Ordinal)) return "artifacts";
        if (name.StartsWith("process_", StringComparison.Ordinal) || name is TlbxMcpFileCallTool.ToolName or TlbxMcpOutputFileTool.ToolName) return "local";
        if (name.StartsWith("update_", StringComparison.Ordinal)) return "updates";
        if (name.StartsWith("workflow_", StringComparison.Ordinal)) return "workflows";
        return "other";
    }

    private static int DomainSortIndex(string domain)
    {
        var index = Array.IndexOf(DomainOrder, domain);
        return index >= 0 ? index : DomainOrder.Length;
    }

    private static string BuildCapabilityIndex(IReadOnlyList<CatalogEntry> entries)
    {
        var sb = new StringBuilder(2600);
        sb.Append("Discover exact schemas for tlbx subtools and invoke them via tool_call (or workflow_run). Direct Tier-1 tools need no discovery. Subtools by domain:");
        foreach (var group in entries.GroupBy(entry => entry.Domain, StringComparer.Ordinal))
        {
            sb.Append("\n- ");
            sb.Append(group.Key);
            sb.Append(": ");
            var first = true;
            foreach (var item in group)
            {
                if (!first) sb.Append(", ");
                sb.Append(item.Name);
                first = false;
            }
        }
        return sb.ToString();
    }

    internal sealed record CatalogEntry(string Name, string Domain, int Tier, McpServerTool ServerTool);
}

/// <summary>Tier-1 discovery tool returning on-demand schemas and summaries for Tier-2 subtools.</summary>
internal sealed class TlbxMcpDiscoverTool : McpServerTool
{
    internal const string ToolName = "tools_discover";
    private readonly TlbxMcpCatalog _catalog;
    private readonly Tool _definition;

    internal TlbxMcpDiscoverTool(TlbxMcpCatalog catalog, string capabilityDescription)
    {
        _catalog = catalog;
        var domainEnum = new JsonArray();
        foreach (var domain in TlbxMcpCatalog.Domains)
        {
            if (domain != "other") domainEnum.Add(domain);
        }
        _definition = new Tool
        {
            Name = ToolName,
            Description = capabilityDescription,
            InputSchema = JsonSerializer.SerializeToElement(new JsonObject
            {
                ["type"] = "object",
                ["additionalProperties"] = false,
                ["properties"] = new JsonObject
                {
                    ["domain"] = new JsonObject { ["type"] = "string", ["enum"] = domainEnum },
                    ["query"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 128 },
                    ["tools"] = new JsonObject
                    {
                        ["type"] = "array",
                        ["maxItems"] = 64,
                        ["items"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 }
                    },
                    ["includeSchemas"] = new JsonObject { ["type"] = "boolean" }
                }
            }, McpJsonContext.Default.JsonObject),
            Annotations = new ToolAnnotations { ReadOnlyHint = true, IdempotentHint = true, OpenWorldHint = false }
        };
    }

    public override Tool ProtocolTool => _definition;
    public override IReadOnlyList<object> Metadata => [];

    public override ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var services = request.Services!;
        var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext;
        if (http is null || TlbxMcpEndpoints.Authorize(http, services.GetRequiredService<SessionCliContextService>()) is not null)
            return ValueTask.FromResult(McpApiResult.Error("TLBX_MCP_CONTEXT_EXPIRED", "Obtain a fresh verified context before discovering subtools."));

        var args = request.Params?.Arguments;
        if (args is not null && args.Keys.Any(k => k is not ("domain" or "query" or "tools" or "includeSchemas")))
            return ValueTask.FromResult(McpApiResult.Error("TLBX_MCP_INVALID_ARGUMENT", "Unknown argument. Allowed: domain, query, tools, includeSchemas."));

        string? domain = null;
        if (args?.TryGetValue("domain", out var domainVal) == true && domainVal.ValueKind != JsonValueKind.Null)
        {
            if (domainVal.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(domainVal.GetString()))
                return ValueTask.FromResult(McpApiResult.Error("TLBX_MCP_INVALID_ARGUMENT", "domain must be a nonempty string."));
            domain = domainVal.GetString()!.Trim().ToLowerInvariant();
        }

        string? query = null;
        if (args?.TryGetValue("query", out var queryVal) == true && queryVal.ValueKind != JsonValueKind.Null)
        {
            if (queryVal.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(queryVal.GetString()))
                return ValueTask.FromResult(McpApiResult.Error("TLBX_MCP_INVALID_ARGUMENT", "query must be a nonempty string."));
            query = queryVal.GetString()!.Trim();
        }

        List<string>? requestedTools = null;
        if (args?.TryGetValue("tools", out var toolsVal) == true && toolsVal.ValueKind != JsonValueKind.Null)
        {
            if (toolsVal.ValueKind != JsonValueKind.Array || toolsVal.GetArrayLength() > 64)
                return ValueTask.FromResult(McpApiResult.Error("TLBX_MCP_INVALID_ARGUMENT", "tools must be an array of at most 64 tool names."));
            requestedTools = [];
            for (var index = 0; index < toolsVal.GetArrayLength(); index++)
            {
                var element = toolsVal[index];
                if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
                    return ValueTask.FromResult(McpApiResult.Error("TLBX_MCP_INVALID_ARGUMENT", "Every entry in tools must be a nonempty string."));
                requestedTools.Add(element.GetString()!.Trim());
            }
        }

        bool? explicitIncludeSchemas = null;
        if (args?.TryGetValue("includeSchemas", out var includeVal) == true && includeVal.ValueKind != JsonValueKind.Null)
        {
            if (includeVal.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                return ValueTask.FromResult(McpApiResult.Error("TLBX_MCP_INVALID_ARGUMENT", "includeSchemas must be a boolean."));
            explicitIncludeSchemas = includeVal.GetBoolean();
        }

        var hasFilter = domain is not null || query is not null || (requestedTools is { Count: > 0 });
        var includeSchemas = explicitIncludeSchemas ?? hasFilter;

        var matched = new List<TlbxMcpCatalog.CatalogEntry>();
        var unknownTools = new JsonArray();

        if (requestedTools is { Count: > 0 })
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in requestedTools)
            {
                if (!seen.Add(name)) continue;
                if (_catalog.TryGetSubtool(name, out var entry))
                {
                    if ((domain is null || string.Equals(entry.Domain, domain, StringComparison.Ordinal))
                        && MatchesQuery(entry, query))
                    {
                        matched.Add(entry);
                    }
                }
                else
                {
                    unknownTools.Add(name);
                }
            }
        }
        else
        {
            foreach (var entry in _catalog.Subtools)
            {
                if (domain is not null && !string.Equals(entry.Domain, domain, StringComparison.Ordinal))
                    continue;
                if (!MatchesQuery(entry, query))
                    continue;
                matched.Add(entry);
            }
        }

        var items = new JsonArray();
        foreach (var entry in matched)
        {
            var protocol = entry.ServerTool.ProtocolTool;
            var obj = new JsonObject
            {
                ["name"] = entry.Name,
                ["domain"] = entry.Domain,
                ["tier"] = entry.Tier,
                ["description"] = protocol.Description ?? ""
            };
            if (includeSchemas)
            {
                obj["inputSchema"] = JsonNode.Parse(protocol.InputSchema.GetRawText());
                if (protocol.Annotations is { } annotations)
                {
                    var ann = new JsonObject();
                    if (annotations.ReadOnlyHint.HasValue) ann["readOnlyHint"] = annotations.ReadOnlyHint.Value;
                    if (annotations.DestructiveHint.HasValue) ann["destructiveHint"] = annotations.DestructiveHint.Value;
                    if (annotations.IdempotentHint.HasValue) ann["idempotentHint"] = annotations.IdempotentHint.Value;
                    if (annotations.OpenWorldHint.HasValue) ann["openWorldHint"] = annotations.OpenWorldHint.Value;
                    if (ann.Count > 0) obj["annotations"] = ann;
                }
            }
            items.Add(obj);
        }

        var response = new JsonObject
        {
            ["totalSubtools"] = _catalog.Subtools.Count,
            ["matchedCount"] = matched.Count,
            ["includeSchemas"] = includeSchemas,
            ["tools"] = items
        };
        if (unknownTools.Count > 0)
        {
            response["unknownTools"] = unknownTools;
        }
        return ValueTask.FromResult(McpApiResult.Structured(response));
    }

    private static bool MatchesQuery(TlbxMcpCatalog.CatalogEntry entry, string? query)
    {
        if (string.IsNullOrWhiteSpace(query)) return true;
        var tokens = query.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var description = entry.ServerTool.ProtocolTool.Description ?? string.Empty;
        foreach (var token in tokens)
        {
            if (!entry.Name.Contains(token, StringComparison.OrdinalIgnoreCase)
                && !entry.Domain.Contains(token, StringComparison.OrdinalIgnoreCase)
                && !description.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }
        return true;
    }
}

/// <summary>Tier-1 gateway tool that invokes any discovered Tier-2 (or Tier-1) subtool with exact caller scope.</summary>
internal sealed class TlbxMcpCallTool : McpServerTool
{
    internal const string ToolName = "tool_call";
    private readonly TlbxMcpCatalog _catalog;
    private static readonly Tool Definition = new()
    {
        Name = ToolName,
        Description = "Invoke any tlbx subtool listed in tools_discover by exact name and JSON arguments object. Preserves verified caller scope, validation, and single-dispatch semantics.",
        InputSchema = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object",
            ["additionalProperties"] = false,
            ["required"] = new JsonArray("tool"),
            ["properties"] = new JsonObject
            {
                ["tool"] = new JsonObject { ["type"] = "string", ["minLength"] = 1, ["description"] = "Exact subtool name from tools_discover." },
                ["arguments"] = new JsonObject { ["type"] = "object", ["description"] = "JSON arguments object matching the subtool inputSchema." }
            }
        }, McpJsonContext.Default.JsonObject),
        Annotations = new ToolAnnotations { ReadOnlyHint = false, IdempotentHint = false, OpenWorldHint = true }
    };

    internal TlbxMcpCallTool(TlbxMcpCatalog catalog)
    {
        _catalog = catalog;
    }

    public override Tool ProtocolTool => Definition;
    public override IReadOnlyList<object> Metadata => [];

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var services = request.Services!;
        var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext;
        if (http is null || TlbxMcpEndpoints.Authorize(http, services.GetRequiredService<SessionCliContextService>()) is not null)
            return McpApiResult.Error("TLBX_MCP_CONTEXT_EXPIRED", "No subtool was executed. Obtain a fresh verified context.");

        var args = request.Params?.Arguments;
        if (args is null || !args.TryGetValue("tool", out var toolVal) || toolVal.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(toolVal.GetString()))
            return McpApiResult.Error("TLBX_MCP_INVALID_ARGUMENT", "tool must be a nonempty subtool name.");
        if (args.Keys.Any(k => k is not ("tool" or "arguments")))
            return McpApiResult.Error("TLBX_MCP_INVALID_ARGUMENT", "Unknown argument. Allowed: tool, arguments.");

        var subtoolName = toolVal.GetString()!.Trim();
        if (string.Equals(subtoolName, ToolName, StringComparison.Ordinal))
            return McpApiResult.Error("TLBX_MCP_INVALID_ARGUMENT", "Recursive tool_call is not allowed.");
        if (!_catalog.TryGetCallable(subtoolName, out var target))
            return McpApiResult.Error("TLBX_MCP_UNKNOWN_TOOL", "Unknown tlbx subtool '" + subtoolName + "'. Call tools_discover to inspect available subtools.");

        var subArgs = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        if (args.TryGetValue("arguments", out var rawArgs) && rawArgs.ValueKind != JsonValueKind.Null)
        {
            if (rawArgs.ValueKind != JsonValueKind.Object)
                return McpApiResult.Error("TLBX_MCP_INVALID_ARGUMENT", "arguments must be a JSON object.");
            using var properties = rawArgs.EnumerateObject();
            foreach (var property in properties)
            {
                subArgs[property.Name] = property.Value.Clone();
            }
        }

        var childContext = new JsonRpcMessageContext
        {
            User = request.User,
            Items = new Dictionary<string, object?>(StringComparer.Ordinal)
        };
        foreach (var item in request.Items) childContext.Items[item.Key] = item.Value;
        var childMessage = new JsonRpcRequest
        {
            Id = request.JsonRpcRequest.Id,
            Method = "tools/call",
            Context = childContext
        };
        var childRequest = new RequestContext<CallToolRequestParams>(
            request.Server,
            childMessage,
            new CallToolRequestParams { Name = subtoolName, Arguments = subArgs })
        {
            Services = services,
            MatchedPrimitive = target
        };
        return await target.InvokeAsync(childRequest, cancellationToken).ConfigureAwait(false);
    }
}

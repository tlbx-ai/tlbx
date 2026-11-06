using System.Text.Json;
using System.Text.Json.Nodes;
using Ai.Tlbx.MidTerm.Services.Sessions;
using Ai.Tlbx.MidTerm.Services.Tmux;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>Typed split-window dispatch through the existing tmux lifecycle and layout handler.</summary>
internal sealed class TlbxMcpTmuxTool : McpServerTool
{
    private static readonly Tool Definition = new()
    {
        Name = "tmux_split",
        Description = "Create a terminal pane beside verified self or an explicit targetSessionId/targetPane. Inherits target dimensions, records its tmux parent and requests right docking when horizontal=true, otherwise bottom docking. Optional command is exact shell text when arguments are omitted; with arguments, existing tmux argument quoting is preserved. No shell or CLI subprocess is used for orchestration.",
        InputSchema = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["properties"] = new JsonObject
            {
                ["targetSessionId"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                ["targetPane"] = new JsonObject { ["type"] = "string", ["minLength"] = 1,
                    ["description"] = "Explicit tmux pane selector, for example %3 or session:window.3; mutually exclusive with targetSessionId." },
                ["horizontal"] = new JsonObject { ["type"] = "boolean", ["default"] = false },
                ["workingDirectory"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                ["command"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
                ["arguments"] = new JsonObject { ["type"] = "array", ["maxItems"] = 256,
                    ["items"] = new JsonObject { ["type"] = "string" } }
            }
        }, McpJsonContext.Default.JsonObject),
        Annotations = new ToolAnnotations { ReadOnlyHint = false, OpenWorldHint = true }
    };

    public override Tool ProtocolTool => Definition;
    public override IReadOnlyList<object> Metadata => [];
    internal static IEnumerable<McpServerTool> CreateTools() => [new TlbxMcpTmuxTool()];

    internal static TmuxCommandParser.ParsedCommand Prepare(IReadOnlyDictionary<string, JsonElement> arguments, string? self)
    {
        if (arguments.Keys.Any(key => key is not ("targetSessionId" or "targetPane" or "horizontal" or "workingDirectory" or "command" or "arguments")))
            throw new ArgumentException("Unknown split argument.");
        string? Text(string key)
        {
            if (!arguments.TryGetValue(key, out var value)) return null;
            if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
                throw new ArgumentException(key + " must be a nonempty string.");
            return value.GetString();
        }
        var session = Text("targetSessionId");
        var pane = Text("targetPane");
        if (session is not null && pane is not null) throw new ArgumentException("Use either targetSessionId or targetPane.");
        var target = pane ?? session ?? self;
        if (string.IsNullOrEmpty(target)) throw new ArgumentException("An unbound operator must specify targetSessionId or targetPane.");
        var command = new TmuxCommandParser.ParsedCommand { Name = "split-window" };
        command.Flags["-t"] = target;
        if (arguments.TryGetValue("horizontal", out var horizontal))
        {
            if (horizontal.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new ArgumentException("horizontal must be a boolean.");
            if (horizontal.GetBoolean()) command.Flags["-h"] = null;
        }
        if (Text("workingDirectory") is { } directory) command.Flags["-c"] = directory;
        if (Text("command") is { } initialCommand) command.Positional.Add(initialCommand);
        if (arguments.TryGetValue("arguments", out var extra))
        {
            if (extra.ValueKind != JsonValueKind.Array || extra.GetArrayLength() > 256 || command.Positional.Count == 0)
                throw new ArgumentException("arguments requires command and at most 256 string values.");
            using var entries = extra.EnumerateArray();
            foreach (var value in entries)
            {
                if (value.ValueKind != JsonValueKind.String) throw new ArgumentException("arguments must contain strings.");
                command.Positional.Add(value.GetString()!);
            }
        }
        return command;
    }

    public override async ValueTask<CallToolResult> InvokeAsync(RequestContext<CallToolRequestParams> request,
        CancellationToken cancellationToken = default)
    {
        var services = request.Services!;
        var http = services.GetRequiredService<IHttpContextAccessor>().HttpContext;
        if (http is null || !RequestAccessContext.HasFullUserAccess(http))
            return McpApiResult.Error("TLBX_MCP_AUTH_REQUIRED", "Full tlbx access is required.");
        var denied = TlbxMcpEndpoints.Authorize(http, services.GetRequiredService<SessionCliContextService>());
        if (denied is not null) return McpApiResult.Error("TLBX_MCP_CONTEXT_INVALID", denied.Value.Message);
        try
        {
            var self = http.Items[TlbxMcpEndpoints.SessionItem] as string;
            var arguments = request.Params?.Arguments?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            var command = Prepare(arguments, self);
            var backend = services.GetService<TlbxMcpTmuxRuntime>()?.Current;
            if (backend is null) return McpApiResult.Error("TLBX_MCP_TMUX_UNAVAILABLE", "Enable the existing tmux compatibility setting to split panes.");
            var mapper = backend.Mapper;
            var callerPane = self is null ? null : mapper.SessionIdToPaneId(self);
            var targetSession = backend.Resolver.ResolveToSessionId(command.GetFlag("-t"), callerPane);
            if (targetSession is null || services.GetRequiredService<TtyHostSessionManager>().GetSession(targetSession) is null)
                return McpApiResult.Error("TLBX_MCP_TARGET_NOT_FOUND", "The target pane does not exist.");
            var result = await backend.Dispatcher
                .DispatchAsync([command], callerPane, cancellationToken).ConfigureAwait(false);
            if (!result.Success) return McpApiResult.Error("TLBX_MCP_SPLIT_FAILED", result.Output);
            var paneId = result.Output.Trim();
            return McpApiResult.Structured(new JsonObject
            {
                ["success"] = true, ["paneId"] = paneId, ["sessionId"] = mapper.PaneIdToSessionId(paneId),
                ["parentSessionId"] = targetSession, ["horizontal"] = command.HasFlag("-h")
            });
        }
        catch (ArgumentException error) { return McpApiResult.Error("TLBX_MCP_INVALID_ARGUMENT", error.Message); }
    }
}

/// <summary>References the same optional backend configured by the existing tmux setting.</summary>
internal sealed class TlbxMcpTmuxRuntime
{
    internal sealed record Backend(TmuxCommandDispatcher Dispatcher, TmuxPaneMapper Mapper, TmuxTargetResolver Resolver);
    internal Backend? Current { get; private set; }
    internal void Configure(TmuxCommandDispatcher dispatcher, TmuxPaneMapper mapper, TmuxTargetResolver resolver) =>
        Current = new Backend(dispatcher, mapper, resolver);
}

using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>Observes authenticated responses; never reloads code or retries a tool.</summary>
internal sealed class McpStdioFreshness(string adapterVersion)
{
    internal const string VersionHeader = "X-Tlbx-Mcp-Server-Version";
    internal const string InstanceHeader = "X-Tlbx-Mcp-Server-Instance";
    private readonly Lock _gate = new();
    private string? _version;
    private string? _instance;
    private McpServer? _downstream;

    internal void Initialize(string version)
    {
        lock (_gate) _version ??= version;
    }

    internal void Attach(McpServer downstream)
    {
        lock (_gate) _downstream = downstream;
    }

    internal IAsyncDisposable RelayNotifications(McpClient upstream) => upstream.RegisterNotificationHandler(
        NotificationMethods.ToolListChangedNotification, (_, ct) => NotifyToolsChangedAsync(ct));

    internal async ValueTask ObserveAsync(HttpResponseMessage response, CancellationToken ct)
    {
        if (!response.IsSuccessStatusCode
            || !response.Headers.TryGetValues(VersionHeader, out var versions)) return;
        var version = versions.FirstOrDefault();
        var instance = response.Headers.TryGetValues(InstanceHeader, out var instances) ? instances.FirstOrDefault() : null;
        if (string.IsNullOrWhiteSpace(version) || version.Length > 128 || instance?.Length > 128) return;
        bool changed;
        lock (_gate)
        {
            changed = _version is not null && (_version != version || instance is not null && _instance != instance);
            _version = version;
            _instance = instance;
        }
        if (changed) await NotifyToolsChangedAsync(ct).ConfigureAwait(false);
    }

    internal async ValueTask NotifyToolsChangedAsync(CancellationToken ct)
    {
        McpServer? downstream;
        lock (_gate) downstream = _downstream;
        if (downstream is null) return;
        try
        {
            await downstream.SendMessageAsync(new JsonRpcNotification
            {
                Method = NotificationMethods.ToolListChangedNotification,
                Params = new JsonObject()
            }, ct).ConfigureAwait(false);
        }
        catch (Exception error) when (error is McpException or IOException or InvalidOperationException or OperationCanceledException)
        {
            // Losing a catalog notification must never hide a completed mutation
            // or cause it to be replayed. context_get retains the refresh diagnostic.
            Console.Error.WriteLine("TLBX_MCP_CATALOG_REFRESH: Refresh native tool discovery; notification could not be delivered.");
        }
    }

    internal CallToolResult DescribeContext(CallToolResult result)
    {
        if (result.IsError == true || result.StructuredContent is not { ValueKind: JsonValueKind.Object } data
            || JsonNode.Parse(data.GetRawText()) is not JsonObject context) return result;
        var serverVersion = context["serverVersion"]?.GetValue<string>();
        var mismatch = !string.Equals(adapterVersion, serverVersion, StringComparison.Ordinal);
        context["adapterVersion"] = adapterVersion;
        context["adapterRefreshRequired"] = mismatch;
        context["workflowExecution"] = "live-server-unless-caller-local";
        if (mismatch)
            context["adapterRefreshHint"] = "Refresh native tool discovery for server schemas. Restart the MCP connection when idle to update caller-local tools and mixed workflows; running operations are not replayed.";
        var described = McpApiResult.Structured(context);
        described.IsError = result.IsError;
        return described;
    }
}

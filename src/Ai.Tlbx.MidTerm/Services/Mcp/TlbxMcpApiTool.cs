using System.Runtime.InteropServices;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using System.Text.Json.Serialization.Metadata;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>
/// A named, schema-defined MCP operation over an existing C# endpoint. The route
/// and method are fixed by the server, never supplied by the model. The original
/// handler retains validation, conflict handling, input history and side effects.
/// No shell, HTTP loopback hop, privileged cookie or second business implementation.
/// </summary>
internal sealed class TlbxMcpApiTool : McpServerTool
{
    private readonly McpApiOperation _operation;
    private readonly Tool _tool;

    internal TlbxMcpApiTool(McpApiOperation operation)
    {
        _operation = operation;
        _tool = new Tool
        {
            Name = operation.Name,
            Description = operation.Description,
            InputSchema = JsonSerializer.SerializeToElement(operation.BuildSchema(), McpJsonContext.Default.JsonObject),
            Annotations = new ToolAnnotations
            {
                ReadOnlyHint = operation.Method == "GET",
                DestructiveHint = operation.Method == "DELETE",
                OpenWorldHint = operation.OpenWorld
            }
        };
    }

    public override Tool ProtocolTool => _tool;
    public override IReadOnlyList<object> Metadata => [];

    public override async ValueTask<CallToolResult> InvokeAsync(
        RequestContext<CallToolRequestParams> request, CancellationToken cancellationToken = default)
    {
        var services = request.Services!;
        var context = services.GetRequiredService<IHttpContextAccessor>().HttpContext;
        if (context is null || !RequestAccessContext.HasFullUserAccess(context))
            return McpApiResult.Error("TLBX_MCP_AUTH_REQUIRED", "Full tlbx access is required.");
        var denied = TlbxMcpEndpoints.Authorize(context,
            services.GetRequiredService<Sessions.SessionCliContextService>());
        if (denied is not null) return McpApiResult.Error("TLBX_MCP_CONTEXT_INVALID", denied.Value.Message);
        try
        {
            var arguments = request.Params?.Arguments?.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
                ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (_operation.Name == "repos_list" && arguments.TryGetValue("details", out var detailValue)
                && detailValue.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new McpApiArgumentException("details must be a boolean.");
            var self = context.Items[TlbxMcpEndpoints.SessionItem] as string;
            var invocation = _operation.Bind(arguments, self);
            var result = await services.GetRequiredService<TlbxMcpApiInvoker>()
                .InvokeAsync(context, invocation, cancellationToken).ConfigureAwait(false);
            return result;
        }
        catch (McpApiArgumentException ex) { return McpApiResult.Error("TLBX_MCP_INVALID_ARGUMENT", ex.Message); }
        catch (BadHttpRequestException ex) { return McpApiResult.Error("TLBX_MCP_INVALID_ARGUMENT", ex.Message); }
        catch (JsonException ex) { return McpApiResult.Error("TLBX_MCP_INVALID_JSON", ex.Message); }
    }
}

internal sealed class McpApiOperation(string name, string method, string pattern, string description)
{
    internal string Name { get; } = name;
    internal string Method { get; } = method;
    internal string Pattern { get; } = pattern;
    internal string Description { get; } = description;
    internal bool OpenWorld { get; init; }
    internal JsonTypeInfo? BodyType { get; init; }
    internal string? SessionRoute { get; init; }
    internal string? SessionBody { get; init; }
    internal bool AllowUnassociatedBody { get; init; }
    internal string? RemotePattern { get; init; }
    internal Dictionary<string, string> Routes { get; } = new(StringComparer.Ordinal);
    internal List<McpApiParameter> Parameters { get; } = [];
    internal JsonObject Defaults { get; } = new();
    internal JsonObject Forced { get; } = new();
    internal Action<McpApiBinding>? Transform { get; init; }

    private JsonObject? _schema;
    private JsonObject? _bodyProperties;

    // Operations are configured before publication; reuse their immutable metadata on every call.
    internal JsonObject BuildSchema() => _schema ??= CreateSchema();

    private JsonObject CreateSchema()
    {
        var schema = BodyType?.GetJsonSchemaAsNode() as JsonObject ?? new JsonObject();
        _bodyProperties = schema["properties"]?.DeepClone() as JsonObject;
        schema["type"] = "object";
        schema["additionalProperties"] = false;
        var properties = schema["properties"] as JsonObject ?? new JsonObject();
        schema["properties"] = null;
        schema["properties"] = properties;
        var required = schema["required"] as JsonArray ?? new JsonArray();
        schema["required"] = null;
        schema["required"] = required;
        if (SessionBody is { } sessionBody)
        {
            properties.Remove(sessionBody);
            RemoveRequired(required, sessionBody);
        }
        if (SessionRoute is not null || SessionBody is not null || Parameters.Any(p => p.Argument == "targetSessionId"))
            properties["targetSessionId"] = new JsonObject
            {
                ["type"] = "string", ["minLength"] = 1,
                ["description"] = "Target session; defaults to verified self. Operators must specify it."
            };
        foreach (var (route, argument) in Routes)
        {
            properties[argument] = new JsonObject { ["type"] = "string", ["minLength"] = 1,
                ["description"] = "Identifier for " + route + "." };
            if (!required.Any(n => n?.GetValue<string>() == argument)) required.Add((JsonNode?)JsonValue.Create(argument));
        }
        foreach (var parameter in Parameters)
        {
            properties[parameter.Argument] = parameter.Schema.DeepClone();
            if (parameter.Required && !required.Any(n => n?.GetValue<string>() == parameter.Argument)) required.Add((JsonNode?)JsonValue.Create(parameter.Argument));
        }
        if (RemotePattern is not null)
            properties["machineId"] = new JsonObject { ["type"] = "string", ["minLength"] = 1,
                ["description"] = "Optional registered Hub machine. Omit for this tlbx host." };
        foreach (var (key, value) in Defaults)
        {
            if (properties[key] is JsonObject property) property["default"] = value?.DeepClone();
            RemoveRequired(required, key);
        }
        foreach (var (key, _) in Forced)
        {
            properties.Remove(key); // The operation supplies fixed values; agents never need to emit them.
            RemoveRequired(required, key);
        }
        if (required.Count == 0) schema.Remove("required");
        return schema;
    }

    internal McpApiInvocation Bind(IReadOnlyDictionary<string, JsonElement> arguments, string? self)
    {
        var schema = BuildSchema();
        var properties = (JsonObject)schema["properties"]!;
        foreach (var argument in arguments.Keys)
            if (!properties.ContainsKey(argument) && !Forced.ContainsKey(argument)) throw new McpApiArgumentException("Unknown argument: " + argument);
        foreach (var (key, value) in Forced)
            if (arguments.TryGetValue(key, out var supplied) && !JsonNode.DeepEquals(value, JsonNode.Parse(JsonMarshal.GetRawUtf8Value(supplied))))
                throw new McpApiArgumentException(key + " is fixed by this operation.");
        foreach (var key in schema["required"] as JsonArray ?? [])
            if (key?.GetValue<string>() is { } required && !arguments.ContainsKey(required) && !Defaults.ContainsKey(required) && !Forced.ContainsKey(required))
                throw new McpApiArgumentException("Required argument: " + required);

        var routeValues = new RouteValueDictionary();
        var query = new Dictionary<string, string?>(StringComparer.Ordinal);
        var body = new JsonObject();
        if (BodyType is not null)
        {
            var bodyProperties = _bodyProperties;
            foreach (var (key, value) in arguments)
                if (bodyProperties?.ContainsKey(key) == true && key != SessionBody)
                    body[key] = JsonNode.Parse(JsonMarshal.GetRawUtf8Value(value));
            foreach (var (key, value) in Defaults)
                if (!body.ContainsKey(key)) body[key] = value?.DeepClone();
            foreach (var (key, value) in Forced) body[key] = value?.DeepClone();
        }
        string Target()
        {
            var explicitTarget = ReadString(arguments, "targetSessionId");
            return explicitTarget ?? self ?? throw new McpApiArgumentException(
                "No verified self session. Supply targetSessionId explicitly or bind this runtime to a tlbx session.");
        }
        if (SessionRoute is not null) routeValues[SessionRoute] = Target();
        if (SessionBody is not null && (!AllowUnassociatedBody || self is not null || arguments.ContainsKey("targetSessionId")))
            body[SessionBody] = Target();
        foreach (var (route, argument) in Routes)
            routeValues[route] = ReadString(arguments, argument) ?? throw new McpApiArgumentException("Required identifier: " + argument);
        foreach (var parameter in Parameters)
        {
            if (parameter.Query is null) continue;
            if (arguments.TryGetValue(parameter.Argument, out var value))
                query[parameter.Query] = Scalar(value, parameter.Argument);
            else if (parameter.SelfDefault) query[parameter.Query] = Target();
            else if (parameter.Default is not null) query[parameter.Query] = parameter.Default;
        }
        var resolvedPattern = Pattern;
        if (ReadString(arguments, "machineId") is { } machineId && RemotePattern is not null)
        {
            resolvedPattern = RemotePattern;
            routeValues["id"] = machineId;
        }
        var binding = new McpApiBinding(arguments, routeValues, query, body);
        Transform?.Invoke(binding);
        var path = resolvedPattern;
        foreach (var (key, value) in routeValues)
            path = path.Replace("{" + key + "}", Uri.EscapeDataString(Convert.ToString(value, CultureInfo.InvariantCulture)!), StringComparison.Ordinal);
        return new McpApiInvocation(Method, resolvedPattern, path, routeValues,
            QueryString.Create(query), BodyType is not null ? body.ToJsonString() : null);
    }

    private static void RemoveRequired(JsonArray required, string name)
    {
        for (var i = required.Count - 1; i >= 0; i--)
            if (required[i]?.GetValue<string>() == name) required.RemoveAt(i);
    }
    internal static string? ReadString(IReadOnlyDictionary<string, JsonElement> arguments, string key)
    {
        if (!arguments.TryGetValue(key, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new McpApiArgumentException(key + " must be a nonempty string.");
        return value.GetString();
    }
    private static string? Scalar(JsonElement value, string name) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(), JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False => value.GetRawText(),
        JsonValueKind.Null => null, _ => throw new McpApiArgumentException(name + " must be a scalar value.")
    };
}

internal sealed record McpApiParameter(string Argument, string? Query, JsonObject Schema,
    bool Required = false, string? Default = null, bool SelfDefault = false);
internal sealed record McpApiBinding(IReadOnlyDictionary<string, JsonElement> Arguments,
    RouteValueDictionary Routes, Dictionary<string, string?> Query, JsonObject Body);
internal sealed record McpApiInvocation(string Method, string Pattern, string Path,
    RouteValueDictionary Routes, QueryString Query, string? Body);
internal sealed class McpApiArgumentException(string message) : Exception(message);

/// <summary>Invoke only registered fixed routes with the current authenticated caller's context.</summary>
internal sealed class TlbxMcpApiInvoker(IEnumerable<EndpointDataSource> sources)
{
    internal async Task<CallToolResult> InvokeAsync(HttpContext caller, McpApiInvocation invocation, CancellationToken ct)
    {
        if (!RequestAccessContext.HasFullUserAccess(caller))
            return McpApiResult.Error("TLBX_MCP_AUTH_REQUIRED", "Full tlbx access is required.");
        var endpoint = sources.SelectMany(source => source.Endpoints).OfType<RouteEndpoint>().SingleOrDefault(endpoint =>
            endpoint.RoutePattern.RawText == invocation.Pattern &&
            endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods.Contains(invocation.Method, StringComparer.Ordinal) == true);
        if (endpoint?.RequestDelegate is null)
            return McpApiResult.Error("TLBX_MCP_UNAVAILABLE", "The existing API endpoint is unavailable: " + invocation.Method + " " + invocation.Pattern);
        var context = new DefaultHttpContext
        {
            RequestServices = caller.RequestServices,
            User = caller.User,
            RequestAborted = ct,
            TraceIdentifier = caller.TraceIdentifier
        };
        foreach (var item in caller.Items) context.Items[item.Key] = item.Value;
        context.Connection.LocalIpAddress = caller.Connection.LocalIpAddress;
        context.Connection.RemoteIpAddress = caller.Connection.RemoteIpAddress;
        context.Connection.LocalPort = caller.Connection.LocalPort;
        context.Connection.RemotePort = caller.Connection.RemotePort;
        context.Request.Method = invocation.Method;
        context.Request.Scheme = caller.Request.Scheme;
        context.Request.Host = caller.Request.Host;
        context.Request.PathBase = caller.Request.PathBase;
        context.Request.Path = invocation.Path;
        context.Request.QueryString = invocation.Query;
        context.Request.RouteValues = invocation.Routes;
        foreach (var header in caller.Request.Headers)
            if (!header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase)
                && !header.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))
                context.Request.Headers[header.Key] = header.Value;
        await using var input = new StringStream(invocation.Body ?? string.Empty, Encoding.UTF8);
        context.Request.Body = input;
        var inputLength = invocation.Body is null ? 0 : Encoding.UTF8.GetByteCount(invocation.Body);
        context.Request.ContentLength = inputLength;
        if (invocation.Body is not null) context.Request.ContentType = "application/json; charset=utf-8";
        context.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyDetection(inputLength != 0));
        context.SetEndpoint(endpoint);
        await using var output = new MemoryStream();
        context.Response.Body = output;
        await endpoint.RequestDelegate(context).ConfigureAwait(false);
        await context.Response.CompleteAsync().ConfigureAwait(false);
        var bytes = output.GetBuffer().AsSpan(0, checked((int)output.Length));
        JsonNode? data;
        if (context.Response.ContentType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true && bytes.Length != 0)
            data = JsonNode.Parse(bytes);
        else if (context.Response.ContentType?.StartsWith("application/octet-stream", StringComparison.OrdinalIgnoreCase) == true)
            data = new JsonObject { ["base64"] = Convert.ToBase64String(bytes), ["byteLength"] = bytes.Length };
        else data = JsonValue.Create(Encoding.UTF8.GetString(bytes));
        return McpApiResult.Structured(new JsonObject
        {
            ["statusCode"] = context.Response.StatusCode,
            ["contentType"] = context.Response.ContentType,
            ["data"] = data
        }, context.Response.StatusCode >= 400);
    }

    private sealed class BodyDetection(bool canHaveBody) : IHttpRequestBodyDetectionFeature
    {
        public bool CanHaveBody => canHaveBody;
    }
}

internal static class McpApiResult
{
    internal static CallToolResult Structured(JsonObject value, bool error = false)
    {
        var data = JsonSerializer.SerializeToElement(value, McpJsonContext.Default.JsonObject);
        return new CallToolResult { StructuredContent = data, IsError = error,
            Content = [new TextContentBlock { Text = data.GetRawText() }] };
    }
    internal static CallToolResult Error(string code, string message) => Structured(new JsonObject
    {
        ["errorCode"] = code, ["message"] = message
    }, error: true);
}

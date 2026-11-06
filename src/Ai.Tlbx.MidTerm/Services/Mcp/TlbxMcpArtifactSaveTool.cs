using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Ai.Tlbx.MidTerm.Services.Mcp;

/// <summary>Streams complete generated artifacts to the native caller, never to service-owned paths.</summary>
internal sealed class TlbxMcpArtifactSaveTool : McpServerTool
{
    internal const string ToolName = "artifact_save";
    internal const int ChunkBytes = 4 * 1024 * 1024;
    internal static Tool Definition { get; } = new()
    {
        Name = ToolName,
        Description = "Save a complete caller-owned generated artifact to a new file on the native MCP caller machine as its OS user. Streams byte chunks without sending binary through model context; returns path, size and SHA256. Existing files are never overwritten. Requires native stdio; HTTP cannot write caller files.",
        InputSchema = JsonSerializer.SerializeToElement(new JsonObject
        {
            ["type"] = "object", ["additionalProperties"] = false,
            ["required"] = new JsonArray("uri", "path"),
            ["properties"] = new JsonObject
            {
                ["uri"] = new JsonObject { ["type"] = "string", ["minLength"] = 1 },
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
        return ValueTask.FromResult(McpApiResult.Error("TLBX_MCP_CLIENT_LOCAL_REQUIRED",
            "artifact_save writes on the caller machine as its OS user. Use mt mcp --stdio; the HTTP service cannot write a remote caller's files."));
    }

    internal static async ValueTask<CallToolResult> InvokeLocalAsync(CallToolRequestParams request,
        Func<string, IDictionary<string, JsonElement>, CancellationToken, ValueTask<CallToolResult>> invoke,
        CancellationToken ct)
    {
        string? temporary = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var arguments = request.Arguments ?? new Dictionary<string, JsonElement>(StringComparer.Ordinal);
            if (arguments.Keys.Any(key => key is not ("uri" or "path"))) throw new ArgumentException("Unknown artifact-save argument.");
            string Required(string name) => arguments.TryGetValue(name, out var value) && value.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(value.GetString()) ? value.GetString()! : throw new ArgumentException(name + " is required.");
            var uri = Required("uri");
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || parsed.Scheme != "tlbx-artifact" ||
                parsed.Query.Length != 0 || parsed.Fragment.Length != 0)
                throw new ArgumentException("Use the complete base artifact URI returned by tlbx, without a chunk query.");
            var path = Path.GetFullPath(Required("path"));
            if (File.Exists(path) || Directory.Exists(path)) throw new IOException("Destination already exists; choose a new file path.");
            var directory = Path.GetDirectoryName(path)!;
            if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Destination directory does not exist.");
            var listed = await invoke("artifacts_list", new Dictionary<string, JsonElement>(StringComparer.Ordinal), ct).ConfigureAwait(false);
            if (listed.IsError == true || listed.StructuredContent is not { } listing ||
                !listing.TryGetProperty("resources", out var resources) || resources.ValueKind != JsonValueKind.Array)
                throw new InvalidDataException("Artifact listing failed; no destination was written.");
            JsonElement? selected = null;
            using (var entries = resources.EnumerateArray())
                foreach (var entry in entries)
                    if (entry.TryGetProperty("uri", out var identifier) && identifier.GetString() == uri)
                    {
                        if (selected is not null) throw new InvalidDataException("Duplicate artifact metadata.");
                        selected = entry;
                    }
            if (selected is not { } metadata || !metadata.TryGetProperty("size", out var size) ||
                !size.TryGetInt32(out var length) || length < 0)
                throw new InvalidDataException("Artifact unavailable for this caller.");
            temporary = Path.Combine(directory, ".tlbx-mcp-" + Guid.NewGuid().ToString("N") + ".partial");
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            await using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                65536, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                for (var offset = 0; offset < length;)
                {
                    var count = Math.Min(ChunkBytes, length - offset);
                    var encoded = JsonSerializer.SerializeToElement(new JsonObject
                        { ["uri"] = uri, ["offset"] = offset, ["length"] = count }, McpJsonContext.Default.JsonObject);
                    var values = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
                    {
                        ["uri"] = encoded.GetProperty("uri"),
                        ["offset"] = encoded.GetProperty("offset"),
                        ["length"] = encoded.GetProperty("length")
                    };
                    var result = await invoke("artifact_read", values, ct).ConfigureAwait(false);
                    var expectedUri = uri + "?offset=" + offset.ToString(CultureInfo.InvariantCulture) +
                        "&length=" + count.ToString(CultureInfo.InvariantCulture);
                    if (result.IsError == true || result.Content.Count != 1 ||
                        result.Content[0] is not EmbeddedResourceBlock { Resource: BlobResourceContents blob } || blob.Uri != expectedUri)
                        throw new InvalidDataException("Artifact chunk unavailable or mismatched; the destination was not published.");
                    var bytes = blob.DecodedData;
                    if (bytes.Length != count) throw new InvalidDataException("Artifact chunk was truncated; the destination was not published.");
                    ct.ThrowIfCancellationRequested();
                    await file.WriteAsync(bytes, ct).ConfigureAwait(false);
                    hash.AppendData(bytes.Span);
                    offset += count;
                }
                await file.FlushAsync(ct).ConfigureAwait(false);
            }
            ct.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: false);
            temporary = null;
            return McpApiResult.Structured(new JsonObject
            {
                ["path"] = path, ["byteLength"] = length, ["sha256"] = Convert.ToHexStringLower(hash.GetHashAndReset()),
                ["uri"] = uri, ["executionHost"] = "native-mcp-caller"
            });
        }
        catch (OperationCanceledException)
        { return McpApiResult.Error("TLBX_MCP_ARTIFACT_SAVE_CANCELLED", "Artifact save cancelled; no destination was published."); }
        catch (Exception error) when (error is ArgumentException or IOException or InvalidDataException or UnauthorizedAccessException or
            InvalidOperationException or FormatException or HttpRequestException or ModelContextProtocol.McpException)
        { return McpApiResult.Error("TLBX_MCP_ARTIFACT_SAVE_FAILED", error.Message); }
        finally
        {
            if (temporary is not null)
                try { File.Delete(temporary); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                { Console.Error.WriteLine("TLBX_MCP_ARTIFACT_SAVE_CLEANUP: " + temporary + ": " + error.Message); }
        }
    }
}

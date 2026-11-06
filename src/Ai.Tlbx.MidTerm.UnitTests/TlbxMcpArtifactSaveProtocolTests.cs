using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ai.Tlbx.MidTerm.Services.Mcp;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TlbxMcpArtifactSaveProtocolTests : IDisposable
{
    private const string Uri = "tlbx-artifact://capture/opaque-fixture";
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tlbx-mcp-save-" + Guid.NewGuid().ToString("N"));
    public TlbxMcpArtifactSaveProtocolTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(0)]
    [InlineData(4194304)]
    [InlineData(4194321)]
    public async Task CompleteBinaryIsStreamedToNewCallerFileAndOnlyMetadataReturns(int length)
    {
        var bytes = new byte[length];
        RandomNumberGenerator.Fill(bytes);
        var path = Path.Combine(_directory, "Grüße 日本 'literal'.bin");
        var reads = 0;
        var result = await Save(path, (name, arguments, _) =>
        {
            if (name == "artifacts_list") return ValueTask.FromResult(List(length));
            reads++;
            return ValueTask.FromResult(Read(bytes, arguments));
        });
        Assert.NotEqual(true, result.IsError);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Equal(length == 0 ? 0 : (length + 4194303) / 4194304, reads);
        var data = result.StructuredContent!.Value;
        Assert.Equal(length, data.GetProperty("byteLength").GetInt32());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), data.GetProperty("sha256").GetString());
        Assert.Equal(path, data.GetProperty("path").GetString());
        Assert.DoesNotContain(result.Content, item => item is EmbeddedResourceBlock or ImageContentBlock);
        Assert.Empty(Directory.GetFiles(_directory, "*.partial"));
    }

    [Theory]
    [InlineData("truncated")]
    [InlineData("wrong-uri")]
    [InlineData("expired")]
    public async Task InvalidOrExpiredSecondChunkCannotPublishPartialDestination(string failure)
    {
        var path = Path.Combine(_directory, "must-not-exist.bin");
        var bytes = new byte[TlbxMcpArtifactSaveTool.ChunkBytes + 7];
        var reads = 0;
        var result = await Save(path, (name, arguments, _) =>
        {
            if (name == "artifacts_list") return ValueTask.FromResult(List(bytes.Length));
            if (++reads == 1) return ValueTask.FromResult(Read(bytes, arguments));
            if (failure == "expired") return ValueTask.FromResult(new CallToolResult { IsError = true });
            var normal = Read(bytes, arguments);
            var content = Assert.IsType<BlobResourceContents>(Assert.IsType<EmbeddedResourceBlock>(normal.Content.Single()).Resource);
            return ValueTask.FromResult(new CallToolResult { Content = [new EmbeddedResourceBlock
            {
                Resource = BlobResourceContents.FromBytes(failure == "truncated" ? new byte[6] : new byte[7],
                    failure == "wrong-uri" ? Uri + "/sibling" : content.Uri, "application/octet-stream")
            }] });
        });
        Assert.True(result.IsError);
        Assert.Equal(2, reads);
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task CancellationAfterFirstChunkDeletesTemporaryAndNeverPublishes()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var path = Path.Combine(_directory, "cancelled.bin");
        var bytes = new byte[TlbxMcpArtifactSaveTool.ChunkBytes + 1];
        var reads = 0;
        var result = await TlbxMcpArtifactSaveTool.InvokeLocalAsync(Request(path), (name, arguments, token) =>
        {
            if (name == "artifacts_list") return ValueTask.FromResult(List(bytes.Length));
            if (++reads == 2) { cancellation.Cancel(); token.ThrowIfCancellationRequested(); }
            return ValueTask.FromResult(Read(bytes, arguments));
        }, cancellation.Token);
        Assert.Equal(2, reads);
        Assert.True(result.IsError);
        Assert.Equal("TLBX_MCP_ARTIFACT_SAVE_CANCELLED", result.StructuredContent!.Value.GetProperty("errorCode").GetString());
        Assert.Empty(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingAndRacingDestinationsAreNeverOverwritten(bool race)
    {
        var path = Path.Combine(_directory, "existing.bin");
        byte[] existing = [4, 5, 6];
        var reads = 0;
        if (!race) await File.WriteAllBytesAsync(path, existing, TestContext.Current.CancellationToken);
        var result = await Save(path, (name, arguments, _) =>
        {
            if (name == "artifacts_list") return ValueTask.FromResult(List(1));
            reads++;
            File.WriteAllBytes(path, existing);
            return ValueTask.FromResult(Read([1], arguments));
        });
        Assert.True(result.IsError);
        Assert.Equal(race ? 1 : 0, reads);
        Assert.Equal(existing, await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Fact]
    public async Task MissingCallerArtifactCannotCreateDestinationAndHttpCannotWriteLocalFiles()
    {
        var result = await Save(Path.Combine(_directory, "missing.bin"), (_, _, _) =>
            ValueTask.FromResult(McpApiResult.Structured(new JsonObject { ["resources"] = new JsonArray() })));
        Assert.True(result.IsError);
        Assert.Empty(Directory.GetFiles(_directory));
        var http = await new TlbxMcpArtifactSaveTool().InvokeAsync(null!, TestContext.Current.CancellationToken);
        Assert.Equal("TLBX_MCP_CLIENT_LOCAL_REQUIRED", http.StructuredContent!.Value.GetProperty("errorCode").GetString());
    }

    private ValueTask<CallToolResult> Save(string path,
        Func<string, IDictionary<string, JsonElement>, CancellationToken, ValueTask<CallToolResult>> invoke)
        => TlbxMcpArtifactSaveTool.InvokeLocalAsync(Request(path), invoke, TestContext.Current.CancellationToken);

    private static CallToolRequestParams Request(string path) => new()
    {
        Name = TlbxMcpArtifactSaveTool.ToolName,
        Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        { ["uri"] = JsonSerializer.SerializeToElement(Uri), ["path"] = JsonSerializer.SerializeToElement(path) }
    };

    private static CallToolResult List(int size) => McpApiResult.Structured(new JsonObject
    {
        ["resources"] = new JsonArray(new JsonObject { ["uri"] = Uri, ["size"] = size })
    });

    private static CallToolResult Read(byte[] bytes, IDictionary<string, JsonElement> arguments)
    {
        Assert.Equal(Uri, arguments["uri"].GetString());
        var offset = arguments["offset"].GetInt32();
        var count = arguments["length"].GetInt32();
        Assert.InRange(count, 1, TlbxMcpArtifactSaveTool.ChunkBytes);
        var uri = Uri + "?offset=" + offset.ToString(CultureInfo.InvariantCulture) + "&length=" + count.ToString(CultureInfo.InvariantCulture);
        return new CallToolResult { Content = [new EmbeddedResourceBlock
            { Resource = BlobResourceContents.FromBytes(bytes.AsMemory(offset, count), uri, "application/octet-stream") }] };
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

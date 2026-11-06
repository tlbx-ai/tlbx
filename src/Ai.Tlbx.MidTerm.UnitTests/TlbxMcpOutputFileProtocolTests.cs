using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ai.Tlbx.MidTerm.Services.Mcp;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TlbxMcpOutputFileProtocolTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "tlbx-mcp-output-" + Guid.NewGuid().ToString("N"));
    public TlbxMcpOutputFileProtocolTests() => Directory.CreateDirectory(_directory);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompleteLargeUnicodeJsonAndBlobResultIsSavedOnceWithoutReturningPayload(bool toolError)
    {
        var path = Path.Combine(_directory, "Grüße 'literal'.json");
        var text = string.Concat(Enumerable.Repeat("日本 🦊 '$()' `literal`\r\n", 30000));
        var original = McpApiResult.Structured(new JsonObject { ["data"] = text }, toolError);
        original.Content.Add(new EmbeddedResourceBlock { Resource = BlobResourceContents.FromBytes(new byte[] { 0, 255, 13, 10 },
            "tlbx-artifact://capture/opaque", "application/octet-stream") });
        var calls = 0;
        var result = await Save(path, (_, arguments, _) =>
        {
            calls++;
            Assert.Equal("exact\r\n'quoted'", arguments["text"].GetString());
            return ValueTask.FromResult(original);
        });
        Assert.Equal(1, calls);
        Assert.Equal(toolError, result.IsError == true);
        var bytes = await File.ReadAllBytesAsync(path, TestContext.Current.CancellationToken);
        using var document = JsonDocument.Parse(bytes);
        Assert.Equal(text, document.RootElement.GetProperty("structuredContent").GetProperty("data").GetString());
        var restored = JsonSerializer.Deserialize(bytes, McpClientJsonContext.Default.CallToolResult)!;
        Assert.Equal(new byte[] { 0, 255, 13, 10 }, Assert.IsType<BlobResourceContents>(
            Assert.IsType<EmbeddedResourceBlock>(restored.Content[1]).Resource).DecodedData.ToArray());
        var metadata = result.StructuredContent!.Value;
        Assert.Equal(bytes.Length, metadata.GetProperty("byteLength").GetInt64());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(bytes)), metadata.GetProperty("sha256").GetString());
        Assert.True(metadata.GetProperty("saved").GetBoolean());
        Assert.True(metadata.GetProperty("responseReceived").GetBoolean());
        Assert.False(metadata.GetProperty("replaySafe").GetBoolean());
        Assert.True(result.Content.OfType<TextContentBlock>().Single().Text.Length < 2048);
    }

    [Fact]
    public async Task ExistingDestinationAndInvalidWrapperNeverDispatch()
    {
        var path = Path.Combine(_directory, "existing.json");
        await File.WriteAllTextAsync(path, "keep", TestContext.Current.CancellationToken);
        var calls = 0;
        ValueTask<CallToolResult> Invoke(string _, IDictionary<string, JsonElement> __, CancellationToken ___)
        { calls++; return ValueTask.FromResult(new CallToolResult()); }
        var existing = await Save(path, Invoke);
        Assert.False(existing.StructuredContent!.Value.GetProperty("dispatched").GetBoolean());
        foreach (var tool in new[] { "tool_call_to_file", "workflow_run" })
        {
            var result = await TlbxMcpOutputFileTool.InvokeLocalAsync(Request(Path.Combine(_directory, "never.json"), tool), Invoke,
                TestContext.Current.CancellationToken);
            Assert.False(result.StructuredContent!.Value.GetProperty("dispatched").GetBoolean());
        }
        Assert.Equal(0, calls);
        Assert.Equal("keep", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        Assert.Single(Directory.GetFiles(_directory));
    }

    [Theory]
    [InlineData("transport")]
    [InlineData("cancelled")]
    [InlineData("destination-race")]
    public async Task FailureAfterDispatchNeverReplaysAndNeverPublishesPartialOutput(string mode)
    {
        var path = Path.Combine(_directory, "result.json");
        var calls = 0;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var result = await TlbxMcpOutputFileTool.InvokeLocalAsync(Request(path), (_, _, _) =>
        {
            calls++;
            if (mode == "transport") throw new HttpRequestException("lost response");
            if (mode == "cancelled") cancellation.Cancel();
            if (mode == "destination-race") File.WriteAllText(path, "preserve racing file");
            return ValueTask.FromResult(new CallToolResult { Content = [new TextContentBlock { Text = "source completed" }] });
        }, cancellation.Token);
        Assert.Equal(1, calls);
        Assert.True(result.IsError);
        var data = result.StructuredContent!.Value;
        Assert.True(data.GetProperty("dispatched").GetBoolean());
        Assert.Equal(mode != "transport", data.GetProperty("responseReceived").GetBoolean());
        Assert.False(data.GetProperty("replaySafe").GetBoolean());
        Assert.False(data.GetProperty("saved").GetBoolean());
        Assert.Empty(Directory.GetFiles(_directory, "*.partial"));
        if (mode == "destination-race") Assert.Equal("preserve racing file", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
        else Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task FileInputCanBeCapturedWithoutRelayingInputOrOutputThroughModel()
    {
        var input = Path.Combine(_directory, "input.json");
        var text = "literal\r\n日本 '$()'";
        await File.WriteAllTextAsync(input, new JsonObject { ["text"] = text }.ToJsonString(), new UTF8Encoding(false), TestContext.Current.CancellationToken);
        var output = Path.Combine(_directory, "output.json");
        var parameters = Request(output, "tool_call_from_file");
        parameters.Arguments!["arguments"] = JsonSerializer.SerializeToElement(new { toolName = "echo", argumentsFile = input });
        var calls = 0;
        var result = await TlbxMcpOutputFileTool.InvokeLocalAsync(parameters, (name, arguments, ct) =>
        {
            Assert.Equal("tool_call_from_file", name);
            return TlbxMcpFileCallTool.InvokeLocalAsync(new CallToolRequestParams { Name = name, Arguments = arguments },
                (_, expanded, _) => { calls++; return ValueTask.FromResult(McpApiResult.Structured(new JsonObject { ["text"] = expanded["text"].GetString() })); }, ct);
        }, TestContext.Current.CancellationToken);
        Assert.NotEqual(true, result.IsError);
        Assert.Equal(1, calls);
        using var document = JsonDocument.Parse(await File.ReadAllBytesAsync(output, TestContext.Current.CancellationToken));
        Assert.Equal(text, document.RootElement.GetProperty("structuredContent").GetProperty("text").GetString());
    }

    private ValueTask<CallToolResult> Save(string path, Func<string, IDictionary<string, JsonElement>, CancellationToken, ValueTask<CallToolResult>> invoke)
        => TlbxMcpOutputFileTool.InvokeLocalAsync(Request(path), invoke, TestContext.Current.CancellationToken);
    private static CallToolRequestParams Request(string path, string tool = "echo") => new()
    {
        Name = TlbxMcpOutputFileTool.ToolName,
        Arguments = new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["toolName"] = JsonSerializer.SerializeToElement(tool), ["path"] = JsonSerializer.SerializeToElement(path),
            ["arguments"] = JsonSerializer.SerializeToElement(new { text = "exact\r\n'quoted'" })
        }
    };
    public void Dispose() => Directory.Delete(_directory, recursive: true);
}

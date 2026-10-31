using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Ai.Tlbx.MidTerm.Services.Mcp;
using ModelContextProtocol.Protocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TlbxMcpFileCallProtocolTests
{
    [Fact]
    public async Task JsonFileForwardsCompleteArgumentsExactlyOnceAndPreservesToolResult()
    {
        using var fixture = new PayloadFixture();
        var payload = new JsonObject { ["boardId"] = "opaque-board", ["expectedRevision"] = 42,
            ["item"] = new JsonObject { ["path"] = @"Q:\literal path\'quoted'\board.json", ["text"] = "Grüße\r\n$() `literal`" } };
        var path = fixture.Write("board.json", payload.ToJsonString(), bom: true);
        var expected = new CallToolResult { IsError = true, Content = [new TextContentBlock { Text = "existing conflict" }] };
        var calls = 0;
        var result = await TlbxMcpFileCallTool.InvokeLocalAsync(Request(new JsonObject
            { ["toolName"] = "board_item_write", ["argumentsFile"] = path }), (tool, arguments, _) =>
        {
            calls++;
            Assert.Equal("board_item_write", tool);
            Assert.Equal(42, arguments["expectedRevision"].GetInt32());
            Assert.True(JsonNode.DeepEquals(payload["item"], JsonNode.Parse(arguments["item"].GetRawText())));
            return ValueTask.FromResult(expected);
        }, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        Assert.Same(expected, result);
    }

    [Theory]
    [InlineData(false, "browser_evaluate", "code")]
    [InlineData(true, "terminal_paste", "text")]
    public async Task TextFilePreservesLargeUnicodeBomNewlinesAndOtherArguments(bool bom, string tool, string argument)
    {
        using var fixture = new PayloadFixture();
        var text = string.Concat(Enumerable.Repeat("Grüße 日本語 🦊 '$()' `literal`\r\n", 4096)) + "\nlast line\r\n";
        Assert.True(Encoding.UTF8.GetByteCount(text) > 32768);
        var path = fixture.Write("payload 'literal'.txt", text, bom);
        var relative = Path.GetRelativePath(Environment.CurrentDirectory, path);
        var calls = 0;
        var result = await TlbxMcpFileCallTool.InvokeLocalAsync(Request(new JsonObject
        {
            ["toolName"] = tool, ["textFile"] = relative, ["textArgument"] = argument,
            ["arguments"] = new JsonObject { ["targetSessionId"] = "explicit-target", ["bracketedPaste"] = false }
        }), (name, arguments, _) =>
        {
            calls++;
            Assert.Equal(tool, name);
            Assert.Equal(text, arguments[argument].GetString());
            Assert.Equal("explicit-target", arguments["targetSessionId"].GetString());
            Assert.False(arguments["bracketedPaste"].GetBoolean());
            return ValueTask.FromResult(new CallToolResult());
        }, TestContext.Current.CancellationToken);
        Assert.NotEqual(true, result.IsError);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{")]
    [InlineData("{\"text\":1,\"text\":2}")]
    public async Task InvalidJsonFileNeverDispatches(string text)
    {
        using var fixture = new PayloadFixture();
        await AssertNotDispatched(new JsonObject { ["toolName"] = "board_create", ["argumentsFile"] = fixture.Write("invalid.json", text) });
    }

    [Fact]
    public async Task ConflictingModesArgumentsAndRecursiveTargetsNeverDispatch()
    {
        await AssertNotDispatched(new JsonObject { ["toolName"] = "tool_call_from_file", ["argumentsFile"] = "missing" });
        await AssertNotDispatched(new JsonObject { ["toolName"] = "workflow_run", ["argumentsFile"] = "missing" });
        await AssertNotDispatched(new JsonObject { ["toolName"] = "board_create", ["argumentsFile"] = "missing", ["textFile"] = "missing", ["textArgument"] = "text" });
        await AssertNotDispatched(new JsonObject { ["toolName"] = "terminal_paste", ["textFile"] = "missing", ["textArgument"] = "text", ["arguments"] = new JsonObject { ["text"] = "do not overwrite" } });
    }

    [Fact]
    public async Task InvalidUtf8MissingFileAndOversizedPayloadNeverDispatch()
    {
        using var fixture = new PayloadFixture();
        var invalid = Path.Combine(fixture.Root, "invalid.txt");
        await File.WriteAllBytesAsync(invalid, [0xc3, 0x28], TestContext.Current.CancellationToken);
        var oversized = Path.Combine(fixture.Root, "too-large.txt");
        await using (var file = File.Create(oversized)) file.SetLength(TlbxMcpFileCallTool.MaximumFileBytes + 1L);
        foreach (var path in new[] { invalid, oversized, Path.Combine(fixture.Root, "missing.txt") })
            await AssertNotDispatched(new JsonObject { ["toolName"] = "terminal_paste", ["textFile"] = path, ["textArgument"] = "text" });
    }

    [Fact]
    public async Task CancellationBeforeDispatchIsDistinctFromUncertainCancellationAfterDispatch()
    {
        using var fixture = new PayloadFixture();
        var request = Request(new JsonObject { ["toolName"] = "board_create", ["argumentsFile"] = fixture.Write("payload.json", "{}") });
        using var before = new CancellationTokenSource();
        await before.CancelAsync();
        var calls = 0;
        var result = await TlbxMcpFileCallTool.InvokeLocalAsync(request, (_, _, _) =>
        { calls++; return ValueTask.FromResult(new CallToolResult()); }, before.Token);
        Assert.Equal(0, calls);
        AssertOutcome(result, dispatched: false);
        using var after = new CancellationTokenSource();
        result = await TlbxMcpFileCallTool.InvokeLocalAsync(request, (_, _, _) =>
        { calls++; after.Cancel(); throw new OperationCanceledException(after.Token); }, after.Token);
        Assert.Equal(1, calls);
        AssertOutcome(result, dispatched: true);
    }

    [Fact]
    public async Task CallbackTransportFailureNeverReplaysDispatchedOperation()
    {
        using var fixture = new PayloadFixture();
        var calls = 0;
        var result = await TlbxMcpFileCallTool.InvokeLocalAsync(Request(new JsonObject
            { ["toolName"] = "board_create", ["argumentsFile"] = fixture.Write("payload.json", "{}") }), (_, _, _) =>
        { calls++; throw new HttpRequestException("lost upstream result"); }, TestContext.Current.CancellationToken);
        Assert.Equal(1, calls);
        AssertOutcome(result, dispatched: true);
    }

    [Fact]
    public async Task HttpDescriptorRefusesCallerFilesWithoutReadingThem()
    {
        var tool = new TlbxMcpFileCallTool();
        Assert.Equal(TlbxMcpFileCallTool.ToolName, tool.ProtocolTool.Name);
        var result = await tool.InvokeAsync(null!, TestContext.Current.CancellationToken);
        AssertOutcome(result, dispatched: false);
        Assert.Equal("TLBX_MCP_CLIENT_LOCAL_REQUIRED", result.StructuredContent!.Value.GetProperty("errorCode").GetString());
    }

    private static async Task AssertNotDispatched(JsonObject input)
    {
        var calls = 0;
        var result = await TlbxMcpFileCallTool.InvokeLocalAsync(Request(input), (_, _, _) =>
        { calls++; return ValueTask.FromResult(new CallToolResult()); }, TestContext.Current.CancellationToken);
        Assert.Equal(0, calls);
        AssertOutcome(result, dispatched: false);
    }

    private static void AssertOutcome(CallToolResult result, bool dispatched)
    {
        Assert.True(result.IsError);
        var data = result.StructuredContent!.Value;
        Assert.Equal(dispatched, data.GetProperty("dispatched").GetBoolean());
        Assert.Equal(!dispatched, data.GetProperty("replaySafe").GetBoolean());
    }

    private static CallToolRequestParams Request(JsonObject input)
    {
        var element = JsonSerializer.SerializeToElement(input, McpJsonContext.Default.JsonObject);
        using var properties = element.EnumerateObject();
        return new CallToolRequestParams { Name = TlbxMcpFileCallTool.ToolName,
            Arguments = properties.ToDictionary(property => property.Name, property => property.Value.Clone(), StringComparer.Ordinal) };
    }

    private sealed class PayloadFixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "tlbx-mcp-file-" + Guid.NewGuid().ToString("N"));
        internal PayloadFixture() => Directory.CreateDirectory(Root);
        internal string Write(string name, string text, bool bom = false)
        {
            var path = Path.Combine(Root, name);
            File.WriteAllText(path, text, new UTF8Encoding(bom, true));
            return path;
        }
        public void Dispose() => Directory.Delete(Root, recursive: true);
    }
}

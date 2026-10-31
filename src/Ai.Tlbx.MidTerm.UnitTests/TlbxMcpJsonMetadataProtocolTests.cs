using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using Ai.Tlbx.MidTerm.Services.Mcp;
using ModelContextProtocol;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class TlbxMcpJsonMetadataProtocolTests
{
    [Theory]
    [InlineData("public")]
    [InlineData("private")]
    public void ForwardingPreservesCacheHintsAndOpaqueMetadataUsingSdkContracts(string cacheScope)
    {
        var contracts = new (JsonTypeInfo Metadata, string Collection)[]
        {
            (McpClientJsonContext.Default.ListToolsResult, "tools"),
            (McpClientJsonContext.Default.ListResourcesResult, "resources"),
            (McpClientJsonContext.Default.ListResourceTemplatesResult, "resourceTemplates"),
            (McpClientJsonContext.Default.ListPromptsResult, "prompts"),
            (McpClientJsonContext.Default.ReadResourceResult, "contents")
        };
        foreach (var (metadata, collection) in contracts)
        {
            // Explicit type-info overloads also work with reflection disabled,
            // including the SDK's otherwise inaccessible property converters.
            Assert.Same(McpJsonUtilities.DefaultOptions, metadata.Options);
            var wire = new JsonObject { [collection] = new JsonArray(), ["cacheScope"] = cacheScope,
                ["ttlMs"] = 37, ["_meta"] = new JsonObject { ["vendor/opaque"] = new JsonArray("a", 42, true) } };
            if (collection != "contents") wire["nextCursor"] = "opaque/cursor%2F";
            var value = JsonSerializer.Deserialize(wire.ToJsonString(), metadata);
            var forwarded = JsonNode.Parse(JsonSerializer.Serialize(value, metadata))!;
            Assert.Equal(cacheScope, forwarded["cacheScope"]!.GetValue<string>());
            Assert.Equal(37, forwarded["ttlMs"]!.GetValue<int>());
            Assert.True(JsonNode.DeepEquals(wire["_meta"], forwarded["_meta"]));
            if (collection != "contents") Assert.Equal("opaque/cursor%2F", forwarded["nextCursor"]!.GetValue<string>());

            // Retain the SDK's tolerant behavior for future hint values.
            wire["cacheScope"] = "future-scope";
            var future = JsonSerializer.Deserialize(wire.ToJsonString(), metadata);
            Assert.Null(JsonNode.Parse(JsonSerializer.Serialize(future, metadata))!["cacheScope"]);
        }
    }

    [Fact]
    public void ForwardingToolResultPreservesErrorContentAndStructuredPayload()
    {
        const string wire = """{"isError":true,"content":[{"type":"text","text":"upstream error"}],"structuredContent":{"opaque":[1,true,null]},"_meta":{"vendor/future":{"untouched":true}}}""";
        var metadata = McpClientJsonContext.Default.CallToolResult;
        Assert.Same(McpJsonUtilities.DefaultOptions, metadata.Options);
        var value = JsonSerializer.Deserialize(wire, metadata)!;
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(wire), JsonNode.Parse(JsonSerializer.Serialize(value, metadata))));
    }
}

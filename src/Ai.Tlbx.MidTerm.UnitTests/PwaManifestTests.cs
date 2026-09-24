using System.Text.Json.Nodes;
using Ai.Tlbx.MidTerm.Services.StaticFiles;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public class PwaManifestTests
{
    [Fact]
    public void WithServerName_UpdatesLabelsWithoutChangingAppIdentity()
    {
        const string template = """
            {"name":"tlbx","short_name":"tlbx","id":"/index.html","start_url":"/","icons":[{"src":"/icon.png"}]}
            """;

        var manifest = JsonNode.Parse(PwaManifest.WithServerName(template, "Work & Home"))!.AsObject();

        Assert.Equal("tlbx Work & Home", manifest["name"]!.GetValue<string>());
        Assert.Equal("tlbx Work & Home", manifest["short_name"]!.GetValue<string>());
        Assert.Equal("/index.html", manifest["id"]!.GetValue<string>());
        Assert.Equal("/", manifest["start_url"]!.GetValue<string>());
        Assert.Equal("/icon.png", manifest["icons"]![0]!["src"]!.GetValue<string>());
    }
}

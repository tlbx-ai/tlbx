using System.Text.Json;
using Ai.Tlbx.MidTerm.Services.WebPreview;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class WebPreviewImportMapProtocolTests
{
    [Theory]
    [InlineData("type=importmap")]
    [InlineData("type='importmap'")]
    [InlineData("TYPE=\"IMPORTMAP\"")]
    public void RewrittenModulesDoNotUseUpstreamByteHashes(string attributes)
    {
        const string json = """
            {"imports":{"./component.js":"./component.fingerprint.js"},"scopes":{"./area/":{"shared":"./shared.js"}},"integrity":{"./component.fingerprint.js":"sha256-upstream"},"extension":true}
            """;
        var rewritten = WebPreviewProxyMiddleware.StripImportMapIntegrity($"<script {attributes}>{json}</script>");
        using var map = JsonDocument.Parse(rewritten[(rewritten.IndexOf(">", StringComparison.Ordinal) + 1)..rewritten.LastIndexOf("<", StringComparison.Ordinal)]);
        Assert.False(map.RootElement.TryGetProperty("integrity", out _));
        Assert.Equal("./component.fingerprint.js", map.RootElement.GetProperty("imports").GetProperty("./component.js").GetString());
        Assert.Equal("./shared.js", map.RootElement.GetProperty("scopes").GetProperty("./area/").GetProperty("shared").GetString());
        Assert.True(map.RootElement.GetProperty("extension").GetBoolean());
    }

    [Theory]
    [InlineData("<script type='application/json'>{\"integrity\":{}}</script>")]
    [InlineData("<script data-type='importmap'>{\"integrity\":{}}</script>")]
    [InlineData("<script type='importmap'>{\"imports\":{}}</script>")]
    [InlineData("<script type='importmap'>invalid</script>")]
    public void OtherOrInvalidScriptDataIsPreserved(string html)
    {
        Assert.Equal(html, WebPreviewProxyMiddleware.StripImportMapIntegrity(html));
    }
}

using Ai.Tlbx.MidTerm.Services.WebPreview;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class WebPreviewLocationRewriterTests
{
    [Theory]
    [InlineData("location.pathname")]
    [InlineData("window.location.port")]
    [InlineData("document.location.href")]
    [InlineData("self.location.origin")]
    [InlineData("globalThis['location'].search")]
    [InlineData("window?.location?.hash")]
    public void AdaptsLocationObjectWithoutChangingPropertyAccess(string expression)
    {
        var rewritten = WebPreviewLocationRewriter.Rewrite("const value=" + expression);
        Assert.Contains("globalThis.__mtPreviewLocation", rewritten, StringComparison.Ordinal);
        Assert.EndsWith(expression[(expression.LastIndexOf('.') + 1)..], rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void PreservesStringsCommentsRegexesAndObjectPropertyNames()
    {
        const string source = "const text='location.pathname'; /* window.location.href */ const re=/location.port/; const item={location:{pathname:'/local'}}; item.location.pathname;";
        Assert.Equal(source, WebPreviewLocationRewriter.Rewrite(source));
    }

    [Fact]
    public void KeepsBindingsAndUsesRuntimeIdentityForShadowedLocation()
    {
        var rewritten = WebPreviewLocationRewriter.Rewrite("function read(location){return location.pathname} const window={location:{port:'test'}}; window.location.port;");
        Assert.Contains("function read(location)", rewritten, StringComparison.Ordinal);
        Assert.Contains("const window={location:{port:'test'}}", rewritten, StringComparison.Ordinal);
        Assert.Contains("(location))", rewritten, StringComparison.Ordinal);
        Assert.Contains("(window.location))", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void SupportsNavigationWritesAndTemplateExpressions()
    {
        var rewritten = WebPreviewLocationRewriter.Rewrite("location.href='/admin'; const url=`${location.port === '5178' ? 'http://localhost:5188' : ''}/api`; window.location.replace('/login');");
        Assert.Contains("(location)).href='/admin'", rewritten, StringComparison.Ordinal);
        Assert.Contains("(location)).port === '5178'", rewritten, StringComparison.Ordinal);
        Assert.Contains("(window.location)).replace('/login')", rewritten, StringComparison.Ordinal);
    }

    [Fact]
    public void SupportsModuleAndNonStrictClassicScripts()
    {
        Assert.Contains("__mtPreviewLocation", WebPreviewLocationRewriter.Rewrite("import x from './module.js'; export const path=location.pathname;"), StringComparison.Ordinal);
        Assert.Contains("__mtPreviewLocation", WebPreviewLocationRewriter.Rewrite("with ({}) { location.pathname; }"), StringComparison.Ordinal);
    }

    [Fact]
    public void LeavesUnparseableSourceUntouched()
    {
        const string source = "const path = location.pathname; const =";
        Assert.Equal(source, WebPreviewLocationRewriter.Rewrite(source));
    }

    [Fact]
    public void OnlyRewritesExecutableInlineScripts()
    {
        const string html = "<p>location.pathname</p><script type='application/ld+json'>{\"location.pathname\":true}</script><script type=module>const path=location.pathname</script>";
        var rewritten = WebPreviewLocationRewriter.RewriteInlineScripts(html);
        Assert.StartsWith("<p>location.pathname</p><script type='application/ld+json'>{\"location.pathname\":true}</script>", rewritten, StringComparison.Ordinal);
        Assert.Contains("globalThis.__mtPreviewLocation", rewritten, StringComparison.Ordinal);
    }
}

using System.Diagnostics;
using Ai.Tlbx.MidTerm.Services.WebPreview;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class WebPreviewLocationProtocolTests
{
    [Fact]
    public async Task DocumentAndLocationMetadataStayInOneCoordinateSystemWithoutChangingShadowedObjects()
    {
        var source = WebPreviewLocationRewriter.Rewrite("""
            const local = (document) => document.baseURI;
            const result = [document.baseURI, window.document.URL, document['documentURI'], location.href, local({baseURI:'local'})];
            require('node:assert/strict').deepEqual(result, ['https://upstream.test/app/','https://upstream.test/app/page','https://upstream.test/app/page','https://upstream.test/app/page','local']);
            """);
        var native = WebPreviewLocationRewriter.Rewrite("require('node:assert/strict').equal(document.baseURI, 'https://proxy.test/webpreview/key/');");
        var prelude = """
            const document = {baseURI:'https://proxy.test/webpreview/key/', URL:'https://proxy.test/webpreview/key/page', documentURI:'https://proxy.test/webpreview/key/page'};
            const location = {href:document.URL}; const window = {document,location};
            globalThis.__mtPreviewDocument = value => value === document ? {baseURI:'https://upstream.test/app/', URL:'https://upstream.test/app/page', documentURI:'https://upstream.test/app/page'} : value;
            globalThis.__mtPreviewLocation = value => value === location ? {href:'https://upstream.test/app/page'} : value;
            """;
        var start = new ProcessStartInfo("node") {UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true};
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(prelude + source + "; delete globalThis.__mtPreviewDocument;" + native);
        using var process = Process.Start(start)!;
        var ct = TestContext.Current.CancellationToken;
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        await output;
        Assert.True(process.ExitCode == 0, await errors);
    }
}

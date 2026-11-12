using System.Diagnostics;
using Ai.Tlbx.MidTerm.Services.WebPreview;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class WebPreviewLocationProtocolTests
{
    [Fact]
    public async Task ComputedImportsPreserveNativeEvaluationAndNestedDocumentAdaptation()
    {
        var source = WebPreviewLocationRewriter.Rewrite("""
            let evaluations = 0;
            const url = () => { evaluations++; return new URL('/module.js', document.baseURI).href; };
            const imported = await import(url());
            assert.equal(imported.default, 42);
            assert.equal(evaluations, 1);
            assert.equal(await import(new URL('/module.js', document.baseURI).href).then(m => m.default), 42);
            assert.equal(await import('data:text/javascript,export default 7').then(m => m.default), 7);
            const literal = "import('/not-a-module')";
            assert.equal(literal, "import('/not-a-module')");
            const invalid = {toString(){throw new Error('coercion');}};
            await assert.rejects(import(invalid), /coercion/);
            delete globalThis.__mtPreviewImport;
            assert.equal(await import('data:text/javascript,export default 9').then(m => m.default), 9);
            """);
        var prelude = """
            import assert from 'node:assert/strict';
            const document = {baseURI:'https://proxy.test/webpreview/key/'};
            globalThis.__mtPreviewDocument = value => value === document ? {baseURI:'https://upstream.test/app/'} : value;
            globalThis.__mtPreviewImport = value => {
                if (typeof value !== 'string' || value.startsWith('data:')) return value;
                assert.equal(value, 'https://upstream.test/module.js');
                return 'data:text/javascript,export default 42';
            };
            """;
        var start = new ProcessStartInfo("node") {UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true};
        start.ArgumentList.Add("--input-type=module"); start.ArgumentList.Add("-e"); start.ArgumentList.Add(prelude + source);
        using var process = Process.Start(start)!;
        var ct = TestContext.Current.CancellationToken;
        var output = process.StandardOutput.ReadToEndAsync(ct); var errors = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        await output; Assert.True(process.ExitCode == 0, await errors);
    }

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

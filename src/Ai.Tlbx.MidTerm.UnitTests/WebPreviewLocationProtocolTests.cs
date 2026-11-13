using System.Diagnostics;
using Ai.Tlbx.MidTerm.Services.WebPreview;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class WebPreviewLocationProtocolTests
{
    [Fact]
    public async Task LocationAliasesDestructuringAndCoercionUseUpstreamValuesWithoutChangingReferences()
    {
        const string application = """
            function p(e){var t=e||{},n=t.key,r=t.state,i=window.location,a=i.pathname,o=i.search,s=i.hash,c=a+o+s;return c;}
            assert.equal(p(), '/explore?local=1#section');
            const alias = window.location;
            assert.equal(alias, upstream);
            const {pathname, search, hash} = window.location;
            assert.deepEqual([pathname,search,hash], ['/explore','?local=1','#section']);
            let route;
            ({pathname:route} = window['location']);
            assert.equal(route, '/explore');
            let assigned;
            assigned = self.location;
            assert.equal(assigned.pathname, '/explore');
            assert.equal(new URL(window.location).href, upstream.href);
            assert.equal(String(location), upstream.href);
            assert.equal(`${self.location}`, upstream.href);
            assert.equal(location + '', upstream.href);
            assert.equal(({route:window.location}).route, upstream);
            const beforeMember = calls;
            assert.equal(window.location.pathname, '/explore');
            assert.equal(calls - beforeMember, 1);
            const shadow = {pathname:'shadow'};
            const shorthand = {location};
            assert.equal(shorthand.location, native);
            assert.equal(((location)=>location)(shadow), shadow);
            assert.equal(((window)=>window.location)({location:shadow}), shadow);
            let reads = 0;
            const shadowWindow = {get location(){reads++;return shadow;}};
            assert.equal(((window)=>{const x=window.location;return x;})(shadowWindow), shadow);
            assert.equal(reads, 1);
            assert.equal((function(document){const x=document.location;return x;})({location:shadow}), shadow);
            assert.equal((function(location){location='assigned';return location;})('old'), 'assigned');
            (function(window){
              window.location = 'assigned';
              assert.equal(window.location, 'assigned');
              ({value:window.location} = {value:'destructured'});
              assert.equal(window.location, 'destructured');
              for(window.location of ['first','last']){}
              assert.equal(window.location, 'last');
            })({location:'old'});
            (function(window){
              const before = calls;
              assert.equal(window.location++, 2);
              assert.equal(++window.location, 4);
              assert.equal(delete window.location, true);
              assert.equal(calls, before);
            })({location:2});
            (function(window){
              const before = calls;
              assert.equal(window.location.pathname++, 2);
              assert.equal(delete window.location.pathname, true);
              assert.equal(delete window.location?.pathname, true);
              assert.equal(calls, before);
            })({location:{pathname:2}});
            assert.equal(((window)=>window?.location.pathname)(null), undefined);
            assert.equal(((window)=>window.location?.pathname)({location:undefined}), undefined);
            assert.equal(((window)=>window?.location)(null), undefined);
            let evaluated = 0;
            const optional = null;
            assert.equal(optional?.[(evaluated++, window.location)], undefined);
            assert.equal(evaluated, 0);
            // A unary IIFE must not suppress aliases in the function body.
            !function(){const x=window.location;assert.equal(x.pathname,'/explore');}();
            assert.equal(typeof missingLocation, 'undefined');
            """;
        var source = WebPreviewLocationRewriter.Rewrite(application);
        Assert.Equal(source, WebPreviewLocationRewriter.Rewrite(source));
        var fallback = WebPreviewLocationRewriter.Rewrite("const fallback=window.location;assert.equal(fallback,native);");
        var prelude = """
            const assert=require('node:assert/strict');
            const native=new URL('https://proxy.test/webpreview/route/_access/id/token/explore?transport=1');
            const upstream=new URL('https://mastodon.social/explore?local=1#section');
            let location=native;
            const window={location},self=window;
            let calls=0;
            globalThis.__mtPreviewLocation=value=>{calls++;return value===native?upstream:value;};
            """;
        var start = new ProcessStartInfo("node") {UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true};
        start.ArgumentList.Add("-e");
        start.ArgumentList.Add(prelude + source + ";delete globalThis.__mtPreviewLocation;" + fallback);
        using var process = Process.Start(start)!;
        var ct = TestContext.Current.CancellationToken;
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);
        try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(5), ct); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        await output;
        Assert.True(process.ExitCode == 0, await errors);
    }

    [Fact]
    public void AdapterDefinitionsAndMarkedInlineTransportCodeKeepNativeLocationReads()
    {
        const string definition = "globalThis.__mtPreviewLocation=function(value){const native=window.location;return value===native?value:native;};";
        Assert.Equal(definition, WebPreviewLocationRewriter.Rewrite(definition));
        const string transport = "<script data-mt-proxy-route='/webpreview/route'>const native=window.location;</script>";
        Assert.Equal(transport, WebPreviewLocationRewriter.RewriteInlineScripts(transport));
    }

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

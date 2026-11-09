using System.Diagnostics;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Services.WebPreview;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class WebPreviewNestedBridgeProtocolTests
{
    private const string Parent = "/webpreview/parent-route/_access/parent-id/parent-token";
    private const string Child = "/webpreview/child-route/_access/child-id/child-token";

    [Theory]
    [InlineData("child", true)]
    [InlineData("parent", false)]
    [InlineData("", false)]
    [InlineData("wrong-token", false)]
    [InlineData("wrong-route", false)]
    public async Task OrderedDoubleInjectionUsesOnlyTheIndependentChildAttachment(string identity, bool connected)
    {
        var html = Document(Child, "https://child.example", "<html><head></head><body></body></html>");
        html = Document(Parent, "https://parent.example", html);
        await RunAsync(html, identity, connected, nested: true);
    }

    [Theory]
    [InlineData("child", true)]
    [InlineData("wrong-token", false)]
    [InlineData("", false)]
    public async Task LegacyOuterSingletonDoesNotSuppressAnOwnedChild(string identity, bool connected)
    {
        var html = Document(Child, "https://child.example", "<html><head></head></html>");
        await RunAsync(html, identity, connected, nested: true, legacyProxy: true);
    }

    [Fact]
    public async Task LegacyLocationRewritingLeavesNativeChildCoordinatesUsable()
    {
        var adapter = WebPreviewProxyMiddleware.GetUrlRewriteScript(Child);
        var code = adapter[(adapter.IndexOf(">", StringComparison.Ordinal) + 1)..adapter.LastIndexOf("</script>", StringComparison.Ordinal)];
        var html = "<html><head><script>window.__mtTargetOrigin='https://child.example';</script><script>"
            + WebPreviewLocationRewriter.Rewrite(code) + "</script></head></html>";
        await RunAsync(html, "child", true, nested: true, legacyProxy: true, legacyLocation: true);
    }

    [Theory]
    [InlineData("child", true)]
    [InlineData("wrong-token", false)]
    [InlineData("wrong-route", false)]
    [InlineData("wrong-id", false)]
    public async Task ParentContextRestoresAnOwnedAttachmentAfterWindowNameClears(string identity, bool connected)
    {
        var html = Document(Child, "https://child.example", "<html><head></head></html>");
        await RunAsync(html, identity, connected, nested: true, legacyProxy: true, parentHandshake: true);
    }

    [Fact]
    public async Task ClearedWindowNameAlsoRecoversWithoutAnOlderOuterAdapter()
    {
        var html = Document(Child, "https://child.example", "<html><head></head></html>");
        await RunAsync(html, "child", true, nested: true, parentHandshake: true);
    }

    [Fact]
    public async Task AForeignWindowCannotSupplyNativeParentContext()
    {
        var html = Document(Child, "https://child.example", "<html><head></head></html>");
        await RunAsync(html, "child", false, nested: true, legacyProxy: true, parentHandshake: true, foreignParentHandshake: true);
    }

    [Fact]
    public async Task IndependentChildBootstrapOutranksDuplicateSharedParentCookies()
    {
        var parentContext = JsonSerializer.Serialize(new { previewId = "parent-id", previewToken = "parent-token", sessionId = "parent-session", routeKey = "parent-route" });
        var childContext = JsonSerializer.Serialize(new { previewId = "child-id", previewToken = "child-token", sessionId = "child-session", routeKey = "child-route" });
        var cookies = "mt-preview-ctx=" + Uri.EscapeDataString(parentContext) + "; mt-preview-ctx=" + Uri.EscapeDataString(childContext);
        var html = Document(Parent, "https://parent.example", Document(Child, "https://child.example", "<html><head></head></html>"));
        await RunAsync(html, "child", true, nested: true, initialCookies: cookies);
    }
    [Fact]
    public async Task DeeperAdapterWinsBeforeTheParserHasReachedAnyInnerScript()
    {
        var html = Document(Child, "https://child.example", "<html><head></head></html>");
        html = Document("/webpreview/middle/_access/middle-id/middle-token", "https://middle.example", html);
        html = Document(Parent, "https://parent.example", html);
        await RunAsync(html, "child", true, nested: true, middle: true);
    }

    [Fact]
    public async Task OrdinaryTopLevelAttachmentStillConnectsAndUnrelatedRouteDoesNotTakeOver()
    {
        var unrelated = WebPreviewProxyMiddleware.GetUrlRewriteScript(Child);
        var html = Document(Parent, "https://parent.example", "<html><head>" + unrelated + "</head></html>");
        await RunAsync(html, "parent", true, nested: false);
    }

    [Fact]
    public void SnapshotsStripBothLegacyAndScopedAdaptersWithoutRemovingApplicationScripts()
    {
        var html = WebPreviewProxyMiddleware.GetUrlRewriteScript(Child)
            + "<script>(function(){if(window.__mtProxy)return;})();</script><script>application()</script>";
        Assert.Equal("<script>application()</script>", WebPreviewHtmlSnapshotSanitizer.StripProxyArtifacts(html));
    }

    private static string Document(string route, string origin, string html)
    {
        html = WebPreviewLocationRewriter.RewriteInlineScripts(html);
        var originScript = $"<script>window.__mtTargetOrigin={JsonSerializer.Serialize(origin)};</script>";
        return WebPreviewProxyMiddleware.InjectHeadContent(html, originScript
            + WebPreviewProxyMiddleware.GetUrlRewriteScript(route, upstreamHtml: html));
    }

    private static async Task RunAsync(string html, string identity, bool connected, bool nested, bool middle = false, string initialCookies = "", bool legacyProxy = false, bool legacyLocation = false, bool parentHandshake = false, bool foreignParentHandshake = false)
    {
        // Execute every complete, server-generated script in parser order. No future
        // script nodes are visible to the prepended adapter in this fixture.
        var setup = "const html=" + JsonSerializer.Serialize(html)
            + ";const identity=" + JsonSerializer.Serialize(identity)
            + ";const connected=" + JsonSerializer.Serialize(connected)
            + ";const nested=" + JsonSerializer.Serialize(nested)
            + ";const foreignParentHandshake=" + JsonSerializer.Serialize(foreignParentHandshake) + ";const parentHandshake=" + JsonSerializer.Serialize(parentHandshake) + ";const legacyLocation=" + JsonSerializer.Serialize(legacyLocation) + ";const legacyProxy=" + JsonSerializer.Serialize(legacyProxy) + ";const middle=" + JsonSerializer.Serialize(middle) + ";const initialCookies=" + JsonSerializer.Serialize(initialCookies) + ";";
        var script = setup + """
            const assert=require('node:assert/strict'),vm=require('node:vm');
            const parent='/webpreview/parent-route/_access/parent-id/parent-token';
            const child='/webpreview/child-route/_access/child-id/child-token';
            const middlePath=middle?'/webpreview/middle/_access/middle-id/middle-token':'';
            const route=nested?'child':'parent';
            const attachment={routeKey:route+'-route',previewId:route+'-id',previewToken:route+'-token',sessionId:route+'-session',previewName:'self',targetRevision:7};
            let saved=identity==='parent'?{...attachment,routeKey:'parent-route',previewId:'parent-id',previewToken:'parent-token',sessionId:'parent-session'}:{...attachment};
            if(identity==='wrong-token')saved.previewToken='wrong';
            if(identity==='wrong-route')saved.routeKey='wrong';
            if(identity==='wrong-id')saved.previewId='wrong';
            const sockets=[],messages=[],listeners=new Map();
            const location=new URL('https://outer.example'+parent+middlePath+(nested?child:'')+'/page?__mtTargetRevision=8'+(nested?'&__mtSubframe=1':''));
            location.assign=()=>{};location.replace=()=>{};location.reload=()=>{};
            class Element{setAttribute(name,value){this[name]=value;}getAttribute(){return null;}hasAttribute(){return false;}}
            class Frame extends Element{get src(){return this.value;}set src(value){this.value=value;}}
            class Xhr{open(){}abort(){}send(){}}
            class Socket{constructor(url){sockets.push(new URL(url));this.readyState=0;}send(){}}
            const document={cookie:initialCookies,hidden:false,visibilityState:'visible',documentElement:{},querySelector:()=>null,querySelectorAll:()=>[],addEventListener:()=>{},hasFocus:()=>false,baseURI:location.href};
            const context={URL,URLSearchParams,DOMException,location,document,Element,XMLHttpRequest:Xhr,WebSocket:Socket,
              HTMLIFrameElement:Frame,HTMLLinkElement:class extends Element{},HTMLAnchorElement:class extends Element{},HTMLFormElement:class extends Element{},
              HTMLObjectElement:class extends Element{},HTMLVideoElement:class extends Element{},
              MutationObserver:class{observe(){}},navigator:{},fetch:()=>{},open:()=>{},
              requestAnimationFrame:()=>1,cancelAnimationFrame:()=>{},setTimeout:()=>1,clearTimeout:()=>{},queueMicrotask:()=>{},
              addEventListener:(type,fn)=>listeners.set(type,[...(listeners.get(type)||[]),fn]),
              removeEventListener:(type,fn)=>listeners.set(type,(listeners.get(type)||[]).filter(f=>f!==fn)),console:{log(){},info(){},warn(){},error(){},debug(){}},
              __mtProxy:legacyProxy,name:parentHandshake?'':identity?JSON.stringify(saved):'',parent:{__mtProxy:true,postMessage:message=>messages.push(message)},
              history:{state:null,pushState(){},replaceState(_s,_t,url){location.href=new URL(url,location).href;}}};
            if(legacyLocation)context.__mtPreviewLocation=()=>new URL('https://child.example/page');
            context.window=context;context.self=context;context.top=context.parent;
            vm.createContext(context);
            for(const match of html.matchAll(/<script\b[^>]*>([\s\S]*?)<\/script>/g))vm.runInContext(match[1],context,{timeout:1000});
            if(parentHandshake){
              const data={type:'mt-refresh-browser-state',force:true,visible:true,context:{...saved,targetRevision:8}};
              for(const listener of [...(listeners.get('message')||[])])listener({source:foreignParentHandshake?{}:context.parent,data});
            }
            assert.equal(sockets.length,connected?1:0);
            if(connected){
              const socket=sockets[0];
              assert.equal(socket.pathname,(nested?parent+middlePath:'')+'/ws/browser');
              assert.equal(socket.searchParams.get('routeKey'),attachment.routeKey);
              assert.equal(socket.searchParams.get('previewId'),attachment.previewId);
              assert.equal(socket.searchParams.get('token'),attachment.previewToken);
              assert.equal(socket.searchParams.get('sessionId'),attachment.sessionId);
              assert.equal(socket.searchParams.get('targetRevision'),'8');
              assert.equal(context.__mtPreviewLocation(location).href,'https://'+route+'.example/page');
              const retained=JSON.parse(context.name);
              assert.equal(retained.sessionId,attachment.sessionId);
              assert.equal(retained.targetRevision,8);
              assert.ok(document.cookie.includes('path='+parent+middlePath+(nested?child:'')+'/;'));
            }
            // Ordinary site iframe URLs remain explicitly suppressed, including
            // ones with their own /webpreview-looking application path.
            // The legacy singleton fixture models its already-initialized flag, not its DOM wrappers.
            if(!legacyProxy||connected)for(const path of ['/ordinary','/webpreview/application/path']){
              const frame=new Frame();frame.src=path;
              assert.equal(new URL(frame.src).searchParams.get('__mtSubframe'),'1');
            }
            """;
        var start = new ProcessStartInfo("node") {UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true};
        // Full double-injected documents exceed Windows' command-line limit.
        start.ArgumentList.Add("-");
        using var process = Process.Start(start)!;
        var ct = TestContext.Current.CancellationToken;
        var output = process.StandardOutput.ReadToEndAsync(ct);
        var errors = process.StandardError.ReadToEndAsync(ct);
        await process.StandardInput.WriteAsync(script.AsMemory(), ct);
        process.StandardInput.Close();
        try { await process.WaitForExitAsync(ct).WaitAsync(TimeSpan.FromSeconds(10), ct); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        await output;
        Assert.True(process.ExitCode == 0, await errors);
    }
}

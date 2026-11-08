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

    private static async Task RunAsync(string html, string identity, bool connected, bool nested, bool middle = false)
    {
        // Execute every complete, server-generated script in parser order. No future
        // script nodes are visible to the prepended adapter in this fixture.
        var setup = "const html=" + JsonSerializer.Serialize(html)
            + ";const identity=" + JsonSerializer.Serialize(identity)
            + ";const connected=" + JsonSerializer.Serialize(connected)
            + ";const nested=" + JsonSerializer.Serialize(nested)
            + ";const middle=" + JsonSerializer.Serialize(middle) + ";";
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
            const sockets=[],messages=[];
            const location=new URL('https://outer.example'+parent+middlePath+(nested?child:'')+'/page?__mtTargetRevision=8'+(nested?'&__mtSubframe=1':''));
            location.assign=()=>{};location.replace=()=>{};location.reload=()=>{};
            class Element{setAttribute(name,value){this[name]=value;}getAttribute(){return null;}hasAttribute(){return false;}}
            class Frame extends Element{get src(){return this.value;}set src(value){this.value=value;}}
            class Xhr{open(){}abort(){}send(){}}
            class Socket{constructor(url){sockets.push(new URL(url));this.readyState=0;}send(){}}
            const document={cookie:'',hidden:false,visibilityState:'visible',documentElement:{},querySelector:()=>null,querySelectorAll:()=>[],addEventListener:()=>{},hasFocus:()=>false,baseURI:location.href};
            const context={URL,URLSearchParams,location,document,Element,XMLHttpRequest:Xhr,WebSocket:Socket,
              HTMLIFrameElement:Frame,HTMLLinkElement:class extends Element{},HTMLAnchorElement:class extends Element{},HTMLFormElement:class extends Element{},
              HTMLObjectElement:class extends Element{},HTMLVideoElement:class extends Element{},
              MutationObserver:class{observe(){}},navigator:{},fetch:()=>{},open:()=>{},
              requestAnimationFrame:()=>1,cancelAnimationFrame:()=>{},setTimeout:()=>1,clearTimeout:()=>{},queueMicrotask:()=>{},
              addEventListener:()=>{},console:{log(){},info(){},warn(){},error(){},debug(){}},
              name:identity?JSON.stringify(saved):'',parent:{__mtProxy:true,postMessage:message=>messages.push(message)},
              history:{state:null,pushState(){},replaceState(_s,_t,url){location.href=new URL(url,location).href;}}};
            context.window=context;context.self=context;context.top=context.parent;
            vm.createContext(context);
            for(const match of html.matchAll(/<script\b[^>]*>([\s\S]*?)<\/script>/g))vm.runInContext(match[1],context,{timeout:1000});
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
            for(const path of ['/ordinary','/webpreview/application/path']){
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

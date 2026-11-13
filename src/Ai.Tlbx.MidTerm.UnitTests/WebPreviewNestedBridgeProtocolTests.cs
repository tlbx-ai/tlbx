using System.Diagnostics;
using System.Text.Json;
using Ai.Tlbx.MidTerm.Services.WebPreview;
using Xunit;

namespace Ai.Tlbx.MidTerm.UnitTests;

public sealed class WebPreviewNestedBridgeProtocolTests
{
    private const string Parent = "/webpreview/parent-route/_access/parent-id/parent-token";
    private const string Child = "/webpreview/child-route/_access/child-id/child-token";

    [Fact]
    public void StaticSrcdocInstrumentationPreservesRawTextApplicationMarkup()
    {
        const string html = "<script>const example=`<iframe srcdoc='&lt;p&gt;literal&lt;/p&gt;'></iframe>`;</script><!-- <iframe srcdoc='literal'> --><textarea><iframe srcdoc='literal'></iframe></textarea>";
        Assert.Equal(html, WebPreviewProxyMiddleware.MarkSubframeSources(html));
    }

    [Fact]
    public async Task CooperativeParentBindsExactFrameNonceDocumentAndCleansPendingNavigation()
    {
        await RunAsync(Document(Parent, "https://parent.example", "<html><head></head></html>"), "parent", true, nested: false, cooperative: true);
    }

    [Theory]
    [InlineData("<iframe srcdoc=\"&lt;p&gt;child&lt;/p&gt;\"></iframe>")]
    [InlineData("<iframe title='&gt;' srcdoc='&lt;html&gt;&lt;head&gt;&lt;/head&gt;&lt;p&gt;child&lt;/p&gt;&lt;/html&gt;'></iframe>")]
    public async Task StaticSrcdocUsesCredentialFreeCooperationWithSourceNonceReplayAndTeardownChecks(string html)
    {
        var rewritten = WebPreviewProxyMiddleware.MarkSubframeSources(html);
        var attribute = System.Text.RegularExpressions.Regex.Match(rewritten, "srcdoc=([\"'])(.*?)\\1", System.Text.RegularExpressions.RegexOptions.Singleline);
        var childHtml = System.Net.WebUtility.HtmlDecode(attribute.Groups[2].Value);
        var script = "const html=" + JsonSerializer.Serialize(childHtml) + ";" + """
            (async function(){
              const assert=require('node:assert/strict'),vm=require('node:vm');
              const listeners=new Map(),messages=[],timers=new Map();let nextTimer=0;
              const parent={postMessage:d=>messages.push(JSON.parse(JSON.stringify(d)))};
              const body={textContent:'child text'};
              const document={body,title:'child title',querySelectorAll:selector=>selector==='body'?[body]:[],createTreeWalker:()=>{
                let done=false;return {nextNode:()=>done?null:(done=true,{nodeType:3,data:body.textContent})};
              }};
              const context={document,crypto:require('node:crypto').webcrypto,NodeFilter:{SHOW_ALL:0},parent,
                setTimeout:fn=>{timers.set(++nextTimer,fn);return nextTimer;},clearTimeout:id=>timers.delete(id),
                addEventListener:(type,fn)=>listeners.set(type,fn),WebSocket:class{constructor(){throw Error('Child must never register a socket');}}};
              context.window=context;vm.createContext(context);
              for(const match of html.matchAll(/<script\b[^>]*>([\s\S]*?)<\/script>/g))vm.runInContext(match[1],context,{timeout:1000});
              const dispatch=(source,data)=>listeners.get('message')({source,data});
              const probe=(nonce)=>({type:'tlbx-child-dom-v1',phase:'probe',nonce});
              const nonce='1'.repeat(32);
              dispatch({},probe(nonce));assert.equal(messages.length,0);
              dispatch(parent,probe(nonce));assert.equal(messages.length,1);
              const ready=messages[0];assert.match(ready.documentId,/^[a-f0-9]{32}$/);
              const request={...ready,phase:'command',command:{command:'query',selector:'body',textOnly:true}};
              dispatch(parent,{...request,documentId:'0'.repeat(32)});assert.equal(messages.length,1);
              dispatch({},request);assert.equal(messages.length,1);
              dispatch(parent,request);await new Promise(setImmediate);
              assert.equal(messages.length,2);assert.equal(messages[1].result.result,'child text');assert.equal(messages[1].result.matchCount,1);
              dispatch(parent,request);await new Promise(setImmediate);assert.equal(messages.length,2);
              const execute=async(n,command)=>{
                const value=n.toString(16).padStart(32,'0');dispatch(parent,probe(value));const r=messages.at(-1);
                dispatch(parent,{...r,phase:'command',command});await new Promise(setImmediate);return messages.at(-1);
              };
              const written=await execute(2,{command:'exec',value:"document.body.textContent='changed'; document.title"});
              assert.equal(written.result.result,'child title');assert.equal(body.textContent,'changed');
              const failed=await execute(3,{command:'exec',value:"Promise.reject(new Error('child rejected'))"});
              assert.equal(failed.result.success,false);assert.equal(failed.result.error,'child rejected');
              const globals=await execute(4,{command:'exec',value:"[typeof mtCtx,typeof bws,typeof childBootstrap].join(',')"});
              assert.equal(globals.result.result,'undefined,undefined,undefined');
              const oversized=await execute(5,{command:'exec',value:"'x'.repeat(1000001)"});assert.equal(oversized.result.success,false);
              const unsupported=await execute(6,{command:'screenshot'});assert.equal(unsupported.result.success,false);
              const expired='7'.repeat(32);dispatch(parent,probe(expired));const expiredReady=messages.at(-1);
              const expire=timers.get(nextTimer);timers.delete(nextTimer);expire();const count=messages.length;
              dispatch(parent,{...expiredReady,phase:'command',command:{command:'exec',value:'42'}});await new Promise(setImmediate);assert.equal(messages.length,count);
              const pending='8'.repeat(32);dispatch(parent,probe(pending));const pendingReady=messages.at(-1);
              dispatch(parent,{...pendingReady,phase:'command',command:{command:'exec',value:'new Promise(resolve=>globalThis.finishChild=resolve)'}});
              listeners.get('pagehide')();const before=messages.length;context.finishChild('late');await new Promise(setImmediate);
              assert.equal(messages.length,before);assert.equal(timers.size,0);
            })().catch(error=>{console.error(error);process.exitCode=1;});
            """;
        await RunNodeAsync(script);
    }

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

    private static async Task RunAsync(string html, string identity, bool connected, bool nested, bool middle = false, string initialCookies = "", bool legacyProxy = false, bool legacyLocation = false, bool parentHandshake = false, bool foreignParentHandshake = false, bool cooperative = false)
    {
        // Execute every complete, server-generated script in parser order. No future
        // script nodes are visible to the prepended adapter in this fixture.
        var setup = "const html=" + JsonSerializer.Serialize(html)
            + ";const identity=" + JsonSerializer.Serialize(identity)
            + ";const connected=" + JsonSerializer.Serialize(connected)
            + ";const nested=" + JsonSerializer.Serialize(nested)
            + ";const cooperative=" + JsonSerializer.Serialize(cooperative)
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
            const sockets=[],connections=[],commandReplies=[],messages=[],listeners=new Map(),observers=[];
            const location=new URL('https://outer.example'+parent+middlePath+(nested?child:'')+'/page?__mtTargetRevision=8'+(nested?'&__mtSubframe=1':''));
            location.assign=()=>{};location.replace=()=>{};location.reload=()=>{};
            class Element{setAttribute(name,value){this[name]=value;}getAttribute(){return null;}hasAttribute(){return false;}}
            class Frame extends Element{
              constructor(){super();this.tagName='IFRAME';this.isConnected=true;this.events=new Map();}
              get src(){return this.value;}set src(value){this.value=value;}
              get srcdoc(){return this.html;}set srcdoc(value){this.html=value;}
              addEventListener(type,fn){this.events.set(type,fn);}removeEventListener(type){this.events.delete(type);}
            }
            class Xhr{open(){}abort(){}send(){}}
            class Socket{constructor(url){sockets.push(new URL(url));connections.push(this);this.readyState=0;}send(data){commandReplies.push(JSON.parse(data));}}
            const document={cookie:initialCookies,hidden:false,visibilityState:'visible',documentElement:{},querySelector:()=>null,querySelectorAll:()=>[],addEventListener:()=>{},hasFocus:()=>false,baseURI:location.href};
            const context={URL,URLSearchParams,DOMException,crypto:require('node:crypto').webcrypto,location,document,Element,XMLHttpRequest:Xhr,WebSocket:Socket,
              MutationObserver:class{constructor(callback){this.callback=callback;observers.push(this);}observe(){}disconnect(){this.disconnected=true;}},
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
            if(cooperative){
              const frame=new Frame(),calls=[];frame.contentWindow={postMessage:message=>calls.push(message)};
              document.querySelectorAll=selector=>selector==='#child'?[frame]:selector==='.ambiguous'?[frame,new Frame()]:[];
              const socket=connections[0];socket.readyState=1;Socket.OPEN=1;
              const dispatch=(id,selector='#child')=>socket.onmessage({data:JSON.stringify({id,command:'query',selector:'body',frame:selector})});
              const deliver=(source,data)=>{for(const listener of [...(listeners.get('message')||[])])listener({source,data});};
              dispatch('one');assert.equal(calls.length,1);const probe=calls[0];
              assert.match(probe.nonce,/^[a-f0-9]{32}$/);assert.equal(probe.phase,'probe');
              const ready={...probe,phase:'ready',documentId:'a'.repeat(32)};
              deliver({},ready);assert.equal(calls.length,1);
              deliver(frame.contentWindow,{...ready,nonce:'f'.repeat(32)});assert.equal(calls.length,1);
              deliver(frame.contentWindow,ready);assert.equal(calls.length,2);assert.equal(calls[1].phase,'command');
              assert.deepEqual(Object.keys(calls[1].command).sort(),['command','maxDepth','selector','textOnly','value']);
              assert(!JSON.stringify(calls).includes('parent-token'));
              deliver(frame.contentWindow,{...ready,phase:'result',documentId:'b'.repeat(32),result:{success:true,result:'wrong'}});
              assert.equal(commandReplies.length,0);
              deliver(frame.contentWindow,{...ready,phase:'result',result:{success:true,result:'child text',matchCount:1}});
              assert.equal(commandReplies.length,1);assert.equal(commandReplies[0].id,'one');assert.equal(commandReplies[0].result,'child text');
              assert.equal(commandReplies[0].previewId,'parent-id');assert(observers[0].disconnected);
              deliver(frame.contentWindow,{...ready,phase:'result',result:{success:true,result:'duplicate'}});assert.equal(commandReplies.length,1);
              dispatch('two');frame.events.get('load')();assert.equal(commandReplies.length,2);assert.equal(commandReplies[1].success,false);assert(observers[1].disconnected);
              dispatch('three');frame.isConnected=false;observers[2].callback([]);assert.equal(commandReplies.length,3);assert.equal(commandReplies[2].success,false);
              frame.isConnected=true;dispatch('four');observers[3].callback([{type:'attributes',target:frame}]);assert.equal(commandReplies.length,4);assert.equal(commandReplies[3].success,false);
              dispatch('ambiguous','.ambiguous');assert.equal(commandReplies.length,5);assert.equal(commandReplies[4].success,false);
              dispatch('close');socket.readyState=3;socket.onclose();assert(observers[4].disconnected);
              const dynamic=new Frame();dynamic.srcdoc='<html><head></head><body>child</body></html>';
              assert(dynamic.srcdoc.includes('data-tlbx-child-dom="1"'));assert(!dynamic.srcdoc.includes('parent-token'));
              dynamic.setAttribute('srcdoc','<p>runtime attribute</p>');assert(dynamic.srcdoc.includes('data-tlbx-child-dom="1"'));
            }
            """;
        await RunNodeAsync(script);
    }

    private static async Task RunNodeAsync(string script)
    {
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

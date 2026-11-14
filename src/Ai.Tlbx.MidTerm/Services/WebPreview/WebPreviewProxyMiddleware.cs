using Ai.Tlbx.MidTerm.Startup;
using System.Diagnostics;
using System.IO.Compression;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Ai.Tlbx.MidTerm.Models.WebPreview;
using Ai.Tlbx.MidTerm.Services;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.AspNetCore.WebUtilities;

namespace Ai.Tlbx.MidTerm.Services.WebPreview;

public sealed partial class WebPreviewProxyMiddleware
{
    private const string ProxyPrefix = "/webpreview";
    private const string PreviewBootstrapIdQueryParam = "__mtPreviewId";
    private const string PreviewBootstrapTokenQueryParam = "__mtPreviewToken";
    private const string PreviewTargetRevisionQueryParam = "__mtTargetRevision";
    private const string PreviewReloadTokenQueryParam = "__mtReloadToken";
    private const string PreviewSubframeQueryParam = "__mtSubframe";
    private const string InternalProxyRequestHeaderName = "X-MidTerm-Internal-Proxy";
    private const string InternalProxyRequestHeaderValue = "1";
    internal const string UpstreamUrlHeaderName = "X-Tlbx-Preview-Upstream-Url";
    private const int WsBufferSize = 8192;
    private static readonly TimeSpan WsCloseTimeout = TimeSpan.FromSeconds(5);
    private static readonly FileExtensionContentTypeProvider ContentTypeProvider = new();

    // Injected into proxied HTML to rewrite URLs in fetch/XHR/DOM at runtime.
    // Rewrites root-relative URLs to /webpreview/... and absolute external URLs
    // to /webpreview/_ext?u=... so all requests go through the MT proxy.
    // Patches: fetch, XHR, element .src/.href setters, setAttribute, window.open.
    private const string UrlRewriteScript = """
        <script data-mt-proxy-route="__MT_PROXY_ROUTE_ATTRIBUTE__">(function mtProxyAdapter(){
          var mtInjectionRoute="__MT_PROXY_ROUTE__";
          var mtParentContext=arguments.length?arguments[0]:null;
          // Transport coordinates must stay native beneath an older Location-rewriting proxy.
          var mtNativeLocation=window["location"];
          var mtInnerAdapterRoutes=/*__MT_INNER_ADAPTER_ROUTES__*/[];
          function mtRoutePosition(route){
            if(typeof route!=="string"||!/^\/webpreview\/[^/]+(?:\/_access\/[^/]+\/[^/]+)?$/.test(route))return -1;
            var path=mtNativeLocation.pathname||"",index=path.indexOf(route+"/");
            return index>=0?index:(path.endsWith(route)?path.length-route.length:-1);
          }
          // The outer transport prepends its adapter. Only the innermost matching
          // native adapter may patch this document; this marker grants no authority.
          function mtHasInnerAdapter(){
            var own=mtRoutePosition(mtInjectionRoute);
            if(own<0)return false;
            for(var i=0;i<mtInnerAdapterRoutes.length;i++){
              if(mtRoutePosition(mtInnerAdapterRoutes[i])>own)return true;
            }
            return false;
          }
          if(/^\/webpreview\/[^/]+\/_access\/[^/]+\/[^/]+$/.test(mtInjectionRoute)&&mtRoutePosition(mtInjectionRoute)<0)return;
          if(mtHasInnerAdapter())return;
          // An older outer adapter may already have set its global singleton.
          // Only this registered attachment may establish its own scoped adapter.
          function mtReadNativeParentContext(event){
            try{
              var d=event&&event.data,saved=d&&d.context;
              if(event.source!==window.parent||!d||d.type!=="mt-refresh-browser-state"||!saved)return null;
              var parts=mtInjectionRoute.match(/^\/webpreview\/([^/]+)\/_access\/([^/]+)\/([^/]+)$/);
              if(!parts||mtRoutePosition(mtInjectionRoute)<0
                ||saved.routeKey!==decodeURIComponent(parts[1])||saved.previewId!==decodeURIComponent(parts[2])
                ||saved.previewToken!==decodeURIComponent(parts[3])||typeof saved.sessionId!=="string"
                ||typeof saved.previewName!=="string"||!Number.isInteger(saved.targetRevision)||saved.targetRevision<0)return null;
              return saved;
            }catch(e){return null;}
          }
          if(window.__mtProxy&&window.__mtOwnedProxyRoute===mtInjectionRoute)return;
          if(window.__mtProxy&&!mtOwnNativeAttachment()){
            // Opaque cross-origin navigation may clear window.name. The owning
            // UI repeats the exact registered context in its existing load message.
            var resume=function(event){
              var context=mtReadNativeParentContext(event);if(!context)return;
              window.removeEventListener("message",resume);clearTimeout(resumeTimer);
              mtProxyAdapter(context);
            };
            window.addEventListener("message",resume);
            var resumeTimer=setTimeout(function(){window.removeEventListener("message",resume);},10000);
            return;
          }
          window.__mtProxy=1;window.__mtOwnedProxyRoute=mtInjectionRoute;
          // Save the real parent for navigation and cookie bridge notifications.
          var _realParent=window.parent;
          var mtNestedPreview=new URLSearchParams(mtNativeLocation.search).get("__mtSubframe")==="1";
          try{mtNestedPreview=mtNestedPreview||!!_realParent.__mtProxy;}catch(e){}
          function mtOwnNativeAttachment(){
            try{
              var parts=mtInjectionRoute.match(/^\/webpreview\/([^/]+)\/_access\/([^/]+)\/([^/]+)$/);
              if(!parts||mtRoutePosition(mtInjectionRoute)<0)return false;
              var saved=mtParentContext||(window.name?JSON.parse(window.name):null);
              return !!saved&&saved.routeKey===decodeURIComponent(parts[1])
                &&saved.previewId===decodeURIComponent(parts[2])&&saved.previewToken===decodeURIComponent(parts[3]);
            }catch(e){return false;}
          }
          // A registered child attachment is independent of its parent's preview.
          // Ordinary site frames lack this identity and keep subframe suppression.
          if(mtOwnNativeAttachment())mtNestedPreview=false;
          /*__MT_NESTED_DOM_BRIDGE__*/
          var mtCtx=null;
          function mtReadCookie(name){
            try{
              var parts=(document.cookie||"").split(/;\s*/);
              for(var i=0;i<parts.length;i++){
                if(parts[i].indexOf(name+"=")===0)return parts[i].slice(name.length+1);
              }
            }catch(e){}
            return "";
          }
          function mtReadBootstrapContext(){
            try{
              var params=new URLSearchParams(mtNativeLocation.search);
              var previewId=params.get("__mtPreviewId")||"";
              var previewToken=params.get("__mtPreviewToken")||"";
              var ownAccess=mtInjectionRoute.match(/^\/webpreview\/[^/]+\/_access\/([^/]+)\/([^/]+)$/);
              if(ownAccess){previewId=decodeURIComponent(ownAccess[1]);previewToken=decodeURIComponent(ownAccess[2]);}
              else if(!previewId||!previewToken){
                var access=(mtNativeLocation.pathname||"").match(/\/webpreview\/[^/]+\/_access\/([^/]+)\/([^/]+)(?:\/|$)/);
                if(access){previewId=access[1];previewToken=access[2];}
              }
              if(!previewId||!previewToken)return null;
              var context={previewId:previewId,previewToken:previewToken};
              try{
                var saved=mtParentContext||(window.name?JSON.parse(window.name):null);
                if(saved&&saved.previewId===previewId&&saved.previewToken===previewToken)
                  context=Object.assign({},saved,context);
              }catch(e){}
              return context;
            }catch(e){}
            return null;
          }
          function mtPersistPreviewContext(){
            if(mtNestedPreview)return;
            if(!mtCtx||!mtCtx.previewId||!mtCtx.previewToken)return;
            try{window.name=JSON.stringify(mtCtx);}catch(e){}
            try{
              var match=(mtNativeLocation.pathname||"").match(/^\/webpreview\/[^/]+/);
              var cookiePath=match?match[0]+"/":"/";
              var ownPosition=mtRoutePosition(mtInjectionRoute);
              if(ownPosition>=0)cookiePath=mtNativeLocation.pathname.slice(0,ownPosition)+mtInjectionRoute+"/";
              document.cookie="mt-preview-ctx="+encodeURIComponent(JSON.stringify(mtCtx))+"; path="+cookiePath+"; secure; samesite=lax";
            }catch(e){}
          }
          function mtReadReloadToken(){
            try{
              var params=new URLSearchParams(mtNativeLocation.search);
              return params.get("__mtReloadToken")||"";
            }catch(e){}
            return "";
          }
          var mtReloadToken=mtReadReloadToken();
          function mtStripBootstrapQuery(){
            try{
              var url=new URL(mtNativeLocation.href);
              if(!url.searchParams.has("__mtPreviewId")&&!url.searchParams.has("__mtPreviewToken")&&!url.searchParams.has("__mtTargetRevision")&&!url.searchParams.has("__mtReloadToken"))return;
              url.searchParams.delete("__mtPreviewId");
              url.searchParams.delete("__mtPreviewToken");
              url.searchParams.delete("__mtTargetRevision");
              url.searchParams.delete("__mtReloadToken");
              history.replaceState(history.state,"",url.pathname+url.search+url.hash);
            }catch(e){}
          }
          // Explicit navigation owns this attachment; an older shared route cookie must never replace it.
          mtCtx=mtReadBootstrapContext();
          if(!mtCtx){
            try{mtCtx=window.name?JSON.parse(window.name):null;}catch(e){mtCtx=null;}
          }
          if(!mtCtx){
            try{
              var mtCookieCtx=mtReadCookie("mt-preview-ctx");
              mtCtx=mtCookieCtx?JSON.parse(decodeURIComponent(mtCookieCtx)):null;
            }catch(e){mtCtx=null;}
          }
          if(mtCtx){
            var revisionParam=new URLSearchParams(mtNativeLocation.search).get("__mtTargetRevision");
            if(revisionParam!==null)mtCtx.targetRevision=Number(revisionParam);
          }
          mtPersistPreviewContext();
          mtStripBootstrapQuery();
          function mtMsg(type,extra){
            if(mtNestedPreview)return null;
            if(!mtCtx)return null;
            var msg=extra||{};
            msg.type=type;
            if(mtCtx.sessionId)msg.sessionId=mtCtx.sessionId;
            if(mtCtx.targetRevision!==undefined)msg.targetRevision=mtCtx.targetRevision;
            if(mtCtx.previewId)msg.previewId=mtCtx.previewId;
            if(mtCtx.previewToken)msg.previewToken=mtCtx.previewToken;
            return msg;
          }
          // Do not cloak parent/top/frameElement. Sites with consent/ad frame
          // coordination can enter postMessage loops when an embedded frame
          // pretends to be top-level.
          function mtProxyPrefix(){
            var path=mtNativeLocation.pathname||"";
            if(path===P||path.indexOf(P+"/")===0)return "";
            var idx=path.indexOf(P+"/");
            if(idx<0&&path.endsWith(P))idx=path.length-P.length;
            if(idx>0)return path.slice(0,idx);
            var match=path.match(/^\/webpreview\/[^/]+/);
            return match?match[0]:"";
          }
          var P="/webpreview",BP=mtProxyPrefix(),PP=BP+P,E=PP+"/_ext?u=";
          function syncProxyBase(){
            try{
              BP=mtProxyPrefix();PP=BP+P;E=PP+"/_ext?u=";
              var baseEl=document.querySelector("base");
              if(baseEl&&BP){
                var nativeBase=baseEl.getAttribute("href")||P+"/";
                if(nativeBase===P||nativeBase.indexOf(P+"/")===0)baseEl.setAttribute("href",BP+nativeBase);
              }
              else if(baseEl){
                var baseHref=baseEl.getAttribute("href")||"";
                if(baseHref.indexOf("/webpreview/")===0&&baseHref.indexOf(P+"/")!==0){
                  baseEl.setAttribute("href",P+baseHref);
                }
              }
            }catch(e){}
          }
          syncProxyBase();
          try{queueMicrotask(syncProxyBase);}catch(e){setTimeout(syncProxyBase,0);}
          document.addEventListener("DOMContentLoaded",syncProxyBase);
          function ar(u){
            if((!mtReloadToken&&!mtNestedPreview)||typeof u!=="string")return u;
            try{
              var parsed=new URL(u,mtNativeLocation.href);
              if(parsed.pathname===PP||parsed.pathname.indexOf(PP+"/")===0){
                if(mtReloadToken)parsed.searchParams.set("__mtReloadToken",mtReloadToken);
                if(mtNestedPreview)parsed.searchParams.set("__mtSubframe","1");
                if(/^(?:https?:|wss?:)/i.test(u))return parsed.toString();
                return parsed.pathname+parsed.search+parsed.hash;
              }
            }catch(e){}
            return u;
          }
          function frameUrl(u){
            var rewritten=r(u);
            if(typeof rewritten!=="string"||/^(?:data:|blob:|about:|javascript:|#)/i.test(rewritten))return rewritten;
            try{var url=new URL(rewritten,mtNativeLocation.href);url.searchParams.set("__mtSubframe","1");return url.href;}catch(e){return rewritten;}
          }
          function dprop(target,name,getter){
            if(!target)return false;
            try{Object.defineProperty(target,name,{configurable:true,get:getter});return true;}catch(e){}
            try{
              var proto=Object.getPrototypeOf(target);
              if(proto){Object.defineProperty(proto,name,{configurable:true,get:getter});return true;}
            }catch(e){}
            return false;
          }
          function mtStoragePrefix(name){
            var route="default";
            try{
              // P is the server-bound route even when Link adds its own prefix
              // before /webpreview. The displayed pathname is transport-specific.
              var match=P.match(/^\/webpreview\/([^/]+)/);
              if(match&&match[1])route=decodeURIComponent(match[1]);
            }catch(e){}
            return "__midterm_webpreview__"+route+"__"+encodeURIComponent(window.__mtTargetOrigin||"null")+"__"+name+"__";
          }
          var mtStorageBootstrap=/*__MT_STORAGE_BOOTSTRAP__*/null;
          var mtStorageFetch=window.fetch.bind(window),mtStoragePending=new Map(),mtStorageBatch=null,mtStorageFlight=null,mtStorageSequence=0,mtStorageTimer=0;
          function mtFlushStorage(){
            clearTimeout(mtStorageTimer);mtStorageTimer=0;
            if(mtStorageFlight)return mtStorageFlight.then(mtFlushStorage);
            if(!mtStorageBatch){
              if(!mtStoragePending.size)return Promise.resolve();
              mtStorageBatch={token:mtStorageBootstrap.token,sequence:++mtStorageSequence,operations:Array.from(mtStoragePending.values())};
              mtStoragePending.clear();
            }
            var abort=new AbortController(),timer=setTimeout(function(){abort.abort();},5000);
            mtStorageFlight=mtStorageFetch(new URL(PP+"/_storage",mtNativeLocation.href).href,{
              method:"POST",mode:"cors",credentials:"omit",headers:{"Content-Type":"application/json"},
              body:JSON.stringify(mtStorageBatch),signal:abort.signal
            }).then(function(response){
              if(!response.ok)throw new Error("Preview storage was not persisted (HTTP "+response.status+"); reload cancelled.");
              mtStorageBatch=null;
            },function(){
              throw new Error("Preview storage connection failed; reload cancelled.");
            }).finally(function(){clearTimeout(timer);mtStorageFlight=null;});
            return mtStorageFlight.then(mtFlushStorage);
          }
          function mtQueueStorage(store,action,key,value){
            if(action==="clear"){
              mtStoragePending.forEach(function(op,id){if(op.store===store)mtStoragePending.delete(id);});
            }
            var id=store+":"+(action==="clear"?"clear":"key:"+key);
            if(!mtStoragePending.has(id)&&mtStoragePending.size>=258)throw new DOMException("Preview storage pending-write quota exceeded","QuotaExceededError");
            var units=(key?key.length:0)+(value?value.length:0)+16;
            mtStoragePending.forEach(function(op,other){if(other!==id)units+=(op.key?op.key.length:0)+(op.value?op.value.length:0)+16;});
            if(units>131072)throw new DOMException("Preview storage pending-write quota exceeded","QuotaExceededError");
            mtStoragePending.set(id,{store:store,action:action,key:key,value:value});
            if(!mtStorageTimer)mtStorageTimer=setTimeout(function(){mtFlushStorage().catch(function(){});},40);
          }
          function mkDurableStore(name){
            var store=name==="localStorage"?"local":"session";
            var data=Object.assign(Object.create(null),mtStorageBootstrap[store]);
            function replace(next,action,key,value){
              var keys=Object.keys(next),units=0;
              keys.forEach(function(k){units+=k.length+next[k].length+16;});
              if(keys.length>128||units>65536)throw new DOMException("Preview storage quota exceeded","QuotaExceededError");
              mtQueueStorage(store,action,key,value);data=next;
            }
            var api={
              getItem:function(k){k=String(k);return Object.prototype.hasOwnProperty.call(data,k)?data[k]:null;},
              setItem:function(k,v){k=String(k);v=String(v);var next=Object.assign(Object.create(null),data);next[k]=v;replace(next,"set",k,v);},
              removeItem:function(k){k=String(k);if(!Object.prototype.hasOwnProperty.call(data,k))return;var next=Object.assign(Object.create(null),data);delete next[k];replace(next,"remove",k);},
              clear:function(){replace(Object.create(null),"clear");},
              key:function(i){var keys=Object.keys(data);i=Number(i)>>>0;return i<keys.length?keys[i]:null;}
            };
            Object.defineProperty(api,"length",{configurable:true,get:function(){return Object.keys(data).length;}});
            return new Proxy(api,{
              get:function(t,k){return typeof k!=="string"||k in t?Reflect.get(t,k):api.getItem(k)??undefined;},
              set:function(t,k,v){if(typeof k!=="string")return false;api.setItem(k,v);return true;},
              deleteProperty:function(t,k){if(typeof k==="string")api.removeItem(k);return true;},
              ownKeys:function(){return Object.keys(data);},
              getOwnPropertyDescriptor:function(t,k){return Object.prototype.hasOwnProperty.call(data,k)?{configurable:true,enumerable:true,writable:true,value:data[k]}:undefined;}
            });
          }
          function mkStore(name){
            // An older outer adapter may already expose a per-document memory
            // object. The real opaque origin must still use the durable seed.
            if(window.origin==="null"){
              if(mtStorageBootstrap)return mkDurableStore(name);
              throw new DOMException("Preview storage requires a registered document navigation","SecurityError");
            }
            var nativeStore=null,prefix=mtStoragePrefix(name);
            try{nativeStore=window[name];}catch(e){}
            if(!nativeStore||typeof nativeStore.getItem!=="function"||typeof nativeStore.setItem!=="function"){
              if(mtStorageBootstrap)return mkDurableStore(name);
              throw new DOMException("Preview storage requires a registered document navigation","SecurityError");
            }
            var api={
              getItem:function(k){return nativeStore.getItem(prefix+String(k));},
              setItem:function(k,v){nativeStore.setItem(prefix+String(k),String(v));},
              removeItem:function(k){nativeStore.removeItem(prefix+String(k));},
              clear:function(){
                var keys=[];
                for(var i=0;i<nativeStore.length;i++){
                  var key=nativeStore.key(i);
                  if(key&&key.indexOf(prefix)===0)keys.push(key);
                }
                keys.forEach(function(k){nativeStore.removeItem(k);});
              },
              key:function(i){
                var scoped=[];
                for(var j=0;j<nativeStore.length;j++){
                  var key=nativeStore.key(j);
                  if(key&&key.indexOf(prefix)===0)scoped.push(key.slice(prefix.length));
                }
                return i>=0&&i<scoped.length?scoped[i]:null;
              }
            };
            try{Object.defineProperty(api,"length",{configurable:true,get:function(){
              var count=0;
              for(var i=0;i<nativeStore.length;i++){
                var key=nativeStore.key(i);
                if(key&&key.indexOf(prefix)===0)count++;
              }
              return count;
            }});}catch(e){}
            return api;
          }
          function ensureStore(name){
            var scoped;
            try{scoped=mkStore(name);}catch(error){dprop(window,name,function(){throw error;});return null;}
            if(!dprop(window,name,function(){return scoped;})){
              try{window[name]=scoped;}catch(e){}
            }
            return scoped;
          }
          function mkSwContainer(){
            var reg={
              scope:(mtNativeLocation.origin||"null")+P+"/",
              active:null,installing:null,waiting:null,
              update:function(){return Promise.resolve();},
              unregister:function(){return Promise.resolve(false);},
              addEventListener:function(){},
              removeEventListener:function(){}
            };
            return {
              controller:null,
              ready:Promise.resolve(reg),
              register:function(){return Promise.resolve(reg);},
              getRegistration:function(){return Promise.resolve(undefined);},
              getRegistrations:function(){return Promise.resolve([]);},
              startMessages:function(){},
              addEventListener:function(){},
              removeEventListener:function(){},
              dispatchEvent:function(){return false;}
            };
          }
          function ensureServiceWorker(){
            var fallback=mkSwContainer();
            if(!dprop(navigator,"serviceWorker",function(){return fallback;})){
              try{navigator.serviceWorker=fallback;}catch(e){}
            }
            return fallback;
          }
          ensureStore("localStorage");
          ensureStore("sessionStorage");
          dprop(navigator,"cookieEnabled",function(){return true;});
          // r(u): rewrite a URL to go through the proxy (add /webpreview prefix or _ext proxy)
          function mtProxyUrl(u,p){
            return u===p||u.indexOf(p+"/")===0||u.indexOf(p+"?")===0||u.indexOf(p+"#")===0;
          }
          function r(u){
            if(typeof u!=="string")return u;
            // Non-network schemes must not recurse through relative URL resolution.
            if(u.startsWith("#")||(/^[a-z][a-z0-9+.-]*:/i.test(u)&&!/^https?:|^wss?:/i.test(u)))return u;
            if(u.startsWith("//"))return r(mtNativeLocation.protocol+u);
            if(BP&&mtProxyUrl(u,P))return ar(BP+u);
            if(BP&&mtProxyUrl(u,mtNativeLocation.origin+P))return ar(mtNativeLocation.origin+BP+u.substring(mtNativeLocation.origin.length));
            if(mtProxyUrl(u,PP)||mtProxyUrl(u,mtNativeLocation.origin+PP))return ar(u);
            if(window.__mtExternalDocument){
              try{return ar(E+encodeURIComponent(new URL(u,window.__mtDocumentUrl).href));}catch(e){}
            }
            if(!u.includes("://")&&!u.startsWith("/")&&!u.startsWith("//")){
              try{return r(new URL(u,document.baseURI).toString());}catch(e){}
            }
            if(u.startsWith("/")&&!u.startsWith(PP+"/")&&!u.startsWith(P+"/")&&!u.startsWith("//"))return ar(PP+u);
            if(u.startsWith("http://")||u.startsWith("https://")||u.startsWith("ws://")||u.startsWith("wss://")){
              try{var h=new URL(u);
                if(window.__mtTargetOrigin&&h.origin===window.__mtTargetOrigin)return ar(PP+h.pathname+h.search+h.hash);
                if(h.host===mtNativeLocation.host&&!h.pathname.startsWith(PP+"/"))return ar(h.protocol+"//"+ h.host+PP+h.pathname+h.search+h.hash);
                if(h.host!==mtNativeLocation.host){
                  return ar(E+encodeURIComponent(u));
                }
              }catch(e){}
            }
            return ar(u);
          }
          // === Network APIs ===
          // Patch only metadata getters. Keeping the native receiver preserves
          // Response branding, clone/body streams, and native illegal-invocation errors.
          function mtMetadataUrl(value){
            if(typeof value!=="string")return null;
            var upstream=mtUpstreamUrl(value,false);
            if(upstream!==null)return upstream;
            // A previous outer getter may already have removed its envelope.
            // Only our exact capability prefix at our registered inner origin
            // can take this fallback; unrelated origins/routes remain native.
            if(!BP||!/^\/webpreview\/[^/]+\/_access\/[^/]+\/[^/]+$/.test(P))return null;
            try{
              if(!mtCtx||!mtCtx.origin||new URL(value).origin!==new URL(mtCtx.origin).origin)return null;
              return mtUpstreamUrl(value,true);
            }catch(e){return null;}
          }
          window.__mtPreviewModuleUrl=function(value,finalUrl){
            if(mtMetadataUrl(value)===null)return value;
            try{var final=new URL(finalUrl);if(!/^https?:$/.test(final.protocol))return value;final.username="";final.password="";return final.href;}catch(e){return value;}
          };
          function mtResponseUrl(nativeUrl,readHeader){
            var upstream=mtMetadataUrl(nativeUrl);
            if(upstream===null)return nativeUrl;
            try{
              var finalUrl=readHeader("X-Tlbx-Preview-Upstream-Url");
              if(finalUrl){
                var final=new URL(finalUrl);
                if(/^https?:$/.test(final.protocol)){
                  // A nested proxy reports the inner transport as its final URL.
                  var inner=mtUpstreamUrl(final.href,true);
                  final=new URL(inner===null?final.href:inner);
                  final.username="";final.password="";
                  return final.href;
                }
              }
            }catch(e){}
            return upstream;
          }
          function mtInstallResponseUrl(proto,key,readHeader){
            if(!proto)return;
            var descriptor=Object.getOwnPropertyDescriptor(proto,key);
            if(!descriptor||!descriptor.configurable||!descriptor.get)return;
            var nativeGet=descriptor.get;
            Object.defineProperty(proto,key,Object.assign({},descriptor,{get:function(){
              var self=this,nativeUrl=nativeGet.call(self);
              return mtResponseUrl(nativeUrl,function(name){return readHeader.call(self,name);});
            }}));
          }
          var mtHeadersGet=window.Headers&&window.Headers.prototype.get;
          var mtResponseHeaders=window.Response&&Object.getOwnPropertyDescriptor(window.Response.prototype,"headers");
          if(mtHeadersGet&&mtResponseHeaders&&mtResponseHeaders.get){
            mtInstallResponseUrl(window.Response.prototype,"url",function(name){return mtHeadersGet.call(mtResponseHeaders.get.call(this),name);});
          }
          mtInstallResponseUrl(XMLHttpRequest.prototype,"responseURL",XMLHttpRequest.prototype.getResponseHeader);
          var F=window.fetch;
          function rfq(self,q,o){
            var ru=r(q.url),init={
              method:q.method,headers:q.headers,mode:q.mode,credentials:q.credentials,cache:q.cache,
              redirect:q.redirect,referrer:q.referrer,referrerPolicy:q.referrerPolicy,
              keepalive:q.keepalive,signal:q.signal
            };
            if(o)for(var k in o)init[k]=o[k];
            if(init.body!==undefined){
              try{if(q.duplex&&init.duplex===undefined)init.duplex=q.duplex;}catch(e){}
              return F.call(self,ru,init);
            }
            if(q.method==="GET"||q.method==="HEAD")return F.call(self,ru,init);
            return q.clone().arrayBuffer().then(function(body){
              init.body=body;
              try{if(q.duplex&&init.duplex===undefined)init.duplex=q.duplex;}catch(e){}
              return F.call(self,ru,init);
            },function(){return F.call(self,ru,init);});
          }
          function wrapCookieRefresh(p){
            if(!p||typeof p.then!=="function"){qrc();return p;}
            return p.then(function(v){qrc();return v;},function(err){qrc();throw err;});
          }
          function proxyFetch(self,u,o){
            if(typeof u==="string")return wrapCookieRefresh(F.call(self,r(u),o));
            if(u&&typeof u==="object"&&u.url){try{return wrapCookieRefresh(rfq(self,u,o));}catch(e){}}
            return wrapCookieRefresh(F.call(self,u,o));
          }
          window.fetch=function(u,o){
            var self=this;
            return cookieWrites?cookieWriteQueue.then(function(){return proxyFetch(self,u,o);}):proxyFetch(self,u,o);
          };
          var X=XMLHttpRequest.prototype.open;
          var synchronousXhrs=new WeakSet(),xhrSendVersions=new WeakMap();
          XMLHttpRequest.prototype.open=function(m,u){var a=[].slice.call(arguments);a[1]=r(u);xhrSendVersions.set(this,(xhrSendVersions.get(this)||0)+1);if(a[2]===false)synchronousXhrs.add(this);else synchronousXhrs.delete(this);return X.apply(this,a);};
          var XA=XMLHttpRequest.prototype.abort;
          XMLHttpRequest.prototype.abort=function(){xhrSendVersions.set(this,(xhrSendVersions.get(this)||0)+1);return XA.apply(this,arguments);};
          var XS=XMLHttpRequest.prototype.send;
          XMLHttpRequest.prototype.send=function(){
            var xhr=this,done=false;
            function onDone(){
              if(done)return;
              done=true;
              qrc();
              try{xhr.removeEventListener("loadend",onDone);}catch(e){}
            }
            try{xhr.addEventListener("loadend",onDone);}catch(e){}
            var sendArgs=arguments;
            if(cookieWrites&&!synchronousXhrs.has(xhr)){
              var sendVersion=xhrSendVersions.get(xhr);
              cookieWriteQueue.then(function(){if(xhrSendVersions.get(xhr)!==sendVersion){onDone();return;}try{XS.apply(xhr,sendArgs);}catch(err){onDone();xhr.dispatchEvent(new Event("error"));}});
              return;
            }
            try{return XS.apply(xhr,sendArgs);}catch(err){onDone();throw err;}
          };
          if(navigator.sendBeacon){var sb=navigator.sendBeacon.bind(navigator);navigator.sendBeacon=function(u,d){var ok=sb(r(u),d);if(ok)qrc();return ok;};}
          // === Element property setters ===
          function mtInstallScriptUrl(proto){
            if(!proto)return;
            var descriptor=Object.getOwnPropertyDescriptor(proto,"src");
            if(!descriptor||!descriptor.configurable||!descriptor.get)return;
            var previousGet=descriptor.get;
            Object.defineProperty(proto,"src",Object.assign({},descriptor,{get:function(){
              var value=previousGet.call(this),upstream=mtMetadataUrl(value);
              return upstream===null?value:upstream;
            }}));
          }
          mtInstallScriptUrl(window.HTMLScriptElement&&window.HTMLScriptElement.prototype);
          // .src on elements that load resources
          ["HTMLScriptElement","HTMLImageElement","HTMLIFrameElement","HTMLSourceElement","HTMLEmbedElement","HTMLVideoElement","HTMLAudioElement","HTMLInputElement","HTMLTrackElement"].forEach(function(n){
            var p=window[n]&&window[n].prototype;if(!p)return;
            var d=Object.getOwnPropertyDescriptor(p,"src");if(!d||!d.set)return;
            Object.defineProperty(p,"src",{set:function(v){d.set.call(this,n==="HTMLIFrameElement"?frameUrl(v):r(v));},get:d.get,configurable:true,enumerable:true});
          });
          // .href on link elements (stylesheets, preloads)
          var ld=Object.getOwnPropertyDescriptor(HTMLLinkElement.prototype,"href");
          if(ld&&ld.set){Object.defineProperty(HTMLLinkElement.prototype,"href",{set:function(v){ld.set.call(this,r(v));},get:ld.get,configurable:true,enumerable:true});}
          // .href on anchor elements
          var ad=Object.getOwnPropertyDescriptor(HTMLAnchorElement.prototype,"href");
          if(ad&&ad.set){Object.defineProperty(HTMLAnchorElement.prototype,"href",{set:function(v){ad.set.call(this,r(v));},get:ad.get,configurable:true,enumerable:true});}
          // .action on form elements
          var fd=Object.getOwnPropertyDescriptor(HTMLFormElement.prototype,"action");
          if(fd&&fd.set){Object.defineProperty(HTMLFormElement.prototype,"action",{set:function(v){fd.set.call(this,r(v));},get:fd.get,configurable:true,enumerable:true});}
          // .data on object elements
          var od=Object.getOwnPropertyDescriptor(HTMLObjectElement.prototype,"data");
          if(od&&od.set){Object.defineProperty(HTMLObjectElement.prototype,"data",{set:function(v){od.set.call(this,r(v));},get:od.get,configurable:true,enumerable:true});}
          // srcset rewriting: each entry is "url descriptor, ..." — rewrite each URL
          function rss(v){
            if(typeof v!=="string")return v;
            return v.replace(/(^|,\s*)([^\s,]+)/g,function(m,pre,url){return pre+r(url);});
          }
          // .srcset on img/source elements
          ["HTMLImageElement","HTMLSourceElement"].forEach(function(n){
            var p=window[n]&&window[n].prototype;if(!p)return;
            var d=Object.getOwnPropertyDescriptor(p,"srcset");if(!d||!d.set)return;
            Object.defineProperty(p,"srcset",{set:function(v){d.set.call(this,rss(v));},get:d.get,configurable:true,enumerable:true});
          });
          if(window.HTMLLinkElement){
            var lid=Object.getOwnPropertyDescriptor(HTMLLinkElement.prototype,"imageSrcset");
            if(lid&&lid.set){Object.defineProperty(HTMLLinkElement.prototype,"imageSrcset",{set:function(v){lid.set.call(this,rss(v));},get:lid.get,configurable:true,enumerable:true});}
          }
          // .poster on video elements
          var vpd=Object.getOwnPropertyDescriptor(HTMLVideoElement.prototype,"poster");
          if(vpd&&vpd.set){Object.defineProperty(HTMLVideoElement.prototype,"poster",{set:function(v){vpd.set.call(this,r(v));},get:vpd.get,configurable:true,enumerable:true});}
          // setAttribute for src/href/action/poster/data/formaction/srcset/imagesrcset
          var sa=Element.prototype.setAttribute;
          Element.prototype.setAttribute=function(n,v){
            if(/^integrity$/i.test(n))return;
            if(typeof v==="string"){
              if(/^src$/i.test(n)&&this.tagName==="IFRAME")v=frameUrl(v);
              else if(/^(src|href|action|poster|data|formaction)$/i.test(n))v=r(v);
              else if(/^(srcset|imagesrcset)$/i.test(n))v=rss(v);
              if(/^(src|href|action|poster|data|formaction|srcset|imagesrcset)$/i.test(n)){
                try{if(this.hasAttribute&&this.hasAttribute("integrity"))this.removeAttribute("integrity");}catch(e){}
              }
            }
            return sa.call(this,n,v);
          };
          // A normal preview gesture may navigate its own document, never the tlbx shell.
          function mtInnerTarget(element,name){
            if(!element||!element.getAttribute)return;
            var target=(element.getAttribute(name)||"").toLowerCase();
            if(target==="_top"||target==="_parent")element.setAttribute(name,"_self");
          }
          document.addEventListener("click",function(event){
            var element=event.target;
            if(element&&element.closest)mtInnerTarget(element.closest("a,area"),"target");
          },true);
          document.addEventListener("submit",function(event){
            mtInnerTarget(event.target,"target");
            mtInnerTarget(event.submitter,"formtarget");
          },true);
          var nativeFormSubmit=HTMLFormElement.prototype.submit;
          if(nativeFormSubmit)HTMLFormElement.prototype.submit=function(){mtInnerTarget(this,"target");return nativeFormSubmit.apply(this,arguments);};
          // === Constructors ===
          var wo=window.open;
          window.open=function(u){var a=[].slice.call(arguments);if(typeof u==="string")a[0]=r(u);return wo.apply(this,a);};
          var OWS=window.WebSocket;
          if(OWS&&window.Proxy){
            try{window.WebSocket=new Proxy(OWS,{construct:function(t,a){if(a&&a.length>0)a[0]=r(a[0]);return Reflect.construct(t,a);}});}catch(e){}
          }
          var OES=window.EventSource;
          if(OES&&window.Proxy){
            try{window.EventSource=new Proxy(OES,{construct:function(t,a){if(a&&a.length>0)a[0]=r(a[0]);return Reflect.construct(t,a);}});}catch(e){}
          }
          var OA=window.Audio;
          if(OA){window.Audio=function(u){return new OA(r(u));};window.Audio.prototype=OA.prototype;}
          var OI=window.Image;
          if(OI){window.Image=function(w,h){return new OI(w,h);};window.Image.prototype=OI.prototype;}
          // Worker/SharedWorker constructors
          if(window.Worker){
            var OW=window.Worker;
            window.Worker=function Worker(u,o){
              if(!new.target)throw new TypeError("Worker must be constructed with new");
              if(!arguments.length)return Reflect.construct(OW,[],new.target);
              if(typeof u==="symbol")throw new TypeError("Cannot convert a Symbol value to a string");
              var rewritten=r(String(u)),args=[rewritten],address;
              if(arguments.length>1)args.push(o);
              try{address=new URL(rewritten,document.baseURI).href;}catch(error){return Reflect.construct(OW,args,new.target);}
              if(window.origin!=="null"||!/^https?:/.test(address))return Reflect.construct(OW,args,new.target);
              // A blob created by this opaque document retains its isolation,
              // while its imports use the existing authorized resource proxy.
              var nonce=Math.random().toString(36).slice(2),module=o&&o.type==="module";
              var done=";self.postMessage({__tlbxWorkerLoaded:"+JSON.stringify(nonce)+"});";
              function loadModule(url,key){
                var pending=[];
                function hold(event){pending.push(event);event.stopImmediatePropagation();}
                self.addEventListener("message",hold);
                self.importScripts=function(){throw new TypeError("Module workers do not support importScripts");};
                import(url).then(function(){
                  self.removeEventListener("message",hold);
                  pending.forEach(function(event){self.dispatchEvent(new MessageEvent("message",{
                    data:event.data,origin:event.origin,lastEventId:event.lastEventId,ports:event.ports
                  }));});
                  self.postMessage({__tlbxWorkerLoaded:key});
                }).catch(function(error){setTimeout(function(){throw error;},0);});
              }
              // Chromium rejects a module blob entry in an opaque document.
              // Load its ESM once in an isolated classic entry and retain early messages.
              var source=module?"("+loadModule.toString()+")("+JSON.stringify(address)+","+JSON.stringify(nonce)+");"
                :"importScripts("+JSON.stringify(address)+");"+done;
              if(module)args[1]=new Proxy(o,{get:function(target,key){return key==="type"?"classic":Reflect.get(target,key,target);}});
              var objectUrl=URL.createObjectURL(new Blob([source],{type:"text/javascript"})),worker;
              args[0]=objectUrl;
              try{worker=Reflect.construct(OW,args,new.target);}catch(error){URL.revokeObjectURL(objectUrl);throw error;}
              function cleanup(event){
                if(event&&event.type==="message"){
                  if(!event.data||event.data.__tlbxWorkerLoaded!==nonce)return;
                  event.stopImmediatePropagation();
                }
                URL.revokeObjectURL(objectUrl);
                worker.removeEventListener("message",cleanup);
                worker.removeEventListener("error",cleanup);
              }
              worker.addEventListener("message",cleanup);
              worker.addEventListener("error",cleanup);
              var terminate=worker.terminate;
              worker.terminate=function(){if(this===worker)cleanup();return terminate.apply(this,arguments);};
              return worker;
            };
            window.Worker.prototype=OW.prototype;
            Object.setPrototypeOf(window.Worker,OW);
          }
          if(window.SharedWorker){var OSW=window.SharedWorker;window.SharedWorker=function(u,o){return new OSW(r(u),o);};window.SharedWorker.prototype=OSW.prototype;}
          // Service worker registration
          var mtsw=ensureServiceWorker();
          if(mtsw&&mtsw.register){
            var swr=mtsw.register.bind(mtsw);
            mtsw.register=function(u,o){return swr(r(u),o);};
          }
          // === Navigation APIs ===
          function mtUpstreamSearch(search){
            if(!search)return "";
            var entries=search.substring(1).split("&").filter(function(entry){
              var key=entry.split("=",1)[0];
              try{key=decodeURIComponent(key.replace(/\+/g," "));}catch(e){}
              return ["__mtPreviewId","__mtPreviewToken","__mtTargetRevision","__mtReloadToken","__mtSubframe"].indexOf(key)<0;
            });
            return entries.length?"?"+entries.join("&"):"";
          }
          // Invert only this attachment's transport. Never strip controls from
          // the embedded _ext URL: those query names can belong to the real site.
          function mtUpstreamUrl(value,innerTransport){
            if(!value)return null;
            var transport;
            try{transport=new URL(value);}catch(e){return null;}
            var prefix=PP;
            if(innerTransport&&BP&&mtProxyUrl(transport.pathname,P))prefix=P;
            else if(transport.origin!==mtNativeLocation.origin)return null;
            if(!mtProxyUrl(transport.pathname,prefix))return null;
            if(transport.pathname===prefix+"/_ext"){
              try{
                var ext=new URLSearchParams(transport.search).get("u");
                if(ext){
                  var external=new URL(ext);
                  if(!/^https?:$/.test(external.protocol))return null;
                  external.username="";external.password="";
                  if(transport.hash)external.hash=transport.hash;
                  return external.href;
                }
              }catch(e){}
              return null;
            }
            var path=transport.pathname.substring(prefix.length)||"/";
            return (window.__mtTargetOrigin||"")+path+mtUpstreamSearch(transport.search)+transport.hash;
          }
          function curU(){
            return mtUpstreamUrl(mtNativeLocation.href,false)||mtNativeLocation.href;
          }
          // Frameworks pass document.baseURI together with mtNativeLocation.href to the
          // server. Both must use upstream coordinates or Blazor rejects its circuit.
          function mtDocumentBaseURI(){
            try{
              var base=new URL(document.baseURI);
              if(base.pathname===PP+"/_ext")return new URL(base.searchParams.get("u")).href;
              if(base.pathname===PP||base.pathname.indexOf(PP+"/")===0){
                var path=base.pathname.substring(PP.length)||"/";
                return new URL(path+mtUpstreamSearch(base.search)+base.hash,window.__mtTargetOrigin||curU()).href;
              }
              return base.href;
            }catch(e){return document.baseURI;}
          }
          var mtDocumentView={};
          Object.defineProperty(mtDocumentView,"baseURI",{get:mtDocumentBaseURI});
          ["URL","documentURI"].forEach(function(key){Object.defineProperty(mtDocumentView,key,{get:curU});});
          window.__mtPreviewDocument=function(value){return value===document?mtDocumentView:value;};
          // import() does not call window.fetch. Route computed absolute/root
          // module URLs through this preview; lexical relative imports stay native.
          // Leave non-string inputs untouched so native coercion/rejection survives.
          window.__mtPreviewImport=function(value){
            if(typeof value!=="string"||!/^(?:https?:\/\/|\/)/i.test(value))return value;
            // A module loader can resolve relative to its script rather than the
            // document. Hand it an absolute transport URL, including nested proxies.
            try{return new URL(r(value),mtNativeLocation.href).href;}catch(e){return value;}
          };
          // Application Location is upstream-facing; the bridge keeps the native proxy URL.
          function mtNavigationUrl(value){
            var text=String(value);
            // HTML rewriting may already have converted an inline redirect.
            if(mtProxyUrl(text,P)||mtProxyUrl(text,PP)
              ||mtProxyUrl(text,mtNativeLocation.origin+P)||mtProxyUrl(text,mtNativeLocation.origin+PP))return r(text);
            return r(new URL(text,curU()).href);
          }
          function mtNavigateAfterStorage(url,replace){
            function navigate(){if(replace)mtNativeLocation.replace(url);else mtNativeLocation.assign(url);}
            if(!mtStorageBatch&&!mtStorageFlight&&!mtStoragePending.size){navigate();return;}
            mtFlushStorage().then(navigate).catch(function(error){console.error(error.message);});
          }
          var mtLocationView={};
          ["href","origin","protocol","host","hostname","port","pathname","search","hash"].forEach(function(key){
            Object.defineProperty(mtLocationView,key,{enumerable:true,get:function(){
              return new URL(curU())[key];
            },set:key==="origin"?undefined:function(value){
              if(key==="href"){mtNavigateAfterStorage(mtNavigationUrl(value),false);return;}
              var next=new URL(curU());next[key]=value;mtNavigateAfterStorage(r(next.href),false);
            }});
          });
          mtLocationView.assign=function(url){mtNavigateAfterStorage(mtNavigationUrl(url),false);};
          mtLocationView.replace=function(url){mtNavigateAfterStorage(mtNavigationUrl(url),true);};
          mtLocationView.reload=function(){mtFlushStorage().then(function(){mtNativeLocation.reload();}).catch(function(e){console.error(e.message);});};
          mtLocationView.toString=function(){return curU();};
          mtLocationView[Symbol.toPrimitive]=function(){return curU();};
          window.__mtPreviewLocation=function(value){
            if(value===window.location)return mtLocationView;
            try{
              if(value===window.top.location||value===window.parent.location){
                // An application's top-level navigation belongs to its preview,
                // never to the surrounding tlbx control page.
                try{if(window.parent.__mtProxy)return window.parent.__mtPreviewLocation(window.parent.location);}catch(e){}
                return mtLocationView;
              }
            }catch(e){}
            return value;
          };
          function postMt(type,extra){
            var msg=mtMsg(type,extra);
            if(!msg)return;
            try{_realParent.postMessage(msg,"*");}catch(e){}
          }
          var lastMtNavigationKey="",navNotifyTimer=0;
          function ntfyNow(){
            var upstreamUrl=curU();
            var navKey=mtNativeLocation.href+"\n"+upstreamUrl;
            if(navKey===lastMtNavigationKey)return;
            lastMtNavigationKey=navKey;
            postMt("mt-navigation",{url:mtNativeLocation.href,targetOrigin:window.__mtTargetOrigin||"",upstreamUrl:upstreamUrl});
          }
          function ntfy(){
            if(navNotifyTimer)return;
            navNotifyTimer=setTimeout(function(){
              navNotifyTimer=0;
              ntfyNow();
            },50);
          }
          var hps=history.pushState.bind(history),hrs=history.replaceState.bind(history);
          function mtHistoryArgs(values){
            var args=Array.from(values),url=args[2];
            // Defer URL conversion to the native binding, preserving title/URL
            // conversion order, argument validation and exactly-once coercion.
            if(args.length>2&&url!=null)args[2]={toString:function(){
              if(typeof url==="symbol")throw new TypeError("Cannot convert a Symbol value to a string");
              return r(String(url));
            }};
            return args;
          }
          history.pushState=function(s,t,u){var x=hps.apply(null,mtHistoryArgs(arguments));ntfy();return x;};
          history.replaceState=function(s,t,u){var x=hrs.apply(null,mtHistoryArgs(arguments));ntfy();return x;};
          var la=mtNativeLocation.assign.bind(mtNativeLocation),lr=mtNativeLocation.replace.bind(mtNativeLocation);
          mtNativeLocation.assign=function(u){return la(r(u));};
          mtNativeLocation.replace=function(u){return lr(r(u));};
          window.addEventListener("popstate",ntfy);
          window.addEventListener("hashchange",ntfy);
          setTimeout(ntfyNow,0);
          // === Cookie bridge ===
          var cc=/*__MT_INITIAL_COOKIES__*/"",cookieSeq=0,cookiePending={},cookieRefreshTimer=0;
          var cookieWriteQueue=Promise.resolve(),cookieWrites=0,cookieWriteVersion=0;
          window.addEventListener("message",function(ev){
            var d=ev.data;
            if(!d||d.type!=="mt-cookie-response")return;
            if(mtCtx&&((mtCtx.previewId||"")!==(d.previewId||"")||(mtCtx.previewToken||"")!==(d.previewToken||"")))return;
            var done=cookiePending[d.requestId];
            if(!done)return;
            delete cookiePending[d.requestId];
            done(d.error?null:d);
          });
          function reqCookie(action,raw,upstreamUrl){
            return new Promise(function(resolve){
              upstreamUrl=upstreamUrl||curU();
              var msg=mtMsg("mt-cookie-request",{requestId:"c"+(++cookieSeq),action:action,raw:raw||"",upstreamUrl:upstreamUrl});
              if(!msg){resolve(null);return;}
              if(_realParent===window){
                try{
                  var cu=PP+"/_cookies?u="+encodeURIComponent(upstreamUrl);
                  var fo={method:action==="set"?"POST":"GET"};
                  if(action==="set"){
                    fo.headers={"Content-Type":"application/json"};
                    fo.body=JSON.stringify({raw:raw||""});
                  }
                  F.call(window,cu,fo).then(function(r){return r.ok?r.json():null;}).then(resolve).catch(function(){resolve(null);});
                  return;
                }catch(e){}
              }
              cookiePending[msg.requestId]=resolve;
              try{_realParent.postMessage(msg,"*");}catch(e){delete cookiePending[msg.requestId];resolve(null);return;}
              setTimeout(function(){
                if(cookiePending[msg.requestId]){
                  delete cookiePending[msg.requestId];
                  resolve(null);
                }
              },5000);
            });
          }
          function rc(){var version=cookieWriteVersion;return reqCookie("get").then(function(j){if(!cookieWrites&&version===cookieWriteVersion&&j)cc=j.header||"";}).catch(function(){});}
          function qrc(){
            if(cookieRefreshTimer)return;
            cookieRefreshTimer=setTimeout(function(){
              cookieRefreshTimer=0;
              rc();
            },0);
          }
          rc();
          try{
            var d=Object.getOwnPropertyDescriptor(Document.prototype,"cookie")||Object.getOwnPropertyDescriptor(HTMLDocument.prototype,"cookie");
            if(d&&d.configurable){
              Object.defineProperty(document,"cookie",{configurable:true,get:function(){return cc;},set:function(v){
                if(typeof v!=="string")return;
                var n=v.split(";")[0]||"";if(n){var i=n.indexOf("="),k=i>0?n.slice(0,i).trim():"";if(k){var p=cc?cc.split(/;\s*/):[];var nx=[];for(var z=0;z<p.length;z++){if(!p[z].startsWith(k+"="))nx.push(p[z]);}nx.push(n.trim());cc=nx.join("; ");}}
                var version=++cookieWriteVersion,url=curU();cookieWrites++;
                cookieWriteQueue=cookieWriteQueue.then(function(){return reqCookie("set",v,url);}).then(function(j){if(version===cookieWriteVersion&&j&&typeof j.header==="string")cc=j.header;}).catch(function(){}).then(function(){cookieWrites--;});
              }});
            }
          }catch(e){}
          // === MutationObserver: catch dynamically added elements ===
          function rewriteEl(el){
            if(!el.getAttribute)return;
            ["src","href","action","data","formaction","poster"].forEach(function(attr){
              var v=el.getAttribute(attr);
              if(v){var rv=attr==="src"&&el.tagName==="IFRAME"?frameUrl(v):r(v);if(rv!==v)sa.call(el,attr,rv);}
            });
            try{
              if(el.hasAttribute&&el.hasAttribute("integrity"))el.removeAttribute("integrity");
            }catch(e){}
            var ss=el.getAttribute("srcset");
            if(ss){var rv=rss(ss);if(rv!==ss)sa.call(el,"srcset",rv);}
            var iss=el.getAttribute("imagesrcset");
            if(iss){var irv=rss(iss);if(irv!==iss)sa.call(el,"imagesrcset",irv);}
            // <meta http-equiv="refresh" content="0;url=/path"> — PHP redirect pattern
            if(el.tagName==="META"&&/^refresh$/i.test(el.getAttribute("http-equiv")||"")){
              var ct=el.getAttribute("content")||"";
              var rm=ct.match(/^(\d+\s*;\s*url\s*=\s*)(.+)$/i);
              if(rm){var ru=r(rm[2].trim());sa.call(el,"content",rm[1]+ru);}
            }
          }
          new MutationObserver(function(muts){
            for(var i=0;i<muts.length;i++){
              var nodes=muts[i].addedNodes;
              for(var j=0;j<nodes.length;j++){
                var n=nodes[j];if(n.nodeType!==1)continue;
                rewriteEl(n);
                if(n.querySelectorAll){
                  var els=n.querySelectorAll("[src],[href],[action],[data],[formaction],[poster],[srcset],[imagesrcset],[integrity],meta[http-equiv]");
                  for(var k=0;k<els.length;k++)rewriteEl(els[k]);
                }
              }
            }
          }).observe(document.documentElement,{childList:true,subtree:true});
          // Installed before upstream scripts and always on, so arguments are read
          // without running page getters or toJSON and each entry costs a bounded walk.
          var mtLog=window.__mtLog=[],mtLogDropped=0,mtLogBusy=false,mtLogEntryMax=2000;
          var mtErrorStackGetter=Object.getOwnPropertyDescriptor(new Error(),"stack");
          mtErrorStackGetter=mtErrorStackGetter&&mtErrorStackGetter.get;
          var mtDomNameGetter=Object.getOwnPropertyDescriptor(DOMException.prototype,"name").get;
          var mtDomMessageGetter=Object.getOwnPropertyDescriptor(DOMException.prototype,"message").get;
          function mtLogData(v,key){
            for(var obj=v,n=0;obj&&n<32;obj=Object.getPrototypeOf(obj),n++){
              var desc=Object.getOwnPropertyDescriptor(obj,key);
              if(desc)return "value" in desc?desc.value:
                desc.get&&(desc.get===mtDomNameGetter||desc.get===mtDomMessageGetter)?desc.get.call(v):undefined;
            }
          }
          function mtLogValue(v,depth,path,budget){
            var type=typeof v;
            if(type==="string"){var str=v.slice(0,mtLogEntryMax+1);return depth?JSON.stringify(str):str;}
            if(v===null||(type!=="object"&&type!=="function"))return String(v);
            // Object#toString invokes Symbol.toStringTag getters; inspect descriptors first.
            var customTag=mtLogData(v,Symbol.toStringTag);
            var tag=typeof customTag==="string"?customTag:
              Array.isArray(v)?"Array":v instanceof Error?"Error":
              v instanceof Date?"Date":v instanceof RegExp?"RegExp":type==="function"?"Function":"Object";
            if(tag==="Error"||tag==="DOMException"){
              var name=mtLogData(v,"name"),message=mtLogData(v,"message"),stack=mtLogData(v,"stack");
              var stackDesc=Object.getOwnPropertyDescriptor(v,"stack");
              if(typeof name==="string"&&typeof message==="string"&&stackDesc&&mtErrorStackGetter&&stackDesc.get===mtErrorStackGetter)stack=mtErrorStackGetter.call(v);
              var head=(typeof name==="string"?name:"Error")+": "+(typeof message==="string"?message:"");
              stack=typeof stack==="string"?stack:"";
              return stack.indexOf(head)===0?stack:stack?head+"\n"+stack:head;
            }
            if(tag==="Date"||tag==="RegExp")return String(v);
            if(tag!=="Array"&&tag!=="Object")return"["+tag+"]";
            var isArray=tag==="Array",open=isArray?"[":"{",close=isArray?"]":"}";
            if(path.indexOf(v)>=0)return"[Circular]";
            if(depth>=4)return open+"..."+close;
            path.push(v);
            var keys=isArray?null:Object.keys(v),count=keys?keys.length:v.length,parts=[],start=budget.left;
            for(var i=0;i<count;i++){
              if(budget.left<=0){parts.push("...");break;}
              var key=keys?keys[i]:i,prop=Object.getOwnPropertyDescriptor(v,key);
              var text=!prop?"undefined":"value" in prop?mtLogValue(prop.value,depth+1,path,budget):"[getter]";
              budget.left-=text.length+1;
              parts.push(keys?JSON.stringify(key)+":"+text:text);
            }
            path.pop();
            // The caller charges the returned text once, so release the children's interim charges.
            budget.left=start;
            return open+parts.join(",")+close;
          }
          function mtLogPush(level,args){
            if(mtLogBusy)return;
            mtLogBusy=true;
            var budget={left:mtLogEntryMax},parts=[];
            for(var i=0;i<args.length&&budget.left>0;i++){
              var text;
              try{text=mtLogValue(args[i],0,[],budget);}catch(e){text="[unserializable]";}
              budget.left-=text.length+1;
              parts.push(text);
            }
            mtLogBusy=false;
            // Stack frames carry the proxy route and preview capability; show the upstream origin instead.
            var message=parts.join(" ")
              .replace(/https?:\/\/[^\s/]+\/webpreview\/[^\s/?#]+(?:\/_access\/[^\s/?#]+\/[^\s/?#]+)?/g,window.__mtTargetOrigin||"")
              .replace(/[?&]__mtPreview(?:Id|Token)=[^&\s:)]*/g,"");
            if(message.length>mtLogEntryMax)message=message.slice(0,mtLogEntryMax)+"...[truncated]";
            mtLog.push({l:level,m:message,t:Date.now()});
            if(mtLog.length>50){mtLog.shift();mtLogDropped++;}
          }
          ["error","warn","log"].forEach(function(level){
            var original=console[level];
            console[level]=function(){
              mtLogPush(level,arguments);
              return original.apply(console,arguments);
            };
          });
          window.addEventListener("error",function(ev){
            mtLogPush("error",ev.error instanceof Error
              ?["Uncaught",ev.error]
              :[ev.message+" ("+(ev.filename||"")+":"+(ev.lineno||0)+":"+(ev.colno||0)+")"]);
          });
          window.addEventListener("unhandledrejection",function(ev){
            mtLogPush("error",["Unhandled rejection:",ev.reason]);
          });
          // Browser command channel: WebSocket to /ws/browser for agent-driven interaction
          var bws,bwsReady=false,captureLoad=null;
          /*__MT_QUERY_HELPERS__*/
          function semanticOutline(root,mx){
            var lines=[],visited=0,count=0,chars=0,truncated=false,selectorCache=new Map(),selectorChecks=0;
            function unique(selector,el){
              var scope=el.getRootNode?el.getRootNode():document,cache=selectorCache.get(scope);
              if(!cache){cache=new Map();selectorCache.set(scope,cache);}
              if(!cache.has(selector)){if(selectorChecks++>=1200)return false;var matches=scope.querySelectorAll(selector);cache.set(selector,matches.length===1?matches[0]:null);}
              return cache.get(selector)===el;
            }
            function localSelectorFor(el){
              var tag=CSS.escape(el.localName),candidate,classSelector=tag;
              for(var i=0;i<Math.min(el.classList.length,3);i++){var cls=el.classList.item(i);if(cls.length<=128)classSelector+="."+CSS.escape(cls);}
              if(el.id&&el.id.length<=256){candidate="#"+CSS.escape(el.id);if(unique(candidate,el))return candidate;}
              var scope=el.closest("[data-session-id]"),prefix=scope&&scope!==el?'[data-session-id='+CSS.escape(scope.getAttribute("data-session-id"))+'] ':"";
              for(var attr of ["data-testid","data-request-id","data-session-id","data-action","data-tab","name","aria-label"]){
                var value=el.getAttribute(attr);if(!value||value.length>256)continue;
                candidate=tag+"["+attr+"="+CSS.escape(value)+"]";
                if(prefix&&unique(prefix+candidate,el))return prefix+candidate;
                if(unique(candidate,el))return candidate;
                candidate=classSelector+"["+attr+"="+CSS.escape(value)+"]";
                if(prefix&&unique(prefix+candidate,el))return prefix+candidate;
                if(unique(candidate,el))return candidate;
              }
              candidate=classSelector;
              if(prefix&&unique(prefix+candidate,el))return prefix+candidate;
              if(unique(candidate,el))return candidate;
              var path="";
              for(var n=el;n&&n.nodeType===1;n=previewParent(n)){
                var index=1;for(var p=n.previousElementSibling;p;p=p.previousElementSibling)if(p.localName===n.localName&&p.namespaceURI===n.namespaceURI)index++;
                var segment=n.id&&n.id.length<=256&&unique("#"+CSS.escape(n.id),n)?"#"+CSS.escape(n.id):CSS.escape(n.localName)+":nth-of-type("+index+")";
                path=segment+(path?" > "+path:"");
                if(path.length>1024)return null;
                if(unique(path,el))return path;
              }
              return null;
            }
            function selectorFor(el){
              var parts=[];
              for(var n=el;n;){
                var local=localSelectorFor(n);if(!local)return null;parts.unshift(local);
                if(parts.length>32||parts.join(" >>> ").length>4096)return null;
                var scope=n.getRootNode&&n.getRootNode();n=scope&&scope.host;
              }
              return parts.join(" >>> ");
            }
            function compact(text){return String(text||"").slice(0,160).replace(/[\uE000-\uF8FF]/g,"").replace(/\s+/g," ").trim();}
            function labelText(el){
              var walker=document.createTreeWalker(el,NodeFilter.SHOW_ALL),text="",node,seen=0;
              while((node=walker.nextNode())&&seen++<200&&text.length<160){
                if(node.nodeType===3&&!node.parentElement.closest('input,textarea,select,script,style,template,[aria-hidden="true"]'))text+=node.data.slice(0,160-text.length)+" ";
              }
              return compact(text);
            }
            function keyboardTarget(el){return el.hasAttribute("tabindex")&&el.tabIndex>=0||el.hasAttribute("contenteditable")&&el.isContentEditable;}
            function label(el){
              var direct=compact(el.getAttribute("aria-label"));if(direct)return direct;
              if(el.tagName==="DETAILS"){var summary=el.querySelector(":scope > summary");if(summary)return labelText(summary);}
              var refs=(el.getAttribute("aria-labelledby")||"").slice(0,512).split(/\s+/).slice(0,8),names=[];
              var labelScope=el.getRootNode?el.getRootNode():document;
              for(var id of refs){var named=labelScope.getElementById?labelScope.getElementById(id):null;if(named)names.push(labelText(named));}
              if(names.join("").trim())return compact(names.join(" "));
              if(el.labels&&el.labels.length)return compact(Array.from(el.labels).slice(0,4).map(labelText).join(" "));
              var fallback=compact(el.getAttribute("alt")||el.getAttribute("title")||el.getAttribute("data-tooltip")||el.getAttribute("placeholder"));
              if(/^(BUTTON|A|SUMMARY|H[1-6]|LEGEND|OPTION)$/.test(el.tagName)||/^(button|link|menuitem|tab|option)$/.test(el.getAttribute("role")||"")||keyboardTarget(el))return labelText(el)||fallback;
              return fallback;
            }
            var stack=[{el:root,depth:0,index:-1,nextDepth:0}];
            while(stack.length){
              var frame=stack[stack.length-1],el=frame.el;
              if(frame.index<0){
                if(visited++>=3000||count>=200||chars>=40000){truncated=true;break;}
                var css=getComputedStyle(el),role=(el.getAttribute("role")||"").slice(0,80),shadow=previewOpenShadow(el);
                if(/^(HEAD|SCRIPT|STYLE|LINK|META|TEMPLATE|NOSCRIPT)$/.test(el.tagName)||el.hidden||el.inert||el.getAttribute("aria-hidden")==="true"||css.display==="none"||css.contentVisibility==="hidden"||Number(css.opacity)===0){stack.pop();continue;}
                if((/^(hidden|clip)$/.test(css.overflowX)&&el.clientWidth===0)||(/^(hidden|clip)$/.test(css.overflowY)&&el.clientHeight===0)){stack.pop();continue;}
                frame.children=shadow?Array.from(el.children).concat(Array.from(shadow.children)):el.children;
                var semantic=el===root||!!shadow||/^(MAIN|NAV|ASIDE|HEADER|FOOTER|SECTION|ARTICLE|FORM|FIELDSET|DIALOG|H[1-6]|BUTTON|A|INPUT|TEXTAREA|SELECT|DETAILS|SUMMARY|CANVAS)$/.test(el.tagName)||!!role&&role!=="none"&&role!=="presentation"||keyboardTarget(el);
                var emit=semantic&&(el===root||isControlVisible(el));
                if(emit&&frame.depth<=mx){
                  var name=label(el),selector=selectorFor(el),states=[];
                  if(shadow)states.push("open-shadow-root");
                  if(keyboardTarget(el))states.push("focusable");
                  if(el.hasAttribute("contenteditable")&&el.isContentEditable)states.push("editable");
                  if(el.matches(":disabled")||previewClosest(el,'[aria-disabled="true"]'))states.push("disabled");
                  if(el.readOnly)states.push("readonly");
                  if(el.tagName==="DETAILS")states.push("expanded="+el.open);
                  for(var state of ["aria-expanded","aria-selected","aria-pressed","aria-checked"]){var value=el.getAttribute(state);if(value!==null)states.push(state.slice(5)+"="+value.slice(0,40));}
                  if(el.tagName==="INPUT"&&/^(checkbox|radio)$/.test(el.type))states.push("checked="+el.checked);
                  if(el===previewActiveElement())states.push("focused");
                  var line="  ".repeat(frame.depth)+el.localName+(role?" role="+role:"")+(el.tagName==="INPUT"?" type="+el.type:"")+(name?" "+JSON.stringify(name):"")+(states.length?" ["+states.join(", ")+"]":"")+" selector="+(selector?JSON.stringify(selector):"(unique selector unavailable within bounds; narrow the root)");
                  if(chars+line.length>40000){truncated=true;break;}
                  lines.push(line);chars+=line.length+1;count++;
                }
                frame.nextDepth=frame.depth+(emit?1:0);frame.index=0;
                if(emit&&frame.depth>=mx){if(frame.children.length)truncated=true;frame.index=frame.children.length;}
                else if(emit&&/^(BUTTON|A|INPUT|TEXTAREA|SELECT)$/.test(el.tagName)){frame.children=shadow?shadow.children:[];}
              }
              if(frame.index>=frame.children.length){stack.pop();continue;}
              var child=frame.children[frame.index++];
              if(el.tagName==="DETAILS"&&!el.open&&child.tagName!=="SUMMARY")continue;
              stack.push({el:child,depth:frame.nextDepth,index:-1,nextDepth:frame.nextDepth});
            }
            if(truncated)lines.push("[outline truncated: "+count+" entries, "+visited+" nodes visited; narrow selector or adjust maxDepth]");
            return {text:lines.join("\n")||"(no visible semantic elements)",count:count};
          }
          function settleHiddenCaptureTransitions(){
            if(!document.hidden||typeof CSSTransition!=="function"||typeof document.getAnimations!=="function")return;
            document.getAnimations().forEach(function(a){
              if(a instanceof CSSTransition&&a.playState==="running"&&a.effect&&Number.isFinite(a.effect.getComputedTiming().endTime)){
                try{a.finish();}catch(e){}
              }
            });
          }
          function captureBackground(){
            var root=getComputedStyle(document.documentElement),opaque=/^rgb\([\d\s.,]+\)$/;
            if(root.backgroundImage!=="none")return undefined;
            if(opaque.test(root.backgroundColor))return root.backgroundColor;
            if(root.backgroundColor!=="transparent"&&!/^rgba\([^,]+,[^,]+,[^,]+,\s*0(?:\.0+)?\)$/.test(root.backgroundColor))return undefined;
            var body=document.body?getComputedStyle(document.body):null;
            return body&&body.backgroundImage==="none"&&opaque.test(body.backgroundColor)?body.backgroundColor:undefined;
          }
          function captureSurfaceInventory(clip){
            var region=clip==="viewport"?{x:window.scrollX,y:window.scrollY,width:window.innerWidth,height:window.innerHeight}:clip;
            var media={frames:0,unreadableFrames:0,canvases:0,videos:0};
            document.querySelectorAll("iframe,canvas,video").forEach(function(el){
              var rect=el.getBoundingClientRect(),css=getComputedStyle(el);
              if(rect.width<=0||rect.height<=0||css.visibility==="hidden"||css.visibility==="collapse")return;
              for(var n=el;n&&n.nodeType===1;n=n.parentElement){
                var style=getComputedStyle(n);
                if(style.display==="none"||Number(style.opacity)===0)return;
              }
              var x=rect.left+window.scrollX,y=rect.top+window.scrollY;
              if(region&&(x+rect.width<=region.x||y+rect.height<=region.y||x>=region.x+region.width||y>=region.y+region.height))return;
              if(el.tagName==="IFRAME"){
                media.frames++;
                try{if(!el.contentDocument||!el.contentDocument.documentElement)media.unreadableFrames++;}catch(e){media.unreadableFrames++;}
              }else if(el.tagName==="CANVAS")media.canvases++;
              else media.videos++;
            });
            return {renderer:"dom-svg",pixelCoverage:media.unreadableFrames?"partial":"unverified",inventory:"parent-light-dom",mediaPixels:"unverified",nestedMedia:"unknown",media:media};
          }
          function ensureCapture(){
            if(window.__tlbxDomCapture&&typeof window.__tlbxDomCapture.snapdom==="function")return Promise.resolve(window.__tlbxDomCapture.snapdom);
            if(captureLoad)return captureLoad;
            // Keep this host asset inside the authenticated preview route. Link's
            // isolated origin routes root-relative fetches to the upstream site.
            captureLoad=F.call(window,PP+"/_assets/dom-capture.min.js",{credentials:"same-origin"}).then(function(resp){
              if(!resp.ok)throw new Error("failed to fetch screenshot renderer");
              return resp.text();
            }).then(function(text){
              return new Promise(function(resolve,reject){
                var blob=new Blob([text],{type:"text/javascript"});
                var blobUrl=URL.createObjectURL(blob);
                var scr=document.createElement("script");
                scr.src=blobUrl;
                scr.onload=function(){
                  URL.revokeObjectURL(blobUrl);scr.remove();
                  if(!window.__tlbxDomCapture||typeof window.__tlbxDomCapture.snapdom!=="function"){reject(new Error("screenshot renderer did not initialize"));return;}
                  resolve(window.__tlbxDomCapture.snapdom);
                };
                scr.onerror=function(){URL.revokeObjectURL(blobUrl);scr.remove();reject(new Error("failed to load screenshot renderer"));};
                document.head.appendChild(scr);
              });
            }).catch(function(err){captureLoad=null;throw err;});
            return captureLoad;
          }
          /*__MT_CONTROL_HELPERS__*/
          /*__MT_CHILD_PARENT_BRIDGE__*/
          async function handleBCmd(msg){
            if(msg.frame!=null){delegateChildCommand(msg);return;}
            var res={id:msg.id,success:true,result:null,error:null,matchCount:null,sessionId:mtCtx&&mtCtx.sessionId?mtCtx.sessionId:null,previewId:mtCtx&&mtCtx.previewId?mtCtx.previewId:null};
            try{
              if(msg.command==="reload"||msg.command==="navigate"||msg.command==="flushstorage")await mtFlushStorage();
              switch(msg.command){
                case"flushstorage":res.result="flushed";break;
                case"query":{
                  if(!msg.selector){res.success=false;res.error="selector required";break;}
                  var els=previewQueryAll(msg.selector);
                  res.matchCount=els.length;
                  var parts=[],mx=msg.maxDepth||0,budget=queryBudget(),shown=0;
                  if(mx<0)throw new Error("maxDepth must be non-negative");
                  for(var i=0;i<els.length&&i<50;i++){
                    if(budget.nodes<=0||budget.chars<32){budget.truncated=true;break;}
                    parts.push(msg.textOnly?queryText(els[i],budget):truncEl(els[i],mx,budget));shown++;budget.chars-=5;
                  }
                  if(shown<els.length||budget.truncated)parts.push("[query truncated: "+shown+" of "+els.length+" matches; narrow selector/maxDepth, or use browser_snapshot for the full document]");
                  res.result=parts.join("\n---\n");
                  break;}
                case"click":{
                  if(!msg.selector){res.success=false;res.error="selector required";break;}
                  var el=resolveControl(msg.selector,true),surface=clickSurface(el),target=surface.target;
                  controlIntent(target,function(){assertClickEnabled(el,surface);},surface.point);
                  // One native activation owns label forwarding, checkbox/radio
                  // state, bubbling, input/change and preventDefault behavior.
                  if(typeof target.click==="function")target.click();
                  else target.dispatchEvent(new MouseEvent("click",{bubbles:true,cancelable:true,composed:true,button:0}));
                  res.result="clicked";
                  break;}
                case"scroll":{
                  var s=(msg.selector||"").trim();
                  var el=!s||s==="window"||s==="document"||s==="body"
                    ? (document.scrollingElement||document.documentElement)
                    : previewQueryOne(s);
                  if(!el){res.success=false;res.error="scroll target not found: "+s;break;}
                  var raw=(msg.value||"").trim().toLowerCase();
                  var dx=0,dy=0,mode="by";
                  if(!raw){dy=600;}
                  else if(raw==="top"||raw==="bottom"||raw==="left"||raw==="right"){mode=raw;}
                  else{
                    var parts=raw.split(/[,\s]+/).filter(Boolean);
                    dy=parseFloat(parts[0]||"0")||0;
                    dx=parseFloat(parts[1]||"0")||0;
                  }
                  if(mode==="top")el.scrollTop=0;
                  else if(mode==="bottom")el.scrollTop=el.scrollHeight;
                  else if(mode==="left")el.scrollLeft=0;
                  else if(mode==="right")el.scrollLeft=el.scrollWidth;
                  else if(typeof el.scrollBy==="function")el.scrollBy({top:dy,left:dx,behavior:"auto"});
                  else{el.scrollTop+=dy;el.scrollLeft+=dx;}
                  el.dispatchEvent(new Event("scroll",{bubbles:true}));
                  window.dispatchEvent(new Event("scroll"));
                  res.result=JSON.stringify({
                    selector:s||"window",
                    scrollTop:el.scrollTop,
                    scrollLeft:el.scrollLeft,
                    scrollHeight:el.scrollHeight,
                    scrollWidth:el.scrollWidth,
                    clientHeight:el.clientHeight,
                    clientWidth:el.clientWidth
                  });
                  break;}
                case"wheel":{
                  var s=(msg.selector||"").trim();
                  var dy=Number.isFinite(msg.deltaY)?msg.deltaY:120;
                  var dx=Number.isFinite(msg.deltaX)?msg.deltaX:0;
                  var pageTarget=!s||s==="window"||s==="document"||s==="body";
                  var el=pageTarget?(document.scrollingElement||document.documentElement):previewQueryOne(s);
                  if(pageTarget&&document.body){
                    var body=document.body,vertical=Math.abs(dy)>=Math.abs(dx),bodyStyle=getComputedStyle(body);
                    var bodyRange=vertical?body.scrollHeight-body.clientHeight:body.scrollWidth-body.clientWidth;
                    var rootRange=vertical?el.scrollHeight-el.clientHeight:el.scrollWidth-el.clientWidth;
                    if(bodyRange>0&&/^(auto|scroll|overlay)$/.test(vertical?bodyStyle.overflowY:bodyStyle.overflowX)&&(s==="body"||rootRange<=0))el=body;
                  }
                  if(!el){res.success=false;res.error="wheel target not found: "+s;break;}
                  var steps=Math.max(1,Math.min(100,Math.trunc(msg.steps||1)));
                  var metrics=function(){
                    var maxTop=Math.max(0,el.scrollHeight-el.clientHeight);
                    var maxLeft=Math.max(0,el.scrollWidth-el.clientWidth);
                    return {
                      scrollTop:el.scrollTop,
                      scrollLeft:el.scrollLeft,
                      scrollHeight:el.scrollHeight,
                      scrollWidth:el.scrollWidth,
                      clientHeight:el.clientHeight,
                      clientWidth:el.clientWidth,
                      maxScrollTop:maxTop,
                      atTop:el.scrollTop<=1,
                      atBottom:maxTop-el.scrollTop<=1,
                      progress:maxTop>0?el.scrollTop/maxTop:1
                    };
                  };
                  (async function(){
                    try{
                      var before=metrics(),samples=[],cancelled=0;
                      for(var i=0;i<steps;i++){
                        var accepted=el.dispatchEvent(new WheelEvent("wheel",{
                          bubbles:true,cancelable:true,composed:true,
                          deltaX:dx,deltaY:dy,deltaMode:WheelEvent.DOM_DELTA_PIXEL
                        }));
                        if(!accepted){cancelled++;}
                        else if(typeof el.scrollBy==="function")el.scrollBy({top:dy,left:dx,behavior:"instant"});
                        else{el.scrollTop+=dy;el.scrollLeft+=dx;}
                        await new Promise(function(resolve){setTimeout(resolve,16);});
                        samples.push(metrics());
                      }
                      res.result=JSON.stringify({
                        selector:s||"window",deltaX:dx,deltaY:dy,steps:steps,
                        synthetic:true,cancelledSteps:cancelled,before:before,after:metrics(),samples:samples
                      });
                    }catch(e){res.success=false;res.error="wheel failed: "+(e.message||String(e));}
                    bws.send(JSON.stringify(res));
                  })();
                  return;}
                case"fill":{
                  if(!msg.selector){res.success=false;res.error="selector required";break;}
                  var el=resolveControl(msg.selector),value=msg.value||"";
                  if(el.isContentEditable){fillEditableHost(el,value);res.result="filled";break;}
                  if(!el.matches("input,textarea,select"))throw new Error("fill requires an input, textarea, select or contenteditable editing host");
                  if(el.readOnly)throw new Error("fill target is read-only");
                  if(el.tagName==="INPUT"&&/^(file|checkbox|radio|button|submit|reset|image|hidden)$/i.test(el.type))throw new Error("fill does not support input type "+el.type+"; use the appropriate interaction");
                  if(el.tagName==="SELECT"&&!Array.from(el.options).some(function(option){return option.value===value&&!option.disabled&&!(option.parentElement.tagName==="OPTGROUP"&&option.parentElement.disabled);}))throw new Error("select has no enabled option with the requested value");
                  controlIntent(el);
                  if(el.readOnly)throw new Error("fill target became read-only");
                  setFormControlValue(el,value);
                  el.dispatchEvent(new Event("input",{bubbles:true}));
                  el.dispatchEvent(new Event("change",{bubbles:true}));
                  res.result="filled";
                  break;}
                case"exec":{
                  if(!msg.value){res.success=false;res.error="js code required";break;}
                  var rv=eval(msg.value);
                  Promise.resolve(rv).then(function(value){
                    res.result=value===undefined?"undefined":typeof value==="object"?JSON.stringify(value,function(key,item){return typeof item==="bigint"?String(item):item;}):String(value);
                  }).catch(function(error){
                    res.success=false;res.error=(error&&error.message)||String(error);
                  }).finally(function(){
                    if(bws.readyState===WebSocket.OPEN)bws.send(JSON.stringify(res));
                  });
                  return;}
                case"wait":{
                  if(!msg.selector){res.success=false;res.error="selector required";break;}
                  var waitState=msg.value||"attached";
                  if(["attached","visible","hidden","detached"].indexOf(waitState)<0){res.success=false;res.error="invalid wait state: "+waitState;break;}
                  var to=(msg.timeout||5)*1000,start=Date.now(),waitSocket=bws;
                  (function poll(){
                    // An old wait cannot answer through a replacement connection.
                    if(!waitSocket||waitSocket!==bws||waitSocket.readyState!==WebSocket.OPEN)return;
                    try{
                      var found=previewQueryAll(msg.selector),visible=false;
                      if(waitState==="visible"||waitState==="hidden"){
                        for(var wi=0;wi<found.length;wi++)if(isControlVisible(found[wi])){visible=true;break;}
                      }
                      var done=waitState==="attached"?found.length>0:waitState==="detached"?found.length===0:waitState==="visible"?visible:!visible;
                      if(done){res.result=waitState==="attached"?"found":waitState;res.matchCount=found.length;waitSocket.send(JSON.stringify(res));return;}
                      if(Date.now()-start>=to){res.success=false;res.error="timeout waiting for"+(waitState==="attached"?"":" "+waitState)+": "+msg.selector;waitSocket.send(JSON.stringify(res));return;}
                    }catch(error){res.success=false;res.error=(error&&error.message)||String(error);waitSocket.send(JSON.stringify(res));return;}
                    setTimeout(poll,200);
                  })();return;}
                case"screenshot":{
                  var fullPage=msg.fullPage===true;
                  var releaseCapture=null,captureCrop=null,capturedDocument=null;
                  Promise.resolve().then(function(){
                    settleHiddenCaptureTransitions();
                    return typeof window.__tlbxBeforeCapture==="function"?window.__tlbxBeforeCapture():null;
                  }).then(function(release){
                    releaseCapture=release;
                    return ensureCapture();
                  }).then(function(capture){
                    var clip=fullPage?null:"viewport",inventoryClip=clip,captureRoot=document.documentElement;
                    if(msg.selector!=null){
                      if(fullPage)throw new Error("screenshot selector cannot be combined with fullPage");
                      if(typeof msg.selector!=="string"||!msg.selector.trim())throw new Error("screenshot selector must not be empty");
                      var targets=previewQueryAll(msg.selector);
                      res.matchCount=targets.length;
                      if(targets.length!==1)throw new Error("screenshot selector must match exactly one element; matched "+targets.length);
                      var target=targets[0],bounds=target.getBoundingClientRect();
                      if(!isControlVisible(target)||bounds.width<=0||bounds.height<=0)throw new Error("screenshot target is hidden or has no visible area");
                      // Capturing the HTML root drops collapsed body margins in the renderer.
                      // Keep the body layout and overlaps, then crop in its capture viewBox.
                      if(document.body&&target!==document.documentElement)captureRoot=document.body;
                      inventoryClip={x:bounds.left+window.scrollX,y:bounds.top+window.scrollY,width:bounds.width,height:bounds.height};
                      var rootBounds=captureRoot.getBoundingClientRect();
                      captureCrop={x:bounds.left-rootBounds.left,y:bounds.top-rootBounds.top,width:bounds.width,height:bounds.height};
                      clip=null;
                    }
                    res.capture=captureSurfaceInventory(inventoryClip);
                    return capture(captureRoot,{
                      clip:clip,
                      dpr:1,
                      scale:1,
                      backgroundColor:captureBackground(),
                      fast:false,
                      reconcile:true,
                      // Image compression awaits bitmap decode(), which never settles in a hidden, never-painted document.
                      compress:false
                    });
                  }).then(function(capture){
                    capturedDocument=capture;
                    if(captureCrop){
                      var meta=capture.meta||{};
                      captureCrop.x+=Number(meta.contentX)||0;captureCrop.y+=Number(meta.contentY)||0;
                      return capture.toCanvas({crop:captureCrop});
                    }
                    return capture.toCanvas();
                  }).then(function(canvas){
                    var warnings=capturedDocument.warnings||[],codes=Object.create(null);
                    warnings.slice(0,50).forEach(function(w){
                      var code=w&&w.code;
                      if(typeof code==="string"&&/^[a-z][a-z0-9-]{0,63}$/.test(code))codes[code]=(codes[code]||0)+1;
                    });
                    res.capture.warningCodes=codes;
                    res.capture.warningsMayBeTruncated=warnings.length>=50;
                    res.capture.output={width:canvas.width,height:canvas.height};
                    if(warnings.length)res.capture.pixelCoverage="partial";
                    res.result=canvas.toDataURL("image/png");if(bws.readyState===WebSocket.OPEN)bws.send(JSON.stringify(res));
                  }).catch(function(e){
                    res.success=false;res.error="screenshot failed: "+e.message;if(bws.readyState===WebSocket.OPEN)bws.send(JSON.stringify(res));
                  }).finally(function(){
                    if(typeof releaseCapture==="function")releaseCapture();
                  });
                  return;}
                case"snapshot":{
                  res.result=previewSnapshot();
                  break;}
                case"navigate":{
                  if(!msg.value){res.success=false;res.error="url required";break;}
                  mtNativeLocation.href=mtNavigationUrl(msg.value);res.result="navigating";
                  break;}
                case"reload":{
                  if((msg.value||"")==="force"){
                    try{
                      var reloadUrl=new URL(mtNativeLocation.href);
                      mtReloadToken=Date.now().toString(36)+Math.random().toString(36).slice(2,8);
                      reloadUrl.searchParams.set("__mtReloadToken",mtReloadToken);
                      mtNativeLocation.replace(reloadUrl.toString());
                    }catch(e){
                      mtNativeLocation.reload();
                    }
                  }else{
                    mtNativeLocation.reload();
                  }
                  res.result="reloading";
                  break;}
                case"outline":{
                  var mx=msg.maxDepth==null?4:msg.maxDepth,root=document.body||document.documentElement;
                  if(mx<0)throw new Error("maxDepth must be non-negative");
                  if(msg.selector){var roots=previewQueryAll(msg.selector);if(roots.length!==1)throw new Error("outline selector must match exactly one root");root=roots[0];}
                  var outline=semanticOutline(root,mx);res.result=outline.text;res.matchCount=outline.count;
                  break;}
                case"attrs":{
                  if(!msg.selector){res.success=false;res.error="selector required";break;}
                  var els=previewQueryAll(msg.selector);
                  res.matchCount=els.length;
                  var parts=[];
                  for(var i=0;i<els.length&&i<30;i++){
                    var ae=els[i].cloneNode(false);ae.innerHTML="";
                    parts.push(ae.outerHTML.replace("></"+ae.tagName.toLowerCase()+">",">"));
                  }
                  res.result=parts.join("\n---\n");
                  break;}
                case"css":{
                  if(!msg.selector){res.success=false;res.error="selector required";break;}
                  if(!msg.value){res.success=false;res.error="css properties required (comma-separated)";break;}
                  var props=msg.value.split(",").map(function(p){return p.trim();});
                  var els=previewQueryAll(msg.selector);
                  res.matchCount=els.length;
                  var parts=[];
                  for(var i=0;i<els.length&&i<20;i++){
                    var cs=getComputedStyle(els[i]);
                    var lines=[msg.selector+" ("+(i+1)+" of "+els.length+")"];
                    for(var j=0;j<props.length;j++)lines.push("  "+props[j]+": "+cs.getPropertyValue(props[j]));
                    parts.push(lines.join("\n"));
                  }
                  res.result=parts.join("\n---\n");
                  break;}
                case"log":{
                  var flt=msg.value||"all";
                  var ent=mtLog.filter(function(e){return flt==="all"||e.l===flt;});
                  var lines=ent.map(function(e){return"["+e.l+"] "+e.m;});
                  if(mtLogDropped)lines.unshift("("+mtLogDropped+" older entries dropped)");
                  res.result=lines.length?lines.join("\n"):"(no entries)";
                  res.matchCount=ent.length;
                  break;}
                case"links":{
                  var anchors=previewQueryAll("a[href]");
                  var seen={},parts=[];
                  for(var i=0;i<anchors.length;i++){
                    var href=anchors[i].getAttribute("href");
                    if(!href||href==="#"||seen[href])continue;
                    seen[href]=1;
                    var txt=(anchors[i].textContent||"").trim().substring(0,80);
                    parts.push(href+" > "+txt);
                  }
                  parts.sort();
                  res.result=parts.join("\n");
                  res.matchCount=parts.length;
                  break;}
                case"submit":{
                  var fsel=msg.selector||"form";
                  var f=previewQueryOne(fsel);
                  if(!f){res.success=false;res.error="form not found: "+fsel;break;}
                  rc().finally(function(){
                    res.result="submitted";
                    bws.send(JSON.stringify(res));
                    setTimeout(function(){try{f.requestSubmit();}catch(e){f.submit();}},50);
                  });
                  return;}
                case"forms":{
                  var fsel=msg.selector||"form";
                  var forms=previewQueryAll(fsel);
                  res.matchCount=forms.length;
                  var parts=[];
                  for(var i=0;i<forms.length;i++){
                    var f=forms[i];
                    var ftag=f.tagName.toLowerCase();
                    var fid=f.id?"#"+f.id:"";
                    var fhdr=ftag+fid;
                    if(f.action)fhdr+=" (action="+f.getAttribute("action")+", method="+(f.method||"GET").toUpperCase()+")";
                    var flines=[fhdr];
                    var inputs=previewQueryAll("input,select,textarea,button",f);
                    for(var j=0;j<inputs.length;j++){
                      var inp=inputs[j];
                      var it=inp.tagName.toLowerCase();
                      var iname=inp.name?"[name="+inp.name+"]":"";
                      var itp=inp.type?" type="+inp.type:"";
                      var ireq=inp.required?" required":"";
                      var ival=it==="select"?" value=\""+(inp.options[inp.selectedIndex]||{}).text+"\"":
                               it==="button"?" \""+(inp.textContent||"").trim()+"\"":
                               " value=\""+((inp.type==="password"?"***":inp.value)||"")+"\"";
                      var ilbl="";
                      var labels=inp.labels;
                      if(labels&&labels.length)ilbl=" label=\""+Array.prototype.map.call(labels,function(le){return le.textContent.trim();}).join(" ")+"\"";
                      if(!ilbl&&inp.closest&&inp.closest("label"))ilbl=" label=\""+inp.closest("label").textContent.trim()+"\"";
                      flines.push("  "+it+iname+itp+ireq+ival+ilbl);
                    }
                    parts.push(flines.join("\n"));
                  }
                  res.result=parts.join("\n---\n");
                  break;}
                case"url":{
                  res.result=curU();
                  break;}
                case"ready":{
                  function readyAfterFrame(){
                    // A controllable background document can have suspended animation
                    // frames. DOM readiness must not wait forever for visible paint.
                    var completed=false,frame=0,timer=0;
                    function finishReady(){
                      if(completed)return;
                      completed=true;clearTimeout(timer);
                      if(frame)cancelAnimationFrame(frame);
                      res.result=curU();
                      if(bws.readyState===WebSocket.OPEN)bws.send(JSON.stringify(res));
                    }
                    frame=requestAnimationFrame(finishReady);
                    timer=setTimeout(finishReady,100);
                  }
                  if(document.readyState==="loading"){
                    document.addEventListener("DOMContentLoaded",readyAfterFrame,{once:true});
                  }else{
                    readyAfterFrame();
                  }
                  return;}
                case"clearcookies":{
                  var all=document.cookie.split(";");
                  for(var i=0;i<all.length;i++){var n=all[i].split("=")[0].trim();if(n)document.cookie=n+"=;expires=Thu,01 Jan 1970 00:00:00 GMT;path=/";}
                  cc="";res.result="cleared";
                  break;}
                case"clearstate":{
                  (async function(){
                    try{
                      var all=document.cookie.split(";");
                      for(var i=0;i<all.length;i++){var n=all[i].split("=")[0].trim();if(n)document.cookie=n+"=;expires=Thu,01 Jan 1970 00:00:00 GMT;path=/";}
                      cc="";
                      localStorage.clear();
                      sessionStorage.clear();
                      await mtFlushStorage();
                      try{
                        if(window.indexedDB&&typeof indexedDB.databases==="function"){
                          var dbs=await indexedDB.databases();
                          for(var i=0;i<dbs.length;i++){
                            var db=dbs[i];
                            if(db&&db.name){try{indexedDB.deleteDatabase(db.name);}catch(e){}}
                          }
                        }
                      }catch(e){}
                      try{
                        if(window.caches&&typeof caches.keys==="function"){
                          var keys=await caches.keys();
                          await Promise.all(keys.map(function(key){return caches.delete(key);}));
                        }
                      }catch(e){}
                      try{
                        if(navigator.serviceWorker&&navigator.serviceWorker.getRegistrations){
                          var regs=await navigator.serviceWorker.getRegistrations();
                          await Promise.all(regs.map(function(reg){return reg.unregister();}));
                        }
                      }catch(e){}
                      res.result="cleared state";
                    }catch(e){res.success=false;res.error=e.message||String(e);}
                    bws.send(JSON.stringify(res));
                  })();
                  return;}
                default:res.success=false;res.error="unknown command: "+msg.command;
              }
            }catch(e){res.success=false;res.error=e.message||String(e);}
            bws.send(JSON.stringify(res));
          }
          var bwsReconnectTimer=0,bwsStateKey="",bwsVisibleOverride=null;
          // Chromium can stop animation frames in an offscreen cross-origin
          // iframe even though its document and automation socket remain live.
          // Keep application rendering progressing for CLI-owned background previews.
          var mtNativeRaf=window.requestAnimationFrame.bind(window);
          var mtNativeCancelRaf=window.cancelAnimationFrame.bind(window);
          var mtPendingFrames=new Map(),mtFrameTimer=0;
          function mtBackgroundFrames(){
            return bwsVisibleOverride===false||document.hidden;
          }
          function mtScheduleBackgroundFrames(){
            if(mtFrameTimer||!mtPendingFrames.size||!mtBackgroundFrames())return;
            mtFrameTimer=setTimeout(function(){
              mtFrameTimer=0;
              if(!mtBackgroundFrames())return;
              var batch=Array.from(mtPendingFrames.entries());
              var timestamp=performance.now();
              for(var i=0;i<batch.length;i++){
                var id=batch[i][0],callback=mtPendingFrames.get(id);
                if(!callback)continue;
                mtPendingFrames.delete(id);
                mtNativeCancelRaf(id);
                try{callback.call(window,timestamp);}catch(error){setTimeout(function(){throw error;},0);}
              }
              mtScheduleBackgroundFrames();
            },50);
          }
          if(!mtNestedPreview){
            window.requestAnimationFrame=function(callback){
              if(typeof callback!=="function")return mtNativeRaf(callback);
              var id=mtNativeRaf(function(timestamp){
                var pending=mtPendingFrames.get(id);
                if(!pending)return;
                mtPendingFrames.delete(id);
                pending.call(window,timestamp);
              });
              mtPendingFrames.set(id,callback);
              mtScheduleBackgroundFrames();
              return id;
            };
            window.cancelAnimationFrame=function(id){
              id=Number(id)>>>0;
              mtPendingFrames.delete(id);
              mtNativeCancelRaf(id);
            };
            document.addEventListener("visibilitychange",mtScheduleBackgroundFrames);
          }
          function curBwsState(){
            var hasFocus=false,topLevel=false;
            try{hasFocus=!!document.hasFocus();}catch(e){}
            try{topLevel=window.top===window.self;}catch(e){}
            var visible=bwsVisibleOverride!==null?bwsVisibleOverride:document.visibilityState==="visible";
            return {visible:visible,focus:hasFocus,topLevel:topLevel};
          }
          function curBwsStateKey(){
            var s=curBwsState();
            return (s.visible?"1":"0")+(s.focus?"1":"0")+(s.topLevel?"1":"0");
          }
          function withBwsState(wsUrl){
            var s=curBwsState();
            wsUrl+=(wsUrl.indexOf("?")>=0?"&":"?")+"visible="+(s.visible?"1":"0")+"&focus="+(s.focus?"1":"0")+"&topLevel="+(s.topLevel?"1":"0");
            return wsUrl;
          }
          function schedBwsReconnect(delay){
            if(bwsReconnectTimer)return;
            bwsReconnectTimer=setTimeout(function(){bwsReconnectTimer=0;connectBws();},delay||0);
          }
          function refreshBwsState(force){
            var nextKey=curBwsStateKey();
            if(!force&&nextKey===bwsStateKey)return;
            bwsStateKey=nextKey;
            if(bws&&bws.readyState===1){
              var state=curBwsState();state.type="browser-state";
              if(mtStorageBootstrap)state.storageToken=mtStorageBootstrap.token;
              bws.send(JSON.stringify(state));
              return;
            }
            if(bws&&bws.readyState===0)return;
            schedBwsReconnect(50);
          }
          window.addEventListener("message",function(e){
            var d=e&&e.data;
            if(d&&d.type==="mt-refresh-browser-state"){
              var context=mtReadNativeParentContext(e);
              if(context&&mtNestedPreview){
                mtParentContext=context;mtCtx=context;mtNestedPreview=false;
                mtPersistPreviewContext();connectBws();
              }
              if(d.visible===true)bwsVisibleOverride=true;
              else if(d.visible===false)bwsVisibleOverride=false;
              mtScheduleBackgroundFrames();
              refreshBwsState(d.force===true);
            }
          });
          function connectBws(){
            if(mtNestedPreview)return;
            try{
              if(bws&&(bws.readyState===0||bws.readyState===1))return;
              var proto=mtNativeLocation.protocol==="https:"?"wss:":"ws:";
              var routeMatch=(P||"").match(/^\/webpreview\/([^/]+)/);
              if(!routeMatch){
                routeMatch=(mtNativeLocation.pathname||"").match(/^\/webpreview\/([^/]+)/);
              }
              var wsUrl=proto+"//"+mtNativeLocation.host+BP+"/ws/browser";
              if(routeMatch&&routeMatch[1]){
                wsUrl+=(wsUrl.indexOf("?")>=0?"&":"?")+"routeKey="+encodeURIComponent(routeMatch[1]);
              }
              if(mtCtx&&mtCtx.previewId&&mtCtx.previewToken){
                wsUrl+=(wsUrl.indexOf("?")>=0?"&":"?")+"previewId="+encodeURIComponent(mtCtx.previewId)+"&token="+encodeURIComponent(mtCtx.previewToken);
                if(mtCtx.targetRevision!==undefined)wsUrl+="&targetRevision="+encodeURIComponent(mtCtx.targetRevision);
                if(mtCtx.sessionId)wsUrl+="&sessionId="+encodeURIComponent(mtCtx.sessionId);
              }
              wsUrl=withBwsState(wsUrl);
              var stateKey=curBwsStateKey();
              bws=new OWS(wsUrl);
              bws.onopen=function(){bwsReady=true;bwsStateKey=stateKey;refreshBwsState(true);};
              bws.onmessage=function(e){try{handleBCmd(JSON.parse(e.data));}catch(ex){}};
              bws.onclose=function(){cancelChildCommands();bwsReady=false;bws=null;schedBwsReconnect(3000);};
              bws.onerror=function(){};
            }catch(e){}
          }
          document.addEventListener("visibilitychange",refreshBwsState);
          window.addEventListener("focus",refreshBwsState);
          window.addEventListener("blur",refreshBwsState);
          window.addEventListener("pageshow",refreshBwsState);
          connectBws();
        })();</script>
        """;

    [GeneratedRegex(@"<script\b[^>]*\sdata-mt-proxy-route=""(/webpreview/[^/""]+)""[^>]*>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ProxyAdapterRouteRegex();

    internal static string GetUrlRewriteScript(string routePrefix, string initialCookieHeader = "", string upstreamHtml = "",
        PreviewStorageBootstrap? storage = null)
    {
        // The parser has not reached the inner script when our prepended script
        // runs. Carry only its route key forward, never capabilities or context.
        var accessIndex = routePrefix.IndexOf("/_access/", StringComparison.Ordinal);
        var adapterRoute = accessIndex < 0 ? routePrefix : routePrefix[..accessIndex];
        var innerRoutes = ProxyAdapterRouteRegex().Matches(upstreamHtml)
            .Select(match => WebUtility.HtmlDecode(match.Groups[1].Value))
            .Distinct(StringComparer.Ordinal)
            .Select(route => $"\"{JsonEncodedText.Encode(route)}\"");
        return UrlRewriteScript.Replace("/*__MT_QUERY_HELPERS__*/", SharedQueryHelpers, StringComparison.Ordinal)
            .Replace("/*__MT_CONTROL_HELPERS__*/", SharedControlHelpers, StringComparison.Ordinal)
            .Replace("/*__MT_NESTED_DOM_BRIDGE__*/", "if(mtNestedPreview){" + GetCooperativeChildRuntime() + "}", StringComparison.Ordinal)
            .Replace("/*__MT_CHILD_PARENT_BRIDGE__*/", CooperativeParentBridge, StringComparison.Ordinal)
            .Replace("/*__MT_CHILD_BOOTSTRAP_JSON__*/null", "\"" + JsonEncodedText.Encode(GetCooperativeChildScript()) + "\"", StringComparison.Ordinal)
            .Replace(
            "__MT_PROXY_ROUTE_ATTRIBUTE__", WebUtility.HtmlEncode(adapterRoute), StringComparison.Ordinal).Replace(
            "__MT_PROXY_ROUTE__", JsonEncodedText.Encode(routePrefix).ToString(), StringComparison.Ordinal).Replace(
            "/*__MT_INNER_ADAPTER_ROUTES__*/[]", "[" + string.Join(",", innerRoutes) + "]", StringComparison.Ordinal).Replace(
            "/*__MT_STORAGE_BOOTSTRAP__*/null",
            $"JSON.parse(\"{JsonEncodedText.Encode(JsonSerializer.Serialize(storage, PreviewStorageJsonContext.Default.PreviewStorageBootstrap))}\")", StringComparison.Ordinal).Replace(
            "var P=\"/webpreview\"",
            $"var P=\"{routePrefix}\"",
            StringComparison.Ordinal).Replace(
                "/*__MT_INITIAL_COOKIES__*/\"\"",
                $"\"{JsonEncodedText.Encode(initialCookieHeader)}\"",
                StringComparison.Ordinal);
    }


    private static readonly HashSet<string> HopByHopHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade"
    };

    private static readonly HashSet<string> StrippedResponseHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Content-Security-Policy", "Content-Security-Policy-Report-Only",
        "X-Frame-Options", "Cross-Origin-Opener-Policy", "Cross-Origin-Embedder-Policy",
        "Cross-Origin-Resource-Policy",
        "Permissions-Policy", "Document-Policy", "Origin-Agent-Cluster", "Clear-Site-Data",
        "Set-Cookie"  // Cookies managed by server-side cookie jar, not forwarded to browser
    };

    // Headers that must NOT be forwarded from browser to upstream.
    // Everything else is forwarded (blocklist approach for maximum compatibility).
    private static readonly HashSet<string> BlockedRequestHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        // Hop-by-hop (also in HopByHopHeaders, but listed for completeness)
        "Connection", "Keep-Alive", "Proxy-Authenticate", "Proxy-Authorization",
        "TE", "Trailer", "Transfer-Encoding", "Upgrade",
        // Host is set by HttpClient from the request URI
        "Host",
        // Browser cookies are MT session cookies — upstream cookies come from CookieContainer
        "Cookie",
        // Preview targets are independent websites, not trusted reverse-proxy backends.
        // Never disclose the user's network or the supervising tlbx host.
        "Forwarded", "Via", "X-Real-IP",
        // Internal loop-prevention header is for server-originated requests only
        InternalProxyRequestHeaderName,
        // WebSocket negotiation headers managed by ClientWebSocket
        "Sec-WebSocket-Key", "Sec-WebSocket-Version", "Sec-WebSocket-Extensions",
        "Sec-WebSocket-Protocol",
        // Site/User describe the supervising page. Mode/Dest must survive so
        // a nested tlbx can distinguish document navigation from script fetch.
        "Sec-Fetch-Site", "Sec-Fetch-User",
        // Content headers are set on HttpContent, not the request
        "Content-Type", "Content-Length"
    };

    private readonly RequestDelegate _next;
    private readonly WebPreviewService _service;
    private readonly WebPreviewStorageService? _storage;

    // Learned path prefixes that should bypass subpath-prefixing and go to the
    // upstream server root. Stored per preview route key so unrelated browser
    // contexts do not pollute each other's fallback cache.
    private readonly Dictionary<string, Dictionary<string, bool>> _rootFallbackPrefixesByRoute = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string?> _rootFallbackTargetsByRoute = new(StringComparer.OrdinalIgnoreCase);

    public WebPreviewProxyMiddleware(RequestDelegate next, WebPreviewService service, WebPreviewStorageService? storage = null)
    {
        _next = next;
        _service = service;
        _storage = storage;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var originalPath = context.Request.Path;
        if (context.Items.ContainsKey(PreviewProxyAuthorization.PathPrefixItemKey)
            && PreviewProxyAuthorization.TryParseAccessPath(originalPath, out _, out _, out _, out _, out var logicalPath))
            context.Request.Path = logicalPath;
        try { await InvokeAuthorizedAsync(context); }
        finally { context.Request.Path = originalPath; }
    }

    private async Task InvokeAuthorizedAsync(HttpContext context)
    {
        var path = context.Request.Path;

        if (TryParseProxyRoute(path, out var routeKey, out var remainingPath))
        {
            if (!PreviewProxyAuthorization.AllowsRoute(context, routeKey))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (remainingPath.Equals("/_assets/dom-capture.min.js", StringComparison.Ordinal))
            {
                if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
                {
                    context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
                    context.Response.Headers.Allow = "GET, HEAD";
                    return;
                }

                // Resolve only this owned asset through the normal static-file
                // pipeline, including the embedded Brotli asset in native builds.
                context.Request.Path = "/js/dom-capture.min.js";
                try { await _next(context); }
                finally { context.Request.Path = path; }
                return;
            }
            if (remainingPath.StartsWith("/_ext", StringComparison.Ordinal))
            {
                if (context.WebSockets.IsWebSocketRequest)
                {
                    await ProxyExternalWebSocketAsync(context, routeKey);
                }
                else
                {
                    await ProxyExternalAsync(context, routeKey);
                }
                return;
            }
            if (remainingPath.Equals("/_cookies", StringComparison.Ordinal))
            {
                await HandleCookieBridgeAsync(context, routeKey);
                return;
            }
            if (remainingPath.Equals("/_storage", StringComparison.Ordinal))
            {
                await HandleStorageWriteAsync(context, routeKey);
                return;
            }

            _service.TryGetTargetUriByRouteKey(routeKey, out var targetUri);
            if (targetUri is null)
            {
                context.Response.StatusCode = 502;
                context.Response.ContentType = "text/plain";
                await context.Response.WriteAsync("No web preview target configured.", context.RequestAborted);
                return;
            }

            if (context.WebSockets.IsWebSocketRequest)
            {
                if (targetUri.IsFile)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                }
                else
                {
                    await ProxyWebSocketAsync(context, routeKey, targetUri, remainingPath);
                }
            }
            else
            {
                if (targetUri.IsFile)
                {
                    await ProxyFileAsync(context, routeKey, targetUri, remainingPath);
                }
                else
                {
                    await ProxyHttpAsync(context, routeKey, targetUri, remainingPath);
                }
            }

            return;
        }

        // Guard: if web preview is active and a proxied page's JS leaks calls to
        // /api/webpreview/* (e.g. inner MidTerm calling DELETE /api/webpreview/target),
        // proxy those upstream instead of letting them hit our local handlers.
        if (path.StartsWithSegments("/api/webpreview", StringComparison.Ordinal)
            && ShouldProxyWebPreviewApiRequest(context.Request)
            && TryResolvePreviewFromRequest(context.Request, out routeKey, out var apiTargetUri))
        {
            if (!PreviewProxyAuthorization.AllowsRoute(context, routeKey))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            if (context.WebSockets.IsWebSocketRequest)
                await ProxyWebSocketAsync(context, routeKey, apiTargetUri, path.Value ?? "/");
            else
                await ProxyHttpAsync(context, routeKey, apiTargetUri, path.Value ?? "/");
            return;
        }

        // Catch-all: if a proxied page leaks a root-relative URL outside /webpreview/{routeKey},
        // proxy it back to the active preview target instead of letting it fall into MidTerm's
        // own static-file tree. This is especially important for inline and module import specifiers
        // such as `import "/js/config.js"` that cannot be rewritten client-side.
        if (!IsInternalProxyRequest(context.Request))
        {
            var requestPath = path.Value ?? "/";
            if (!TryResolvePreviewFromRequest(context.Request, out routeKey, out var targetUri)
                || !ShouldProxyPreviewLeak(context.Request, requestPath))
            {
                if (context.Items.ContainsKey(PreviewProxyAuthorization.RouteItemKey))
                    context.Response.StatusCode = StatusCodes.Status404NotFound;
                else
                    await _next(context);
                return;
            }

            var proxyPath = requestPath;
            if (!PreviewProxyAuthorization.AllowsRoute(context, routeKey))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                return;
            }
            _service.RememberLeakedPathRoute(routeKey, proxyPath);
            if (context.WebSockets.IsWebSocketRequest)
            {
                await ProxyWebSocketAsync(context, routeKey, targetUri, proxyPath);
            }
            else
            {
                await ProxyHttpAsync(context, routeKey, targetUri, proxyPath);
            }

            return;
        }

        if (context.Items.ContainsKey(PreviewProxyAuthorization.RouteItemKey))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }
        await _next(context);
    }

    private static bool ShouldProxyWebPreviewApiRequest(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Referer", out var refererValues))
        {
            return false;
        }

        if (!Uri.TryCreate(refererValues.ToString(), UriKind.Absolute, out var refererUri))
        {
            return false;
        }

        var refererPath = refererUri.AbsolutePath;
        return refererPath.Equals(ProxyPrefix, StringComparison.OrdinalIgnoreCase)
            || refererPath.StartsWith(ProxyPrefix + "/", StringComparison.OrdinalIgnoreCase);
    }

    internal bool TryResolvePreviewFromRequest(HttpRequest request, out string routeKey, out Uri targetUri)
    {
        routeKey = "";
        targetUri = null!;

        if (TryParseProxyRoute(request.Path, out var directRouteKey, out _)
            && _service.TryGetTargetUriByRouteKey(directRouteKey, out var directTargetUri)
            && directTargetUri is not null)
        {
            routeKey = directRouteKey;
            targetUri = directTargetUri;
            return true;
        }

        if (!request.Headers.TryGetValue("Referer", out var refererValues))
        {   
            return TryResolvePreviewFromLeakedRequestPath(request, out routeKey, out targetUri);
        }

        if (Uri.TryCreate(refererValues.ToString(), UriKind.Absolute, out var refererUri))
        {
            if (!TryParseProxyRoute(refererUri.AbsolutePath, out routeKey, out _))
            {
                if (!_service.TryGetRouteKeyByLeakedPath(refererUri.AbsolutePath, out routeKey))
                {
                    routeKey = "";
                }
            }

            if (!string.IsNullOrWhiteSpace(routeKey)
                && _service.TryGetTargetUriByRouteKey(routeKey, out var refererTargetUri)
                && refererTargetUri is not null)
            {
                targetUri = refererTargetUri;
                return true;
            }
        }

        return TryResolvePreviewFromLeakedRequestPath(request, out routeKey, out targetUri);
    }

    private bool TryResolvePreviewFromLeakedRequestPath(
        HttpRequest request,
        out string routeKey,
        out Uri targetUri)
    {
        routeKey = "";
        targetUri = null!;

        var requestPath = request.Path.Value ?? "/";
        if (_service.TryGetRouteKeyByLeakedPath(requestPath, out var leakedRouteKey)
            && _service.TryGetTargetUriByRouteKey(leakedRouteKey, out var leakedTargetUri)
            && leakedTargetUri is not null)
        {
            routeKey = leakedRouteKey;
            targetUri = leakedTargetUri;
            return true;
        }

        return false;
    }

    internal static bool TryParseProxyRoute(PathString path, out string routeKey, out string remainingPath)
    {
        routeKey = "";
        remainingPath = "/";

        if (!path.StartsWithSegments(ProxyPrefix, StringComparison.Ordinal, out var remaining))
        {
            return false;
        }

        var value = remaining.Value ?? "";
        if (string.IsNullOrEmpty(value) || value == "/")
        {
            return false;
        }

        var normalized = value.StartsWith('/', StringComparison.Ordinal) ? value : "/" + value;
        var nextSlash = normalized.IndexOf('/', 1, StringComparison.Ordinal);
        if (nextSlash < 0)
        {
            routeKey = normalized[1..];
            remainingPath = "";
            return !string.IsNullOrWhiteSpace(routeKey);
        }

        routeKey = normalized[1..nextSlash];
        remainingPath = normalized[nextSlash..];
        return !string.IsNullOrWhiteSpace(routeKey);
    }

    internal static bool TryParseProxyRoute(string path, out string routeKey, out string remainingPath)
    {
        return TryParseProxyRoute(new PathString(path), out routeKey, out remainingPath);
    }

    private static bool IsInternalProxyRequest(HttpRequest request)
    {
        return request.Headers.TryGetValue(InternalProxyRequestHeaderName, out var values)
            && values.Count > 0
            && string.Equals(values[0], InternalProxyRequestHeaderValue, StringComparison.Ordinal);
    }

    /// <summary>
    /// Returns true if the path belongs to MidTerm itself (API, WebSocket, static files).
    /// Paths that don't match are candidates for proxying to the web preview target.
    /// </summary>
    private static bool IsMidTermPath(string path)
    {
        // Known MidTerm path prefixes
        if (path.StartsWith("/api/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/ws/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/fonts/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/locales/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/img/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/favicon/", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Root-level MidTerm files (only pages/assets that MidTerm itself needs).
        // Do NOT include /favicon.ico, /site.webmanifest, or other root-level assets
        // that proxied sites commonly reference — those should go to upstream.
        return path is "/"
            or "/index.html"
            or "/login.html"
            or "/trust.html"
            or "/web-preview-popup.html"
            or "/THIRD-PARTY-LICENSES.txt"
            or "/midFont-style.css";
    }

    internal static bool ShouldProxyPreviewLeak(HttpRequest request, string path)
    {
        if (!IsMidTermPath(path))
        {
            return true;
        }

        return HasPreviewReferer(request)
            && IsLeakedPreviewPath(path)
            && !IsPreviewLocalPath(path);
    }

    internal static bool HasPreviewReferer(HttpRequest request)
    {
        if (!request.Headers.TryGetValue("Referer", out var refererValues))
        {
            return false;
        }

        return Uri.TryCreate(refererValues.ToString(), UriKind.Absolute, out var refererUri)
            && TryParseProxyRoute(refererUri.AbsolutePath, out _, out _);
    }

    private static bool IsLeakedPreviewPath(string path)
    {
        return path is "/"
            or "/index.html"
            or "/login.html"
            || path.StartsWith("/js/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/css/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/fonts/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/locales/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/img/", StringComparison.OrdinalIgnoreCase)
            || path.StartsWith("/favicon/", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsPreviewLocalPath(string path)
    {
        return path.Equals("/js/dom-capture.min.js", StringComparison.OrdinalIgnoreCase);
    }

    private async Task ProxyHttpAsync(HttpContext context, string routeKey, Uri targetUri, string path)
    {
        SyncSelfTargetAuthCookie(routeKey, context.Request, targetUri);
        var upstreamOrigin = $"{targetUri.Scheme}://{targetUri.Authority}";
        var targetBase = targetUri.AbsolutePath.TrimEnd('/');
        var hasSubpath = !string.IsNullOrEmpty(targetBase) && targetBase != "/";

        // Determine primary URL (may use root fallback if previously learned)
        var primaryPath = BuildUpstreamPath(targetUri, path);
        var useRootFirst = hasSubpath && ShouldTryRootFirst(routeKey, path, targetBase);
        if (useRootFirst)
        {
            primaryPath = string.IsNullOrEmpty(path) || path == "/" ? "/" : path;
            if (!primaryPath.StartsWith('/', StringComparison.Ordinal))
                primaryPath = "/" + primaryPath;
        }

        var requestQuery = StripPreviewBootstrapQuery(context.Request.QueryString.Value);
        var currentUrl = BuildUpstreamUrlFromPath(targetUri, primaryPath, requestQuery);

        var originalMethod = new HttpMethod(context.Request.Method);
        byte[]? requestBodyBuffer = null;
        var requestHasBody = context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding");
        if (requestHasBody && originalMethod != HttpMethod.Get && originalMethod != HttpMethod.Head)
        {
            await using var bodyCopy = new MemoryStream();
            await context.Request.Body.CopyToAsync(bodyCopy, context.RequestAborted);
            requestBodyBuffer = bodyCopy.ToArray();
        }

        HttpRequestMessage BuildRequest(HttpMethod method, string url)
        {
            var msg = new HttpRequestMessage(method, url);
            ForwardRequestHeaders(context.Request, msg, routeKey, targetUri, upstreamOrigin);
            if (_service.IsSelfTarget(msg.RequestUri!))
            {
                msg.Headers.TryAddWithoutValidation(
                    InternalProxyRequestHeaderName,
                    InternalProxyRequestHeaderValue);
            }
            AttachRequestBody(msg, method, requestBodyBuffer, context.Request.ContentType, context.Request.ContentLength);
            return msg;
        }

        var (upstreamResponse, errorCode, finalUrl) = await SendUpstreamAsync(
            context, routeKey, originalMethod, currentUrl, BuildRequest, context.RequestAborted);

        // Retry-on-404: if we got 404 and the target has a subpath, try the alternate path.
        // If we tried subpath-prefixed first, retry at server root. If we tried root first
        // (from learned cache), retry with subpath-prefixed.
        if (hasSubpath
            && upstreamResponse is not null
            && upstreamResponse.StatusCode == System.Net.HttpStatusCode.NotFound
            && !PathAlreadyUnderTarget(path, targetBase))
        {
            var fallbackPath = useRootFirst
                ? BuildUpstreamPath(targetUri, path)
                : (string.IsNullOrEmpty(path) || path == "/" ? "/" : (path.StartsWith('/', StringComparison.Ordinal) ? path : "/" + path));

            if (fallbackPath != primaryPath)
            {
                var fallbackUrl = BuildUpstreamUrlFromPath(targetUri, fallbackPath, requestQuery);
                var (fallbackResponse, fallbackError, fallbackFinalUrl) = await SendUpstreamAsync(
                    context, routeKey, originalMethod, fallbackUrl, BuildRequest, context.RequestAborted);

                if (fallbackResponse is not null
                    && fallbackResponse.StatusCode != System.Net.HttpStatusCode.NotFound)
                {
                    upstreamResponse.Dispose();
                    upstreamResponse = fallbackResponse;
                    errorCode = fallbackError;
                    finalUrl = fallbackFinalUrl;

                    // Learn: if root worked, remember this prefix for future requests
                    if (!useRootFirst)
                    {
                        LearnRootFallback(routeKey, path, targetUri.ToString());
                    }
                }
                else
                {
                    fallbackResponse?.Dispose();
                    // Learn: if subpath worked from root-first attempt, un-learn
                    if (useRootFirst && fallbackResponse?.StatusCode != System.Net.HttpStatusCode.NotFound)
                    {
                        UnlearnRootFallback(routeKey, path);
                    }
                }
            }
        }

        _service.PersistCookies(routeKey);

        if (upstreamResponse is not null
            && ShouldAdoptCanonicalTarget(context.Request, upstreamResponse, finalUrl, targetUri.Authority, out var canonicalUri))
        {
            var canonicalTarget = canonicalUri.GetLeftPart(UriPartial.Authority) + targetUri.AbsolutePath;
            if (_service.GetPreviewSessionByRouteKey(routeKey) is { SessionId: var sessionId, PreviewName: var previewName })
            {
                _service.SetTarget(sessionId, previewName, canonicalTarget, preserveCookies: true, preserveTargetRevision: true);
            }
        }

        if (upstreamResponse is null)
        {
            await PreviewProxyFailure.WriteAsync(context, errorCode);
            return;
        }

        using (upstreamResponse)
        {
            context.Response.StatusCode = (int)upstreamResponse.StatusCode;
            CopyResponseHeaders(
                upstreamResponse,
                context.Response,
                PreviewProxyAuthorization.GetProxyPrefix(context, routeKey),
                targetUri);
            ApplyForcedReloadHeaders(context.Request, context.Response);
            await DispatchResponseBodyAsync(context, routeKey, upstreamResponse, finalUrl);
        }
    }

    private async Task ProxyHtmlResponseAsync(HttpContext context, string routeKey, Uri targetUri, HttpResponseMessage upstreamResponse, string? finalUrl, string? documentHtml = null)
    {
        var html = documentHtml ?? await DecompressTextAsync(upstreamResponse, context.RequestAborted);
        var reloadToken = GetPreviewReloadToken(context.Request.Query);

        // Capture this before URL rewriting removes or changes the upstream base tag.
        // We still inject a proxy-prefixed base below so browser routers and relative
        // assets resolve against the visible /webpreview/{routeKey}/ document URL.
        string? originalBaseHref = null;
        var baseMatch = BaseHrefValueRegex().Match(html);
        if (baseMatch.Success)
        {
            originalBaseHref = baseMatch.Groups[1].Value;
        }

        // Rewrite root-relative URLs to go through the proxy.
        // <base href> only handles truly relative URLs (foo/bar.js),
        // but root-relative URLs (/path/to/file) need explicit rewriting.
        var routePrefix = PreviewProxyAuthorization.GetProxyPrefix(context, routeKey);
        var targetAuthority = targetUri.Authority;
        var isExternalDocument = Uri.TryCreate(finalUrl, UriKind.Absolute, out var documentUri)
            && !documentUri.Authority.Equals(targetAuthority, StringComparison.OrdinalIgnoreCase);
        if (isExternalDocument)
        {
            html = RewriteExternalDocumentAttributes(html, documentUri!, routePrefix);
        }
        html = RootRelativeAttrRegex().Replace(html, m => RewriteRootRelativeAttributeUrl(m, routePrefix, reloadToken));
        html = RewriteSrcsetAttributes(html, routePrefix, targetUri.Scheme, targetAuthority, reloadToken);
        html = RootRelativeCssUrlRegex().Replace(html, m => RewriteRootRelativeCssUrl(m, routePrefix, reloadToken));

        // Rewrite <meta http-equiv="refresh" content="0;url=/path"> URLs (PHP redirect pattern)
        html = MetaRefreshRegex().Replace(html, m =>
        {
            var prefix = m.Groups[1].Value;
            var url = m.Groups[2].Value;
            if (url.StartsWith('/', StringComparison.Ordinal) && !url.StartsWith(routePrefix + "/", StringComparison.Ordinal))
                return prefix + AppendReloadTokenToProxyUrl(routePrefix + url, reloadToken);
            return m.Value;
        });

        // Rewrite absolute external URLs (https://cdn.example.com/...) to go through _ext proxy.
        // This allows MT to fetch third-party resources server-side, bypassing CORS/ad blockers.
        html = AbsoluteUrlAttrRegex().Replace(html, m => RewriteExternalUrl(m, routePrefix, targetAuthority, reloadToken));
        html = AbsoluteUrlCssRegex().Replace(html, m => RewriteExternalCssUrl(m, routePrefix, targetAuthority, reloadToken));
        html = ProtocolRelativeAttrRegex().Replace(html, m =>
            RewriteProtocolRelativeUrl(m, routePrefix, targetUri.Scheme, targetAuthority, reloadToken));
        html = ProtocolRelativeCssRegex().Replace(html, m =>
            RewriteProtocolRelativeCssUrl(m, routePrefix, targetUri.Scheme, targetAuthority, reloadToken));
        html = RewriteCssImports(html, routePrefix, targetUri.Scheme, targetAuthority, reloadToken);
        html = StripIntegrityAttributeRegex().Replace(html, "");
        html = StripImportMapIntegrity(html);

        // Prime the root-fallback cache from rewritten HTML before the browser starts
        // requesting assets. This avoids the first-wave 404s on deep document targets
        // whose HTML points at server-root assets like /_astro/*.
        PrimeRootFallbacksFromHtml(routeKey, html, routePrefix);

        // Remove any existing <base> tags to avoid duplicates — we inject our own
        html = ExistingBaseTagRegex().Replace(html, "");

        // Strip upstream CSP and X-Frame-Options meta tags — after proxying, 'self' in those
        // directives would resolve to MidTerm's origin instead of the upstream site's origin,
        // causing the proxied page to block framing of external resources.
        html = UpstreamSecurityMetaTagRegex().Replace(html, "");

        // Build proxy-prefixed base href. Trust the upstream's <base href> value — it knows
        // how its assets are served (root vs subpath). Just prefix with /webpreview.
        var baseHref = BuildInjectedBaseHref(routePrefix, finalUrl, originalBaseHref, html);

        // Rewrite inline ESM specifiers before the browser resolves them.
        html = RewriteRootRelativeModuleSpecifiers(html, routePrefix, reloadToken);
        html = WebPreviewLocationRewriter.RewriteInlineScripts(html);

        // Inject <base href> for truly relative URLs, plus a script that patches
        // fetch/XHR to rewrite root-relative URLs at runtime (safer than regex on JS source).
        var targetOrigin = (documentUri ?? targetUri).GetLeftPart(UriPartial.Authority);
        var originScript = $"<script>window.__mtTargetOrigin=\"{JsonEncodedText.Encode(targetOrigin)}\";"
            + $"window.__mtDocumentUrl=\"{JsonEncodedText.Encode(finalUrl ?? targetUri.ToString())}\";"
            + $"window.__mtExternalDocument={(isExternalDocument ? "true" : "false")};</script>";
        var redirectedProxyPath = BuildRedirectedProxyPath(
            routePrefix,
            targetUri,
            finalUrl,
            context.Request.Path.Value ?? "/",
            context.Request.QueryString.Value);
        var redirectPathScript = redirectedProxyPath is null
            ? ""
            : $"<script>history.replaceState(history.state,\"\",\"{JsonEncodedText.Encode(redirectedProxyPath)}\");</script>";
        html = MarkSubframeSources(html);
        html = InjectHeadContent(html,
            $"<base href=\"{WebUtility.HtmlEncode(baseHref)}\">{originScript}" + GetUrlRewriteScript(routePrefix,
                _service.GetBrowserCookies(routeKey, documentUri ?? targetUri).Header ?? "", html,
                _storage?.CreateBootstrap(context, routeKey, documentUri ?? targetUri)) + redirectPathScript);

        // Send uncompressed — strip Content-Encoding and Content-Length for this response
        context.Response.Headers.Remove("Content-Length");
        context.Response.Headers.Remove("Content-Encoding");
        // Every document receives a fresh storage seed/lease. Reusing a cached
        // bootstrap would roll reads back and restart an already-used sequence.
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Remove("ETag");
        context.Response.Headers.Remove("Last-Modified");
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html, context.RequestAborted);
    }

    internal static string InjectHeadContent(string html, string injection)
    {
        var head = HeadTagRegex().Match(html);
        if (head.Success) return html.Insert(head.Index + head.Length, injection);
        // A script-only login redirect is valid HTML too. Install the adapter
        // before any upstream script while preserving the document's doctype.
        var prefix = HtmlDocumentPrefixRegex().Match(html);
        return html.Insert(prefix.Success ? prefix.Length : 0, "<head>" + injection + "</head>");
    }

    internal static string MarkSubframeSources(string html)
    {
        html = IframeSourceRegex().Replace(html, match =>
        {
            var url = WebUtility.HtmlDecode(match.Groups[2].Value);
            if (!url.Contains("/webpreview/", StringComparison.Ordinal)) return match.Value;
            var marked = QueryHelpers.AddQueryString(url, PreviewSubframeQueryParam, "1");
            return match.Groups[1].Value + WebUtility.HtmlEncode(marked) + match.Groups[3].Value;
        });
        return SrcdocFrameRegex().Replace(html, match => !match.Groups["prefix"].Success ? match.Value
            : match.Groups["prefix"].Value + match.Groups["quote"].Value
                + WebUtility.HtmlEncode(InjectHeadContent(WebUtility.HtmlDecode(match.Groups["value"].Value), GetCooperativeChildScript()))
                + match.Groups["quote"].Value);
    }

    internal static string RewriteExternalDocumentAttributes(string html, Uri documentUri, string routePrefix) =>
        DocumentUrlAttributeRegex().Replace(html, match =>
        {
            var value = WebUtility.HtmlDecode(match.Groups[2].Value);
            if (value.StartsWith('#', StringComparison.Ordinal) || value.StartsWith(routePrefix + "/", StringComparison.Ordinal)
                || !Uri.TryCreate(documentUri, value, out var url) || url.Scheme is not ("http" or "https"))
                return match.Value;
            var proxied = routePrefix + "/_ext?u=" + Uri.EscapeDataString(url.ToString());
            return match.Groups[1].Value + WebUtility.HtmlEncode(proxied) + match.Groups[3].Value;
        });

    private static string ComputeBaseHref(string routePrefix, string? finalUrl)
    {
        if (finalUrl is null || !Uri.TryCreate(finalUrl, UriKind.Absolute, out var finalUri))
            return routePrefix + "/";

        // A document URL is already the correct base for relative resources. Keeping
        // its filename and query also makes fragment-only links stay on this page.
        return routePrefix.TrimEnd('/') + finalUri.PathAndQuery;
    }

    internal static string? BuildRedirectedProxyPath(
        string routePrefix,
        Uri targetUri,
        string? finalUrl,
        string requestPath,
        string? requestQuery)
    {
        if (string.IsNullOrWhiteSpace(finalUrl)
            || !Uri.TryCreate(finalUrl, UriKind.Absolute, out var finalUri)
            || !finalUri.Authority.Equals(targetUri.Authority, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        var upstreamRequestPath = TryParseProxyRoute(requestPath, out _, out var remainingPath)
            ? remainingPath
            : requestPath;
        var initialUrl = BuildUpstreamUrlFromPath(
            targetUri,
            BuildUpstreamPath(targetUri, upstreamRequestPath),
            StripPreviewBootstrapQuery(requestQuery));

        if (Uri.TryCreate(initialUrl, UriKind.Absolute, out var initialUri)
            && initialUri.AbsoluteUri.Equals(finalUri.AbsoluteUri, StringComparison.Ordinal))
        {
            return null;
        }

        return routePrefix + finalUri.PathAndQuery + finalUri.Fragment;
    }

    internal static string BuildInjectedBaseHref(string routePrefix, string? finalUrl, string? originalBaseHref, string html)
    {
        if (originalBaseHref is not null)
        {
            var basePath = WebUtility.HtmlDecode(originalBaseHref);
            if (basePath.Length == 0 || basePath == "/")
                return routePrefix + "/";

            return routePrefix + (basePath.StartsWith('/', StringComparison.Ordinal) ? basePath : "/" + basePath);
        }

        return ComputeBaseHref(routePrefix, finalUrl);
    }

    private static Uri BuildRequestedFileUri(Uri targetUri, string requestPath)
    {
        if (string.IsNullOrEmpty(requestPath) || requestPath == "/")
        {
            return targetUri;
        }

        var decodedPath = Uri.UnescapeDataString(requestPath);
        if (decodedPath.StartsWith('/', StringComparison.Ordinal)
            && decodedPath.Length >= 4
            && char.IsLetter(decodedPath[1])
            && decodedPath[2] == ':'
            && decodedPath[3] == '/')
        {
            return new Uri($"file://{decodedPath}");
        }

        return new Uri(targetUri, decodedPath.TrimStart('/'));
    }

    private static string GetContentType(string localPath)
    {
        return ContentTypeProvider.TryGetContentType(localPath, out var contentType)
            ? contentType
            : "application/octet-stream";
    }

    internal void PrimeRootFallbacksFromHtml(string routeKey, string html, string? scopedPrefix = null)
    {
        _service.TryGetTargetUriByRouteKey(routeKey, out var targetUri);
        if (targetUri is null)
        {
            return;
        }

        var targetBase = targetUri.AbsolutePath.TrimEnd('/');
        if (string.IsNullOrEmpty(targetBase) || targetBase == "/")
        {
            return;
        }

        ResetFallbackCacheIfTargetChanged(routeKey, targetUri.ToString());
        var routeCache = GetOrCreateRootFallbackCache(routeKey);

        foreach (var prefix in CollectProxyPathPrefixes(scopedPrefix ?? _service.BuildProxyPrefix(routeKey), html))
        {
            routeCache[prefix] = true;
        }
    }

    internal static string[] CollectProxyPathPrefixes(string routePrefix, string html)
    {
        if (string.IsNullOrEmpty(html))
        {
            return Array.Empty<string>();
        }

        var prefixes = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in ProxiedPathRegex().Matches(html))
        {
            var proxiedPath = match.Groups[1].Value;
            if (!proxiedPath.StartsWith(routePrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var upstreamPath = proxiedPath[routePrefix.Length..];
            var prefix = GetPathPrefix(upstreamPath);
            if (prefix is null || prefix.Equals("/_ext/", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            prefixes.Add(prefix);
        }

        return prefixes.Count == 0 ? Array.Empty<string>() : [.. prefixes];
    }

    private async Task ProxyCssResponseAsync(HttpContext context, string routeKey, HttpResponseMessage upstreamResponse, string? finalUrl)
    {
        var css = await DecompressTextAsync(upstreamResponse, context.RequestAborted);
        var routePrefix = PreviewProxyAuthorization.GetProxyPrefix(context, routeKey);
        var reloadToken = GetPreviewReloadToken(context.Request.Query);

        css = RewriteStylesheetResources(css, routePrefix, finalUrl,
            _service.GetTargetUriByRouteKey(routeKey), reloadToken);

        context.Response.Headers.Remove("Content-Length");
        context.Response.Headers.Remove("Content-Encoding");
        context.Response.ContentType = "text/css; charset=utf-8";
        await context.Response.WriteAsync(css, context.RequestAborted);
    }

    private async Task ProxyJavaScriptResponseAsync(HttpContext context, string routeKey, HttpResponseMessage upstreamResponse, string? finalUrl)
    {
        var script = await DecompressTextAsync(upstreamResponse, context.RequestAborted);
        var routePrefix = PreviewProxyAuthorization.GetProxyPrefix(context, routeKey);
        var reloadToken = GetPreviewReloadToken(context.Request.Query);
        script = RewriteJavaScriptModuleSpecifiers(
            script, routePrefix, finalUrl, _service.GetTargetUriByRouteKey(routeKey), reloadToken);
        script = WebPreviewLocationRewriter.RewriteImportMetaUrlReads(script, finalUrl);
        script = WebPreviewLocationRewriter.Rewrite(script);

        context.Response.Headers.Remove("Content-Length");
        context.Response.Headers.Remove("Content-Encoding");
        context.Response.ContentType = upstreamResponse.Content.Headers.ContentType?.ToString()
            ?? "application/javascript; charset=utf-8";
        await context.Response.WriteAsync(script, context.RequestAborted);
    }

    private async Task ProxyExternalAsync(HttpContext context, string routeKey)
    {
        var externalUrl = context.Request.Query["u"].FirstOrDefault();
        if (string.IsNullOrEmpty(externalUrl) || !Uri.TryCreate(externalUrl, UriKind.Absolute, out var extUri))
        {
            context.Response.StatusCode = 400;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("Missing or invalid 'u' parameter.", context.RequestAborted);
            return;
        }

        if (extUri.Scheme is not ("http" or "https"))
        {
            context.Response.StatusCode = 400;
            return;
        }

        var currentUrl = extUri.ToString();
        var originalMethod = new HttpMethod(context.Request.Method);

        byte[]? requestBodyBuffer = null;
        var requestHasBody = context.Request.ContentLength > 0 || context.Request.Headers.ContainsKey("Transfer-Encoding");
        if (requestHasBody && originalMethod != HttpMethod.Get && originalMethod != HttpMethod.Head)
        {
            await using var bodyCopy = new MemoryStream();
            await context.Request.Body.CopyToAsync(bodyCopy, context.RequestAborted);
            requestBodyBuffer = bodyCopy.ToArray();
        }

        HttpRequestMessage BuildRequest(HttpMethod method, string url)
        {
            var requestUri = new Uri(url);
            var upstreamOrigin = $"{requestUri.Scheme}://{requestUri.Authority}";
            var msg = new HttpRequestMessage(method, url);
            ForwardRequestHeaders(context.Request, msg, routeKey, requestUri, upstreamOrigin);
            AttachRequestBody(msg, method, requestBodyBuffer, context.Request.ContentType, null);
            return msg;
        }

        var (upstreamResponse, errorCode, finalUrl) = await SendUpstreamAsync(
            context, routeKey, originalMethod, currentUrl, BuildRequest, context.RequestAborted, "ext-http");

        _service.PersistCookies(routeKey);

        if (upstreamResponse is null)
        {
            await PreviewProxyFailure.WriteAsync(context, errorCode);
            return;
        }

        using (upstreamResponse)
        {
            if (ShouldAdoptCanonicalTarget(context.Request, upstreamResponse, finalUrl, _service.GetTargetUriByRouteKey(routeKey)?.Authority, out var canonicalUri))
            {
                var canonicalTarget = canonicalUri.GetLeftPart(UriPartial.Authority) + "/";
                if (_service.GetPreviewSessionByRouteKey(routeKey) is { SessionId: var sessionId, PreviewName: var previewName })
                {
                    _service.SetTarget(sessionId, previewName, canonicalTarget, preserveCookies: true, preserveTargetRevision: true);
                }
            }

            context.Response.StatusCode = (int)upstreamResponse.StatusCode;
            var responseHeaderUri = Uri.TryCreate(finalUrl, UriKind.Absolute, out var parsedFinalUrl)
                ? parsedFinalUrl
                : extUri;
            CopyResponseHeaders(
                upstreamResponse,
                context.Response,
                PreviewProxyAuthorization.GetProxyPrefix(context, routeKey),
                responseHeaderUri);
            await DispatchResponseBodyAsync(context, routeKey, upstreamResponse, finalUrl);
        }
    }

    internal static bool IsTlbxAuthenticationHeader(HttpRequest request, string headerName) =>
        RequestAccessContext.IsApiKeyAuthenticated(request.HttpContext)
        && (headerName.Equals("Authorization", StringComparison.OrdinalIgnoreCase)
            || headerName.Equals("X-API-Key", StringComparison.OrdinalIgnoreCase));

    private void ForwardRequestHeaders(
        HttpRequest source,
        HttpRequestMessage target,
        string routeKey,
        Uri currentTargetUri,
        string upstreamOrigin)
    {
        foreach (var header in source.Headers)
        {
            if (IsBlockedRequestHeader(header.Key)
                || IsTlbxAuthenticationHeader(source, header.Key))
                continue;

            if (header.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase))
            {
                target.Headers.TryAddWithoutValidation(header.Key, upstreamOrigin);
                continue;
            }
            if (header.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase))
            {
                var refValue = RewriteRefererForUpstream(header.Value.ToString(), routeKey, currentTargetUri, source);
                if (!string.IsNullOrEmpty(refValue))
                    target.Headers.TryAddWithoutValidation(header.Key, refValue);
                continue;
            }

            target.Headers.TryAddWithoutValidation(header.Key, header.Value.ToArray());
        }

    }

    internal static bool IsBlockedRequestHeader(string name) =>
        BlockedRequestHeaders.Contains(name)
        || name.StartsWith("X-Forwarded-", StringComparison.OrdinalIgnoreCase);

    internal string RewriteRefererForUpstream(string refererValue, string currentRouteKey, Uri currentTargetUri, HttpRequest? source = null)
    {
        if (!Uri.TryCreate(refererValue, UriKind.Absolute, out var refererUri))
        {
            return "";
        }

        // Normalize only our capability envelope, preserving escaped upstream
        // path bytes. External-module referers must reach the _ext branch below.
        var refererPath = PreviewProxyAuthorization.RedactAccessPath(refererUri.AbsolutePath);
        if (!TryParseProxyRoute(refererPath, out var refererRouteKey, out var refererRemainingPath))
        {
            if (_service.TryGetRouteKeyByLeakedPath(refererUri.AbsolutePath, out var leakedRouteKey)
                && _service.TryGetTargetUriByRouteKey(leakedRouteKey, out var leakedTargetUri)
                && leakedTargetUri is not null)
            {
                return BuildUpstreamUrlFromPath(leakedTargetUri, BuildUpstreamPath(leakedTargetUri, refererUri.AbsolutePath), StripPreviewBootstrapQuery(refererUri.Query));
            }

            // Initial navigation refers to the tlbx shell, not an upstream page.
            if (_service.IsSelfTarget(refererUri)
                || (source is not null && refererUri.Host.Equals(source.Host.Host, StringComparison.OrdinalIgnoreCase)))
                return "";
            return refererValue;
        }

        if (string.Equals(refererRemainingPath, "/_ext", StringComparison.OrdinalIgnoreCase))
        {
            var externalUrl = QueryHelpers.ParseQuery(refererUri.Query)["u"].FirstOrDefault();
            return !string.IsNullOrWhiteSpace(externalUrl)
                && Uri.TryCreate(externalUrl, UriKind.Absolute, out var externalUri)
                ? externalUri.ToString()
                : "";
        }

        var refererTarget = _service.GetTargetUriByRouteKey(refererRouteKey);
        if (refererTarget is null
            && string.Equals(refererRouteKey, currentRouteKey, StringComparison.Ordinal))
        {
            refererTarget = currentTargetUri;
        }

        if (refererTarget is null)
        {
            return "";
        }

        var upstreamPath = BuildUpstreamPath(refererTarget, refererRemainingPath);
        return BuildUpstreamUrlFromPath(refererTarget, upstreamPath, StripPreviewBootstrapQuery(refererUri.Query));
    }

    private static void AttachRequestBody(
        HttpRequestMessage request, HttpMethod method,
        byte[]? bodyBuffer, string? contentType, long? contentLength)
    {
        if (bodyBuffer is null || method == HttpMethod.Get || method == HttpMethod.Head)
            return;

        request.Content = new ByteArrayContent(bodyBuffer);
        if (contentType is not null)
        {
            request.Content.Headers.ContentType =
                System.Net.Http.Headers.MediaTypeHeaderValue.Parse(contentType);
        }
        if (contentLength is > 0)
        {
            request.Content.Headers.ContentLength = bodyBuffer.Length;
        }
    }

    private static void CopyResponseHeaders(
        HttpResponseMessage upstream,
        HttpResponse downstream,
        string routePrefix,
        Uri currentTargetUri)
    {
        var reloadToken = GetPreviewReloadToken(downstream.HttpContext.Request.Query);
        foreach (var header in upstream.Headers)
        {
            if (HopByHopHeaders.Contains(header.Key) || StrippedResponseHeaders.Contains(header.Key))
                continue;
            if (header.Key.Equals("Location", StringComparison.OrdinalIgnoreCase))
                continue;
            if (header.Key.Equals("Link", StringComparison.OrdinalIgnoreCase))
            {
                downstream.Headers[header.Key] = header.Value
                    .Select(value => RewriteLinkHeaderValue(value, routePrefix, currentTargetUri, reloadToken))
                    .ToArray();
                continue;
            }
            if (header.Key.Equals("Refresh", StringComparison.OrdinalIgnoreCase))
            {
                downstream.Headers[header.Key] = header.Value
                    .Select(value => RewriteRefreshHeaderValue(value, routePrefix, currentTargetUri, reloadToken))
                    .ToArray();
                continue;
            }
            downstream.Headers[header.Key] = header.Value.ToArray();
        }

        foreach (var header in upstream.Content.Headers)
        {
            if (StrippedResponseHeaders.Contains(header.Key))
                continue;
            downstream.Headers[header.Key] = header.Value.ToArray();
        }
    }

    private async Task<(HttpResponseMessage? Response, int ErrorCode, string? FinalUrl)> SendUpstreamAsync(
        HttpContext context,
        string routeKey,
        HttpMethod originalMethod,
        string startUrl,
        Func<HttpMethod, string, HttpRequestMessage> buildRequest,
        CancellationToken cancellationToken,
        string? logType = null)
    {
        const int maxRedirects = 10;
        var currentUrl = startUrl;
        string? responseUrl = null;
        var currentMethod = originalMethod;
        HttpResponseMessage? upstreamResponse = null;
        var sw = Stopwatch.StartNew();

        for (var redirect = 0; redirect <= maxRedirects; redirect++)
        {
            var requestMessage = buildRequest(currentMethod, currentUrl);
            if (context.Request.Headers["Sec-Fetch-Mode"] == "navigate")
            {
                requestMessage.Headers.Remove("If-None-Match");
                requestMessage.Headers.Remove("If-Modified-Since");
            }
            if (requestMessage.RequestUri is not null)
            {
                var cookieHeader = _service.GetForwardedCookieHeader(routeKey, requestMessage.RequestUri);
                if (!string.IsNullOrWhiteSpace(cookieHeader))
                {
                    requestMessage.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
                }
            }

            try
            {
                upstreamResponse?.Dispose();
                upstreamResponse = await _service.GetHttpClient(routeKey).SendAsync(
                    requestMessage, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            catch (HttpRequestException ex)
            {
                context.Items[typeof(PreviewProxyFailure)] = PreviewProxyFailure.Describe(ex, new Uri(currentUrl));
                sw.Stop();
                LogHttpRequest(context, routeKey, logType ?? "http", originalMethod.Method, startUrl, currentUrl, 502, sw.ElapsedMilliseconds, requestMessage, null, ex.Message);
                requestMessage.Dispose();
                return (null, 502, null);
            }
            catch (TaskCanceledException ex)
            {
                context.Items[typeof(PreviewProxyFailure)] = PreviewProxyFailure.Describe(ex, new Uri(currentUrl));
                sw.Stop();
                LogHttpRequest(context, routeKey, logType ?? "http", originalMethod.Method, startUrl, currentUrl, 504, sw.ElapsedMilliseconds, requestMessage, null, ex.Message);
                requestMessage.Dispose();
                return (null, 504, null);
            }

            if (upstreamResponse is not null && Uri.TryCreate(currentUrl, UriKind.Absolute, out var responseUri))
            {
                responseUrl = currentUrl;
                _service.StoreResponseCookies(routeKey, responseUri, upstreamResponse);
            }

            if (upstreamResponse is null)
            {
                requestMessage.Dispose();
                return (null, 502, null);
            }

            var statusCode = (int)upstreamResponse.StatusCode;
            if (statusCode is >= 301 and <= 308)
            {
                var location = upstreamResponse.Headers.Location?.ToString()
                    ?? upstreamResponse.Content.Headers.ContentLocation?.ToString();
                if (location is not null
                    && Uri.TryCreate(new Uri(currentUrl), location, out var resolved))
                {
                    currentUrl = resolved.ToString();
                    currentMethod = statusCode is 307 or 308 ? originalMethod : HttpMethod.Get;
                    requestMessage.Dispose();
                    continue;
                }
            }

            sw.Stop();
            LogHttpRequest(context, routeKey, logType ?? "http", originalMethod.Method, startUrl, currentUrl, statusCode, sw.ElapsedMilliseconds, requestMessage, upstreamResponse, null);
            requestMessage.Dispose();
            break;
        }

        return upstreamResponse is not null
            ? (upstreamResponse, 0, responseUrl)
            : (null, 502, null);
    }

    private void LogHttpRequest(
        HttpContext context, string routeKey, string type, string method,
        string startUrl, string finalUrl, int statusCode, long durationMs,
        HttpRequestMessage? request, HttpResponseMessage? response, string? error)
    {
        var entry = new WebPreviewProxyLogEntry
        {
            Type = type,
            Method = method,
            RequestUrl = PreviewProxyAuthorization.RedactAccessPath(context.Request.Path) + StripPreviewBootstrapQuery(context.Request.QueryString.Value ?? ""),
            UpstreamUrl = finalUrl,
            StatusCode = statusCode,
            DurationMs = durationMs,
            Error = error
        };

        if (request is not null)
        {
            // Typed header enumeration splits User-Agent into products/comments.
            // Preserve its original spacing instead of inventing comma separators.
            foreach (var h in request.Headers.NonValidated)
                entry.RequestHeaders[h.Key] = string.Join(", ", h.Value);
            var cookieHeader = request.RequestUri is not null
                ? _service.GetForwardedCookieHeader(routeKey, request.RequestUri)
                : null;
            if (string.IsNullOrWhiteSpace(cookieHeader)
                && request.Headers.TryGetValues("Cookie", out var cookies))
            {
                cookieHeader = string.Join("; ", cookies);
            }

            entry.RequestCookies = cookieHeader;
        }

        if (response is not null)
        {
            foreach (var h in response.Headers)
                entry.ResponseHeaders[h.Key] = string.Join(", ", h.Value);
            foreach (var h in response.Content.Headers)
                entry.ResponseHeaders[h.Key] = string.Join(", ", h.Value);
            entry.ContentType = response.Content.Headers.ContentType?.ToString();
            entry.ContentLength = response.Content.Headers.ContentLength;

            var setCookies = response.Headers.TryGetValues("Set-Cookie", out var sc)
                ? string.Join(" | ", sc) : null;
            entry.ResponseCookies = setCookies;
        }

        _service.AddLogEntry(routeKey, entry);
    }

    private static string SanitizeRequestHeader(string name, string value)
    {
        if (name.Equals("Cookie", StringComparison.OrdinalIgnoreCase) || name.Equals("Authorization", StringComparison.OrdinalIgnoreCase))
            return "[redacted]";
        if (name.Equals("Referer", StringComparison.OrdinalIgnoreCase) && Uri.TryCreate(value, UriKind.Absolute, out var uri))
            return new UriBuilder(uri)
            {
                Path = PreviewProxyAuthorization.RedactAccessPath(uri.AbsolutePath),
                Query = StripPreviewBootstrapQuery(uri.Query)
            }.Uri.ToString();
        return value;
    }

    private void LogWebSocket(
        HttpContext context, string routeKey, string type, string upstreamUrl,
        IList<string> requestedProtocols, string? negotiatedProtocol,
        long durationMs, int statusCode, string? error)
    {
        var entry = new WebPreviewProxyLogEntry
        {
            Type = type,
            Method = "WS-UPGRADE",
            RequestUrl = PreviewProxyAuthorization.RedactAccessPath(context.Request.Path) + StripPreviewBootstrapQuery(context.Request.QueryString.Value ?? ""),
            UpstreamUrl = upstreamUrl,
            StatusCode = statusCode,
            DurationMs = durationMs,
            Error = error,
            SubProtocols = requestedProtocols.Count > 0 ? string.Join(", ", requestedProtocols) : null,
            NegotiatedProtocol = negotiatedProtocol
        };

        foreach (var h in context.Request.Headers)
        {
            entry.RequestHeaders[h.Key] = SanitizeRequestHeader(h.Key, h.Value.ToString());
        }

        if (Uri.TryCreate(upstreamUrl, UriKind.Absolute, out var upstreamUri))
        {
            var httpScheme = upstreamUri.Scheme == "wss" ? "https" : "http";
            var cookieLookupUri = new UriBuilder(upstreamUri) { Scheme = httpScheme }.Uri;
            entry.RequestCookies = _service.GetForwardedCookieHeader(routeKey, cookieLookupUri)
                ?? context.Request.Headers.Cookie.ToString();
        }

        _service.AddLogEntry(routeKey, entry);
    }

    internal static void ExposeUpstreamUrlHeader(HttpResponse response)
    {
        var exposed = response.Headers.AccessControlExposeHeaders.ToString();
        if (exposed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Contains(UpstreamUrlHeaderName, StringComparer.OrdinalIgnoreCase)) return;
        response.Headers.AccessControlExposeHeaders = string.IsNullOrWhiteSpace(exposed)
            ? UpstreamUrlHeaderName : exposed + ", " + UpstreamUrlHeaderName;
    }

    internal static void SetUpstreamUrlHeader(HttpResponse response, string? finalUrl)
    {
        // Called after all upstream headers were copied. An upstream must never
        // be able to spoof the URL observed by the preview's network clients.
        response.Headers.Remove(UpstreamUrlHeaderName);
        if (Uri.TryCreate(finalUrl, UriKind.Absolute, out var uri)
            && uri.Scheme is "http" or "https")
        {
            var safeUrl = new UriBuilder(uri) { UserName = "", Password = "" };
            response.Headers[UpstreamUrlHeaderName] = safeUrl.Uri.AbsoluteUri;
            // An outer proxy must also be able to expose this metadata.
            ExposeUpstreamUrlHeader(response);
        }
    }

    private async Task DispatchResponseBodyAsync(HttpContext context, string routeKey, HttpResponseMessage upstreamResponse, string? finalUrl)
    {
        SetUpstreamUrlHeader(context.Response, finalUrl);
        var contentType = upstreamResponse.Content.Headers.ContentType?.MediaType;
        if (IsFontResponse(contentType, context.Request.Path.Value))
        {
            context.Response.Headers["Access-Control-Allow-Origin"] = "*";
        }

        if (IsDocumentNavigation(context.Request) && !IsViewableDocumentNavigation(context.Request, upstreamResponse))
        {
            // Downloads, partial representations and bodyless statuses keep their
            // original representation, even when the resource is HTML/CSS/script.
            await using var original = await upstreamResponse.Content.ReadAsStreamAsync(context.RequestAborted);
            await original.CopyToAsync(context.Response.Body, context.RequestAborted);
            return;
        }

        if (contentType is "text/html")
        {
            var targetUri = _service.GetTargetUriByRouteKey(routeKey);
            if (targetUri is null)
            {
                context.Response.StatusCode = 502;
                return;
            }

            await ProxyHtmlResponseAsync(context, routeKey, targetUri, upstreamResponse, finalUrl);
        }
        else if (ShouldRenderTextDocument(context.Request, upstreamResponse)
            || ShouldRenderImageDocument(context.Request, upstreamResponse))
        {
            var targetUri = _service.GetTargetUriByRouteKey(routeKey);
            if (targetUri is null)
            {
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                return;
            }

            var image = ShouldRenderImageDocument(context.Request, upstreamResponse);
            var (body, error) = await ReadDocumentBodyAsync(upstreamResponse, image ? 4 * 1024 * 1024 : 1024 * 1024, context.RequestAborted);
            var originalUrl = finalUrl ?? targetUri.AbsoluteUri;
            var originalType = upstreamResponse.Content.Headers.ContentType?.ToString() ?? "";
            string document;
            if (error is not null)
                document = RenderTextDocument(error, originalUrl, originalType);
            else if (image)
                document = RenderImageDocument(body!, contentType!, originalUrl);
            else
            {
                try
                {
                    var charset = upstreamResponse.Content.Headers.ContentType?.CharSet?.Trim('"');
                    var encoding = string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset);
                    using var reader = new StreamReader(new MemoryStream(body!, writable: false), encoding, detectEncodingFromByteOrderMarks: true);
                    document = RenderTextDocument(await reader.ReadToEndAsync(context.RequestAborted), originalUrl, originalType);
                }
                catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
                {
                    document = RenderTextDocument("Preview unavailable: the response declares an unsupported text charset.", originalUrl, originalType);
                }
            }
            await ProxyHtmlResponseAsync(context, routeKey, targetUri, upstreamResponse, finalUrl,
                document);
        }
        else if (contentType is "text/css")
        {
            await ProxyCssResponseAsync(context, routeKey, upstreamResponse, finalUrl);
        }
        else if (contentType is "application/javascript" or "text/javascript")
        {
            await ProxyJavaScriptResponseAsync(context, routeKey, upstreamResponse, finalUrl);
        }
        else
        {
            await using var stream = await upstreamResponse.Content.ReadAsStreamAsync(context.RequestAborted);
            await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
        }
    }

    internal static bool ShouldRenderTextDocument(HttpRequest request, HttpResponseMessage response)
    {
        // Only browser document navigations get a readable, controllable viewer.
        // Subresources keep their existing response/rewrite paths; downloads are not viewers.
        if (!IsViewableDocumentNavigation(request, response)) return false;

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        return (mediaType?.StartsWith("text/", StringComparison.OrdinalIgnoreCase) == true
                && !mediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
            || string.Equals(mediaType, "application/json", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/javascript", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/ecmascript", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/x-javascript", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "application/xml", StringComparison.OrdinalIgnoreCase)
            || string.Equals(mediaType, "image/svg+xml", StringComparison.OrdinalIgnoreCase)
            || (mediaType?.StartsWith("application/", StringComparison.OrdinalIgnoreCase) == true
                && (mediaType.EndsWith("+json", StringComparison.OrdinalIgnoreCase)
                    || mediaType.EndsWith("+xml", StringComparison.OrdinalIgnoreCase)));
    }

    private static bool IsDocumentNavigation(HttpRequest request) =>
        HttpMethods.IsGet(request.Method)
        && string.Equals(request.Headers["Sec-Fetch-Mode"], "navigate", StringComparison.OrdinalIgnoreCase)
        && request.Headers["Sec-Fetch-Dest"].ToString() is "document" or "iframe" or "frame";

    private static bool IsViewableDocumentNavigation(HttpRequest request, HttpResponseMessage response) =>
        IsDocumentNavigation(request)
        && response.StatusCode != HttpStatusCode.PartialContent
        && response.StatusCode is not (HttpStatusCode.NoContent or HttpStatusCode.ResetContent or HttpStatusCode.NotModified)
        && response.Content.Headers.ContentRange is null
        && (!response.Content.Headers.Contains("Content-Disposition")
            || string.Equals(response.Content.Headers.ContentDisposition?.DispositionType, "inline", StringComparison.OrdinalIgnoreCase));

    internal static bool ShouldRenderImageDocument(HttpRequest request, HttpResponseMessage response) =>
        IsViewableDocumentNavigation(request, response)
        && IsRasterImageDocument(response.Content.Headers.ContentType?.MediaType);

    private static bool IsRasterImageDocument(string? mediaType) => mediaType?.ToLowerInvariant() is
        "image/png" or "image/jpeg" or "image/gif" or "image/webp" or "image/avif" or "image/apng" or "image/bmp" or "image/x-icon" or "image/vnd.microsoft.icon";

    private static string DocumentHeading(string? url, string? contentType)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https")
            url = new UriBuilder(uri) { UserName = "", Password = "" }.Uri.AbsoluteUri;
        return "<header><code>" + WebUtility.HtmlEncode(contentType ?? "") + "</code><p>"
            + WebUtility.HtmlEncode(url ?? "") + "</p></header>";
    }

    // Encode slashes too: nested HTML proxy passes must not rewrite source-code url() tokens.
    internal static string RenderTextDocument(string text, string? url = null, string? contentType = null) =>
        "<!doctype html><html><head><meta charset=\"utf-8\"><title>Response</title>"
        + "<style>body{margin:16px}pre{white-space:pre-wrap;overflow-wrap:anywhere}</style>"
        + "</head><body>" + DocumentHeading(url, contentType) + "<pre>" + WebUtility.HtmlEncode(text).Replace("/", "&#47;", StringComparison.Ordinal) + "</pre></body></html>";

    internal static string RenderImageDocument(byte[] body, string mediaType, string url)
    {
        if (!IsRasterImageDocument(mediaType)) throw new ArgumentException("Unsupported image document type.", nameof(mediaType));
        return "<!doctype html><html><head><meta charset=\"utf-8\"><title>Image</title>"
            + "<style>body{margin:16px}img{max-width:100%;height:auto}header{overflow-wrap:anywhere}</style>"
            + "</head><body>" + DocumentHeading(url, mediaType)
            + "<img alt=\"Response image\" src=\"data:" + mediaType.ToLowerInvariant() + ";base64,"
            + Convert.ToBase64String(body) + "\"></body></html>";
    }

    // Ownership of a newly created decoder transfers to the caller's await-using scope.
    private static Stream? OpenDecodedDocumentStream(Stream raw, string? encoding) => encoding?.ToLowerInvariant() switch
    {
        "gzip" => new GZipStream(raw, CompressionMode.Decompress, leaveOpen: true),
        "br" => new BrotliStream(raw, CompressionMode.Decompress, leaveOpen: true),
        "deflate" => new DeflateStream(raw, CompressionMode.Decompress, leaveOpen: true),
        _ => null
    };

    internal static async Task<(byte[]? Body, string? Error)> ReadDocumentBodyAsync(
        HttpResponseMessage response, int maximumBytes, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(10));
        try
        {
            if (response.Content.Headers.ContentEncoding.Count > 1)
                return (null, "Preview unavailable: multiple response content encodings are unsupported.");
            await using var raw = await response.Content.ReadAsStreamAsync(deadline.Token);
            var encoding = response.Content.Headers.ContentEncoding.FirstOrDefault()?.ToLowerInvariant();
            if (encoding is not (null or "identity" or "gzip" or "br" or "deflate"))
                return (null, "Preview unavailable: unsupported response content encoding.");
            await using var decoder = OpenDecodedDocumentStream(raw, encoding);
            using var copy = new MemoryStream();
            var buffer = new byte[8192];
            while (true)
            {
                var count = await (decoder ?? raw).ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, maximumBytes + 1 - (int)copy.Length)), deadline.Token);
                if (count == 0) return (copy.ToArray(), null);
                copy.Write(buffer, 0, count);
                if (copy.Length > maximumBytes)
                    return (null, $"Preview unavailable: decoded response exceeds the {maximumBytes.ToString(System.Globalization.CultureInfo.InvariantCulture)} byte document limit. The source was not displayed or executed.");
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return (null, "Preview unavailable: the response body did not finish within 10 seconds.");
        }
        catch (InvalidDataException)
        {
            return (null, "Preview unavailable: invalid compressed response body.");
        }
        catch (IOException)
        {
            return (null, "Preview unavailable: the response body could not be read completely.");
        }
    }

    private async Task ProxyFileAsync(HttpContext context, string routeKey, Uri targetUri, string path)
    {
        if (!HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method))
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }

        var requestedFileUri = BuildRequestedFileUri(targetUri, path);
        if (!requestedFileUri.IsFile)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        var localPath = requestedFileUri.LocalPath;
        if (Directory.Exists(localPath))
        {
            localPath = Path.Combine(localPath, "index.html");
        }

        var localRoot = _service.GetLocalRootByRouteKey(routeKey);
        if (localRoot is not null && WebPreviewLocalDirectory.ResolveFile(localRoot, Path.GetRelativePath(localRoot, localPath)) is null)
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        if (!File.Exists(localPath))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("File not found.", context.RequestAborted);
            return;
        }

        var contentType = GetContentType(localPath);
        context.Response.StatusCode = StatusCodes.Status200OK;
        context.Response.ContentType = contentType;
        context.Response.ContentLength = new FileInfo(localPath).Length;
        ApplyForcedReloadHeaders(context.Request, context.Response);

        if (HttpMethods.IsHead(context.Request.Method))
        {
            return;
        }

        if (contentType.StartsWith("text/html", StringComparison.OrdinalIgnoreCase))
        {
            await ProxyLocalHtmlResponseAsync(context, routeKey, requestedFileUri, localPath);
            return;
        }

        await using var stream = File.OpenRead(localPath);
        await stream.CopyToAsync(context.Response.Body, context.RequestAborted);
    }

    private async Task ProxyLocalHtmlResponseAsync(HttpContext context, string routeKey, Uri requestedFileUri, string localPath)
    {
        var html = await File.ReadAllTextAsync(localPath, context.RequestAborted);
        var reloadToken = GetPreviewReloadToken(context.Request.Query);

        var routePrefix = PreviewProxyAuthorization.GetProxyPrefix(context, routeKey);
        html = RootRelativeAttrRegex().Replace(html, m => RewriteRootRelativeAttributeUrl(m, routePrefix, reloadToken));
        html = RewriteSrcsetAttributes(html, routePrefix, Uri.UriSchemeHttps, null, reloadToken);
        html = RootRelativeCssUrlRegex().Replace(html, m => RewriteRootRelativeCssUrl(m, routePrefix, reloadToken));
        html = RewriteCssImports(html, routePrefix, Uri.UriSchemeHttps, null, reloadToken);

        html = MetaRefreshRegex().Replace(html, m =>
        {
            var prefix = m.Groups[1].Value;
            var url = m.Groups[2].Value;
            if (url.StartsWith('/', StringComparison.Ordinal) && !url.StartsWith(routePrefix + "/", StringComparison.Ordinal))
                return prefix + AppendReloadTokenToProxyUrl(routePrefix + url, reloadToken);
            return m.Value;
        });

        html = ExistingBaseTagRegex().Replace(html, "");
        html = UpstreamSecurityMetaTagRegex().Replace(html, "");

        var baseHref = ComputeBaseHref(routePrefix, requestedFileUri.ToString());
        var targetOrigin = $"{context.Request.Scheme}://{context.Request.Host}";
        var originScript = $"<script>window.__mtTargetOrigin=\"{targetOrigin}\";</script>";
        html = MarkSubframeSources(html);
        html = InjectHeadContent(html, $"<base href=\"{WebUtility.HtmlEncode(baseHref)}\">{originScript}" + GetUrlRewriteScript(routePrefix, upstreamHtml: html));

        context.Response.Headers.Remove("Content-Length");
        context.Response.ContentType = "text/html; charset=utf-8";
        await context.Response.WriteAsync(html, context.RequestAborted);
    }

    private async Task ProxyExternalWebSocketAsync(HttpContext context, string routeKey)
    {
        var externalUrl = context.Request.Query["u"].FirstOrDefault();
        if (string.IsNullOrEmpty(externalUrl) || !Uri.TryCreate(externalUrl, UriKind.Absolute, out var extUri))
        {
            context.Response.StatusCode = 400;
            context.Response.ContentType = "text/plain";
            await context.Response.WriteAsync("Missing or invalid 'u' parameter.", context.RequestAborted);
            return;
        }

        if (extUri.Scheme is not ("ws" or "wss" or "http" or "https"))
        {
            context.Response.StatusCode = 400;
            return;
        }

        var wsScheme = extUri.Scheme switch
        {
            "https" => "wss",
            "http" => "ws",
            _ => extUri.Scheme
        };

        var upstreamUri = new UriBuilder(extUri) { Scheme = wsScheme }.Uri;
        var upstreamOriginScheme = wsScheme == "wss" ? "https" : "http";
        var upstreamOrigin = $"{upstreamOriginScheme}://{upstreamUri.Authority}";
        await ProxyWebSocketToUpstreamAsync(context, routeKey, upstreamUri, upstreamOrigin);
    }

    private async Task HandleStorageWriteAsync(HttpContext context, string routeKey)
    {
        context.Response.Headers.CacheControl = "no-store";
        if (_storage is null || context.Request.Method != HttpMethods.Post)
        {
            context.Response.StatusCode = StatusCodes.Status405MethodNotAllowed;
            return;
        }
        const int maximumBodyBytes = 1024 * 1024;
        using var body = new MemoryStream();
        var bytes = new byte[8192];
        while (true)
        {
            var read = await context.Request.Body.ReadAsync(bytes, context.RequestAborted);
            if (read == 0) break;
            if (body.Length + read > maximumBodyBytes)
            {
                context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
                return;
            }
            body.Write(bytes.AsSpan(0, read));
        }
        try
        {
            var request = JsonSerializer.Deserialize(body.GetBuffer().AsSpan(0, checked((int)body.Length)), PreviewStorageJsonContext.Default.PreviewStorageWrite);
            context.Response.StatusCode = request is not null && _storage.Apply(routeKey, request)
                ? StatusCodes.Status204NoContent : StatusCodes.Status409Conflict;
        }
        catch (JsonException)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
        }
    }

    private async Task HandleCookieBridgeAsync(HttpContext context, string routeKey)
    {
        var cookieRequestUri = ResolveCookieBridgeRequestUri(context.Request);
        if (context.Request.Method == HttpMethods.Get)
        {
            var response = _service.GetBrowserCookies(routeKey, cookieRequestUri);
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(
                context.Response.Body,
                response,
                AppJsonContext.Default.WebPreviewCookiesResponse,
                context.RequestAborted);
            return;
        }

        if (context.Request.Method == HttpMethods.Post)
        {
            WebPreviewCookieSetRequest? request;
            try
            {
                request = await JsonSerializer.DeserializeAsync(
                    context.Request.Body,
                    AppJsonContext.Default.WebPreviewCookieSetRequest,
                    context.RequestAborted);
            }
            catch (JsonException)
            {
                context.Response.StatusCode = 400;
                return;
            }

            if (request is null || !_service.SetCookieFromRaw(routeKey, request.Raw, cookieRequestUri, allowHttpOnly: false))
            {
                context.Response.StatusCode = 400;
                return;
            }

            var response = _service.GetBrowserCookies(routeKey, cookieRequestUri);
            context.Response.ContentType = "application/json";
            await JsonSerializer.SerializeAsync(
                context.Response.Body,
                response,
                AppJsonContext.Default.WebPreviewCookiesResponse,
                context.RequestAborted);
            return;
        }

        context.Response.StatusCode = 405;
    }

    private Uri? ResolveCookieBridgeRequestUri(HttpRequest request)
    {
        if (!TryResolvePreviewFromRequest(request, out _, out var targetUri))
            return null;

        if (targetUri is null)
            return null;

        var requestedUrl = request.Query["u"].FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(requestedUrl)
            && Uri.TryCreate(requestedUrl, UriKind.Absolute, out var explicitUri))
        {
            return explicitUri;
        }

        if (!request.Headers.TryGetValue("Referer", out var refererValues))
            return targetUri;

        if (!Uri.TryCreate(refererValues.ToString(), UriKind.Absolute, out var refererUri))
            return targetUri;

        if (!TryParseProxyRoute(refererUri.AbsolutePath, out _, out var refererRemainingPath))
            return targetUri;

        if (string.Equals(refererRemainingPath, "/_ext", StringComparison.OrdinalIgnoreCase))
        {
            var externalUrl = QueryHelpers.ParseQuery(refererUri.Query)["u"].FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(externalUrl)
                && Uri.TryCreate(externalUrl, UriKind.Absolute, out var externalUri))
            {
                return externalUri;
            }

            return targetUri;
        }

        var upstreamUrl = BuildUpstreamUrlFromPath(targetUri, refererRemainingPath, refererUri.Query);
        return Uri.TryCreate(upstreamUrl, UriKind.Absolute, out var upstreamUri)
            ? upstreamUri
            : targetUri;
    }

    internal static bool ShouldAdoptCanonicalTarget(
        HttpRequest request,
        HttpResponseMessage upstreamResponse,
        string? finalUrl,
        string? currentAuthority,
        out Uri canonicalUri)
    {
        canonicalUri = null!;

        if (request.Query[PreviewSubframeQueryParam] == "1") return false;

        if (string.IsNullOrWhiteSpace(finalUrl))
            return false;

        if (!HttpMethods.IsGet(request.Method))
            return false;

        var mediaType = upstreamResponse.Content.Headers.ContentType?.MediaType;
        if (!string.Equals(mediaType, "text/html", StringComparison.OrdinalIgnoreCase))
            return false;

        if (request.Headers.TryGetValue("Sec-Fetch-Mode", out var mode)
            && !string.Equals(mode.ToString(), "navigate", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (request.Headers.TryGetValue("Sec-Fetch-Dest", out var destination))
        {
            var destValue = destination.ToString();
            if (!string.Equals(destValue, "document", StringComparison.OrdinalIgnoreCase)
                && !string.Equals(destValue, "iframe", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        if (!Uri.TryCreate(finalUrl, UriKind.Absolute, out var finalUri))
            return false;

        if (string.IsNullOrWhiteSpace(currentAuthority)
            || finalUri.Authority.Equals(currentAuthority, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        canonicalUri = finalUri;
        return true;
    }

    private static async Task<string> DecompressTextAsync(
        HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var contentEncoding = response.Content.Headers.ContentEncoding.FirstOrDefault();
        await using var rawStream = await response.Content.ReadAsStreamAsync(cancellationToken);

        if (contentEncoding is "gzip")
        {
            await using var decompressed = new GZipStream(rawStream, CompressionMode.Decompress);
            using var reader = new StreamReader(decompressed, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        if (contentEncoding is "br")
        {
            await using var decompressed = new BrotliStream(rawStream, CompressionMode.Decompress);
            using var reader = new StreamReader(decompressed, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        if (contentEncoding is "deflate")
        {
            await using var decompressed = new DeflateStream(rawStream, CompressionMode.Decompress);
            using var reader = new StreamReader(decompressed, Encoding.UTF8);
            return await reader.ReadToEndAsync(cancellationToken);
        }

        using (var reader = new StreamReader(rawStream, Encoding.UTF8, leaveOpen: true))
        {
            return await reader.ReadToEndAsync(cancellationToken);
        }
    }

    private async Task ProxyWebSocketAsync(HttpContext context, string routeKey, Uri targetUri, string path)
    {
        SyncSelfTargetAuthCookie(routeKey, context.Request, targetUri);
        var targetBase = targetUri.AbsolutePath.TrimEnd('/');
        var hasSubpath = !string.IsNullOrEmpty(targetBase) && targetBase != "/";

        // Use learned root fallback for WebSocket paths too
        string wsPath;
        if (hasSubpath && ShouldTryRootFirst(routeKey, path, targetBase))
        {
            wsPath = string.IsNullOrEmpty(path) || path == "/" ? "/" : path;
            if (!wsPath.StartsWith('/', StringComparison.Ordinal))
                wsPath = "/" + wsPath;
        }
        else
        {
            wsPath = BuildUpstreamPath(targetUri, path);
        }

        var upstreamUrl = BuildUpstreamWsUrlFromPath(targetUri, wsPath, StripPreviewBootstrapQuery(context.Request.QueryString.Value));
        var upstreamUri = new Uri(upstreamUrl);
        var upstreamOrigin = $"{targetUri.Scheme}://{targetUri.Authority}";
        await ProxyWebSocketToUpstreamAsync(context, routeKey, upstreamUri, upstreamOrigin, targetUri);
    }

    private async Task ProxyWebSocketToUpstreamAsync(
        HttpContext context, string routeKey, Uri upstreamUri, string upstreamOrigin, Uri? targetUri = null)
    {
        using var upstream = new ClientWebSocket();
        // Configure SSL + forward server-side cookie jar (for SignalR session correlation)
        _service.ConfigureWebSocket(routeKey, upstream, upstreamUri);

        // Forward all request headers except blocked ones (same blocklist as HTTP)
        foreach (var header in context.Request.Headers)
        {
            if (IsBlockedRequestHeader(header.Key)
                || IsTlbxAuthenticationHeader(context.Request, header.Key))
                continue;
            // Skip WebSocket upgrade headers — ClientWebSocket manages these
            if (header.Key.StartsWith("Sec-WebSocket-", StringComparison.OrdinalIgnoreCase))
                continue;

            var value = header.Value.ToString();

            // Rewrite Origin/Referer to match upstream host — Blazor/SignalR validates
            // these against its own host and rejects connections from foreign origins
            if (header.Key.Equals("Origin", StringComparison.OrdinalIgnoreCase))
            {
                value = upstreamOrigin;
            }
            else if (header.Key.Equals("Referer", StringComparison.OrdinalIgnoreCase))
            {
                value = RewriteRefererForUpstream(value, routeKey, targetUri ?? new Uri(upstreamOrigin), context.Request);
                if (string.IsNullOrEmpty(value))
                    continue;
            }

            try
            {
                upstream.Options.SetRequestHeader(header.Key, value);
            }
            catch (ArgumentException)
            {
                // Some headers can't be set on ClientWebSocket — skip silently
            }
        }

        // Forward WebSocket sub-protocols (critical for SignalR)
        var subProtocols = context.WebSockets.WebSocketRequestedProtocols;
        foreach (var protocol in subProtocols)
        {
            upstream.Options.AddSubProtocol(protocol);
        }

        var wsType = context.Request.Path.Value?.Contains("/_ext", StringComparison.Ordinal) == true ? "ext-ws" : "ws";
        var wsSw = Stopwatch.StartNew();

        try
        {
            await upstream.ConnectAsync(upstreamUri, context.RequestAborted);
        }
        catch (WebSocketException ex)
        {
            wsSw.Stop();
            LogWebSocket(context, routeKey, wsType, upstreamUri.ToString(), subProtocols, null, wsSw.ElapsedMilliseconds, 502, ex.Message);
            context.Response.StatusCode = 502;
            return;
        }
        catch (HttpRequestException ex)
        {
            wsSw.Stop();
            LogWebSocket(context, routeKey, wsType, upstreamUri.ToString(), subProtocols, null, wsSw.ElapsedMilliseconds, 502, ex.Message);
            context.Response.StatusCode = 502;
            return;
        }

        wsSw.Stop();
        LogWebSocket(context, routeKey, wsType, upstreamUri.ToString(), subProtocols, upstream.SubProtocol, wsSw.ElapsedMilliseconds, 101, null);

        // Accept downstream with the negotiated sub-protocol from upstream
        var acceptProtocol = upstream.SubProtocol;
        using var downstream = acceptProtocol is not null
            ? await context.WebSockets.AcceptWebSocketAsync(acceptProtocol)
            : await context.WebSockets.AcceptWebSocketAsync();

        // WebSocket messages are relayed without content rewriting. The page lives
        // under /webpreview/ and JS sees proxy URLs in location.href and document.baseURI.
        // Frameworks like Blazor store client-provided URLs (proxy URLs) in their state
        // and use relative paths for routing — the absolute origin doesn't matter.
        // When the server echoes URLs back, they're already proxy URLs. No rewriting needed.
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted);

        var downToUp = RelayWebSocketAsync(downstream, upstream, cts);
        var upToDown = RelayWebSocketAsync(upstream, downstream, cts);

        await Task.WhenAny(downToUp, upToDown);
        await cts.CancelAsync();

        await CloseWebSocketSafe(downstream);
        await CloseWebSocketSafe(upstream);
    }

    private static async Task RelayWebSocketAsync(
        WebSocket source, WebSocket destination, CancellationTokenSource cts)
    {
        var buffer = new byte[WsBufferSize];
        try
        {
            while (source.State == WebSocketState.Open && !cts.Token.IsCancellationRequested)
            {
                var result = await source.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                await destination.SendAsync(
                    new ArraySegment<byte>(buffer, 0, result.Count),
                    result.MessageType,
                    result.EndOfMessage,
                    cts.Token);
            }
        }
        catch (OperationCanceledException)
        {
            // Expected on shutdown
        }
        catch (WebSocketException)
        {
            // Connection dropped
        }
    }


    private static async Task CloseWebSocketSafe(WebSocket ws)
    {
        try
        {
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
            {
                using var timeout = new CancellationTokenSource(WsCloseTimeout);
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
            }
        }
        catch
        {
            // Best effort
        }
    }

    private static string BuildUpstreamUrl(Uri target, string path, string? queryString)
    {
        var sb = new StringBuilder(256);
        sb.Append(target.Scheme).Append("://").Append(target.Authority);
        sb.Append(BuildUpstreamPath(target, path));
        if (!string.IsNullOrEmpty(queryString))
        {
            sb.Append(queryString);
        }
        return sb.ToString();
    }

    private void SyncSelfTargetAuthCookie(string routeKey, HttpRequest request, Uri targetUri)
    {
        if (!RequestAccessContext.HasFullUserAccess(request.HttpContext) || !_service.IsSelfTarget(targetUri))
        {
            return;
        }

        if (request.Cookies.TryGetValue(AuthService.SessionCookieName, out var token))
        {
            _service.SyncSessionCookieForSelfTarget(routeKey, token, targetUri);
        }
    }

    private static string BuildUpstreamWsUrl(Uri target, string path, string? queryString)
    {
        var scheme = target.Scheme == "https" ? "wss" : "ws";
        var sb = new StringBuilder(256);
        sb.Append(scheme).Append("://").Append(target.Authority);
        sb.Append(BuildUpstreamPath(target, path));
        if (!string.IsNullOrEmpty(queryString))
        {
            sb.Append(queryString);
        }
        return sb.ToString();
    }

    internal static string BuildUpstreamPath(Uri target, string path)
    {
        // Only the route without a path is an alias for the configured entry.
        // An explicit slash is an origin-root navigation, including after login.
        if (string.IsNullOrEmpty(path))
        {
            return string.IsNullOrEmpty(target.AbsolutePath) ? "/" : target.AbsolutePath;
        }

        var normalizedPath = path.StartsWith('/', StringComparison.Ordinal) ? path : "/" + path;

        // Relative URLs have already resolved against the injected document base.
        // A rooted path identifies the upstream origin, not a directory under the target.
        return normalizedPath;
    }

    private static string BuildUpstreamUrlFromPath(Uri target, string upstreamPath, string? queryString)
    {
        var sb = new StringBuilder(256);
        sb.Append(target.Scheme).Append("://").Append(target.Authority);
        sb.Append(upstreamPath);
        if (!string.IsNullOrEmpty(queryString))
            sb.Append(queryString);
        return sb.ToString();
    }

    private static string BuildUpstreamWsUrlFromPath(Uri target, string upstreamPath, string? queryString)
    {
        var scheme = target.Scheme == "https" ? "wss" : "ws";
        var sb = new StringBuilder(256);
        sb.Append(scheme).Append("://").Append(target.Authority);
        sb.Append(upstreamPath);
        if (!string.IsNullOrEmpty(queryString))
            sb.Append(queryString);
        return sb.ToString();
    }

    internal static string StripPreviewBootstrapQuery(string? queryString)
    {
        if (string.IsNullOrWhiteSpace(queryString))
        {
            return "";
        }

        var parsed = QueryHelpers.ParseQuery(queryString);
        if (!parsed.ContainsKey(PreviewBootstrapIdQueryParam)
            && !parsed.ContainsKey(PreviewBootstrapTokenQueryParam)
            && !parsed.ContainsKey(PreviewTargetRevisionQueryParam)
            && !parsed.ContainsKey(PreviewReloadTokenQueryParam)
            && !parsed.ContainsKey(PreviewSubframeQueryParam))
        {
            return queryString ?? "";
        }

        var sanitized = new List<KeyValuePair<string, string?>>();
        foreach (var entry in parsed)
        {
            if (entry.Key.Equals(PreviewBootstrapIdQueryParam, StringComparison.Ordinal)
                || entry.Key.Equals(PreviewBootstrapTokenQueryParam, StringComparison.Ordinal)
                || entry.Key.Equals(PreviewTargetRevisionQueryParam, StringComparison.Ordinal)
                || entry.Key.Equals(PreviewReloadTokenQueryParam, StringComparison.Ordinal)
                || entry.Key.Equals(PreviewSubframeQueryParam, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var value in entry.Value)
            {
                sanitized.Add(new KeyValuePair<string, string?>(entry.Key, value));
            }
        }

        return QueryString.Create(sanitized).Value ?? "";
    }

    private static string? GetPreviewReloadToken(IQueryCollection query)
    {
        if (!query.TryGetValue(PreviewReloadTokenQueryParam, out var values))
        {
            return null;
        }

        var token = values.FirstOrDefault();
        return string.IsNullOrWhiteSpace(token) ? null : token;
    }

    private static void ApplyForcedReloadHeaders(HttpRequest request, HttpResponse response)
    {
        if (GetPreviewReloadToken(request.Query) is null)
        {
            return;
        }

        response.Headers.CacheControl = "no-store, no-cache, must-revalidate";
        response.Headers.Pragma = "no-cache";
        response.Headers.Expires = "0";
    }

    private static string AppendReloadTokenToProxyUrl(string proxyUrl, string? reloadToken)
    {
        if (string.IsNullOrWhiteSpace(reloadToken)
            || string.IsNullOrWhiteSpace(proxyUrl)
            || !proxyUrl.StartsWith(ProxyPrefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            return proxyUrl;
        }

        var placeholder = new Uri($"https://midterm.invalid{proxyUrl}");
        var builder = new UriBuilder(placeholder);
        var parsed = QueryHelpers.ParseQuery(builder.Query);
        var sanitized = new List<KeyValuePair<string, string?>>();
        foreach (var entry in parsed)
        {
            if (entry.Key.Equals(PreviewReloadTokenQueryParam, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (var value in entry.Value)
            {
                sanitized.Add(new KeyValuePair<string, string?>(entry.Key, value));
            }
        }

        sanitized.Add(new KeyValuePair<string, string?>(PreviewReloadTokenQueryParam, reloadToken));
        builder.Query = QueryString.Create(sanitized).Value;
        return builder.Uri.PathAndQuery + builder.Uri.Fragment;
    }

    private static bool PathAlreadyUnderTarget(string path, string targetBase)
    {
        var normalized = string.IsNullOrEmpty(path) ? "/" : path;
        if (!normalized.StartsWith('/', StringComparison.Ordinal))
            normalized = "/" + normalized;
        return normalized.StartsWith(targetBase + "/", StringComparison.OrdinalIgnoreCase)
            || normalized.Equals(targetBase, StringComparison.OrdinalIgnoreCase);
    }

    private bool ShouldTryRootFirst(string routeKey, string path, string targetBase)
    {
        ResetFallbackCacheIfTargetChanged(routeKey, _service.GetTargetUriByRouteKey(routeKey)?.ToString());
        var prefix = GetPathPrefix(path);
        var routeCache = GetOrCreateRootFallbackCache(routeKey);
        return prefix is not null && routeCache.TryGetValue(prefix, out var preferRoot) && preferRoot;
    }

    private void LearnRootFallback(string routeKey, string path, string targetUrl)
    {
        ResetFallbackCacheIfTargetChanged(routeKey, targetUrl);
        var prefix = GetPathPrefix(path);
        if (prefix is not null)
            GetOrCreateRootFallbackCache(routeKey)[prefix] = true;
    }

    private void UnlearnRootFallback(string routeKey, string path)
    {
        var prefix = GetPathPrefix(path);
        if (prefix is not null && _rootFallbackPrefixesByRoute.TryGetValue(routeKey, out var routeCache))
            routeCache.Remove(prefix);
    }

    private void ResetFallbackCacheIfTargetChanged(string routeKey, string? currentTarget)
    {
        _rootFallbackTargetsByRoute.TryGetValue(routeKey, out var previousTarget);
        if (previousTarget != currentTarget)
        {
            GetOrCreateRootFallbackCache(routeKey).Clear();
            _rootFallbackTargetsByRoute[routeKey] = currentTarget;
        }
    }

    private Dictionary<string, bool> GetOrCreateRootFallbackCache(string routeKey)
    {
        if (_rootFallbackPrefixesByRoute.TryGetValue(routeKey, out var existing))
        {
            return existing;
        }

        var created = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        _rootFallbackPrefixesByRoute[routeKey] = created;
        return created;
    }

    private static string? GetPathPrefix(string path)
    {
        var normalized = string.IsNullOrEmpty(path) ? "/" : path;
        if (!normalized.StartsWith('/', StringComparison.Ordinal))
            normalized = "/" + normalized;
        var queryIdx = normalized.IndexOf('?', StringComparison.Ordinal);
        if (queryIdx >= 0)
            normalized = normalized[..queryIdx];
        if (normalized == "/")
            return null;
        var secondSlash = normalized.IndexOf('/', 1, StringComparison.Ordinal);
        return secondSlash > 0 ? normalized[..secondSlash] + "/" : normalized + "/";
    }

    [GeneratedRegex(@"<head(\s[^>]*)?>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex HeadTagRegex();

    [GeneratedRegex(@"\A\s*(?:<!doctype\b[^>]*>\s*)?(?:<html\b[^>]*>\s*)?", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex HtmlDocumentPrefixRegex();

    [GeneratedRegex("(<iframe\\b[^>]*?\\bsrc\\s*=\\s*[\"'])([^\"']*)([\"'])", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex IframeSourceRegex();

    [GeneratedRegex("(\\b(?:src|href|action|poster|formaction)\\s*=\\s*[\"'])([^\"']*)([\"'])", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex DocumentUrlAttributeRegex();

    // Matches existing <base ...> tags (self-closing or not) to remove before injecting ours
    [GeneratedRegex(@"<base\s[^>]*>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ExistingBaseTagRegex();

    // Extracts the href value from a <base href="..."> tag
    [GeneratedRegex(@"<base\s[^>]*href\s*=\s*[""']([^""']*)[""']", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex BaseHrefValueRegex();

    // Matches <meta http-equiv="content-security-policy" ...> and <meta http-equiv="x-frame-options" ...>
    // Upstream CSP/XFO meta tags must be stripped: after proxying, 'self' resolves to MidTerm's origin,
    // which would block framing of the upstream site's own resources.
    [GeneratedRegex(@"<meta\s[^>]*http-equiv\s*=\s*[""']\s*(?:content-security-policy|x-frame-options)\s*[""'][^>]*>", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex UpstreamSecurityMetaTagRegex();

    // Matches <meta http-equiv="refresh" content="N;url=/path"> for PHP-style redirects
    [GeneratedRegex(@"(<meta\s[^>]*content\s*=\s*[""']\d+\s*;\s*url\s*=\s*)([^""'>\s]+)", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex MetaRefreshRegex();

    // Matches src="/foo", href="/bar", action="/baz", poster="/img.png" with the full URL value.
    // Requires at least one path character after / to avoid matching broken attributes like href="/".
    [GeneratedRegex(@"(\b(?:src|href|action|poster|formaction|data)\s*=\s*[""'])(/(?!/)[^""'\s>]+)([""'])", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex RootRelativeAttrRegex();

    // Matches full srcset/imagesrcset attributes so every candidate URL can be
    // rewritten, not only the first candidate.
    [GeneratedRegex(@"\b(srcset|imagesrcset)\s*=\s*([""'])(.*?)\2", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex SrcsetAttributeRegex();

    // Matches url(/...) in inline CSS (with optional quotes), capturing the full URL.
    [GeneratedRegex(@"(url\(\s*[""']?)(/(?!/)[^""')\s]+)([""']?\s*\))", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex RootRelativeCssUrlRegex();

    // Matches absolute http(s) URLs in HTML attributes: src="https://...", href="http://..."
    [GeneratedRegex(@"(\b(?:src|href|action|poster|formaction|data)\s*=\s*[""'])(https?://[^""'\s>]+)", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex AbsoluteUrlAttrRegex();

    // Matches absolute http(s) URLs in CSS url(): url(https://...) or url("https://...")
    [GeneratedRegex(@"(url\(\s*[""']?)(https?://[^""')>\s]+)", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex AbsoluteUrlCssRegex();

    [GeneratedRegex(@"(\b(?:src|href|action|poster|formaction|data)\s*=\s*[""'])(//[^""'\s>]+)", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ProtocolRelativeAttrRegex();

    [GeneratedRegex(@"(url\(\s*[""']?)(//[^""')>\s]+)", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ProtocolRelativeCssRegex();

    [GeneratedRegex(@"(@import\s+(?:url\(\s*)?[""']?)([^""')\s;]+)([""']?\s*\)?\s*;?)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex CssImportRegex();

    // External stylesheets only: consume comments/ordinary strings unchanged,
    // and isolate actual url() tokens and the string form of @import.
    [GeneratedRegex("""/\*[\s\S]*?(?:\*/|\z)|(?<prefix>@import\s+)(?<token>"(?:\\[\s\S]|[^"\\\r\n])*"|'(?:\\[\s\S]|[^'\\\r\n])*')|(?<prefix>(?<![\w-])url\(\s*)(?<token>"(?:\\[\s\S]|[^"\\\r\n])*"|'(?:\\[\s\S]|[^'\\\r\n])*'|(?:\\[\s\S]|[^\s"'()\\])*)(?<suffix>\s*\))|"(?:\\[\s\S]|[^"\\\r\n])*(?:"|(?=\r|\n|\z))|'(?:\\[\s\S]|[^'\\\r\n])*(?:'|(?=\r|\n|\z))""", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex StylesheetResourceRegex();

    [GeneratedRegex(@"\s+\bintegrity\s*=\s*(?:""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex StripIntegrityAttributeRegex();

    [GeneratedRegex(@"<([^>]+)>", RegexOptions.CultureInvariant, 1000)]
    private static partial Regex LinkHeaderUrlRegex();

    [GeneratedRegex(@"(\bimport\s+[^;""'\r\n]*?\bfrom\s*[""'])/(?!/|webpreview/)([^""']+)([""'])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex JSImportFromRegex();

    [GeneratedRegex(@"(\bimport\s*[""'])/(?!/|webpreview/)([^""']+)([""'])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex JSImportBareRegex();

    [GeneratedRegex(@"(\bimport\s*\(\s*[""'])/(?!/|webpreview/)([^""']+)([""'])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex JSImportDynamicRegex();

    [GeneratedRegex(@"(\bexport\s+[^;""'\r\n]*?\bfrom\s*[""'])/(?!/|webpreview/)([^""']+)([""'])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, 1000)]
    private static partial Regex JSExportFromRegex();

    // Matches rewritten proxy paths in HTML/CSS attributes so we can prime root
    // fallback prefixes before the browser requests them.
    [GeneratedRegex(@"[""'(=]\s*(/webpreview/[^""')\s,>]+)", RegexOptions.IgnoreCase, 1000)]
    private static partial Regex ProxiedPathRegex();

    /// <summary>
    /// Rewrite absolute external URL in an HTML attribute to go through the _ext proxy.
    /// URLs pointing to the target authority are rewritten to /webpreview/ (same-origin proxy).
    /// URLs pointing to other hosts go through /webpreview/_ext?u=...
    /// </summary>
    internal static string RewriteSrcsetAttributes(
        string content,
        string routePrefix,
        string targetScheme,
        string? targetAuthority,
        string? reloadToken)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        return SrcsetAttributeRegex().Replace(content, match =>
        {
            var name = match.Groups[1].Value;
            var quote = match.Groups[2].Value;
            var value = match.Groups[3].Value;
            var rewritten = RewriteSrcsetValue(value, routePrefix, targetScheme, targetAuthority, reloadToken);
            return $"{name}={quote}{rewritten}{quote}";
        });
    }

    internal static string RewriteSrcsetValue(
        string value,
        string routePrefix,
        string targetScheme,
        string? targetAuthority,
        string? reloadToken)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return Regex.Replace(
            value,
            @"(^|,\s*)([^\s,]+)",
            match =>
            {
                var prefix = match.Groups[1].Value;
                var url = match.Groups[2].Value;
                return prefix + RewriteUrlToken(url, routePrefix, targetScheme, targetAuthority, reloadToken);
            },
            RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(1000));
    }

    internal static string RewriteStylesheetResources(
        string content,
        string routePrefix,
        string? finalStylesheetUrl,
        Uri? primaryTarget,
        string? reloadToken = null)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(routePrefix)
            || !Uri.TryCreate(finalStylesheetUrl, UriKind.Absolute, out var stylesheet)
            || stylesheet.Scheme is not ("http" or "https")) return content;

        return StylesheetResourceRegex().Replace(content, match =>
        {
            var token = match.Groups["token"];
            if (!token.Success) return match.Value;
            var url = DecodeCssResourceToken(token.Value);
            if (string.IsNullOrEmpty(url) || url.StartsWith("#", StringComparison.Ordinal)
                || url == routePrefix || url.StartsWith(routePrefix + "/", StringComparison.Ordinal)
                || !Uri.TryCreate(stylesheet, url, out var resource)
                || resource.Scheme is not ("http" or "https")) return match.Value;

            var query = QueryHelpers.ParseQuery(resource.Query);
            var hasTransportNames = query.ContainsKey(PreviewBootstrapIdQueryParam)
                || query.ContainsKey(PreviewBootstrapTokenQueryParam) || query.ContainsKey(PreviewTargetRevisionQueryParam)
                || query.ContainsKey(PreviewReloadTokenQueryParam) || query.ContainsKey(PreviewSubframeQueryParam);
            string transport;
            if (!hasTransportNames && primaryTarget is not null
                && resource.Scheme.Equals(primaryTarget.Scheme, StringComparison.OrdinalIgnoreCase)
                && resource.Authority.Equals(primaryTarget.Authority, StringComparison.OrdinalIgnoreCase))
            {
                transport = routePrefix + resource.PathAndQuery + resource.Fragment;
            }
            else
            {
                // SVG/font fragments belong on the browser-facing URL too. The
                // encoded upstream query remains independent of outer controls.
                var upstream = new UriBuilder(resource) { Fragment = "", UserName = "", Password = "" }.Uri.AbsoluteUri;
                transport = routePrefix + "/_ext?u=" + Uri.EscapeDataString(upstream) + resource.Fragment;
            }
            transport = AppendReloadTokenToProxyUrl(transport, reloadToken);
            var quoted = "\"" + transport.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
            return match.Groups["prefix"].Value + quoted + match.Groups["suffix"].Value;
        });
    }

    private static string? DecodeCssResourceToken(string token)
    {
        var quoted = token.Length >= 2 && token[0] is '\'' or '"';
        var text = quoted ? token.AsSpan(1, token.Length - 2) : token.AsSpan();
        if (text.IndexOf('\\') < 0) return text.ToString();
        var value = new StringBuilder(text.Length);
        for (var index = 0; index < text.Length; index++)
        {
            var ch = text[index];
            if (ch != '\\') { value.Append(ch); continue; }
            if (++index == text.Length) return null;
            ch = text[index];
            if (ch is '\r' or '\n' or '\f')
            {
                if (!quoted) return null;
                if (ch == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
                continue;
            }
            if (!char.IsAsciiHexDigit(ch)) { value.Append(ch); continue; }
            var start = index;
            while (index + 1 < text.Length && index - start < 5 && char.IsAsciiHexDigit(text[index + 1])) index++;
            var code = Convert.ToInt32(text[start..(index + 1)].ToString(), 16);
            value.Append(char.ConvertFromUtf32(code is 0 or > 0x10FFFF or >= 0xD800 and <= 0xDFFF ? 0xFFFD : code));
            if (index + 1 < text.Length && text[index + 1] is ' ' or '\t' or '\r' or '\n' or '\f')
            {
                if (text[++index] == '\r' && index + 1 < text.Length && text[index + 1] == '\n') index++;
            }
        }
        return value.ToString();
    }

    internal static string RewriteCssImports(
        string content,
        string routePrefix,
        string targetScheme,
        string? targetAuthority,
        string? reloadToken)
    {
        if (string.IsNullOrEmpty(content))
        {
            return content;
        }

        return CssImportRegex().Replace(content, match =>
            match.Groups[1].Value
            + RewriteUrlToken(match.Groups[2].Value, routePrefix, targetScheme, targetAuthority, reloadToken)
            + match.Groups[3].Value);
    }

    private static string RewriteUrlToken(
        string url,
        string routePrefix,
        string targetScheme,
        string? targetAuthority,
        string? reloadToken)
    {
        if (string.IsNullOrWhiteSpace(url)
            || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("#", StringComparison.Ordinal))
        {
            return url;
        }

        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            return RewriteExternalUrlFromText("", targetScheme + ":" + url, routePrefix, targetAuthority, reloadToken);
        }

        if (url.StartsWith("/", StringComparison.Ordinal)
            && !url.StartsWith("//", StringComparison.Ordinal)
            && !url.StartsWith(routePrefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            return AppendReloadTokenToProxyUrl(routePrefix + url, reloadToken);
        }

        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return RewriteExternalUrlFromText("", url, routePrefix, targetAuthority, reloadToken);
        }

        return url;
    }

    private static string RewriteRootRelativeAttributeUrl(Match match, string routePrefix, string? reloadToken)
    {
        var prefix = match.Groups[1].Value;
        var url = match.Groups[2].Value;
        var suffix = match.Groups[3].Value;
        return prefix + AppendReloadTokenToProxyUrl(routePrefix + url, reloadToken) + suffix;
    }

    private static string RewriteRootRelativeCssUrl(Match match, string routePrefix, string? reloadToken)
    {
        var prefix = match.Groups[1].Value;
        var url = match.Groups[2].Value;
        var suffix = match.Groups[3].Value;
        return prefix + AppendReloadTokenToProxyUrl(routePrefix + url, reloadToken) + suffix;
    }

    private static string RewriteExternalUrl(Match match, string routePrefix, string? targetAuthority, string? reloadToken)
    {
        var prefix = match.Groups[1].Value;  // e.g. src="
        var url = match.Groups[2].Value;     // e.g. https://cdn.example.com/script.js

        // Same-authority URLs → /webpreview/path (already handled by root-relative rewriting,
        // but absolute same-authority URLs need rewriting too)
        if (targetAuthority is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Authority.Equals(targetAuthority, StringComparison.OrdinalIgnoreCase))
        {
            return prefix + AppendReloadTokenToProxyUrl(routePrefix + uri.PathAndQuery, reloadToken);
        }

        return prefix + AppendReloadTokenToProxyUrl(
            routePrefix + "/_ext?u=" + Uri.EscapeDataString(url),
            reloadToken);
    }

    private static string RewriteExternalCssUrl(Match match, string routePrefix, string? targetAuthority, string? reloadToken)
    {
        var prefix = match.Groups[1].Value;  // e.g. url(
        var url = match.Groups[2].Value;     // e.g. https://fonts.googleapis.com/css

        if (targetAuthority is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Authority.Equals(targetAuthority, StringComparison.OrdinalIgnoreCase))
        {
            return prefix + AppendReloadTokenToProxyUrl(routePrefix + uri.PathAndQuery, reloadToken);
        }

        return prefix + AppendReloadTokenToProxyUrl(
            routePrefix + "/_ext?u=" + Uri.EscapeDataString(url),
            reloadToken);
    }

    private static string RewriteProtocolRelativeUrl(
        Match match,
        string routePrefix,
        string targetScheme,
        string? targetAuthority,
        string? reloadToken)
    {
        var prefix = match.Groups[1].Value;
        var url = targetScheme + ":" + match.Groups[2].Value;
        return RewriteExternalUrlFromText(prefix, url, routePrefix, targetAuthority, reloadToken);
    }

    private static string RewriteProtocolRelativeCssUrl(
        Match match,
        string routePrefix,
        string targetScheme,
        string? targetAuthority,
        string? reloadToken)
    {
        var prefix = match.Groups[1].Value;
        var url = targetScheme + ":" + match.Groups[2].Value;
        return RewriteExternalUrlFromText(prefix, url, routePrefix, targetAuthority, reloadToken);
    }

    private static string RewriteExternalUrlFromText(
        string prefix,
        string url,
        string routePrefix,
        string? targetAuthority,
        string? reloadToken)
    {
        if (targetAuthority is not null && Uri.TryCreate(url, UriKind.Absolute, out var uri)
            && uri.Authority.Equals(targetAuthority, StringComparison.OrdinalIgnoreCase))
        {
            return prefix + AppendReloadTokenToProxyUrl(routePrefix + uri.PathAndQuery, reloadToken);
        }

        return prefix + AppendReloadTokenToProxyUrl(
            routePrefix + "/_ext?u=" + Uri.EscapeDataString(url),
            reloadToken);
    }

    internal static string RewriteLinkHeaderValue(
        string value,
        string routePrefix,
        Uri currentTargetUri,
        string? reloadToken)
    {
        return LinkHeaderUrlRegex().Replace(value, match =>
        {
            var url = match.Groups[1].Value.Trim();
            var rewritten = RewriteResponseHeaderUrl(url, routePrefix, currentTargetUri, reloadToken);
            return rewritten == url ? match.Value : "<" + rewritten + ">";
        });
    }

    internal static string RewriteRefreshHeaderValue(
        string value,
        string routePrefix,
        Uri currentTargetUri,
        string? reloadToken)
    {
        var match = Regex.Match(
            value,
            @"^(\s*\d+\s*;\s*url\s*=\s*)(.+?)\s*$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromMilliseconds(1000));
        if (!match.Success)
        {
            return value;
        }

        var url = match.Groups[2].Value.Trim().Trim('"', '\'');
        var rewritten = RewriteResponseHeaderUrl(url, routePrefix, currentTargetUri, reloadToken);
        return match.Groups[1].Value + rewritten;
    }

    internal static string RewriteResponseHeaderUrl(
        string url,
        string routePrefix,
        Uri currentTargetUri,
        string? reloadToken)
    {
        if (string.IsNullOrWhiteSpace(url)
            || url.StartsWith("data:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("blob:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("about:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase)
            || url.StartsWith(routePrefix + "/", StringComparison.OrdinalIgnoreCase))
        {
            return url;
        }

        if (url.StartsWith("//", StringComparison.Ordinal))
        {
            url = currentTargetUri.Scheme + ":" + url;
        }

        if (url.StartsWith("/", StringComparison.Ordinal) && !url.StartsWith("//", StringComparison.Ordinal))
        {
            return AppendReloadTokenToProxyUrl(routePrefix + url, reloadToken);
        }

        if (Uri.TryCreate(currentTargetUri, url, out var absolute)
            && absolute.Scheme is "http" or "https")
        {
            if (absolute.Authority.Equals(currentTargetUri.Authority, StringComparison.OrdinalIgnoreCase))
            {
                return AppendReloadTokenToProxyUrl(routePrefix + absolute.PathAndQuery, reloadToken);
            }

            return AppendReloadTokenToProxyUrl(
                routePrefix + "/_ext?u=" + Uri.EscapeDataString(absolute.ToString()),
                reloadToken);
        }

        return url;
    }

    internal static string RewriteJavaScriptModuleSpecifiers(
        string content,
        string routePrefix,
        string? finalScriptUrl,
        Uri? primaryTarget,
        string? reloadToken = null)
    {
        if (string.IsNullOrEmpty(routePrefix)
            || !Uri.TryCreate(finalScriptUrl, UriKind.Absolute, out var scriptUri)
            || scriptUri.Scheme is not ("http" or "https")) return content;

        return WebPreviewLocationRewriter.RewriteModuleSpecifiers(content, specifier =>
        {
            if (specifier.StartsWith(routePrefix + "/", StringComparison.Ordinal))
                return AppendReloadTokenToProxyUrl(specifier, reloadToken);
            // Bare names (including import-map keys such as #alias) stay native.
            if (!specifier.StartsWith("./", StringComparison.Ordinal)
                && !specifier.StartsWith("../", StringComparison.Ordinal)
                && !specifier.StartsWith("/", StringComparison.Ordinal)
                && !Uri.TryCreate(specifier, UriKind.Absolute, out _)) return specifier;
            if (!Uri.TryCreate(scriptUri, specifier, out var resolved)
                || resolved.Scheme is not ("http" or "https")) return specifier;

            // The CDN is the resolution base, never the preview's primary target.
            if (primaryTarget is not null
                && resolved.Authority.Equals(primaryTarget.Authority, StringComparison.OrdinalIgnoreCase))
                return AppendReloadTokenToProxyUrl(routePrefix + resolved.PathAndQuery + resolved.Fragment, reloadToken);
            return RewriteExternalUrlFromText("", resolved.AbsoluteUri, routePrefix, null, reloadToken);
        });
    }

    internal static string RewriteRootRelativeModuleSpecifiers(
        string content,
        string routePrefix,
        string? reloadToken = null)
    {
        if (string.IsNullOrEmpty(content) || string.IsNullOrEmpty(routePrefix))
        {
            return content;
        }

        content = JSImportFromRegex().Replace(content, m =>
            m.Groups[1].Value
            + AppendReloadTokenToProxyUrl($"{routePrefix}/{m.Groups[2].Value}", reloadToken)
            + m.Groups[3].Value);
        content = JSImportBareRegex().Replace(content, m =>
            m.Groups[1].Value
            + AppendReloadTokenToProxyUrl($"{routePrefix}/{m.Groups[2].Value}", reloadToken)
            + m.Groups[3].Value);
        content = JSImportDynamicRegex().Replace(content, m =>
            m.Groups[1].Value
            + AppendReloadTokenToProxyUrl($"{routePrefix}/{m.Groups[2].Value}", reloadToken)
            + m.Groups[3].Value);
        content = JSExportFromRegex().Replace(content, m =>
            m.Groups[1].Value
            + AppendReloadTokenToProxyUrl($"{routePrefix}/{m.Groups[2].Value}", reloadToken)
            + m.Groups[3].Value);
        return content;
    }

    private static bool IsFontResponse(string? contentType, string? path)
    {
        if (!string.IsNullOrEmpty(contentType)
            && (contentType.StartsWith("font/", StringComparison.OrdinalIgnoreCase)
                || contentType.Contains("font", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }

        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        return path.EndsWith(".woff", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".woff2", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".eot", StringComparison.OrdinalIgnoreCase);
    }
}

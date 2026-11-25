using System.Text.RegularExpressions;

namespace Ai.Tlbx.MidTerm.Services.WebPreview;

public sealed partial class WebPreviewProxyMiddleware
{
    // No proxy routes, cookies, preview identity, transport tokens or server APIs
    // enter this bootstrap. Opaque children cooperate only with their real parent.
    internal static string GetCooperativeChildScript() => "<script data-tlbx-child-dom=\"1\">"
        + GetCooperativeChildRuntime() + "</script>";

    private static string GetCooperativeChildRuntime() =>
        CooperativeChildBridge.Replace("/*__MT_QUERY_HELPERS__*/", SharedQueryHelpers, StringComparison.Ordinal)
            .Replace("/*__MT_CONTROL_HELPERS__*/", SharedControlHelpers, StringComparison.Ordinal);

    // Skip raw-text elements and comments: iframe-looking application strings are not HTML attributes.
    [GeneratedRegex("<!--.*?-->|<(?<raw>script|style|textarea|title)\\b[^>]*>.*?</\\k<raw>\\s*>|(?<prefix><iframe\\b(?:[^\"'>]|\"[^\"]*\"|'[^']*')*?\\ssrcdoc\\s*=\\s*)(?<quote>[\"'])(?<value>.*?)\\k<quote>", RegexOptions.IgnoreCase | RegexOptions.Singleline, 1000)]
    private static partial Regex SrcdocFrameRegex();

    private const string CooperativeChildBridge = """
        (function(){
          if(window.parent===window||window.__tlbxChildDomBridge||typeof crypto==="undefined"||typeof crypto.getRandomValues!=="function")return;
          window.__tlbxChildDomBridge=true;
          var parentWindow=window.parent,probes=new Map(),alive=true;
          var documentId=Array.from(crypto.getRandomValues(new Uint32Array(4)),function(n){return n.toString(16).padStart(8,"0");}).join("");
          var allowed=new Set(["query","exec","attrs","css","click","fill"]);
          /*__MT_QUERY_HELPERS__*/
          /*__MT_CONTROL_HELPERS__*/
          function send(message){if(alive)try{parentWindow.postMessage(message,"*");}catch(e){}}
          async function execute(msg){
            var res={success:true,result:null,error:null,matchCount:null};
            try{
              if(!msg||!allowed.has(msg.command))throw new Error("Unsupported cooperative child command");
              if(msg.command!=="exec"&&(typeof msg.selector!=="string"||!msg.selector.trim()))throw new Error("selector required");
              switch(msg.command){
                case "query":{
                  var els=previewQueryAll(msg.selector),parts=[],budget=queryBudget(),shown=0,mx=msg.maxDepth||0;
                  if(mx<0)throw new Error("maxDepth must be non-negative");
                  res.matchCount=els.length;
                  for(var i=0;i<els.length&&i<50;i++){
                    if(budget.nodes<=0||budget.chars<32){budget.truncated=true;break;}
                    parts.push(msg.textOnly?queryText(els[i],budget):truncEl(els[i],mx,budget));shown++;budget.chars-=5;
                  }
                  if(shown<els.length||budget.truncated)parts.push("[query truncated: "+shown+" of "+els.length+" matches; narrow selector/maxDepth]");
                  res.result=parts.join("\n---\n");break;
                }
                case "exec":{
                  if(typeof msg.value!=="string"||!msg.value)throw new Error("js code required");
                  // Evaluate in the child page global, never in the nonce/transport closure.
                  var value=await (0,eval)(msg.value);
                  res.result=value===undefined?"undefined":typeof value==="object"?JSON.stringify(value,function(k,v){return typeof v==="bigint"?String(v):v;}):String(value);break;
                }
                case "attrs":{
                  var els=previewQueryAll(msg.selector),parts=[];res.matchCount=els.length;
                  for(var i=0;i<els.length&&i<30;i++){var el=els[i].cloneNode(false);el.innerHTML="";parts.push(el.outerHTML.replace("></"+el.tagName.toLowerCase()+">",">"));}
                  res.result=parts.join("\n---\n");break;
                }
                case "css":{
                  if(typeof msg.value!=="string"||!msg.value)throw new Error("css properties required");
                  var els=previewQueryAll(msg.selector),props=msg.value.split(","),parts=[];res.matchCount=els.length;
                  for(var i=0;i<els.length&&i<20;i++){
                    var css=getComputedStyle(els[i]),lines=[msg.selector+" ("+(i+1)+" of "+els.length+")"];
                    for(var prop of props)lines.push("  "+prop.trim()+": "+css.getPropertyValue(prop.trim()));parts.push(lines.join("\n"));
                  }
                  res.result=parts.join("\n---\n");break;
                }
                case "click":{
                  var el=resolveControl(msg.selector,true),surface=clickSurface(el);controlIntent(surface.target,function(){assertClickEnabled(el,surface);},surface.point);
                  if(typeof surface.target.click==="function")surface.target.click();else surface.target.dispatchEvent(new MouseEvent("click",{bubbles:true,cancelable:true,composed:true,button:0}));
                  res.result="clicked";break;
                }
                case "fill":{
                  var el=resolveControl(msg.selector),value=msg.value||"";
                  if(el.isContentEditable){fillEditableHost(el,value);res.result="filled";break;}
                  if(!/^(INPUT|TEXTAREA|SELECT)$/.test(el.tagName))throw new Error("fill target is not an editable form control");
                  if(el.readOnly)throw new Error("fill target is read-only");
                  if(el.tagName==="INPUT"&&/^(file|checkbox|radio|button|submit|reset|image|hidden)$/i.test(el.type))throw new Error("fill does not support input type "+el.type);
                  if(el.tagName==="SELECT"&&!Array.from(el.options).some(function(o){return o.value===value&&!o.disabled&&!(o.parentElement.tagName==="OPTGROUP"&&o.parentElement.disabled);}))throw new Error("select has no enabled option with the requested value");
                  controlIntent(el);if(el.readOnly)throw new Error("fill target became read-only");
                  setFormControlValue(el,value);el.dispatchEvent(new Event("input",{bubbles:true}));el.dispatchEvent(new Event("change",{bubbles:true}));
                  res.result="filled";break;
                }
              }
              if(res.result!=null&&res.result.length>1000000)throw new Error("Child result exceeds the 1000000-character limit; narrow the request");
            }catch(error){res.success=false;res.result=null;res.error=String(error&&error.message||error).slice(0,2048);}
            return res;
          }
          window.addEventListener("message",function(event){
            var d=event.data;
            if(!alive||event.source!==parentWindow||!d||d.type!=="tlbx-child-dom-v1"||! /^[a-f0-9]{32}$/.test(d.nonce||""))return;
            if(d.phase==="probe"){
              if(probes.has(d.nonce)||probes.size>=32)return;
              var timer=setTimeout(function(){probes.delete(d.nonce);},15000);probes.set(d.nonce,{timer:timer,phase:"probe"});
              send({type:d.type,phase:"ready",nonce:d.nonce,documentId:documentId});
            }else if(d.documentId===documentId&&probes.has(d.nonce)){
              var pending=probes.get(d.nonce);
              if(d.phase==="cancel"){clearTimeout(pending.timer);probes.delete(d.nonce);return;}
              if(d.phase!=="command"||pending.phase!=="probe")return;
              pending.phase="running";
              execute(d.command).then(function(result){
                if(probes.get(d.nonce)!==pending)return;
                clearTimeout(pending.timer);probes.delete(d.nonce);
                send({type:d.type,phase:"result",nonce:d.nonce,documentId:documentId,result:result});
              });
            }
          });
          window.addEventListener("pagehide",function(){alive=false;probes.forEach(function(p){clearTimeout(p.timer);});probes.clear();});
        })();
        """;

    private const string CooperativeParentBridge = """
        var childBootstrap=/*__MT_CHILD_BOOTSTRAP_JSON__*/null,childPending=new Set();
        function instrumentSrcdoc(html){
          if(typeof html!=="string"||html.indexOf('data-tlbx-child-dom="1"')>=0)return html;
          var head=/<head\b[^>]*>/i.exec(html);
          if(head)return html.slice(0,head.index+head[0].length)+childBootstrap+html.slice(head.index+head[0].length);
          var prefix=/^\s*<!doctype[^>]*>/i.exec(html),at=prefix?prefix[0].length:0;
          return html.slice(0,at)+childBootstrap+html.slice(at);
        }
        if(typeof HTMLIFrameElement!=="undefined"){
          var srcdocDescriptor=Object.getOwnPropertyDescriptor(HTMLIFrameElement.prototype,"srcdoc");
          if(srcdocDescriptor&&srcdocDescriptor.set)Object.defineProperty(HTMLIFrameElement.prototype,"srcdoc",{
            get:srcdocDescriptor.get,set:function(value){srcdocDescriptor.set.call(this,instrumentSrcdoc(value));},configurable:true,enumerable:srcdocDescriptor.enumerable
          });
          var previousSetAttribute=Element.prototype.setAttribute;
          Element.prototype.setAttribute=function(name,value){
            if(this.tagName==="IFRAME"&&String(name).toLowerCase()==="srcdoc")value=instrumentSrcdoc(value);
            return previousSetAttribute.call(this,name,value);
          };
        }
        function cancelChildCommands(){Array.from(childPending).forEach(function(cancel){cancel("Parent browser connection closed; child outcome may be uncertain. Do not replay a write.");});}
        function delegateChildCommand(msg){
          var socket=bws,finished=false,frame=null,observer=null,timer=null,documentId=null,nonce=null,childWindow=null;
          function finish(result){
            if(finished)return;finished=true;clearTimeout(timer);childPending.delete(cancel);
            if(!result.success&&documentId&&childWindow)try{childWindow.postMessage({type:"tlbx-child-dom-v1",phase:"cancel",nonce:nonce,documentId:documentId},"*");}catch(e){}
            window.removeEventListener("message",onMessage);window.removeEventListener("pagehide",onPageHide);
            if(frame)frame.removeEventListener("load",onLoad);if(observer)observer.disconnect();
            if(socket===bws&&socket&&socket.readyState===WebSocket.OPEN)socket.send(JSON.stringify({id:msg.id,success:result.success===true,result:result.result==null?null:result.result,error:result.error||null,matchCount:result.matchCount==null?null:result.matchCount,sessionId:mtCtx&&mtCtx.sessionId,previewId:mtCtx&&mtCtx.previewId}));
          }
          function cancel(reason){finish({success:false,error:reason});}
          function onLoad(){cancel("Child frame navigated; command outcome may be uncertain. Do not replay a write.");}
          function onPageHide(){cancel("Parent document closed; child outcome may be uncertain. Do not replay a write.");}
          function onMessage(event){
            var d=event.data;
            if(finished||event.source!==childWindow||!frame.isConnected||frame.contentWindow!==childWindow||!d||d.type!=="tlbx-child-dom-v1"||d.nonce!==nonce)return;
            if(d.phase==="ready"&&!documentId&&typeof d.documentId==="string"&&/^[a-f0-9]{32}$/.test(d.documentId)){
              documentId=d.documentId;
              try{childWindow.postMessage({type:d.type,phase:"command",nonce:nonce,documentId:documentId,command:{command:msg.command,selector:msg.selector,value:msg.value,maxDepth:msg.maxDepth,textOnly:msg.textOnly}},"*");}
              catch(error){cancel("Child command dispatch failed; inspect state before replaying a write.");}
            }else if(d.phase==="result"&&documentId&&d.documentId===documentId){
              var result=d.result;
              if(!result||typeof result.success!=="boolean"||result.result!=null&&(typeof result.result!=="string"||result.result.length>1000000)||result.error!=null&&(typeof result.error!=="string"||result.error.length>2048)||result.matchCount!=null&&(!Number.isInteger(result.matchCount)||result.matchCount<0)){
                cancel("Invalid cooperative child response");return;
              }
              finish(result);
            }
          }
          try{
            if(["query","exec","attrs","css","click","fill"].indexOf(msg.command)<0)throw new Error("This command does not support a cooperative frame target");
            if(childPending.size>=32)throw new Error("Too many pending cooperative child requests");
            if(typeof msg.frame!=="string"||!msg.frame.trim()||msg.frame.length>8192)throw new Error("frame must be a nonempty iframe selector of at most 8192 characters");
            var matches=previewQueryAll(msg.frame);
            if(matches.length!==1||matches[0].tagName!=="IFRAME")throw new Error("frame must match exactly one iframe; matched "+matches.length);
            frame=matches[0];childWindow=frame.contentWindow;
            if(!frame.isConnected||!childWindow)throw new Error("Child frame is detached or unavailable");
            if(msg.selector!=null&&(typeof msg.selector!=="string"||msg.selector.length>8192))throw new Error("Child selector exceeds the 8192-character limit");
            if(msg.value!=null&&(typeof msg.value!=="string"||msg.value.length>1000000))throw new Error("Child command value exceeds the 1000000-character limit");
            nonce=Array.from(crypto.getRandomValues(new Uint32Array(4)),function(n){return n.toString(16).padStart(8,"0");}).join("");
            childPending.add(cancel);frame.addEventListener("load",onLoad);window.addEventListener("message",onMessage);window.addEventListener("pagehide",onPageHide);
            observer=new MutationObserver(function(records){
              if(!frame.isConnected||records.some(function(r){return r.type==="attributes"&&r.target===frame;}))cancel("Child frame was removed or retargeted; outcome may be uncertain. Do not replay a write.");
            });
            observer.observe(document.documentElement,{subtree:true,childList:true,attributes:true,attributeFilter:["src","srcdoc","sandbox"]});
            timer=setTimeout(function(){cancel("Cooperative child deadline exceeded or no injected child bridge is available; outcome may be uncertain. Do not replay a write.");},Math.min(15000,Math.max(1000,(msg.timeout||15)*1000)));
            childWindow.postMessage({type:"tlbx-child-dom-v1",phase:"probe",nonce:nonce},"*");
          }catch(error){cancel(String(error&&error.message||error));}
        }
        """;

    private const string SharedQueryHelpers = """
          function previewSelectorParts(selector){
            var parts=[],start=0,quote="",brackets=0,parens=0,comment=false;
            for(var i=0;i<selector.length;i++){
              var c=selector[i],next=selector[i+1];
              if(comment){if(c==="*"&&next==="/"){comment=false;i++;}continue;}
              if(c==="\\"){i++;continue;}
              if(quote){if(c===quote)quote="";continue;}
              if(c==='"'||c==="'"){quote=c;continue;}
              if(c==="/"&&next==="*"){comment=true;i++;continue;}
              if(c==="[")brackets++;else if(c==="]")brackets--;
              else if(c==="(")parens++;else if(c===")")parens--;
              if(!brackets&&!parens&&selector.slice(i,i+3)===">>>"){
                parts.push(selector.slice(start,i).trim());start=i+3;i+=2;
              }
            }
            if(!parts.length)return [selector];
            parts.push(selector.slice(start).trim());
            if(quote||comment||brackets||parens||parts.some(function(part){return !part;}))throw new Error("invalid open-shadow selector: empty or unbalanced CSS segment");
            if(parts.length>32)throw new Error("open-shadow selector exceeds 32 CSS segments");
            return parts;
          }
          function previewOpenShadow(el){var root=el.shadowRoot;return root&&root.mode==="open"?root:null;}
          function previewQueryAll(selector,root){
            root=root||document;
            var parts=previewSelectorParts(selector);
            if(parts.length===1)return root.querySelectorAll(selector);
            // Validate even a later segment with no matching host. No selectors
            // or native query methods are rewritten globally.
            var validation=document.createDocumentFragment();
            parts.forEach(function(part){validation.querySelectorAll(part);});
            var roots=[root],matches=[],visited=0;
            for(var i=0;i<parts.length;i++){
              matches=[];var seen=new Set();
              for(var scope of roots){
                if(++visited>4000)throw new Error("open-shadow traversal limit exceeded; narrow the host selector");
                for(var el of scope.querySelectorAll(parts[i]))if(!seen.has(el)){seen.add(el);matches.push(el);}
              }
              if(i<parts.length-1)roots=matches.map(previewOpenShadow).filter(Boolean);
            }
            return matches;
          }
          function previewQueryOne(selector,root){return previewQueryAll(selector,root)[0]||null;}
          function previewParent(el){return el.assignedSlot||el.parentElement||(el.getRootNode&&el.getRootNode().host)||null;}
          function previewClosest(el,selector){
            for(var n=el;n;){var match=n.closest(selector);if(match)return match;var root=n.getRootNode&&n.getRootNode();n=root&&root.host;}
            return null;
          }
          function previewActiveElement(){
            var el=document.activeElement;
            var root;while(el&&(root=previewOpenShadow(el))&&root.activeElement)el=root.activeElement;
            return el;
          }
          function queryBudget(){return {nodes:4000,chars:40000,truncated:false};}
          function queryText(el,budget){
            var stack=[{node:el,index:0,shadow:false}],parts=[];
            while(stack.length){
              var frame=stack[stack.length-1];
              if(frame.index>=frame.node.childNodes.length){
                var shadow=previewOpenShadow(frame.node);
                if(!frame.shadow&&shadow){frame.shadow=true;stack.push({node:shadow,index:0,shadow:false});continue;}
                stack.pop();continue;
              }
              var node=frame.node.childNodes[frame.index++];
              if(budget.nodes--<=0||budget.chars<=0){budget.truncated=true;break;}
              if(node.nodeType===3){var text=node.data.slice(0,budget.chars);parts.push(text);budget.chars-=text.length;if(text.length<node.data.length){budget.truncated=true;break;}}
              else if(node.childNodes)stack.push({node:node,index:0,shadow:false});
            }
            return parts.join("");
          }
          function truncEl(el,mx,budget,full){
            // An inert document avoids invoking custom-element constructors or
            // fetching resources while producing a copy of the inspected DOM.
            var cloneDocument=document.implementation.createHTMLDocument("");
            function shallow(source){
              if(budget.nodes--<=0||budget.chars<32){budget.truncated=true;return null;}
              var copy=cloneDocument.importNode(source,false),attrs=Array.from(copy.attributes);
              for(var i=0;i<attrs.length;i++){
                if(!full&&i>=64){copy.removeAttribute(attrs[i].name);budget.truncated=true;}
                else if(!full&&attrs[i].value.length>2048){copy.setAttribute(attrs[i].name,attrs[i].value.slice(0,2048)+"[truncated]");budget.truncated=true;}
              }
              var length=copy.outerHTML.length;
              while(length>budget.chars&&copy.attributes.length){copy.removeAttribute(copy.attributes[copy.attributes.length-1].name);budget.truncated=true;length=copy.outerHTML.length;}
              if(length>budget.chars){budget.truncated=true;return null;}
              budget.chars-=length;return copy;
            }
            var root=shallow(el);if(!root)return "";
            var stack=[{source:el,copy:root,depth:0,index:0}];
            while(stack.length){
              var frame=stack[stack.length-1],source=frame.source.content&&frame.source.tagName==="TEMPLATE"?frame.source.content:frame.source;
              var parent=frame.copy.content&&frame.copy.tagName==="TEMPLATE"?frame.copy.content:frame.copy;
              var shadow=previewOpenShadow(frame.source);
              if(mx>0&&frame.depth>=mx){if(source.childNodes.length||shadow){budget.truncated=true;if(budget.chars>=14){parent.appendChild(cloneDocument.createComment(" ... "));budget.chars-=14;}}stack.pop();continue;}
              if(!frame.shadowDone){
                frame.shadowDone=true;
                if(shadow){
                  if(budget.nodes<=0||budget.chars<64){budget.truncated=true;break;}
                  var template=cloneDocument.createElement("template");template.setAttribute("shadowrootmode","open");
                  parent.appendChild(template);budget.nodes--;budget.chars-=48;
                  stack.push({source:shadow,copy:template,depth:frame.depth+1,index:0});continue;
                }
              }
              if(frame.index>=source.childNodes.length){stack.pop();continue;}
              if(budget.nodes<=0||budget.chars<32){budget.truncated=true;break;}
              var child=source.childNodes[frame.index++];
              if(child.nodeType===1){var copy=shallow(child);if(!copy)break;parent.appendChild(copy);stack.push({source:child,copy:copy,depth:frame.depth+1,index:0});}
              else if(child.nodeType===3||child.nodeType===8){
                budget.nodes--;var text=child.data.slice(0,Math.floor((budget.chars-10)/6));
                parent.appendChild(child.nodeType===3?cloneDocument.createTextNode(text):cloneDocument.createComment(text));budget.chars-=text.length*6+10;
                if(text.length<child.data.length){budget.truncated=true;break;}
              }
            }
            return root.outerHTML;
          }
          function previewSnapshot(){
            for(var el of document.querySelectorAll("*"))if(previewOpenShadow(el))return truncEl(document.documentElement,0,{nodes:Infinity,chars:Infinity,truncated:false},true);
            return document.documentElement.outerHTML;
          }
        """;

    private const string SharedControlHelpers = """
          function isControlVisible(el){
            if(!el.isConnected||!el.getClientRects().length)return false;
            var visibility=getComputedStyle(el).visibility;
            if(visibility==="hidden"||visibility==="collapse")return false;
            for(var n=el;n&&n.nodeType===1;n=previewParent(n)){
              var css=getComputedStyle(n);
              if(n.hidden||n.inert||css.display==="none"||Number(css.opacity)===0)return false;
            }
            return true;
          }
          function isCheckableControl(el){return !!el&&el.tagName==="INPUT"&&/^(checkbox|radio)$/.test(el.type);}
          function isCheckableRendered(el){
            if(!el.isConnected||!el.getClientRects().length)return false;
            var rect=el.getBoundingClientRect();
            if(rect.width<=0||rect.height<=0)return false;
            for(var n=el;n&&n.nodeType===1;n=previewParent(n)){
              var css=getComputedStyle(n);
              if(n.hidden||n.inert||css.display==="none"||css.visibility==="hidden"||css.visibility==="collapse"||n!==el&&Number(css.opacity)===0)return false;
            }
            return true;
          }
          function controlHitPoint(el,label){
            if(typeof document.elementFromPoint!=="function")return null;
            var rect=el.getBoundingClientRect(),x=rect.left+rect.width/2,y=rect.top+rect.height/2;
            var hitRoot=el.getRootNode?el.getRootNode():document;
            var hit=typeof hitRoot.elementFromPoint==="function"?hitRoot.elementFromPoint(x,y):document.elementFromPoint(x,y);
            if(hit!==el&&(!label||!el.contains(hit)))return null;
            // Clicking a link/button inside a label must not activate its input.
            if(label&&hit!==el&&hit.closest('a,button,input,select,textarea,[contenteditable="true"]'))return null;
            return {x:x,y:y};
          }
          function clickSurface(el){
            if(isCheckableControl(el)){
              if(!isCheckableRendered(el))return null;
              if(isControlVisible(el))return {target:el,control:el,point:null};
              var point=controlHitPoint(el,false);
              if(point)return {target:el,control:el,point:point};
              var labels=el.labels||[];
              for(var i=0;i<labels.length;i++){
                var label=labels[i];
                if(label.control!==el||!isControlVisible(label))continue;
                point=controlHitPoint(label,true);
                if(point)return {target:label,control:el,point:point};
              }
              return null;
            }
            if(!isControlVisible(el))return null;
            var label=el.tagName==="LABEL"?el:el.closest("label");
            var control=label&&isCheckableControl(label.control)?label.control:null;
            if(control&&el!==label&&el.closest('a,button,input,select,textarea,[contenteditable="true"]'))control=null;
            var point=control?controlHitPoint(el,true):null;
            if(control&&!point)return null;
            return {target:el,control:control,point:point};
          }
          function assertClickEnabled(el,surface){
            var current=clickSurface(el);
            if(!surface||!current||current.target!==surface.target||current.control!==surface.control)throw new Error("click target became hidden, detached or changed association");
            if(surface.target!==surface.control||isControlVisible(surface.target))assertControlEnabled(surface.target);
            var control=surface.control;
            if(control){
              if(!isCheckableRendered(control))throw new Error("checkbox/radio target is hidden or detached");
              if(control.matches(":disabled")||previewClosest(control,'[aria-disabled="true"]'))throw new Error("interaction target is disabled");
              if(control.readOnly||previewClosest(control,'[aria-readonly="true"],[readonly]')||previewClosest(surface.target,'[aria-readonly="true"],[readonly]'))throw new Error("interaction target is read-only");
            }
          }
          function resolveControl(selector,click){
            var matches=previewQueryAll(selector),visible=[];
            for(var i=0;i<matches.length;i++)if(click?clickSurface(matches[i]):isControlVisible(matches[i]))visible.push(matches[i]);
            if(!visible.length)throw new Error("no visible element: "+selector);
            if(visible.length!==1)throw new Error("ambiguous selector: "+selector+" ("+visible.length+" visible elements); use a more specific selector");
            if(click)assertClickEnabled(visible[0],clickSurface(visible[0]));else assertControlEnabled(visible[0]);
            return visible[0];
          }
          function assertControlEnabled(el){
            if(!isControlVisible(el))throw new Error("interaction target became hidden or detached");
            if(el.matches(":disabled")||previewClosest(el,'[aria-disabled="true"]'))throw new Error("interaction target is disabled");
          }
          function controlIntent(el,validate,point){
            // Match pointer activation without moving keyboard focus or claiming terminal size ownership.
            var rect=el.getBoundingClientRect(),opts={bubbles:true,cancelable:true,composed:true,button:0,buttons:1,clientX:point?point.x:rect.left+rect.width/2,clientY:point?point.y:rect.top+rect.height/2};
            var mouse=true;
            if(typeof PointerEvent!=="undefined")mouse=el.dispatchEvent(new PointerEvent("pointerdown",Object.assign({},opts,{pointerId:1,pointerType:"mouse",isPrimary:true})));
            if(mouse)el.dispatchEvent(new MouseEvent("mousedown",opts));
            opts.buttons=0;
            if(typeof PointerEvent!=="undefined")el.dispatchEvent(new PointerEvent("pointerup",Object.assign({},opts,{pointerId:1,pointerType:"mouse",isPrimary:true})));
            if(mouse)el.dispatchEvent(new MouseEvent("mouseup",opts));
            if(validate)validate();else assertControlEnabled(el);
          }
          function setFormControlValue(el,value){
            var proto=null;
            if(typeof HTMLTextAreaElement!=="undefined"&&el instanceof HTMLTextAreaElement)proto=HTMLTextAreaElement.prototype;
            else if(typeof HTMLSelectElement!=="undefined"&&el instanceof HTMLSelectElement)proto=HTMLSelectElement.prototype;
            else if(typeof HTMLInputElement!=="undefined"&&el instanceof HTMLInputElement)proto=HTMLInputElement.prototype;
            var nv=proto?Object.getOwnPropertyDescriptor(proto,"value"):null;
            if(nv&&nv.set)nv.set.call(el,value);
            else el.value=value;
          }
          function assertEditableHost(el){
            assertControlEnabled(el);
            if(!el.isContentEditable||el.parentElement&&el.parentElement.isContentEditable)throw new Error("fill requires a contenteditable editing host");
            if(previewClosest(el,'[aria-readonly="true"],[readonly],[disabled]'))throw new Error("fill target is read-only or disabled");
            if(el.querySelector('[contenteditable="false"]'))throw new Error("fill cannot replace non-editable editor content");
          }
          function fillEditableHost(el,value){
            assertEditableHost(el);
            var command=value===""?"delete":"insertText";
            if(typeof document.execCommand!=="function"||typeof document.queryCommandSupported!=="function"||!document.queryCommandSupported(command))throw new Error("native text editing is unavailable; rich-editor fill was not attempted");
            controlIntent(el);
            el.focus({preventScroll:true});
            assertEditableHost(el);
            if(previewActiveElement()!==el)throw new Error("fill target could not receive focus");
            var selection=window.getSelection(),range=document.createRange();
            if(!selection)throw new Error("editor selection is unavailable");
            range.selectNodeContents(el);
            selection.removeAllRanges();selection.addRange(range);
            // Use the browser editing operation: literal text, native input/selection and undo.
            // Do not write textContent/innerHTML or synthesize a second input event: model-backed
            // editors observe this edit and reconcile it through their normal input pipeline.
            if(!document.execCommand(command,false,value))throw new Error("native editor replacement was rejected");
          }
        """;
}

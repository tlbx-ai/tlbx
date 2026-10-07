import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { chromium, request } = require('../../docs/marketing/ScreenshotAutomation/node_modules/playwright');
const url = process.env.TLBX_PERF_URL;
const out = path.resolve(process.env.TLBX_PERF_ARTIFACT_ROOT || '.dev/maintenance/browser');
if (!url || !process.env.TLBX_COOKIE_HEADER) throw Error('TLBX_PERF_URL and TLBX_COOKIE_HEADER are required.');
fs.mkdirSync(out, { recursive: true });
const profile = path.join(out, 'chrome-profile');
fs.mkdirSync(profile, { recursive: true });
const chrome = spawn(process.env.TLBX_CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe', [
  '--remote-debugging-port=0', '--user-data-dir=' + profile, '--ignore-certificate-errors',
  '--no-first-run', '--no-default-browser-check', '--window-size=1440,1060', '--disable-background-timer-throttling',
], { windowsHide: false, stdio: ['ignore', fs.openSync(path.join(out, 'chrome.stdout.log'), 'w'), fs.openSync(path.join(out, 'chrome.stderr.log'), 'w')] });
const sleep = ms => new Promise(r => setTimeout(r, ms));
const summary = { url, ok: false, sessions: [], transitions: [], errors: [] };
let browser, context, page, apiRequest, rawSocket, rawEndpoint;
const rawPending = new Map();
let rawRequestId = 0;
async function rawCall(method, params = {}) {
  const id = ++rawRequestId;
  const result = new Promise((resolve, reject) => {
    const timeout = setTimeout(() => { rawPending.delete(id); reject(Error(method + ' timed out')); }, 10000);
    rawPending.set(id, { resolve, reject, timeout });
  });
  rawSocket.send(JSON.stringify({ id, method, params }));
  return result;
}
async function connectRaw() {
  rawSocket = new WebSocket(rawEndpoint);
  await new Promise((resolve, reject) => {
    rawSocket.addEventListener('open', resolve, { once: true });
    rawSocket.addEventListener('error', reject, { once: true });
  });
  rawSocket.addEventListener('message', event => {
    const response = JSON.parse(event.data);
    const pending = rawPending.get(response.id);
    if (!pending) return;
    rawPending.delete(response.id);
    clearTimeout(pending.timeout);
    response.error ? pending.reject(Error(JSON.stringify(response.error))) : pending.resolve(response.result);
  });
}
async function api(method, route, data) {
  const response = await apiRequest.fetch(url + route, { method, data, ignoreHTTPSErrors: true });
  if (!response.ok()) throw Error(route + ': ' + response.status() + ' ' + await response.text());
  const text = await response.text();
  return text ? (response.headers()['content-type']?.includes('json') ? JSON.parse(text) : text) : null;
}
async function select(id) {
  await page.locator('.session-item[data-session-id="' + id + '"]').click({ position: { x: 32, y: 12 } });
  await page.waitForFunction(id => window.mmDebug.activeId === id, id);
}
async function sample(stage, expected) {
  await page.waitForFunction(ids => ids.every(id => {
    const s = window.mmDebug.terminals.get(id);
    const rect = s?.container.getBoundingClientRect();
    return s?.opened && rect?.width > 0 && rect?.height > 0 && getComputedStyle(s.container).visibility !== 'hidden';
  }), expected, { timeout: 10000 });
  await sleep(150);
  const state = await page.evaluate(({ stage, ids }) => ({ stage, visibility: document.visibilityState,
    active: window.mmDebug.activeId, layout: window.mmDebug.layout.sessions, rootVisible: window.mmDebug.layout.rootVisible,
    panes: ids.map(id => { const s=window.mmDebug.terminals.get(id), rect=s.container.getBoundingClientRect(), screen=s.container.querySelector('.xterm-screen').getBoundingClientRect();
      return {id, rows:s.terminal.rows, cols:s.terminal.cols, height:rect.height, width:rect.width, overflow:screen.bottom-rect.bottom}; })
  }), { stage, ids: expected });
  if (state.panes.some(p => p.overflow > 2)) throw Error('Terminal overflow at ' + stage + ': ' + JSON.stringify(state));
  summary.transitions.push(state);
}
try {
  for (let i=0;i<100;i++) {
    try { const [portText, suffix]=fs.readFileSync(path.join(profile,'DevToolsActivePort'),'utf8').trim().split('\n');const port=Number(portText);rawEndpoint='ws://127.0.0.1:'+port+suffix;browser=await chromium.connectOverCDP('http://127.0.0.1:'+port, { noDefaults: true });break; } catch {await sleep(100);}
  }
  if (!browser) throw Error('Chrome did not start');
  apiRequest=await request.newContext({ignoreHTTPSErrors:true,extraHTTPHeaders:{Cookie:process.env.TLBX_COOKIE_HEADER}});
  summary.stayActiveInBackground=(await api('GET','/api/settings')).stayActiveInBackground === true;
  context=browser.contexts()[0];
  const cookie=process.env.TLBX_COOKIE_HEADER, idx=cookie.indexOf('=');
  await context.addCookies([{name:cookie.slice(0,idx),value:cookie.slice(idx+1),url}]);
  page=await context.newPage();
  page.on('pageerror',e=>summary.errors.push(e.message));
  await page.goto(url);
  await page.bringToFront();
  await page.waitForFunction(()=>document.visibilityState==='visible',null,{polling:100});
  await page.waitForFunction(()=>window.mmDebug?.terminals);
  const pageCdp = await context.newCDPSession(page);
  await pageCdp.send('Emulation.setFocusEmulationEnabled', { enabled: false });
  for(let i=0;i<3;i++) {
    const s=await api('POST','/api/sessions',{shell:'Pwsh',cols:100,rows:30,workingDirectory:process.env.TLBX_WORKING_DIRECTORY || 'Q:\\repos\\Jpa',launchRequestId:crypto.randomUUID()});
    summary.sessions.push(s.id);
  }
  const [a,b,c]=summary.sessions;
  await page.waitForFunction(ids=>ids.every(id=>document.querySelector('.session-item[data-session-id="'+id+'"]')),summary.sessions);
  await select(a);
  await page.evaluate(([a,b])=>window.mmDebug.layout.dock(a,b,'right'),[a,b]);
  await sample('initial-layout',[a,b]);
  for (let i=0;i<8;i++) {
    await select(c);await sample('standalone-'+i,[c]);
    await select(a);await sample('layout-return-'+i,[a,b]);
  }
  await page.screenshot({path:path.join(out,'layout-return.png')});
  await page.evaluate(id=>{
    const t=window.mmDebug.terminals.get(id).terminal;
    window.lifecycleProbe={parsed:0,painted:0};
    t.onWriteParsed(()=>window.lifecycleProbe.parsed++);
    t.onRender(()=>window.lifecycleProbe.painted++);
  },a);
  await api('POST','/api/sessions/'+a+'/input/text',{text:'$heartbeat=0; while($true){[Console]::WriteLine("LIFECYCLE-HEARTBEAT " + ($heartbeat++)); Start-Sleep -Milliseconds 100}',appendNewline:true});
  await sleep(700);
  // A page debugger forces Chromium to stay visible. Record native events while
  // detached; browser-level Target activation itself does not attach an inspector.
  const targetInfo = await pageCdp.send('Target.getTargetInfo');
  await page.evaluate(() => {
    window.lifecycleSnapshots=[];
    document.addEventListener('visibilitychange', () => {
      const snapshot = kind => ({kind, visibility:document.visibilityState,hidden:document.hidden,...window.lifecycleProbe});
      if(document.hidden) {
        window.lifecycleSnapshots.push(snapshot('hidden-start'));
        setTimeout(()=>window.lifecycleSnapshots.push(snapshot('hidden-output')),2000);
      } else {
        window.lifecycleSnapshots.push(snapshot('restored'));
      }
    });
  });
  await connectRaw();
  // For an externally launched CDP browser, close disconnects Playwright; it
  // keeps the native browser running. Finally explicitly sends Browser.close.
  await browser.close();
  browser=null;page=null;
  const blank=await rawCall('Target.createTarget',{url:'about:blank',background:false,newWindow:false});
  await rawCall('Target.activateTarget',{targetId:blank.targetId});
  summary.hiddenMethod='native-background-tab-with-page-debugger-detached';
  await sleep(3500);
  await rawCall('Target.activateTarget',{targetId:targetInfo.targetInfo.targetId});
  await sleep(700);
  const endpoint = rawEndpoint.replace('ws://','http://').replace(/\/devtools\/browser\/.+$/,'');
  browser=await chromium.connectOverCDP(endpoint,{noDefaults:true});
  context=browser.contexts()[0];
  page=context.pages().find(candidate=>candidate.url().startsWith(url));
  if(!page) throw Error('Original source page missing after debugger reattachment');
  page.on('pageerror',e=>summary.errors.push(e.message));
  await page.waitForFunction(()=>document.visibilityState==='visible');
  summary.visibilityEvents=await page.evaluate(()=>window.lifecycleSnapshots);
  summary.hiddenBefore=summary.visibilityEvents.find(sample=>sample.kind==='hidden-start'&&sample.hidden===true);
  summary.hiddenAfter=summary.visibilityEvents.find(sample=>sample.kind==='hidden-output'&&sample.hidden===true);
  if(!summary.hiddenBefore||!summary.hiddenAfter) throw Error('No real document.hidden=true output interval was observed');
  if(summary.stayActiveInBackground && summary.hiddenAfter.parsed <= summary.hiddenBefore.parsed) throw Error('Stay-active hidden tab stopped receiving output');
  await sleep(700);
  summary.restoredBefore=await page.evaluate(()=>({...window.lifecycleProbe}));
  await page.locator('#terminal-'+a).screenshot({path:path.join(out,'restored-before.png')});
  await sleep(700);
  summary.restoredAfter=await page.evaluate(()=>({...window.lifecycleProbe}));
  await page.locator('#terminal-'+a).screenshot({path:path.join(out,'restored-after.png')});
  if(summary.restoredAfter.painted <= summary.restoredBefore.painted) throw Error('Restored terminal stopped painting');
  if(summary.restoredAfter.parsed <= summary.restoredBefore.parsed) throw Error('Restored terminal stopped parsing output');
  await rawCall('Target.closeTarget',{targetId:blank.targetId});
  summary.ok=summary.errors.length===0;
  if(!summary.ok) throw Error(summary.errors.join('\n'));
} catch(error) {
  summary.errors.push(error.stack || String(error));process.exitCode=1;
  if(page) summary.failureVisibility=await page.evaluate(()=>document.visibilityState).catch(()=>null);
  if(page) await page.screenshot({path:path.join(out,'failure.png')}).catch(()=>{});
} finally {
  for(const id of summary.sessions) await api('DELETE','/api/sessions/'+id).catch(e=>summary.errors.push(e.message));
  if(apiRequest) {
    try {
      const remaining=await api('GET','/api/sessions');
      summary.cleanupVerified=!remaining.sessions.some(session=>summary.sessions.includes(session.id));
      if(!summary.cleanupVerified) summary.errors.push('Test-owned sessions remain after cleanup');
    } catch(error) { summary.errors.push('Cleanup verification failed: '+error.message); }
  }
  if(summary.errors.length) { summary.ok=false; process.exitCode=1; }
  fs.writeFileSync(path.join(out,'summary.json'),JSON.stringify(summary,null,2));
  if(rawSocket?.readyState===WebSocket.OPEN){await rawCall('Browser.close').catch(()=>{});rawSocket.close();}
  else if(browser){const cdp=await browser.newBrowserCDPSession();await cdp.send('Browser.close').catch(()=>{});}
  else chrome.kill();
  await apiRequest?.dispose();
}
console.log(JSON.stringify({ok:summary.ok,transitions:summary.transitions.length,errors:summary.errors,summaryPath:path.join(out,'summary.json')}));

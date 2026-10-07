import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { chromium } = require('../../docs/marketing/ScreenshotAutomation/node_modules/playwright');
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
let browser, context, page;
async function api(method, route, data) {
  const response = await context.request.fetch(url + route, { method, data, ignoreHTTPSErrors: true });
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
    try { const port=Number(fs.readFileSync(path.join(profile,'DevToolsActivePort'),'utf8').split('\n')[0]);browser=await chromium.connectOverCDP('http://127.0.0.1:'+port, { noDefaults: true });break; } catch {await sleep(100);}
  }
  if (!browser) throw Error('Chrome did not start');
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
  const [blank] = await Promise.all([
    context.waitForEvent('page'),
    page.evaluate(() => { window.open('about:blank', '_blank'); }),
  ]);
  await blank.bringToFront();
  summary.hiddenMethod='background-tab';
  await page.waitForFunction(()=>document.visibilityState==='hidden',null,{timeout:5000,polling:100});
  summary.hiddenBefore=await page.evaluate(()=>({visibility:document.visibilityState,...window.lifecycleProbe}));
  await sleep(2500);
  summary.hiddenAfter=await page.evaluate(()=>({visibility:document.visibilityState,...window.lifecycleProbe}));
  if(summary.hiddenAfter.parsed <= summary.hiddenBefore.parsed) throw Error('Hidden tab stopped receiving output');
  await page.bringToFront();
  await page.waitForFunction(()=>document.visibilityState==='visible');
  await sleep(700);
  summary.restoredBefore=await page.evaluate(()=>({...window.lifecycleProbe}));
  await page.locator('#terminal-'+a).screenshot({path:path.join(out,'restored-before.png')});
  await sleep(700);
  summary.restoredAfter=await page.evaluate(()=>({...window.lifecycleProbe}));
  await page.locator('#terminal-'+a).screenshot({path:path.join(out,'restored-after.png')});
  if(summary.restoredAfter.painted <= summary.restoredBefore.painted) throw Error('Restored terminal stopped painting');
  await blank.close();
  summary.ok=summary.errors.length===0;
  if(!summary.ok) throw Error(summary.errors.join('\n'));
} catch(error) {
  summary.errors.push(error.stack || String(error));process.exitCode=1;
  if(page) summary.failureVisibility=await page.evaluate(()=>document.visibilityState).catch(()=>null);
  if(page) await page.screenshot({path:path.join(out,'failure.png')}).catch(()=>{});
} finally {
  for(const id of summary.sessions) await api('DELETE','/api/sessions/'+id).catch(e=>summary.errors.push(e.message));
  fs.writeFileSync(path.join(out,'summary.json'),JSON.stringify(summary,null,2));
  if(browser){const cdp=await browser.newBrowserCDPSession();await cdp.send('Browser.close').catch(()=>{});}else chrome.kill();
}
console.log(JSON.stringify({ok:summary.ok,transitions:summary.transitions.length,errors:summary.errors,summaryPath:path.join(out,'summary.json')}));

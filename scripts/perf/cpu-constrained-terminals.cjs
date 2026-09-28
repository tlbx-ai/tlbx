// Real four-pane workload. Run through run-cpu-constrained.ps1 on an isolated source instance.
const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { chromium } = require('../../docs/marketing/ScreenshotAutomation/node_modules/playwright');
const out = path.resolve(process.env.TLBX_PERF_OUT);
const url = process.env.TLBX_PERF_URL;
const rates = (process.env.TLBX_PERF_RATES || '1,4,8,16,32').split(',').map(Number);
const repetitions = Number(process.env.TLBX_PERF_REPETITIONS || 1);
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
fs.mkdirSync(out, { recursive: true });
const summary = { url, rates, stages: [], errors: [], created: [], cleaned: [] };
const chrome = spawn('C:/Program Files/Google/Chrome/Application/chrome.exe', [
  '--headless=new', '--remote-debugging-port=9339', `--user-data-dir=${path.join(out, 'chrome')}`,
  '--ignore-certificate-errors', '--no-first-run', '--no-default-browser-check',
], { windowsHide: true, stdio: ['ignore', fs.openSync(path.join(out, 'chrome.stdout.log'), 'w'), fs.openSync(path.join(out, 'chrome.stderr.log'), 'w')] });
let browser, context, page, cdp;
const api = async (method, route, data) => {
  const r = await context.request.fetch(url + route, { method, data, timeout: 15000 });
  if (!r.ok()) throw Error(`${method} ${route}: ${r.status()} ${await r.text()}`);
  const text = await r.text(); return text ? (r.headers()['content-type']?.includes('json') ? JSON.parse(text) : text) : null;
};
const quantiles = values => {
  const v = values.filter(Number.isFinite).sort((a,b) => a-b);
  return { n: v.length, p50: v[Math.floor(v.length*.5)], p95: v[Math.min(v.length-1, Math.floor(v.length*.95))], max: v.at(-1) };
};
(async () => {
  try {
    for (let i=0; i<100; i++) { try { browser = await chromium.connectOverCDP('http://127.0.0.1:9339'); break; } catch { await sleep(100); } }
    if (!browser) throw Error('Chrome did not start');
    context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1800, height: 1100 } });
    await context.addCookies((process.env.TLBX_COOKIE_HEADER || '').split(';').filter(Boolean).map(p => { const i=p.indexOf('='); return { name:p.slice(0,i).trim(), value:p.slice(i+1).trim(), url }; }));
    page = await context.newPage();
    page.on('pageerror', e => summary.errors.push(e.message));
    await page.addInitScript(() => {
      window.__probe = { keys: [], longTasks: [], expected: '', stage: '' };
      const WS = window.WebSocket;
      window.WebSocket = class extends WS {
        constructor(...args) {
          super(...args);
          if (String(args[0]).includes('/ws/mux')) this.addEventListener('message', e => {
            if (!(e.data instanceof ArrayBuffer)) return;
            const bytes = new Uint8Array(e.data);
            if (bytes[0] !== 1 && bytes[0] !== 7) return;
            const id = new TextDecoder().decode(bytes.subarray(1,9));
            if (id !== window.__probe.echoId) return;
            // Raw echo is uncompressed. Match echoed prefixes, not unrelated
            // cursor/title traffic or an earlier character's first output.
            if (bytes[0] !== 1) return;
            window.__probe.receivedText = (window.__probe.receivedText || '') + new TextDecoder().decode(bytes.subarray(21)).replace(/\x1b\[[0-?]*[ -/]*[@-~]/g,'').replace(/[\r\n]/g,'');
            const now = performance.now();
            for (const k of window.__probe.keys) if (k.received === undefined && window.__probe.receivedText.includes(k.prefix)) k.received = now;
          });
        }
        send(data) {
          if (ArrayBuffer.isView(data) && data[0] === 0x14) {
            const now = performance.now();
            for (const k of window.__probe.keys) if (k.sent === undefined) k.sent = now;
          }
          return super.send(data);
        }
      };
      new PerformanceObserver(list => { for (const e of list.getEntries()) window.__probe.longTasks.push({ at:e.startTime, ms:e.duration, stage:window.__probe.stage }); }).observe({type:'longtask',buffered:true});
      document.addEventListener('keydown', e => {
        if (!window.__probe.stage || e.key.length !== 1) return;
        window.__probe.expected += e.key;
        window.__probe.keys.push({ key:e.key, prefix:window.__probe.expected, at:performance.now(), stage:window.__probe.stage });
      }, true);
    });
    await page.goto(url); await page.waitForFunction(() => window.mmDebug?.terminals);
    summary.version = await api('GET', '/api/version');
    const preexisting = await api('GET', '/api/sessions');
    if (preexisting.sessions.length) throw Error('Test requires an empty isolated source instance');
    for (const name of ['Echo latency', 'btop A', 'btop B', 'High output']) {
      const s = await api('POST', '/api/sessions', {shell:'Pwsh', cols:100, rows:30, workingDirectory:process.cwd()});
      summary.created.push(s.id); await api('PUT', `/api/sessions/${s.id}/name`, {name});
    }
    const [echo, topA, topB, flood] = summary.created;
    await page.waitForFunction(ids => ids.every(id => document.querySelector(`.session-item[data-session-id="${id}"]`)), summary.created);
    await page.evaluate(([echo,a,b,flood]) => {
      document.querySelector(`.session-item[data-session-id="${echo}"]`).click();
      window.mmDebug.layout.dock(echo,a,'right'); window.mmDebug.layout.dock(echo,b,'bottom'); window.mmDebug.layout.dock(a,flood,'bottom');
      window.mmDebug.layout.focus(echo);
    }, summary.created);
    await page.waitForFunction(ids => ids.every(id => {
      const t=window.mmDebug.terminals.get(id)?.terminal;
      if (!t) return false;
      const text=Array.from({length:t.buffer.active.length},(_,i)=>t.buffer.active.getLine(i)?.translateToString(true)||'').join('\n');
      return text.includes('PS ') && text.includes('>');
    }), summary.created, {timeout:60000});
    const input = (id,text) => api('POST', `/api/sessions/${id}/input/text`, {text,appendNewline:true});
    for (const id of [topA,topB]) await input(id, 'btop');
    await input(echo, `python -u -c "import msvcrt,sys; print('RAWREADY',flush=True); exec('while True:\\n c=msvcrt.getwch()\\n if c == chr(3): break\\n sys.stdout.write(c); sys.stdout.flush()')"`);
    await input(flood, `python -u -c "import sys,time; line='0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ'*3; end=time.monotonic()+600; exec('while time.monotonic()<end:\\n sys.stdout.write((line+chr(13)+chr(10))*100); sys.stdout.flush(); time.sleep(0.01)')"`);
    await page.waitForFunction(id => {
      const t=window.mmDebug.terminals.get(id)?.terminal;
      return t && Array.from({length:t.buffer.active.length},(_,i)=>t.buffer.active.getLine(i)?.translateToString(true)).join('\n').includes('RAWREADY');
    }, echo);
    await sleep(4000);
    for (let attempt=0; attempt<30; attempt++) {
      summary.workload = await api('GET','/api/sessions');
      if (summary.workload.sessions.filter(s => /btop/i.test(s.foregroundName || '')).length === 2) break;
      await sleep(1000);
    }
    if (summary.workload.sessions.filter(s => /btop/i.test(s.foregroundName || '')).length !== 2) throw Error('Two real btop processes were not confirmed');
    if (process.env.TLBX_PERF_SERVER_CONTENTION === '1') {
      fs.writeFileSync(path.join(out,'workload-ready'),String(Date.now()));
      const deadline = Date.now()+30000;
      while (!fs.existsSync(path.join(out,'contention-applied'))) {
        if (Date.now()>deadline) throw Error('Server contention controller did not apply the constraint');
        await sleep(100);
      }
    }
    await page.evaluate(id => {
      window.__probe.echoId = id;
      const t=window.mmDebug.terminals.get(id).terminal;
      const text = () => Array.from({length:t.rows+2},(_,i)=>t.buffer.active.getLine(Math.max(0,t.buffer.active.baseY-1)+i)?.translateToString(true)||'').join('');
      const mark = phase => {
        const content=text(), now=performance.now();
        for (const k of window.__probe.keys) if (k[phase]===undefined && content.includes(k.prefix)) k[phase]=now;
      };
      t.onWriteParsed(()=>mark('parsed')); t.onRender(()=>{mark('render'); requestAnimationFrame(()=>mark('frame'));});
      window.mmDebug.layout.focus(id); t.focus();
    }, echo);
    cdp=await context.newCDPSession(page);
    await cdp.send('Profiler.enable');
    for (const rate of rates) {
      console.log(`Starting ${rate}x CPU throttle`);
      await cdp.send('Emulation.setCPUThrottlingRate', {rate});
      await sleep(2000);
      await page.evaluate(({id,rate}) => { window.__probe.stage=String(rate); const t=window.mmDebug.terminals.get(id).terminal; t.focus(); },{id:echo,rate});
      await cdp.send('Profiler.start');
      const start=Date.now();
      for (const key of 'abcdefghijklmnopqrstuvwx'.repeat(repetitions)) { await page.keyboard.press(key); await sleep(80); }
      await sleep(2000);
      const profile=(await cdp.send('Profiler.stop')).profile;
      fs.writeFileSync(path.join(out,`cpu-${rate}.json`),JSON.stringify(profile));
      const stage=await page.evaluate(({ids,rate}) => ({rate, keys:window.__probe.keys.filter(k=>k.stage===String(rate)), longTasks:window.__probe.longTasks.filter(t=>t.stage===String(rate)), terminals:ids.map(id=>{
        const s=window.mmDebug.terminals.get(id), r=s.container.getBoundingClientRect();
        return {id,width:r.width,height:r.height,hidden:s.container.classList.contains('hidden'),transport:window.mmDebug.transport(id)};
      })}),{ids:summary.created,rate});
      stage.wallMs=Date.now()-start;
      for (const phase of ['sent','received','parsed','render','frame']) stage[phase]=quantiles(stage.keys.map(k=>k[phase]===undefined?NaN:k[phase]-k.at));
      stage.missing=stage.keys.filter(k=>k.render===undefined).length;
      summary.stages.push(stage);
      fs.writeFileSync(path.join(out,'summary.json'),JSON.stringify(summary,(_,v)=>typeof v==='bigint'?String(v):v,2));
      console.log(JSON.stringify({rate,render:stage.render,missing:stage.missing,longTasks:quantiles(stage.longTasks.map(t=>t.ms))}));
      await cdp.send('Emulation.setCPUThrottlingRate',{rate:1});
      await page.screenshot({path:path.join(out,`panes-${rate}.png`),timeout:15000});
      await page.waitForFunction(() => window.__probe.keys.every(k=>k.render!==undefined), null, {timeout:15000});
      stage.recovered = await page.evaluate(() => window.__probe.keys.every(k=>k.render!==undefined));
    }
  } catch(e) { summary.failure=e.stack; process.exitCode=1; console.error(e.stack); }
  finally {
    if(cdp) await cdp.send('Emulation.setCPUThrottlingRate',{rate:1}).catch(()=>{});
    for(const id of summary.created) try { await api('DELETE',`/api/sessions/${id}`); summary.cleaned.push(id); } catch(e) { summary.errors.push(e.message); }
    if(browser) await browser.close().catch(()=>{});
    chrome.kill();
    fs.writeFileSync(path.join(out,'summary.json'),JSON.stringify(summary,(_,v)=>typeof v==='bigint'?String(v):v,2));
    console.log(path.join(out,'summary.json'));
  }
})();


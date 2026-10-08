import fs from 'node:fs';
import path from 'node:path';
import { spawn } from 'node:child_process';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { chromium } = require('../../docs/marketing/ScreenshotAutomation/node_modules/playwright');
const url = process.env.TLBX_PERF_URL;
const out = path.resolve(process.env.TLBX_PERF_ARTIFACT_ROOT || '.dev/multi-browser');
if (!url || !process.env.TLBX_COOKIE_HEADER) throw Error('Authenticated source URL and cookie required');
fs.mkdirSync(out, { recursive: true });
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
const summary = { ok: false, samples: [], errors: [] };
const browsers = [], processes = [], pages = [];
let context, id, settings;
async function api(method, route, data) {
  const response = await context.request.fetch(url + route, { method, data, ignoreHTTPSErrors: true });
  if (!response.ok()) throw Error(route + ': ' + response.status() + ' ' + await response.text());
  const body = await response.text();
  return body ? JSON.parse(body) : null;
}
async function sample(stage) {
  const states = await Promise.all(pages.map(page => page.evaluate(id => {
    const state = window.mmDebug.terminals.get(id), t = state.terminal;
    return { ...window.multiBrowserProbe, active: window.mmDebug.activeId, hidden: document.hidden,
      first: t.buffer.active.getLine(t.buffer.active.viewportY)?.translateToString(true),
      paused: t._core._renderService._isPaused, transport: window.mmDebug.transport?.(id) };
  }, id)));
  summary.samples.push({ stage, states });
  return states;
}
try {
  for (let i = 0; i < 2; i++) {
    const profile = path.join(out, 'chrome-' + i);
    fs.mkdirSync(profile, { recursive: true });
    const chrome = spawn('C:/Program Files/Google/Chrome/Application/chrome.exe', [
      '--headless=new', '--remote-debugging-port=0', '--user-data-dir=' + profile,
      '--ignore-certificate-errors', '--no-first-run', '--no-default-browser-check',
    ], { windowsHide: true, stdio: ['ignore', fs.openSync(path.join(out, i + '.stdout.log'), 'w'), fs.openSync(path.join(out, i + '.stderr.log'), 'w')] });
    processes.push(chrome);
    let browser;
    for (let attempt = 0; attempt < 100; attempt++) {
      try {
        const port = Number(fs.readFileSync(path.join(profile, 'DevToolsActivePort'), 'utf8').split('\n')[0]);
        browser = await chromium.connectOverCDP('http://127.0.0.1:' + port);
        break;
      } catch { await sleep(100); }
    }
    if (!browser) throw Error('Chrome did not start');
    browsers.push(browser);
    const ctx = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1400 - i * 350, height: 900 - i * 150 } });
    context ??= ctx;
    const cookie = process.env.TLBX_COOKIE_HEADER, eq = cookie.indexOf('=');
    await ctx.addCookies([{ name: cookie.slice(0, eq), value: cookie.slice(eq + 1), url }]);
    const page = await ctx.newPage();
    page.on('pageerror', e => summary.errors.push(e.message));
    pages.push(page);
    await page.goto(url);
    await page.waitForFunction(() => window.mmDebug?.terminals);
  }
  settings = await api('GET', '/api/settings');
  id = (await api('POST', '/api/sessions', { shell: 'Pwsh', cols: 100, rows: 30,
    workingDirectory: process.env.TLBX_WORKING_DIRECTORY || 'Q:\\repos\\Jpa', launchRequestId: crypto.randomUUID() })).id;
  summary.sessionId = id;
  for (const page of pages) {
    await page.locator('.session-item[data-session-id="' + id + '"]').waitFor();
    await page.locator('.session-item[data-session-id="' + id + '"]').click({ position: { x: 40, y: 12 } });
    await page.waitForFunction(id => {
      const s = window.mmDebug.terminals.get(id);
      return window.mmDebug.activeId === id && s?.opened && s.container.getBoundingClientRect().height > 0;
    }, id);
    await page.evaluate(id => {
      const t = window.mmDebug.terminals.get(id).terminal;
      window.multiBrowserProbe = { parsed: 0, painted: 0 };
      t.onWriteParsed(() => window.multiBrowserProbe.parsed++);
      t.onRender(() => window.multiBrowserProbe.painted++);
    }, id);
  }
  await api('POST', '/api/sessions/' + id + '/input/text', {
    text: '$n=0; while($true){ [Console]::Write(("MULTI-BROWSER FRAME " + ($n++) + " " + ("X" * 60) + "`r`n") * 50); Start-Sleep -Milliseconds 25 }', appendNewline: true,
  });
  await sleep(1500);
  const before = await sample('before-browser-stall');
  const cdp = await pages[1].context().newCDPSession(pages[1]);
  await cdp.send('Emulation.setCPUThrottlingRate', { rate: 8 });
  await sleep(1500);
  // Stop only one browser's JS while the same real PTY keeps emitting to both.
  await cdp.send('Debugger.enable');
  await cdp.send('Debugger.pause');
  await sleep(2500);
  await cdp.send('Debugger.resume');
  await cdp.send('Debugger.disable');
  await sleep(2500);
  const after = await sample('after-browser-stall');
  if (after.some((s, i) => s.parsed <= before[i].parsed || s.painted <= before[i].painted || s.active !== id || s.paused))
    throw Error('Output did not recover in both browsers without changing sessions');
  await pages[1].locator('#terminal-' + id).screenshot({ path: path.join(out, 'follower-before.png') });
  await sleep(1000);
  const final = await sample('continued-output');
  await pages[1].locator('#terminal-' + id).screenshot({ path: path.join(out, 'follower-after.png') });
  if (final.some((s, i) => s.painted <= after[i].painted || s.first === after[i].first)) throw Error('Terminal pixels stopped progressing');
  await cdp.send('Emulation.setCPUThrottlingRate', { rate: 1 });
  // Drive the actual setting controls and inspect the persisted server value.
  const page = pages[0];
  await page.locator('#btn-settings').click();
  await page.locator('.settings-tab[data-tab="sessions"]').click();
  await page.locator('#setting-runtime-priority-boost').check();
  await page.locator('#setting-runtime-priority-class').selectOption('aboveNormal');
  await sleep(500);
  const saved = await api('GET', '/api/settings');
  if (saved.runtimePriorityClass !== 'aboveNormal' || !saved.runtimePriorityBoostEnabled) throw Error('Priority setting did not save');
  summary.ok = summary.errors.length === 0;
} catch (e) {
  summary.errors.push(e.stack);
  process.exitCode = 1;
} finally {
  if (id && context) await api('DELETE', '/api/sessions/' + id).catch(e => summary.errors.push(e.message));
  if (settings && context) await api('PATCH', '/api/settings', { runtimePriorityBoostEnabled: settings.runtimePriorityBoostEnabled,
    runtimePriorityClass: settings.runtimePriorityClass }).catch(e => summary.errors.push(e.message));
  for (const browser of browsers) {
    const cdp = await browser.newBrowserCDPSession();
    await cdp.send('Browser.close').catch(() => {});
  }
  for (const chrome of processes) if (chrome.exitCode === null) chrome.kill();
  fs.writeFileSync(path.join(out, 'summary.json'), JSON.stringify(summary, (_, value) => typeof value === 'bigint' ? value.toString() : value, 2));
}
console.log(JSON.stringify({ ok: summary.ok, errors: summary.errors, artifactRoot: out }));

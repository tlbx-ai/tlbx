import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import os from 'node:os';
import { spawn, execFileSync } from 'node:child_process';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { chromium } = require('../../docs/marketing/ScreenshotAutomation/node_modules/playwright');
const { PNG } = require('../../docs/marketing/ScreenshotAutomation/node_modules/playwright-core/lib/utilsBundle.js');
const url = process.env.TLBX_PERF_URL, cookie = process.env.TLBX_COOKIE_HEADER;
if (!url || !cookie) throw new Error('Authenticated source URL required');
const out = path.resolve(process.env.TLBX_PERF_ARTIFACT_ROOT || '.dev/file-git-production/file-surfaces');
fs.mkdirSync(out, { recursive: true });
const fixture = fs.mkdtempSync(path.join(os.tmpdir(), 'tlbx-surfaces-'));
const documentPath = path.join(fixture, 'tracked.txt');
fs.writeFileSync(documentPath, 'Original first line\nsecond line\nthird line\n');
fs.writeFileSync(path.join(fixture, '.gitignore'), '.tlbx/\nno-hooks/\n'); fs.mkdirSync(path.join(fixture, 'no-hooks'));
const git = args => execFileSync('git', ['-C', fixture, ...args], { windowsHide: true, stdio: 'pipe' });
git(['init', '--quiet']); git(['config', 'user.name', 'Surface acceptance']); git(['config', 'user.email', 'surface@tlbx.invalid']);
git(['config', 'core.hooksPath', path.join(fixture, 'no-hooks')]); git(['add', '--', 'tracked.txt', '.gitignore']); git(['commit', '--quiet', '-m', 'Fixture baseline', '-m', 'A commit body for surface acceptance.']);
fs.writeFileSync(documentPath, 'Intermediate first line\nsecond line\nthird line\n');
git(['add', '--', 'tracked.txt']); git(['commit', '--quiet', '-m', 'Fixture surface', '-m', 'Commit body.']);
fs.writeFileSync(documentPath, 'Changed first line\nsecond line\nthird line\n');
const wallpaper = new PNG({ width: 64, height: 64 });
for (let i = 0; i < wallpaper.data.length; i += 4) { wallpaper.data[i] = 60; wallpaper.data[i + 1] = 120; wallpaper.data[i + 2] = 168; wallpaper.data[i + 3] = 255; }
const wallpaperBytes = PNG.sync.write(wallpaper); fs.writeFileSync(path.join(out, 'wallpaper.png'), wallpaperBytes);
const mediaAcceptance = process.env.TLBX_SURFACE_MEDIA === '1';
const policyAcceptance = process.env.TLBX_SURFACE_POLICY === '1';
const contrastAcceptance = process.env.TLBX_SURFACE_CONTRAST === '1';
if (contrastAcceptance) fs.writeFileSync(path.join(fixture, 'code.ts'), '// Metadata comment\nconst value = "protected text";\nif (value) console.log(value);\n');
if (mediaAcceptance) {
  const picture = new PNG({ width: 32, height: 32 });
  for (let i = 0; i < picture.data.length; i += 4) { picture.data[i] = 200; picture.data[i + 1] = 60; picture.data[i + 2] = 80; picture.data[i + 3] = 128; }
  fs.writeFileSync(path.join(fixture, 'picture.png'), PNG.sync.write(picture));
  const wav = Buffer.alloc(44 + 8000); wav.write('RIFF', 0); wav.writeUInt32LE(wav.length - 8, 4); wav.write('WAVEfmt ', 8); wav.writeUInt32LE(16, 16); wav.writeUInt16LE(1, 20); wav.writeUInt16LE(1, 22); wav.writeUInt32LE(8000, 24); wav.writeUInt32LE(16000, 28); wav.writeUInt16LE(2, 32); wav.writeUInt16LE(16, 34); wav.write('data', 36); wav.writeUInt32LE(8000, 40);
  fs.writeFileSync(path.join(fixture, 'sound.wav'), wav);
  const content = 'BT /F1 18 Tf 20 100 Td (Surface fixture) Tj ET';
  const objects = ['<< /Type /Catalog /Pages 2 0 R >>', '<< /Type /Pages /Kids [3 0 R] /Count 1 >>', '<< /Type /Page /Parent 2 0 R /MediaBox [0 0 240 180] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >>', `<< /Length ${content.length} >>\nstream\n${content}\nendstream`, '<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>'];
  let pdf = '%PDF-1.4\n', offsets = [0];
  for (let i = 0; i < objects.length; i++) { offsets.push(Buffer.byteLength(pdf)); pdf += `${i + 1} 0 obj\n${objects[i]}\nendobj\n`; }
  const xref = Buffer.byteLength(pdf); pdf += `xref\n0 6\n0000000000 65535 f \n${offsets.slice(1).map(n => String(n).padStart(10, '0') + ' 00000 n ').join('\n')}\ntrailer\n<< /Size 6 /Root 1 0 R >>\nstartxref\n${xref}\n%%EOF\n`;
  fs.writeFileSync(path.join(fixture, 'paper.pdf'), pdf);
}
const result = { ok: false, fixture, samples: [], errors: [] };
const sleep = ms => new Promise(resolve => setTimeout(resolve, ms));
let chrome, browser, context, page, session, savedSettings, savedImage;
async function api(method, route, data) {
  const response = await context.request.fetch(url + route, { method, data }); assert.ok(response.ok(), `${route}: ${response.status()}`);
  const text = await response.text(); return text ? JSON.parse(text) : null;
}
async function upload(bytes, name) {
  const response = await context.request.post(url + '/api/settings/background-image', { multipart: { file: { name, mimeType: 'image/png', buffer: bytes } } });
  assert.ok(response.ok(), `Image upload: ${response.status()}`); return response.json();
}
function rgba(value) {
  const numbers = value.match(/[\d.]+/g)?.map(Number) || [0, 0, 0, 0];
  if (value.startsWith('color(')) for (let i = 0; i < 3; i++) numbers[i] *= 255;
  return [...numbers.slice(0, 3), numbers[3] ?? 1];
}
function contrast(fg, bg) {
  const luminance = color => color.slice(0, 3).reduce((sum, c, i) => { const value = c / 255; return sum + [0.2126, 0.7152, 0.0722][i] * (value <= 0.04045 ? value / 12.92 : ((value + 0.055) / 1.055) ** 2.4); }, 0);
  const a = luminance(fg), b = luminance(bg); return (Math.max(a, b) + 0.05) / (Math.min(a, b) + 0.05);
}
async function measureContrast(label, selectors) {
  const evidence = await page.locator(selectors).evaluateAll(nodes => {
    const visible = [...new Set(nodes)].filter(el => el.getClientRects().length > 0 && getComputedStyle(el).visibility !== 'hidden');
    const evidence = visible.map(el => { const style = getComputedStyle(el), rect = el.getBoundingClientRect(); return { text: el.textContent.slice(0, 60), color: style.color, x: Math.min(innerWidth - 2, Math.max(1, rect.x + Math.min(rect.width / 2, 30))), y: Math.min(innerHeight - 2, Math.max(1, rect.y + Math.min(rect.height / 2, 8))) }; });
    window.workspaceContrastElements = [...new Set(visible.flatMap(el => [el,...el.querySelectorAll('*')]))].map(el => ({ el, style: el.getAttribute('style'), background: getComputedStyle(el).backgroundColor }));
    for (const {el,background} of window.workspaceContrastElements) { el.style.setProperty('background-color',background,'important');el.style.setProperty('color','transparent','important');el.style.setProperty('text-shadow','none','important'); }
    return evidence;
  });
  let raster;
  try { raster = PNG.sync.read(await page.screenshot({ animations: 'disabled' })); }
  finally { await page.evaluate(() => { for (const { el, style } of window.workspaceContrastElements) { if (style === null) el.removeAttribute('style'); else el.setAttribute('style', style); } delete window.workspaceContrastElements; }); }
  assert.ok(evidence.length > 0, label + ' must contain visible text');
  for (const e of evidence) {
    const offset = (Math.floor(e.y) * raster.width + Math.floor(e.x)) * 4, background = [...raster.data.subarray(offset, offset + 3)], foreground = rgba(e.color);
    const ratio = contrast(foreground, background); result.samples.push({ label, text: e.text, foreground, background, ratio });
    if (ratio < 4.5) { fs.writeFileSync(path.join(out,'contrast-probe.png'),PNG.sync.write(raster));fs.writeFileSync(path.join(out,'contrast-probe.json'),JSON.stringify({label,e,evidence,foreground,background,ratio},null,2)); }
    assert.ok(ratio >= 4.5, `${label} ${e.text}: contrast ${ratio.toFixed(4)} foreground ${foreground} background ${background}`);
  }
}
async function sample(label, selector, owner, imageColor, leftCorner = false, probeSubstrate = false) {
  const evidence = await page.locator(selector).last().evaluate((element, { stop, leftCorner }) => {
    const chain = []; let node = element;
    while (node) {
      const style = getComputedStyle(node);
      chain.push({ tag: node.tagName, class: node.className, background: style.backgroundColor, image: style.backgroundImage });
      if (node.matches(stop)) break; node = node.parentElement;
    }
    const box = element.getBoundingClientRect(); let right = Math.min(innerWidth, box.right), bottom = Math.min(innerHeight, box.bottom);
    for (let parent = element.parentElement; parent; parent = parent.parentElement) {
      const style = getComputedStyle(parent), rect = parent.getBoundingClientRect();
      if (/(auto|hidden|scroll|clip)/.test(style.overflowX)) right = Math.min(right, rect.right);
      if (/(auto|hidden|scroll|clip)/.test(style.overflowY)) bottom = Math.min(bottom, rect.bottom);
      if (parent.matches(stop)) break;
    }
    return { chain, owner: chain.at(-1)?.background, point: { x: leftCorner ? box.left + 24 : right - 24, y: bottom - 24 }, box: { x: box.x, y: box.y, width: box.width, height: box.height } };
  }, { stop: owner, leftCorner });
  const painted = evidence.chain.filter(layer => rgba(layer.background)[3] > 0);
  assert.equal(painted.length, 1, `${label}: ${JSON.stringify(evidence.chain)}`);
  const expectedSurface = rgba(evidence.owner);
  let substrate = imageColor;
  if (probeSubstrate || owner === '.file-viewer-modal-content') {
    const oldColor = await page.locator(selector).last().evaluate((element, owner) => { const surface = element.closest(owner); const color = surface.style.backgroundColor; surface.style.backgroundColor = 'transparent'; return color; }, owner);
    try {
      const background = PNG.sync.read(await page.screenshot({ animations: 'disabled' }));
      const offset = (Math.floor(evidence.point.y) * background.width + Math.floor(evidence.point.x)) * 4; substrate = [...background.data.subarray(offset, offset + 3)];
    } finally { await page.locator(selector).last().evaluate((element, { owner, oldColor }) => { element.closest(owner).style.backgroundColor = oldColor; }, { owner, oldColor }); }
  }
  const screenshot = await page.screenshot({ animations: 'disabled' });
  const pixels = PNG.sync.read(screenshot), x = Math.floor(evidence.point.x), y = Math.floor(evidence.point.y), offset = (y * pixels.width + x) * 4;
  const actual = [...pixels.data.subarray(offset, offset + 3)];
  const expected = expectedSurface.slice(0, 3).map((c, i) => Math.round(c * expectedSurface[3] + substrate[i] * (1 - expectedSurface[3])));
  assert.ok(actual.every((c, i) => Math.abs(c - expected[i]) <= 3), `${label} pixel ${actual} != ${expected}`);
  result.samples.push({ label, actual, expected, alpha: expectedSurface[3], chain: evidence.chain });
  if (label.includes('65')) fs.writeFileSync(path.join(out, label + '.png'), screenshot);
}
try {
  const profile = path.join(out, 'chrome-profile'); fs.mkdirSync(profile, { recursive: true });
  chrome = spawn('C:/Program Files/Google/Chrome/Application/chrome.exe', ['--headless=new', '--remote-debugging-port=0', '--user-data-dir=' + profile, '--ignore-certificate-errors', '--no-first-run', '--no-default-browser-check'], {
    windowsHide: true, stdio: ['ignore', fs.openSync(path.join(out, 'chrome.stdout.log'), 'w'), fs.openSync(path.join(out, 'chrome.stderr.log'), 'w')],
  });
  for (let i = 0; i < 100; i++) { try { const port = Number(fs.readFileSync(path.join(profile, 'DevToolsActivePort'), 'utf8').split(/\r?\n/)[0]); browser = await chromium.connectOverCDP('http://127.0.0.1:' + port); break; } catch { await sleep(100); } }
  if (!browser) throw new Error('Chrome did not start');
  context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1440, height: 1000 }, deviceScaleFactor: 1 });
  const eq = cookie.indexOf('='); await context.addCookies([{ name: cookie.slice(0, eq), value: cookie.slice(eq + 1), url }]);
  let ready = false;
  for (let i = 0; i < 40; i++) { try { const css = await context.request.get(url + '/css/app.css'); ready = css.ok() && (await css.text()).includes('.file-listing-options-panel'); } catch {} if (ready) break; await sleep(250); }
  if (!ready) throw new Error('Source assets are not ready');
  savedSettings = await api('GET', '/api/settings');
  if (savedSettings.backgroundImageFileName) { const image = await context.request.get(url + '/api/settings/background-image'); assert.ok(image.ok()); savedImage = await image.body(); fs.writeFileSync(path.join(out, 'original-wallpaper.backup'), savedImage); }
  const info = await upload(wallpaperBytes, 'surface-wallpaper.png');
  await api('PATCH', '/api/settings', { backgroundImageEnabled: true, backgroundKenBurnsEnabled: false });
  session = (await api('POST', '/api/sessions', { shell: 'Pwsh', cols: 90, rows: 30, workingDirectory: fixture, launchRequestId: crypto.randomUUID() })).id;
  page = await context.newPage(); page.setDefaultTimeout(15000); page.on('pageerror', e => result.errors.push(e.message));
  await page.goto(url); await page.locator(`.session-item[data-session-id="${session}"] .session-title`).click();
  const imageColor = await page.evaluate(async revision => { const image = new Image(); image.src = `/api/settings/background-image?v=${revision}&encoding=2`; await image.decode(); const canvas = document.createElement('canvas'); canvas.width = canvas.height = 1; const ctx = canvas.getContext('2d'); ctx.drawImage(image, 0, 0, 1, 1); return [...ctx.getImageData(0, 0, 1, 1).data].slice(0, 3); }, info.revision);
  const wrapper = page.locator(`.session-wrapper[data-session-id="${session}"]`), files = wrapper.locator('.session-tab[data-tab="files"]'), terminal = wrapper.locator('.session-tab[data-tab="terminal"]');
  await files.click();
  const row = wrapper.locator('.tree-node').filter({ has: page.locator('.tree-name', { hasText: /^tracked\.txt$/ }) }); await row.waitFor();
  if (contrastAcceptance) {
    for (const pattern of ['white', 'black', 'structured']) {
      const backdrop = new PNG({ width: 64, height: 64 });
      for (let y = 0; y < 64; y++) for (let x = 0; x < 64; x++) { const i = (y * 64 + x) * 4, bright = pattern === 'white' || (pattern === 'structured' && (Math.floor(x / 8) + Math.floor(y / 8)) % 2 === 0); backdrop.data[i] = backdrop.data[i + 1] = backdrop.data[i + 2] = bright ? 255 : 0; backdrop.data[i + 3] = 255; }
      await upload(PNG.sync.write(backdrop), pattern + '.png');
      for (const theme of ['dark', 'light', 'solarizedDark', 'solarizedLight']) for (const transparency of [0, 65, 90]) {
        await api('PATCH', '/api/settings', { theme, uiTransparency: transparency, terminalTransparency: transparency, backgroundImageEnabled: true });
        await page.reload(); await page.locator(`.session-item[data-session-id="${session}"] .session-title`).click(); await files.click(); await row.waitFor();
        const label = `${pattern}-${theme}-${transparency}`, tree = wrapper.locator('.file-browser-tree');
        await page.route('**/api/files/document**', async route => { await sleep(800); await route.continue(); });
        await row.click(); await wrapper.locator('.preview-loading').waitFor();
        await measureContrast(label + '-loading', `.session-wrapper[data-session-id="${session}"] .preview-loading`);
        await wrapper.locator('.file-document-source').waitFor(); await page.unroute('**/api/files/document**');
        await page.route('**/api/files/document**', route => route.fulfill({ status: 403, body: JSON.stringify({ code: 'access_denied' }) }));
        const codeRow = wrapper.locator('.tree-node').filter({ has: page.locator('.tree-name', { hasText: /^code\.ts$/ }) }); await codeRow.click();
        await wrapper.locator('.preview-error').waitFor(); await measureContrast(label + '-error', `.session-wrapper[data-session-id="${session}"] .preview-error`);
        await page.unroute('**/api/files/document**'); await row.click(); await codeRow.click();
        await wrapper.locator('.file-document-source').waitFor();
        await measureContrast(label + '-files', `.session-wrapper[data-session-id="${session}"] .tree-size, .session-wrapper[data-session-id="${session}"] .file-visibility-badge, .session-wrapper[data-session-id="${session}"] .preview-toolbar-subtitle, .session-wrapper[data-session-id="${session}"] .file-document-metadata, .session-wrapper[data-session-id="${session}"] .file-document-mode-status, .session-wrapper[data-session-id="${session}"] .hl-comment, .session-wrapper[data-session-id="${session}"] .hl-string, .session-wrapper[data-session-id="${session}"] .hl-keyword`);
        await row.hover(); await measureContrast(label + '-hover', `.session-wrapper[data-session-id="${session}"] .tree-node[data-name="tracked.txt"] .tree-size`);
        await terminal.click(); await wrapper.locator('.git-indicator').click();
        const gitDock = page.locator('#git-dock'); await gitDock.locator('.git-dock-resize-grip').press('End'); await gitDock.locator('.git-tree-file[data-path="tracked.txt"]').click(); await gitDock.locator('.git-diff-lines').waitFor();
        await measureContrast(label + '-git', '#git-dock .git-panel-summary-subtitle, #git-dock .git-tree-file-selected .git-loc-add, #git-dock .git-tree-file-selected .git-loc-del, #git-dock .git-diff-line-add .git-diff-code, #git-dock .git-diff-line-del .git-diff-code, #git-dock .git-diff-number, #git-dock .git-diff-word-change');
        if (pattern === 'structured' && transparency === 90) await page.screenshot({ path: path.join(out, label + '-git.png'), animations: 'disabled' });
        await gitDock.locator('#git-dock-close').click();
      }
    }
    assert.deepEqual(result.errors, []); result.ok = true;
  } else if (policyAcceptance) {
    for (const theme of ['dark', 'light', 'solarizedDark', 'solarizedLight']) for (const imageEnabled of [false, true]) {
      await api('PATCH', '/api/settings', { theme, uiTransparency: 65, terminalTransparency: 65, backgroundImageEnabled: imageEnabled });
      await page.reload(); await page.locator(`.session-item[data-session-id="${session}"] .session-title`).click(); await files.click(); await row.click();
      const preview = wrapper.locator('.file-browser-preview'), prefix = `.session-wrapper[data-session-id="${session}"] .file-browser-preview`;
      await preview.locator('.file-document-source').waitFor();
      const base = await wrapper.locator('.file-browser').evaluate(el => getComputedStyle(el).backgroundColor), label = `${theme}-65-image${imageEnabled}`;
      await sample(label + '-tab', prefix + ' .file-viewer-text', '.file-browser', imageColor, false, true);
      await terminal.click(); await page.evaluate(target => window.mmDebug.terminals.get(window.mmDebug.activeId).terminal._core._linkProviderService.linkProviders.at(-2)._handler(new MouseEvent('click'), target), documentPath);
      const modal = page.locator('#file-viewer-modal'), content = modal.locator('.file-viewer-modal-content'); await modal.locator('.file-document-view').waitFor();
      assert.equal(await content.evaluate(el => getComputedStyle(el).backgroundColor), base);
      await modal.locator('.file-document-mode[data-mode="edit"]').click();
      const editor = modal.locator('textarea'), draft = 'Preserved draft\n' + 'scrollable line\n'.repeat(180); await editor.fill(draft);
      const position = await editor.evaluate(el => { window.policyEditor = el; el.setSelectionRange(5, 19); el.scrollTop = 500; return [el.selectionStart, el.selectionEnd, el.scrollTop]; });
      const retained = async root => { assert.equal(await root.locator('textarea').evaluate(el => el === window.policyEditor), true); assert.equal(await root.locator('textarea').inputValue(), draft); assert.deepEqual(await root.locator('textarea').evaluate(el => [el.selectionStart, el.selectionEnd, el.scrollTop]), position); };
      await sample(label + '-modal', '#file-viewer-modal textarea', '.file-viewer-modal-content', imageColor, false, true);
      await modal.locator('#file-viewer-maximize').click(); await page.waitForFunction(() => document.querySelector('.file-viewer-modal-content')?.classList.contains('is-maximized')); assert.equal(await page.evaluate(() => document.fullscreenElement), null); await retained(modal);
      assert.equal(await content.evaluate(el => getComputedStyle(el).backgroundColor), base);
      await sample(label + '-maximized', '#file-viewer-modal textarea', '.file-viewer-modal-content', imageColor, false, true);
      await modal.locator('#file-viewer-maximize').click(); await page.waitForFunction(() => !document.querySelector('.file-viewer-modal-content')?.classList.contains('is-maximized')); assert.equal(await page.evaluate(() => document.fullscreenElement), null);
      await modal.locator('#file-viewer-dock-btn').click(); const dock = page.locator('#file-viewer-dock'); await dock.waitFor(); await retained(dock);
      assert.equal(await dock.evaluate(el => getComputedStyle(el).backgroundColor), base);
      await sample(label + '-dock', '#file-viewer-dock textarea', '#file-viewer-dock', imageColor, false, true);
      await dock.locator('#dock-undock').click(); await retained(modal);
      await modal.locator('.modal-close').click(); const decision = page.locator('.file-unsaved-overlay'); await decision.locator('[data-file-decision="discard"]').click(); await modal.waitFor({ state: 'hidden' });
      assert.equal(fs.readFileSync(documentPath, 'utf8'), 'Changed first line\nsecond line\nthird line\n');
    }
    assert.deepEqual(result.errors, []); result.ok = true;
  } else if (mediaAcceptance) {
    await page.bringToFront();
    const movie = await page.evaluate(async () => {
      const canvas = document.createElement('canvas'); canvas.width = 160; canvas.height = 90;
      canvas.style.cssText = 'position:fixed;left:0;top:0;width:160px;height:90px;pointer-events:none'; document.body.append(canvas);
      const ctx = canvas.getContext('2d'); ctx.fillStyle = '#805080'; ctx.fillRect(0, 0, 160, 90);
      const stream = canvas.captureStream(10), recorder = new MediaRecorder(stream, { mimeType: 'video/webm;codecs=vp8' }), chunks = [];
      recorder.ondataavailable = event => chunks.push(event.data);
      const stopped = new Promise(resolve => { recorder.onstop = resolve; });
      recorder.start(); let frame = 0; const draw = setInterval(() => { ctx.fillStyle = frame++ % 2 ? '#805080' : '#806080'; ctx.fillRect(0, 0, 160, 90); }, 50);
      await new Promise(resolve => setTimeout(resolve, 1200)); recorder.stop(); await stopped; clearInterval(draw); stream.getTracks().forEach(track => track.stop()); canvas.remove();
      const bytes = new Uint8Array(await new Blob(chunks, { type: 'video/webm' }).arrayBuffer()); return btoa(String.fromCharCode(...bytes));
    });
    fs.writeFileSync(path.join(fixture, 'movie.webm'), Buffer.from(movie, 'base64'));
    for (const theme of ['dark', 'light', 'solarizedDark', 'solarizedLight']) {
      await api('PATCH', '/api/settings', { theme, uiTransparency: 65, terminalTransparency: 65 });
      await page.reload(); await page.locator(`.session-item[data-session-id="${session}"] .session-title`).click(); await files.click(); await row.waitFor();
      const prefix = `.session-wrapper[data-session-id="${session}"] .file-browser-preview`, preview = wrapper.locator('.file-browser-preview');
      await preview.locator('.preview-empty').waitFor({state:'hidden'}); await sample(`${theme}-65-browse`, `.session-wrapper[data-session-id="${session}"] .file-browser-tree`, '.file-browser', imageColor, true);
      await page.route('**/api/files/document**', async route => { await sleep(400); await route.continue(); });
      await row.click(); await preview.locator('.preview-loading').waitFor(); await sample(`${theme}-65-loading`, prefix, '.file-browser', imageColor, true);
      await preview.locator('.file-document-source').waitFor(); await page.unroute('**/api/files/document**');
      await sample(`${theme}-65-text-state`, prefix + ' .file-document-source .file-viewer-text', '.file-browser', imageColor);
      await page.route('**/api/files/document**', route => route.fulfill({ status: 403, body: JSON.stringify({ code: 'access_denied' }) }));
      const other = wrapper.locator('.tree-node').filter({ has: page.locator('.tree-name', { hasText: /^\.gitignore$/ }) }); await other.click();
      await preview.locator('.preview-error').waitFor(); await sample(`${theme}-65-error`, prefix, '.file-browser', imageColor, true); await page.unroute('**/api/files/document**');
      for (const [name, selector] of [['picture.png', '.file-viewer-image-stage'], ['sound.wav', 'audio'], ['movie.webm', 'video'], ['paper.pdf', 'iframe']]) {
        const mediaRow = wrapper.locator('.tree-node').filter({ has: page.locator('.tree-name', { hasText: name }) }); await mediaRow.click(); await preview.locator(selector).waitFor();
        if (name === 'picture.png') await page.waitForFunction(selector => document.querySelector(selector + ' img')?.naturalWidth === 32, prefix);
        if (name === 'sound.wav' || name === 'movie.webm') {
          await page.waitForFunction(selector => document.querySelector(selector)?.readyState >= 2, prefix + ' ' + selector);
          const playback = await preview.locator(selector).evaluate(async media => { media.muted = true; await media.play(); await new Promise(resolve => setTimeout(resolve, 100)); const evidence = { time: media.currentTime, duration: media.duration, width: media.videoWidth, height: media.videoHeight }; media.pause(); return evidence; });
          assert.ok(playback.time > 0); if (name === 'movie.webm') assert.deepEqual([playback.width, playback.height], [160, 90]);
          result.samples.push({ label: `${theme}-playback-${name}`, playback });
        }
        if (name === 'paper.pdf') {
          const rect = await preview.locator('iframe').boundingBox(); let whitePixels = 0, paintedPdf;
          for (let attempt = 0; attempt < 20; attempt++) {
            paintedPdf = await page.screenshot({ animations: 'disabled' }); const raster = PNG.sync.read(paintedPdf); whitePixels = 0;
            for (let y = Math.ceil(rect.y); y < Math.min(raster.height, rect.y + rect.height); y++) for (let x = Math.ceil(rect.x); x < Math.min(raster.width, rect.x + rect.width); x++) { const offset = (y * raster.width + x) * 4; if (raster.data[offset] > 245 && raster.data[offset + 1] > 245 && raster.data[offset + 2] > 245) whitePixels++; }
            if (whitePixels > 10000) break; await sleep(150);
          }
          assert.ok(whitePixels > 10000, 'Native PDF must paint the actual white document page');
          const backgrounds = await preview.locator('.preview-text-body').evaluate(el => { const output = []; for (let n = el; n; n = n.parentElement) { output.push(getComputedStyle(n).backgroundColor); if (n.matches('.file-browser')) break; } return output; });
          assert.equal(backgrounds.filter(value => rgba(value)[3] > 0).length, 1);
          result.samples.push({ label: `${theme}-65-pdf`, backgrounds, nativeDocumentSurface: true, whitePixels });
          fs.writeFileSync(path.join(out, `${theme}-65-pdf.png`), paintedPdf);
        } else await sample(`${theme}-65-${name}`, prefix + ' .preview-text-body', '.file-browser', imageColor, true);
      }
    }
    assert.deepEqual(result.errors, []); result.ok = true;
  } else {
  for (const theme of ['dark', 'light', 'solarizedDark', 'solarizedLight']) for (const transparency of [0, 65, 90]) {
    await api('PATCH', '/api/settings', { theme, uiTransparency: transparency, terminalTransparency: transparency });
    await page.reload(); await page.locator(`.session-item[data-session-id="${session}"] .session-title`).click();
    await page.waitForFunction(({ theme, transparency }) => window.mmDebug.settings.theme === theme && window.mmDebug.settings.uiTransparency === transparency, { theme, transparency });
    await page.evaluate(() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve))));
    await files.click(); await row.click();
    const preview = wrapper.locator('.file-browser-preview'); await preview.locator('.file-document-source').waitFor();
    const label = `${theme}-${transparency}`;
    await sample(label + '-tree', `.session-wrapper[data-session-id="${session}"] .file-browser-tree`, '.file-browser', imageColor);
    await sample(label + '-source', `.session-wrapper[data-session-id="${session}"] .file-document-source .file-viewer-text`, '.file-browser', imageColor);
    await preview.locator('.file-document-mode[data-mode="edit"]').click();
    await sample(label + '-editor', `.session-wrapper[data-session-id="${session}"] .file-document-editor textarea`, '.file-browser', imageColor);
    await terminal.click();
    await page.evaluate(target => window.mmDebug.terminals.get(window.mmDebug.activeId).terminal._core._linkProviderService.linkProviders.at(-2)._handler(new MouseEvent('click'), target), documentPath);
    const modal = page.locator('#file-viewer-modal'); await modal.locator('.file-document-view').waitFor();
    await modal.evaluate(el => Promise.all(el.getAnimations({ subtree: true }).filter(animation => animation.effect?.getTiming().iterations !== Infinity).map(animation => animation.finished.catch(() => {}))));
    await sample(label + '-modal', '#file-viewer-modal .file-document-source .file-viewer-text', '.file-viewer-modal-content', imageColor);
    await modal.locator('#file-viewer-dock-btn').click();
    const dock = page.locator('#file-viewer-dock'); await dock.locator('.file-document-source').waitFor();
    await sample(label + '-dock', '#file-viewer-dock .file-document-source .file-viewer-text', '#file-viewer-dock', imageColor);
    await dock.locator('#dock-close').click();
    await wrapper.locator('.git-indicator').click();
    const gitDock = page.locator('#git-dock'); await gitDock.locator('.git-dock-resize-grip').press('End'); await gitDock.locator('.git-tree-file[data-path="tracked.txt"]').click();
    await gitDock.locator('.git-diff-lines').waitFor();
    await sample(label + '-git-list', '#git-dock .git-panel-list', '.git-panel-shell', imageColor);
    await sample(label + '-git-inspector', '#git-dock .git-panel-inspector', '.git-panel-shell', imageColor);
    await sample(label + '-git-diff', '#git-dock .git-diff-lines', '.git-panel-shell', imageColor);
    await gitDock.locator('[data-action="toggle-section"][data-section="commits"]').click();
    await gitDock.locator('.git-commit-entry').first().click(); await gitDock.locator('.git-commit-body').waitFor();
    await sample(label + '-git-commit', '#git-dock .git-commit-body', '.git-panel-shell', imageColor);
    await gitDock.locator('#git-dock-close').click();
  }
  assert.deepEqual(result.errors, []); result.ok = true;
  }
} catch (error) {
  result.failure = String(error.stack || error).replaceAll(cookie, '<redacted-session-cookie>');
  await page?.screenshot({ path: path.join(out, 'failure.png') }).catch(() => {}); process.exitCode = 1;
} finally {
  if (context) try {
    if (session) await api('DELETE', '/api/sessions/' + session);
    if (savedSettings) {
      if (savedImage) await upload(savedImage, savedSettings.backgroundImageFileName); else await api('DELETE', '/api/settings/background-image');
      await api('PATCH', '/api/settings', { theme: savedSettings.theme, uiTransparency: savedSettings.uiTransparency, terminalTransparency: savedSettings.terminalTransparency, backgroundImageEnabled: savedSettings.backgroundImageEnabled, backgroundKenBurnsEnabled: savedSettings.backgroundKenBurnsEnabled });
    }
  } catch (error) { result.cleanupFailure = String(error); result.ok = false; process.exitCode = 1; }
  await context?.close(); await browser?.close(); chrome?.kill();
  fs.writeFileSync(path.join(out, 'summary.json'), JSON.stringify(result, null, 2));
  console.log(JSON.stringify({ ok: result.ok, samples: result.samples.length, failure: result.failure, cleanupFailure: result.cleanupFailure, summary: path.join(out, 'summary.json') }));
}

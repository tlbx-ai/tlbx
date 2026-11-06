// Real-input acceptance against an isolated authenticated source instance.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const { chromium } = require('../../docs/marketing/ScreenshotAutomation/node_modules/playwright');
const base = process.env.TLBX_PERF_URL;
const cookie = process.env.TLBX_COOKIE_HEADER;
if (!base || !cookie) throw new Error('Authenticated test instance URL and cookie required');
const out = path.resolve(process.env.TLBX_PERF_ARTIFACT_ROOT || '.dev/boards-browser');
fs.mkdirSync(out, { recursive: true });
const fixture = fs.mkdtempSync(path.join(os.tmpdir(), 'tlbx-boards-'));
fs.writeFileSync(path.join(fixture, 'index.html'), '<!doctype html><h1>Small program</h1><button id="counter">0</button><script src="./app.js"></script>');
const nativeMt = path.resolve('src/Ai.Tlbx.MidTerm/bin/Debug/net10.0/mt.exe');
fs.writeFileSync(path.join(fixture, 'agent.py'), "import sys, subprocess, json, re, os\nfrom pathlib import Path\nroot = Path(__file__).parent\nboard_id = item_id = None\n(root / \"native-info.json\").write_text(json.dumps({key:os.environ.get(key) for key in [\"MT_BASE_URL\",\"MT_PORT\",\"MT_SESSION_ID\",\"TLBX_PORT\",\"TLBX_BIND\"]}), encoding=\"utf-8\")\nwith (root / \"prompt.txt\").open(\"w\", encoding=\"utf-8\", buffering=1) as log:\n    for line in sys.stdin:\n        log.write(line)\n        board = re.search(r\"Board ID: ([a-f0-9]{32})\", line)\n        item = re.search(r\"Item ID: ([a-f0-9]{32})\", line)\n        if board: board_id = board.group(1)\n        if item: item_id = item.group(1)\n        if \"Date:\" in line and board_id and item_id:\n            for action, args in [(\"native-item\", [\"item\", board_id, item_id]), (\"native-serve\", [\"serve\", board_id, str(root)])]:\n                result = subprocess.run([NATIVE_MT, \"board\", *args], cwd=root, capture_output=True, encoding=\"utf-8\", errors=\"replace\")\n                (root / (action + \".json\")).write_text(json.dumps({\"code\": result.returncode, \"stdout\": result.stdout, \"stderr\": result.stderr}), encoding=\"utf-8\")\n".replace('NATIVE_MT', JSON.stringify(nativeMt)));
fs.writeFileSync(path.join(fixture, 'app.js'), "document.querySelector('#counter').onclick = event => event.target.textContent = String(Number(event.target.textContent) + 1);");
const browser = await chromium.launch({ executablePath: process.env.TLBX_CHROME_PATH || 'C:/Program Files/Google/Chrome/Application/chrome.exe', headless: true });
const context = await browser.newContext({ ignoreHTTPSErrors: true, viewport: { width: 1440, height: 1000 } });
await context.addCookies([{ name: 'mm-session', value: cookie.slice('mm-session='.length), url: base }]);
const page = await context.newPage();
page.setDefaultTimeout(15000);
const errors = [];
const diagnostics = { responses: [], console: [] };
page.on('pageerror', error => errors.push(error.message));
page.on('response', response => { if (response.status() >= 400) diagnostics.responses.push({ status: response.status(), path: new URL(response.url()).pathname }); });
page.on('console', message => { if (message.type() === 'error') diagnostics.console.push(message.text().replace(/([?&](?:token|__mtPreviewToken)=)[^&#\s'"]+/g, '$1[redacted]')); });
const passed = [];
let boardId;
let taskSession;
const api = async (suffix = '', method = 'GET', body) => {
  let response;
  try {
    response = await context.request.fetch(`${base}/api/boards${suffix}`, { method, ...(body === undefined ? {} : { data: body }) });
  } catch {
    throw new Error(`${method} ${suffix}: test instance is unreachable`);
  }
  assert.ok(response.ok(), `${method} ${suffix}: ${response.status()} ${await response.text()}`);
  return response.status() === 204 ? undefined : response.json();
};
const waitFor = async (predicate, label) => {
  const deadline = Date.now() + 15000;
  while (Date.now() < deadline) { if (await predicate()) return; await new Promise(resolve => setTimeout(resolve, 150)); }
  throw new Error(`Timed out: ${label}`);
};
const boardAction = async (target, action) => {
  await target.locator('.boards-menu-trigger').click();
  await target.locator(`.boards-actions-menu [data-board-action="${action}"]`).click();
};
const createItem = async kind => {
  await boardAction(page, 'add');
  await page.locator(`.boards-context-menu [data-board-action="${kind}"]`).click();
};
try {
  const board = await api('', 'POST', { title: 'Project desk', panX: 80, panY: 80 });
  boardId = board.id;
  await page.goto(`${base}/?board=${boardId}`);
  await page.locator('.boards-workspace').waitFor({ state: 'visible' });
  const surface = page.locator('.boards-surface');
  assert.equal(await surface.evaluate(el => getComputedStyle(el).backgroundImage), 'none');
  await page.locator('.boards-empty button').click();
  await page.locator('.boards-context-menu').waitFor();
  await page.keyboard.press('Escape');
  await page.locator('.boards-identity [data-board-action="rename"]').click();
  await page.locator('.boards-title').fill('Cancelled title');
  await page.keyboard.press('Escape');
  assert.equal(await page.locator('.boards-header select option:checked').textContent(), 'Project desk');
  await page.locator('.boards-identity [data-board-action="new"]').click();
  await page.locator('.boards-title:not([hidden])').waitFor();
  await page.locator('.boards-title').fill('Created beside the title');
  await page.keyboard.press('Enter');
  let createdBoard;
  await waitFor(async () => { createdBoard = (await api()).find(b => b.title === 'Created beside the title'); return !!createdBoard; }, 'new board title saved');
  await page.reload();
  await page.locator('.boards-header select').selectOption(boardId);
  await page.locator('.boards-empty button').waitFor();
  const identityBounds = await page.locator('.boards-identity').boundingBox();
  const renameBounds = await page.locator('.boards-identity [data-board-action="rename"]').boundingBox();
  assert.ok(renameBounds.x >= identityBounds.x && renameBounds.x < identityBounds.x + identityBounds.width);
  await api(`/${createdBoard.id}?expectedRevision=${(await api(`/${createdBoard.id}`)).revision}`, 'DELETE');
  passed.push('quiet canvas, empty-board creation, adjacent New/Rename, Escape cancel and Enter save with reload');

  assert.equal(await page.locator('.boards-tools').count(), 0);
  await surface.click({button:'right',position:{x:200,y:120}});
  await page.locator('.boards-context-menu').waitFor();
  await page.screenshot({path:path.join(out,'board-context-menu.png')});
  await page.locator('.boards-context-menu [data-board-action="note"]').click();
  await page.locator('.board-item-text').waitFor();
  let contextNote = (await api(`/${boardId}`)).items[0];
  assert.equal(contextNote.x,120); assert.equal(contextNote.y,40);
  assert.equal((await api(`/${boardId}`)).panX,80);
  await page.locator('.boards-header select').focus();
  await api(`/${boardId}/items/${contextNote.id}?expectedRevision=${contextNote.revision}`,'DELETE');
  await page.locator('.board-item').waitFor({state:'hidden'});
  await surface.focus(); await page.keyboard.press('Shift+F10');
  await page.locator('.boards-context-menu').waitFor();
  await page.keyboard.press('Escape');
  assert.equal(await surface.evaluate(element => element === document.activeElement),true);
  passed.push('right-click creates at canvas coordinates without panning; keyboard context menu restores focus');

  await surface.dblclick({ position: { x: 220, y: 180 } });
  const note = page.locator('.board-item[data-kind="note"]');
  await note.locator('.board-item-title').fill('An idea worth keeping');
  await note.locator('.board-item-text').fill('Make space for the next useful thing.\nA note should be this easy.');
  await waitFor(async () => (await api(`/${boardId}`)).items.some(item => item.text.includes('A note should')), 'note autosave');
  passed.push('double-click creation and direct text autosave');
  const noteId = await note.getAttribute('data-id');
  const before = (await api(`/${boardId}`)).items.find(item => item.id === noteId);
  const grip = await note.locator('.board-item-grip').boundingBox();
  await page.mouse.move(grip.x + 40, grip.y + 15);
  await page.mouse.down();
  await page.mouse.move(grip.x + 160, grip.y + 95, { steps: 8 });
  await page.mouse.up();
  await waitFor(async () => (await api(`/${boardId}`)).items.find(item => item.id === noteId).x > before.x + 100, 'drag persisted');
  const resize = await note.locator('.board-resize').boundingBox();
  await page.mouse.move(resize.x + 8, resize.y + 8);
  await page.mouse.down();
  await page.mouse.move(resize.x + 128, resize.y + 68, { steps: 8 });
  await page.mouse.up();
  await waitFor(async () => (await api(`/${boardId}`)).items.find(item => item.id === noteId).width > before.width + 100, 'resize persisted');
  passed.push('real pointer drag and resize saved in board coordinates');
  const viewport = await page.evaluate(() => ({ dpr: devicePixelRatio, width: innerWidth, scale: visualViewport.scale }));
  await page.mouse.move(700, 650);
  await page.keyboard.down('Control');
  await page.mouse.wheel(0, -300);
  await page.keyboard.up('Control');
  await waitFor(async () => (await api(`/${boardId}`)).zoom > 1.2, 'board zoom saved');
  assert.deepEqual(await page.evaluate(() => ({ dpr: devicePixelRatio, width: innerWidth, scale: visualViewport.scale })), viewport);
  passed.push('Ctrl-wheel zooms board and preserves browser zoom');
  await createItem('todo');
  const todo = page.locator('.board-item[data-kind="todo"]');
  await todo.locator('.board-item-title').fill('Today');
  await todo.locator('.board-item-title').press('Enter');
  assert.equal(await todo.locator('.board-entry-add').evaluate(element => element === document.activeElement), true);
  await page.keyboard.type('Ship the useful part');
  await page.keyboard.press('Enter');
  await todo.locator('input[type="checkbox"]').first().check();
  await waitFor(async () => (await api(`/${boardId}`)).items.some(item => item.kind === 'todo' && item.entries[0]?.done), 'todo saved');
  passed.push('low-friction Enter-to-add and toggle todo');
  const rowText = todo.locator('.board-entry-text');
  await todo.locator('input[type="checkbox"]').first().focus();
  assert.equal(await todo.locator('.board-mail-url').first().isVisible(),false);
  await todo.locator('.board-entry-text').first().focus();
  assert.equal(await todo.locator('.board-mail-url').first().isVisible(),false);
  passed.push('focused todo checkbox and text keep mail-only fields hidden');

  await rowText.first().focus();
  await page.keyboard.press('End');
  await page.keyboard.press('Enter');
  assert.equal(await rowText.count(), 2);
  assert.equal(await rowText.nth(1).evaluate(element => element === document.activeElement), true);
  await page.keyboard.type('Second task');
  await rowText.nth(1).evaluate(element => element.setSelectionRange(7,7));
  await page.keyboard.press('Enter');
  assert.equal(await rowText.count(), 3);
  assert.equal(await rowText.nth(2).inputValue(), 'task');
  assert.equal(await rowText.nth(2).evaluate(element => element.selectionStart), 0);
  await page.keyboard.type('Next ');
  await waitFor(async () => (await api(`/${boardId}`)).items.find(item => item.kind === 'todo').entries[2]?.text === 'Next task', 'Enter rows saved');
  await page.reload();
  await todo.waitFor();
  assert.deepEqual(await rowText.evaluateAll(elements => elements.map(element => element.value)), ['Ship the useful part','Second ','Next task']);
  assert.deepEqual(await todo.locator('input[type="checkbox"]').evaluateAll(elements => elements.map(element => element.checked)), [true,false,false]);
  await todo.locator('.board-entry').nth(2).getByRole('button', {name:'Delete entry',exact:true}).click();
  await todo.locator('.board-entry').nth(1).getByRole('button', {name:'Delete entry',exact:true}).click();
  await waitFor(async () => (await api(`/${boardId}`)).items.find(item => item.kind === 'todo').entries.length === 1, 'extra keyboard rows removed');
  passed.push('Enter in an existing to-do inserts and focuses a new unchecked row, splits at caret and persists exact row order through reload');

  const longTask = 'Mit Julia die nächste Woche für Johanna, Miriam, Georg und Nora abstimmen: Termine, Wege und Betreuung.';
  await todo.locator('.board-entry-add').fill(longTask);
  await todo.locator('.board-entry-add').press('Enter');
  const wrapped = todo.locator('.board-entry-text').last();
  const wrapping = await wrapped.evaluate(el => ({height:el.clientHeight,scroll:el.scrollHeight,line:parseFloat(getComputedStyle(el).lineHeight),value:el.value}));
  assert.equal(wrapping.value,longTask);
  assert.ok(wrapping.height > wrapping.line * 2 && wrapping.scroll <= wrapping.height + 1);
  await wrapped.focus();
  await wrapped.evaluate(el => el.setSelectionRange(8,8));
  await page.keyboard.type('gemeinsam ');
  await waitFor(async () => (await api(`/${boardId}`)).items.find(item => item.kind === 'todo').entries.at(-1).text.includes('gemeinsam '), 'wrapped edit saved');
  await page.reload();
  await todo.waitFor();
  assert.equal(await todo.locator('.board-entry-text').last().inputValue(),longTask.slice(0,8)+'gemeinsam '+longTask.slice(8));
  await todo.locator('.board-entry').last().getByRole('button', {name:'Delete entry',exact:true}).click();
  passed.push('long German tasks wrap fully, grow during editing and persist exact text after reload');

  const state = await api(`/${boardId}`);
  const latestNote = state.items.find(item => item.id === noteId);
  const tileIdentity = await note.evaluate(element => { element.__boardAcceptanceIdentity = 42; return element.dataset.id; });
  await api(`/${boardId}/items/${noteId}`, 'PUT', { expectedRevision: latestNote.revision, item: { ...latestNote, title: 'Updated by CLI agent' } });
  await page.locator('.boards-header select').focus();
  await waitFor(async () => await note.locator('.board-item-title').inputValue() === 'Updated by CLI agent', 'agent edit visible');
  assert.equal(await note.evaluate(element => element.__boardAcceptanceIdentity), 42);
  assert.equal(await note.getAttribute('data-id'), tileIdentity);
  passed.push('external updates preserve tile DOM identity');
  const stale = await context.request.put(`${base}/api/boards/${boardId}/items/${noteId}`, { data: { expectedRevision: latestNote.revision, item: latestNote } });
  assert.equal(stale.status(), 409);
  passed.push('stale agent write rejected with 409');
  await api(`/${boardId}/items`, 'POST', { item: { kind: 'browser', title: 'A tiny local program', x: 850, y: 60, width: 400, height: 300, localDirectory: fixture } });
  const local = page.locator('.board-item[data-kind="browser"] iframe');
  await local.waitFor();
  const program = local.contentFrame().frameLocator('#preview-frame');
  await waitFor(async () => await program.locator('#counter').evaluate(element => typeof element.onclick === 'function').catch(() => false), 'local JS handler attached');
  await program.locator('#counter').click();
  assert.equal(await program.locator('#counter').textContent(), '1');
  assert.equal(await program.locator('body').evaluate(() => { try { return !!parent.document; } catch { return false; } }), false);
  passed.push('local HTML and JS work in isolated interactive tile');
  const browserItem = (await api(`/${boardId}`)).items.find(item => item.kind === 'browser');
  const execution = await api(`/${boardId}/items/${browserItem.id}/browser-command`, 'POST', {
    command: 'exec', value: '({count:Number(document.querySelector("#counter").textContent)})',
  });
  assert.deepEqual(JSON.parse(execution.result), { count: 1 });
  await api(`/${boardId}/items/${browserItem.id}/browser-command`, 'POST', { command: 'click', selector: '#counter' });
  await waitFor(async () => await program.locator('#counter').textContent() === '2', 'CLI click in board browser');
  assert.equal(await local.contentFrame().locator('.web-preview-dock-header').isVisible(), false);
  passed.push('scoped browser exec and click work in standalone board without a terminal UI');
  await page.screenshot({ path: path.join(out, 'board-desktop.png') });
  await page.reload();
  await note.waitFor();
  assert.equal(await note.locator('.board-item-title').inputValue(), 'Updated by CLI agent');
  assert.ok((await api(`/${boardId}`)).zoom > 1.2);
  passed.push('reload retains saved text, geometry and viewport');
  await boardAction(page, 'reset');
  await waitFor(async () => (await api(`/${boardId}`)).zoom === 1, 'reset view saved');
  await createItem('calendar');
  await page.locator('.board-item[data-kind="calendar"] .board-item-title').waitFor();
  await page.locator('.boards-header select').focus();
  let calendarItem = (await api(`/${boardId}`)).items.find(item => item.kind === 'calendar');
  await api(`/${boardId}/items/${calendarItem.id}`, 'PUT', { expectedRevision: calendarItem.revision, item: { ...calendarItem, calendarMonth: '2021-01' } });
  const calendar = page.locator('.board-item[data-kind="calendar"]');
  await calendar.locator('[data-date="2021-01-04"]').click();
  await calendar.locator('.board-entry-add').fill('Year-boundary planning');
  await calendar.locator('.board-entry-add').press('Enter');
  await waitFor(async () => (await api(`/${boardId}`)).items.find(item => item.kind === 'calendar').entries.some(entry => entry.date === '2021-01-04'), 'calendar date saved');
  assert.equal(await calendar.locator('.board-calendar-grid span').first().textContent(), '53');
  await calendar.locator('.board-calendar-header button').last().click();
  await waitFor(async () => (await api(`/${boardId}`)).items.find(item => item.kind === 'calendar').calendarMonth === '2021-02', 'calendar month saved');
  await calendar.locator('.board-calendar-header button').first().click();
  passed.push('interactive month calendar, selected-day creation and ISO week 53');
  await createItem('mail');
  const mail = page.locator('.board-item[data-kind="mail"]');
  await mail.locator('.board-item-title').fill('Mail inbox');
  await mail.locator('.board-entry-add').fill('Architecture review');
  await mail.locator('.board-entry-add').press('Enter');
  await mail.locator('.board-mail-sender').fill('Alex');
  await mail.locator('.board-mail-url').fill('https://example.test/mail/42');
  await mail.locator('input[type="checkbox"]').check();
  await waitFor(async () => (await api(`/${boardId}`)).items.some(item => item.kind === 'mail' && item.entries[0]?.sender === 'Alex' && item.entries[0]?.done && item.entries[0]?.url === 'https://example.test/mail/42'), 'mail state saved');
  passed.push('mail sender, reference and read state edit directly');
  await createItem('timeline');
  const timeline = page.locator('.board-item[data-kind="timeline"]');
  await timeline.locator('.board-item-title').fill('Long-term direction');
  await timeline.locator('.board-entry-add').fill('Next milestone');
  await timeline.locator('.board-entry-add').press('Enter');
  await timeline.locator('.board-entry-period').fill('2027 W12');
  await timeline.locator('.board-item-title').click();
  await waitFor(async () => (await api(`/${boardId}`)).items.some(item => item.kind === 'timeline' && item.entries[0]?.week === 12 && item.entries[0]?.year === 2027), 'week saved');
  await timeline.locator('.board-entry-period').fill('2027 Q3');
  await timeline.locator('.board-item-title').click();
  await waitFor(async () => (await api(`/${boardId}`)).items.some(item => item.kind === 'timeline' && item.entries[0]?.quarter === 3 && item.entries[0]?.week === null), 'quarter saved');
  passed.push('long-term week and quarter plans persist as structured values');
  await page.locator('.boards-header select').focus();
  const layout = { note: [20, 20, 360, 270], todo: [420, 20, 320, 300], browser: [780, 20, 450, 300], calendar: [20, 330, 360, 440], mail: [420, 360, 360, 360], timeline: [820, 360, 360, 300] };
  for (const item of (await api(`/${boardId}`)).items) {
    const [x, y, width, height] = layout[item.kind];
    await api(`/${boardId}/items/${item.id}`, 'PUT', { expectedRevision: item.revision, item: { ...item, x, y, width, height } });
  }
  await waitFor(async () => await timeline.evaluate(element => element.style.transform) === 'translate(820px, 360px)', 'agent geometry visible');
  await surface.click({ position: { x: 10, y: 10 } });
  await note.locator('.board-item-text').focus();
  const conflictBase = (await api(`/${boardId}`)).items.find(item => item.id === noteId);
  await api(`/${boardId}/items/${noteId}`, 'PUT', {expectedRevision:conflictBase.revision, item:{...conflictBase,text:'Remote draft kept safely'}});
  await note.locator('.board-item-text').fill('My unsaved draft kept safely');
  const conflictPanel = page.locator('[role="alertdialog"]');
  await conflictPanel.waitFor();
  assert.ok((await conflictPanel.locator('textarea').first().inputValue()).includes('My unsaved draft kept safely'));
  assert.ok((await conflictPanel.locator('textarea').last().inputValue()).includes('Remote draft kept safely'));
  await conflictPanel.locator('[data-conflict-action="server"]').click();
  await conflictPanel.waitFor({state:'hidden'});
  assert.equal(await note.locator('.board-item-text').inputValue(), 'Remote draft kept safely');
  const cameraBefore = await surface.evaluate(element => element.querySelector('.boards-plane').style.transform);
  const titleBase = await api(`/${boardId}`);
  await page.locator('.boards-identity [data-board-action="rename"]').click();
  await api(`/${boardId}`, 'PATCH', {expectedRevision:titleBase.revision,title:'Agent board title'});
  await page.locator('.boards-title').fill('My board title');
  await surface.focus();
  await conflictPanel.waitFor();
  await conflictPanel.locator('[data-conflict-action="server"]').click();
  await conflictPanel.waitFor({state:'hidden'});
  assert.equal(await page.locator('.boards-title').inputValue(), 'Agent board title');
  assert.equal(await page.locator('.boards-header select option:checked').textContent(), 'Agent board title');
  assert.equal(await surface.evaluate(element => element.querySelector('.boards-plane').style.transform), cameraBefore);
  await note.locator('.board-item-grip').click({position:{x:40,y:15}});
  const keyboardX = (await api(`/${boardId}`)).items.find(item => item.id === noteId).x;
  await page.keyboard.press('Shift+ArrowRight');
  await waitFor(async () => (await api(`/${boardId}`)).items.find(item => item.id === noteId).x === keyboardX+10, 'keyboard move persisted');
  await surface.click({position:{x:10,y:10}});
  passed.push('item and title conflict comparison preserves drafts, explicit resolution and camera; keyboard movement persists');
  await page.screenshot({ path: path.join(out, 'board-desktop.png') });
  const popupPromise = page.waitForEvent('popup');
  await boardAction(page, 'detach');
  const popup = await popupPromise;
  await popup.locator('.board-item[data-kind="note"]').waitFor();
  assert.equal(new URL(popup.url()).searchParams.get('board'), boardId);
  const childProgram = popup.locator('.board-item[data-kind="browser"] iframe').contentFrame().frameLocator('#preview-frame');
  await waitFor(async () => await childProgram.locator('#counter').evaluate(element => typeof element.onclick === 'function').catch(() => false), 'detached browser ready');
  await api(`/${boardId}/items/${browserItem.id}/browser-command`, 'POST', { command: 'click', selector: '#counter' });
  await waitFor(async () => await childProgram.locator('#counter').textContent() === '1', 'CLI ownership transferred to child');
  const childClosed = popup.waitForEvent('close');
  await boardAction(popup, 'dock');
  await childClosed;
  await waitFor(async () => await program.locator('#counter').evaluate(element => typeof element.onclick === 'function').catch(() => false), 'docked browser ready');
  await api(`/${boardId}/items/${browserItem.id}/browser-command`, 'POST', { command: 'click', selector: '#counter' });
  await waitFor(async () => await program.locator('#counter').textContent() === '1', 'CLI ownership restored to parent');
  passed.push('detach and dock transfer live browser command ownership');

  await page.locator('.boards-header select').focus();
  await boardAction(page, 'settings');
  await page.locator('.board-session-form input[name="workingDirectory"]').fill(fixture);
  await page.locator('.board-session-form input[name="launchCommand"]').fill(`python -u "${path.join(fixture, 'agent.py')}"`);
  await page.locator('.board-session-form input[name="sessionProfile"]').fill('generic-ai');
  await page.locator('.board-session-form button[type="submit"]').click();
  await page.locator('.boards-inspector').waitFor({state:'hidden'});
  await todo.locator('.board-entry-launch').click();
  await waitFor(async () => (await api(`/${boardId}/launches`)).some(launch => launch.state === 'submitted'), 'task prompt submitted');
  const launch = (await api(`/${boardId}/launches`))[0];
  taskSession = launch.sessionId;
  const taskItem = (await api(`/${boardId}`)).items.find(item => item.kind === 'todo');
  assert.equal(launch.entryId, taskItem.entries[0].id);
  await waitFor(async () => fs.existsSync(path.join(fixture, 'prompt.txt')) && fs.readFileSync(path.join(fixture, 'prompt.txt'), 'utf8').includes('Task: Ship the useful part'), 'real terminal received board task prompt');
  const received = fs.readFileSync(path.join(fixture, 'prompt.txt'), 'utf8');
  assert.ok(received.includes(`Board ID: ${boardId}`));
  assert.ok(received.includes(`Item ID: ${taskItem.id}`));
  assert.ok(received.includes(`Task entry ID: ${launch.entryId}`));
  assert.ok(received.includes(`/?board=${boardId}&item=${taskItem.id}`));
  await todo.locator('.board-entry-launch').click();
  assert.equal((await api(`/${boardId}/launches`)).length, 1);
  const link = page.locator('.board-launch-list a');
  assert.equal(await link.getAttribute('href'), `/?session=${taskSession}`);
  const linkedPage = await context.newPage();
  await linkedPage.goto(`${base}/?board=${boardId}&item=${taskItem.id}`);
  await linkedPage.locator(`.board-item[data-id="${taskItem.id}"].is-selected`).waitFor();
  await linkedPage.close();
  passed.push('task row launches real terminal with exact board item entry reference and saved session link');
  await waitFor(async () => fs.existsSync(path.join(fixture, 'native-serve.json')), 'native CLI executed inside real task terminal');
  const nativeItem = JSON.parse(fs.readFileSync(path.join(fixture, 'native-item.json'), 'utf8'));
  assert.equal(nativeItem.code, 0, nativeItem.stderr);
  assert.equal(JSON.parse(nativeItem.stdout).id, taskItem.id);
  const nativeServe = JSON.parse(fs.readFileSync(path.join(fixture, 'native-serve.json'), 'utf8'));
  assert.equal(nativeServe.code, 0, nativeServe.stderr);
  const served = JSON.parse(nativeServe.stdout);
  assert.equal(served.localDirectory, fixture);
  const servedFrame = page.locator(`[data-id="${served.id}"] iframe`).contentFrame().frameLocator('#preview-frame');
  await servedFrame.locator('#counter').waitFor();
  const controlled = await api(`/${boardId}/items/${served.id}/browser-command`, 'POST', { command:'exec', value:'document.querySelector("#counter").textContent' });
  assert.equal(controlled.result, '0');
  await api(`/${boardId}/items/${served.id}?expectedRevision=${served.revision}`, 'DELETE');
  passed.push('native mt board item and serve work inside real session with authenticated CLI ancestry and live local program');
  for (const theme of ['dark', 'light', 'solarizedDark', 'solarizedLight']) {
    const shell = await context.newPage();
    shell.on('pageerror', error => errors.push(error.message));
    const appearance = settings => ({ ...settings, theme, uiTransparency:100,
      terminalTransparency:100, backgroundImageEnabled:true,
      backgroundImageFileName:'acceptance.svg', backgroundImageRevision:1 });
    for (const endpoint of ['settings', 'bootstrap']) {
      await shell.route(`**/api/${endpoint}`, async route => {
        const response = await route.fetch();
        const data = await response.json();
        const json = endpoint === 'bootstrap' ? {...data,settings:appearance(data.settings)} : appearance(data);
        await route.fulfill({response,json});
      });
    }
    await shell.routeWebSocket('**/ws/settings', socket => {
      const server = socket.connectToServer();
      server.onMessage(message => {
        const data = JSON.parse(String(message));
        socket.send(JSON.stringify(data.settings ? {...data,settings:appearance(data.settings)} : data));
      });
    });
    await shell.route('**/api/settings/background-image?*', route => route.fulfill({
      contentType: 'image/svg+xml', body: '<svg xmlns="http://www.w3.org/2000/svg" width="1600" height="1000"><path fill="#df81b0" d="M0 0h1600v1000H0z"/><path fill="#285d65" d="M0 500L800 100l800 600v300H0z"/></svg>'
    }));
    await shell.goto(`${base}/?session=${taskSession}`);
    await shell.locator(`.session-item[data-session-id="${taskSession}"].active`).waitFor();
    await shell.locator('body.has-app-background').waitFor();
    const nativeBar = await shell.locator('.session-tab-bar').first().evaluate(element => ({
      height:parseFloat(getComputedStyle(element).height),font:getComputedStyle(element.querySelector('.session-tab')).fontSize
    }));
    await shell.locator('#btn-boards').click();
    await shell.locator('.boards-workspace:not([hidden])').waitFor();
    const boardBar = await shell.locator('.boards-header').evaluate(element => ({
      height:element.getBoundingClientRect().height,font:getComputedStyle(element).fontSize
    }));
    assert.deepEqual(boardBar, nativeBar, 'Boards header uses native bar height and type size');
    assert.equal(await shell.locator('.boards-tools, [data-board-action="close"]').count(), 0);
    assert.equal(await shell.locator('.boards-title').isVisible(), false);
    await shell.locator('.boards-header select').selectOption(boardId);
    await shell.locator('.boards-workspace[aria-busy="false"]').waitFor();
    await boardAction(shell, 'reset');
    const assertPane = async (withWallpaper = true) => {
      const geometry = await shell.evaluate(() => {
        const root = document.querySelector('.boards-workspace');
        const host = document.querySelector('.terminals-area');
        const rect = element => { const r = element.getBoundingClientRect(); return [r.x,r.y,r.width,r.height]; };
        const color = element => getComputedStyle(element).backgroundColor;
        const sidebar = document.querySelector('#btn-boards');
        const r = sidebar.getBoundingClientRect();
        return { root:rect(root), host:rect(host), parent:root.parentElement === host,
          appInert:document.querySelector('#app').inert, role:root.getAttribute('role'),
          sidebarUsable: sidebar.contains(document.elementFromPoint(r.x+r.width/2,r.y+r.height/2)),
          terminalVisible:getComputedStyle(document.querySelector('.layout-root')).visibility,
          colors:[root,document.querySelector('.boards-header'),document.querySelector('.boards-actions-menu')].map(color),
          foreground:getComputedStyle(root).color,
          wallpaper:getComputedStyle(document.documentElement).getPropertyValue('--app-background-image') };
      });
      assert.deepEqual(geometry.root, geometry.host, `${theme}: board fills terminal pane`);
      assert.equal(geometry.parent, true);
      assert.equal(geometry.appInert, false);
      assert.equal(geometry.role, 'region');
      assert.equal(geometry.terminalVisible, 'hidden');
      assert.ok(geometry.colors.every(color => /^rgb\(/.test(color)), `${theme}: opaque canvas and chrome: ${geometry.colors}`);
      const luminance = color => color.match(/[\d.]+/g).slice(0,3).map(Number).map(c => c/255)
        .map(c => c<=0.04045 ? c/12.92 : ((c+0.055)/1.055)**2.4)
        .reduce((sum,c,i) => sum+c*[0.2126,0.7152,0.0722][i],0);
      const foreground = luminance(geometry.foreground);
      for (const color of geometry.colors) {
        const background = luminance(color);
        assert.ok((Math.max(foreground,background)+0.05)/(Math.min(foreground,background)+0.05)>=4.5,
          `${theme}: readable text ${geometry.foreground} contrast on ${color}`);
      }
      if (withWallpaper) assert.ok(geometry.wallpaper.includes('background-image'));
      return geometry;
    };
    assert.equal((await assertPane()).sidebarUsable, true);
    const beforeBoardKeys = fs.readFileSync(path.join(fixture, 'prompt.txt'), 'utf8');
    await shell.locator('.boards-surface').focus();
    await shell.keyboard.type('z');
    await shell.keyboard.press('Enter');
    await new Promise(resolve => setTimeout(resolve,150));
    assert.equal(fs.readFileSync(path.join(fixture, 'prompt.txt'), 'utf8'), beforeBoardKeys);
    await shell.screenshot({ path: path.join(out, `board-embedded-${theme}.png`) });
    const deleteItem = (await api(`/${boardId}/items`, 'POST', {item:{
      kind:'note', title:'Deletion preview', text:'A disposable item', entries:[],
      x:0,y:0,width:200,height:200,z:9999,color:'plain'
    }}));
    const deleteTile = shell.locator(`[data-id="${deleteItem.id}"]`);
    await deleteTile.waitFor();
    const openDelete = async () => {
      await deleteTile.getByRole('button',{name:'Delete item',exact:true}).click();
      const dialog = shell.getByRole('alertdialog',{name:'Delete item',exact:true});
      await dialog.waitFor();
      await dialog.evaluate(element => Promise.all(element.getAnimations({subtree:true}).map(animation => animation.finished)));
      return dialog;
    };
    for (const [width,height] of [[1440,1000],[390,844],[320,568]]) {
      await shell.setViewportSize({width,height});
      const dialog = await openDelete();
      assert.equal(await dialog.locator('.modal-footer [data-role="cancel"]').evaluate(element => element === document.activeElement),true);
      const layout = await dialog.evaluate(element => {
        const bounds = node => {const r=node.getBoundingClientRect();return {x:r.x,y:r.y,width:r.width,height:r.height,right:r.right,bottom:r.bottom};};
        return {dialog:bounds(element),buttons:[...element.querySelectorAll('.modal-footer button')].map(bounds),
          scroll:element.scrollWidth,client:element.clientWidth,
          background:getComputedStyle(element).backgroundColor,
          dangerColor:getComputedStyle(element.querySelector('.btn-danger')).color};
      });
      assert.ok(Math.abs(layout.dialog.width-Math.min(420,width-32))<1,`${theme}: dialog width at ${width}`);
      assert.ok(layout.dialog.x>=15 && layout.dialog.right<=width-15);
      assert.ok(layout.dialog.y>=0 && layout.dialog.bottom<=height);
      assert.ok(layout.buttons.every(button => button.x>=layout.dialog.x+15 && button.right<=layout.dialog.right-15));
      assert.ok(layout.scroll<=layout.client);
      assert.ok(/^rgb\(/.test(layout.background));
      const luminance = color => color.match(/[\d.]+/g).slice(0,3).map(Number).map(c => c/255)
        .map(c => c<=0.04045 ? c/12.92 : ((c+0.055)/1.055)**2.4)
        .reduce((sum,c,i) => sum+c*[0.2126,0.7152,0.0722][i],0);
      const foreground = luminance(layout.dangerColor), background = luminance(layout.background);
      assert.ok((Math.max(foreground,background)+0.05)/(Math.min(foreground,background)+0.05)>=4.5,
        `${theme}: destructive action text contrast`);
      await shell.screenshot({path:path.join(out,`delete-${theme}-${width}.png`)});
      // Tab wraps within the dialog; Enter on Cancel must cancel rather than delete.
      await shell.keyboard.press('Shift+Tab');
      await shell.keyboard.press('Shift+Tab');
      assert.equal(await dialog.locator('[data-role="confirm"]').evaluate(element => element === document.activeElement),true);
      await shell.keyboard.press('Tab');
      await shell.keyboard.press('Tab');
      await shell.keyboard.press('Enter');
      await dialog.waitFor({state:'hidden'});
      assert.ok((await api(`/${boardId}`)).items.some(item => item.id===deleteItem.id));
      await openDelete();
      await shell.keyboard.press('Escape');
      await dialog.waitFor({state:'hidden'});
      assert.ok((await api(`/${boardId}`)).items.some(item => item.id===deleteItem.id));
    }
    const deletion = await openDelete();
    await deletion.getByRole('button',{name:'Delete item',exact:true}).click();
    await waitFor(async () => !(await api(`/${boardId}`)).items.some(item => item.id===deleteItem.id),'explicit Delete removes item');
    await shell.setViewportSize({width:1440,height:1000});
    // Settings and even reselecting the current session must remain usable from the sidebar.
    await shell.locator('#btn-settings').click();
    await shell.locator('.boards-workspace').waitFor({state:'hidden'});
    await shell.locator('#settings-view').waitFor({state:'visible'});
    await shell.locator('#btn-boards').click();
    await shell.locator('.boards-workspace[aria-busy="false"]').waitFor();
    await shell.locator(`.session-item[data-session-id="${taskSession}"]`).click({position:{x:30,y:20}});
    await shell.locator('.boards-workspace').waitFor({state:'hidden'});
    await shell.locator('#btn-boards').click();
    await shell.locator('.boards-workspace[aria-busy="false"]').waitFor();
    await shell.setViewportSize({ width:390, height:844 });
    const settle = () => shell.evaluate(() => Promise.all(document.getAnimations()
      .filter(animation => animation.effect?.getTiming().iterations !== Infinity)
      .map(animation => animation.finished.catch(() => {}))));
    await settle();
    await shell.locator('#btn-mobile-strip-terminal').click();
    await shell.locator('.boards-workspace').waitFor({state:'hidden'});
    await shell.locator('#btn-hamburger').click();
    await shell.locator('#btn-boards').click();
    await shell.locator('.boards-workspace[aria-busy="false"]').waitFor();
    await settle();
    await assertPane(false);
    await shell.screenshot({ path:path.join(out, `board-embedded-${theme}-narrow.png`) });
    await shell.close();
  }
  passed.push('delete confirmation fits desktop, 390px and 320px in all four themes; Cancel/Enter, Escape, focus wrapping and explicit Delete work');
  passed.push('embedded Boards fills terminal pane with opaque chrome under wallpaper in all four themes; sidebar navigation, key isolation and narrow layout work');
  await page.locator('.boards-inspector > button').first().click();
  await page.locator('.boards-menu-trigger').focus();
  await page.keyboard.press('Enter');
  await page.keyboard.press('End');
  assert.equal(await page.evaluate(() => document.activeElement.dataset.boardAction), 'reset');
  await page.keyboard.press('Home');
  assert.equal(await page.evaluate(() => document.activeElement.dataset.boardAction), 'add');
  await page.keyboard.press('Escape');
  assert.equal(await page.locator('.boards-workspace').isVisible(), true);
  assert.equal(await page.evaluate(() => document.activeElement.classList.contains('boards-menu-trigger')), true);
  passed.push('overflow menu keyboard navigation and Escape preserve board and restore trigger focus');
  await page.setViewportSize({ width: 390, height: 844 });
  const touch = await context.newCDPSession(page);
  await touch.send('Emulation.setTouchEmulationEnabled', {enabled:true,maxTouchPoints:2});
  const rect = await surface.boundingBox();
  const touchZoom = (await api(`/${boardId}`)).zoom;
  const touchScale = await page.evaluate(() => ({dpr:devicePixelRatio,scale:visualViewport.scale}));
  const points = [{x:rect.x+20,y:rect.y+120,id:1}, {x:rect.x+80,y:rect.y+120,id:2}];
  await touch.send('Input.dispatchTouchEvent', {type:'touchStart',touchPoints:points});
  await touch.send('Input.dispatchTouchEvent', {type:'touchMove',touchPoints:[points[0],{x:rect.x+150,y:rect.y+120,id:2}]});
  await touch.send('Input.dispatchTouchEvent', {type:'touchEnd',touchPoints:[]});
  await waitFor(async () => (await api(`/${boardId}`)).zoom > touchZoom*1.5, 'two-finger zoom persisted');
  assert.deepEqual(await page.evaluate(() => ({dpr:devicePixelRatio,scale:visualViewport.scale})), touchScale);
  await touch.detach();
  await boardAction(page, 'reset');
  await waitFor(async () => (await api(`/${boardId}`)).zoom === 1, 'reset after touch');
  await page.mouse.move(5, 300);
  await page.mouse.wheel(90,0);
  await waitFor(async () => (await api(`/${boardId}`)).panX < 0, 'mobile pan saved');
  passed.push('real two-finger touch zoom persists without browser zoom and mobile pan remains usable');
  await page.screenshot({ path: path.join(out, 'board-narrow.png') });
  assert.ok(await page.locator('.boards-menu-trigger').isVisible());
  await boardAction(page, 'add');
  await page.locator('.boards-context-menu').waitFor();
  await page.screenshot({path:path.join(out,'board-narrow-menu.png')});
  await page.keyboard.press('Escape');
  passed.push('narrow viewport retains board controls');
  assert.deepEqual(errors, []);
  fs.writeFileSync(path.join(out, 'summary.json'), JSON.stringify({ passed, errors }, null, 2));
  console.log(JSON.stringify({ passed, errors }));
} catch (error) {
  if (taskSession) {
    const terminal = await context.request.get(`${base}/api/sessions/${taskSession}/buffer/tail?lines=120&stripAnsi=true`).catch(() => null);
    if (terminal?.ok()) fs.writeFileSync(path.join(out, 'task-terminal.json'), await terminal.text());
  }
  await page.screenshot({ path: path.join(out, 'failure.png') }).catch(() => {});
  fs.writeFileSync(path.join(out, 'summary.json'), JSON.stringify({ passed, errors, failure: String(error) }, null, 2));
  fs.writeFileSync(path.join(out, 'diagnostics.json'), JSON.stringify({ ...diagnostics, frames: page.frames().map(frame => { const url = new URL(frame.url() || 'about:blank'); return url.origin + url.pathname; }) }, null, 2));
  throw error;
} finally {
  try {
    if (taskSession) {
      let deleted;
      try { deleted = await context.request.delete(`${base}/api/sessions/${taskSession}`); }
      catch { throw new Error('Test session cleanup could not reach its source instance'); }
      assert.ok(deleted.ok(), `Test session cleanup failed: ${deleted.status()}`);
    }
    if (boardId) {
      const current = await api(`/${boardId}`);
      await api(`/${boardId}?expectedRevision=${current.revision}`, 'DELETE');
    }
  } finally {
    await context.close();
    await browser.close();
    assert.equal(path.dirname(fixture), os.tmpdir());
    assert.ok(path.basename(fixture).startsWith('tlbx-boards-'));
    for (const name of ['prompt.txt', 'native-item.json', 'native-serve.json', 'native-info.json', 'agent.py']) {
      if (fs.existsSync(path.join(fixture, name))) fs.copyFileSync(path.join(fixture, name), path.join(out, name));
    }
    fs.rmSync(fixture, { recursive: true });
  }
}

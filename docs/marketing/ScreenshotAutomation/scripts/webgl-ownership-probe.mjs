#!/usr/bin/env node
// Behavioral probe for WebGL context ownership: create more sessions than
// MAX_WEBGL_CONTEXTS (6), cycle through them, and verify the active terminal
// always renders with WebGL (canvas present) while hidden ones surrender
// contexts instead of starving the active session.
import { chromium } from '@playwright/test';

const baseUrl = process.env.MIDTERM_BASE_URL ?? 'https://127.0.0.1:2100';
const SESSION_COUNT = 8;

const browser = await chromium.launch({ headless: false });
const context = await browser.newContext({
  baseURL: baseUrl,
  ignoreHTTPSErrors: true,
  viewport: { width: 1344, height: 756 },
});
const page = await context.newPage();
await page.goto('/');
await page.waitForLoadState('domcontentloaded');
await page.waitForTimeout(1500);

const sessionIds = [];
for (let i = 1; i <= SESSION_COUNT; i += 1) {
  const id = await page.evaluate(async (n) => {
    const response = await fetch('/api/sessions', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ shell: 'Pwsh', cols: 100, rows: 30, surface: 'webgl-probe' }),
    });
    const session = await response.json();
    await fetch(`/api/sessions/${session.id}/input/text`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ text: `Clear-Host; Write-Host 'webgl probe session ${n}' -ForegroundColor Green`, appendNewline: true }),
    });
    return session.id;
  }, i);
  sessionIds.push(id);
  await page.waitForTimeout(250);
}

await page.waitForTimeout(2500);

const results = [];
for (const sessionId of sessionIds) {
  await page.evaluate((id) => {
    document
      .querySelector(`.session-item[data-session-id="${CSS.escape(id)}"]`)
      ?.dispatchEvent(new MouseEvent('click', { bubbles: true }));
  }, sessionId);
  await page.waitForTimeout(1300);

  const check = await page.evaluate((id) => {
    const wrapper = document.querySelector(`.session-wrapper[data-session-id="${CSS.escape(id)}"]`);
    const activeCanvases = wrapper?.querySelectorAll('canvas').length ?? 0;
    let totalCanvasTerminals = 0;
    for (const w of document.querySelectorAll('.session-wrapper')) {
      if (w.querySelectorAll('canvas').length > 0) totalCanvasTerminals += 1;
    }
    return {
      activeHidden: wrapper?.classList.contains('hidden') ?? null,
      activeCanvases,
      totalCanvasTerminals,
    };
  }, sessionId);
  results.push({ sessionId, ...check });
}

const failures = results.filter((r) => r.activeCanvases === 0 || r.activeHidden !== false);
console.log(JSON.stringify({ results, failures: failures.length }, null, 2));

for (const id of sessionIds) {
  await page.evaluate(async (sessionId) => {
    await fetch(`/api/sessions/${sessionId}`, { method: 'DELETE' });
  }, id);
}
await browser.close();
process.exit(failures.length === 0 ? 0 : 1);

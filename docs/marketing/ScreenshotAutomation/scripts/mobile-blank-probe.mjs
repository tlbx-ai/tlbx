#!/usr/bin/env node
// Diagnostic probe for the mobile blank-terminal issue seen in marketing captures.
// Launches a Chromium context (mobile-emulated or desktop), creates a seeded
// session via API, selects it, then dumps xterm renderer diagnostics.
import { chromium } from '@playwright/test';

const baseUrl = process.env.MIDTERM_BASE_URL ?? 'https://127.0.0.1:2100';
const mobile = process.argv.includes('--mobile');
const withSettings = process.argv.includes('--settings');
const withCss = process.argv.includes('--css');
const withHideLoop = process.argv.includes('--hideloop');
const withClaim = process.argv.includes('--claim');

const browser = await chromium.launch({ headless: false });
const context = await browser.newContext({
  baseURL: baseUrl,
  ignoreHTTPSErrors: true,
  viewport: mobile ? { width: 390, height: 693 } : { width: 1344, height: 756 },
  isMobile: mobile,
  hasTouch: mobile,
  deviceScaleFactor: mobile ? 3 : 1,
});
const page = await context.newPage();
await page.goto('/');
await page.waitForLoadState('domcontentloaded');
await page.waitForTimeout(1500);

if (withSettings) {
  await page.evaluate(async () => {
    const original = await (await fetch('/api/settings')).json();
    await fetch('/api/settings', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({
        ...original,
        theme: 'dark',
        terminalColorScheme: 'dark',
        backgroundImageEnabled: false,
        backgroundKenBurnsEnabled: false,
        hideBackgroundImageOnMobile: true,
        uiTransparency: 0,
        terminalTransparency: 0,
        terminalCellBackgroundTransparency: 0,
        mobileDenseTerminalMode: false,
        fontSize: 17,
        lineHeight: 1.08,
        terminalThemeLightnessBoost: 8,
      }),
    });
  });
  await page.waitForTimeout(1200);
}

const applyCaptureCss = async () => {
  if (withCss) {
    await page.addStyleTag({
      content: `
        body.mobile-marketing-capture,
        body.mobile-marketing-capture #app,
        body.mobile-marketing-capture .app,
        body.mobile-marketing-capture .app-shell {
          background: #05070b !important;
        }
        body.mobile-marketing-capture .session-wrapper,
        body.mobile-marketing-capture .terminal-container,
        body.mobile-marketing-capture .xterm,
        body.mobile-marketing-capture .xterm-viewport,
        body.mobile-marketing-capture .xterm-screen {
          background: #05070b !important;
        }
        body.mobile-marketing-capture .xterm-rows {
          opacity: 0.96 !important;
        }
      `,
    });
    await page.evaluate(() => document.body.classList.add('mobile-marketing-capture'));
  }
  if (withHideLoop) {
    await page.evaluate(() => {
      for (const element of document.querySelectorAll('body *')) {
        if (element.innerText?.includes('No password set.')) {
          element.style.display = 'none';
        }
      }
    });
  }
};
await applyCaptureCss();

const withSecond = process.argv.includes('--second');

const createProbeSession = async (label) =>
  page.evaluate(async (text) => {
    const response = await fetch('/api/sessions', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ shell: 'Pwsh', cols: 46, rows: 30, surface: 'blank-probe' }),
    });
    const session = await response.json();
    await fetch(`/api/sessions/${session.id}/input/text`, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ text, appendNewline: true }),
    });
    return session.id;
  }, `Clear-Host; 1..12 | ForEach-Object { Write-Host ("${label} line {0}" -f $_) -ForegroundColor Green }`);

const created = await createProbeSession('probe');
// With --second, create a second session afterwards so it becomes the
// auto-activated one; selecting the first session post-reload then exercises
// the switch-to-background-session path.
const secondId = withSecond ? await createProbeSession('decoy') : null;

await page.waitForTimeout(1500);
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForTimeout(2500);
await applyCaptureCss();

if (withClaim) {
  await page.evaluate(async () => {
    const clientId = document.cookie.match(/(?:^|;\s*)mt-client-id=([^;]+)/)?.[1];
    const tabId = sessionStorage.getItem('mt-tab-id');
    const browserId = clientId
      ? tabId
        ? `${decodeURIComponent(clientId)}:${tabId}`
        : decodeURIComponent(clientId)
      : null;
    if (!browserId) throw new Error('no browser id');
    await fetch('/api/browser/main', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ value: browserId }),
    });
  });
  await page.waitForTimeout(1200);
}

if (mobile) {
  await page.locator('#btn-hamburger').click({ force: true }).catch(() => {});
  await page.waitForTimeout(800);
}
await page.evaluate((id) => {
  document
    .querySelector(`.session-item[data-session-id="${CSS.escape(id)}"]`)
    ?.dispatchEvent(new MouseEvent('click', { bubbles: true }));
}, created);
await page.waitForTimeout(4000);

const diag = await page.evaluate((id) => {
  const wrapper = document.querySelector(`.session-wrapper[data-session-id="${CSS.escape(id)}"]`);
  const container = wrapper?.querySelector('.terminal-container');
  const canvases = [...(wrapper?.querySelectorAll('canvas') ?? [])].map((c) => ({
    cls: c.className,
    w: c.width,
    h: c.height,
    cssW: c.style.width,
    cssH: c.style.height,
  }));
  const rows = wrapper?.querySelector('.xterm-rows');
  const probeCanvas = document.createElement('canvas');
  return {
    wrapperHidden: wrapper?.classList.contains('hidden') ?? null,
    containerClass: container?.className ?? null,
    canvasCount: canvases.length,
    canvases,
    rowsChildCount: rows?.childElementCount ?? null,
    rowsTextLength: (rows?.textContent ?? '').trim().length,
    xtermPresent: Boolean(wrapper?.querySelector('.xterm')),
    webgl2Available: Boolean(probeCanvas.getContext('webgl2')),
    visibilityState: document.visibilityState,
    documentHidden: document.hidden,
  };
}, created);

const variant = [
  mobile ? 'mobile' : 'desktop',
  withSettings ? 'settings' : '',
  withCss ? 'css' : '',
  withHideLoop ? 'hideloop' : '',
  withClaim ? 'claim' : '',
  withSecond ? 'second' : '',
]
  .filter(Boolean)
  .join('-');
console.log(JSON.stringify({ variant, sessionId: created, diag }, null, 2));

await page.screenshot({ path: `C:\\temp\\blank-probe-${variant}.png` });
for (const id of [created, secondId].filter(Boolean)) {
  await page.evaluate(async (sessionId) => {
    await fetch(`/api/sessions/${sessionId}`, { method: 'DELETE' });
  }, id);
}
await browser.close();

import { test, expect, type Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

const outputBase = path.join(__dirname, '../output/mobile-vertical-demos');
const mobileViewport = { width: 390, height: 693 };
const videoSize = { width: 1080, height: 1920 };
const rawVideoSize = mobileViewport;
const demoSessionNames = ['Editor TUI', 'Build Loop', 'Agent Console', 'Dev Browser'] as const;

type SessionDto = {
  id: string;
  name?: string | null;
  surface?: string | null;
};

type Settings = Record<string, unknown>;

function getNextRunNumber(): number {
  if (!fs.existsSync(outputBase)) {
    return 1;
  }

  return (
    fs
      .readdirSync(outputBase)
      .map((entry) => /^run-(\d+)$/.exec(entry)?.[1])
      .filter((value): value is string => Boolean(value))
      .map((value) => Number.parseInt(value, 10))
      .reduce((max, value) => Math.max(max, value), 0) + 1
  );
}

async function readSettings(page: Page): Promise<Settings> {
  return page.evaluate(async () => {
    const response = await fetch('/api/settings');
    if (!response.ok) {
      throw new Error(`GET /api/settings failed: ${response.status}`);
    }
    return (await response.json()) as Record<string, unknown>;
  });
}

async function writeSettings(page: Page, settings: Settings): Promise<void> {
  await page.evaluate(async (nextSettings) => {
    const response = await fetch('/api/settings', {
      method: 'PUT',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify(nextSettings),
    });
    if (!response.ok) {
      throw new Error(`PUT /api/settings failed: ${response.status}`);
    }
  }, settings);
}

async function applyCaptureSettings(page: Page, runDir: string): Promise<Settings> {
  const original = await readSettings(page);
  fs.writeFileSync(path.join(runDir, 'settings-before.json'), JSON.stringify(original, null, 2));

  const next = {
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
    fontSize: 15,
    lineHeight: 1.08,
    terminalLigaturesEnabled: true,
    terminalThemeLightnessBoost: 6,
  };
  await writeSettings(page, next);
  await page.waitForTimeout(1000);
  fs.writeFileSync(path.join(runDir, 'settings-applied.json'), JSON.stringify(await readSettings(page), null, 2));
  return original;
}

async function restoreSettings(page: Page, original: Settings | null): Promise<void> {
  if (original) {
    await writeSettings(page, original);
  }
}

async function getDemoSessions(page: Page): Promise<Record<(typeof demoSessionNames)[number], string>> {
  return page.evaluate(async (names) => {
    const response = await fetch('/api/sessions');
    if (!response.ok) {
      throw new Error(`GET /api/sessions failed: ${response.status}`);
    }
    const payload = (await response.json()) as { sessions?: SessionDto[] };
    const result: Record<string, string> = {};
    for (const name of names) {
      const session = payload.sessions?.find((entry) => entry.name === name);
      if (!session) {
        throw new Error(`Missing seeded marketing demo session: ${name}`);
      }
      result[name] = session.id;
    }
    return result as Record<(typeof names)[number], string>;
  }, demoSessionNames);
}

async function claimMainBrowser(page: Page): Promise<string> {
  return page.evaluate(async () => {
    const cookieMatch = document.cookie.match(/(?:^| )mt-client-id=([^;]+)/);
    const clientId = cookieMatch?.[1] ? decodeURIComponent(cookieMatch[1]) : null;
    const tabId = window.sessionStorage.getItem('mt-tab-id');
    const browserId = clientId && tabId ? `${clientId}:${tabId}` : clientId;
    if (!browserId) {
      throw new Error('Cannot resolve tlbx browser id for mobile capture.');
    }

    const response = await fetch('/api/browser/main', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ value: browserId }),
    });
    const payload = (await response.json()) as { success?: boolean; error?: string };
    if (!response.ok || payload.success !== true) {
      throw new Error(payload.error ?? `POST /api/browser/main failed: ${response.status}`);
    }
    return browserId;
  });
}

async function hideNonDemoSidebarSessions(page: Page, sessionIds: string[]): Promise<void> {
  await page.evaluate((ids) => {
    const keep = new Set(ids);
    for (const item of document.querySelectorAll<HTMLElement>('.session-item[data-session-id]')) {
      item.style.display = keep.has(item.dataset.sessionId ?? '') ? '' : 'none';
    }
  }, sessionIds);
}

async function selectSession(page: Page, sessionId: string): Promise<void> {
  const clicked = await page.evaluate((id) => {
    const item = document.querySelector<HTMLElement>(`.session-item[data-session-id="${CSS.escape(id)}"]`);
    item?.click();
    return Boolean(item);
  }, sessionId);
  if (!clicked) {
    throw new Error(`Cannot find session item for ${sessionId}`);
  }
  await page.locator(`.session-wrapper[data-session-id="${sessionId}"]:not(.hidden)`).waitFor({
    timeout: 8000,
  });
  await page.waitForTimeout(900);
}

async function openSidebar(page: Page): Promise<void> {
  const app = page.locator('.terminal-page.sidebar-open');
  if ((await app.count()) === 0) {
    await page.locator('#btn-hamburger').click({ force: true });
    await expect(page.locator('.terminal-page.sidebar-open')).toBeVisible({ timeout: 5000 });
  }
  await page.waitForTimeout(700);
}

async function closeSidebar(page: Page): Promise<void> {
  await page.evaluate(() => document.querySelector<HTMLElement>('.sidebar-overlay')?.click());
  await page.waitForTimeout(500);
}

async function showLabel(page: Page, text: string): Promise<void> {
  await page.evaluate((label) => {
    let el = document.getElementById('mobile-marketing-label');
    if (!el) {
      el = document.createElement('div');
      el.id = 'mobile-marketing-label';
      el.style.position = 'fixed';
      el.style.left = '14px';
      el.style.right = '14px';
      el.style.bottom = '18px';
      el.style.zIndex = '999999';
      el.style.padding = '10px 12px';
      el.style.borderRadius = '8px';
      el.style.background = 'rgba(5,8,14,0.9)';
      el.style.border = '1px solid rgba(255,255,255,0.16)';
      el.style.color = '#fff';
      el.style.font = '700 16px/1.15 Inter, system-ui, sans-serif';
      el.style.letterSpacing = '0';
      el.style.pointerEvents = 'none';
      document.body.appendChild(el);
    }
    el.textContent = label;
  }, text);
}

async function hideLabel(page: Page): Promise<void> {
  await page.evaluate(() => document.getElementById('mobile-marketing-label')?.remove());
}

async function stabilizeSurface(page: Page): Promise<void> {
  await page.addStyleTag({
    content: `
      html, body, #app, .app, .app-shell, .terminal-container, .session-wrapper,
      .xterm, .xterm-viewport, .xterm-screen {
        background-image: none !important;
      }

      body.mobile-marketing-capture,
      body.mobile-marketing-capture #app,
      body.mobile-marketing-capture .app,
      body.mobile-marketing-capture .app-shell {
        background: #05070b !important;
      }

      body.mobile-marketing-capture [data-testid*="password" i],
      body.mobile-marketing-capture [class*="password" i] {
        display: none !important;
      }

      body.mobile-marketing-capture .xterm-rows {
        opacity: 0.96 !important;
      }

      body.mobile-marketing-capture .adaptive-footer-dock,
      body.mobile-marketing-capture .adaptive-footer-reserve,
      body.mobile-marketing-capture .manager-bar,
      body.mobile-marketing-capture .mobile-pip-preview {
        display: none !important;
      }

      body.mobile-marketing-capture {
        --adaptive-footer-reserved-height: 0px !important;
      }
    `,
  });
  await page.evaluate(() => {
    document.body.classList.add('mobile-marketing-capture');
    for (const element of document.querySelectorAll<HTMLElement>('body *')) {
      if (element.innerText?.includes('No password set.')) {
        element.style.display = 'none';
      }
    }
  });
}

async function openDevBrowser(page: Page, sessionId: string, demoUrl: string): Promise<void> {
  await page.evaluate(
    async ({ targetSessionId, url }) => {
      const response = await fetch('/api/webpreview/target', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          sessionId: targetSessionId,
          previewName: 'default',
          url,
        }),
      });
      if (!response.ok) {
        throw new Error(`PUT /api/webpreview/target failed: ${response.status}`);
      }
    },
    { targetSessionId: sessionId, url: demoUrl },
  );

  await selectSession(page, sessionId);
  await closeSidebar(page);
  await page.locator('#btn-mobile-actions-menu').click({ force: true });
  await page.evaluate(() => document.getElementById('btn-mobile-web')?.click());
  await expect(page.locator('#web-preview-dock:not(.hidden)')).toBeVisible({ timeout: 8000 });
  await page.locator('#web-preview-url-input').fill(demoUrl);
  await page.locator('#web-preview-go').click({ force: true });
  await expect(page.locator('.web-preview-iframe:not(.hidden)')).toBeVisible({ timeout: 10000 });
  await page.waitForTimeout(1300);
}

async function switchTheme(page: Page, scheme: string): Promise<void> {
  const settings = await readSettings(page);
  await writeSettings(page, {
    ...settings,
    theme: scheme.includes('Light') || scheme === 'light' || scheme === 'solarizedLight' ? 'light' : 'dark',
    terminalColorScheme: scheme,
  });
  await page.waitForTimeout(620);
}

test.use({
  baseURL: process.env.MIDTERM_BASE_URL ?? 'https://127.0.0.1:2100',
  ignoreHTTPSErrors: true,
  viewport: mobileViewport,
  isMobile: true,
  hasTouch: true,
  deviceScaleFactor: 3,
  video: {
    mode: 'on',
    size: rawVideoSize,
  },
});

test.setTimeout(180000);

test('mobile vertical demo - sessions tui browser themes', async ({ page }) => {
  const runDir = path.join(outputBase, `run-${getNextRunNumber()}`);
  fs.mkdirSync(runDir, { recursive: true });
  let originalSettings: Settings | null = null;
  test.info().annotations.push({ type: 'runDir', description: runDir });
  test.info().annotations.push({ type: 'artifactBase', description: 'mobile-vertical-demo' });

  try {
    await page.goto('/');
    await expect(page.locator('#mobile-topbar')).toBeVisible({ timeout: 10000 });
    originalSettings = await applyCaptureSettings(page, runDir);
    await stabilizeSurface(page);
    const browserId = await claimMainBrowser(page);
    const sessions = await getDemoSessions(page);
    await hideNonDemoSidebarSessions(page, Object.values(sessions));

    await openSidebar(page);
    await showLabel(page, 'Multiple live local sessions, phone-width UI.');
    await page.screenshot({ path: path.join(runDir, 'frame-sidebar.png'), fullPage: false });
    await page.waitForTimeout(1600);
    await hideLabel(page);

    await selectSession(page, sessions['Editor TUI']);
    await closeSidebar(page);
    await showLabel(page, 'Real TUI: edit running locally.');
    await page.screenshot({ path: path.join(runDir, 'frame-editor-tui.png'), fullPage: false });
    await page.waitForTimeout(2300);
    await hideLabel(page);

    await selectSession(page, sessions['Build Loop']);
    await closeSidebar(page);
    await showLabel(page, 'Build loop stays alive while you switch context.');
    await page.screenshot({ path: path.join(runDir, 'frame-build-loop.png'), fullPage: false });
    await page.waitForTimeout(2100);
    await hideLabel(page);

    await selectSession(page, sessions['Agent Console']);
    await closeSidebar(page);
    await showLabel(page, 'Codex, Grok, and Copilot are visible local tools.');
    await page.screenshot({ path: path.join(runDir, 'frame-agent-console.png'), fullPage: false });
    await page.waitForTimeout(1800);
    await hideLabel(page);

    await openDevBrowser(page, sessions['Dev Browser'], process.env.MIDTERM_DEMO_URL ?? 'http://127.0.0.1:4177/');
    await showLabel(page, 'Session-scoped Dev Browser opens beside the work.');
    await page.screenshot({ path: path.join(runDir, 'frame-dev-browser.png'), fullPage: false });
    await page.waitForTimeout(1800);
    await hideLabel(page);

    await selectSession(page, sessions['Build Loop']);
    for (const scheme of ['dark', 'solarizedDark', 'matrix', 'campbell']) {
      await switchTheme(page, scheme);
      await showLabel(page, `Theme: ${scheme}`);
      await page.waitForTimeout(520);
    }
    await hideLabel(page);
    await page.screenshot({ path: path.join(runDir, 'frame-theme-final.png'), fullPage: false });

    fs.writeFileSync(
      path.join(runDir, 'mobile-demo-audit.json'),
      JSON.stringify(
        {
          browserId,
          viewport: mobileViewport,
          videoSize,
          sessions,
          checks: {
            mobileTopbarVisible: await page.locator('#mobile-topbar').isVisible(),
            bodyMarkedForCapture: await page.evaluate(() =>
              document.body.classList.contains('mobile-marketing-capture'),
            ),
            seededSessionCount: Object.keys(sessions).length,
          },
        },
        null,
        2,
      ),
    );

  } finally {
    await restoreSettings(page, originalSettings);
  }
});

test.afterEach(async ({ page }, testInfo) => {
  const runDir = testInfo.annotations.find((annotation) => annotation.type === 'runDir')?.description;
  const artifactBase =
    testInfo.annotations.find((annotation) => annotation.type === 'artifactBase')?.description ??
    'mobile-vertical-demo';
  const video = page.video();
  if (!runDir || !video) {
    return;
  }

  const destPath = path.join(runDir, `${artifactBase}.webm`);
  await page.close();
  await video.saveAs(destPath);
  console.log(`Mobile vertical demo saved to: ${destPath}`);
});

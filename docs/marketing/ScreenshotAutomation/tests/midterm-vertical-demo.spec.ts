import { test, expect, type Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

const outputBase = path.join(__dirname, '../output/vertical-demos');
const verticalViewport = { width: 1080, height: 1920 };
const productionSettingsPatch = {
  theme: 'dark',
  terminalColorScheme: 'dark',
  backgroundImageEnabled: false,
  backgroundKenBurnsEnabled: false,
  uiTransparency: 0,
  terminalTransparency: 0,
  terminalCellBackgroundTransparency: 0,
  fontSize: 18,
  lineHeight: 1.08,
  terminalThemeLightnessBoost: 10,
} as const;

type AuditEvidence = {
  browserId?: string | null;
  settings?: Record<string, unknown>;
  browserStatus?: unknown;
  terminalMetrics?: unknown;
  checks: Record<string, boolean>;
};

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

function parseCookie(raw: string | undefined): { name: string; value: string } | null {
  if (!raw) {
    return null;
  }

  const match = /mm-session=([^;\s]+)/.exec(raw);
  if (!match?.[1]) {
    return null;
  }

  return { name: 'mm-session', value: match[1] };
}

async function authenticate(page: Page): Promise<void> {
  const cookie = parseCookie(process.env.MT_COOKIE) ?? parseCookie(process.env.MT_TOKEN ? `mm-session=${process.env.MT_TOKEN}` : undefined);
  if (cookie) {
    await page.context().addCookies([
      {
        name: cookie.name,
        value: cookie.value,
        domain: 'localhost',
        path: '/',
        httpOnly: true,
        secure: true,
        sameSite: 'Lax',
      },
    ]);
  }

  const demoSessionId = process.env.MIDTERM_DEMO_SESSION_ID?.trim();
  if (demoSessionId) {
    await page.addInitScript((sessionId) => {
      window.localStorage.setItem('midterm.activeSessionId', sessionId);
    }, demoSessionId);
  }
}

async function pause(ms: number): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, ms));
}

async function showCaption(page: Page, text: string): Promise<void> {
  await page.evaluate((caption) => {
    let el = document.getElementById('marketing-caption');
    if (!el) {
      el = document.createElement('div');
      el.id = 'marketing-caption';
      el.style.position = 'fixed';
      el.style.left = '36px';
      el.style.bottom = '124px';
      el.style.zIndex = '999999';
      el.style.width = 'min(520px, calc(100vw - 72px))';
      el.style.padding = '18px 22px';
      el.style.borderRadius = '10px';
      el.style.background = 'rgba(5, 8, 14, 0.92)';
      el.style.border = '1px solid rgba(255,255,255,0.14)';
      el.style.color = 'white';
      el.style.font = '700 30px/1.12 Inter, system-ui, sans-serif';
      el.style.letterSpacing = '0';
      el.style.boxShadow = '0 12px 28px rgba(0,0,0,0.36)';
      el.style.pointerEvents = 'none';
      document.body.appendChild(el);
    }
    el.textContent = caption;
  }, text);
}

async function hideCaption(page: Page): Promise<void> {
  await page.evaluate(() => document.getElementById('marketing-caption')?.remove());
}

async function readSettings(page: Page): Promise<Record<string, unknown>> {
  return page.evaluate(async () => {
    const response = await fetch('/api/settings');
    if (!response.ok) {
      throw new Error(`GET /api/settings failed: ${response.status}`);
    }
    return (await response.json()) as Record<string, unknown>;
  });
}

async function writeSettings(page: Page, settings: Record<string, unknown>): Promise<void> {
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

async function applyProductionSettings(page: Page, runDir: string): Promise<Record<string, unknown>> {
  const original = await readSettings(page);
  fs.writeFileSync(path.join(runDir, 'settings-before.json'), JSON.stringify(original, null, 2));
  const next = { ...original, ...productionSettingsPatch };
  await writeSettings(page, next);
  await pause(1200);
  const applied = await readSettings(page);
  fs.writeFileSync(path.join(runDir, 'settings-applied.json'), JSON.stringify(applied, null, 2));
  return original;
}

async function restoreSettings(page: Page, settings: Record<string, unknown> | null): Promise<void> {
  if (!settings) {
    return;
  }

  await writeSettings(page, settings);
}

async function claimMainBrowserAndAssert(page: Page): Promise<string | null> {
  return page.evaluate(async () => {
    function getCookie(name: string): string | null {
      const match = document.cookie.match(new RegExp(`(^| )${name}=([^;]+)`));
      const value = match?.[2];
      return value === undefined ? null : decodeURIComponent(value);
    }

    const clientId = getCookie('mt-client-id');
    const tabId = window.sessionStorage.getItem('mt-tab-id');
    const browserId = clientId && tabId ? `${clientId}:${tabId}` : clientId;
    if (!browserId) {
      throw new Error('Cannot resolve MidTerm browser id for production capture.');
    }

    let lastError = '';
    for (let attempt = 0; attempt < 20; attempt += 1) {
      const response = await fetch('/api/browser/main', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ value: browserId }),
      });
      const text = await response.text();
      const payload = text
        ? (JSON.parse(text) as { success?: boolean; error?: string; result?: string })
        : { success: false, error: `empty response from POST /api/browser/main (${response.status})` };
      if (response.ok && payload.success === true) {
        return browserId;
      }

      lastError = payload.error ?? `POST /api/browser/main failed: ${response.status}`;
      await new Promise((resolve) => window.setTimeout(resolve, 500));
    }

    throw new Error(lastError || 'Timed out while claiming leading browser through /api/browser/main.');
  });
}

async function stabilizeMarketingSurface(page: Page): Promise<void> {
  const sidebarCollapse = page.locator('#btn-collapse-sidebar:visible').first();
  if (await sidebarCollapse.isVisible().catch(() => false)) {
    await sidebarCollapse.click({ force: true });
    await pause(800);
  }

  const scaledOverlay = page.locator('.scaled-overlay:visible').first();
  if (await scaledOverlay.isVisible().catch(() => false)) {
    await scaledOverlay.click({ force: true });
    await pause(900);
  }

  await page.addStyleTag({
    content: `
      html,
      body,
      #app,
      .app,
      .app-shell,
      .terminal-container,
      .session-wrapper,
      .xterm,
      .xterm-viewport,
      .xterm-screen {
        background-image: none !important;
      }

      body.marketing-capture-stage,
      body.marketing-capture-stage #app,
      body.marketing-capture-stage .app,
      body.marketing-capture-stage .app-shell {
        background: #05070b !important;
      }

      body.marketing-capture-stage .session-wrapper,
      body.marketing-capture-stage .terminal-container,
      body.marketing-capture-stage .xterm,
      body.marketing-capture-stage .xterm-viewport,
      body.marketing-capture-stage .xterm-screen {
        background: #05070b !important;
      }

      body.marketing-capture-stage .xterm-rows {
        opacity: 0.78 !important;
      }

      body.marketing-capture-stage .web-preview-tab,
      body.marketing-capture-stage #web-preview-url-input {
        font-size: 13px !important;
      }

      body.marketing-capture-stage [data-testid*="password" i],
      body.marketing-capture-stage [class*="password" i] {
        display: none !important;
      }

      #marketing-caption {
        max-width: calc(100vw - 72px);
      }
    `,
  });
  await page.evaluate(() => {
    document.body.classList.add('marketing-capture-stage');
    for (const element of document.querySelectorAll<HTMLElement>('body *')) {
      if (element.innerText?.includes('No password set.')) {
        element.style.display = 'none';
      }
    }
  });
}

async function selectDemoSession(page: Page): Promise<string | undefined> {
  const demoSessionId = process.env.MIDTERM_DEMO_SESSION_ID?.trim();
  if (demoSessionId) {
    await page.evaluate((sessionId) => {
      const selector = `.session-item[data-session-id="${CSS.escape(sessionId)}"]`;
      const el = document.querySelector<HTMLElement>(selector);
      el?.click();
    }, demoSessionId);
    await page
      .locator(`.session-wrapper[data-session-id="${demoSessionId}"]:visible`)
      .waitFor({ timeout: 5000 })
      .catch(() => undefined);
    await pause(1200);
    return demoSessionId;
  }

  const demoSession = page.getByText('MidTerm Demo Workspace', { exact: false }).first();
  if (await demoSession.isVisible().catch(() => false)) {
    await demoSession.click({ force: true });
    await pause(1200);
  }

  return page.evaluate(() => {
    const visibleWrapper = document.querySelector<HTMLElement>('.session-wrapper:not(.hidden)');
    return visibleWrapper?.dataset.sessionId;
  });
}

async function collectTerminalMetrics(page: Page, sessionId?: string): Promise<unknown> {
  return page.evaluate((targetSessionId) => {
    const wrapper = targetSessionId
      ? document.querySelector<HTMLElement>(`.session-wrapper[data-session-id="${CSS.escape(targetSessionId)}"]`)
      : document.querySelector<HTMLElement>('.session-wrapper:not(.hidden)');
    const screen = wrapper?.querySelector<HTMLElement>('.xterm-screen');
    const viewport = wrapper?.querySelector<HTMLElement>('.xterm-viewport');
    const overlay = wrapper?.querySelector<HTMLElement>('.scaled-overlay');
    const wrapperRect = wrapper?.getBoundingClientRect();
    const screenRect = screen?.getBoundingClientRect();
    const viewportRect = viewport?.getBoundingClientRect();
    return {
      sessionId: targetSessionId ?? wrapper?.dataset.sessionId ?? null,
      overlayText: overlay?.innerText ?? null,
      wrapper: wrapperRect
        ? { width: wrapperRect.width, height: wrapperRect.height, left: wrapperRect.left, top: wrapperRect.top }
        : null,
      screen: screenRect
        ? { width: screenRect.width, height: screenRect.height, left: screenRect.left, top: screenRect.top }
        : null,
      viewport: viewportRect
        ? { width: viewportRect.width, height: viewportRect.height, left: viewportRect.left, top: viewportRect.top }
        : null,
      bodyBackgroundImage: getComputedStyle(document.body).backgroundImage,
      bodyBackgroundColor: getComputedStyle(document.body).backgroundColor,
      appBackgroundImage: getComputedStyle(document.getElementById('app') ?? document.body).backgroundImage,
      appBackgroundColor: getComputedStyle(document.getElementById('app') ?? document.body).backgroundColor,
      stageClassPresent: document.body.classList.contains('marketing-capture-stage'),
    };
  }, sessionId);
}

async function assertProductionSurface(page: Page): Promise<void> {
  await expect(page.locator('.scaled-overlay:visible')).toHaveCount(0);
  await expect(page.getByText(/make this the reference scale browser|scaled content/i)).toHaveCount(0);
  await expect(page.locator('body')).toHaveClass(/marketing-capture-stage/);
  await expect(page.locator('body')).not.toHaveCSS('background-image', /url\(/);
}

async function writeAudit(runDir: string, name: string, evidence: AuditEvidence): Promise<void> {
  fs.writeFileSync(path.join(runDir, `${name}-audit.json`), JSON.stringify(evidence, null, 2));
}

async function getBrowserStatus(page: Page, sessionId: string | undefined): Promise<unknown> {
  return page.evaluate(async ({ targetSessionId }) => {
    const response = await fetch(
      `/api/browser/status?sessionId=${encodeURIComponent(targetSessionId ?? '')}&previewName=marketing`,
    );
    if (!response.ok) {
      throw new Error(`GET /api/browser/status failed: ${response.status}`);
    }
    return (await response.json()) as unknown;
  }, { targetSessionId: sessionId });
}

function browserStatusHasMainClient(status: unknown): boolean {
  return (
    (status as { clients?: Array<{ isMainBrowser?: boolean }> }).clients?.some(
      (client) => client.isMainBrowser === true,
    ) === true
  );
}

async function waitForMainBrowserPreviewClient(
  page: Page,
  sessionId: string | undefined,
): Promise<unknown> {
  let latest: unknown = null;
  for (let attempt = 0; attempt < 6; attempt += 1) {
    latest = await getBrowserStatus(page, sessionId);
    if (browserStatusHasMainClient(latest)) {
      return latest;
    }
    await claimMainBrowserAndAssert(page);
    await pause(1000);
  }
  return latest;
}

test.use({
  baseURL: process.env.MIDTERM_BASE_URL ?? 'https://localhost:2000',
  ignoreHTTPSErrors: true,
  viewport: verticalViewport,
  video: {
    mode: 'on',
    size: verticalViewport,
  },
});

test.setTimeout(180000);

test('vertical demo - workspace around the shell', async ({ page }) => {
  const runDir = path.join(outputBase, `run-${getNextRunNumber()}`);
  fs.mkdirSync(runDir, { recursive: true });
  let originalSettings: Record<string, unknown> | null = null;
  let browserId: string | null = null;

  try {
    await authenticate(page);
    await page.goto('/');
    await expect(page.locator('body')).toBeVisible();
    originalSettings = await applyProductionSettings(page, runDir);
    browserId = await claimMainBrowserAndAssert(page);
    await pause(1500);
    await stabilizeMarketingSurface(page);
    const sessionId = await selectDemoSession(page);
    await stabilizeMarketingSurface(page);
    await assertProductionSurface(page);

    await pause(1200);
    await showCaption(page, 'Persistent local shells in one browser workspace.');
    await pause(2200);
    await hideCaption(page);

    await showCaption(page, 'Sessions, files, git, previews, and agents stay attached.');
    await pause(2600);
    await hideCaption(page);

    const sessionItems = page.locator('.session-item:visible');
    const count = await sessionItems.count().catch(() => 0);
    if (!process.env.MIDTERM_DEMO_SESSION_ID?.trim() && count > 1) {
      await sessionItems.nth(0).click();
      await pause(900);
      await sessionItems.nth(1).click();
      await pause(900);
    }

    await showCaption(page, 'Local-first: your repo and shell stay on your machine.');
    await pause(2200);
    await hideCaption(page);
    await assertProductionSurface(page);

    const settings = await readSettings(page);
    const terminalMetrics = await collectTerminalMetrics(page, sessionId);
    await writeAudit(runDir, 'workspace-around-shell', {
      browserId,
      settings,
      terminalMetrics,
      checks: {
        backgroundImageDisabled: settings.backgroundImageEnabled === false,
        uiOpaque: settings.uiTransparency === 0,
        terminalOpaque: settings.terminalTransparency === 0,
        noVisibleScaleOverlay: (terminalMetrics as { overlayText?: string | null }).overlayText === null,
        marketingStageCssActive: (terminalMetrics as { stageClassPresent?: boolean }).stageClassPresent === true,
      },
    });

    await page.screenshot({ path: path.join(runDir, 'poster-workspace.png'), fullPage: false });
    test.info().annotations.push({ type: 'runDir', description: runDir });
    test.info().annotations.push({ type: 'artifactBase', description: 'workspace-around-shell' });
  } finally {
    await restoreSettings(page, originalSettings);
  }
});

test('vertical demo - dev browser validation loop', async ({ page }) => {
  const runDir = path.join(outputBase, `run-${getNextRunNumber()}`);
  fs.mkdirSync(runDir, { recursive: true });
  let originalSettings: Record<string, unknown> | null = null;
  let browserId: string | null = null;

  try {
    await authenticate(page);
    await page.goto('/');
    await expect(page.locator('body')).toBeVisible();
    originalSettings = await applyProductionSettings(page, runDir);
    browserId = await claimMainBrowserAndAssert(page);
    await pause(1500);
    const sessionId = await selectDemoSession(page);
    await stabilizeMarketingSurface(page);
    await assertProductionSurface(page);

    const demoUrl = process.env.MIDTERM_DEMO_URL?.trim() || 'https://example.com';
    await page.evaluate(
      async ({ targetSessionId, url }) => {
        if (!targetSessionId) {
          return;
        }

        await fetch('/api/browser/open', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json' },
          body: JSON.stringify({
            sessionId: targetSessionId,
            previewName: 'marketing',
            url,
            activateSession: true,
          }),
        });
      },
      { targetSessionId: sessionId, url: demoUrl },
    );
    await pause(1800);

    const browserButton = page.locator('.ide-bar-web:visible').first();
    if (await browserButton.isVisible().catch(() => false)) {
      await browserButton.click({ force: true });
      await pause(1600);
    }

    await expect(page.locator('#web-preview-dock:not(.hidden)')).toBeVisible({ timeout: 8000 });
    const demoHost = new URL(demoUrl).host;
    const previewTab = page
      .locator('.web-preview-tab')
      .filter({ hasText: demoHost })
      .first();
    if (await previewTab.isVisible().catch(() => false)) {
      await previewTab.click({ force: true });
      await pause(1800);
    }

    if ((await page.locator('.web-preview-iframe:not(.hidden)').count().catch(() => 0)) === 0) {
      await page.locator('#web-preview-url-input').fill(demoUrl);
      await page.locator('#web-preview-go').click({ force: true });
      await pause(2200);
    }

    await expect(page.locator('.web-preview-iframe:not(.hidden)')).toBeVisible({ timeout: 8000 });
    await stabilizeMarketingSurface(page);
    await assertProductionSurface(page);

    browserId = await claimMainBrowserAndAssert(page);
    const browserStatus = await waitForMainBrowserPreviewClient(page, sessionId);
    if (!browserStatusHasMainClient(browserStatus)) {
      await writeAudit(runDir, 'dev-browser-validation-failed-leading-browser', {
        browserId,
        browserStatus,
        terminalMetrics: await collectTerminalMetrics(page, sessionId),
        checks: {
          browserStatusHasMainClient: false,
        },
      });
    }
    expect(browserStatusHasMainClient(browserStatus)).toBe(true);

    await pause(1400);
    await showCaption(page, 'Dev Browser state is session-scoped and scriptable.');
    await pause(2400);
    await hideCaption(page);
    await pause(1600);
    await assertProductionSurface(page);

    const settings = await readSettings(page);
    const terminalMetrics = await collectTerminalMetrics(page, sessionId);
    await writeAudit(runDir, 'dev-browser-validation', {
      browserId,
      settings,
      browserStatus,
      terminalMetrics,
      checks: {
        backgroundImageDisabled: settings.backgroundImageEnabled === false,
        uiOpaque: settings.uiTransparency === 0,
        terminalOpaque: settings.terminalTransparency === 0,
        noVisibleScaleOverlay: (terminalMetrics as { overlayText?: string | null }).overlayText === null,
        marketingStageCssActive: (terminalMetrics as { stageClassPresent?: boolean }).stageClassPresent === true,
        browserStatusHasMainClient: browserStatusHasMainClient(browserStatus),
      },
    });

    await page.screenshot({ path: path.join(runDir, 'poster-dev-browser.png'), fullPage: false });
    test.info().annotations.push({ type: 'runDir', description: runDir });
    test.info().annotations.push({ type: 'artifactBase', description: 'dev-browser-validation' });
  } finally {
    await restoreSettings(page, originalSettings);
  }
});

test.afterEach(async ({ page }, testInfo) => {
  const runDir = testInfo.annotations.find((annotation) => annotation.type === 'runDir')?.description;
  const artifactBase =
    testInfo.annotations.find((annotation) => annotation.type === 'artifactBase')?.description ??
    'vertical-demo';
  const video = page.video();
  if (!runDir || !video) {
    return;
  }

  const destPath = path.join(runDir, `${artifactBase}.webm`);
  await page.close();
  await video.saveAs(destPath);
  console.log(`Vertical demo saved to: ${destPath}`);
});

import { test, expect, type Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

const outputBase = path.join(__dirname, '../output/mobile-feature-series');
const mobileViewport = { width: 390, height: 693 };
const repoRoot = path.resolve(__dirname, '../../../..');
const demoUrl = process.env.MIDTERM_DEMO_URL?.trim() || 'http://127.0.0.1:4177/';
const buildLoopScript = path.join(__dirname, '../scripts/demo-build-loop.ps1');
const agentConsoleScript = path.join(__dirname, '../scripts/demo-agent-console.ps1');

type SessionDto = {
  id: string;
  name?: string | null;
  surface?: string | null;
};

type CreateSessionResponse = SessionDto & {
  currentDirectory?: string | null;
};

type HistoryResponse = {
  id: string;
};

type Story = {
  slug: string;
  title: string;
  hook: string;
  payoff: string;
  focus: keyof DemoSessions;
  prepare?: (page: Page, sessions: DemoSessions) => Promise<void>;
  action: (page: Page, sessions: DemoSessions, runDir: string) => Promise<void>;
};

type DemoSessions = {
  terminal: string;
  agents: string;
  paste: string;
  build: string;
  browser: string;
  git: string;
};

let sharedRunDir: string | null = null;

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

function getSharedRunDir(): string {
  sharedRunDir ??= path.join(outputBase, `run-${getNextRunNumber()}`);
  fs.mkdirSync(sharedRunDir, { recursive: true });
  return sharedRunDir;
}

async function fetchJson<T>(
  page: Page,
  url: string,
  init?: { method?: string; body?: unknown },
): Promise<T> {
  return page.evaluate(
    async ({ targetUrl, requestInit }) => {
      const response = await fetch(targetUrl, {
        method: requestInit.method,
        headers: requestInit.body === undefined ? undefined : { 'Content-Type': 'application/json' },
        body: requestInit.body === undefined ? undefined : JSON.stringify(requestInit.body),
      });
      const text = await response.text();
      if (!response.ok) {
        throw new Error(`${requestInit.method ?? 'GET'} ${targetUrl} failed: ${response.status} ${text}`);
      }
      if (!text) {
        return {};
      }
      try {
        return JSON.parse(text) as unknown;
      } catch {
        return { text };
      }
    },
    { targetUrl: url, requestInit: init ?? {} },
  ) as Promise<T>;
}

async function authenticate(page: Page): Promise<void> {
  const token = process.env.MT_COOKIE?.match(/mm-session=([^;\s]+)/)?.[1] ?? process.env.MT_TOKEN;
  if (!token) {
    return;
  }

  const baseUrl = process.env.MIDTERM_BASE_URL ?? 'https://127.0.0.1:2100';
  await page.context().addCookies([
    {
      name: 'mm-session',
      value: token,
      domain: new URL(baseUrl).hostname,
      path: '/',
      httpOnly: true,
      secure: true,
      sameSite: 'Lax',
    },
  ]);
}

async function pause(ms: number): Promise<void> {
  await new Promise((resolve) => setTimeout(resolve, ms));
}

async function showCaption(page: Page, eyebrow: string, text: string): Promise<void> {
  await page.evaluate(
    ({ captionEyebrow, captionText }) => {
      let el = document.getElementById('mobile-marketing-caption');
      if (!el) {
        el = document.createElement('div');
        el.id = 'mobile-marketing-caption';
        el.innerHTML = '<div class="eyebrow"></div><div class="text"></div>';
        el.style.position = 'fixed';
        el.style.left = '12px';
        el.style.right = '12px';
        el.style.bottom = '104px';
        el.style.zIndex = '999999';
        el.style.padding = '12px 14px';
        el.style.borderRadius = '10px';
        el.style.background = 'rgba(5, 8, 14, 0.94)';
        el.style.border = '1px solid rgba(255,255,255,0.16)';
        el.style.boxShadow = '0 12px 30px rgba(0,0,0,0.38)';
        el.style.pointerEvents = 'none';
        document.body.appendChild(el);
      }
      const eyebrowEl = el.querySelector<HTMLElement>('.eyebrow');
      const textEl = el.querySelector<HTMLElement>('.text');
      if (eyebrowEl) {
        eyebrowEl.textContent = captionEyebrow;
        eyebrowEl.style.color = '#4cc9ff';
        eyebrowEl.style.font = '800 11px/1 Inter, system-ui, sans-serif';
        eyebrowEl.style.letterSpacing = '0';
        eyebrowEl.style.textTransform = 'uppercase';
        eyebrowEl.style.marginBottom = '6px';
      }
      if (textEl) {
        textEl.textContent = captionText;
        textEl.style.color = '#fff';
        textEl.style.font = '800 17px/1.2 Inter, system-ui, sans-serif';
        textEl.style.letterSpacing = '0';
      }
    },
    { captionEyebrow: eyebrow, captionText: text },
  );
}

async function hideCaption(page: Page): Promise<void> {
  await page.evaluate(() => document.getElementById('mobile-marketing-caption')?.remove());
}

async function stabilizeSurface(page: Page): Promise<void> {
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

      body.mobile-marketing-capture [data-testid*="password" i],
      body.mobile-marketing-capture [class*="password" i],
      body.mobile-marketing-capture .agent-history-placeholder-chip,
      body.mobile-marketing-capture .smart-input-attachment-chip {
        display: none !important;
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

async function applyCaptureSettings(page: Page, runDir: string): Promise<Record<string, unknown>> {
  const original = await fetchJson<Record<string, unknown>>(page, '/api/settings');
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
    // WebGL renderer paints a black canvas under Playwright mobile emulation
    // at 9.17.35-dev (no automatic fallback); force the DOM renderer.
    useWebGL: false,
    showBookmarks: true,
    allowAdHocSessionBookmarks: true,
    fontSize: 17,
    lineHeight: 1.08,
    terminalThemeLightnessBoost: 8,
  };
  await fetchJson<Record<string, unknown>>(page, '/api/settings', { method: 'PUT', body: next });
  await pause(900);
  fs.writeFileSync(
    path.join(runDir, 'settings-applied.json'),
    JSON.stringify(await fetchJson<Record<string, unknown>>(page, '/api/settings'), null, 2),
  );
  return original;
}

async function restoreSettings(page: Page, settings: Record<string, unknown> | null): Promise<void> {
  if (settings) {
    await fetchJson<Record<string, unknown>>(page, '/api/settings', { method: 'PUT', body: settings });
  }
}

async function claimRecordingBrowser(page: Page): Promise<string> {
  const browserId = await page.waitForFunction(
    () => {
      const clientId = document.cookie.match(/(?:^|;\s*)mt-client-id=([^;]+)/)?.[1];
      const tabId = sessionStorage.getItem('mt-tab-id');
      if (!clientId) {
        return '';
      }
      return tabId ? `${decodeURIComponent(clientId)}:${tabId}` : decodeURIComponent(clientId);
    },
    undefined,
    { timeout: 10000 },
  );
  const id = await browserId.jsonValue();
  if (!id) {
    throw new Error('Cannot resolve recording browser id.');
  }
  await fetchJson<Record<string, unknown>>(page, '/api/browser/main', {
    method: 'POST',
    body: { value: id },
  });
  await pause(700);
  return id;
}

async function createSession(
  page: Page,
  name: string,
  command: string,
  surface = 'marketing-mobile-series',
): Promise<string> {
  const created = await fetchJson<CreateSessionResponse>(page, '/api/sessions', {
    method: 'POST',
    body: {
      shell: 'Pwsh',
      workingDirectory: repoRoot,
      cols: 46,
      rows: 30,
      surface,
    },
  });
  await fetchJson<Record<string, unknown>>(page, `/api/sessions/${created.id}/name`, {
    method: 'PUT',
    body: { name },
  });
  await fetchJson<Record<string, unknown>>(page, `/api/sessions/${created.id}/topic`, {
    method: 'PUT',
    body: { topic: 'Mobile feature series recording' },
  });
  await pause(350);
  await sendText(page, created.id, command, true);
  return created.id;
}

async function sendText(page: Page, sessionId: string, text: string, appendNewline: boolean): Promise<void> {
  await fetchJson<Record<string, unknown>>(page, `/api/sessions/${sessionId}/input/text`, {
    method: 'POST',
    body: { text, appendNewline },
  });
}

async function pasteText(page: Page, sessionId: string, text: string, bracketedPaste = false): Promise<void> {
  await fetchJson<Record<string, unknown>>(page, `/api/sessions/${sessionId}/input/paste`, {
    method: 'POST',
    body: { text, bracketedPaste },
  });
}

async function sendKeys(page: Page, sessionId: string, keys: string[], literal = false): Promise<void> {
  await fetchJson<Record<string, unknown>>(page, `/api/sessions/${sessionId}/input/keys`, {
    method: 'POST',
    body: { keys, literal },
  });
}

async function createBookmark(page: Page, label: string, commandLine: string): Promise<string> {
  const response = await fetchJson<HistoryResponse>(page, '/api/history', {
    method: 'POST',
    body: {
      shellType: 'Pwsh',
      executable: label.toLowerCase().replaceAll(' ', '-'),
      commandLine,
      workingDirectory: repoRoot,
      dedupeKey: `marketing-mobile-${label}`,
      isStarred: true,
      label,
      notes: 'Marketing recording fixture',
      launchMode: 'terminal',
      launchOrigin: 'adHoc',
      surfaceType: 'trm',
      foregroundProcessName: 'pwsh',
      foregroundProcessDisplayName: label,
    },
  });
  return response.id;
}

async function ensureDemoSessions(page: Page): Promise<DemoSessions> {
  const sessions = await fetchJson<{ sessions?: SessionDto[] }>(page, '/api/sessions');
  const staleMarketingSessions = (sessions.sessions ?? []).filter(
    (session) => session.surface === 'marketing-mobile-series',
  );
  for (const session of staleMarketingSessions) {
    await fetchJson<Record<string, unknown>>(page, `/api/sessions/${session.id}`, { method: 'DELETE' }).catch(
      () => ({}),
    );
  }
  await pause(staleMarketingSessions.length > 0 ? 700 : 0);

  const defs = {
    terminal: {
      name: 'MT Mobile - Terminal',
      command:
        "Clear-Host; Write-Host 'Real local PTY' -ForegroundColor Green; Write-Host 'phone surface, local shell' -ForegroundColor DarkGray; Write-Host ''; git log --oneline -6; Write-Host ''; Get-Date -Format 'HH:mm:ss'",
    },
    agents: {
      name: 'MT Mobile - Agents',
      command: `pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass -File "${agentConsoleScript}"`,
    },
    paste: {
      name: 'MT Mobile - Paste',
      command:
        "Clear-Host; Write-Host 'Paste target ready' -ForegroundColor Yellow; Write-Host 'waiting for structured text...' -ForegroundColor DarkGray",
    },
    build: {
      name: 'MT Mobile - Build loop',
      command: `pwsh -NoLogo -NoProfile -ExecutionPolicy Bypass -File "${buildLoopScript}"`,
    },
    browser: {
      name: 'MT Mobile - Dev Browser',
      command:
        "Clear-Host; Write-Host 'Dev Browser session' -ForegroundColor Cyan; Write-Host 'preview: 127.0.0.1:4177' -ForegroundColor DarkGray",
    },
    git: {
      name: 'MT Mobile - Git context',
      command:
        "Clear-Host; Write-Host 'Repo context' -ForegroundColor Green; Write-Host ''; git log --oneline -8; Write-Host ''; git branch --show-current",
    },
  } as const;

  const result: Partial<DemoSessions> = {};
  for (const [key, def] of Object.entries(defs) as Array<[keyof DemoSessions, (typeof defs)[keyof typeof defs]]>) {
    result[key] = await createSession(page, def.name, def.command);
  }

  await createBookmark(page, 'tlbx demo workspace', 'pwsh -NoLogo');
  await createBookmark(page, 'Build loop fixture', 'pwsh -File docs\\marketing\\ScreenshotAutomation\\scripts\\demo-build-loop.ps1');

  return result as DemoSessions;
}

async function hideNonDemoSidebarSessions(page: Page, sessionIds: string[]): Promise<void> {
  await page.evaluate((ids) => {
    const keep = new Set(ids);
    const applyFilter = () => {
      for (const item of document.querySelectorAll<HTMLElement>('.session-item[data-session-id]')) {
        const id = item.dataset.sessionId ?? '';
        const text = item.innerText ?? '';
        item.style.display = keep.has(id) || text.includes('MT Mobile -') ? '' : 'none';
      }

      for (const item of document.querySelectorAll<HTMLElement>('.history-item')) {
        const text = item.innerText ?? '';
        if (text.includes('tlbx demo workspace') || text.includes('Build loop fixture')) {
          item.style.display = '';
        } else if (text.includes('JPA') || text.includes('Q:\\repos') || text.includes('commit and push')) {
          item.style.display = 'none';
        }
      }
    };

    applyFilter();

    const w = window as Window & typeof globalThis & { __mobileMarketingSidebarFilter?: number };
    if (w.__mobileMarketingSidebarFilter !== undefined) {
      window.clearInterval(w.__mobileMarketingSidebarFilter);
    }
    w.__mobileMarketingSidebarFilter = window.setInterval(applyFilter, 250);
  }, sessionIds);
}

async function waitForDemoSessionItemsRendered(page: Page, sessionIds: string[]): Promise<void> {
  await openSidebar(page);
  await page.waitForFunction(
    (ids) =>
      ids.every(
        (id) => document.querySelector(`.session-item[data-session-id="${CSS.escape(id)}"]`),
      ),
    sessionIds,
    { timeout: 12000 },
  );
  await closeSidebar(page);
}

async function openSidebar(page: Page): Promise<void> {
  const open = await page.locator('.terminal-page.sidebar-open').count();
  if (open === 0) {
    await page.locator('#btn-hamburger').click({ force: true });
    await expect(page.locator('.terminal-page.sidebar-open')).toBeVisible({ timeout: 5000 });
  }
  await pause(700);
}

async function closeSidebar(page: Page): Promise<void> {
  await page.evaluate(() => document.querySelector<HTMLElement>('.sidebar-overlay')?.click());
  await pause(500);
}

const terminalContentWaits: Array<{ sessionId: string; scaledOverlayClicked: boolean }> = [];

async function claimTerminalScale(page: Page, sessionId: string): Promise<boolean> {
  const overlay = page
    .locator(`.session-wrapper[data-session-id="${sessionId}"] .scaled-overlay`)
    .first();
  if (!(await overlay.isVisible().catch(() => false))) {
    return false;
  }
  await overlay.click({ force: true }).catch(() => {});
  await pause(1100);
  return true;
}

async function selectSession(page: Page, sessionId: string): Promise<void> {
  await openSidebar(page);
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
  await closeSidebar(page);
  // API-seeded sessions start at a PTY size that does not match the phone
  // viewport; tlbx then waits for a manual "resize to this viewport" tap
  // before rendering. Click the overlay so the terminal fits and renders.
  const scaledOverlayClicked = await claimTerminalScale(page, sessionId);
  terminalContentWaits.push({ sessionId, scaledOverlayClicked });
  await pause(700);
}

async function openBookmarks(page: Page): Promise<boolean> {
  await openSidebar(page);
  const button = page.locator('#btn-bookmarks');
  if (!(await button.isVisible().catch(() => false))) {
    return false;
  }
  await button.click({ force: true });
  await page.locator('.history-dropdown, .history-entry-list, .history-item').first().waitFor({ timeout: 5000 });
  await page.evaluate(() => {
    const allowed = ['tlbx demo workspace', 'Build loop fixture'];
    for (const item of document.querySelectorAll<HTMLElement>('.history-item')) {
      const text = item.innerText ?? '';
      item.style.display = allowed.some((label) => text.includes(label)) ? '' : 'none';
    }
  });
  await pause(1200);
  return true;
}

async function bindGitRepo(page: Page, sessionId: string): Promise<void> {
  await fetchJson<Record<string, unknown>>(page, '/api/git/repos', {
    method: 'POST',
    body: {
      sessionId,
      path: repoRoot,
      role: 'target',
      label: 'tlbx',
    },
  }).catch(() => ({}));
  await fetchJson<Record<string, unknown>>(page, '/api/git/repos/refresh', {
    method: 'POST',
    body: { sessionId },
  }).catch(() => ({}));
  await pause(1200);
}

async function openDevBrowser(page: Page, sessionId: string): Promise<void> {
  await fetchJson<Record<string, unknown>>(page, '/api/webpreview/target', {
    method: 'PUT',
    body: {
      sessionId,
      previewName: 'default',
      url: demoUrl,
    },
  });
  await selectSession(page, sessionId);
  await page.locator('#btn-mobile-actions-menu').click({ force: true });
  await pause(400);
  await page.evaluate(() => document.getElementById('btn-mobile-web')?.click());
  await expect(page.locator('#web-preview-dock:not(.hidden)')).toBeVisible({ timeout: 8000 });
  await page.locator('#web-preview-url-input').fill(demoUrl);
  await page.locator('#web-preview-go').click({ force: true });
  await expect(page.locator('.web-preview-iframe:not(.hidden)')).toBeVisible({ timeout: 10000 });
  await pause(1500);
}

async function captureStory(page: Page, story: Story, runDir: string, videoStartMs: number): Promise<void> {
  const originalSettings = await applyCaptureSettings(page, runDir);
  try {
    await stabilizeSurface(page);
    const sessions = await ensureDemoSessions(page);
    await page.reload({ waitUntil: 'domcontentloaded' });
    await expect(page.locator('#mobile-topbar')).toBeVisible({ timeout: 10000 });
    await stabilizeSurface(page);
    const recordingBrowserId = await claimRecordingBrowser(page);
    await waitForDemoSessionItemsRendered(page, Object.values(sessions));
    await hideNonDemoSidebarSessions(page, Object.values(sessions));
    await selectSession(page, sessions[story.focus]);
    if (story.prepare) {
      await story.prepare(page, sessions);
    }
    const hookAtSec = (Date.now() - videoStartMs) / 1000;
    await showCaption(page, story.title, story.hook);
    await pause(1900);
    await story.action(page, sessions, runDir);
    const payoffAtSec = (Date.now() - videoStartMs) / 1000;
    await showCaption(page, story.title, story.payoff);
    await pause(2300);
    const endAtSec = (Date.now() - videoStartMs) / 1000;
    await hideCaption(page);
    const renderDiag = await page.evaluate((id) => {
      const wrapper = document.querySelector<HTMLElement>(
        `.session-wrapper[data-session-id="${CSS.escape(id)}"]`,
      );
      const container = wrapper?.querySelector<HTMLElement>('.terminal-container');
      const xterm = wrapper?.querySelector<HTMLElement>('.xterm');
      const rows = wrapper?.querySelector<HTMLElement>('.xterm-rows');
      const firstGlyph = rows?.querySelector<HTMLElement>('span');
      const styleOf = (el: HTMLElement | null | undefined) => {
        if (!el) return null;
        const cs = getComputedStyle(el);
        const rect = el.getBoundingClientRect();
        return {
          display: cs.display,
          visibility: cs.visibility,
          opacity: cs.opacity,
          color: cs.color,
          transform: cs.transform,
          rect: { x: Math.round(rect.x), y: Math.round(rect.y), w: Math.round(rect.width), h: Math.round(rect.height) },
        };
      };
      return {
        wrapperClass: wrapper?.className ?? null,
        containerClass: container?.className ?? null,
        canvasCount: wrapper?.querySelectorAll('canvas').length ?? null,
        rowsChildCount: rows?.childElementCount ?? null,
        rowsTextLength: (rows?.textContent ?? '').trim().length,
        wrapperStyle: styleOf(wrapper),
        containerStyle: styleOf(container),
        xtermStyle: styleOf(xterm),
        rowsStyle: styleOf(rows),
        firstGlyphStyle: styleOf(firstGlyph),
      };
    }, sessions[story.focus]);
    fs.writeFileSync(
      path.join(runDir, `${story.slug}-render-diag.json`),
      JSON.stringify(renderDiag, null, 2),
    );
    await page.screenshot({ path: path.join(runDir, `${story.slug}-poster.png`), fullPage: false });
    fs.writeFileSync(
      path.join(runDir, `${story.slug}-audit.json`),
      JSON.stringify(
        {
          slug: story.slug,
          title: story.title,
          hook: story.hook,
          payoff: story.payoff,
          focusSessionId: sessions[story.focus],
          recordingBrowserId,
          viewport: mobileViewport,
          timeline: {
            hookAtSec: Math.round(hookAtSec * 10) / 10,
            payoffAtSec: Math.round(payoffAtSec * 10) / 10,
            endAtSec: Math.round(endAtSec * 10) / 10,
          },
          terminalContentWaits: terminalContentWaits.splice(0),
          checks: {
            bodyMarkedForCapture: await page.evaluate(() =>
              document.body.classList.contains('mobile-marketing-capture'),
            ),
            mobileTopbarVisible: await page.locator('#mobile-topbar').isVisible().catch(() => false),
            commandBayVisible: await page
              .locator('.adaptive-footer-dock')
              .isVisible()
              .catch(() => false),
            visibleSession: await page
              .locator(`.session-wrapper[data-session-id="${sessions[story.focus]}"]:not(.hidden)`)
              .isVisible()
              .catch(() => false),
          },
        },
        null,
        2,
      ),
    );
  } finally {
    await restoreSettings(page, originalSettings);
  }
}

const stories: Story[] = [
  {
    slug: '01-pocket-terminal',
    title: 'Pocket terminal',
    hook: 'Your terminal. On your phone.',
    payoff: 'Real local PTY, phone browser surface.',
    focus: 'terminal',
    action: async (page, sessions) => {
      await sendText(page, sessions.terminal, 'Get-ChildItem -Name | Select-Object -First 7', true);
      await pause(1700);
    },
  },
  {
    slug: '02-agents-on-the-go',
    title: 'Agents on the go',
    hook: 'Check your agents from anywhere.',
    payoff: 'AI tools and shells stay visible.',
    focus: 'agents',
    action: async (page) => {
      await pause(3200);
    },
  },
  {
    slug: '03-session-switching',
    title: 'Session switching',
    hook: 'Every shell, one swipe away.',
    payoff: 'Live sessions in the phone sidebar.',
    focus: 'terminal',
    action: async (page, sessions) => {
      await openSidebar(page);
      await pause(1300);
      await selectSession(page, sessions.build);
      await pause(1000);
      await selectSession(page, sessions.agents);
      await pause(800);
    },
  },
  {
    slug: '04-real-paste',
    title: 'Real paste',
    hook: 'Paste should stay exact - on glass too.',
    payoff: 'Multiline text lands as text.',
    focus: 'paste',
    action: async (page, sessions) => {
      await pasteText(page, sessions.paste, "@'\nPlan\n- line one\n- line two\n  - indented\n'@");
      await pause(500);
      await sendKeys(page, sessions.paste, ['Enter']);
      await pause(1900);
    },
  },
  {
    slug: '05-mobile-dev-browser',
    title: 'Dev Browser',
    hook: 'Preview beside the shell.',
    payoff: 'Session-scoped browser on the phone.',
    focus: 'browser',
    prepare: async (page, sessions) => {
      await openDevBrowser(page, sessions.browser);
    },
    action: async (page, sessions) => {
      await sendText(page, sessions.browser, "Write-Host 'preview target live' -ForegroundColor Green", true);
      await pause(900);
      await page.locator('#web-preview-go').click({ force: true });
      await pause(2100);
    },
  },
  {
    slug: '06-bookmarks',
    title: 'Bookmarks',
    hook: 'Pinned shells, one tap.',
    payoff: 'Recurring work stays one tap away.',
    focus: 'terminal',
    action: async (page, sessions, runDir) => {
      const opened = await openBookmarks(page);
      fs.writeFileSync(
        path.join(runDir, '06-bookmarks-surface.json'),
        JSON.stringify({ bookmarksButtonVisibleOnMobile: opened }, null, 2),
      );
      await pause(1900);
    },
  },
  {
    slug: '07-files-git-context',
    title: 'Git context',
    hook: 'Repo state in your pocket.',
    payoff: 'Git context stays next to the shell.',
    focus: 'git',
    action: async (page, sessions) => {
      await bindGitRepo(page, sessions.git);
      await openSidebar(page);
      await pause(2000);
    },
  },
  {
    slug: '08-live-build',
    title: 'Live output',
    hook: 'Long jobs stay visible.',
    payoff: 'Output keeps streaming on mobile.',
    focus: 'build',
    action: async (page) => {
      await pause(4200);
    },
  },
];

test.use({
  baseURL: process.env.MIDTERM_BASE_URL ?? 'https://127.0.0.1:2100',
  ignoreHTTPSErrors: true,
  viewport: mobileViewport,
  isMobile: true,
  hasTouch: true,
  deviceScaleFactor: 3,
  video: process.env.MT_CAPTURE_NO_VIDEO
    ? 'off'
    : {
        mode: 'on',
        size: mobileViewport,
      },
  launchOptions: {
    slowMo: process.env.MT_CAPTURE_NO_SLOWMO ? 0 : 100,
  },
});

test.setTimeout(210000);

for (const story of stories) {
  test(`mobile feature series - ${story.slug}`, async ({ page }) => {
    const runDir = getSharedRunDir();
    test.info().annotations.push({ type: 'runDir', description: runDir });
    test.info().annotations.push({ type: 'artifactBase', description: story.slug });
    const videoStartMs = Date.now();
    await authenticate(page);
    await page.goto('/');
    await expect(page.locator('body')).toBeVisible({ timeout: 10000 });
    await captureStory(page, story, runDir, videoStartMs);
  });
}

test.afterEach(async ({ page }, testInfo) => {
  const runDir = testInfo.annotations.find((annotation) => annotation.type === 'runDir')?.description;
  const artifactBase =
    testInfo.annotations.find((annotation) => annotation.type === 'artifactBase')?.description ?? 'mobile-feature';
  const video = page.video();
  if (!runDir || !video) {
    return;
  }

  const destPath = path.join(runDir, `${artifactBase}.webm`);
  await page.close();
  await video.saveAs(destPath);
  console.log(`Mobile feature clip saved to: ${destPath}`);
});

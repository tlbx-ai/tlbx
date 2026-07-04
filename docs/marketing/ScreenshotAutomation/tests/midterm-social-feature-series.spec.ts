import { test, expect, type Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

const outputBase = path.join(__dirname, '../output/social-feature-series');
const landscapeViewport = { width: 1920, height: 1080 };
const repoRoot = path.resolve(__dirname, '../../../..');
const demoUrl = process.env.MIDTERM_DEMO_URL?.trim() || 'http://127.0.0.1:4177/';

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
  action: (page: Page, sessions: DemoSessions, runDir: string) => Promise<void>;
};

type DemoSessions = {
  adhoc: string;
  terminal: string;
  paste: string;
  files: string;
  agents: string;
  console: string;
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

  const baseUrl = process.env.MIDTERM_BASE_URL ?? 'https://localhost:2000';
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
      let el = document.getElementById('social-marketing-caption');
      if (!el) {
        el = document.createElement('div');
        el.id = 'social-marketing-caption';
        el.innerHTML = '<div class="eyebrow"></div><div class="text"></div>';
        el.style.position = 'fixed';
        el.style.left = '56px';
        el.style.right = 'auto';
        el.style.bottom = '48px';
        el.style.maxWidth = '720px';
        el.style.zIndex = '999999';
        el.style.padding = '18px 22px';
        el.style.borderRadius = '12px';
        el.style.background = 'rgba(5, 8, 14, 0.94)';
        el.style.border = '1px solid rgba(255,255,255,0.16)';
        el.style.boxShadow = '0 18px 42px rgba(0,0,0,0.38)';
        el.style.pointerEvents = 'none';
        document.body.appendChild(el);
      }
      const eyebrowEl = el.querySelector<HTMLElement>('.eyebrow');
      const textEl = el.querySelector<HTMLElement>('.text');
      if (eyebrowEl) {
        eyebrowEl.textContent = captionEyebrow;
        eyebrowEl.style.color = '#4cc9ff';
        eyebrowEl.style.font = '800 15px/1 Inter, system-ui, sans-serif';
        eyebrowEl.style.letterSpacing = '0';
        eyebrowEl.style.textTransform = 'uppercase';
        eyebrowEl.style.marginBottom = '9px';
      }
      if (textEl) {
        textEl.textContent = captionText;
        textEl.style.color = '#fff';
        textEl.style.font = '800 30px/1.1 Inter, system-ui, sans-serif';
        textEl.style.letterSpacing = '0';
      }
    },
    { captionEyebrow: eyebrow, captionText: text },
  );
}

async function hideCaption(page: Page): Promise<void> {
  await page.evaluate(() => document.getElementById('social-marketing-caption')?.remove());
}

async function stabilizeSurface(page: Page): Promise<void> {
  await page.addStyleTag({
    content: `
      body.social-marketing-capture,
      body.social-marketing-capture #app,
      body.social-marketing-capture .app,
      body.social-marketing-capture .app-shell {
        background: #05070b !important;
      }

      body.social-marketing-capture .session-wrapper,
      body.social-marketing-capture .terminal-container,
      body.social-marketing-capture .xterm,
      body.social-marketing-capture .xterm-viewport,
      body.social-marketing-capture .xterm-screen {
        background: #05070b !important;
      }

      body.social-marketing-capture .xterm-rows {
        opacity: 0.9 !important;
      }

      body.social-marketing-capture [data-testid*="password" i],
      body.social-marketing-capture [class*="password" i],
      body.social-marketing-capture .security-warning,
      body.social-marketing-capture .scaled-overlay,
      body.social-marketing-capture .git-repo-chip,
      body.social-marketing-capture .manager-bar,
      body.social-marketing-capture .manager-bar-buttons,
      body.social-marketing-capture .agent-history-placeholder-chip,
      body.social-marketing-capture .smart-input-attachment-chip {
        display: none !important;
      }
    `,
  });
  await page.evaluate(() => document.body.classList.add('social-marketing-capture'));
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
    uiTransparency: 0,
    terminalTransparency: 0,
    terminalCellBackgroundTransparency: 0,
    showBookmarks: true,
    allowAdHocSessionBookmarks: true,
    fontSize: 18,
    lineHeight: 1.08,
    terminalThemeLightnessBoost: 10,
  };
  await fetchJson<Record<string, unknown>>(page, '/api/settings', { method: 'PUT', body: next });
  await pause(800);
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
  surface = 'marketing-social-series',
): Promise<string> {
  const created = await fetchJson<CreateSessionResponse>(page, '/api/sessions', {
    method: 'POST',
    body: {
      shell: 'Pwsh',
      workingDirectory: repoRoot,
      cols: 96,
      rows: 36,
      surface,
    },
  });
  await fetchJson<Record<string, unknown>>(page, `/api/sessions/${created.id}/name`, {
    method: 'PUT',
    body: { name },
  });
  await fetchJson<Record<string, unknown>>(page, `/api/sessions/${created.id}/topic`, {
    method: 'PUT',
    body: { topic: 'Social feature series recording' },
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
      dedupeKey: `marketing-social-${label}`,
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
    (session) => session.surface === 'marketing-social-series',
  );
  for (const session of staleMarketingSessions) {
    await fetchJson<Record<string, unknown>>(page, `/api/sessions/${session.id}`, { method: 'DELETE' }).catch(
      () => ({}),
    );
  }
  await pause(staleMarketingSessions.length > 0 ? 700 : 0);

  const defs = {
    adhoc: {
      name: 'MT Social - Ad-hoc shell',
      command:
        "Clear-Host; Write-Host 'New ad-hoc shell' -ForegroundColor Cyan; Write-Host 'created inside MidTerm'; Get-Location",
    },
    terminal: {
      name: 'MT Social - Web terminal',
      command:
        "Clear-Host; Write-Host 'Real local PTY' -ForegroundColor Green; Write-Host 'browser tab can reconnect'; Write-Host ''; Get-Date",
    },
    paste: {
      name: 'MT Social - Paste target',
      command:
        "Clear-Host; Write-Host 'Paste target ready' -ForegroundColor Yellow; Write-Host 'waiting for structured multiline text...'",
    },
    files: {
      name: 'MT Social - File Radar',
      command:
        "Clear-Host; Write-Host 'File Radar demo' -ForegroundColor Cyan; Write-Host 'docs\\marketing\\features.md:1'; Write-Host 'src\\Ai.Tlbx.MidTerm\\Program.cs:1'",
    },
    agents: {
      name: 'MT Social - Agents',
      command:
        "Clear-Host; Write-Host 'Agent supervision' -ForegroundColor Magenta; Write-Host 'codex   running in local repo'; Write-Host 'claude  ready for review'; Write-Host 'grok    waiting for prompt'",
    },
    console: {
      name: 'MT Social - Console work',
      command:
        "Clear-Host; Write-Host 'Regular console work' -ForegroundColor White; Write-Host 'build loop, tests, logs, release scripts'; Write-Host ''; dir docs\\marketing | Select-Object -First 8",
    },
    browser: {
      name: 'MT Social - Dev Browser',
      command:
        "Clear-Host; Write-Host 'Dev Browser session' -ForegroundColor Cyan; Write-Host 'Preview target: http://127.0.0.1:4177/'",
    },
    git: {
      name: 'MT Social - Files and Git',
      command:
        "Clear-Host; Write-Host 'Files + Git context' -ForegroundColor Green; git status --short --branch",
    },
  } as const;

  const result: Partial<DemoSessions> = {};
  for (const [key, def] of Object.entries(defs) as Array<[keyof DemoSessions, (typeof defs)[keyof typeof defs]]>) {
    result[key] = await createSession(page, def.name, def.command);
  }

  await createBookmark(page, 'MidTerm demo workspace', 'pwsh -NoLogo');
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
        item.style.display = keep.has(id) || text.includes('MT Social -') ? '' : 'none';
      }

      for (const item of document.querySelectorAll<HTMLElement>('.history-item')) {
        const text = item.innerText ?? '';
        if (text.includes('MidTerm demo workspace') || text.includes('Build loop fixture')) {
          item.style.display = '';
        } else if (text.includes('JPA') || text.includes('Q:\\repos') || text.includes('commit and push')) {
          item.style.display = 'none';
        }
      }

      for (const item of document.querySelectorAll<HTMLElement>('.manager-btn, .git-command-row, .git-repo-chip')) {
        const text = item.innerText ?? item.textContent ?? '';
        if (text.includes('Q:\\repos') || text.includes('commit and push') || text.includes('Jpa')) {
          item.style.display = 'none';
        }
      }
    };

    applyFilter();

    const w = window as Window & typeof globalThis & { __socialMarketingSidebarFilter?: number };
    if (w.__socialMarketingSidebarFilter !== undefined) {
      window.clearInterval(w.__socialMarketingSidebarFilter);
    }
    w.__socialMarketingSidebarFilter = window.setInterval(applyFilter, 250);
  }, sessionIds);
}

async function waitForDemoSessionItemsRendered(page: Page, sessionIds: string[]): Promise<void> {
  await page.waitForFunction(
    (ids) =>
      ids.every(
        (id) => document.querySelector(`.session-item[data-session-id="${CSS.escape(id)}"]`),
      ),
    sessionIds,
    { timeout: 12000 },
  );
}

async function selectSession(page: Page, sessionId: string): Promise<void> {
  const item = page.locator(`.session-item[data-session-id="${sessionId}"]`).first();
  await item.waitFor({ state: 'visible', timeout: 10000 });
  await page.evaluate((id) => {
    const target = document.querySelector<HTMLElement>(`.session-item[data-session-id="${CSS.escape(id)}"]`);
    if (!target) {
      throw new Error(`Session item ${id} is not rendered.`);
    }
    target.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, pointerType: 'mouse' }));
    target.dispatchEvent(new MouseEvent('click', { bubbles: true, button: 0 }));
  }, sessionId);
  await page.locator(`.session-wrapper[data-session-id="${sessionId}"]:not(.hidden)`).waitFor({
    timeout: 8000,
  });
  await pause(900);
}

async function openBookmarks(page: Page): Promise<void> {
  await page.locator('#btn-bookmarks').click({ force: true });
  await page.locator('.history-dropdown, .history-entry-list, .history-item').first().waitFor({ timeout: 5000 });
  await page.evaluate(() => {
    const allowed = ['MidTerm demo workspace', 'Build loop fixture'];
    for (const item of document.querySelectorAll<HTMLElement>('.history-item')) {
      const text = item.innerText ?? '';
      item.style.display = allowed.some((label) => text.includes(label)) ? '' : 'none';
    }
  });
  await pause(1200);
}

async function openFiles(page: Page): Promise<void> {
  const filesTab = page.getByText('Files', { exact: true }).first();
  if (await filesTab.isVisible().catch(() => false)) {
    await filesTab.click({ force: true });
    await pause(1300);
  }
}

async function openTerminal(page: Page): Promise<void> {
  const terminalTab = page.getByText('Terminal', { exact: true }).first();
  if (await terminalTab.isVisible().catch(() => false)) {
    await terminalTab.click({ force: true });
    await pause(700);
  }
}

async function openDevBrowser(page: Page, sessionId: string): Promise<void> {
  await fetchJson<Record<string, unknown>>(page, '/api/webpreview/target', {
    method: 'PUT',
    body: {
      sessionId,
      previewName: 'social',
      url: demoUrl,
    },
  });
  await pause(1400);
  const browserButton = page.locator('[data-action="web"]:visible, #btn-mobile-web:visible').first();
  if (await browserButton.isVisible().catch(() => false)) {
    await browserButton.click({ force: true });
  }
  await page.locator('#web-preview-dock:not(.hidden)').waitFor({ timeout: 10000 });
  await page.locator('#web-preview-url-input').fill(demoUrl);
  await page.locator('#web-preview-go').click({ force: true });
  await page.locator('.web-preview-iframe:not(.hidden)').waitFor({ timeout: 10000 });
  await pause(1500);
}

async function captureStory(page: Page, story: Story, runDir: string): Promise<void> {
  const originalSettings = await applyCaptureSettings(page, runDir);
  try {
    await stabilizeSurface(page);
    const sessions = await ensureDemoSessions(page);
    await page.reload({ waitUntil: 'domcontentloaded' });
    await expect(page.locator('body')).toBeVisible({ timeout: 10000 });
    await stabilizeSurface(page);
    const recordingBrowserId = await claimRecordingBrowser(page);
    await waitForDemoSessionItemsRendered(page, Object.values(sessions));
    await hideNonDemoSidebarSessions(page, Object.values(sessions));
    await selectSession(page, sessions[story.focus]);
    await openTerminal(page);
    await showCaption(page, story.title, story.hook);
    await pause(1900);
    await story.action(page, sessions, runDir);
    await showCaption(page, story.title, story.payoff);
    await pause(2300);
    await hideCaption(page);
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
          viewport: landscapeViewport,
          checks: {
            bodyMarkedForCapture: await page.evaluate(() =>
              document.body.classList.contains('social-marketing-capture'),
            ),
            visibleSession: await page
              .locator(`.session-wrapper[data-session-id="${sessions[story.focus]}"]:not(.hidden)`)
              .isVisible(),
            securityWarningHidden: await page.evaluate(() => {
              const el = document.getElementById('security-warning');
              return !el || getComputedStyle(el).display === 'none';
            }),
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
    slug: '01-adhoc-session',
    title: 'Ad-hoc session',
    hook: 'Need a shell now?',
    payoff: 'Create throwaway local work without leaving the workspace.',
    focus: 'adhoc',
    action: async (page, sessions) => {
      await selectSession(page, sessions.adhoc);
      await sendText(page, sessions.adhoc, "Write-Host 'still inside the same browser workspace' -ForegroundColor Green", true);
      await pause(1600);
    },
  },
  {
    slug: '02-web-terminal',
    title: 'Web terminal',
    hook: 'The terminal is real. The surface is the browser.',
    payoff: 'Local PTY, persistent browser control surface.',
    focus: 'terminal',
    action: async (page, sessions) => {
      await selectSession(page, sessions.terminal);
      await pause(1700);
    },
  },
  {
    slug: '03-real-copy-paste',
    title: 'Real copy and paste',
    hook: 'Paste should stay exact.',
    payoff: 'Structured text lands as text, not broken keystrokes.',
    focus: 'paste',
    action: async (page, sessions) => {
      await selectSession(page, sessions.paste);
      await pasteText(page, sessions.paste, "@'\nPlan\n- keep line one\n- keep line two\n  - keep indentation\n'@");
      await pause(500);
      await sendKeys(page, sessions.paste, ['Enter']);
      await pause(1900);
    },
  },
  {
    slug: '04-file-radar',
    title: 'File Radar',
    hook: 'Terminal output should be clickable context.',
    payoff: 'Paths become navigation, not dead text.',
    focus: 'files',
    action: async (page, sessions) => {
      await selectSession(page, sessions.files);
      await openFiles(page);
      await pause(1700);
      await openTerminal(page);
    },
  },
  {
    slug: '05-bookmarks',
    title: 'Bookmarks',
    hook: 'Some shells are worth coming back to.',
    payoff: 'Pinned launch contexts are one click away.',
    focus: 'terminal',
    action: async (page) => {
      await openBookmarks(page);
      await pause(1900);
    },
  },
  {
    slug: '06-multi-agents',
    title: 'Multi-agent supervision',
    hook: 'Agents need a control room.',
    payoff: 'AI tools and normal shells stay visible together.',
    focus: 'agents',
    action: async (page, sessions) => {
      await selectSession(page, sessions.agents);
      await pause(1800);
    },
  },
  {
    slug: '07-side-by-side-console',
    title: 'Side-by-side console work',
    hook: 'Not every job belongs in one pane.',
    payoff: 'Builds, logs, previews, and shells share one dashboard.',
    focus: 'console',
    action: async (page, sessions) => {
      await selectSession(page, sessions.console);
      await openDevBrowser(page, sessions.browser);
      await pause(1600);
    },
  },
  {
    slug: '08-dev-browser-validation',
    title: 'Dev Browser validation',
    hook: 'The preview belongs next to the command.',
    payoff: 'Preview, reset, inspect, and screenshot from the same workspace.',
    focus: 'browser',
    action: async (page, sessions) => {
      await openDevBrowser(page, sessions.browser);
      await pause(1800);
    },
  },
  {
    slug: '09-desktop-control',
    title: 'Desktop control',
    hook: 'Desktop mode keeps the whole workspace visible.',
    payoff: 'Sessions, chrome, controls, and context stay on one canvas.',
    focus: 'terminal',
    action: async (page, sessions) => {
      await selectSession(page, sessions.terminal);
      await openDevBrowser(page, sessions.browser);
      await pause(900);
      await openTerminal(page);
      await pause(900);
    },
  },
  {
    slug: '10-files-git-context',
    title: 'Files and Git context',
    hook: 'The shell needs surrounding context.',
    payoff: 'Terminal, files, git, and previews stay in one workspace.',
    focus: 'git',
    action: async (page, sessions) => {
      await selectSession(page, sessions.git);
      await openFiles(page);
      await pause(1900);
    },
  },
];

test.use({
  baseURL: process.env.MIDTERM_BASE_URL ?? 'https://localhost:2000',
  ignoreHTTPSErrors: true,
  viewport: landscapeViewport,
  video: {
    mode: 'on',
    size: landscapeViewport,
  },
});

test.setTimeout(210000);

for (const story of stories) {
  test(`social feature series - ${story.slug}`, async ({ page }) => {
    const runDir = getSharedRunDir();
    test.info().annotations.push({ type: 'runDir', description: runDir });
    test.info().annotations.push({ type: 'artifactBase', description: story.slug });
    await authenticate(page);
    await page.goto('/');
    await expect(page.locator('body')).toBeVisible({ timeout: 10000 });
    await captureStory(page, story, runDir);
  });
}

test.afterEach(async ({ page }, testInfo) => {
  const runDir = testInfo.annotations.find((annotation) => annotation.type === 'runDir')?.description;
  const artifactBase =
    testInfo.annotations.find((annotation) => annotation.type === 'artifactBase')?.description ?? 'social-feature';
  const video = page.video();
  if (!runDir || !video) {
    return;
  }

  const destPath = path.join(runDir, `${artifactBase}.webm`);
  await page.close();
  await video.saveAs(destPath);
  console.log(`Social feature clip saved to: ${destPath}`);
});

import { test, expect, type Page } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

const outputBase = path.join(__dirname, '../output/background-feature');
const landscapeViewport = { width: 1920, height: 1080 };
const repoRoot = path.resolve(__dirname, '../../../..');
const backgroundAssetPath = path.resolve(
  __dirname,
  '../../assets/tlbx-sci-fi-ken-burns-background-steel-cyan-2026-06-03.png',
);

type Settings = Record<string, unknown>;

type SessionDto = {
  id: string;
  name?: string | null;
  surface?: string | null;
};

type ImageSnapshot = {
  dataUrl: string;
  type: string;
  name: string;
} | null;

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

async function snapshotCurrentBackgroundImage(page: Page, settings: Settings): Promise<ImageSnapshot> {
  if (!settings.backgroundImageFileName) {
    return null;
  }

  const fileName =
    typeof settings.backgroundImageFileName === 'string' && settings.backgroundImageFileName.trim()
      ? settings.backgroundImageFileName
      : 'restored-background.png';

  return page.evaluate(async (name) => {
    const response = await fetch('/api/settings/background-image');
    if (!response.ok) {
      return null;
    }

    const blob = await response.blob();
    const dataUrl = await new Promise<string>((resolve, reject) => {
      const reader = new FileReader();
      reader.onload = () => resolve(String(reader.result));
      reader.onerror = () => reject(reader.error ?? new Error('Background image read failed.'));
      reader.readAsDataURL(blob);
    });
    return {
      dataUrl,
      type: blob.type || 'image/png',
      name,
    };
  }, fileName);
}

async function uploadBackgroundDataUrl(page: Page, snapshot: ImageSnapshot): Promise<void> {
  if (!snapshot) {
    await fetchJson<Record<string, unknown>>(page, '/api/settings/background-image', { method: 'DELETE' });
    return;
  }

  await page.evaluate(async ({ dataUrl, type, name }) => {
    const base64 = dataUrl.includes(',') ? dataUrl.slice(dataUrl.indexOf(',') + 1) : dataUrl;
    const binary = atob(base64);
    const bytes = new Uint8Array(binary.length);
    for (let i = 0; i < binary.length; i += 1) {
      bytes[i] = binary.charCodeAt(i);
    }
    const blob = new Blob([bytes], { type });
    const form = new FormData();
    form.append('file', new File([blob], name, { type }));
    const upload = await fetch('/api/settings/background-image', { method: 'POST', body: form });
    if (!upload.ok) {
      throw new Error(`Restore background upload failed: ${upload.status} ${await upload.text()}`);
    }
  }, snapshot);
}

async function uploadDemoBackground(page: Page): Promise<void> {
  const dataUrl = `data:image/png;base64,${fs.readFileSync(backgroundAssetPath).toString('base64')}`;
  await uploadBackgroundDataUrl(page, {
    dataUrl,
    type: 'image/png',
    name: path.basename(backgroundAssetPath),
  });
}

async function applyBackgroundFeatureSettings(page: Page, runDir: string): Promise<{
  settings: Settings;
  image: ImageSnapshot;
}> {
  const originalSettings = await fetchJson<Settings>(page, '/api/settings');
  const originalImage = await snapshotCurrentBackgroundImage(page, originalSettings);
  fs.writeFileSync(path.join(runDir, 'settings-before.json'), JSON.stringify(originalSettings, null, 2));

  await uploadDemoBackground(page);

  const next = {
    ...originalSettings,
    theme: 'dark',
    terminalColorScheme: 'dark',
    backgroundImageEnabled: true,
    hideBackgroundImageOnMobile: false,
    backgroundKenBurnsEnabled: true,
    backgroundKenBurnsZoomPercent: 165,
    backgroundKenBurnsSpeedPxPerSecond: 24,
    uiTransparency: 50,
    terminalTransparency: 50,
    terminalCellBackgroundTransparency: 50,
    fontSize: 18,
    lineHeight: 1.08,
    terminalThemeLightnessBoost: 12,
  };
  await fetchJson<Settings>(page, '/api/settings', { method: 'PUT', body: next });
  await pause(900);
  fs.writeFileSync(
    path.join(runDir, 'settings-applied.json'),
    JSON.stringify(await fetchJson<Settings>(page, '/api/settings'), null, 2),
  );
  return { settings: originalSettings, image: originalImage };
}

async function restoreBackgroundFeatureSettings(
  page: Page,
  original: { settings: Settings; image: ImageSnapshot } | null,
): Promise<void> {
  if (!original) {
    return;
  }

  await uploadBackgroundDataUrl(page, original.image);
  await fetchJson<Settings>(page, '/api/settings', { method: 'PUT', body: original.settings });
}

async function createSession(page: Page): Promise<string> {
  const sessions = await fetchJson<{ sessions?: SessionDto[] }>(page, '/api/sessions');
  const stale = (sessions.sessions ?? []).filter((session) => session.surface === 'marketing-background-feature');
  for (const session of stale) {
    await fetchJson<Record<string, unknown>>(page, `/api/sessions/${session.id}`, { method: 'DELETE' }).catch(
      () => ({}),
    );
  }

  const created = await fetchJson<SessionDto>(page, '/api/sessions', {
    method: 'POST',
    body: {
      shell: 'Pwsh',
      workingDirectory: repoRoot,
      cols: 110,
      rows: 34,
      surface: 'marketing-background-feature',
    },
  });
  await fetchJson<Record<string, unknown>>(page, `/api/sessions/${created.id}/name`, {
    method: 'PUT',
    body: { name: 'MT Marketing - Transparent Background' },
  });
  await fetchJson<Record<string, unknown>>(page, `/api/sessions/${created.id}/topic`, {
    method: 'PUT',
    body: { topic: 'Background feature recording' },
  });
  await fetchJson<Record<string, unknown>>(page, `/api/sessions/${created.id}/input/text`, {
    method: 'POST',
    body: {
      appendNewline: true,
      text:
        "Clear-Host; Write-Host 'Background image + transparency' -ForegroundColor Cyan; " +
        "Write-Host 'Ken Burns motion behind real terminal surfaces'; " +
        "Write-Host ''; Write-Host 'UI transparency: 50%'; Write-Host 'Terminal transparency: 50%'",
    },
  });
  return created.id;
}

async function selectSession(page: Page, sessionId: string): Promise<void> {
  await page.locator(`.session-item[data-session-id="${sessionId}"]`).first().waitFor({
    state: 'visible',
    timeout: 10000,
  });
  await page.evaluate((id) => {
    const target = document.querySelector<HTMLElement>(`.session-item[data-session-id="${CSS.escape(id)}"]`);
    if (!target) {
      throw new Error(`Session item ${id} is not rendered.`);
    }
    target.dispatchEvent(new PointerEvent('pointerdown', { bubbles: true, button: 0, pointerType: 'mouse' }));
    target.dispatchEvent(new MouseEvent('click', { bubbles: true, button: 0 }));
  }, sessionId);
  await page.locator(`.session-wrapper[data-session-id="${sessionId}"]:not(.hidden)`).waitFor({
    timeout: 10000,
  });
  await pause(1200);
}

async function showCaption(page: Page, eyebrow: string, text: string): Promise<void> {
  await page.evaluate(
    ({ captionEyebrow, captionText }) => {
      let el = document.getElementById('background-feature-caption');
      if (!el) {
        el = document.createElement('div');
        el.id = 'background-feature-caption';
        el.innerHTML = '<div class="eyebrow"></div><div class="text"></div>';
        el.style.position = 'fixed';
        el.style.left = '56px';
        el.style.right = 'auto';
        el.style.bottom = '48px';
        el.style.maxWidth = '760px';
        el.style.zIndex = '999999';
        el.style.padding = '18px 22px';
        el.style.borderRadius = '12px';
        el.style.background = 'rgba(5, 8, 14, 0.76)';
        el.style.border = '1px solid rgba(255,255,255,0.18)';
        el.style.boxShadow = '0 18px 42px rgba(0,0,0,0.38)';
        el.style.backdropFilter = 'blur(10px)';
        el.style.pointerEvents = 'none';
        document.body.appendChild(el);
      }
      const eyebrowEl = el.querySelector<HTMLElement>('.eyebrow');
      const textEl = el.querySelector<HTMLElement>('.text');
      if (eyebrowEl) {
        eyebrowEl.textContent = captionEyebrow;
        eyebrowEl.style.color = '#59d7ff';
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

async function hidePrivateShellContext(page: Page, sessionId: string): Promise<void> {
  await page.addStyleTag({
    content: `
      .scaled-overlay,
      [data-testid*="password" i],
      [class*="password" i] {
        display: none !important;
      }
    `,
  });
  await page.evaluate((id) => {
    const keep = id;
    const applyFilter = () => {
      for (const item of document.querySelectorAll<HTMLElement>('.session-item[data-session-id]')) {
        item.style.display = item.dataset.sessionId === keep ? '' : 'none';
      }
      for (const item of document.querySelectorAll<HTMLElement>('.git-repo-chip, .manager-bar, .manager-btn')) {
        item.style.display = 'none';
      }
    };
    applyFilter();
    const w = window as Window & typeof globalThis & { __backgroundFeatureFilter?: number };
    if (w.__backgroundFeatureFilter !== undefined) {
      window.clearInterval(w.__backgroundFeatureFilter);
    }
    w.__backgroundFeatureFilter = window.setInterval(applyFilter, 250);
  }, sessionId);
}

test.use({
  baseURL: process.env.MIDTERM_BASE_URL ?? 'https://localhost:2000',
  ignoreHTTPSErrors: true,
  viewport: landscapeViewport,
  video: {
    mode: 'on',
    size: landscapeViewport,
  },
});

test.setTimeout(90000);

test('background feature - image, ken burns, 50 percent transparency', async ({ page }) => {
  const runDir = getSharedRunDir();
  test.info().annotations.push({ type: 'runDir', description: runDir });
  test.info().annotations.push({ type: 'artifactBase', description: 'background-ken-burns-transparency' });
  await authenticate(page);
  await page.goto('/');
  await expect(page.locator('body')).toBeVisible({ timeout: 10000 });

  const original = await applyBackgroundFeatureSettings(page, runDir);
  try {
    const sessionId = await createSession(page);
    await page.reload({ waitUntil: 'domcontentloaded' });
    await expect(page.locator('body')).toBeVisible({ timeout: 10000 });
    await hidePrivateShellContext(page, sessionId);
    await selectSession(page, sessionId);
    await showCaption(page, 'Background image', 'Ken Burns motion, 50% transparent UI.');
    await pause(2800);
    await showCaption(page, 'Readable terminal', 'The workspace stays usable over the scene.');
    await pause(3600);
    const evidence = await page.evaluate(() => {
      const root = getComputedStyle(document.documentElement);
      return {
        backgroundImage: root.getPropertyValue('--app-background-image'),
        backgroundTransform: root.getPropertyValue('--app-background-transform'),
        backgroundAnimation: root.getPropertyValue('--app-background-animation'),
        backgroundScale: root.getPropertyValue('--app-background-ken-burns-scale'),
        backgroundPanX: root.getPropertyValue('--app-background-ken-burns-pan-x'),
        backgroundPanY: root.getPropertyValue('--app-background-ken-burns-pan-y'),
        bodyClasses: Array.from(document.body.classList),
      };
    });
    await page.screenshot({ path: path.join(runDir, 'background-ken-burns-transparency-poster.png'), fullPage: false });
    fs.writeFileSync(
      path.join(runDir, 'background-ken-burns-transparency-audit.json'),
      JSON.stringify({ sessionId, viewport: landscapeViewport, evidence }, null, 2),
    );
  } finally {
    await restoreBackgroundFeatureSettings(page, original);
  }
});

test.afterEach(async ({ page }, testInfo) => {
  const runDir = testInfo.annotations.find((annotation) => annotation.type === 'runDir')?.description;
  const artifactBase =
    testInfo.annotations.find((annotation) => annotation.type === 'artifactBase')?.description ??
    'background-feature';
  const video = page.video();
  if (!runDir || !video) {
    return;
  }

  const destPath = path.join(runDir, `${artifactBase}.webm`);
  await page.close();
  await video.saveAs(destPath);
  console.log(`Background feature clip saved to: ${destPath}`);
});

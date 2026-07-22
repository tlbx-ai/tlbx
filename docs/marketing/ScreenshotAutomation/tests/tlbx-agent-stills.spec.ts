import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

/**
 * Captures three desktop stills of real agent CLI sessions (Codex, Claude
 * Code, OpenCode) staged beforehand against a neutral fixture project.
 * Session ids are passed via STILL_* environment variables; the sessions
 * must already contain live agent TUIs.
 */

const outputBase = path.join(__dirname, '../output/agent-stills');
const demoUrl = process.env.MIDTERM_DEMO_URL?.trim() || 'http://127.0.0.1:4177/';

test.use({
  viewport: { width: 1920, height: 1080 },
  video: 'off',
});

function getRunDir(): string {
  const next =
    (fs.existsSync(outputBase)
      ? fs
          .readdirSync(outputBase)
          .map((entry) => /^run-(\d+)$/.exec(entry)?.[1])
          .filter((value): value is string => Boolean(value))
          .map((value) => Number.parseInt(value, 10))
          .reduce((max, value) => Math.max(max, value), 0)
      : 0) + 1;
  const dir = path.join(outputBase, `run-${next}`);
  fs.mkdirSync(dir, { recursive: true });
  return dir;
}

test('capture real agent stills', async ({ page }) => {
  const ids = {
    codex: process.env.STILL_CODEX ?? '',
    claude: process.env.STILL_CLAUDE ?? '',
    opencode: process.env.STILL_OPENCODE ?? '',
    server: process.env.STILL_SERVER ?? '',
  };
  for (const [key, value] of Object.entries(ids)) {
    if (!value) throw new Error(`missing session id env for ${key}`);
  }

  const selectSession = async (sessionId: string): Promise<void> => {
    await page.locator(`.session-item[data-session-id="${sessionId}"]`).first().click({ force: true });
    await page
      .locator(`.session-wrapper[data-session-id="${sessionId}"]:not(.hidden)`)
      .waitFor({ timeout: 10000 });
    await page.waitForTimeout(3500);
  };

  await page.goto('/');
  await expect(page.locator('#app')).toBeVisible({ timeout: 15000 });
  await page.waitForTimeout(4000);

  const runDir = getRunDir();

  await selectSession(ids.codex);
  await page.screenshot({ path: path.join(runDir, 'workspace-overview.png') });

  await selectSession(ids.claude);
  await page.screenshot({ path: path.join(runDir, 'agent-sessions.png') });

  await selectSession(ids.opencode);
  await page.screenshot({ path: path.join(runDir, 'opencode-session.png') });

  await selectSession(ids.server);
  const browserButton = page.locator('[data-action="web"]:visible, #btn-mobile-web:visible').first();
  await browserButton.click({ force: true });
  await page.locator('#web-preview-dock:not(.hidden)').waitFor({ timeout: 10000 });
  await page.locator('#web-preview-url-input').fill(demoUrl);
  await page.locator('#web-preview-go').click({ force: true });
  await page.locator('.web-preview-iframe:not(.hidden)').waitFor({ timeout: 10000 });
  await page.waitForTimeout(3000);
  await page.screenshot({ path: path.join(runDir, 'console-and-dev-browser.png') });

  console.log(`agent stills written to ${runDir}`);
});

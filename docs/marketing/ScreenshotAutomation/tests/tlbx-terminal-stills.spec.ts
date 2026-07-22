import { test, expect } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

/**
 * Captures general-terminal stills (btop system monitor, tmux pane layout)
 * from pre-staged sessions on a wallpaper-enabled dev instance.
 */

const outputBase = path.join(__dirname, '../output/terminal-stills');

test.use({ viewport: { width: 1920, height: 1080 }, video: 'off' });

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

test('capture terminal stills', async ({ page }) => {
  const ids = {
    btop: process.env.STILL_BTOP ?? '',
  };
  for (const [key, value] of Object.entries(ids)) {
    if (!value) throw new Error(`missing session id env for ${key}`);
  }

  const dismissOwnershipToast = async (): Promise<void> => {
    const takeover = page
      .locator('button:has-text("Take size control"), button:has-text("Continue working here")')
      .first();
    if (await takeover.isVisible().catch(() => false)) {
      await takeover.click({ force: true }).catch(() => {});
      await page.waitForTimeout(2500);
    }
  };

  const selectSession = async (sessionId: string): Promise<void> => {
    await page.locator(`.session-item[data-session-id="${sessionId}"]`).first().click({ force: true });
    await page
      .locator(`.session-wrapper[data-session-id="${sessionId}"]:not(.hidden)`)
      .waitFor({ timeout: 10000 });
    await page.waitForTimeout(2500);
    await dismissOwnershipToast();
    await page.waitForTimeout(2500);
  };

  await page.goto('/');
  await expect(page.locator('#app')).toBeVisible({ timeout: 15000 });
  await page.waitForTimeout(4000);

  const runDir = getRunDir();

  await selectSession(ids.btop);
  await page.screenshot({ path: path.join(runDir, 'btop.png') });

  console.log(`terminal stills written to ${runDir}`);
});

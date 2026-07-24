import { test } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

test.use({ viewport: { width: 1920, height: 1080 }, video: 'off' });

test('sweep demo sessions', async ({ page }) => {
  const order = ['claude', 'codex', 'opencode', 'grok', 'btop', 'far'];
  const ids = JSON.parse(process.env.DEMO_IDS ?? '{}');
  const runDir = path.join(__dirname, '../output/demo-sweep');
  fs.mkdirSync(runDir, { recursive: true });

  await page.addStyleTag({ content: '.size-ownership-toast,[class*="ownership"],[class*="takeover"]{display:none!important}' }).catch(() => {});
  await page.goto('/');
  await page.waitForTimeout(4000);

  for (const key of order) {
    const id = ids[key];
    if (!id) continue;
    await page.locator(`.session-item[data-session-id="${id}"]`).first().click({ force: true }).catch(() => {});
    await page.waitForTimeout(1800);
    const btn = page.locator('button:has-text("Take size control"), button:has-text("Continue working here")').first();
    if (await btn.isVisible().catch(() => false)) { await btn.click({ force: true }).catch(() => {}); await page.waitForTimeout(1500); }
    await page.waitForTimeout(800);
    await page.screenshot({ path: path.join(runDir, `${key}.png`) });
  }
  console.log('sweep done ->', runDir);
});

import { test } from '@playwright/test';
import * as fs from 'fs';
import * as path from 'path';

// Captures two synchronized sweeps of the same six live sessions:
//  - desktop (1920x1080) and mobile (400x860)
// Each context takes size control so both render at native size (no scaled-follower view).
// A priming pass (later trimmed out) grabs control cleanly; the kept sweep is period-anchored
// so the desktop and mobile timelines line up for picture-in-picture compositing.

const ORDER = ['claude', 'codex', 'opencode', 'grok', 'btop', 'far'];
const PERIOD = 3200; // ms each session stays on screen during the kept sweep
const HIDE_CSS = '.scaled-overlay{opacity:0 !important;pointer-events:auto} [class*="ownership"]{display:none !important}';

test.setTimeout(240000);

test('demo video capture', async ({ browser }) => {
  const ids = JSON.parse(process.env.DEMO_IDS ?? '{}');
  const outDir = path.join(__dirname, '../output/demo-video');
  fs.mkdirSync(outDir, { recursive: true });
  const timings: any = {};

  async function capture(kind: 'desktop' | 'mobile') {
    const isMobile = kind === 'mobile';
    const viewport = isMobile ? { width: 540, height: 960 } : { width: 1920, height: 1080 };
    const ctx = await browser.newContext({
      viewport,
      ignoreHTTPSErrors: true,
      isMobile,
      hasTouch: isMobile,
      deviceScaleFactor: isMobile ? 2 : 1,
      recordVideo: { dir: path.join(outDir, kind), size: viewport },
    });
    const page = await ctx.newPage();
    const pageCreated = Date.now();
    await page.goto('/');
    await page.waitForTimeout(4200);
    await page.addStyleTag({ content: HIDE_CSS }).catch(() => {});

    const openAndShow = async (key: string) => {
      const id = ids[key];
      if (!id) return;
      if (isMobile) {
        await page.locator('button.topbar-hamburger').click({ force: true }).catch(() => {});
        await page.waitForTimeout(320);
      }
      await page.locator(`.session-item[data-session-id="${id}"]`).first().click({ force: true }).catch(() => {});
      await page.waitForTimeout(220);
    };

    const takeControl = async () => {
      const ov = page.locator('.scaled-overlay').first();
      if (await ov.isVisible().catch(() => false)) {
        await ov.click({ force: true }).catch(() => {});
        await page.waitForTimeout(450);
      }
    };

    // ---- PRIMING (trimmed out of final cut): grab size control of every session
    for (const key of ORDER) {
      await openAndShow(key);
      await takeControl();
      await page.waitForTimeout(150);
    }
    await page.addStyleTag({ content: HIDE_CSS }).catch(() => {});
    await page.waitForTimeout(600);

    // ---- KEPT SWEEP: period-anchored so desktop and mobile align
    const t0 = Date.now();
    const sweepStartOffset = (t0 - pageCreated) / 1000; // seconds from video start
    const sessions: any[] = [];
    for (let i = 0; i < ORDER.length; i++) {
      const key = ORDER[i];
      if (isMobile) {
        await page.locator('button.topbar-hamburger').click({ force: true }).catch(() => {});
        await page.waitForTimeout(280);
      }
      await page.locator(`.session-item[data-session-id="${ids[key]}"]`).first().click({ force: true }).catch(() => {});
      await takeControl();
      sessions.push({ key, shownAt: (Date.now() - t0) / 1000 });
      const target = (i + 1) * PERIOD;
      const now = Date.now() - t0;
      if (now < target) await page.waitForTimeout(target - now);
    }
    const sweepDuration = (Date.now() - t0) / 1000;
    await page.waitForTimeout(300);

    const vpath = await page.video()!.path();
    await ctx.close(); // finalizes the webm
    const dest = path.join(outDir, `${kind}.webm`);
    fs.copyFileSync(vpath, dest);
    timings[kind] = { sweepStartOffset, sweepDuration, sessions, video: dest };
    console.log(`${kind}: start=${sweepStartOffset.toFixed(2)}s dur=${sweepDuration.toFixed(2)}s`);
  }

  const only = process.env.CAPTURE_ONLY;
  if (only !== 'mobile') await capture('desktop');
  if (only !== 'desktop') await capture('mobile');

  // preserve prior timings for the kind we skipped
  const tp = path.join(outDir, 'timings.json');
  if (only && fs.existsSync(tp)) {
    const prev = JSON.parse(fs.readFileSync(tp, 'utf8'));
    for (const k of ['desktop', 'mobile']) if (!timings[k] && prev[k]) timings[k] = prev[k];
  }
  fs.writeFileSync(tp, JSON.stringify(timings, null, 2));
  console.log('CAPTURE DONE');
});

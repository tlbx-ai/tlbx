import { chromium } from 'playwright';
import { fileURLToPath } from 'node:url';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
const htmlPath = path.join(here, '..', '..', 'Icons', 'midterm-banner.html');
const outPath = process.argv[2];

if (!outPath) {
  console.error('usage: node render-readme-banner.mjs <output.png>');
  process.exit(1);
}

const browser = await chromium.launch();
const page = await browser.newPage({
  viewport: { width: 1920, height: 560 },
  deviceScaleFactor: 1,
});
await page.goto(`file:///${htmlPath.replace(/\\/g, '/')}`);
await page.evaluate(() => document.fonts.ready);
await page.waitForTimeout(250);
await page.screenshot({ path: outPath, fullPage: false });
await browser.close();
console.log(`banner written: ${outPath}`);

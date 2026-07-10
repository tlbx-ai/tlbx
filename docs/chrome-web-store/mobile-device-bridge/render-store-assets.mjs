import { chromium } from '../../marketing/ScreenshotAutomation/node_modules/playwright/index.mjs';
import { fileURLToPath, pathToFileURL } from 'node:url';
import path from 'node:path';

const directory = path.dirname(fileURLToPath(import.meta.url));
const pageUrl = pathToFileURL(path.join(directory, 'store-assets.html')).href;
const assetsDirectory = path.join(directory, 'assets');
const browser = await chromium.launch({ headless: true });

try {
  const screenshotPage = await browser.newPage({ viewport: { width: 1280, height: 800 } });
  await screenshotPage.goto(`${pageUrl}?asset=screenshot`, { waitUntil: 'networkidle' });
  await screenshotPage.screenshot({ path: path.join(assetsDirectory, 'screenshot-1280x800.png') });

  const promoPage = await browser.newPage({ viewport: { width: 440, height: 280 } });
  await promoPage.goto(`${pageUrl}?asset=promo`, { waitUntil: 'networkidle' });
  await promoPage.screenshot({ path: path.join(assetsDirectory, 'promo-440x280.png') });
} finally {
  await browser.close();
}

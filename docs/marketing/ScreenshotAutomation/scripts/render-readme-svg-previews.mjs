import { chromium } from 'playwright';
import { fileURLToPath, pathToFileURL } from 'node:url';
import fs from 'node:fs/promises';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..', '..', '..', '..');
const sourceDir = path.join(repoRoot, 'docs', 'marketing', 'readme');
const outputDir = process.argv[2]
  ? path.resolve(process.argv[2])
  : path.join(repoRoot, '.artifacts', 'readme-svg-preview');

const assets = [
  { name: 'midterm-mark', width: 512, height: 512 },
  { name: 'midterm-wordmark', width: 1600, height: 520 },
  { name: 'browser-next-to-work', width: 1600, height: 720 },
  { name: 'agent-control-room', width: 1600, height: 900 },
  { name: 'local-first-anywhere', width: 1600, height: 650 },
];

await fs.mkdir(outputDir, { recursive: true });

const browser = await chromium.launch();
try {
  for (const asset of assets) {
    const page = await browser.newPage({
      viewport: { width: asset.width, height: asset.height },
      deviceScaleFactor: 1,
    });
    const input = path.join(sourceDir, `${asset.name}.svg`);
    const output = path.join(outputDir, `${asset.name}.png`);

    await page.goto(pathToFileURL(input).href, { waitUntil: 'load' });
    await page.screenshot({ path: output, fullPage: false });
    await page.close();
    console.log(`${asset.name}: ${output}`);
  }
} finally {
  await browser.close();
}

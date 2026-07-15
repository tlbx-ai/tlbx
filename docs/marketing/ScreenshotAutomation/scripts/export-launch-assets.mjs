import { chromium } from 'playwright';
import { fileURLToPath, pathToFileURL } from 'node:url';
import fs from 'node:fs/promises';
import path from 'node:path';

const here = path.dirname(fileURLToPath(import.meta.url));
const repoRoot = path.resolve(here, '..', '..', '..', '..');
const sourceDir = path.join(repoRoot, 'docs', 'marketing', 'readme');
const outputDir = path.join(repoRoot, 'docs', 'marketing', 'launch-assets-2026-07');

const exports = [
  {
    source: 'midterm-mark.svg',
    output: 'product-hunt-thumbnail-240x240.png',
    width: 240,
    height: 240,
  },
  {
    source: 'browser-next-to-work.svg',
    output: 'product-hunt-01-browser-workspace-1270x760.png',
    width: 1270,
    height: 760,
  },
  {
    source: 'your-machines-are-tabs.svg',
    output: 'product-hunt-02-multiple-hosts-1270x760.png',
    width: 1270,
    height: 760,
  },
  {
    source: 'browser-next-to-work.svg',
    output: 'x-01-browser-next-to-work-1600x900.png',
    width: 1600,
    height: 900,
  },
  {
    source: 'your-machines-are-tabs.svg',
    output: 'x-02-your-machines-1600x900.png',
    width: 1600,
    height: 900,
  },
  {
    source: 'github-social-preview.svg',
    output: 'github-social-preview-1280x640.png',
    width: 1280,
    height: 640,
  },
];

await fs.mkdir(outputDir, { recursive: true });
const expectedOutputs = new Set(exports.map((item) => item.output));
for (const entry of await fs.readdir(outputDir, { withFileTypes: true })) {
  if (entry.isFile() && entry.name.endsWith('.png') && !expectedOutputs.has(entry.name)) {
    await fs.rm(path.join(outputDir, entry.name));
  }
}

const browser = await chromium.launch();
try {
  for (const item of exports) {
    const page = await browser.newPage({
      viewport: { width: item.width, height: item.height },
      deviceScaleFactor: 1,
    });
    const input = path.join(sourceDir, item.source);
    const output = path.join(outputDir, item.output);

    await page.goto(pathToFileURL(input).href, { waitUntil: 'load' });
    await page.evaluate(
      ({ width, height }) => {
        const svg = document.documentElement;
        svg.setAttribute('width', String(width));
        svg.setAttribute('height', String(height));
        svg.setAttribute('preserveAspectRatio', 'xMidYMid meet');
        svg.style.background = '#040609';
      },
      { width: item.width, height: item.height },
    );
    await page.screenshot({ path: output, fullPage: false });
    await page.close();
    console.log(`${item.output}: ${item.width}x${item.height}`);
  }
} finally {
  await browser.close();
}

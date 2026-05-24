import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const projectRoot = path.resolve(__dirname, '../../../..');
const html = readFileSync(path.join(projectRoot, 'src/static/index.html'), 'utf8');
const source = readFileSync(path.join(projectRoot, 'src/ts/modules/web/webPanel.ts'), 'utf8');

describe('web preview screenshot wiring', () => {
  it('keeps dev browser actions in the tab strip instead of a separate title header', () => {
    expect(html).toContain('<div class="web-preview-tab-strip">');
    expect(html).toContain('<div class="web-preview-tabs" id="web-preview-tabs"></div>');
    expect(html).toContain('<div class="web-preview-dock-actions">');
    expect(html).not.toContain('id="web-preview-screenshot"');
    expect(html).not.toContain('class="web-preview-dock-title"');
    expect(html).not.toContain('>Dev Browser</span>');
  });

  it('renders a dedicated action message region for screenshot failures', () => {
    expect(html).toContain('id="web-preview-action-message"');
    expect(html).toContain('aria-live="polite"');
  });

  it('keeps per-tab screenshot buttons busy while capture is in progress and surfaces failures', () => {
    expect(source).toContain('let screenshotInFlight = false;');
    expect(source).toContain("screenshotButton.className = 'web-preview-tab-screenshot';");
    expect(source).toContain("button.classList.toggle('web-preview-action-working', isTarget);");
    expect(source).toContain('void handleScreenshot(event.ctrlKey, preview.previewName);');
    expect(source).toContain("setActionMessage('error', 'Screenshot failed:");
    expect(source).toContain("setActionMessage('info', null);");
  });
});

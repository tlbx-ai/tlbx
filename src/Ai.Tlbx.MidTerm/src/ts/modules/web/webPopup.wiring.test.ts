import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const projectRoot = path.resolve(__dirname, '../../../..');
const html = readFileSync(path.join(projectRoot, 'src/static/web-preview-popup.html'), 'utf8');
const script = readFileSync(path.join(projectRoot, 'src/static/js/web-preview-popup.js'), 'utf8');

describe('web preview popup wiring', () => {
  it('keeps the detached popup on the full dev browser chrome', () => {
    expect(html).toContain('class="web-preview-dock web-preview-popup-shell"');
    expect(html).toContain('id="web-preview-session-subtitle"');
    expect(html).toContain('id="web-preview-action-message"');
    expect(html).toContain('id="web-preview-tabs"');
    expect(html).toContain('id="web-preview-url-input"');
    expect(html).toContain('id="web-preview-go"');
    expect(html).toContain('id="web-preview-refresh"');
    expect(html).toContain('id="web-preview-device-status"');
    expect(html).toContain('id="web-preview-screenshot"');
    expect(html).toContain('id="web-preview-clear-cookies"');
    expect(html).toContain('id="web-preview-clear-state"');
    expect(html).toContain('id="web-preview-dock-back"');
  });

  it('drives detached tabs and url changes through the existing preview APIs', () => {
    expect(script).toContain('async function loadOwningSession()');
    expect(script).toContain("var initialMobileMode = params.get('mobile') === '1';");
    expect(script).not.toContain('__mtMobile');
    expect(script).toContain('function readMobileClientProbe()');
    expect(script).toContain('function applyMobileMode(enabled, reloadFrame)');
    expect(script).toContain("'Desktop mobile size'");
    expect(script).toContain("var response = await fetch('/api/sessions');");
    expect(script).toContain('function getOwningSessionDisplayInfo()');
    expect(script).toContain('var screenshotInFlight = false;');
    expect(script).toContain("screenshotButton.classList.add('web-preview-action-working');");
    expect(script).toContain("setActionMessage('error', 'Screenshot failed:");
    expect(script).toContain('/api/webpreview/previews?');
    expect(script).toContain('/api/browser/preview-client');
    expect(script).toContain('/api/webpreview/target');
    expect(script).toContain('function renderTabs()');
    expect(script).toContain('async function selectPreview(previewName)');
    expect(script).toContain('async function handleGo()');
    expect(script).toContain("type: 'dock-back'");
    expect(script).toContain("type: 'navigation'");
    expect(script).toContain("type === 'mobile-mode'");
  });
});

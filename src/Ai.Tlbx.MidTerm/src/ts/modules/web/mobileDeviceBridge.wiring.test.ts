import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const projectRoot = path.resolve(__dirname, '../../../..');
const extensionRoot = path.join(projectRoot, 'src/mobile-device-bridge');
const manifest = JSON.parse(readFileSync(path.join(extensionRoot, 'manifest.json'), 'utf8')) as {
  manifest_version: number;
  permissions: string[];
  content_scripts?: unknown[];
};
const worker = readFileSync(path.join(extensionRoot, 'service-worker.js'), 'utf8');
const pageBridge = readFileSync(path.join(extensionRoot, 'page-bridge.js'), 'utf8');

describe('mobile device bridge wiring', () => {
  it('requires explicit activation on a MidTerm tab without broad host access', () => {
    expect(manifest.manifest_version).toBe(3);
    expect(manifest.permissions).toEqual(
      expect.arrayContaining(['activeTab', 'debugger', 'scripting', 'storage']),
    );
    expect(manifest.permissions).not.toContain('<all_urls>');
    expect(manifest.content_scripts).toBeUndefined();
    expect(worker).toContain('chrome.action.onClicked.addListener');
    expect(worker).toContain("files: ['page-bridge.js']");
    expect(worker).toContain('sender.id !== chrome.runtime.id');
  });

  it('applies top-level Chrome mobile signals and lifecycle controls through CDP', () => {
    expect(worker).toContain("'Emulation.setDeviceMetricsOverride'");
    expect(worker).toContain("'Emulation.setTouchEmulationEnabled'");
    expect(worker).toContain("'Emulation.setUserAgentOverride'");
    expect(worker).toContain('navigator.userAgent.match(/Chrome\\/(\\S+)/)');
    expect(worker).not.toContain("'Browser.getVersion'");
    expect(worker).toContain("'Emulation.setSafeAreaInsetsOverride'");
    expect(worker).toContain("'Page.setWebLifecycleState'");
    expect(worker).toContain("'Page.captureScreenshot'");
    expect(worker).toContain("label: 'Pixel 8'");
    expect(worker).toContain('globalThis.__midtermMobileDeviceBridge = Object.freeze({');
  });

  it('uses a window message bridge instead of opening a server-side Chrome process', () => {
    expect(pageBridge).toContain('.sendMessage({');
    expect(pageBridge).toContain('source: EXTENSION_SOURCE');
    expect(worker).toContain("type: 'normal'");
  });
});

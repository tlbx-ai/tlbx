import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..');
const extensionRoot = path.join(projectRoot, 'src/mobile-device-bridge');
const manifest = JSON.parse(readFileSync(path.join(extensionRoot, 'manifest.json'), 'utf8')) as {
  manifest_version: number;
  permissions: string[];
  icons: Record<string, string>;
  action: { default_icon: Record<string, string> };
  content_scripts?: unknown[];
};

describe('mobile device bridge manifest', () => {
  it('requires explicit activation without broad host access', () => {
    expect(manifest.manifest_version).toBe(3);
    expect(manifest.permissions).toEqual(
      expect.arrayContaining(['activeTab', 'debugger', 'scripting', 'storage']),
    );
    expect(manifest.permissions).not.toContain('<all_urls>');
    expect(manifest.content_scripts).toBeUndefined();
    expect(manifest.icons).toEqual({
      '16': 'icons/icon16.png',
      '48': 'icons/icon48.png',
      '128': 'icons/icon128.png',
    });
    expect(manifest.action.default_icon).toEqual(manifest.icons);

    for (const iconPath of Object.values(manifest.icons)) {
      expect(readFileSync(path.join(extensionRoot, iconPath)).length).toBeGreaterThan(0);
    }
  });
});

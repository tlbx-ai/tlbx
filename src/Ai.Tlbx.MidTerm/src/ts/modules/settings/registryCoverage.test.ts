import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

import { getSettingsRegistryControlEntries, SETTINGS_REGISTRY } from './registry';

const projectRoot = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../../..');
const html = readFileSync(path.join(projectRoot, 'src/static/index.html'), 'utf8');
const settingsModelSource = readFileSync(
  path.join(projectRoot, 'Settings/MidTermSettingsPublic.cs'),
  'utf8',
);

const NON_PERSISTED_SETTING_IDS = new Set([
  'setting-background-upload',
  'setting-background-ken-burns-speed-value',
  'setting-background-ken-burns-zoom-percent-value',
  'setting-ui-transparency-value',
  'setting-terminal-transparency-value',
  'setting-terminal-cell-background-transparency-value',
  'setting-terminal-theme-lightness-boost-value',
]);

function getPersistedSettingIds(): string[] {
  return [...html.matchAll(/id="(setting-[^"]+)"/g)]
    .map((match) => match[1])
    .filter((id): id is string => Boolean(id) && !NON_PERSISTED_SETTING_IDS.has(id))
    .filter((id, index, values) => values.indexOf(id) === index)
    .sort();
}

function getPublicSettingKeys(): string[] {
  return [...settingsModelSource.matchAll(/public\s+[^\s]+\s+(\w+)\s*\{/g)]
    .map((match) => match[1])
    .filter((key): key is string => Boolean(key))
    .map((key) => key.charAt(0).toLowerCase() + key.slice(1))
    .filter((key, index, values) => values.indexOf(key) === index)
    .sort();
}

describe('settings registry completeness', () => {
  it('covers every public setting', () => {
    expect(SETTINGS_REGISTRY.map((entry) => entry.key).sort()).toEqual(getPublicSettingKeys());
  });

  it('covers every persisted settings control', () => {
    expect(
      getSettingsRegistryControlEntries()
        .map((entry) => entry.controlId)
        .filter((id): id is string => Boolean(id))
        .sort(),
    ).toEqual(getPersistedSettingIds());
  });
});

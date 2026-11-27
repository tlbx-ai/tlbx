import fs from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

const LOCALES_DIR = path.resolve(__dirname, '../../../static/locales');
const PLACEHOLDER_PATTERN = /\{[a-zA-Z0-9_]+\}/g;

function extractPlaceholders(value: string): string[] {
  return [...(value.match(PLACEHOLDER_PATTERN) ?? [])].sort();
}

describe('locale key parity', () => {
  const files = fs
    .readdirSync(LOCALES_DIR)
    .filter((file) => file.endsWith('.json'))
    .sort();
  const canonical = JSON.parse(
    fs.readFileSync(path.join(LOCALES_DIR, 'en.json'), 'utf8'),
  ) as Record<string, string>;
  const canonicalKeys = Object.keys(canonical);

  it('includes all 10 supported locale files', () => {
    expect(files).toEqual([
      'bn.json',
      'de.json',
      'en.json',
      'es.json',
      'fr.json',
      'hi.json',
      'ja.json',
      'pt.json',
      'ru.json',
      'zh.json',
    ]);
  });

  for (const file of files) {
    if (file === 'en.json') continue;

    it(`matches en.json keys and interpolation placeholders in ${file}`, () => {
      const locale = JSON.parse(
        fs.readFileSync(path.join(LOCALES_DIR, file), 'utf8'),
      ) as Record<string, string>;
      const missing = canonicalKeys.filter((key) => !(key in locale));
      const extra = Object.keys(locale).filter((key) => !(key in canonical));
      expect(missing).toEqual([]);
      expect(extra).toEqual([]);

      const placeholderMismatches: string[] = [];
      for (const key of canonicalKeys) {
        const expected = extractPlaceholders(canonical[key] ?? '');
        const actual = extractPlaceholders(locale[key] ?? '');
        if (expected.join(',') !== actual.join(',')) {
          placeholderMismatches.push(
            `${key}: expected [${expected.join(', ')}], got [${actual.join(', ')}]`,
          );
        }
      }
      expect(placeholderMismatches).toEqual([]);
    });
  }
});

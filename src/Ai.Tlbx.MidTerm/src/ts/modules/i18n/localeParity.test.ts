import { readFileSync, readdirSync } from 'node:fs';
import { resolve } from 'node:path';
import { describe, expect, it } from 'vitest';

// eslint-disable-next-line @typescript-eslint/no-require-imports
const { createLocaleParityReport, STAGED_REQUIRED_PREFIXES } =
  require('../../../../scripts/locale-parity.cjs') as {
    createLocaleParityReport: () => {
      issues: Array<{ type: 'missing' | 'extra'; severity: 'error' | 'warn'; key: string }>;
    };
    STAGED_REQUIRED_PREFIXES: string[];
  };

describe('locale parity', () => {
  it('uses the tlbx product name in every translated value', () => {
    const localeRoot = resolve(__dirname, '../../../static/locales');
    const staleValues: string[] = [];

    for (const fileName of readdirSync(localeRoot).filter((name) => name.endsWith('.json'))) {
      const translations = JSON.parse(readFileSync(resolve(localeRoot, fileName), 'utf8')) as Record<
        string,
        string
      >;
      for (const [key, value] of Object.entries(translations)) {
        if (value.includes('MidTerm')) {
          staleValues.push(`${fileName}: ${key}`);
        }
      }
    }

    expect(staleValues).toEqual([]);
  });

  it('has no stale extra keys in localized files', () => {
    const report = createLocaleParityReport();
    const extras = report.issues.filter((issue) => issue.type === 'extra');
    expect(extras).toEqual([]);
  });

  it('has no missing keys for the staged high-visibility prefixes', () => {
    const report = createLocaleParityReport();
    const enforcedMissing = report.issues.filter(
      (issue) => issue.type === 'missing' && issue.severity === 'error',
    );
    expect(
      enforcedMissing,
      `Expected parity for prefixes: ${STAGED_REQUIRED_PREFIXES.join(', ')}`,
    ).toEqual([]);
  });

  it('resolves every static HTML translation reference', () => {
    const staticRoot = resolve(__dirname, '../../../static');
    const canonical = JSON.parse(
      readFileSync(resolve(staticRoot, 'locales/en.json'), 'utf8'),
    ) as Record<string, string>;
    const missing: string[] = [];
    const referencePattern = /data-i18n(?:-(?:title|placeholder|text))?=(['"])(.*?)\1/g;

    for (const fileName of readdirSync(staticRoot).filter((name) => name.endsWith('.html'))) {
      const html = readFileSync(resolve(staticRoot, fileName), 'utf8');
      for (const match of html.matchAll(referencePattern)) {
        const key = match[2];
        if (key && !(key in canonical)) {
          missing.push(`${fileName}: ${key}`);
        }
      }
    }

    expect(missing).toEqual([]);
  });
});

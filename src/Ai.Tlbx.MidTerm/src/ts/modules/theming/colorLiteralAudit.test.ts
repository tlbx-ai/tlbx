import { readFileSync, readdirSync, statSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const COLOR_LITERAL_PATTERN =
  /(?<![A-Za-z0-9_&-])#(?:[0-9a-fA-F]{3,4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})\b|rgba?\(|hsla?\(/;

const APP_CSS_PATH = 'src/Ai.Tlbx.MidTerm/src/static/css/app.css';

const AUDIT_EXCEPTIONS = new Set<string>([
  'src/Ai.Tlbx.MidTerm/src/static/index.html',
  'src/Ai.Tlbx.MidTerm/src/static/login.html',
  'src/Ai.Tlbx.MidTerm/src/static/site.webmanifest',
  'src/Ai.Tlbx.MidTerm/src/static/trust.html',
  'src/Ai.Tlbx.MidTerm/src/ts/constants.ts',
  'src/Ai.Tlbx.MidTerm/src/ts/modules/terminal/rgbBackgroundTransparency.ts',
  'src/Ai.Tlbx.MidTerm/src/ts/modules/theming/backgroundAppearance.ts',
  'src/Ai.Tlbx.MidTerm/src/ts/modules/theming/colorLiteralAudit.test.ts',
  'src/Ai.Tlbx.MidTerm/src/ts/modules/theming/cssThemes.test.ts',
  'src/Ai.Tlbx.MidTerm/src/ts/modules/theming/cssThemes.ts',
  'src/Ai.Tlbx.MidTerm/src/ts/modules/theming/terminalColorSchemes.ts',
  'src/Ai.Tlbx.MidTerm/src/ts/modules/theming/themes.ts',
]);

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const projectRoot = path.resolve(__dirname, '../../../..');
const repoRoot = path.resolve(projectRoot, '..', '..');

describe('runtime theme color audit', () => {
  it('keeps literal colors confined to theme definitions and metadata', () => {
    const violations = collectViolations();
    expect(violations).toEqual([]);
  });
});

function collectViolations(): string[] {
  const roots = [path.join(projectRoot, 'src', 'static'), path.join(projectRoot, 'src', 'ts')];

  const violations: string[] = [];

  for (const root of roots) {
    for (const filePath of walkFiles(root)) {
      const relativePath = path.relative(repoRoot, filePath).replace(/\\/g, '/');
      if (!shouldAuditFile(relativePath)) {
        continue;
      }

      const content = readContentForAudit(filePath, relativePath);
      const lines = content.split(/\r?\n/);
      lines.forEach((line, index) => {
        if (COLOR_LITERAL_PATTERN.test(line)) {
          violations.push(`${relativePath}:${index + 1}: ${line.trim()}`);
        }
      });
    }
  }

  return violations.sort();
}

function shouldAuditFile(relativePath: string): boolean {
  if (AUDIT_EXCEPTIONS.has(relativePath)) {
    return false;
  }

  if (relativePath.endsWith('.test.ts')) {
    return false;
  }

  const extension = path.extname(relativePath);
  return (
    extension === '.css' || extension === '.html' || extension === '.js' || extension === '.ts'
  );
}

function readContentForAudit(filePath: string, relativePath: string): string {
  const content = readFileSync(filePath, 'utf8');
  if (relativePath !== APP_CSS_PATH) {
    return content;
  }

  return stripFirstRootBlock(content);
}

function stripFirstRootBlock(content: string): string {
  const rootStart = content.indexOf(':root');
  if (rootStart < 0) {
    return content;
  }

  const braceStart = content.indexOf('{', rootStart);
  if (braceStart < 0) {
    return content;
  }

  let depth = 0;
  for (let i = braceStart; i < content.length; i += 1) {
    const char = content[i];
    if (char === '{') {
      depth += 1;
    } else if (char === '}') {
      depth -= 1;
      if (depth === 0) {
        return `${content.slice(0, rootStart)}${content.slice(i + 1)}`;
      }
    }
  }

  return content;
}

function walkFiles(dirPath: string): string[] {
  const entries = readdirSync(dirPath);
  const paths: string[] = [];

  for (const entry of entries) {
    const entryPath = path.join(dirPath, entry);
    const stats = statSync(entryPath);
    if (stats.isDirectory()) {
      paths.push(...walkFiles(entryPath));
      continue;
    }

    paths.push(entryPath);
  }

  return paths;
}

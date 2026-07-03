import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const source = readFileSync(path.join(__dirname, 'index.ts'), 'utf8');

describe('git repo cache wiring', () => {
  it('does not synthesize placeholder repo bindings from bare git statuses', () => {
    expect(source).toMatch(
      /function syncCachedRepoStatus\([\s\S]*?if \(!status\.repoRoot\) \{[\s\S]*?return;\s*\}/,
    );
  });
});

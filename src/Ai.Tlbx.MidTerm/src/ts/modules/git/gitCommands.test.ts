import { describe, expect, it } from 'vitest';

import { quoteGitPath } from './gitCommands';

describe('gitCommands', () => {
  it('quotes git paths for terminal handoff', () => {
    expect(quoteGitPath('src/my file.ts')).toBe('"src/my file.ts"');
    expect(quoteGitPath('src/"quoted".ts')).toBe('"src/\\"quoted\\".ts"');
  });
});

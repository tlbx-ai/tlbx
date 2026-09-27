import { describe, expect, it } from 'vitest';
import {
  QUOTED_ABSOLUTE_PATH_PATTERN,
  RELATIVE_PATH_PATTERN,
  UNC_PATH_PATTERN,
  UNIX_PATH_PATTERN,
  WIN_PATH_PATTERN,
  isLikelyFalsePositive,
  isValidPath,
  shouldRejectFolderMatch,
} from './fileRadar.patterns';

describe('UNIX_PATH_PATTERN', () => {
  it.each([
    ['Modified: /home/user/project/src/main.rs', '/home/user/project/src/main.rs'],
    [
      'at Module._compile (/app/node_modules/ts-node/src/index.ts:1618:12)',
      '/app/node_modules/ts-node/src/index.ts',
    ],
  ])('matches Unix path in: %s', (input, expected) => {
    const match = input.match(UNIX_PATH_PATTERN);
    expect(match).not.toBeNull();
    expect(match![1]).toBe(expected);
  });
});

describe('WIN_PATH_PATTERN', () => {
  it.each([
    ['Error CS1234: C:\\Users\\dev\\src\\Program.cs(42,10)', 'C:\\Users\\dev\\src\\Program.cs'],
    ['Copying C:/tools/cmake/bin/cmake.exe', 'C:/tools/cmake/bin/cmake.exe'],
    ['at Foo.Bar() in D:\\repos\\MyProject\\Foo.cs:line 15', 'D:\\repos\\MyProject\\Foo.cs'],
  ])('matches Windows path in: %s', (input, expected) => {
    const match = input.match(WIN_PATH_PATTERN);
    expect(match).not.toBeNull();
    expect(match![1]).toBe(expected);
  });
});

describe('UNC_PATH_PATTERN', () => {
  it('matches Windows UNC file path', () => {
    const input = 'Opening \\\\server\\share\\folder\\file.txt';
    const match = input.match(UNC_PATH_PATTERN);
    expect(match).not.toBeNull();
    expect(match![1]).toBe('\\\\server\\share\\folder\\file.txt');
  });
});

describe('QUOTED_ABSOLUTE_PATH_PATTERN', () => {
  it('matches quoted Windows absolute path with spaces', () => {
    const input = 'Launching "C:\\Program Files\\Git\\bin\\bash.exe"';
    const match = input.match(QUOTED_ABSOLUTE_PATH_PATTERN);
    expect(match).not.toBeNull();
    expect(match![1]).toBe('C:\\Program Files\\Git\\bin\\bash.exe');
  });

  it('matches quoted Unix absolute path with spaces', () => {
    const input = "cat '/home/user/My Project/file.txt'";
    const match = input.match(QUOTED_ABSOLUTE_PATH_PATTERN);
    expect(match).not.toBeNull();
    expect(match![1]).toBe('/home/user/My Project/file.txt');
  });
});

describe('RELATIVE_PATH_PATTERN', () => {
  it('matches ../shared/utils.ts but isValidPath rejects .. traversal', () => {
    const match = '../shared/utils.ts'.match(RELATIVE_PATH_PATTERN);
    expect(match).not.toBeNull();
    expect(match![1]).toBe('../shared/utils.ts');
    expect(isValidPath('../shared/utils.ts')).toBe(false);
  });
});

describe('URLs — current filter behavior', () => {
  it('shouldRejectFolderMatch catches URL schemes in folder paths', () => {
    expect(shouldRejectFolderMatch('http://')).toBe(true);
    expect(shouldRejectFolderMatch('https://example.com/')).toBe(true);
    expect(shouldRejectFolderMatch('ftp://files/')).toBe(true);
  });

  it('shouldRejectFolderMatch catches scheme-less URL fragments', () => {
    expect(shouldRejectFolderMatch('example.com/')).toBe(true);
  });

  it('RELATIVE_PATH_PATTERN no longer extracts URL fragments with file extensions', () => {
    const m = 'ftp://files.server.com/pub/release.tar.gz'.match(RELATIVE_PATH_PATTERN);
    expect(m).toBeNull();
  });
});

describe('FQN heuristic preserves real file patterns', () => {
  it('paths WITH separators are never caught by FQN heuristic', () => {
    expect(isLikelyFalsePositive('src/Ai.Tlbx.MidTerm/Services/FileEndpoints.cs')).toBe(false);
    expect(isLikelyFalsePositive('node_modules/@angular/core/index.ts')).toBe(false);
  });
});

import { describe, expect, it } from 'vitest';
import {
  FOLDER_PATH_PATTERN,
  KNOWN_FILE_PATTERN,
  QUOTED_ABSOLUTE_PATH_PATTERN,
  RELATIVE_PATH_PATTERN,
  UNC_PATH_PATTERN,
  UNIX_PATH_PATTERN,
  UNIX_PATH_PATTERN_GLOBAL,
  WIN_PATH_PATTERN,
  WIN_PATH_PATTERN_GLOBAL,
  isFragmentOfAbsolutePath,
  isLikelyFalsePositive,
  isValidPath,
  shouldRejectFolderMatch,
  shouldRejectKnownFileMatch,
  shouldRejectRelativeMatch,
} from './fileRadar.patterns';

// ===========================================================================
// Category A: Unix Absolute Paths (UNIX_PATH_PATTERN)
// ===========================================================================

describe('UNIX_PATH_PATTERN', () => {
  it.each([
    ['Modified: /home/user/project/src/main.rs', '/home/user/project/src/main.rs'],
    ['Compiled /usr/local/lib/libfoo.so.2.1', '/usr/local/lib/libfoo.so.2.1'],
    ['drwxr-xr-x  /home/user/.config/nvim', '/home/user/.config/nvim'],
    ['GOPATH=/home/user/go', '/home/user/go'],
    [
      'at Module._compile (/app/node_modules/ts-node/src/index.ts:1618:12)',
      '/app/node_modules/ts-node/src/index.ts',
    ],
    ['rsync user@host:/var/www/html/index.html .', '/var/www/html/index.html'],
  ])('matches Unix path in: %s', (input, expected) => {
    const match = input.match(UNIX_PATH_PATTERN);
    expect(match).not.toBeNull();
    expect(match![1]).toBe(expected);
  });

  it('does not match path preceded by alphanumeric (negative lookbehind)', () => {
    const input = 'node_modules/@xterm/xterm/lib/index.js:42';
    const match = input.match(UNIX_PATH_PATTERN);
    // The pattern has a negative lookbehind for alphanumeric chars
    // "s/" — 's' is alphanumeric, so /xterm/... should not match as a Unix absolute path
    if (match) {
      // If it matches at all, it should not start at the / after "modules"
      expect(match[1]).not.toBe('/xterm/xterm/lib/index.js');
    }
  });

  it('matches /bin and now treats it as a valid absolute folder', () => {
    const match = '/bin'.match(UNIX_PATH_PATTERN);
    expect(match).not.toBeNull();
    expect(isValidPath('/bin')).toBe(true);
  });
});

// ===========================================================================
// Category B: Windows Absolute Paths (WIN_PATH_PATTERN)
// ===========================================================================

describe('WIN_PATH_PATTERN', () => {
  it.each([
    ['Error CS1234: C:\\Users\\dev\\src\\Program.cs(42,10)', 'C:\\Users\\dev\\src\\Program.cs'],
    ['Copying C:/tools/cmake/bin/cmake.exe', 'C:/tools/cmake/bin/cmake.exe'],
    ['APPDATA=C:\\Users\\johan\\AppData\\Roaming', 'C:\\Users\\johan\\AppData\\Roaming'],
    ['at Foo.Bar() in D:\\repos\\MyProject\\Foo.cs:line 15', 'D:\\repos\\MyProject\\Foo.cs'],
    [
      'nuget restore "E:\\packages\\Newtonsoft.Json.13.0.3"',
      'E:\\packages\\Newtonsoft.Json.13.0.3',
    ],
    ['Certificate: C:\\Users\\user\\.midterm\\cert.pfx', 'C:\\Users\\user\\.midterm\\cert.pfx'],
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

// ===========================================================================
// Category C: Relative Paths (RELATIVE_PATH_PATTERN)
// ===========================================================================

describe('RELATIVE_PATH_PATTERN', () => {
  it.each([
    ['Modified src/main.ts', 'src/main.ts'],
    ['Created ./output/report.pdf', './output/report.pdf'],
    ['Edit src\\Ai\\Services\\Foo.cs', 'src\\Ai\\Services\\Foo.cs'],
    ['comparing old.json and new.json', 'old.json'],
    ['Built dist/terminal.min.js', 'dist/terminal.min.js'],
    ['node_modules/@angular/core/index.ts', 'node_modules/@angular/core/index.ts'],
  ])('matches relative path in: %s', (input, expected) => {
    const match = input.match(RELATIVE_PATH_PATTERN);
    expect(match).not.toBeNull();
    expect(match![1]).toBe(expected);
  });

  it('matches ../shared/utils.ts but isValidPath rejects .. traversal', () => {
    const match = '../shared/utils.ts'.match(RELATIVE_PATH_PATTERN);
    expect(match).not.toBeNull();
    expect(match![1]).toBe('../shared/utils.ts');
    expect(isValidPath('../shared/utils.ts')).toBe(false);
  });
});

// ===========================================================================
// Category D: Folder Paths (FOLDER_PATH_PATTERN)
// ===========================================================================

describe('FOLDER_PATH_PATTERN', () => {
  it.each([
    ['Scanning src/', 'src/'],
    ['Deleted ./tmp/cache/', './tmp/cache/'],
    ['Looking in src\\components\\', 'src\\components\\'],
    ['Ignoring .git/', '.git/'],
  ])('matches folder path in: %s', (input, expected) => {
    const match = input.match(FOLDER_PATH_PATTERN);
    expect(match).not.toBeNull();
    expect(match![1]).toBe(expected);
  });
});

// ===========================================================================
// Category E: Known Extensionless Files (KNOWN_FILE_PATTERN)
// ===========================================================================

describe('KNOWN_FILE_PATTERN', () => {
  it.each([
    ['Modified Dockerfile', 'Dockerfile'],
    ['Edited .gitignore', '.gitignore'],
    ['Loaded .env.production', '.env.production'],
    ['Updated docker/Dockerfile', 'docker/Dockerfile'],
    ['Modified src/api/.prettierrc', 'src/api/.prettierrc'],
  ])('matches known file in: %s', (input, expected) => {
    const match = input.match(KNOWN_FILE_PATTERN);
    expect(match).not.toBeNull();
    expect(match![1]).toBe(expected);
  });
});

// ===========================================================================
// Category F: False Positive Filtering (isLikelyFalsePositive)
// ===========================================================================

describe('isLikelyFalsePositive', () => {
  describe('returns true for false positives', () => {
    it.each([
      ['1.2.3', 'version number (3-part)'],
      ['3.12', 'version number (2-part)'],
      ['e.g.', 'abbreviation'],
      ['google.com', 'domain TLD (com)'],
      ['claude.ai', 'domain TLD (ai)'],
    ])('%s — %s', (input) => {
      expect(isLikelyFalsePositive(input)).toBe(true);
    });
  });

  describe('returns false for real file paths', () => {
    it.each([['output.pdf', 'PDF file']])('%s — %s', (input) => {
      expect(isLikelyFalsePositive(input)).toBe(false);
    });
  });
});

// ===========================================================================
// Category G: Path Validation (isValidPath)
// ===========================================================================

describe('isValidPath', () => {
  describe('rejects invalid paths', () => {
    it.each([
      ['', 'empty string'],
      ['/', 'single slash'],
      ['a', 'single character'],
      ['../../../etc/passwd', 'traversal attack'],
    ])('%s — %s', (input) => {
      expect(isValidPath(input)).toBe(false);
    });
  });

  describe('accepts valid paths', () => {
    it.each([
      ['ab', 'minimal 2-char path'],
      ['/bin', 'Unix absolute folder'],
      ['C:\\file.txt', 'Windows absolute'],
    ])('%s — %s', (input) => {
      expect(isValidPath(input)).toBe(true);
    });
  });
});

// ===========================================================================
// Category H: matchCallback Filters
// ===========================================================================

describe('shouldRejectFolderMatch', () => {
  it.each([
    ['D:/bar/', true, 'Windows drive with forward slash'],
    ['https://example.com/', true, 'HTTPS URL scheme'],
    ['src/components/', false, 'valid relative folder'],
  ])('%s → rejected=%s (%s)', (input, expected) => {
    expect(shouldRejectFolderMatch(input)).toBe(expected);
  });
});

describe('shouldRejectKnownFileMatch', () => {
  it.each([
    ['/etc/Dockerfile', true, 'absolute Unix path'],
    ['C:\\Dockerfile', true, 'Windows absolute path'],
    ['docker/Dockerfile', false, 'valid relative path'],
    ['Makefile', false, 'bare known file'],
  ])('%s → rejected=%s (%s)', (input, expected) => {
    expect(shouldRejectKnownFileMatch(input)).toBe(expected);
  });
});

describe('shouldRejectRelativeMatch', () => {
  it.each([
    ['/home/user/file.ts', true, 'absolute Unix path'],
    ['google.com', true, 'false positive (TLD)'],
    ['1.2.3', true, 'false positive (version)'],
    ['src/main.ts', false, 'valid relative path'],
  ])('%s → rejected=%s (%s)', (input, expected) => {
    expect(shouldRejectRelativeMatch(input)).toBe(expected);
  });
});

// ===========================================================================
// Category I: Fragment of Absolute Path Detection (isFragmentOfAbsolutePath)
// ===========================================================================

describe('isFragmentOfAbsolutePath', () => {
  it('detects fragment position within Q:\\ absolute path', () => {
    const input =
      'Q:\\repos\\MidTermWorkspace3\\src\\Ai.Tlbx.MidTerm.UnitTests\\Ai.Tlbx.MidTerm.UnitTests.csproj';
    const idx = input.indexOf('repos\\');
    expect(idx).toBeGreaterThan(0);
    expect(isFragmentOfAbsolutePath({ input, index: idx })).toBe(true);
  });

  it('detects fragment position within C:\\ absolute path', () => {
    const input = 'C:\\Users\\dev\\src\\Program.cs';
    const idx = input.indexOf('Users\\');
    expect(idx).toBeGreaterThan(0);
    expect(isFragmentOfAbsolutePath({ input, index: idx })).toBe(true);
  });

  it('detects fragment position within D:\\ absolute path', () => {
    const input = 'D:\\repos\\MyProject\\Foo.cs';
    const idx = input.indexOf('repos\\');
    expect(idx).toBeGreaterThan(0);
    expect(isFragmentOfAbsolutePath({ input, index: idx })).toBe(true);
  });

  it('does NOT flag a genuine relative path', () => {
    const input = 'Modified src/main.ts';
    const m = input.match(RELATIVE_PATH_PATTERN);
    expect(m).not.toBeNull();
    expect(isFragmentOfAbsolutePath(m!)).toBe(false);
  });

  it('does NOT flag a bare filename', () => {
    const input = 'Opened settings.json';
    const m = input.match(RELATIVE_PATH_PATTERN);
    expect(m).not.toBeNull();
    expect(isFragmentOfAbsolutePath(m!)).toBe(false);
  });

  it('does NOT flag a dot-relative path', () => {
    const input = 'Created ./output/report.pdf';
    const m = input.match(RELATIVE_PATH_PATTERN);
    expect(m).not.toBeNull();
    expect(isFragmentOfAbsolutePath(m!)).toBe(false);
  });

  it('returns false when match has no input/index', () => {
    expect(isFragmentOfAbsolutePath({})).toBe(false);
    expect(isFragmentOfAbsolutePath({ input: 'test', index: undefined })).toBe(false);
  });

  it('returns false when match index is < 3', () => {
    expect(isFragmentOfAbsolutePath({ input: 'src/main.ts', index: 0 })).toBe(false);
    expect(isFragmentOfAbsolutePath({ input: 'xsrc/main.ts', index: 1 })).toBe(false);
    expect(isFragmentOfAbsolutePath({ input: '..src/main.ts', index: 2 })).toBe(false);
  });

  it('detects folder fragment position of absolute path', () => {
    const input = 'C:\\Users\\dev\\src\\';
    const idx = input.indexOf('Users\\');
    expect(idx).toBeGreaterThan(0);
    expect(isFragmentOfAbsolutePath({ input, index: idx })).toBe(true);
  });
});

// ===========================================================================
// Category K: Real-World Terminal Snippets (Full Pipeline)
// ===========================================================================

describe('Real-world terminal output', () => {
  function extractPaths(text: string): string[] {
    const paths: string[] = [];

    // Reset global regexes
    UNIX_PATH_PATTERN_GLOBAL.lastIndex = 0;
    WIN_PATH_PATTERN_GLOBAL.lastIndex = 0;

    let m: RegExpExecArray | null;
    while ((m = UNIX_PATH_PATTERN_GLOBAL.exec(text)) !== null) {
      if (m[1] && isValidPath(m[1])) paths.push(m[1]);
    }
    while ((m = WIN_PATH_PATTERN_GLOBAL.exec(text)) !== null) {
      if (m[1] && isValidPath(m[1])) paths.push(m[1]);
    }

    const relMatch = text.match(RELATIVE_PATH_PATTERN);
    if (relMatch?.[1] && !shouldRejectRelativeMatch(relMatch[1]) && isValidPath(relMatch[1])) {
      paths.push(relMatch[1]);
    }

    const knownMatch = text.match(KNOWN_FILE_PATTERN);
    if (knownMatch?.[1] && !shouldRejectKnownFileMatch(knownMatch[1])) {
      paths.push(knownMatch[1]);
    }

    const folderMatch = text.match(FOLDER_PATH_PATTERN);
    if (folderMatch?.[1] && !shouldRejectFolderMatch(folderMatch[1])) {
      paths.push(folderMatch[1]);
    }

    return [...new Set(paths)];
  }

  it('git diff stat line', () => {
    const paths = extractPaths('src/main.ts | 42 +++---');
    expect(paths).toContain('src/main.ts');
  });

  it('test runner PASS line', () => {
    const paths = extractPaths('PASS tests/unit/auth.test.ts (2.34s)');
    expect(paths).toContain('tests/unit/auth.test.ts');
  });

  it('AI chat mentioning a file', () => {
    const paths = extractPaths("I've updated src/Services/FileEndpoints.cs");
    expect(paths).toContain('src/Services/FileEndpoints.cs');
  });

  it('AI chat mentioning multiple files', () => {
    const paths = extractPaths('check settings.json and .editorconfig');
    expect(paths).toContain('settings.json');
    expect(paths).toContain('.editorconfig');
  });

  it('AI chat mentioning deep path', () => {
    const paths = extractPaths('modify src/ts/modules/terminal/fileLinks.ts');
    expect(paths).toContain('src/ts/modules/terminal/fileLinks.ts');
  });

  it('Node.js stack trace — global pattern now captures absolute path before :line:col', () => {
    const paths = extractPaths(
      'at Object.<anonymous> (/home/user/project/node_modules/@xterm/xterm/lib/index.js:42:10)',
    );
    expect(paths).toContain('/home/user/project/node_modules/@xterm/xterm/lib/index.js');
    // Relative-path extraction still catches index.js variants too.
    const hasRelativePath = paths.some((p) => p.includes('index.js'));
    expect(hasRelativePath).toBe(true);
  });

  it('npm warning with version number — no paths', () => {
    const paths = extractPaths('npm warn deprecated package@1.2.3');
    // Version numbers should be filtered, no meaningful file paths
    const realFiles = paths.filter((p) => !isLikelyFalsePositive(p));
    expect(realFiles).toHaveLength(0);
  });

  it('docker build command', () => {
    const paths = extractPaths('docker build -f docker/Dockerfile .');
    expect(paths).toContain('docker/Dockerfile');
  });

  it('pytest invocation', () => {
    const paths = extractPaths('python -m pytest tests/test_api.py::TestLogin');
    expect(paths).toContain('tests/test_api.py');
  });

  it('Python traceback', () => {
    const paths = extractPaths('File "scripts/deploy.py", line 23');
    expect(paths).toContain('scripts/deploy.py');
  });

  it('Java compile command with multiple paths', () => {
    const paths = extractPaths('javac -cp lib/gson-2.10.1.jar src/Main.java');
    // At least one of these should be detected
    const hasJar = paths.some((p) => p.includes('gson'));
    const hasJava = paths.some((p) => p.includes('Main.java'));
    expect(hasJar || hasJava).toBe(true);
  });

  it('cmake build directory', () => {
    const paths = extractPaths('cmake -S . -B build/');
    expect(paths).toContain('build/');
  });

  it('TypeScript error with line:col', () => {
    const paths = extractPaths('Error at src/foo.ts:42:10');
    expect(paths).toContain('src/foo.ts');
  });

  it('quoted paths (double quotes)', () => {
    const paths = extractPaths('"src/main.ts"');
    expect(paths).toContain('src/main.ts');
  });

  it('quoted paths (single quotes)', () => {
    const paths = extractPaths("'src/main.ts'");
    expect(paths).toContain('src/main.ts');
  });
});

// ===========================================================================
// Category L: URLs That Must NOT Match As Paths
// ===========================================================================

describe('URLs — current filter behavior', () => {
  // In production, URLs are handled by xterm web-links addon (higher priority)
  // before file radar patterns run. These tests document what the regex layer
  // itself would match — not all URL fragments are filtered.

  it('http://localhost:2000/api/health — no relative match (no extension)', () => {
    const relMatch = 'http://localhost:2000/api/health'.match(RELATIVE_PATH_PATTERN);
    // "health" has no file extension, so RELATIVE_PATH_PATTERN won't match
    expect(relMatch).toBeNull();
  });

  it('shouldRejectFolderMatch catches URL schemes in folder paths', () => {
    expect(shouldRejectFolderMatch('http://')).toBe(true);
    expect(shouldRejectFolderMatch('https://example.com/')).toBe(true);
    expect(shouldRejectFolderMatch('ftp://files/')).toBe(true);
  });

  it('shouldRejectFolderMatch catches scheme-less URL fragments', () => {
    expect(shouldRejectFolderMatch('example.com/')).toBe(true);
  });

  it('isLikelyFalsePositive catches single-segment TLD domains', () => {
    // Matches like "example.com" from URLs are caught by TLD check
    expect(isLikelyFalsePositive('example.com')).toBe(true);
    expect(isLikelyFalsePositive('github.io')).toBe(true);
  });

  it('isLikelyFalsePositive catches multi-segment domains', () => {
    expect(isLikelyFalsePositive('docs.microsoft.com')).toBe(true);
    expect(isLikelyFalsePositive('api.example.com')).toBe(true);
  });

  it('RELATIVE_PATH_PATTERN no longer extracts URL fragments with file extensions', () => {
    const m = 'ftp://files.server.com/pub/release.tar.gz'.match(RELATIVE_PATH_PATTERN);
    expect(m).toBeNull();
  });
});

// ===========================================================================
// Category M: Global Pattern Scanning
// ===========================================================================

describe('Global pattern scanning', () => {
  it('UNIX_PATH_PATTERN_GLOBAL finds multiple paths', () => {
    const text = 'cp /etc/nginx/nginx.conf /etc/nginx/nginx.conf.bak';
    UNIX_PATH_PATTERN_GLOBAL.lastIndex = 0;
    const matches: string[] = [];
    let m: RegExpExecArray | null;
    while ((m = UNIX_PATH_PATTERN_GLOBAL.exec(text)) !== null) {
      if (m[1]) matches.push(m[1]);
    }
    expect(matches.length).toBeGreaterThanOrEqual(1);
    expect(matches).toContain('/etc/nginx/nginx.conf');
  });

  it('WIN_PATH_PATTERN_GLOBAL finds Windows paths in text', () => {
    const text = 'copy C:\\src\\file.cs D:\\dest\\file.cs';
    WIN_PATH_PATTERN_GLOBAL.lastIndex = 0;
    const matches: string[] = [];
    let m: RegExpExecArray | null;
    while ((m = WIN_PATH_PATTERN_GLOBAL.exec(text)) !== null) {
      if (m[1]) matches.push(m[1]);
    }
    expect(matches.length).toBeGreaterThanOrEqual(1);
    expect(matches[0]).toBe('C:\\src\\file.cs');
  });

  it('UNIX_PATH_PATTERN_GLOBAL does not match inside words', () => {
    const text = 'node_modules/@xterm/xterm/lib/index.js';
    UNIX_PATH_PATTERN_GLOBAL.lastIndex = 0;
    const matches: string[] = [];
    let m: RegExpExecArray | null;
    while ((m = UNIX_PATH_PATTERN_GLOBAL.exec(text)) !== null) {
      if (m[1]) matches.push(m[1]);
    }
    // Should not match /xterm/... because it's inside a word
    const absMatches = matches.filter((p) => p.startsWith('/'));
    expect(absMatches).toHaveLength(0);
  });
});

// ===========================================================================
// Category N: Real dotnet test / build output (from actual session)
// ===========================================================================

describe('dotnet test output — true positives', () => {
  it('dotnet test command — Windows absolute path to csproj', () => {
    const input =
      'dotnet test "Q:\\repos\\MidTermWorkspace3\\src\\Ai.Tlbx.MidTerm.UnitTests\\Ai.Tlbx.MidTerm.UnitTests.csproj" --verbosity normal';
    const m = input.match(WIN_PATH_PATTERN);
    expect(m).not.toBeNull();
    expect(m![1]).toBe(
      'Q:\\repos\\MidTermWorkspace3\\src\\Ai.Tlbx.MidTerm.UnitTests\\Ai.Tlbx.MidTerm.UnitTests.csproj',
    );
  });

  it('vitest RUN line — Windows path with forward slashes', () => {
    const input = 'RUN  v4.0.18 Q:/repos/MidTermWorkspace3';
    const m = input.match(WIN_PATH_PATTERN);
    expect(m).not.toBeNull();
    expect(m![1]).toBe('Q:/repos/MidTermWorkspace3');
  });

  it('vitest passed file — relative path with dots in directories', () => {
    const input =
      '✓ src/Ai.Tlbx.MidTerm/src/ts/modules/terminal/fileRadar.patterns.test.ts (117 tests) 22ms';
    const m = input.match(RELATIVE_PATH_PATTERN);
    expect(m).not.toBeNull();
    expect(m![1]).toBe('src/Ai.Tlbx.MidTerm/src/ts/modules/terminal/fileRadar.patterns.test.ts');
  });

  it('dotnet build output line — project arrow notation', () => {
    const input =
      'Ai.Tlbx.MidTerm -> Q:\\repos\\MidTermWorkspace3\\src\\Ai.Tlbx.MidTerm\\bin\\Debug\\net10.0\\mt.dll';
    const m = input.match(WIN_PATH_PATTERN);
    expect(m).not.toBeNull();
    expect(m![1]).toBe(
      'Q:\\repos\\MidTermWorkspace3\\src\\Ai.Tlbx.MidTerm\\bin\\Debug\\net10.0\\mt.dll',
    );
  });

  it('build output — js asset paths', () => {
    expect('js/audio-processor.js'.match(RELATIVE_PATH_PATTERN)![1]).toBe('js/audio-processor.js');
    expect('js/webAudioAccess.js'.match(RELATIVE_PATH_PATTERN)![1]).toBe('js/webAudioAccess.js');
  });

  it('prose mention — bare .cs filename', () => {
    const input = '20 C# endpoint tests in FileEndpointsTests.cs';
    const m = input.match(RELATIVE_PATH_PATTERN);
    expect(m).not.toBeNull();
    expect(m![1]).toBe('FileEndpointsTests.cs');
  });

  it('prose mention — bare .test.ts filename', () => {
    const input = '117 TypeScript tests in fileRadar.patterns.test.ts';
    const m = input.match(RELATIVE_PATH_PATTERN);
    expect(m).not.toBeNull();
    expect(m![1]).toBe('fileRadar.patterns.test.ts');
  });
});

describe('dotnet test output — false positives now filtered', () => {
  it('.NET FQN (4+ dots) is caught by isLikelyFalsePositive', () => {
    const input =
      'Passed Ai.Tlbx.MidTerm.UnitTests.FileEndpointsTests.ValidatePath_RejectsRelativePath [< 1 ms]';
    const m = input.match(RELATIVE_PATH_PATTERN);
    expect(m).not.toBeNull();
    expect(m![1]).toBe('Ai.Tlbx.MidTerm.UnitTests.FileEndpointsTests.ValidatePath');
    // 5 dots, no path separator → FQN heuristic catches it
    expect(isLikelyFalsePositive(m![1])).toBe(true);
    expect(shouldRejectRelativeMatch(m![1])).toBe(true);
  });

  it('.NET FQN — AuthServiceTests also caught', () => {
    const input =
      'Passed Ai.Tlbx.MidTerm.UnitTests.AuthServiceTests.RateLimit_FiveFailures_30SecondLockout [3 ms]';
    const m = input.match(RELATIVE_PATH_PATTERN);
    expect(m).not.toBeNull();
    expect(m![1]).toContain('Ai.Tlbx.MidTerm');
    // 4+ dots → caught
    expect(shouldRejectRelativeMatch(m![1])).toBe(true);
  });

  it('C# method call caught by PascalCase extension heuristic', () => {
    const input = 'Results.Forbid()';
    const m = input.match(RELATIVE_PATH_PATTERN);
    expect(m).not.toBeNull();
    expect(m![1]).toBe('Results.Forbid');
    // "Forbid" is 6 chars, starts uppercase, no separator → caught
    expect(isLikelyFalsePositive('Results.Forbid')).toBe(true);
    expect(shouldRejectRelativeMatch(m![1])).toBe(true);
  });

  it('.NET project name caught by PascalCase extension heuristic', () => {
    const input =
      'Ai.Tlbx.MidTerm -> Q:\\repos\\MidTermWorkspace3\\src\\Ai.Tlbx.MidTerm\\bin\\Debug\\net10.0\\mt.dll';
    const rel = input.match(RELATIVE_PATH_PATTERN);
    expect(rel).not.toBeNull();
    expect(rel![1]).toBe('Ai.Tlbx.MidTerm');
    // "MidTerm" is 7 chars, starts uppercase → caught
    expect(isLikelyFalsePositive('Ai.Tlbx.MidTerm')).toBe(true);
  });
});

describe('dotnet test output — remaining edge cases', () => {
  it('API endpoint path still matches as Unix absolute path', () => {
    const input = '/api/files/resolve';
    const m = input.match(UNIX_PATH_PATTERN);
    expect(m).not.toBeNull();
    expect(m![1]).toBe('/api/files/resolve');
    // Has path separators, so FQN heuristic doesn't apply
    expect(isValidPath('/api/files/resolve')).toBe(true);
  });

  it('glob pattern fragment no longer matches as relative path', () => {
    const input = 'ESLint config: Added **/*.test.ts to ignores';
    const m = input.match(RELATIVE_PATH_PATTERN);
    expect(m).toBeNull();
  });

  it('C# method name with slash still matches as folder', () => {
    const input =
      '2 MuxProtocolTests referencing deleted CreateProcessEventFrame/CreateForegroundChangeFrame methods';
    const m = input.match(FOLDER_PATH_PATTERN);
    expect(m).not.toBeNull();
    expect(m![1]).toBe('CreateProcessEventFrame/');
  });
});

// ===========================================================================
// Category P: FQN heuristic does NOT break real files
// ===========================================================================

describe('FQN heuristic preserves real file patterns', () => {
  it.each([
    ['package.json', 'common config file'],
    ['main.ts', 'source file'],
    ['file.test.ts', 'test file (2 dots)'],
    ['.env.production', 'dotenv variant (2 dots)'],
  ])('%s — %s is NOT a false positive', (input) => {
    expect(isLikelyFalsePositive(input)).toBe(false);
  });

  it('file.test.spec.ts (3 dots) is NOT falsely rejected', () => {
    // 3 dots but extension is "ts" (lowercase, 2 chars) → safe
    expect(isLikelyFalsePositive('file.test.spec.ts')).toBe(false);
  });

  it('jquery.min.js.map (3 dots) is NOT falsely rejected', () => {
    expect(isLikelyFalsePositive('jquery.min.js.map')).toBe(false);
  });

  it('Microsoft.Extensions.DependencyInjection.dll (3 dots) is NOT falsely rejected', () => {
    // 3 dots, but extension "dll" is lowercase and short → safe
    expect(isLikelyFalsePositive('Microsoft.Extensions.DependencyInjection.dll')).toBe(false);
  });

  it('paths WITH separators are never caught by FQN heuristic', () => {
    expect(isLikelyFalsePositive('src/Ai.Tlbx.MidTerm/Services/FileEndpoints.cs')).toBe(false);
    expect(isLikelyFalsePositive('node_modules/@angular/core/index.ts')).toBe(false);
  });
});

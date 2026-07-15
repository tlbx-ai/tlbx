/**
 * File Radar Patterns Module
 *
 * Pure regex patterns and validation logic for detecting file paths in terminal output.
 * Extracted for testability - no DOM, fetch, or xterm dependencies.
 */

// ===========================================================================
// Regex Patterns - Compiled once at module load
// ===========================================================================

const LOCAL_PREFIX_LOOKBEHIND = '(?<![a-zA-Z0-9_.@-])';
const GLOBAL_PREFIX_BOUNDARY = '(?:^|[\\s"\'`(\\[{<])';
const GLOBAL_SUFFIX_BOUNDARY = '(?=$|[\\s"\'`)\\]}>.,;!?]|:(?=\\d))';

const WIN_SEGMENT = String.raw`[^<>:"/\\|?*()\r\n\s]+`;
const UNC_SEGMENT = String.raw`[^\\/()\r\n\s]+`;
const UNIX_SEGMENT = String.raw`[^/:"'\`<>|()\r\n\s]+`;

const WIN_SEGMENT_QUOTED = String.raw`[^<>:"/\\|?*\r\n]+`;
const UNC_SEGMENT_QUOTED = String.raw`[^\\/\r\n]+`;
const UNIX_SEGMENT_QUOTED = String.raw`[^/:"'\`<>|\r\n]+`;

const WIN_ABSOLUTE_CORE = String.raw`[A-Za-z]:[\\/](?:${WIN_SEGMENT}[\\/])*(?:${WIN_SEGMENT})?[\\/]?`;
const UNC_ABSOLUTE_CORE = String.raw`\\\\${UNC_SEGMENT}[\\/]+${UNC_SEGMENT}(?:[\\/]+${UNC_SEGMENT})*[\\/]?`;
const UNIX_ABSOLUTE_CORE = String.raw`\/(?:${UNIX_SEGMENT}\/)*(?:${UNIX_SEGMENT})\/?`;

const WIN_ABSOLUTE_QUOTED_CORE = String.raw`[A-Za-z]:[\\/](?:${WIN_SEGMENT_QUOTED}[\\/])*(?:${WIN_SEGMENT_QUOTED})?[\\/]?`;
const UNC_ABSOLUTE_QUOTED_CORE = String.raw`\\\\${UNC_SEGMENT_QUOTED}[\\/]+${UNC_SEGMENT_QUOTED}(?:[\\/]+${UNC_SEGMENT_QUOTED})*[\\/]?`;
const UNIX_ABSOLUTE_QUOTED_CORE = String.raw`\/(?:${UNIX_SEGMENT_QUOTED}\/)*(?:${UNIX_SEGMENT_QUOTED})\/?`;

/**
 * Unix absolute paths: /path/to/file or /path/to/file.ext
 * Negative lookbehind prevents matching /foo/bar inside src/foo/bar (relative paths)
 */
export const UNIX_PATH_PATTERN = new RegExp(`${LOCAL_PREFIX_LOOKBEHIND}(${UNIX_ABSOLUTE_CORE})`);

/**
 * Windows absolute paths: C:\path\file or C:/path/file
 */
export const WIN_PATH_PATTERN = new RegExp(`${LOCAL_PREFIX_LOOKBEHIND}(${WIN_ABSOLUTE_CORE})`);

/**
 * Windows UNC absolute paths: \\server\share\dir\file or //server/share/dir/file
 */
export const UNC_PATH_PATTERN = new RegExp(`${LOCAL_PREFIX_LOOKBEHIND}(${UNC_ABSOLUTE_CORE})`);

/**
 * Absolute paths wrapped in quotes, used to support paths containing spaces.
 */
export const QUOTED_ABSOLUTE_PATH_PATTERN = new RegExp(
  `(?:["'\`])((?:${UNC_ABSOLUTE_QUOTED_CORE}|${WIN_ABSOLUTE_QUOTED_CORE}|${UNIX_ABSOLUTE_QUOTED_CORE}))(?:["'\`])`,
);

/**
 * Global versions for scanning terminal output (anchored with whitespace/quotes)
 */
export const UNIX_PATH_PATTERN_GLOBAL = new RegExp(
  `${GLOBAL_PREFIX_BOUNDARY}(${UNIX_ABSOLUTE_CORE})${GLOBAL_SUFFIX_BOUNDARY}`,
  'g',
);
export const WIN_PATH_PATTERN_GLOBAL = new RegExp(
  `${GLOBAL_PREFIX_BOUNDARY}(${WIN_ABSOLUTE_CORE})${GLOBAL_SUFFIX_BOUNDARY}`,
  'g',
);
export const UNC_PATH_PATTERN_GLOBAL = new RegExp(
  `${GLOBAL_PREFIX_BOUNDARY}(${UNC_ABSOLUTE_CORE})${GLOBAL_SUFFIX_BOUNDARY}`,
  'g',
);
export const QUOTED_ABSOLUTE_PATH_PATTERN_GLOBAL = new RegExp(
  `(?:["'\`])((?:${UNC_ABSOLUTE_QUOTED_CORE}|${WIN_ABSOLUTE_QUOTED_CORE}|${UNIX_ABSOLUTE_QUOTED_CORE}))(?:["'\`])`,
  'g',
);

/**
 * Relative path pattern - matches any filename.extension pattern.
 * Extension: 1-15 chars, must start with letter.
 * Supports both / and \ path separators.
 */
export const RELATIVE_PATH_PATTERN =
  /(?:^|[\s"'`([{<])((?:\.\.?[/\\])?(?:[\w.@-]+[/\\])*[\w.@-]+\.[a-zA-Z][a-zA-Z0-9]{0,14})/;

/**
 * Folder path pattern - matches paths ending with / or \
 */
export const FOLDER_PATH_PATTERN = /(?:^|[\s"'`([{<])((?:\.\.?[/\\])?(?:[\w.@-]+[/\\])+)/;

/**
 * Well-known files without extensions.
 */
export const KNOWN_EXTENSIONLESS_LIST = [
  'Dockerfile',
  'Makefile',
  'Vagrantfile',
  'Gemfile',
  'Rakefile',
  'Procfile',
  'Justfile',
  'Taskfile',
  'Brewfile',
  'Podfile',
  'Fastfile',
  'Appfile',
  'LICENSE',
  'LICENCE',
  'CHANGELOG',
  'README',
  'CONTRIBUTING',
  'AUTHORS',
  '.gitignore',
  '.gitattributes',
  '.gitmodules',
  '.editorconfig',
  '.dockerignore',
  '.eslintignore',
  '.prettierignore',
  '.npmignore',
  '.env',
  '.env.local',
  '.env.production',
  '.env.development',
  '.prettierrc',
  '.eslintrc',
  '.babelrc',
  '.browserslistrc',
];

const KNOWN_FILE_NAMES_ALTERNATION = [...KNOWN_EXTENSIONLESS_LIST]
  .sort((a, b) => b.length - a.length)
  .map((f) => f.replace(/[.*+?^${}()|[\]\\]/g, '\\$&'))
  .join('|');

/**
 * Pattern for extensionless known files - only matches exact known filenames,
 * optionally preceded by a directory path (e.g., src/Dockerfile).
 */
export const KNOWN_FILE_PATTERN = new RegExp(
  `(?:^|[\\s"'\\\`(\\[{<])((?:[\\w.@-]+[/\\\\])*(?:${KNOWN_FILE_NAMES_ALTERNATION}))`,
);

// ===========================================================================
// Validation Functions
// ===========================================================================

const COMMON_TLDS = new Set([
  'com',
  'org',
  'net',
  'io',
  'co',
  'dev',
  'app',
  'ai',
  'edu',
  'gov',
  'me',
  'us',
  'uk',
  'de',
  'fr',
  'jp',
  'cn',
  'ru',
  'br',
  'au',
  'ca',
  'in',
  'nl',
  'it',
  'es',
  'ch',
  'se',
  'no',
  'fi',
  'dk',
  'pl',
  'tv',
  'xyz',
  'site',
  'online',
  'tech',
  'store',
  'blog',
  'cloud',
  'info',
  'biz',
  'pro',
  'name',
  'today',
  'live',
  'news',
  'world',
  'media',
]);

const TRAILING_TRIM_CHARS = new Set([',', ';', '!', '?', '"', "'", '`']);
const BRACKET_PAIRS: ReadonlyArray<{ open: string; close: string }> = [
  { open: '(', close: ')' },
  { open: '[', close: ']' },
  { open: '{', close: '}' },
];

function countChar(input: string, target: string): number {
  let count = 0;
  for (let i = 0; i < input.length; i++) {
    if (input[i] === target) count++;
  }
  return count;
}

export function normalizePathCandidate(path: string): string {
  let normalized = path.trim();

  while (normalized.length > 1) {
    const last = normalized[normalized.length - 1];
    if (!last) break;

    if (TRAILING_TRIM_CHARS.has(last)) {
      normalized = normalized.slice(0, -1);
      continue;
    }

    if (last === ':') {
      const lineInfoMatch = normalized.match(/:(\d+)(?::(\d+))?$/);
      if (lineInfoMatch) {
        normalized = normalized.slice(0, normalized.length - lineInfoMatch[0].length);
        continue;
      }
    }

    let removedUnbalancedBracket = false;
    for (const pair of BRACKET_PAIRS) {
      if (last !== pair.close) continue;
      if (countChar(normalized, pair.close) > countChar(normalized, pair.open)) {
        normalized = normalized.slice(0, -1);
        removedUnbalancedBracket = true;
        break;
      }
    }

    if (removedUnbalancedBracket) {
      continue;
    }

    break;
  }

  return normalized.trimEnd();
}

function parseHostLikePrefix(input: string): string | null {
  if (input.includes('\\')) return null;

  const firstSlashIndex = input.indexOf('/');
  const prefix = firstSlashIndex >= 0 ? input.substring(0, firstSlashIndex) : input;
  const hostPort = prefix.replace(/:\d+$/, '');
  if (!hostPort.includes('.')) return null;
  if (!/^[a-zA-Z0-9.-]+$/.test(hostPort)) return null;

  const labels = hostPort.split('.');
  if (labels.length < 2) return null;
  if (labels.some((label) => label.length === 0 || label.startsWith('-') || label.endsWith('-'))) {
    return null;
  }

  const lastLabel = labels[labels.length - 1];
  if (!lastLabel) return null;
  const tld = lastLabel.toLowerCase();
  if (!/^[a-z]{2,24}$/.test(tld)) return null;
  if (!COMMON_TLDS.has(tld)) return null;

  return hostPort;
}

export function isLikelyUrlOrDomain(path: string): boolean {
  const normalized = path.trim();
  if (!normalized) return false;

  if (/^[a-z][a-z0-9+.-]*:\/\//i.test(normalized)) return true;
  if (/^(?:mailto:|tel:)/i.test(normalized)) return true;
  if (/^www\./i.test(normalized)) return true;
  if (/^\d{1,3}(?:\.\d{1,3}){3}(?::\d+)?(?:\/|$)/.test(normalized)) return true;

  return parseHostLikePrefix(normalized) !== null;
}

export function isValidPath(path: string): boolean {
  const normalized = normalizePathCandidate(path);
  if (!normalized || normalized.length < 2) return false;
  if (normalized.includes('..')) return false;
  if (isLikelyUrlOrDomain(normalized)) return false;
  return true;
}

/**
 * Filter out common false positives that look like files but aren't.
 */
export function isLikelyFalsePositive(path: string): boolean {
  const normalized = normalizePathCandidate(path);

  if (!normalized) return true;
  if (isLikelyUrlOrDomain(normalized)) return true;
  if (/^\d+\.\d+(\.\d+)?$/.test(normalized)) return true;

  const lower = normalized.toLowerCase();
  if (['e.g.', 'i.e.', 'etc.', 'vs.', 'inc.', 'ltd.', 'co.'].includes(lower)) return true;

  if (!normalized.includes('/') && !normalized.includes('\\')) {
    const dotCount = normalized.split('.').length - 1;
    // .NET fully-qualified names: Namespace.Namespace.Class.Method — 4+ dots without
    // path separators is almost certainly a FQN, not a file
    if (dotCount >= 4) return true;
    // PascalCase "extension" (5+ chars starting uppercase) without path separators —
    // catches method calls (Results.Forbid), project names (Ai.Tlbx.tlbx)
    if (dotCount >= 1) {
      const ext = normalized.split('.').pop();
      if (ext && ext.length >= 5 && /^[A-Z]/.test(ext)) return true;
    }
  }

  return false;
}

// ===========================================================================
// matchCallback Filter Predicates
// ===========================================================================

export function isFragmentOfAbsolutePath(match: { input?: string; index?: number }): boolean {
  if (!match.input || match.index === undefined) return false;

  if (match.index >= 3) {
    const drivePrefix = match.input.substring(match.index - 3, match.index);
    if (/^[A-Za-z]:[/\\]$/.test(drivePrefix)) return true;
  }

  if (match.index >= 2) {
    const uncPrefix = match.input.substring(match.index - 2, match.index);
    if (uncPrefix === '\\\\') return true;
  }

  if (match.index >= 1) {
    const prevChar = match.input[match.index - 1];
    if (prevChar === '/' || prevChar === '\\') return true;
  }

  return false;
}

export function shouldRejectFolderMatch(path: string): boolean {
  const normalized = normalizePathCandidate(path);
  if (!normalized) return true;
  if (/^[A-Za-z]:/.test(normalized)) return true;
  if (/^\\\\/.test(normalized)) return true;
  if (/^[a-z]+:\/\//i.test(normalized)) return true;
  if (isLikelyUrlOrDomain(normalized)) return true;

  const withoutTrailingSlash = normalized.replace(/[/\\]+$/, '');
  if (!withoutTrailingSlash.includes('/') && !withoutTrailingSlash.includes('\\')) {
    if (/^[A-Z][A-Za-z0-9_]{5,}$/.test(withoutTrailingSlash)) return true;
  }

  return false;
}

export function shouldRejectKnownFileMatch(path: string): boolean {
  const normalized = normalizePathCandidate(path);
  if (!normalized) return true;
  if (normalized.startsWith('/')) return true;
  if (/^[A-Za-z]:/.test(normalized)) return true;
  if (/^\\\\/.test(normalized)) return true;
  if (isLikelyUrlOrDomain(normalized)) return true;
  return false;
}

export function shouldRejectRelativeMatch(path: string): boolean {
  const normalized = normalizePathCandidate(path);
  if (!normalized) return true;
  if (normalized.startsWith('/')) return true;
  if (/^[A-Za-z]:/.test(normalized)) return true;
  if (/^\\\\/.test(normalized)) return true;
  if (isLikelyUrlOrDomain(normalized)) return true;
  return isLikelyFalsePositive(normalized);
}

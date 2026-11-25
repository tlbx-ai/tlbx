import type { LaunchEntry } from '../../api/types';
import type { Session } from '../../types';
import {
  buildProcessCwdTuple,
  formatRuntimeDisplay,
  getProcessIdentity,
  normalizeTuplePath,
} from '../sidebar/processDisplay';
import { getAgentDisplayName, type HistoryLaunchMode } from './launchMode';

const PIN_SUCCESS_ANIMATION_MS = 560;

export interface BookmarkTitleResolution {
  explicitLabel: string | null;
  defaultLabel: string | null;
}

export interface BookmarkTitleTarget {
  launchMode: HistoryLaunchMode;
  profile: string | null;
  workingDirectory: string;
  executable: string;
  commandLine: string | null;
  isPlainShell?: boolean;
}

function normalizeExecutableToken(value: string | null | undefined): string {
  const trimmed = (value ?? '').trim();
  if (!trimmed) {
    return '';
  }

  let candidate = trimmed;
  const first = candidate[0];
  if (first === '"' || first === "'") {
    const end = candidate.indexOf(first, 1);
    if (end > 1) {
      candidate = candidate.slice(1, end);
    }
  }

  const base = candidate.replace(/\\/g, '/').split('/').pop() ?? candidate;
  const token = base.trim().split(/\s+/)[0] ?? base.trim();
  return token.replace(/\.exe$/i, '').toLowerCase();
}

export function isPlainShellExecutable(
  executable: string | null | undefined,
  shellType: string | null | undefined,
): boolean {
  const normalizedExe = normalizeExecutableToken(executable);
  const normalizedShell = normalizeExecutableToken(shellType);
  if (!normalizedShell) {
    return false;
  }
  return !normalizedExe || normalizedExe === normalizedShell;
}

export function extractBookmarkDirectoryTitle(
  workingDirectory: string | null | undefined,
): string | null {
  const trimmed = (workingDirectory ?? '').trim();
  if (!trimmed) {
    return null;
  }

  const withoutTrailing = trimmed.replace(/[\\/]+$/, '');
  if (!withoutTrailing) {
    return null;
  }

  const segments = withoutTrailing.split(/[\\/]+/).filter(Boolean);
  const last = segments[segments.length - 1]?.trim();
  if (!last) {
    return null;
  }

  // Bare Windows drive like "C:" is less descriptive than a tool/provider name.
  if (/^[A-Za-z]:$/.test(last)) {
    return null;
  }

  return last;
}

export function resolveBookmarkTitle(
  session: Pick<Session, 'name' | 'shellType'>,
  target: BookmarkTitleTarget,
): BookmarkTitleResolution {
  const trimmedName = (session.name ?? '').trim();
  const explicitLabel =
    trimmedName &&
    normalizeExecutableToken(trimmedName) !== normalizeExecutableToken(session.shellType)
      ? trimmedName
      : null;

  if (explicitLabel) {
    return { explicitLabel, defaultLabel: explicitLabel };
  }

  const folderTitle = extractBookmarkDirectoryTitle(target.workingDirectory);
  if (folderTitle) {
    return { explicitLabel: null, defaultLabel: folderTitle };
  }

  if (target.launchMode === 'appServerControl') {
    const providerTitle = getAgentDisplayName(target.profile).trim();
    return { explicitLabel: null, defaultLabel: providerTitle || null };
  }

  if (target.isPlainShell) {
    const shellLabel = (session.shellType || target.executable || '').trim();
    return { explicitLabel: null, defaultLabel: shellLabel || null };
  }

  const processLabel = formatRuntimeDisplay(target.executable, target.commandLine).trim();
  return { explicitLabel: null, defaultLabel: processLabel || target.executable.trim() || null };
}

function normalizeCommandSignature(commandLine: string | null | undefined): string {
  return (commandLine ?? '').trim().replace(/\s+/g, ' ').toLowerCase();
}

export function buildPlainShellBookmarkDedupeKey(
  shellType: string | null | undefined,
  workingDirectory: string | null | undefined,
): string | null {
  const normalizedShell = normalizeExecutableToken(shellType);
  const normalizedCwd = workingDirectory ? normalizeTuplePath(workingDirectory) : '';
  if (!normalizedShell || !normalizedCwd) {
    return null;
  }
  return `shell|${normalizedShell}|${normalizedCwd}`;
}

export function buildTerminalBookmarkDedupeKey(
  shellType: string | null | undefined,
  processName: string | null | undefined,
  commandLine: string | null | undefined,
  workingDirectory: string | null | undefined,
  processIdentity?: string | null,
): string | null {
  if (!processName || !workingDirectory) {
    return null;
  }
  const normalizedShell = normalizeExecutableToken(shellType) || 'shell';
  const normalizedIdentity =
    typeof processIdentity === 'string' && processIdentity.trim().length > 0
      ? processIdentity.trim().toLowerCase()
      : getProcessIdentity(processName, commandLine ?? null);
  const normalizedCwd = normalizeTuplePath(workingDirectory);
  if (!normalizedIdentity || !normalizedCwd) {
    return null;
  }
  const normalizedCmd = normalizeCommandSignature(commandLine);
  return `term|${normalizedShell}|${normalizedIdentity}|${normalizedCmd}|${normalizedCwd}`;
}

export function isPlainShellHistoryEntry(
  entry: Pick<LaunchEntry, 'commandLine' | 'executable' | 'shellType'>,
): boolean {
  return !entry.commandLine?.trim() && isPlainShellExecutable(entry.executable, entry.shellType);
}

export function doesSessionMatchTerminalBookmark(
  session: Pick<Session, 'shellType' | 'currentDirectory'>,
  fgInfo: {
    name: string | null;
    commandLine: string | null;
    cwd: string | null;
    processIdentity: string | null;
  },
  linkedEntry: Pick<
    LaunchEntry,
    'commandLine' | 'executable' | 'shellType' | 'workingDirectory' | 'foregroundProcessIdentity'
  >,
): boolean {
  if (isPlainShellHistoryEntry(linkedEntry)) {
    const currentCwd = fgInfo.cwd ?? session.currentDirectory ?? '';
    return (
      isPlainShellExecutable(fgInfo.name, session.shellType) &&
      !!currentCwd &&
      normalizeTuplePath(currentCwd) === normalizeTuplePath(linkedEntry.workingDirectory)
    );
  }

  const currentTuple = buildProcessCwdTuple(
    fgInfo.name,
    fgInfo.commandLine,
    fgInfo.cwd,
    fgInfo.processIdentity,
  );
  const linkedTuple = buildProcessCwdTuple(
    linkedEntry.executable,
    linkedEntry.commandLine ?? null,
    linkedEntry.workingDirectory,
    linkedEntry.foregroundProcessIdentity ?? null,
  );
  return !!currentTuple && !!linkedTuple && currentTuple === linkedTuple;
}

export function findEquivalentStarredBookmark(
  entries: LaunchEntry[],
  criteria: {
    bookmarkId: string | null | undefined;
    launchMode: HistoryLaunchMode;
    profile: string | null;
    shellType: string;
    executable: string;
    commandLine: string | null;
    workingDirectory: string;
  },
): LaunchEntry | undefined {
  if (criteria.bookmarkId) {
    const byId = entries.find((entry) => entry.id === criteria.bookmarkId);
    if (byId) {
      return byId;
    }
  }

  const normalizedCwd = normalizeTuplePath(criteria.workingDirectory);
  const normalizedCmd = normalizeCommandSignature(criteria.commandLine);
  const normalizedExe = normalizeExecutableToken(criteria.executable);
  const normalizedShell = criteria.shellType.toLowerCase();

  return entries.find(
    (entry) =>
      entry.isStarred &&
      entry.launchMode === criteria.launchMode &&
      (entry.profile ?? null) === criteria.profile &&
      entry.shellType.toLowerCase() === normalizedShell &&
      normalizeExecutableToken(entry.executable) === normalizedExe &&
      normalizeCommandSignature(entry.commandLine) === normalizedCmd &&
      normalizeTuplePath(entry.workingDirectory) === normalizedCwd,
  );
}

export function getBookmarkSurfaceType(
  session: Session,
  profile: string | null,
): 'trm' | 'cdx' | 'acp' {
  if (session.appServerControlOnly && profile === 'codex') {
    return 'cdx';
  }

  if (session.appServerControlOnly && profile) {
    return 'acp';
  }

  return 'trm';
}

export function animateBookmarkSaveSuccess(sessionId: string): void {
  const pinButtons = document.querySelectorAll<HTMLButtonElement>(
    `.session-item[data-session-id="${sessionId}"] .session-pin`,
  );
  for (const pinButton of pinButtons) {
    pinButton.classList.remove('save-success');
    // eslint-disable-next-line @typescript-eslint/no-meaningless-void-operator -- Reading the layout property intentionally forces synchronous layout.
    void pinButton.offsetWidth;
    pinButton.classList.add('save-success');
    window.setTimeout(() => {
      pinButton.classList.remove('save-success');
    }, PIN_SUCCESS_ANIMATION_MS);
  }
}

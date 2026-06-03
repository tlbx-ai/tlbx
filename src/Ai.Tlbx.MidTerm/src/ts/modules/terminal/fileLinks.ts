/**
 * File Links Module
 *
 * Detects file paths in terminal output and makes them clickable.
 * Uses xterm-link-provider for robust link detection and rendering.
 * Clicking opens the file viewer modal.
 */

/* =============================================================================
 * PERFORMANCE-SENSITIVE CODE - FILE PATH DETECTION
 * =============================================================================
 *
 * This module scans ALL terminal output for file paths. It runs on every frame
 * of terminal data, which can be frequent during:
 *   - TUI apps (vim, htop, less) with rapid redraws
 *   - Large file outputs (cat, logs, builds)
 *   - High-frequency updates (tail -f, watch)
 *
 * OPTIMIZATIONS APPLIED:
 *   1. Reused TextDecoder instance (avoid allocation per frame)
 *   2. Minimum frame size threshold (skip tiny cursor-move frames)
 *   3. Bounded per-session scan buffers (avoid unbounded growth)
 *   4. Debounced scanning (batch rapid frames)
 *   5. Reused regex patterns (no new RegExp per call)
 *   6. Early bailout for frames with no path-like characters
 *
 * IF UI PERFORMANCE DEGRADES:
 *   - Increase MIN_SCAN_FRAME_SIZE to skip more small frames
 *   - Increase SCAN_DEBOUNCE_MS to batch more aggressively
 *   - Reduce MAX_PENDING_SCAN_BYTES for tighter memory bounds
 *   - Disable via Settings > Behavior > "File Radar"
 *
 * =========================================================================== */

import type { Terminal } from '@xterm/xterm';
import { LinkProvider } from 'xterm-link-provider';
import type { FilePathInfo, FileCheckResponse, FileResolveResponse } from '../../types';
import { openFile } from '../fileViewer';
import { createLogger } from '../logging';
import { t } from '../i18n';
import { $activeSessionId, $currentSettings } from '../../stores';
import { sessionTerminals } from '../../state';
import {
  UNIX_PATH_PATTERN,
  WIN_PATH_PATTERN,
  UNC_PATH_PATTERN,
  QUOTED_ABSOLUTE_PATH_PATTERN,
  UNIX_PATH_PATTERN_GLOBAL,
  WIN_PATH_PATTERN_GLOBAL,
  UNC_PATH_PATTERN_GLOBAL,
  QUOTED_ABSOLUTE_PATH_PATTERN_GLOBAL,
  RELATIVE_PATH_PATTERN,
  FOLDER_PATH_PATTERN,
  KNOWN_FILE_PATTERN,
  isValidPath,
  isFragmentOfAbsolutePath,
  shouldRejectFolderMatch,
  shouldRejectKnownFileMatch,
  shouldRejectRelativeMatch,
  normalizePathCandidate,
} from './fileRadar.patterns';

const log = createLogger('fileLinks');

// ===========================================================================
// PERFORMANCE TUNING CONSTANTS - Adjust these if performance degrades
// ===========================================================================

/**
 * Check if File Radar is enabled via settings.
 * Controlled by Settings > Behavior > "File Radar"
 * Default: ON (true if settings not yet loaded, since server default is true)
 */
function isFileRadarEnabled(): boolean {
  const settings = $currentSettings.get();
  if (settings === null) return true;
  return settings.fileRadar;
}

/** Minimum frame size in bytes to bother scanning (skip tiny cursor moves) */
const MIN_SCAN_FRAME_SIZE = 8;

/** Debounce interval for batching rapid terminal output (ms) */
const SCAN_DEBOUNCE_MS = 50;

/** Per-session pending scan cap (rough UTF-16 code unit count) */
const MAX_PENDING_SCAN_BYTES = 64 * 1024;

/** Maximum paths to track per session (FIFO eviction) */
const MAX_ALLOWLIST_SIZE = 1000;

/** Cache TTL for file existence checks (ms) */
const EXISTENCE_CACHE_TTL = 30000;

/** Maximum entry counts for caches (hard cap, oldest entry eviction) */
const MAX_EXISTENCE_CACHE_ENTRIES = 2000;
const MAX_RESOLVE_CACHE_ENTRIES = 4000;

/** Quick check: does the text contain characters that could be a path? */
const QUICK_PATH_CHECK_UNIX = /\//;
const QUICK_PATH_CHECK_WIN = /[A-Za-z]:[\\/]/;
const QUICK_PATH_CHECK_UNC = /\\\\/;

/** Cache TTL for resolve results (ms) */
const RESOLVE_CACHE_TTL = 10000;

/** Hover delay before resolving relative paths (ms) - prevents spam during mouse movement */
const RESOLVE_HOVER_DELAY_MS = 150;

/** How long to wait for allowlist registration before checking file existence */
const REGISTER_WAIT_TIMEOUT_MS = 250;

// ===========================================================================
// Module State
// ===========================================================================

type TimedCacheEntry<T> = { value: T; expires: number };

type ScanBufferState = {
  chunks: string[];
  bytes: number;
  timer: number | null;
  idleHandle: number | null;
};

type PathScanWorkerResponse = {
  id: number;
  candidates: string[];
};

type PendingResolve = {
  abort: AbortController;
  timeout: number;
  callback: (match: string | undefined) => void;
};

type RegisterFilePathsFn = (sessionId: string, paths: string[]) => Promise<unknown>;

const pathAllowlists = new Map<string, Set<string>>();
const existenceCache = new Map<string, TimedCacheEntry<FilePathInfo | null>>();
const resolveCache = new Map<string, TimedCacheEntry<FileResolveResponse>>();

/** Reused TextDecoder to avoid allocation per frame */
const textDecoder = new TextDecoder();

/** Bounded per-session scan buffers */
const scanBuffers = new Map<string, ScanBufferState>();

/** One pending resolve per session (prevents hover spam per terminal) */
const pendingResolves = new Map<string, PendingResolve>();

let registerFilePathsFn: RegisterFilePathsFn | null = null;
let registerFilePathsPromise: Promise<RegisterFilePathsFn> | null = null;
let pathScanWorker: Worker | null | undefined;
let pathScanWorkerRequestId = 0;
const pendingPathScanWorkerSessions = new Map<number, string>();

// ===========================================================================
// Toast Notification
// ===========================================================================

function showFileNotFoundToast(path: string): void {
  const existing = document.querySelector('.drop-toast');
  if (existing) existing.remove();

  const toast = document.createElement('div');
  toast.className = 'drop-toast error';
  toast.textContent = `${t('fileLinks.notFound')}: ${path}`;
  document.body.appendChild(toast);

  setTimeout(() => {
    toast.classList.add('hiding');
    setTimeout(() => {
      toast.remove();
    }, 300);
  }, 3000);
}

// ===========================================================================
// Utility Helpers
// ===========================================================================

function setTimedCacheEntry<T>(
  cache: Map<string, TimedCacheEntry<T>>,
  key: string,
  value: T,
  ttlMs: number,
  maxEntries: number,
): void {
  if (cache.has(key)) {
    cache.delete(key);
  }

  cache.set(key, { value, expires: Date.now() + ttlMs });

  const now = Date.now();
  for (const [entryKey, entry] of cache) {
    if (entry.expires <= now) {
      cache.delete(entryKey);
    }
  }

  while (cache.size > maxEntries) {
    const oldest = cache.keys().next().value;
    if (!oldest) break;
    cache.delete(oldest);
  }
}

function buildExistenceCacheKey(path: string, sessionId: string | null): string {
  return `${sessionId ?? ''}|${path}`;
}

function clearSessionCacheEntries(sessionId: string): void {
  for (const key of existenceCache.keys()) {
    if (key.startsWith(`${sessionId}|`)) {
      existenceCache.delete(key);
    }
  }

  for (const key of resolveCache.keys()) {
    if (key.startsWith(`${sessionId}:`)) {
      resolveCache.delete(key);
    }
  }
}

function cancelPendingResolve(sessionId: string): void {
  const pending = pendingResolves.get(sessionId);
  if (!pending) return;
  pending.abort.abort();
  window.clearTimeout(pending.timeout);
  pending.callback(undefined);
  pendingResolves.delete(sessionId);
}

function getOrCreateScanBuffer(sessionId: string): ScanBufferState {
  let state = scanBuffers.get(sessionId);
  if (!state) {
    state = { chunks: [], bytes: 0, timer: null, idleHandle: null };
    scanBuffers.set(sessionId, state);
  }
  return state;
}

function getOrCreatePathScanWorker(): Worker | null {
  if (pathScanWorker !== undefined) {
    return pathScanWorker;
  }

  if (
    typeof Worker === 'undefined' ||
    typeof Blob === 'undefined' ||
    typeof URL === 'undefined' ||
    typeof URL.createObjectURL !== 'function'
  ) {
    pathScanWorker = null;
    return pathScanWorker;
  }

  try {
    const source = `
      const ANSI_CSI_PATTERN = /\\x1b\\[[0-9;?]*[A-Za-z]/g;
      const ANSI_OSC_PATTERN = /\\x1b\\][^\\x07\\x1b]*(?:\\x07|\\x1b\\\\)/g;
      const ANSI_INCOMPLETE_OSC_PATTERN = /\\x1b\\][^\\x07]*/g;
      const CANDIDATE_PATTERNS = [
        /[A-Za-z]:[\\\\/][^\\s<>"'|*?]+/g,
        /\\\\\\\\[^\\s<>"']+/g,
        /(^|[\\s(["'])(\\/[^\\s<>"']+)/g
      ];

      function stripAnsi(text) {
        return text
          .replace(ANSI_CSI_PATTERN, '')
          .replace(ANSI_OSC_PATTERN, '')
          .replace(ANSI_INCOMPLETE_OSC_PATTERN, '');
      }

      function collectCandidates(text) {
        const cleanText = stripAnsi(text);
        const candidates = [];
        for (const pattern of CANDIDATE_PATTERNS) {
          pattern.lastIndex = 0;
          let match;
          while ((match = pattern.exec(cleanText)) !== null) {
            const candidate = match[2] || match[0];
            if (candidate) {
              candidates.push(candidate);
            }
          }
        }
        return candidates;
      }

      self.onmessage = (event) => {
        const { id, text } = event.data || {};
        self.postMessage({ id, candidates: collectCandidates(String(text || '')) });
      };
    `;
    const url = URL.createObjectURL(new Blob([source], { type: 'text/javascript' }));
    pathScanWorker = new Worker(url);
    URL.revokeObjectURL(url);
    pathScanWorker.onmessage = (event: MessageEvent<PathScanWorkerResponse>) => {
      const { id, candidates } = event.data;
      const sessionId = pendingPathScanWorkerSessions.get(id);
      pendingPathScanWorkerSessions.delete(id);
      if (!sessionId) {
        return;
      }
      registerDetectedPathCandidates(sessionId, candidates);
    };
    pathScanWorker.onerror = () => {
      pendingPathScanWorkerSessions.clear();
      pathScanWorker?.terminate();
      pathScanWorker = null;
    };
  } catch {
    pathScanWorker = null;
  }

  return pathScanWorker;
}

function isAsciiLetter(value: number): boolean {
  return (value >= 0x41 && value <= 0x5a) || (value >= 0x61 && value <= 0x7a);
}

function containsLikelyPathBytes(data: Uint8Array): boolean {
  for (let i = 0; i < data.length; i += 1) {
    const value = data[i] ?? -1;
    if (value === 0x2f || value === 0x5c) {
      return true;
    }

    if (
      i + 2 < data.length &&
      isAsciiLetter(value) &&
      data[i + 1] === 0x3a &&
      (data[i + 2] === 0x2f || data[i + 2] === 0x5c)
    ) {
      return true;
    }
  }

  return false;
}

function shouldSkipSessionOutputScan(sessionId: string): boolean {
  const state = sessionTerminals.get(sessionId);
  return state?.terminal.modes.synchronizedOutputMode === true;
}

function queueScanFlush(sessionId: string): void {
  const state = getOrCreateScanBuffer(sessionId);
  if (state.timer !== null || state.idleHandle !== null) return;

  state.timer = window.setTimeout(() => {
    const current = scanBuffers.get(sessionId);
    if (!current) return;
    current.timer = null;
    scheduleScanIdleFlush(sessionId);
  }, SCAN_DEBOUNCE_MS);
}

function scheduleScanIdleFlush(sessionId: string): void {
  const current = scanBuffers.get(sessionId);
  if (!current || current.idleHandle !== null) return;

  const flush = (): void => {
    const latest = scanBuffers.get(sessionId);
    if (!latest) return;
    latest.idleHandle = null;

    if (latest.chunks.length === 0) return;
    const pendingText = latest.chunks.join('');
    latest.chunks = [];
    latest.bytes = 0;

    if (pendingText.length > 0) {
      performScan(sessionId, pendingText);
    }
  };

  const idleScheduler = (
    window as typeof window & {
      requestIdleCallback?: (callback: IdleRequestCallback, options?: IdleRequestOptions) => number;
    }
  ).requestIdleCallback;

  if (typeof idleScheduler === 'function') {
    current.idleHandle = idleScheduler(
      () => {
        flush();
      },
      { timeout: 250 },
    );
    return;
  }

  current.idleHandle = window.setTimeout(flush, 0);
}

function appendScanText(sessionId: string, text: string): void {
  const state = getOrCreateScanBuffer(sessionId);
  state.chunks.push(text);
  state.bytes += text.length;

  while (state.bytes > MAX_PENDING_SCAN_BYTES && state.chunks.length > 1) {
    const dropped = state.chunks.shift();
    if (!dropped) break;
    state.bytes -= dropped.length;
  }

  if (state.bytes > MAX_PENDING_SCAN_BYTES && state.chunks.length === 1) {
    const chunk = state.chunks[0];
    if (!chunk) return;
    const trimmed = chunk.slice(-MAX_PENDING_SCAN_BYTES);
    state.chunks[0] = trimmed;
    state.bytes = trimmed.length;
  }
}

function normalizeAbsoluteMatch(match: RegExpMatchArray): string | undefined {
  const path = match[1];
  if (!path) return undefined;
  const normalized = normalizePathCandidate(path);
  if (!isValidPath(normalized)) return undefined;
  return normalized;
}

async function getRegisterFilePathsFn(): Promise<RegisterFilePathsFn> {
  if (registerFilePathsFn) return registerFilePathsFn;

  if (!registerFilePathsPromise) {
    registerFilePathsPromise = import('../../api/client')
      .then((module) => {
        registerFilePathsFn = module.registerFilePaths as RegisterFilePathsFn;
        return registerFilePathsFn;
      })
      .finally(() => {
        registerFilePathsPromise = null;
      });
  }

  return registerFilePathsPromise;
}

async function registerPathsWithBackendAsync(sessionId: string, paths: string[]): Promise<void> {
  if (!sessionId || paths.length === 0) return;

  try {
    const registerFilePaths = await getRegisterFilePathsFn();
    await registerFilePaths(sessionId, paths);
  } catch (e) {
    log.warn(() => `Failed to register paths: ${String(e)}`);
  }
}

/**
 * Register detected file paths with the backend for security allowlisting.
 * Fire-and-forget by default - callers that need stronger ordering can await
 * registerPathsWithBackendAsync() directly.
 */
function registerPathsWithBackend(sessionId: string, paths: string[]): void {
  void registerPathsWithBackendAsync(sessionId, paths);
}

// ===========================================================================
// Public API
// ===========================================================================

export function getPathAllowlist(sessionId: string): Set<string> {
  let allowlist = pathAllowlists.get(sessionId);
  if (!allowlist) {
    allowlist = new Set();
    pathAllowlists.set(sessionId, allowlist);
  }
  return allowlist;
}

export function clearPathAllowlist(sessionId: string): void {
  pathAllowlists.delete(sessionId);

  const scanState = scanBuffers.get(sessionId);
  if (scanState && scanState.timer !== null) {
    window.clearTimeout(scanState.timer);
  }
  if (scanState && scanState.idleHandle !== null) {
    const idleCanceller = (
      window as typeof window & { cancelIdleCallback?: (handle: number) => void }
    ).cancelIdleCallback;
    if (typeof idleCanceller === 'function') {
      idleCanceller(scanState.idleHandle);
    } else {
      window.clearTimeout(scanState.idleHandle);
    }
  }
  scanBuffers.delete(sessionId);

  for (const [id, pendingSessionId] of pendingPathScanWorkerSessions) {
    if (pendingSessionId === sessionId) {
      pendingPathScanWorkerSessions.delete(id);
    }
  }

  cancelPendingResolve(sessionId);
  clearSessionCacheEntries(sessionId);
}

/**
 * Queue terminal output for path scanning.
 * Debounced to batch rapid frames and reduce CPU overhead.
 *
 * PERFORMANCE NOTE: This is called on EVERY terminal output frame.
 * Keep this function as fast as possible - actual scanning is deferred.
 */
export function scanOutputForPaths(sessionId: string, data: string | Uint8Array): void {
  if (!isFileRadarEnabled()) {
    return;
  }

  if (shouldSkipSessionOutputScan(sessionId)) {
    return;
  }

  if (typeof data !== 'string' && !containsLikelyPathBytes(data)) {
    return;
  }

  // Decode if needed (reuse decoder to avoid allocation)
  const text = typeof data === 'string' ? data : textDecoder.decode(data);

  // Skip tiny frames (likely cursor moves, not real content)
  if (text.length < MIN_SCAN_FRAME_SIZE) return;

  // Quick check: does this text even contain path-like characters?
  // This avoids regex overhead for frames that are clearly not paths
  if (
    !QUICK_PATH_CHECK_UNIX.test(text) &&
    !QUICK_PATH_CHECK_WIN.test(text) &&
    !QUICK_PATH_CHECK_UNC.test(text)
  ) {
    return;
  }

  appendScanText(sessionId, text);
  queueScanFlush(sessionId);
}

// ===========================================================================
// Internal Implementation
// ===========================================================================

/**
 * Actually perform the regex scan on accumulated text.
 * Called after debounce delay.
 * Registers detected paths with backend for security allowlisting.
 */
function performScan(sessionId: string, text: string): void {
  const worker = getOrCreatePathScanWorker();
  if (worker) {
    const id = ++pathScanWorkerRequestId;
    pendingPathScanWorkerSessions.set(id, sessionId);
    worker.postMessage({ id, text });
    return;
  }

  registerDetectedPathCandidates(sessionId, collectPathCandidatesOnMainThread(text));
}

function collectPathCandidatesOnMainThread(text: string): string[] {
  // Strip ANSI escape sequences before regex matching
  /* eslint-disable no-control-regex -- Control-byte patterns are required to remove ANSI escape sequences before scanning terminal text. */
  const cleanText = text
    .replace(/\x1b\[[0-9;?]*[A-Za-z]/g, '') // CSI sequences
    .replace(/\x1b\][^\x07\x1b]*(?:\x07|\x1b\\)/g, '') // OSC sequences
    .replace(/\x1b\][^\x07]*/g, ''); // Incomplete OSC
  /* eslint-enable no-control-regex */

  const candidates: string[] = [];

  const collectMatches = (pattern: RegExp): void => {
    pattern.lastIndex = 0;
    for (const match of cleanText.matchAll(pattern)) {
      const path = match[1];
      if (path) {
        candidates.push(path);
      }
    }
  };

  collectMatches(UNIX_PATH_PATTERN_GLOBAL);
  collectMatches(WIN_PATH_PATTERN_GLOBAL);
  collectMatches(UNC_PATH_PATTERN_GLOBAL);
  collectMatches(QUOTED_ABSOLUTE_PATH_PATTERN_GLOBAL);

  return candidates;
}

function registerDetectedPathCandidates(sessionId: string, candidates: string[]): void {
  const allowlist = getPathAllowlist(sessionId);
  const detectedPaths = new Set<string>();

  for (const path of candidates) {
    const normalized = normalizePathCandidate(path);
    if (!isValidPath(normalized)) continue;

    if (addToAllowlist(allowlist, normalized)) {
      detectedPaths.add(normalized);
    }
  }

  // Register detected paths with backend for security allowlisting
  if (detectedPaths.size > 0) {
    registerPathsWithBackend(sessionId, [...detectedPaths]);
  }
}

function addToAllowlist(allowlist: Set<string>, path: string): boolean {
  if (allowlist.has(path)) {
    return false;
  }

  if (allowlist.size >= MAX_ALLOWLIST_SIZE) {
    // FIFO eviction - remove oldest entry
    const firstKey = allowlist.values().next().value;
    if (firstKey) allowlist.delete(firstKey);
  }
  allowlist.add(path);
  return true;
}

async function checkPathExists(path: string): Promise<FilePathInfo | null> {
  const sessionId = $activeSessionId.get();
  const cacheKey = buildExistenceCacheKey(path, sessionId);
  const cached = existenceCache.get(cacheKey);
  if (cached && cached.expires > Date.now()) {
    return cached.value;
  }

  try {
    const url = sessionId
      ? `/api/files/check?sessionId=${encodeURIComponent(sessionId)}`
      : '/api/files/check';
    const resp = await fetch(url, {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ paths: [path] }),
    });

    if (!resp.ok) {
      setTimedCacheEntry(
        existenceCache,
        cacheKey,
        null,
        EXISTENCE_CACHE_TTL,
        MAX_EXISTENCE_CACHE_ENTRIES,
      );
      return null;
    }

    const data = (await resp.json()) as FileCheckResponse;
    const info = data.results[path] || null;

    setTimedCacheEntry(
      existenceCache,
      cacheKey,
      info,
      EXISTENCE_CACHE_TTL,
      MAX_EXISTENCE_CACHE_ENTRIES,
    );
    return info;
  } catch (e) {
    log.error(() => `Failed to check path existence: ${String(e)}`);
    return null;
  }
}

// ===========================================================================
// Relative Path Resolution (lazy, on hover only, throttled)
// ===========================================================================

/**
 * Resolve a relative path against the session's working directory.
 * @param deep - If true, search subdirectories when exact path not found (expensive, for click only)
 */
async function resolveRelativePath(
  sessionId: string,
  relativePath: string,
  deep: boolean = false,
  signal?: AbortSignal,
): Promise<FileResolveResponse | null> {
  const normalizedRelativePath = normalizePathCandidate(relativePath);
  if (!normalizedRelativePath || normalizedRelativePath.includes('..')) {
    return null;
  }

  const cacheKey = `${sessionId}:${normalizedRelativePath}:${deep}`;
  const cached = resolveCache.get(cacheKey);
  if (cached && cached.expires > Date.now()) {
    return cached.value;
  }

  try {
    const url =
      `/api/files/resolve?sessionId=${encodeURIComponent(sessionId)}` +
      `&path=${encodeURIComponent(normalizedRelativePath)}` +
      (deep ? '&deep=true' : '');
    const fetchOptions: RequestInit = {};
    if (signal) fetchOptions.signal = signal;
    const resp = await fetch(url, fetchOptions);

    if (!resp.ok) {
      const notFound: FileResolveResponse = {
        exists: false,
        isDirectory: false,
        resolvedPath: '',
        size: null,
        mimeType: '',
        modified: null,
        isText: false,
      };
      setTimedCacheEntry(
        resolveCache,
        cacheKey,
        notFound,
        RESOLVE_CACHE_TTL,
        MAX_RESOLVE_CACHE_ENTRIES,
      );
      return notFound;
    }

    const data = (await resp.json()) as FileResolveResponse;
    setTimedCacheEntry(resolveCache, cacheKey, data, RESOLVE_CACHE_TTL, MAX_RESOLVE_CACHE_ENTRIES);
    return data;
  } catch (e) {
    // AbortError is expected when hover moves away
    if (e instanceof Error && e.name === 'AbortError') {
      return null;
    }
    log.error(() => `Failed to resolve relative path: ${String(e)}`);
    return null;
  }
}

/**
 * Throttled resolve - waits for hover to "settle" before making API call.
 * New hovers cancel pending requests for the same session.
 */
function throttledResolveRelativePath(
  sessionId: string,
  path: string,
  matchText: string,
  callback: (match: string | undefined) => void,
): void {
  const normalizedPath = normalizePathCandidate(path);
  const normalizedMatchText = normalizePathCandidate(matchText);
  if (!normalizedPath || !normalizedMatchText) {
    callback(undefined);
    return;
  }

  // Cancel pending resolve for this session to prevent hover spam.
  cancelPendingResolve(sessionId);

  // Check cache - show link only if path was confirmed to exist
  const cacheKey = `${sessionId}:${normalizedPath}:false`;
  const cached = resolveCache.get(cacheKey);
  if (cached && cached.expires > Date.now()) {
    callback(cached.value.exists ? normalizedMatchText : undefined);
    return;
  }

  // Schedule delayed resolve - only show link if path exists
  const abort = new AbortController();
  const timeout = window.setTimeout(() => {
    void (async () => {
      if (abort.signal.aborted) return;

      const result = await resolveRelativePath(sessionId, normalizedPath, false, abort.signal);

      callback(result?.exists ? normalizedMatchText : undefined);

      const pending = pendingResolves.get(sessionId);
      if (pending?.abort === abort) {
        pendingResolves.delete(sessionId);
      }
    })();
  }, RESOLVE_HOVER_DELAY_MS);

  pendingResolves.set(sessionId, { abort, timeout, callback });
}

// ===========================================================================
// Click Handlers
// ===========================================================================

async function handlePathClick(path: string): Promise<void> {
  const normalizedPath = normalizePathCandidate(path);
  if (!isValidPath(normalizedPath)) {
    showFileNotFoundToast(path);
    return;
  }

  const sessionId = $activeSessionId.get();
  if (sessionId) {
    // Avoid first-click race: wait briefly for allowlist registration to settle.
    await Promise.race([
      registerPathsWithBackendAsync(sessionId, [normalizedPath]),
      new Promise<void>((resolve) => window.setTimeout(resolve, REGISTER_WAIT_TIMEOUT_MS)),
    ]);
  }

  const info = await checkPathExists(normalizedPath);
  if (info?.exists) {
    void openFile(normalizedPath, info);
    return;
  }

  // Fallback: Unix-style paths on Windows (e.g., /foo/bar.cs) aren't truly absolute
  // Try resolving as relative path with deep search
  if (sessionId) {
    // Strip leading slash for relative resolution
    const relativePath = normalizedPath.startsWith('/') ? normalizedPath.slice(1) : normalizedPath;
    const resolved = await resolveRelativePath(sessionId, relativePath, true);
    if (resolved?.exists && resolved.resolvedPath) {
      registerPathsWithBackend(sessionId, [resolved.resolvedPath]);
      const resolvedInfo: FilePathInfo = {
        exists: true,
        isDirectory: resolved.isDirectory,
        size: resolved.size ?? null,
        mimeType: resolved.mimeType ?? '',
        modified: resolved.modified ?? null,
        isText: resolved.isText ?? false,
      };
      void openFile(resolved.resolvedPath, resolvedInfo);
      return;
    }
  }

  showFileNotFoundToast(normalizedPath);
}

async function handleRelativePathClick(relativePath: string): Promise<void> {
  const normalizedRelativePath = normalizePathCandidate(relativePath);
  if (!normalizedRelativePath) {
    showFileNotFoundToast(relativePath);
    return;
  }

  const sessionId = $activeSessionId.get();
  if (!sessionId) {
    showFileNotFoundToast(normalizedRelativePath);
    return;
  }

  // Use deep=true for click - search subdirectories if exact path not found
  const resolved = await resolveRelativePath(sessionId, normalizedRelativePath, true);
  if (resolved?.exists && resolved.resolvedPath) {
    // Register resolved path with backend allowlist (fire-and-forget)
    registerPathsWithBackend(sessionId, [resolved.resolvedPath]);

    const info: FilePathInfo = {
      exists: true,
      isDirectory: resolved.isDirectory,
      size: resolved.size ?? null,
      mimeType: resolved.mimeType ?? '',
      modified: resolved.modified ?? null,
      isText: resolved.isText ?? false,
    };
    void openFile(resolved.resolvedPath, info);
  } else {
    showFileNotFoundToast(normalizedRelativePath);
  }
}

async function handleFolderPathClick(folderPath: string): Promise<void> {
  const normalizedFolderPath = normalizePathCandidate(folderPath);
  if (!normalizedFolderPath) {
    showFileNotFoundToast(folderPath);
    return;
  }

  const sessionId = $activeSessionId.get();
  if (!sessionId) {
    showFileNotFoundToast(normalizedFolderPath);
    return;
  }

  // Remove trailing slash/backslash for resolution
  const pathWithoutSlash = normalizedFolderPath.replace(/[/\\]+$/, '');

  // Try to resolve the folder path
  const resolved = await resolveRelativePath(sessionId, pathWithoutSlash, true);
  if (resolved?.exists && resolved.resolvedPath && resolved.isDirectory) {
    registerPathsWithBackend(sessionId, [resolved.resolvedPath]);
    const info: FilePathInfo = {
      exists: true,
      isDirectory: true,
      size: null,
      mimeType: '',
      modified: null,
      isText: false,
    };
    void openFile(resolved.resolvedPath, info);
  } else {
    showFileNotFoundToast(normalizedFolderPath);
  }
}

// ===========================================================================
// Link Provider Registration
// ===========================================================================

/**
 * Register the file link provider with xterm.js using xterm-link-provider.
 * This is called once per terminal session.
 *
 * Registration order matters: later providers take priority in xterm.
 * We register least-specific first, most-specific last so absolute paths
 * win over relative matches at overlapping positions.
 */
export function registerFileLinkProvider(terminal: Terminal, sessionId: string): void {
  if (!isFileRadarEnabled()) return;

  /* eslint-disable @typescript-eslint/no-unsafe-argument, @typescript-eslint/no-unsafe-assignment, @typescript-eslint/no-explicit-any -- xterm-link-provider still exposes legacy xterm typings, so this adapter cast is constrained to the registration boundary. */
  // xterm-link-provider references the old 'xterm' package types; cast required
  const term = terminal as any;

  // 1. Folder paths — least specific (e.g., docs/, src\components\)
  terminal.registerLinkProvider(
    new LinkProvider(
      term,
      FOLDER_PATH_PATTERN,
      (_event, folderPath) => {
        void handleFolderPathClick(folderPath);
      },
      {
        matchCallback: (match: RegExpMatchArray, callback: (match: string | undefined) => void) => {
          const path = match[1];
          if (!path) {
            callback(undefined);
            return;
          }

          const normalizedPath = normalizePathCandidate(path);
          if (
            !normalizedPath ||
            shouldRejectFolderMatch(normalizedPath) ||
            isFragmentOfAbsolutePath(match)
          ) {
            callback(undefined);
            return;
          }

          throttledResolveRelativePath(
            sessionId,
            normalizedPath.replace(/[/\\]+$/, ''),
            normalizedPath,
            callback,
          );
        },
      } as unknown as Record<string, unknown>,
    ),
  );

  // 2. Known extensionless files (e.g., Dockerfile, .gitignore, LICENSE)
  terminal.registerLinkProvider(
    new LinkProvider(
      term,
      KNOWN_FILE_PATTERN,
      (_event, relativePath) => {
        void handleRelativePathClick(relativePath);
      },
      {
        matchCallback: (match: RegExpMatchArray, callback: (match: string | undefined) => void) => {
          const path = match[1];
          if (!path) {
            callback(undefined);
            return;
          }

          const normalizedPath = normalizePathCandidate(path);
          if (!normalizedPath || shouldRejectKnownFileMatch(normalizedPath)) {
            callback(undefined);
            return;
          }

          throttledResolveRelativePath(sessionId, normalizedPath, normalizedPath, callback);
        },
      } as unknown as Record<string, unknown>,
    ),
  );

  // 3. Relative paths with extensions (e.g., src/main.ts, foo.jpg)
  terminal.registerLinkProvider(
    new LinkProvider(
      term,
      RELATIVE_PATH_PATTERN,
      (_event, relativePath) => {
        void handleRelativePathClick(relativePath);
      },
      {
        matchCallback: (match: RegExpMatchArray, callback: (match: string | undefined) => void) => {
          const path = match[1];
          if (!path) {
            callback(undefined);
            return;
          }

          const normalizedPath = normalizePathCandidate(path);
          if (
            !normalizedPath ||
            shouldRejectRelativeMatch(normalizedPath) ||
            isFragmentOfAbsolutePath(match)
          ) {
            callback(undefined);
            return;
          }

          throttledResolveRelativePath(sessionId, normalizedPath, normalizedPath, callback);
        },
      } as unknown as Record<string, unknown>,
    ),
  );

  // 4. Quoted absolute paths (e.g., "C:\Program Files\Git\bin\bash.exe")
  terminal.registerLinkProvider(
    new LinkProvider(
      term,
      QUOTED_ABSOLUTE_PATH_PATTERN,
      (_event, path) => {
        void handlePathClick(path);
      },
      {
        matchCallback: (match: RegExpMatchArray, callback: (match: string | undefined) => void) => {
          callback(normalizeAbsoluteMatch(match));
        },
      } as unknown as Record<string, unknown>,
    ),
  );

  // 5. Windows UNC absolute paths (e.g., \\server\share\file.txt)
  terminal.registerLinkProvider(
    new LinkProvider(
      term,
      UNC_PATH_PATTERN,
      (_event, path) => {
        void handlePathClick(path);
      },
      {
        matchCallback: (match: RegExpMatchArray, callback: (match: string | undefined) => void) => {
          callback(normalizeAbsoluteMatch(match));
        },
      } as unknown as Record<string, unknown>,
    ),
  );

  // 6. Windows absolute paths (e.g., C:\Users\file.txt)
  terminal.registerLinkProvider(
    new LinkProvider(
      term,
      WIN_PATH_PATTERN,
      (_event, path) => {
        void handlePathClick(path);
      },
      {
        matchCallback: (match: RegExpMatchArray, callback: (match: string | undefined) => void) => {
          callback(normalizeAbsoluteMatch(match));
        },
      } as unknown as Record<string, unknown>,
    ),
  );

  // 7. Unix absolute paths — most specific, highest priority
  terminal.registerLinkProvider(
    new LinkProvider(
      term,
      UNIX_PATH_PATTERN,
      (_event, path) => {
        void handlePathClick(path);
      },
      {
        matchCallback: (match: RegExpMatchArray, callback: (match: string | undefined) => void) => {
          callback(normalizeAbsoluteMatch(match));
        },
      } as unknown as Record<string, unknown>,
    ),
  );

  /* eslint-enable @typescript-eslint/no-unsafe-argument, @typescript-eslint/no-unsafe-assignment, @typescript-eslint/no-explicit-any */

  log.verbose(() => `Registered file link provider`);
}

// Legacy export for compatibility (unused but keeps API stable)
export function createFileLinkProvider(_sessionId: string): null {
  return null;
}

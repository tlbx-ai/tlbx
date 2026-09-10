/**
 * Logging Concerns Module
 *
 * Provides dynamic, per-module log filtering controllable from the browser console.
 * Persists enabled concerns in localStorage so they survive page reloads.
 *
 * Usage (browser console):
 *   mtlog.enable('mux')     - Enable logging for the mux module
 *   mtlog.enable('*')       - Enable all modules
 *   mtlog.disable('mux')    - Disable mux module logging
 *   mtlog.disable('*')      - Disable all modules
 *   mtlog.list()            - Show all concerns and their on/off state
 *   mtlog.level('warn')     - Set global minimum log level
 *   mtlog.reset()           - Reset to defaults (all off, level=warn)
 */

import { LogLevel, LOG_LEVEL_NAMES } from './types';

const STORAGE_KEY = 'mt:log-concerns';
const LEVEL_STORAGE_KEY = 'mt:log-level';

const KNOWN_CONCERNS = [
  'main',
  'bootstrap',
  'mux',
  'state',
  'settings-ws',
  'chat',
  'fileViewer',
  'history',
  'history-dropdown',
  'process',
  'networkSection',
  'updating',
  'voice',
  'voiceTools',
  'version',
] as const;

let enabledConcerns: Set<string> = new Set();
let minLevel: LogLevel = LogLevel.Warn;

function loadFromStorage(): void {
  try {
    const stored = localStorage.getItem(STORAGE_KEY);
    if (stored) {
      const parsed = JSON.parse(stored) as string[];
      enabledConcerns = new Set(parsed);
    }
    const level = localStorage.getItem(LEVEL_STORAGE_KEY);
    if (level !== null) {
      const num = parseInt(level, 10);
      // eslint-disable-next-line @typescript-eslint/no-unsafe-enum-comparison -- Validate the numeric storage value against the enum bounds.
      if (num >= LogLevel.Exception && num <= LogLevel.Verbose) {
        minLevel = num;
      }
    }
  } catch {
    // ignore corrupted localStorage
  }
}

function saveToStorage(): void {
  try {
    localStorage.setItem(STORAGE_KEY, JSON.stringify([...enabledConcerns]));
    localStorage.setItem(LEVEL_STORAGE_KEY, String(minLevel));
  } catch {
    // localStorage may be unavailable
  }
}

export function isConcernEnabled(module: string): boolean {
  return enabledConcerns.has('*') || enabledConcerns.has(module);
}

export function getMinLevel(): LogLevel {
  return minLevel;
}

function enable(concern: string): void {
  enabledConcerns.add(concern);
  saveToStorage();
  console.info(`[mtlog] enabled: ${concern}`);
}

function disable(concern: string): void {
  enabledConcerns.delete(concern);
  saveToStorage();
  console.info(`[mtlog] disabled: ${concern}`);
}

function list(): void {
  const levelName = LOG_LEVEL_NAMES[minLevel];
  console.info(`[mtlog] level: ${levelName}`);
  console.info('[mtlog] concerns:');
  for (const c of KNOWN_CONCERNS) {
    const on = isConcernEnabled(c);
    console.info(`  ${on ? '+' : '-'} ${c}`);
  }
  if (enabledConcerns.has('*')) {
    console.info('  * (all enabled)');
  }
}

const LEVEL_NAMES: Record<string, LogLevel> = {
  exception: LogLevel.Exception,
  error: LogLevel.Error,
  warn: LogLevel.Warn,
  info: LogLevel.Info,
  verbose: LogLevel.Verbose,
};

function setLevel(name: string): void {
  const level = LEVEL_NAMES[name.toLowerCase()];
  if (level === undefined) {
    console.error(`[mtlog] unknown level: ${name}. Use: exception, error, warn, info, verbose`);
    return;
  }
  minLevel = level;
  saveToStorage();
  console.info(`[mtlog] level set to: ${name}`);
}

function reset(): void {
  enabledConcerns.clear();
  minLevel = LogLevel.Warn;
  saveToStorage();
  console.info('[mtlog] reset to defaults');
}

interface MtlogApi {
  enable: (concern: string) => void;
  disable: (concern: string) => void;
  list: () => void;
  level: (name: string) => void;
  reset: () => void;
}

export function initLogConcerns(): void {
  loadFromStorage();

  (window as unknown as { mtlog: MtlogApi }).mtlog = {
    enable,
    disable,
    list,
    level: setLevel,
    reset,
  };
}

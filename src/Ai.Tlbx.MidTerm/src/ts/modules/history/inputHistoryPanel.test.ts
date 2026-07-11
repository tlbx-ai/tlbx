import { describe, expect, it, vi } from 'vitest';
import { readFileSync } from 'node:fs';

vi.mock('../../constants', () => ({ icon: (name: string) => name }));
vi.mock('../i18n', () => ({
  t: (key: string) =>
    ({
      'inputHistory.untitled': 'Untitled input',
      'inputHistory.timeUnknown': 'Unknown time',
      'inputHistory.timeNow': 'Just now',
      'inputHistory.timeMinutes': '{count}m ago',
      'inputHistory.timeHours': '{count}h ago',
      'inputHistory.timeDays': '{count}d ago',
    })[key] ?? key,
}));

import type { InputHistoryEntry } from './inputHistoryApi';
import {
  formatInputHistoryMeta,
  formatInputHistoryPreview,
  formatInputHistoryText,
} from './inputHistoryPanel';

const html = readFileSync(new URL('../../../static/index.html', import.meta.url), 'utf8');
const css = readFileSync(new URL('../../../static/css/app.css', import.meta.url), 'utf8');
const dropdown = readFileSync(new URL('./historyDropdown.ts', import.meta.url), 'utf8');
const tabBar = readFileSync(new URL('../sessionTabs/tabBar.ts', import.meta.url), 'utf8');
const sessionMenu = readFileSync(new URL('./sessionInputHistoryMenu.ts', import.meta.url), 'utf8');

function entry(overrides: Partial<InputHistoryEntry> = {}): InputHistoryEntry {
  return {
    id: 'entry-1',
    sessionId: 'session-a',
    sessionName: 'Codex',
    workingDirectory: 'Q:\\repo',
    kind: 'textPaste',
    source: 'terminalPaste',
    surface: 'terminal',
    createdAt: '2026-07-11T10:00:00Z',
    text: 'inspect this',
    path: null,
    displayName: null,
    mimeType: null,
    sizeBytes: null,
    bracketedPaste: true,
    isFilePath: false,
    submit: false,
    ...overrides,
  };
}

describe('input history formatting', () => {
  it('preserves Bookmarks and puts input history in each session top bar', () => {
    expect(html).toContain('id="btn-bookmarks"');
    expect(html).toContain('data-i18n="sidebar.loadBookmarked">Bookmarks</span>');
    expect(html).not.toContain('id="btn-input-history"');
    expect(html).toContain('id="btn-mobile-input-history"');
    expect(dropdown).not.toContain('fetchInputHistory');
    expect(tabBar).toContain('ide-bar-btn ide-bar-input-history');
    expect(tabBar).toContain('inputHistoryClickHandler?.(sessionId, inputHistoryBtn)');
  });

  it('lists and replays only through the owning session boundary', () => {
    expect(sessionMenu).toContain('fetchInputHistory({ sessionId, limit: 100 }');
    expect(sessionMenu).toContain('entry.sessionId === sessionId');
    expect(sessionMenu).toContain("entry.surface === 'terminal'");
    expect(sessionMenu).toContain('replayInputHistory(entry.id, sessionId)');
    expect(sessionMenu).not.toContain('$activeSessionId.get() ?? entry.sessionId');
  });

  it('reuses the established history and sidebar colors instead of defining a parallel treatment', () => {
    expect(css).not.toContain('.history-dropdown-tab');
    expect(css).not.toContain('.input-history-kind {');
    expect(css).not.toContain('.input-history-kind-imagePaste');
  });

  it('loads image thumbnails through the persisted entry boundary', () => {
    const source = readFileSync(new URL('./inputHistoryPanel.ts', import.meta.url), 'utf8');
    expect(source).toContain('/api/input-history/${encodeURIComponent(entry.id)}/content');
    expect(source).not.toContain("url.searchParams.set('sessionId'");
  });

  it('renders a timestamped vertical timeline with text or image content', () => {
    const source = readFileSync(new URL('./inputHistoryPanel.ts', import.meta.url), 'utf8');
    expect(source).toContain("list.className = 'input-history-timeline'");
    expect(source).toContain("timestamp.className = 'input-history-timestamp'");
    expect(source).toContain("text.className = 'input-history-text'");
    expect(source).toContain("thumbnail.className = 'input-history-thumbnail'");
    expect(css).toContain('.input-history-timeline::before');
    expect(source).not.toContain('input-history-kind');
  });

  it('normalizes and bounds exact text previews', () => {
    const value = `${'word '.repeat(40)}\nnext`;
    const preview = formatInputHistoryPreview(entry({ text: value }));

    expect(preview.length).toBe(140);
    expect(preview.endsWith('…')).toBe(true);
    expect(preview).not.toContain('\n');
  });

  it('uses the uploaded file name or path leaf without interpreting content', () => {
    expect(formatInputHistoryPreview(entry({ text: null, displayName: 'screen.png' }))).toBe(
      'screen.png',
    );
    expect(
      formatInputHistoryPreview(
        entry({ text: null, displayName: null, path: 'Q:\\repo\\evidence.pdf' }),
      ),
    ).toBe('evidence.pdf');
    expect(formatInputHistoryText(entry({ text: 'first\nsecond' }))).toBe('first\nsecond');
  });

  it('formats deterministic relative time buckets', () => {
    const now = Date.parse('2026-07-11T12:00:00Z');

    expect(formatInputHistoryMeta(entry(), now)).toBe('Codex · 2h ago');
    expect(formatInputHistoryMeta(entry({ createdAt: 'invalid' }), now)).toBe(
      'Codex · Unknown time',
    );
    expect(formatInputHistoryMeta(entry(), now, false)).toBe('2h ago');
  });
});

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
import { formatInputHistoryMeta, formatInputHistoryPreview } from './inputHistoryPanel';

const html = readFileSync(new URL('../../../static/index.html', import.meta.url), 'utf8');
const css = readFileSync(new URL('../../../static/css/app.css', import.meta.url), 'utf8');
const dropdown = readFileSync(new URL('./historyDropdown.ts', import.meta.url), 'utf8');

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
  it('preserves Bookmarks and exposes Terminal input history directly in the native sidebar', () => {
    expect(html).toContain('id="btn-bookmarks"');
    expect(html).toContain('data-i18n="sidebar.loadBookmarked">Bookmarks</span>');
    expect(html).toContain('id="btn-input-history"');
    expect(html).toContain('data-i18n="sidebar.inputHistory">Prompt &amp; Paste</span>');
    expect(dropdown).toContain("toggleHistoryDropdown('input')");
    expect(dropdown).toContain("activeView === 'input' ? 'btn-input-history' : 'btn-bookmarks'");
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
  });

  it('formats deterministic relative time buckets', () => {
    const now = Date.parse('2026-07-11T12:00:00Z');

    expect(formatInputHistoryMeta(entry(), now)).toBe('Codex · 2h ago');
    expect(formatInputHistoryMeta(entry({ createdAt: 'invalid' }), now)).toBe(
      'Codex · Unknown time',
    );
  });
});

import { afterEach, describe, expect, it, vi } from 'vitest';
import type { Session } from '../../types';
import { dom } from '../../state';
import {
  syncSidebarActiveSessionState,
  syncSidebarSessionDisplayText,
} from './spacesTreeSidebarDisplay';

vi.mock('./sessionList', () => ({
  getSessionDisplayInfo: (session: Session) => ({
    primary: session.name ?? session.terminalTitle ?? session.shellType ?? 'Terminal',
    secondary: session.name ? (session.terminalTitle ?? session.shellType ?? null) : null,
  }),
}));

function makeSession(overrides: Partial<Session>): Session {
  return {
    id: 's1',
    name: null,
    terminalTitle: null,
    shellType: 'pwsh',
    appServerControlOnly: false,
    ...overrides,
  } as Session;
}

afterEach(() => {
  dom.sessionList = null;
  vi.unstubAllGlobals();
});

describe('spaces tree sidebar display sync', () => {
  it('updates a terminal-title-only row without replacing the sidebar tree', () => {
    const title = { textContent: 'old title' };
    const titleRow = {};
    const item = {
      dataset: { sessionId: 's1' },
      querySelector: (selector: string) => {
        if (selector === '.session-title') return title;
        if (selector === '.session-title-row') return titleRow;
        if (selector === '.session-subtitle') return null;
        return null;
      },
    };
    const host = {
      querySelectorAll: (selector: string) =>
        selector === '.session-item[data-session-id]' ? [item] : [],
    };
    dom.sessionList = host as unknown as HTMLElement;

    expect(syncSidebarSessionDisplayText(makeSession({ terminalTitle: 'Codex ⠋' }))).toBe(true);
    expect(title.textContent).toBe('Codex ⠋');
  });

  it('updates named-session subtitles in place when only the terminal title changes', () => {
    const title = { textContent: 'worker' };
    const subtitle = { textContent: 'old title', remove: () => {} };
    const titleRow = {};
    const item = {
      dataset: { sessionId: 's1' },
      querySelector: (selector: string) => {
        if (selector === '.session-title') return title;
        if (selector === '.session-title-row') return titleRow;
        if (selector === '.session-subtitle') return subtitle;
        return null;
      },
    };
    const host = {
      querySelectorAll: (selector: string) =>
        selector === '.session-item[data-session-id]' ? [item] : [],
    };
    dom.sessionList = host as unknown as HTMLElement;

    expect(
      syncSidebarSessionDisplayText(makeSession({ name: 'worker', terminalTitle: 'Codex ⠙' })),
    ).toBe(true);
    expect(title.textContent).toBe('worker');
    expect(subtitle.textContent).toBe('Codex ⠙');
  });

  it('updates active row state without replacing sidebar items', () => {
    function makeItem(sessionId: string) {
      const classes = new Set<string>();
      const attributes = new Map<string, string>();
      return {
        dataset: { sessionId },
        classList: {
          toggle: (className: string, enabled: boolean) => {
            if (enabled) {
              classes.add(className);
            } else {
              classes.delete(className);
            }
          },
        },
        setAttribute: (name: string, value: string) => {
          attributes.set(name, value);
        },
        hasClass: (className: string) => classes.has(className),
        getAttributeValue: (name: string) => attributes.get(name),
      };
    }

    const first = makeItem('s1');
    const second = makeItem('s2');
    const host = {
      querySelectorAll: (selector: string) =>
        selector === '.session-item[data-session-id]' ? [first, second] : [],
    };
    dom.sessionList = host as unknown as HTMLElement;

    expect(syncSidebarActiveSessionState('s2')).toBe(true);
    expect(first.hasClass('active')).toBe(false);
    expect(first.getAttributeValue('aria-current')).toBe('false');
    expect(second.hasClass('active')).toBe(true);
    expect(second.getAttributeValue('aria-current')).toBe('true');
  });
});

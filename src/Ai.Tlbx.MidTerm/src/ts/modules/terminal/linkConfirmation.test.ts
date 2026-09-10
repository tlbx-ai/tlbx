import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import {
  activateTerminalLink,
  setConfirmTerminalLinks,
  shouldConfirmTerminalLinks,
} from './linkConfirmation';
import { openTerminalWebLinkInNewTab } from './webLinks';

vi.mock('./webLinks', () => ({ openTerminalWebLinkInNewTab: vi.fn() }));
vi.mock('../i18n', () => ({ t: (key: string) => key }));
vi.mock('../navigation/backButtonGuard', () => ({ registerBackButtonLayer: vi.fn() }));

describe('terminal link confirmation preference', () => {
  beforeEach(() => {
    const values = new Map<string, string>();
    vi.stubGlobal('localStorage', {
      getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => values.set(key, value),
    });
    vi.clearAllMocks();
  });
  afterEach(() => vi.unstubAllGlobals());

  it('starts enabled and supports disabling and restoring confirmation', () => {
    expect(shouldConfirmTerminalLinks()).toBe(true);
    setConfirmTerminalLinks(false);
    expect(shouldConfirmTerminalLinks()).toBe(false);
    setConfirmTerminalLinks(true);
    expect(shouldConfirmTerminalLinks()).toBe(true);
  });

  it('opens directly after opting out but rejects non-web schemes', () => {
    setConfirmTerminalLinks(false);
    const event = {} as MouseEvent;
    activateTerminalLink(event, 'https://example.com/path');
    expect(openTerminalWebLinkInNewTab).toHaveBeenCalledWith(event, 'https://example.com/path');
    for (const uri of [
      'javascript:alert(1)',
      'data:text/html,hello',
      'file:///secret',
      'invalid',
    ]) {
      activateTerminalLink(event, uri);
    }
    expect(openTerminalWebLinkInNewTab).toHaveBeenCalledOnce();
  });

  it('keeps confirmation enabled when browser storage cannot be read', () => {
    vi.stubGlobal('localStorage', {
      getItem: () => {
        throw new Error('Storage unavailable');
      },
      setItem: () => {
        throw new Error('Storage unavailable');
      },
    });
    expect(() => setConfirmTerminalLinks(false)).not.toThrow();
    expect(shouldConfirmTerminalLinks()).toBe(true);
  });
});

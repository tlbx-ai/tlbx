import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { activateTerminalLink, setConfirmTerminalLinks } from './linkConfirmation';
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

  it('lets xterm receive mouseup when link confirmation opens', () => {
    const overlay = {
      className: '',
      innerHTML: '',
      addEventListener: vi.fn(),
      querySelector: vi.fn(() => ({ focus: vi.fn() })),
    };
    vi.stubGlobal('document', {
      activeElement: null,
      createElement: vi.fn(() => overlay),
      body: { append: vi.fn() },
      addEventListener: vi.fn(),
    });
    const event = {
      preventDefault: vi.fn(),
      stopPropagation: vi.fn(),
    } as unknown as MouseEvent;

    activateTerminalLink(event, 'https://example.com/path');

    expect(event.preventDefault).toHaveBeenCalledOnce();
    expect(event.stopPropagation).not.toHaveBeenCalled();
  });
});

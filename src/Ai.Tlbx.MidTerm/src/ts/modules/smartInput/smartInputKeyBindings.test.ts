import { afterEach, describe, expect, it, vi } from 'vitest';
import { bindSmartInputGlobalKeyBindings } from './smartInputKeyBindings';
import { hasTransientUi } from '../shortcuts/uiContext';
vi.mock('../shortcuts/uiContext', () => ({ hasTransientUi: vi.fn(() => false) }));

afterEach(() => {
  vi.unstubAllGlobals();
  vi.clearAllMocks();
});

function setup() {
  const handlers: ((event: KeyboardEvent) => void)[] = [];
  vi.stubGlobal('document', {
    addEventListener: (type: string, handler: (event: KeyboardEvent) => void) => {
      if (type === 'keydown') handlers.push(handler);
    },
  });
  vi.stubGlobal('window', { addEventListener: vi.fn() });
  const args = {
    beginRecording: vi.fn(),
    canUseVoice: () => false,
    closeFooterTransientUi: vi.fn(() => false),
    endRecording: vi.fn(),
    getInterruptibleAppServerControlSessionId: () => 'agent',
    hasVisibleInput: () => true,
    isRecording: () => false,
    onAppServerControlEscape: vi.fn(),
  };
  bindSmartInputGlobalKeyBindings(args);
  const send = (key: string, extra = {}) => {
    const event = {
      key,
      ctrlKey: false,
      altKey: false,
      shiftKey: false,
      metaKey: false,
      preventDefault: vi.fn(),
      stopPropagation: vi.fn(),
      stopImmediatePropagation: vi.fn(),
      ...extra,
    } as unknown as KeyboardEvent;
    handlers[0]?.(event);
    return event;
  };
  return { args, send };
}
describe('agent interrupt owns only bare Escape without an upper UI layer', () => {
  it.each(['a', 'Enter', 'ArrowDown', 'Control'])(
    'never interrupts on %s while an agent is running',
    (key) => {
      const { args, send } = setup();
      const event = send(key);
      expect(args.onAppServerControlEscape).not.toHaveBeenCalled();
      expect(event.preventDefault).not.toHaveBeenCalled();
    },
  );
  it('interrupts exactly once on an intentional Escape', () => {
    const { args, send } = setup();
    send('Escape');
    expect(args.onAppServerControlEscape).toHaveBeenCalledExactlyOnceWith('agent');
  });
  it.each([{ isComposing: true }, { repeat: true }, { ctrlKey: true }, { defaultPrevented: true }])(
    'protects input state %j',
    (extra) => {
      const { args, send } = setup();
      send('Escape', extra);
      expect(args.onAppServerControlEscape).not.toHaveBeenCalled();
    },
  );
  it('leaves Escape to the search, launcher or rename UI', () => {
    const { args, send } = setup();
    vi.mocked(hasTransientUi).mockReturnValueOnce(true);
    send('Escape');
    expect(args.onAppServerControlEscape).not.toHaveBeenCalled();
  });
  it('dismisses footer UI before interrupting', () => {
    const { args, send } = setup();
    args.closeFooterTransientUi.mockReturnValueOnce(true);
    send('Escape');
    expect(args.onAppServerControlEscape).not.toHaveBeenCalled();
  });
});

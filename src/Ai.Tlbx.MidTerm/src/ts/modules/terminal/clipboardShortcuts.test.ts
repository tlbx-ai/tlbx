import { describe, expect, it, vi } from 'vitest';
import {
  resolveCopyShortcutAction,
  writeTextToClipboardEvent,
  type ShortcutInput,
} from './clipboardShortcuts';

function key(
  value: string,
  mods: Partial<Pick<ShortcutInput, 'ctrlKey' | 'shiftKey' | 'altKey' | 'metaKey'>> = {},
): ShortcutInput {
  return {
    key: value,
    ctrlKey: mods.ctrlKey ?? false,
    shiftKey: mods.shiftKey ?? false,
    altKey: mods.altKey ?? false,
    metaKey: mods.metaKey ?? false,
  };
}

describe('resolveCopyShortcutAction', () => {
  it('copies locally only when there is a selection', () => {
    expect(resolveCopyShortcutAction(key('c', { ctrlKey: true }), 'windows', true)).toBe('copy');
    expect(
      resolveCopyShortcutAction(key('c', { ctrlKey: true, shiftKey: true }), 'unix', true),
    ).toBe('copy');
  });

  it('passes copy shortcuts through to terminal input when nothing is selected', () => {
    expect(resolveCopyShortcutAction(key('c', { ctrlKey: true }), 'windows', false)).toBe(
      'sendKey',
    );
    expect(
      resolveCopyShortcutAction(key('c', { ctrlKey: true, shiftKey: true }), 'unix', false),
    ).toBe('sendKey');
  });

  it('does not turn Cmd+C without a selection into terminal input', () => {
    expect(resolveCopyShortcutAction(key('c', { metaKey: true }), 'unix', false)).toBe('ignore');
  });
});

describe('writeTextToClipboardEvent', () => {
  it('writes through the synchronous copy event without navigator.clipboard', () => {
    const setData = vi.fn();
    const preventDefault = vi.fn();

    expect(
      writeTextToClipboardEvent(
        {
          clipboardData: { setData } as unknown as DataTransfer,
          preventDefault,
        },
        'selected terminal text',
      ),
    ).toBe(true);
    expect(setData).toHaveBeenCalledWith('text/plain', 'selected terminal text');
    expect(preventDefault).toHaveBeenCalledOnce();
  });

  it('leaves the event untouched when clipboardData is unavailable', () => {
    const preventDefault = vi.fn();

    expect(
      writeTextToClipboardEvent({ clipboardData: null, preventDefault }, 'selected terminal text'),
    ).toBe(false);
    expect(preventDefault).not.toHaveBeenCalled();
  });
});

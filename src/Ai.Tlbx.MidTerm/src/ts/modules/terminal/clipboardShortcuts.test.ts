import { describe, expect, it, vi } from 'vitest';
import {
  isCopyShortcut,
  isNativeImagePasteShortcut,
  isPasteShortcut,
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

describe('isPasteShortcut', () => {
  it('matches unified aliases', () => {
    expect(isPasteShortcut(key('v', { ctrlKey: true }))).toBe(true);
    expect(isPasteShortcut(key('V', { ctrlKey: true }))).toBe(true);
    expect(isPasteShortcut(key('v', { ctrlKey: true, shiftKey: true }))).toBe(true);
    expect(isPasteShortcut(key('v', { metaKey: true }))).toBe(true);
    expect(isPasteShortcut(key('v', { altKey: true }))).toBe(false);
  });

  it('rejects unrelated or ambiguous combinations', () => {
    expect(isPasteShortcut(key('v'))).toBe(false);
    expect(isPasteShortcut(key('x', { ctrlKey: true }))).toBe(false);
    expect(isPasteShortcut(key('v', { altKey: true, shiftKey: true }))).toBe(false);
    expect(isPasteShortcut(key('v', { ctrlKey: true, altKey: true }))).toBe(false);
    expect(isPasteShortcut(key('v', { metaKey: true, shiftKey: true }))).toBe(false);
  });
});

describe('isNativeImagePasteShortcut', () => {
  it('matches Alt+V only', () => {
    expect(isNativeImagePasteShortcut(key('v', { altKey: true }))).toBe(true);
    expect(isNativeImagePasteShortcut(key('v', { altKey: true, shiftKey: true }))).toBe(false);
    expect(isNativeImagePasteShortcut(key('v', { ctrlKey: true }))).toBe(false);
  });
});

describe('isCopyShortcut', () => {
  it('matches windows copy shortcuts only', () => {
    expect(isCopyShortcut(key('c', { ctrlKey: true }), 'windows')).toBe(true);
    expect(isCopyShortcut(key('c', { ctrlKey: true, shiftKey: true }), 'windows')).toBe(false);
  });

  it('matches unix copy shortcuts only', () => {
    expect(isCopyShortcut(key('c', { ctrlKey: true, shiftKey: true }), 'unix')).toBe(true);
    expect(isCopyShortcut(key('c', { ctrlKey: true }), 'unix')).toBe(false);
  });

  it('always recognizes the native macOS copy shortcut', () => {
    expect(isCopyShortcut(key('c', { metaKey: true }), 'windows')).toBe(true);
    expect(isCopyShortcut(key('C', { metaKey: true }), 'unix')).toBe(true);
    expect(isCopyShortcut(key('c', { metaKey: true, shiftKey: true }), 'unix')).toBe(false);
  });
});

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

  it('ignores unrelated shortcuts', () => {
    expect(resolveCopyShortcutAction(key('x', { ctrlKey: true }), 'windows', false)).toBe('ignore');
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

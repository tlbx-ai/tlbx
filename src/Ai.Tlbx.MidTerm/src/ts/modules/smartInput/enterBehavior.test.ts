import { describe, expect, it } from 'vitest';

import {
  insertSmartInputLineBreak,
  shouldInsertLineBreakOnEnter,
  shouldSubmitSmartInputOnEnter,
  type SmartInputEnterEvent,
} from './enterBehavior';

function key(
  value: string = 'Enter',
  mods: Partial<Pick<SmartInputEnterEvent, 'ctrlKey' | 'shiftKey' | 'altKey' | 'metaKey'>> = {},
): SmartInputEnterEvent {
  return {
    key: value,
    ctrlKey: mods.ctrlKey ?? false,
    shiftKey: mods.shiftKey ?? false,
    altKey: mods.altKey ?? false,
    metaKey: mods.metaKey ?? false,
  };
}

describe('shouldSubmitSmartInputOnEnter', () => {
  it('submits only on bare Enter', () => {
    expect(shouldSubmitSmartInputOnEnter(key())).toBe(true);
    expect(shouldSubmitSmartInputOnEnter(key('Enter', { shiftKey: true }))).toBe(false);
    expect(shouldSubmitSmartInputOnEnter(key('Enter', { ctrlKey: true }))).toBe(false);
    expect(shouldSubmitSmartInputOnEnter(key('Enter', { altKey: true }))).toBe(false);
    expect(shouldSubmitSmartInputOnEnter(key('Enter', { metaKey: true }))).toBe(false);
    expect(shouldSubmitSmartInputOnEnter(key('Enter', { ctrlKey: true, shiftKey: true }))).toBe(
      false,
    );
  });
});

describe('shouldInsertLineBreakOnEnter', () => {
  it('does not claim bare Enter, meta+Enter, or non-Enter keys', () => {
    expect(shouldInsertLineBreakOnEnter(key())).toBe(false);
    expect(shouldInsertLineBreakOnEnter(key('Enter', { metaKey: true }))).toBe(false);
    expect(shouldInsertLineBreakOnEnter(key('a', { shiftKey: true }))).toBe(false);
  });
});

describe('insertSmartInputLineBreak', () => {
  it('replaces the current selection, moves the caret, and emits input', () => {
    let inputEvents = 0;
    const textarea = {
      value: 'hello world',
      selectionStart: 5,
      selectionEnd: 11,
      dispatchEvent(_event: Event) {
        inputEvents += 1;
        return true;
      },
      setRangeText(replacement: string, start: number, end: number, selectionMode?: string) {
        this.value = `${this.value.slice(0, start)}${replacement}${this.value.slice(end)}`;
        if (selectionMode === 'end') {
          const caret = start + replacement.length;
          this.selectionStart = caret;
          this.selectionEnd = caret;
        }
      },
    } as HTMLTextAreaElement;

    insertSmartInputLineBreak(textarea);

    expect(textarea.value).toBe('hello\n');
    expect(textarea.selectionStart).toBe(6);
    expect(textarea.selectionEnd).toBe(6);
    expect(inputEvents).toBe(1);
  });
});

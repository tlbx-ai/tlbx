import { beforeEach, describe, expect, it, vi } from 'vitest';

const { recordTerminalInputHistory } = vi.hoisted(() => ({
  recordTerminalInputHistory: vi.fn(() => Promise.resolve()),
}));
vi.mock('./inputHistoryApi', () => ({ recordTerminalInputHistory }));
vi.mock('../logging', () => ({ createLogger: () => ({ warn: vi.fn() }) }));

import {
  classifyTerminalEnterIntent,
  captureTerminalInputData,
  captureTerminalLineBreak,
  captureTerminalPasteText,
  expectTerminalSubmission,
  resetTerminalInputCapturesForTest,
  TerminalInputBuffer,
} from './terminalInputCapture';

const enterIntent = (
  modifiers: Partial<Pick<KeyboardEvent, 'altKey' | 'ctrlKey' | 'metaKey' | 'shiftKey'>> = {},
) =>
  classifyTerminalEnterIntent({
    altKey: false,
    code: 'Enter',
    ctrlKey: false,
    key: 'Enter',
    metaKey: false,
    shiftKey: false,
    ...modifiers,
  });

describe('classifyTerminalEnterIntent', () => {
  it('treats only plain Enter as submission', () => {
    expect(enterIntent()).toBe('submit');
  });

  it.each([
    ['Shift+Enter', { shiftKey: true }],
    ['Ctrl+Enter', { ctrlKey: true }],
    ['Alt+Enter', { altKey: true }],
    ['Meta+Enter', { metaKey: true }],
  ])('keeps %s as a line break inside the current item', (_label, modifiers) => {
    expect(enterIntent(modifiers)).toBe('lineBreak');
  });

  it('does not classify unrelated newline data as keyboard submission intent', () => {
    expect(
      classifyTerminalEnterIntent({
        altKey: false,
        code: 'KeyJ',
        ctrlKey: false,
        key: '\n',
        metaKey: false,
        shiftKey: false,
      }),
    ).toBeNull();
  });
});

describe('TerminalInputBuffer', () => {
  it('tracks authored text, cursor edits, and common line editing bytes', () => {
    const buffer = new TerminalInputBuffer();
    buffer.applyData('helo');
    buffer.applyData('\x1b[D');
    buffer.applyData('l');
    buffer.applyData('\x05 world');
    buffer.applyData('\x17MidTerm');

    expect(buffer.value()).toBe('hello MidTerm');
  });

  it('keeps pasted multiline text as one authored value', () => {
    const buffer = new TerminalInputBuffer();
    buffer.appendText('first\nsecond\nthird');

    expect(buffer.consume()).toBe('first\nsecond\nthird');
    expect(buffer.value()).toBe('');
  });
});

describe('terminal input submission capture', () => {
  beforeEach(() => {
    recordTerminalInputHistory.mockClear();
    resetTerminalInputCapturesForTest();
  });

  it('records only an actual unmodified Enter delivery', async () => {
    captureTerminalInputData('session-a', 'review this');
    expectTerminalSubmission('session-a');
    captureTerminalInputData('session-a', '\r');
    await Promise.resolve();

    expect(recordTerminalInputHistory).toHaveBeenCalledWith('session-a', 'review this');
  });

  it('does not split modified-Enter line breaks into separate commands', async () => {
    captureTerminalInputData('session-a', 'first line');
    captureTerminalLineBreak('session-a', true);
    captureTerminalInputData('session-a', '\x1b\r');
    captureTerminalInputData('session-a', 'second line');
    expectTerminalSubmission('session-a');
    captureTerminalInputData('session-a', '\r');
    await Promise.resolve();

    expect(recordTerminalInputHistory).toHaveBeenCalledTimes(1);
    expect(recordTerminalInputHistory).toHaveBeenCalledWith('session-a', 'first line\nsecond line');
  });

  it('keeps newlines inside a paste and records the later submission once', async () => {
    captureTerminalInputData('session-a', 'prefix ');
    captureTerminalPasteText('session-a', 'one\ntwo');
    captureTerminalInputData('session-a', ' suffix');
    expectTerminalSubmission('session-a');
    captureTerminalInputData('session-a', '\r');
    await Promise.resolve();

    expect(recordTerminalInputHistory).toHaveBeenCalledTimes(1);
    expect(recordTerminalInputHistory).toHaveBeenCalledWith('session-a', 'prefix one\ntwo suffix');
  });

  it('does not submit a newline byte without a matching Enter intent', async () => {
    captureTerminalInputData('session-a', 'still composing\nmore');
    await Promise.resolve();

    expect(recordTerminalInputHistory).not.toHaveBeenCalled();
  });
});

import { afterEach, describe, expect, it, vi } from 'vitest';
import {
  bindMobileTerminalTextInput,
  mobileTextEdit,
  resetMobileTerminalTextInput,
} from './mobileTextInput';

vi.mock('../touchController/detection', () => ({
  isTouchDevice: () => true,
  hasPrecisePointer: () => false,
}));

describe('mobile edit translation', () => {
  it.each([
    ['', 'h', 'h'],
    ['h', 'he', 'e'],
    ['hel', 'hello ', 'lo '],
    ['teh', 'the', '\x7f\x7fhe'],
    ['git chek', 'git check', '\x7fck'],
    ['word ', 'word', '\x7f'],
    ['abc', '', '\x7f\x7f\x7f'],
    ['schon', 'schön', '\x7f\x7fön'],
    ['に', '日本', '\x7f日本'],
    ['😀', '😃', '\x7f😃'],
    ['hello', 'hello', ''],
  ])('translates %j to %j', (before, after, expected) => {
    expect(mobileTextEdit(before, after)).toBe(expected);
  });
  it.each([
    ['e\u0301', 'e'],
    ['👨‍👩‍👧', 'family'],
    ['👍🏽', '👍'],
  ])('does not guess how the remote editor erases %j', (before, after) => {
    expect(mobileTextEdit(before, after)).toBeNull();
  });
  it.each(['line\ncommand', '\x1b[2J', '\x03', '\t'])(
    'never injects control characters from text mutation %j',
    (value) => {
      expect(mobileTextEdit('', value)).toBeNull();
    },
  );
});

const disposables: Array<{ dispose: () => void }> = [];
afterEach(() => {
  disposables.splice(0).forEach((d) => d.dispose());
});
function fixture() {
  const handlers = new Map<string, (event: Event) => void>();
  const element = () => ({
    addEventListener: (type: string, fn: (event: Event) => void) => handlers.set(type, fn),
    removeEventListener: (type: string) => handlers.delete(type),
  });
  const textarea = { ...element(), value: '', selectionStart: 0, selectionEnd: 0 };
  let context = 'shell';
  const sent: string[] = [];
  const enter = vi.fn();
  const paste = vi.fn();
  const unsupportedEdit = vi.fn();
  const binding = bindMobileTerminalTextInput({
    sessionId: 'test',
    container: element() as never,
    textarea: textarea as never,
    send: (value) => {
      resetMobileTerminalTextInput('test');
      sent.push(value);
    },
    enter,
    paste,
    context: () => context,
    unsupportedEdit,
  });
  disposables.push(binding);
  const event = (type: string, extra: Record<string, unknown> = {}) => {
    const e = {
      target: textarea,
      cancelable: true,
      key: '',
      keyCode: 0,
      ctrlKey: false,
      metaKey: false,
      altKey: false,
      getModifierState: () => false,
      stopImmediatePropagation: vi.fn(),
      preventDefault: vi.fn(),
      ...extra,
    };
    handlers.get(type)?.(e as unknown as Event);
    return e;
  };
  event('focus');
  const edit = (value: string, inputType = 'insertText', isComposing = false) => {
    const e = event('beforeinput', { inputType, isComposing });
    if (!e.preventDefault.mock.calls.length) {
      textarea.value = value;
      textarea.selectionStart = textarea.selectionEnd = value.length;
      event('input', { inputType, isComposing });
    }
  };
  return {
    event,
    edit,
    sent,
    textarea,
    enter,
    paste,
    unsupportedEdit,
    binding,
    changeContext: () => {
      context = 'another process';
    },
  };
}

describe('mobile textarea input ownership', () => {
  it('retains the keyboard context and sends a suggestion exactly once', () => {
    const f = fixture();
    for (const text of ['h', 'he', 'hel']) f.edit(text);
    f.edit('hello ', 'insertReplacementText');
    expect(f.sent).toEqual(['h', 'e', 'l', 'lo ']);
    expect(f.textarea.value).toBe('hello ');
    f.event('input', { inputType: 'insertReplacementText' });
    expect(f.sent).toHaveLength(4);
  });
  it('uses the value diff even when an OSK labels a replacement insertText', () => {
    const f = fixture();
    f.edit('teh');
    f.edit('the');
    expect(f.sent).toEqual(['teh', '\x7f\x7fhe']);
  });
  it('lets the browser edit printable keys without forwarding them twice', () => {
    const f = fixture();
    const key = f.event('keydown', { key: 'a', keyCode: 65 });
    expect(key.stopImmediatePropagation).toHaveBeenCalledOnce();
    expect(key.preventDefault).not.toHaveBeenCalled();
    f.event('keypress', { key: 'a' });
    f.edit('a');
    expect(f.sent).toEqual(['a']);
  });
  it('deduplicates composition updates, final input and compositionend', async () => {
    const f = fixture();
    f.event('compositionstart');
    f.edit('n', 'insertCompositionText', true);
    f.edit('に', 'insertCompositionText', true);
    f.edit('日本', 'insertCompositionText', true);
    f.event('compositionend');
    f.edit('日本', 'insertText');
    await Promise.resolve();
    expect(f.sent).toEqual(['n', '\x7fに', '\x7f日本']);
  });
  it('sends a final composition mutation without a following input event', async () => {
    const f = fixture();
    f.event('compositionstart');
    f.edit('a', 'insertCompositionText', true);
    f.event('compositionend');
    f.textarea.value = 'ä';
    f.textarea.selectionStart = f.textarea.selectionEnd = 1;
    await Promise.resolve();
    expect(f.sent).toEqual(['a', '\x7fä']);
  });
  it.each(['blur', 'external', 'navigation'])('resets context after %s', (reason) => {
    const f = fixture();
    f.edit('old');
    if (reason === 'external') resetMobileTerminalTextInput('test');
    else if (reason === 'navigation') f.event('keydown', { key: 'ArrowUp' });
    else f.event('blur');
    expect(f.textarea.value).toBe('');
    f.event('focus');
    f.edit('new');
    expect(f.sent).toEqual(['old', 'new']);
  });
  it('resets on a process or buffer change before the next text edit', () => {
    const f = fixture();
    f.edit('old');
    f.changeContext();
    f.edit('n');
    expect(f.sent).toEqual(['old', 'n']);
  });
  it('routes an input-only Enter once and resets the editable tail', () => {
    const f = fixture();
    f.edit('command');
    f.edit('command\n', 'insertLineBreak');
    expect(f.enter).toHaveBeenCalledOnce();
    expect(f.textarea.value).toBe('');
    expect(f.sent).toEqual(['command']);
  });
  it('deletes before the tracked tail when a keyboard emits only beforeinput', () => {
    const f = fixture();
    f.edit('', 'deleteContentBackward');
    expect(f.sent).toEqual(['\x7f']);
  });
  it('does not apply a correction when the textarea caret has moved inside the tail', () => {
    const f = fixture();
    f.edit('abc');
    f.textarea.value = 'aXbc';
    f.textarea.selectionStart = f.textarea.selectionEnd = 2;
    f.event('input', { inputType: 'insertText' });
    expect(f.sent).toEqual(['abc']);
    expect(f.unsupportedEdit).toHaveBeenCalledOnce();
  });
  it('does not apply a delayed composition after losing focus', async () => {
    const f = fixture();
    f.edit('a');
    f.event('compositionend');
    f.event('blur');
    await Promise.resolve();
    expect(f.sent).toEqual(['a']);
  });
  it('unregisters the session input reset on disposal', () => {
    const f = fixture();
    f.edit('a');
    f.binding.dispose();
    f.textarea.value = 'detached';
    resetMobileTerminalTextInput('test');
    expect(f.textarea.value).toBe('detached');
  });
});

describe('mobile browser event variants', () => {
  it('ignores late input after blur before legacy handlers can append it', () => {
    const f = fixture();
    f.edit('hel');
    f.event('blur');
    f.textarea.value = 'hello';
    f.textarea.selectionStart = f.textarea.selectionEnd = 5;
    const event = f.event('input', { inputType: 'insertReplacementText' });
    expect(event.stopImmediatePropagation).toHaveBeenCalledOnce();
    expect(f.sent).toEqual(['hel']);
    f.event('focus');
    expect(f.textarea.value).toBe('');
    f.edit('new');
    expect(f.sent).toEqual(['hel', 'new']);
  });
  it('handles a non-cancelable Enter on input instead of submitting twice', () => {
    const f = fixture();
    f.edit('command');
    f.event('beforeinput', { inputType: 'insertLineBreak', cancelable: false });
    expect(f.enter).not.toHaveBeenCalled();
    f.textarea.value = 'command\n';
    f.event('input', { inputType: 'insertLineBreak' });
    expect(f.enter).toHaveBeenCalledOnce();
    expect(f.textarea.value).toBe('');
  });
  it('uses the existing paste path for the keyboard clipboard', () => {
    const f = fixture();
    f.edit('prefix ');
    f.edit('prefix first\nsecond', 'insertFromPaste');
    expect(f.paste).toHaveBeenCalledWith('first\nsecond');
    expect(f.sent).toEqual(['prefix ']);
    expect(f.textarea.value).toBe('');
  });
  it('preserves manual Backspace for a combined character without guessing correction lengths', () => {
    const f = fixture();
    f.edit('e\u0301');
    f.edit('', 'deleteContentBackward');
    expect(f.sent).toEqual(['e\u0301', '\x7f']);
    expect(f.unsupportedEdit).not.toHaveBeenCalled();
    expect(f.textarea.value).toBe('');
  });
});

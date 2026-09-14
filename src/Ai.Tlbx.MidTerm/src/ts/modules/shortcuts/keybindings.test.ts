import { describe, expect, it } from 'vitest';
import { bindingProblem, matchesSearch, normalizeBinding, type KeyStroke } from './keybindings';

const stroke = (key: string, extra: Partial<KeyStroke> = {}): KeyStroke => ({
  key,
  ctrlKey: false,
  shiftKey: false,
  altKey: false,
  metaKey: false,
  ...extra,
});

describe('browser-safe shortcuts', () => {
  it.each([
    'Ctrl+T',
    'Meta+W',
    'Ctrl+Tab',
    'Ctrl+Shift+P',
    'Shift+Meta+P',
    'Ctrl+Shift+T',
    'Ctrl+Shift+N',
    'Ctrl+Shift+Y',
    'Ctrl+Shift+G',
    'Ctrl+Shift+I',
    'Ctrl+L',
    'F5',
    'F12',
    'Alt+T',
    'Ctrl+Alt+Q',
    'R',
  ])('does not allow browser or terminal reservation %s', (binding) =>
    expect(bindingProblem(binding)).not.toBeNull(),
  );
  it.each([
    'Ctrl+Shift+Space',
    'Shift+Meta+Space',
    'Ctrl+Shift+ArrowRight',
    'Ctrl+Shift+Enter',
    'F2',
    'Ctrl+Alt+W',
    'Ctrl+Alt+A',
    'Ctrl+Alt+S',
    'Ctrl+Alt+D',
  ])('supports explicit application binding %s', (binding) =>
    expect(bindingProblem(binding)).toBeNull(),
  );
  it('does not recognize AltGr, dead keys, or composing text as commands', () => {
    expect(
      normalizeBinding(stroke('@', { ctrlKey: true, altKey: true, getModifierState: () => true })),
    ).toBeNull();
    expect(normalizeBinding(stroke('Dead'))).toBeNull();
    expect(
      normalizeBinding(stroke(' ', { ctrlKey: true, shiftKey: true, isComposing: true })),
    ).toBeNull();
  });
  it('normalizes semantic keys and platform modifiers consistently', () => {
    expect(normalizeBinding(stroke('∑', { code: 'KeyW', ctrlKey: true, altKey: true }))).toBe(
      'Ctrl+Alt+W',
    );
    expect(normalizeBinding(stroke('w', { code: 'KeyW' }))).toBe('W');
    expect(
      normalizeBinding(
        stroke('w', { code: 'KeyW', ctrlKey: true, altKey: true, getModifierState: () => true }),
      ),
    ).toBeNull();
    expect(normalizeBinding(stroke(' ', { ctrlKey: true, shiftKey: true }))).toBe(
      'Ctrl+Shift+Space',
    );
    expect(normalizeBinding(stroke(' ', { metaKey: true, shiftKey: true }))).toBe(
      'Shift+Meta+Space',
    );
    expect(normalizeBinding(stroke('Control'))).toBeNull();
  });
  it('searches all words across labels and paths without accent sensitivity', () => {
    expect(matchesSearch('session andern', 'Session ändern', 'Q:\\repos\\tlbx')).toBe(true);
    expect(matchesSearch('tlbx terminal', 'Terminal', 'Q:\\repos\\tlbx-2')).toBe(true);
    expect(matchesSearch('missing', 'Terminal', 'Q:\\repos\\tlbx-2')).toBe(false);
  });
});

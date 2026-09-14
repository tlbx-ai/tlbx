import { describe, expect, it } from 'vitest';
import {
  bindingProblem,
  matchesSearch,
  normalizeBinding,
  ShortcutModifiers,
  type KeyStroke,
} from './keybindings';

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

describe('right Control with AltGr session navigation', () => {
  const altGr = (key: string, extra: Partial<KeyStroke> = {}): KeyStroke =>
    stroke(key.toLowerCase(), {
      code: `Key${key}`,
      ctrlKey: true,
      altKey: true,
      getModifierState: (name) => name === 'AltGraph',
      ...extra,
    });
  const control = (code = 'ControlRight'): KeyStroke => stroke('Control', { code, ctrlKey: true });

  it.each(['W', 'A', 'S', 'D'])('requires physical right Control for AltGr+%s', (key) => {
    const modifiers = new ShortcutModifiers();
    modifiers.keyDown(control('ControlLeft')); // Windows synthetic AltGr Control
    modifiers.keyDown(stroke('AltGraph', { code: 'AltRight', ctrlKey: true, altKey: true }));
    expect(modifiers.normalize(altGr(key))).toBeNull();
    modifiers.keyDown(control());
    expect(modifiers.normalize(altGr(key))).toBe(`Ctrl+Alt+${key}`);
    expect(modifiers.normalize(altGr(key, { repeat: true }))).toBe(`Ctrl+Alt+${key}`);
    modifiers.keyUp(control());
    expect(modifiers.normalize(altGr(key))).toBeNull();
  });

  it('accepts Control before AltGr and keeps it after AltGr release', () => {
    const modifiers = new ShortcutModifiers();
    modifiers.keyDown(control());
    modifiers.keyDown(stroke('AltGraph', { code: 'AltRight', ctrlKey: true, altKey: true }));
    expect(modifiers.normalize(altGr('W'))).toBe('Ctrl+Alt+W');
    modifiers.keyUp(stroke('AltGraph', { code: 'AltRight', ctrlKey: true }));
    expect(modifiers.normalize(altGr('D'))).toBe('Ctrl+Alt+D');
  });

  it('preserves AltGr text, composition and extra modifiers even with right Control', () => {
    const modifiers = new ShortcutModifiers();
    modifiers.keyDown(control());
    for (const event of [
      altGr('Q', { key: '@' }),
      altGr('E', { key: '�' }),
      altGr('W', { isComposing: true }),
      altGr('W', { key: 'Dead' }),
      altGr('W', { shiftKey: true }),
      altGr('W', { metaKey: true }),
    ]) {
      expect(modifiers.normalize(event)).toBeNull();
    }
  });

  it('clears stale Control on blur/reset or when modifier flags show it released', () => {
    const modifiers = new ShortcutModifiers();
    modifiers.keyDown(control());
    modifiers.clear();
    expect(modifiers.normalize(altGr('W'))).toBeNull();
    modifiers.keyDown(control());
    modifiers.keyDown(stroke('a'));
    expect(modifiers.normalize(altGr('W'))).toBeNull();
    modifiers.keyDown(control());
    modifiers.keyUp(stroke('a'));
    expect(modifiers.normalize(altGr('W'))).toBeNull();
  });
});

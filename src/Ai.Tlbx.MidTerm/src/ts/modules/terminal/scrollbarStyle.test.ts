import { describe, expect, it } from 'vitest';
import { applyTerminalScrollbarStyleClass, resolveEffectiveScrollbarStyle } from './scrollbarStyle';

class MockClassList {
  private classes = new Set<string>();

  add(token: string): void {
    this.classes.add(token);
  }

  remove(...tokens: string[]): void {
    tokens.forEach((token) => this.classes.delete(token));
  }

  has(token: string): boolean {
    return this.classes.has(token);
  }
}

describe('scrollbarStyle', () => {
  it('resolves hover to always on non-hover devices', () => {
    expect(resolveEffectiveScrollbarStyle('hover', false)).toBe('always');
    expect(resolveEffectiveScrollbarStyle('hover', true)).toBe('hover');
    expect(resolveEffectiveScrollbarStyle('off', false)).toBe('off');
    expect(resolveEffectiveScrollbarStyle('always', false)).toBe('always');
  });

  it('applies one effective class and removes stale classes', () => {
    const mock = { classList: new MockClassList() };
    mock.classList.add('scrollbar-off');
    mock.classList.add('scrollbar-hover');
    mock.classList.add('scrollbar-always');

    const applied = applyTerminalScrollbarStyleClass(mock, 'hover', false);

    expect(applied).toBe('always');
    expect(mock.classList.has('scrollbar-off')).toBe(false);
    expect(mock.classList.has('scrollbar-hover')).toBe(false);
    expect(mock.classList.has('scrollbar-always')).toBe(true);
  });
});

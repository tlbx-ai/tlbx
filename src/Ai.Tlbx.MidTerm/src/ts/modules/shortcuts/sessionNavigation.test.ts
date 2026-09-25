import { describe, expect, it } from 'vitest';
import { directionalSession } from './sessionNavigation';

const panes = [
  { id: 'a', left: 0, top: 0, width: 100, height: 100 },
  { id: 'b', left: 100, top: 0, width: 100, height: 100 },
  { id: 'c', left: 0, top: 100, width: 100, height: 100 },
  { id: 'd', left: 100, top: 100, width: 100, height: 100 },
];

describe('directional session focus', () => {
  it('follows all four directions in a split layout', () => {
    expect(directionalSession(panes, 'a', 'right')).toBe('b');
    expect(directionalSession(panes, 'b', 'down')).toBe('d');
    expect(directionalSession(panes, 'd', 'left')).toBe('c');
    expect(directionalSession(panes, 'c', 'up')).toBe('a');
  });
  it('stays put at an edge or when no current pane exists', () => {
    expect(directionalSession(panes, 'a', 'up')).toBeNull();
    expect(directionalSession(panes, 'b', 'right')).toBeNull();
    expect(directionalSession(panes, 'missing', 'down')).toBeNull();
    expect(directionalSession([], 'a', 'left')).toBeNull();
  });
  it('prefers the same column over a closer diagonal pane in an uneven layout', () => {
    const uneven = [
      panes[0]!,
      { id: 'diagonal', left: 101, top: 100, width: 20, height: 20 },
      { id: 'aligned', left: 0, top: 150, width: 100, height: 100 },
    ];
    expect(directionalSession(uneven, 'a', 'down')).toBe('aligned');
  });
});

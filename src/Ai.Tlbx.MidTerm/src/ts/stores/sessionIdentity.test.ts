import { afterEach, describe, expect, it } from 'vitest';
import { $sessions, setSessions } from './index';
import type { Session } from '../types';

describe('session snapshot identity', () => {
  afterEach(() => $sessions.set({}));

  it('preserves unaffected sessions while publishing activity changes and removals', () => {
    const a = { id: 'a', name: 'A', order: 0, supervisor: { currentHeat: 0 } } as Session;
    const b = { id: 'b', name: 'B', order: 1 } as Session;
    setSessions([a, b]);
    const original = $sessions.get();
    expect(setSessions([structuredClone(a), structuredClone(b)])).toBe(false);
    expect($sessions.get()).toBe(original);
    expect(setSessions([{ ...a, supervisor: { currentHeat: 1 } } as Session, { ...b }])).toBe(true);
    expect($sessions.get().b).toBe(original.b);
    expect($sessions.get().a).not.toBe(original.a);
    expect(setSessions([{ ...b }])).toBe(true);
    expect($sessions.get().b).toBe(original.b);
    expect($sessions.get().a).toBeUndefined();
  });
});

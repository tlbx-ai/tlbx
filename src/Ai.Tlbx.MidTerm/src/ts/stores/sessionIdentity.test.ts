import { afterEach, describe, expect, it } from 'vitest';
import {
  $sessions,
  setSessions,
  setSession,
  removeSession,
  markSessionClosing,
  cancelSessionClosing,
  filterClosingSessions,
} from './index';
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

  it('ignores late session updates while closing and permits recovery after a failed close', () => {
    const session = { id: 'closing-update', name: 'Shell' } as Session;
    setSession(session);
    markSessionClosing(session.id);
    removeSession(session.id);
    try {
      setSession({ ...session, name: 'Late rename' });
      setSessions([session]);
      expect($sessions.get()[session.id]).toBeUndefined();
      expect(filterClosingSessions([session])).toEqual([]);
      cancelSessionClosing(session.id);
      setSession(session);
      expect($sessions.get()[session.id]?.name).toBe('Shell');
    } finally {
      cancelSessionClosing(session.id);
    }
  });
});

import { afterEach, describe, expect, it, vi } from 'vitest';
import type { TerminalState } from '../../types';
import {
  disposeTerminalPresentation,
  reduceTerminalPresentationSnapshot,
  scheduleTerminalPresentationFrame,
  type TerminalPresentationSnapshot,
} from './terminalPresentation';

function snapshot(
  epoch: number,
  role: TerminalPresentationSnapshot['role'],
  overrides: Partial<TerminalPresentationSnapshot> = {},
): TerminalPresentationSnapshot {
  return {
    sessionId: 'session-1',
    epoch,
    role,
    ownerOnline: true,
    ownerLabel: 'Windows PC · Chrome',
    canonicalCols: 120,
    canonicalRows: 36,
    viewportWidth: 1280,
    viewportHeight: 720,
    passiveScale: role === 'owner' ? 1 : 0.72,
    actionState: 'idle',
    ...overrides,
  };
}

describe('terminal presentation reducer', () => {
  it('accepts a complete first snapshot and a newer ownership epoch', () => {
    const follower = snapshot(4, 'follower');
    const owner = snapshot(5, 'owner', { ownerLabel: null });

    expect(reduceTerminalPresentationSnapshot(undefined, follower)).toBe(follower);
    expect(reduceTerminalPresentationSnapshot(follower, owner)).toBe(owner);
  });

  it('rejects a delayed response from an older epoch', () => {
    const current = snapshot(9, 'follower', { ownerLabel: 'iPad · Safari' });
    const stale = snapshot(8, 'owner');

    expect(reduceTerminalPresentationSnapshot(current, stale)).toBe(current);
  });

  it('does not reverse owner identity inside the same epoch', () => {
    const owner = snapshot(12, 'owner');
    const contradictoryFollower = snapshot(12, 'follower');

    expect(reduceTerminalPresentationSnapshot(owner, contradictoryFollower)).toBe(owner);
  });
});

describe('terminal presentation frame lifecycle', () => {
  const originalRequestAnimationFrame = globalThis.requestAnimationFrame;
  const originalCancelAnimationFrame = globalThis.cancelAnimationFrame;
  const state = {} as TerminalState;

  afterEach(() => {
    disposeTerminalPresentation(state);
    globalThis.requestAnimationFrame = originalRequestAnimationFrame;
    globalThis.cancelAnimationFrame = originalCancelAnimationFrame;
  });

  it('coalesces repeated work into one pending presentation frame', () => {
    const frames: FrameRequestCallback[] = [];
    globalThis.requestAnimationFrame = vi.fn((callback: FrameRequestCallback) => {
      frames.push(callback);
      return 41;
    });
    const render = vi.fn();

    scheduleTerminalPresentationFrame(state, render);
    scheduleTerminalPresentationFrame(state, render);
    frames[0]?.(0);

    expect(frames).toHaveLength(1);
    expect(render).toHaveBeenCalledOnce();
  });

  it('cancels and invalidates pending presentation work on teardown', () => {
    let pending: FrameRequestCallback | undefined;
    globalThis.requestAnimationFrame = vi.fn((callback: FrameRequestCallback) => {
      pending = callback;
      return 73;
    });
    globalThis.cancelAnimationFrame = vi.fn();
    const render = vi.fn();

    scheduleTerminalPresentationFrame(state, render);
    disposeTerminalPresentation(state);
    pending?.(0);

    expect(cancelAnimationFrame).toHaveBeenCalledWith(73);
    expect(render).not.toHaveBeenCalled();
  });
});

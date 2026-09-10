import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { $sessions } from '../../stores';
import {
  destroyHeatIndicator,
  getDisplayedSessionHeat,
  initHeatIndicator,
  recordTextActivity,
  registerHeatCanvas,
} from './heatIndicator';

describe('server text activity cooldown', () => {
  let now: number;
  let listeners: Record<string, () => void>;
  beforeEach(() => {
    now = Date.parse('2026-09-10T12:00:00Z');
    vi.spyOn(Date, 'now').mockImplementation(() => now);
    listeners = {};
    vi.stubGlobal('document', {
      hidden: false,
      addEventListener: (n: string, f: () => void) => {
        listeners[n] = f;
      },
      removeEventListener: vi.fn(),
    });
    vi.stubGlobal('window', { addEventListener: vi.fn(), removeEventListener: vi.fn() });
    $sessions.set({});
  });
  afterEach(() => {
    destroyHeatIndicator();
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });
  it('lets a fresh snapshot correct a buffered event without reheating it', () => {
    recordTextActivity('a', '2026-09-10T12:00:00Z', 0);
    expect(getDisplayedSessionHeat('a')).toBe(1);
    recordTextActivity('a', '2026-09-10T12:00:00Z', 32_000);
    expect(getDisplayedSessionHeat('a')).toBe(0);
    recordTextActivity('a', '2026-09-10T12:00:00Z', 0);
    expect(getDisplayedSessionHeat('a')).toBe(0);
  });
  it('cools with no packets, timers or animation loop and isolates sessions', () => {
    recordTextActivity('a', '2026-09-10T12:00:00Z', 0);
    expect(getDisplayedSessionHeat('a')).toBe(1);
    expect(getDisplayedSessionHeat('b')).toBe(0);
    now += 5000;
    expect(getDisplayedSessionHeat('a')).toBeCloseTo(5 / 6);
    now += 25_000;
    expect(getDisplayedSessionHeat('a')).toBe(0);
  });
  it('uses server age across clock skew and ignores stale or identical snapshots', () => {
    recordTextActivity('a', '2030-01-01T00:00:00Z', 5000);
    expect(getDisplayedSessionHeat('a')).toBeCloseTo(5 / 6);
    now += 25_000;
    recordTextActivity('a', '2030-01-01T00:00:00Z', 0);
    recordTextActivity('a', '2029-01-01T00:00:00Z', 0);
    expect(getDisplayedSessionHeat('a')).toBe(0);
    recordTextActivity('a', '2030-01-01T00:00:01Z', 0);
    expect(getDisplayedSessionHeat('a')).toBe(1);
  });
  it('registers new surfaces at actual age and does not restart on row reconciliation', () => {
    recordTextActivity('a', '2026-09-10T12:00:00Z', 0);
    now += 4000;
    const setProperty = vi.fn();
    const el = { style: { setProperty } } as unknown as HTMLElement;
    registerHeatCanvas('a', el);
    expect(setProperty).toHaveBeenCalledWith('--heat-age', '-4000ms');
    setProperty.mockClear();
    registerHeatCanvas('a', el);
    expect(setProperty).not.toHaveBeenCalled();
  });
  it('returns cold after a hidden minute and ignores raw hot snapshots', () => {
    initHeatIndicator();
    const setProperty = vi.fn();
    registerHeatCanvas('a', { style: { setProperty } } as unknown as HTMLElement);
    recordTextActivity('a', '2026-09-10T12:00:00Z', 0);
    now += 60_000;
    listeners.visibilitychange?.();
    expect(setProperty).toHaveBeenCalledWith('--heat-red', 'none');
    expect(getDisplayedSessionHeat('a')).toBe(0);
    $sessions.set({
      b: {
        id: 'b',
        supervisor: { currentHeat: 1, lastOutputAt: new Date(now).toISOString() },
      } as any,
    });
    expect(getDisplayedSessionHeat('b')).toBe(0);
  });
});

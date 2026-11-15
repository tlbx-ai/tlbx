import { afterEach, describe, expect, it, vi } from 'vitest';
import { shouldTraceAppServerControlHistory } from './historyTrace';

describe('history trace opt-in', () => {
  afterEach(() => vi.unstubAllGlobals());
  it('keeps normal local and remote scrolling free of diagnostic work', () => {
    for (const hostname of ['localhost', '127.0.0.1', 'terminal.example']) {
      vi.stubGlobal('window', { location: { hostname }, localStorage: { getItem: () => null } });
      expect(shouldTraceAppServerControlHistory()).toBe(false);
    }
  });
  it('retains explicit session and stored diagnostic opt-ins', () => {
    vi.stubGlobal('window', { __MIDTERM_APP_SERVER_CONTROL_TRACE__: true });
    expect(shouldTraceAppServerControlHistory()).toBe(true);
    vi.stubGlobal('window', { localStorage: { getItem: () => '1' } });
    expect(shouldTraceAppServerControlHistory()).toBe(true);
  });
});

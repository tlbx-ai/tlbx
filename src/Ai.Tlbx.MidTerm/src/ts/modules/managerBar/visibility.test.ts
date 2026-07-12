import { describe, expect, it } from 'vitest';

import { shouldShowManagerBar } from './visibility';

describe('manager bar visibility', () => {
  it('stays hidden when there is no active session', () => {
    expect(shouldShowManagerBar(true, null)).toBe(false);
  });

  it('shows only when enabled and a session is active', () => {
    expect(shouldShowManagerBar(true, 'session-1')).toBe(true);
    expect(shouldShowManagerBar(false, 'session-1')).toBe(false);
  });
});

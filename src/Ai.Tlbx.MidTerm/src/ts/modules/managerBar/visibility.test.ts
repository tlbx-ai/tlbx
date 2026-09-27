import { describe, expect, it } from 'vitest';

import { shouldShowManagerBar } from './visibility';

describe('manager bar visibility', () => {
  it('keeps queued work visible when automation buttons are disabled', () => {
    expect(shouldShowManagerBar(false, 'session-1', true)).toBe(true);
    expect(shouldShowManagerBar(false, null, true)).toBe(false);
  });
});

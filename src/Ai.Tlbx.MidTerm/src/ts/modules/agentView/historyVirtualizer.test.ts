import { describe, expect, it } from 'vitest';

import { resolveAppServerControlHistoryWindowTargetCount } from './historyVirtualizer';

describe('historyVirtualizer', () => {
  it('sizes the retained history window with at least 20 items of margin on each side', () => {
    const count = resolveAppServerControlHistoryWindowTargetCount(
      { clientHeight: 600 } as HTMLDivElement,
      10,
      [150, 150, 150],
      {
        overscanItems: 12,
        fetchAheadItems: 6,
      },
    );

    expect(count).toBe(44);
  });
});

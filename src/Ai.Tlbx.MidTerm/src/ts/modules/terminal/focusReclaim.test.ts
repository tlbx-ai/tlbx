import { describe, expect, it } from 'vitest';

import { shouldReclaimTerminalFocusOnMouseUp } from './focusReclaim';

describe('terminal focus reclaim', () => {
  it('does not reclaim terminal focus when mouseup happens inside the AppServerControl panel', () => {
    const appServerControlPanel = { tagName: 'DIV', closest: () => null };
    const appServerControlBody = {
      tagName: 'DIV',
      closest: (selector: string) =>
        selector === '.agent-view-panel' ? appServerControlPanel : null,
    };

    expect(
      shouldReclaimTerminalFocusOnMouseUp(appServerControlBody as EventTarget, {
        rangeCount: 0,
        isCollapsed: true,
      }),
    ).toBe(false);
  });

  it('does not reclaim terminal focus while a non-collapsed document selection exists', () => {
    const target = { tagName: 'DIV', closest: () => null };

    expect(
      shouldReclaimTerminalFocusOnMouseUp(target as EventTarget, {
        rangeCount: 1,
        isCollapsed: false,
      }),
    ).toBe(false);
  });

  it('still reclaims terminal focus after a plain non-interactive mouseup with no selection', () => {
    const target = { tagName: 'DIV', closest: () => null };

    expect(
      shouldReclaimTerminalFocusOnMouseUp(target as EventTarget, {
        rangeCount: 0,
        isCollapsed: true,
      }),
    ).toBe(true);
  });
});

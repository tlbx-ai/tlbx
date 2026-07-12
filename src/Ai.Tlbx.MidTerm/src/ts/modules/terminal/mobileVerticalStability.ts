import type { TerminalState } from '../../types';
import { sessionTerminals } from '../../state';

const MOBILE_VIEWPORT_WIDTH_MAX = 768;
const MOBILE_VERTICAL_WIDTH_TOLERANCE_PX = 2;
const MOBILE_VERTICAL_HEIGHT_TOLERANCE_PX = 1;
const MOBILE_VERTICAL_BOTTOM_STICKY_PX = 32;
const MOBILE_CURSOR_GUARD_PX = 8;

type ViewportSnapshot = {
  width: number;
  height: number;
};

let lastObservedViewportSnapshot: ViewportSnapshot | null = null;
let mobileVerticalStabilityActive = false;

export function rememberCurrentMobileViewportSnapshot(): void {
  lastObservedViewportSnapshot = readViewportSnapshot();
}

export function isMobileTerminalViewport(width = window.innerWidth): boolean {
  const mediaMatches =
    typeof window.matchMedia === 'function' &&
    window.matchMedia(`(max-width: ${MOBILE_VIEWPORT_WIDTH_MAX}px)`).matches;
  return width <= MOBILE_VIEWPORT_WIDTH_MAX || mediaMatches;
}

export function observeMobileVerticalViewportChange(): boolean {
  const next = readViewportSnapshot();
  const previous = lastObservedViewportSnapshot;
  lastObservedViewportSnapshot = next;

  if (!previous || !isMobileTerminalViewport(next.width)) {
    if (!isMobileTerminalViewport(next.width)) {
      setMobileVerticalStability(false);
    }
    return false;
  }

  const widthDelta = Math.abs(next.width - previous.width);
  const heightDelta = Math.abs(next.height - previous.height);
  const verticalOnly =
    widthDelta <= MOBILE_VERTICAL_WIDTH_TOLERANCE_PX &&
    heightDelta > MOBILE_VERTICAL_HEIGHT_TOLERANCE_PX;

  if (!verticalOnly) {
    if (widthDelta > MOBILE_VERTICAL_WIDTH_TOLERANCE_PX) {
      setMobileVerticalStability(false);
    }
    return false;
  }

  setMobileVerticalStability(true);
  return true;
}

export function setMobileVerticalStability(active: boolean): void {
  mobileVerticalStabilityActive = active;
  document.body.classList.toggle('mobile-terminal-vertical-stable', active);
  syncMobileVerticalStableTerminals();
}

export function syncMobileVerticalStableTerminals(): void {
  const active = mobileVerticalStabilityActive && isMobileTerminalViewport();
  sessionTerminals.forEach((state) => {
    const container = state.container;
    const dataset = (container as { dataset?: DOMStringMap }).dataset;
    const wasActive = container.classList.contains('mobile-terminal-vertical-stable');
    const wasScrollable = dataset?.mobileVerticalScrollable === 'true';
    const nearBottom =
      container.scrollHeight - container.scrollTop - container.clientHeight <=
      MOBILE_VERTICAL_BOTTOM_STICKY_PX;
    if (active) {
      container.classList.add('mobile-terminal-vertical-stable');
    } else {
      container.classList.remove('mobile-terminal-vertical-stable');
    }

    if (!active) {
      container.scrollTop = 0;
      if (dataset) {
        delete dataset.mobileVerticalScrollable;
        delete dataset.mobileCursorFollowing;
      }
      return;
    }

    const isScrollable =
      container.scrollHeight - container.clientHeight > MOBILE_VERTICAL_BOTTOM_STICKY_PX;
    if (dataset) {
      dataset.mobileVerticalScrollable = isScrollable ? 'true' : 'false';
      if (!wasActive || !wasScrollable) {
        dataset.mobileCursorFollowing = 'true';
      }
    }

    if (!wasActive || !wasScrollable || nearBottom) {
      revealMobileStableTerminalCursor(state, { force: true });
    }
  });
}

export function revealMobileStableTerminalCursor(
  state: Pick<TerminalState, 'container' | 'terminal'>,
  options: { force?: boolean } = {},
): void {
  if (!mobileVerticalStabilityActive || !isMobileTerminalViewport()) {
    return;
  }

  const container = state.container;
  if (!container.classList.contains('mobile-terminal-vertical-stable')) {
    return;
  }

  const dataset = (container as { dataset?: DOMStringMap }).dataset;
  if (options.force) {
    if (dataset) {
      dataset.mobileCursorFollowing = 'true';
    }
  } else if (dataset?.mobileCursorFollowing !== 'true') {
    return;
  }

  requestAnimationFrame(() => {
    scrollMobileStableTerminalCursorIntoView(state);
    requestAnimationFrame(() => {
      scrollMobileStableTerminalCursorIntoView(state);
    });
  });
}

function scrollMobileStableTerminalCursorIntoView(
  state: Pick<TerminalState, 'container' | 'terminal'>,
): void {
  const { container, terminal } = state;
  const screen = container.querySelector<HTMLElement>('.xterm-screen');
  const rows = Math.max(1, terminal.rows);
  const screenHeight = screen?.offsetHeight ?? 0;
  if (!screen || screenHeight <= 0 || container.clientHeight <= 0) {
    return;
  }

  const cursorRow = Math.max(0, Math.min(rows - 1, terminal.buffer.active.cursorY));
  const cellHeight = screenHeight / rows;
  const cursorTop = screen.offsetTop + cursorRow * cellHeight;
  const cursorBottom = cursorTop + cellHeight;
  const visibleTop = container.scrollTop;
  const visibleBottom = visibleTop + container.clientHeight;
  let nextScrollTop = visibleTop;

  if (cursorBottom + MOBILE_CURSOR_GUARD_PX > visibleBottom) {
    nextScrollTop = cursorBottom + MOBILE_CURSOR_GUARD_PX - container.clientHeight;
  } else if (cursorTop - MOBILE_CURSOR_GUARD_PX < visibleTop) {
    nextScrollTop = cursorTop - MOBILE_CURSOR_GUARD_PX;
  }

  const maxScrollTop = Math.max(0, container.scrollHeight - container.clientHeight);
  container.scrollTop = Math.max(0, Math.min(maxScrollTop, nextScrollTop));
}

export function shouldPreserveMobileTerminalRows(
  state: TerminalState,
  optimalCols: number,
): boolean {
  return (
    mobileVerticalStabilityActive &&
    isMobileTerminalViewport() &&
    state.terminal.cols === optimalCols &&
    state.terminal.rows > 0
  );
}

function readViewportSnapshot(): ViewportSnapshot {
  const vv = window.visualViewport;
  return {
    width: Math.round(vv?.width ?? window.innerWidth),
    height: Math.round(vv?.height ?? window.innerHeight),
  };
}

/**
 * Layout Renderer Module
 *
 * Recursively renders the layout tree to DOM elements.
 * Handles terminal container placement within layout panes.
 */

import type { LayoutNode, LayoutSplit, LayoutLeaf } from '../../types';
import { $layout, $focusedSessionId, $activeSessionId, hasTerminalSizeControl } from '../../stores';
import {
  dom,
  sessionTerminals,
  suppressLayoutAutoFit,
  setSuppressLayoutAutoFit,
} from '../../state';
import { isLayoutActive, focusLayoutSession } from './layoutStore';
import {
  applyTerminalScalingSync,
  fitTerminalToContainer,
  fitSessionToScreen,
} from '../terminal/scaling';
import { isSmartInputMode, showSmartInput } from '../smartInput';
import {
  ensureSessionWrapper,
  getActiveTab,
  getSessionWrapper,
  reparentTerminalContainer,
  switchTab,
} from '../sessionTabs';

let layoutRoot: HTMLElement | null = null;
let unsubscribeLayout: (() => void) | null = null;
let unsubscribeFocus: (() => void) | null = null;

/**
 * Initialize the layout renderer.
 * Sets up subscriptions to layout state changes.
 */
export function initLayoutRenderer(): void {
  // Create layout root container
  if (!layoutRoot && dom.terminalsArea) {
    layoutRoot = document.createElement('div');
    layoutRoot.className = 'layout-root hidden';
    dom.terminalsArea.appendChild(layoutRoot);
  }

  // Subscribe to layout changes
  unsubscribeLayout = $layout.subscribe((layout) => {
    renderLayout(layout.root);
  });

  // Subscribe to focus changes
  unsubscribeFocus = $focusedSessionId.subscribe((focusedId) => {
    updateFocusIndicator(focusedId);
  });
}

/**
 * Clean up layout renderer subscriptions.
 */
export function cleanupLayoutRenderer(): void {
  if (unsubscribeLayout) {
    unsubscribeLayout();
    unsubscribeLayout = null;
  }
  if (unsubscribeFocus) {
    unsubscribeFocus();
    unsubscribeFocus = null;
  }
}

/**
 * Render the layout tree to DOM.
 * Shows standalone terminal or layout root based on state.
 */
export function renderLayout(root: LayoutNode | null): void {
  if (!layoutRoot) return;

  // Clear existing layout DOM
  layoutRoot.innerHTML = '';

  if (!root) {
    // No layout - hide layout root, show standalone terminals
    layoutRoot.classList.add('hidden');
    showStandaloneTerminals();

    // Show the active standalone session
    const activeId = $activeSessionId.get();
    if (activeId) {
      const activeWrapper = getSessionWrapper(activeId);
      if (activeWrapper) {
        activeWrapper.classList.remove('hidden');
      }

      const state = sessionTerminals.get(activeId);
      if (state) {
        state.container.classList.remove('hidden');
        requestAnimationFrame(() => {
          if (hasTerminalSizeControl(activeId)) {
            fitSessionToScreen(activeId);
          } else {
            applyTerminalScalingSync(state);
          }
          if (state.opened) {
            state.terminal.focus();
          }
        });
      }
    }
    return;
  }

  // Layout active - build DOM tree
  layoutRoot.classList.remove('hidden');
  hideStandaloneTerminals();

  const rootElement = renderNode(root);
  if (rootElement) {
    layoutRoot.appendChild(rootElement);
  }

  // Move full session wrappers into their layout panes
  moveSessionWrappersToLayout();

  // Fit or scale terminals depending on context
  requestAnimationFrame(() => {
    if (suppressLayoutAutoFit) {
      setSuppressLayoutAutoFit(false);
    }
    fitTerminalsInLayout();
  });
}

/**
 * Recursively render a layout node to DOM.
 */
function renderNode(node: LayoutNode): HTMLElement | null {
  if (node.type === 'leaf') {
    return renderLeaf(node);
  }
  return renderSplit(node);
}

/**
 * Render a leaf node (terminal pane).
 */
function renderLeaf(leaf: LayoutLeaf): HTMLElement {
  const pane = document.createElement('div');
  pane.className = 'layout-leaf';
  pane.dataset.sessionId = leaf.sessionId;

  // Add click handler for focus
  pane.addEventListener('click', () => {
    focusLayoutSession(leaf.sessionId);
  });

  return pane;
}

/**
 * Render a split node (flex container).
 */
function renderSplit(split: LayoutSplit): HTMLElement {
  const container = document.createElement('div');
  container.className = `layout-split ${split.direction}`;

  for (const child of split.children) {
    const childElement = renderNode(child);
    if (childElement) {
      container.appendChild(childElement);
    }
  }

  return container;
}

/**
 * Move session wrappers from terminals-area into their layout panes.
 */
function moveSessionWrappersToLayout(): void {
  if (!layoutRoot || !dom.terminalsArea) return;

  const layoutSessionIds = new Set<string>();
  const panes = layoutRoot.querySelectorAll('.layout-leaf');
  panes.forEach((pane) => {
    const sessionId = (pane as HTMLElement).dataset.sessionId;
    if (!sessionId) return;
    layoutSessionIds.add(sessionId);

    ensureSessionWrapper(sessionId);
    const wrapper = getSessionWrapper(sessionId);
    if (!wrapper) return;

    const state = sessionTerminals.get(sessionId);
    if (state) {
      if (getActiveTab(sessionId) !== 'terminal') {
        switchTab(sessionId, 'terminal');
      }
      reparentTerminalContainer(sessionId, state.container);
      state.container.classList.remove('hidden');
    }

    wrapper.classList.remove('hidden');
    (pane as HTMLElement).replaceChildren(wrapper);
  });

  dom.terminalsArea.querySelectorAll<HTMLDivElement>('.session-wrapper').forEach((wrapper) => {
    const sessionId = wrapper.dataset.sessionId ?? '';
    if (!layoutSessionIds.has(sessionId)) {
      wrapper.classList.add('hidden');
    }
  });
}

/**
 * Show standalone terminals (when layout is inactive).
 */
function showStandaloneTerminals(): void {
  const area = dom.terminalsArea;
  if (!area) return;

  area.querySelectorAll<HTMLDivElement>('.session-wrapper').forEach((wrapper) => {
    if (wrapper.parentElement !== area) {
      area.appendChild(wrapper);
    }
  });

  // Move any terminals back to terminals-area from layout panes
  sessionTerminals.forEach((state, sessionId) => {
    const wrapper = getSessionWrapper(sessionId);
    if (wrapper) {
      if (wrapper.parentElement !== area) {
        area.appendChild(wrapper);
      }
      return;
    }

    if (state.container.parentElement !== area) {
      area.appendChild(state.container);
    }
  });
}

/**
 * Hide standalone terminals (layout is active).
 */
function hideStandaloneTerminals(): void {
  dom.terminalsArea?.querySelectorAll<HTMLDivElement>('.session-wrapper').forEach((wrapper) => {
    wrapper.classList.add('hidden');
  });

  sessionTerminals.forEach((state) => {
    if (!state.container.closest('.session-wrapper')) {
      state.container.classList.add('hidden');
    }
  });
}

/**
 * Fit all terminals in the layout to their pane sizes.
 * Resizes terminals (cols/rows) to fit panes and notifies server.
 */
function fitTerminalsInLayout(): void {
  if (!layoutRoot) return;

  const panes = layoutRoot.querySelectorAll('.layout-leaf');
  panes.forEach((pane) => {
    const paneEl = pane as HTMLElement;
    const sessionId = paneEl.dataset.sessionId;
    if (!sessionId) return;

    const state = sessionTerminals.get(sessionId);
    if (state?.opened && getActiveTab(sessionId) === 'terminal') {
      const terminalPanel = state.container.parentElement;
      if (!(terminalPanel instanceof HTMLElement)) {
        return;
      }

      // Resize terminal to fit pane dimensions
      fitTerminalToContainer(sessionId, terminalPanel);
    }
  });
}

/**
 * Apply CSS scaling only (no resize) for all terminals in layout.
 * Used when restoring layout from storage — terminals keep their server dimensions.
 */
/**
 * Update focus indicator on layout panes.
 */
function updateFocusIndicator(focusedId: string | null): void {
  if (!layoutRoot) return;

  // Remove focused class from all panes
  const panes = layoutRoot.querySelectorAll('.layout-leaf');
  panes.forEach((pane) => {
    pane.classList.remove('focused');
    const sessionId = (pane as HTMLElement).dataset.sessionId;
    if (sessionId === focusedId) {
      pane.classList.add('focused');

      if (isSmartInputMode()) {
        showSmartInput();
      } else {
        const state = sessionTerminals.get(sessionId);
        if (state?.terminal && state.opened) {
          state.terminal.focus();
        }
      }
    }
  });
}

/**
 * Get the layout root element.
 */
export function getLayoutRoot(): HTMLElement | null {
  return layoutRoot;
}

/**
 * Check if a point is within the layout area.
 */
export function isPointInLayoutArea(x: number, y: number): boolean {
  if (!dom.terminalsArea) return false;
  const rect = dom.terminalsArea.getBoundingClientRect();
  return x >= rect.left && x <= rect.right && y >= rect.top && y <= rect.bottom;
}

/**
 * Find the session ID at a point in the layout.
 * Returns null if no session found or layout not active.
 */
export function findSessionAtPoint(x: number, y: number): string | null {
  if (!layoutRoot || !isLayoutActive()) {
    return findStandaloneSessionAtPoint(x, y);
  }

  const panes = layoutRoot.querySelectorAll('.layout-leaf');
  for (const pane of panes) {
    if (isPointInRect(x, y, pane.getBoundingClientRect())) {
      return (pane as HTMLElement).dataset.sessionId ?? null;
    }
  }

  return null;
}

function findStandaloneSessionAtPoint(x: number, y: number): string | null {
  const wrappers = dom.terminalsArea?.querySelectorAll<HTMLElement>(
    '.session-wrapper:not(.hidden)',
  );
  if (wrappers) {
    for (const wrapper of wrappers) {
      const surface = getVisibleStandaloneSessionSurface(wrapper.dataset.sessionId ?? '');
      if (surface && isPointInRect(x, y, surface.getBoundingClientRect())) {
        return wrapper.dataset.sessionId ?? null;
      }
    }
  }

  const containers = dom.terminalsArea?.querySelectorAll<HTMLElement>(
    '.terminal-container:not(.hidden)',
  );
  if (!containers) {
    return null;
  }

  for (const container of containers) {
    if (isPointInRect(x, y, container.getBoundingClientRect())) {
      const id = container.id.replace('terminal-', '');
      return id || null;
    }
  }

  return null;
}

function isPointInRect(x: number, y: number, rect: DOMRect): boolean {
  return (
    hasRenderableArea(rect) &&
    x >= rect.left &&
    x <= rect.right &&
    y >= rect.top &&
    y <= rect.bottom
  );
}

function hasRenderableArea(rect: DOMRect): boolean {
  return rect.width > 0 && rect.height > 0;
}

function getRenderableRect(element: HTMLElement | null): DOMRect | null {
  const rect = element?.getBoundingClientRect();
  return rect && hasRenderableArea(rect) ? rect : null;
}

function getVisibleStandaloneSessionSurface(sessionId: string): HTMLElement | null {
  if (!sessionId) {
    return null;
  }

  const wrapper = getSessionWrapper(sessionId);
  if (wrapper && !wrapper.classList.contains('hidden')) {
    const activePanel = wrapper.querySelector<HTMLElement>('.session-tab-panel.active');
    if (getRenderableRect(activePanel)) {
      return activePanel;
    }

    if (getRenderableRect(wrapper)) {
      return wrapper;
    }
  }

  const container = document.getElementById(`terminal-${sessionId}`);
  if (container && !container.classList.contains('hidden') && getRenderableRect(container)) {
    return container;
  }

  return null;
}

/**
 * Get the bounding rect for a session's pane in the layout.
 */
export function getSessionPaneRect(sessionId: string): DOMRect | null {
  if (!layoutRoot || !isLayoutActive()) {
    return getRenderableRect(getVisibleStandaloneSessionSurface(sessionId));
  }

  const pane = layoutRoot.querySelector(`.layout-leaf[data-session-id="${sessionId}"]`);
  return getRenderableRect(pane instanceof HTMLElement ? pane : null);
}

/**
 * Web Preview Dock
 *
 * Dock/undock logic for the web preview panel.
 * Unlike commands/git docks, this panel COEXISTS with other docks.
 * The web preview dock sits as the outermost (rightmost) panel.
 */

import {
  $activeSessionId,
  $webPreviewDocked,
  $webPreviewViewport,
  $isMainBrowser,
} from '../../stores';
import { rescaleAllTerminalsImmediate, autoResizeAllTerminalsImmediate } from '../terminal/scaling';
import { setActionButtonActive } from '../sessionTabs';
import { hideIframe, restoreLastUrl, showIframe, unloadIframe } from './webPanel';
import { isDetachedOpenForSession } from './webDetach';
import { clearWebPreviewTarget } from './webApi';
import { createLogger } from '../logging';
import { getActiveMode, getActivePreviewName, setActiveMode } from './webSessionState';

const log = createLogger('webDock');

const DOCK_MIN_WIDTH = 250;
const DOCK_MAX_WIDTH_RATIO = 0.8;
const DOCK_WIDTH_KEY = 'mt-web-preview-dock-width';

function getDockMaxWidth(panel: HTMLElement): number {
  const availableWidth = panel.parentElement?.clientWidth ?? window.innerWidth;
  return Math.max(DOCK_MIN_WIDTH, Math.floor(availableWidth * DOCK_MAX_WIDTH_RATIO));
}

function clampDockWidth(width: number, panel: HTMLElement): number {
  return Math.max(DOCK_MIN_WIDTH, Math.min(getDockMaxWidth(panel), width));
}

function handleDockLayoutChange(): void {
  if ($isMainBrowser.get()) {
    autoResizeAllTerminalsImmediate();
  } else {
    requestAnimationFrame(rescaleAllTerminalsImmediate);
  }
}

/**
 * Get the current web preview dock width (0 if hidden).
 */
export function getWebPreviewDockWidth(): number {
  const dock = document.getElementById('web-preview-dock');
  if (dock && !dock.classList.contains('hidden')) return dock.offsetWidth;
  return 0;
}

/**
 * Adjust the CSS `right` position of inner docks (commands, git, file-viewer)
 * so they sit to the left of the web preview dock.
 */
export function adjustInnerDockPositions(): void {
  const wpWidth = getWebPreviewDockWidth();
  for (const id of ['git-dock', 'commands-dock', 'file-viewer-dock']) {
    const el = document.getElementById(id);
    if (el) el.style.right = wpWidth > 0 ? `${wpWidth}px` : '';
  }
}

/**
 * Recalculate the combined marginRight for all visible dock panels
 * on session-tab-panels elements.
 */
export function updateAllDockMargins(): void {
  let total = 0;
  for (const id of ['git-dock', 'commands-dock', 'file-viewer-dock', 'web-preview-dock']) {
    const el = document.getElementById(id);
    if (el && !el.classList.contains('hidden')) total += el.offsetWidth;
  }
  document
    .querySelectorAll<HTMLElement>('.session-tab-panels')
    .forEach((p) => (p.style.marginRight = total > 0 ? `${total}px` : ''));
  const footerDock = document.getElementById('adaptive-footer-dock');
  if (footerDock) {
    footerDock.style.right = total > 0 ? `${total}px` : '';
  }

  const managerQueue = document.getElementById('manager-bar-queue');
  if (managerQueue) {
    managerQueue.style.marginRight = total > 0 ? `${total}px` : '';
  }
}

/** Toggle the web preview dock panel open or closed based on current state. */
export function toggleWebPreviewDock(): void {
  const mode = getActiveMode();
  if (mode === 'docked') {
    closeWebPreviewDock();
  } else {
    openWebPreviewDock();
  }
}

/** Open the web preview dock panel, restore saved width, and show the iframe. */
export function openWebPreviewDock(): void {
  setActiveMode('docked');
  $webPreviewDocked.set(true);
  setActionButtonActive('web', true);

  const dockPanel = document.getElementById('web-preview-dock');
  if (!dockPanel) return;

  dockPanel.classList.remove('hidden');

  // Restore saved width
  const savedWidth = localStorage.getItem(DOCK_WIDTH_KEY);
  if (savedWidth) {
    const w = parseInt(savedWidth, 10);
    if (w >= DOCK_MIN_WIDTH) {
      dockPanel.style.width = `${clampDockWidth(w, dockPanel)}px`;
    }
  }

  showIframe();
  restoreLastUrl();

  // Adjust inner docks and margins for coexistence
  adjustInnerDockPositions();
  updateAllDockMargins();
  handleDockLayoutChange();

  log.info(() => 'Web preview dock opened');
}

/** Close the web preview dock panel and unload the iframe. */
export function closeWebPreviewDock(): void {
  const activeId = $activeSessionId.get();
  const activePreviewName = getActivePreviewName();
  const detachedActive = isDetachedOpenForSession(activeId, activePreviewName);
  if (!detachedActive) {
    setActiveMode('hidden');
  }
  $webPreviewDocked.set(false);
  setActionButtonActive('web', false);

  // Unload iframe to stop all network activity
  unloadIframe(activeId, activePreviewName);
  if (!detachedActive && activeId) {
    void clearWebPreviewTarget(activeId, activePreviewName);
  }
  const dockPanel = document.getElementById('web-preview-dock');
  if (dockPanel) {
    dockPanel.classList.add('hidden');
    dockPanel.style.width = '';
  }

  // Reset inner dock positions and margins
  adjustInnerDockPositions();
  updateAllDockMargins();
  handleDockLayoutChange();

  log.info(() => 'Web preview dock closed');
}

/** Suspend the web preview dock: hide the panel but keep iframe loaded and proxy target active. */
export function suspendWebPreviewDock(): void {
  $webPreviewDocked.set(false);
  setActionButtonActive('web', false);

  const dockPanel = document.getElementById('web-preview-dock');
  if (dockPanel) {
    dockPanel.classList.add('hidden');
    dockPanel.style.width = '';
  }

  adjustInnerDockPositions();
  updateAllDockMargins();
  handleDockLayoutChange();

  log.info(() => 'Web preview dock suspended (iframe kept alive)');
}

/** Hide the dock panel when detaching to a popup window, keeping the action button active. */
export function hideWebPreviewDockForDetach(): void {
  const activeId = $activeSessionId.get();
  const activePreviewName = getActivePreviewName();
  $webPreviewDocked.set(false);
  setActionButtonActive('web', true);
  unloadIframe(activeId, activePreviewName);

  const dockPanel = document.getElementById('web-preview-dock');
  if (dockPanel) {
    dockPanel.classList.add('hidden');
    dockPanel.style.width = '';
  }

  adjustInnerDockPositions();
  updateAllDockMargins();
  handleDockLayoutChange();
}

/** Fully hide the web preview: close dock, unload iframe, clear target, and clean up detach state. */
export function applyWebPreviewHiddenState(): void {
  const activeId = $activeSessionId.get();
  const activePreviewName = getActivePreviewName();
  $webPreviewDocked.set(false);
  setActionButtonActive('web', false);
  hideIframe();
  if (activeId) {
    void clearWebPreviewTarget(activeId, activePreviewName);
  }

  const dockPanel = document.getElementById('web-preview-dock');
  if (dockPanel) {
    dockPanel.classList.add('hidden');
    dockPanel.style.width = '';
  }

  adjustInnerDockPositions();
  updateAllDockMargins();
  handleDockLayoutChange();
}

/** Set up mouse and touch drag handlers for resizing the web preview dock panel. */
export function setupWebPreviewDockResize(): void {
  const dockPanel = document.getElementById('web-preview-dock');
  const gripEl = dockPanel?.querySelector('.web-preview-dock-resize-grip') as HTMLElement | null;
  if (!dockPanel || !gripEl) return;

  const panel = dockPanel;
  const grip = gripEl;
  let isResizing = false;
  let startX = 0;
  let startWidth = 0;

  const getIframes = (): HTMLIFrameElement[] =>
    Array.from(panel.querySelectorAll<HTMLIFrameElement>('.web-preview-iframe'));

  function beginResize(clientX: number): void {
    isResizing = true;
    startX = clientX;
    startWidth = panel.offsetWidth;
    grip.classList.add('active');
    document.body.style.cursor = 'ew-resize';
    document.body.style.userSelect = 'none';
    for (const iframe of getIframes()) {
      iframe.style.pointerEvents = 'none';
    }
  }

  function updateResize(clientX: number): void {
    if (!isResizing) return;
    const delta = startX - clientX;
    const newWidth = clampDockWidth(startWidth + delta, panel);
    panel.style.width = `${newWidth}px`;
  }

  function endResize(): void {
    if (!isResizing) return;
    isResizing = false;
    grip.classList.remove('active');
    document.body.style.cursor = '';
    document.body.style.userSelect = '';
    for (const iframe of getIframes()) {
      iframe.style.pointerEvents = '';
    }
    adjustInnerDockPositions();
    updateAllDockMargins();
    localStorage.setItem(DOCK_WIDTH_KEY, String(panel.offsetWidth));
    handleDockLayoutChange();
  }

  grip.addEventListener('mousedown', (e: MouseEvent) => {
    beginResize(e.clientX);
    e.preventDefault();
  });
  document.addEventListener('mousemove', (e: MouseEvent) => {
    updateResize(e.clientX);
  });
  document.addEventListener('mouseup', endResize);

  grip.addEventListener(
    'touchstart',
    (e: TouchEvent) => {
      const touch = e.touches[0];
      if (e.touches.length !== 1 || !touch) return;
      beginResize(touch.clientX);
      e.preventDefault();
    },
    { passive: false },
  );

  document.addEventListener(
    'touchmove',
    (e: TouchEvent) => {
      const touch = e.touches[0];
      if (!isResizing || e.touches.length !== 1 || !touch) return;
      updateResize(touch.clientX);
      e.preventDefault();
    },
    { passive: false },
  );

  document.addEventListener('touchend', endResize);
  document.addEventListener('touchcancel', endResize);
}

/**
 * Set the web preview iframe to a fixed viewport size for responsive testing.
 * Pass width=0, height=0 to reset to full size.
 */
export function setViewportSize(width: number, height: number): void {
  const iframe = document.querySelector<HTMLIFrameElement>('.web-preview-iframe:not(.hidden)');
  const body = document.querySelector<HTMLElement>('.web-preview-dock-body');
  const badge = document.getElementById('web-preview-viewport-badge');
  if (!iframe || !body) return;

  if (width <= 0 && height <= 0) {
    iframe.style.width = '';
    iframe.style.height = '';
    iframe.style.maxWidth = '';
    iframe.style.maxHeight = '';
    iframe.style.left = '';
    iframe.style.top = '';
    iframe.style.transform = '';
    body.classList.remove('viewport-constrained');
    $webPreviewViewport.set(null);
    if (badge) badge.classList.add('hidden');
    log.info(() => 'Viewport reset to full size');
    return;
  }

  iframe.style.width = `${width}px`;
  iframe.style.height = `${height}px`;
  iframe.style.maxWidth = `${width}px`;
  iframe.style.maxHeight = `${height}px`;
  iframe.style.left = '50%';
  iframe.style.top = '50%';
  iframe.style.transform = 'translate(-50%, -50%)';
  body.classList.add('viewport-constrained');
  $webPreviewViewport.set({ width, height });

  if (badge) {
    badge.textContent = `${width}\u00D7${height}`;
    badge.classList.remove('hidden');
  }

  log.info(() => `Viewport set to ${width}\u00D7${height}`);
}

/** Wire up the viewport reset badge click handler. */
export function initViewportReset(): void {
  const badge = document.getElementById('web-preview-viewport-badge');
  if (badge) {
    badge.addEventListener('click', () => {
      setViewportSize(0, 0);
    });
  }
}

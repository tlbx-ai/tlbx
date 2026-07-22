/**
 * Sidebar Collapse Module
 *
 * Handles sidebar visibility, collapse/expand state,
 * and island title updates for desktop view.
 */

import { dom } from '../../state';
import { $sidebarOpen, $sidebarCollapsed } from '../../stores';
import { getCookie, setCookie } from '../../utils';
import { updateMobileTitle } from './sessionList';
import { autoResizeAllTerminalsImmediate } from '../terminal/scaling';

// =============================================================================
// Cookie Constants
// =============================================================================

const SIDEBAR_COLLAPSED_COOKIE = 'mm-sidebar-collapsed';
const SIDEBAR_WIDTH_COOKIE = 'mm-sidebar-width';
const DESKTOP_BREAKPOINT = 768;
const MIN_SIDEBAR_WIDTH = 180;
const MAX_SIDEBAR_WIDTH = 400;

// =============================================================================
// Mobile Sidebar Toggle
// =============================================================================

/**
 * Toggle mobile sidebar visibility
 */
export function toggleSidebar(): void {
  const isOpen = !$sidebarOpen.get();
  $sidebarOpen.set(isOpen);
  if (dom.app) dom.app.classList.toggle('sidebar-open', isOpen);
}

/**
 * Close mobile sidebar
 */
export function closeSidebar(): void {
  $sidebarOpen.set(false);
  if (dom.app) dom.app.classList.remove('sidebar-open');
}

// =============================================================================
// Desktop Sidebar Collapse
// =============================================================================

/**
 * Collapse sidebar to icon-only mode (desktop)
 */
export function collapseSidebar(): void {
  $sidebarCollapsed.set(true);
  if (dom.app) dom.app.classList.add('sidebar-collapsed');
  setCookie(SIDEBAR_COLLAPSED_COOKIE, 'true');
  updateMobileTitle();
  requestAnimationFrame(autoResizeAllTerminalsImmediate);
}

/**
 * Expand sidebar to full width (desktop)
 */
export function expandSidebar(): void {
  $sidebarCollapsed.set(false);
  if (dom.app) dom.app.classList.remove('sidebar-collapsed');
  setCookie(SIDEBAR_COLLAPSED_COOKIE, 'false');
  requestAnimationFrame(autoResizeAllTerminalsImmediate);
}

// =============================================================================
// State Restoration
// =============================================================================

/**
 * Restore sidebar collapsed state from cookie (desktop only)
 */
export function restoreSidebarState(): void {
  if (getCookie(SIDEBAR_COLLAPSED_COOKIE) === 'true' && window.innerWidth > DESKTOP_BREAKPOINT) {
    $sidebarCollapsed.set(true);
    if (dom.app) dom.app.classList.add('sidebar-collapsed');
  }

  // Restore sidebar width
  const savedWidth = getCookie(SIDEBAR_WIDTH_COOKIE);
  if (savedWidth) {
    const width = parseInt(savedWidth, 10);
    if (width >= MIN_SIDEBAR_WIDTH && width <= MAX_SIDEBAR_WIDTH) {
      const sidebar = document.getElementById('sidebar');
      if (sidebar) {
        sidebar.style.width = `${width}px`;
      }
    }
  }
}

// =============================================================================
// Sidebar Resize
// =============================================================================

/**
 * Set up sidebar resize grip functionality (mouse + touch)
 */
export function setupSidebarResize(): void {
  const g = document.getElementById('sidebar-resize-grip');
  const s = document.getElementById('sidebar');
  if (!g || !s) return;
  const gripEl: HTMLElement = g;
  const sidebarEl: HTMLElement = s;

  let isResizing = false;
  let startX = 0;
  let startWidth = 0;

  function beginResize(clientX: number): void {
    isResizing = true;
    startX = clientX;
    startWidth = sidebarEl.offsetWidth;
    gripEl.classList.add('active');
    document.body.style.cursor = 'ew-resize';
    document.body.style.userSelect = 'none';
  }

  function updateResize(clientX: number): void {
    if (!isResizing) return;
    const delta = clientX - startX;
    const newWidth = Math.max(MIN_SIDEBAR_WIDTH, Math.min(MAX_SIDEBAR_WIDTH, startWidth + delta));
    sidebarEl.style.width = `${newWidth}px`;
  }

  function endResize(): void {
    if (!isResizing) return;
    isResizing = false;
    gripEl.classList.remove('active');
    document.body.style.cursor = '';
    document.body.style.userSelect = '';
    setCookie(SIDEBAR_WIDTH_COOKIE, String(sidebarEl.offsetWidth));
    requestAnimationFrame(autoResizeAllTerminalsImmediate);
  }

  // Mouse events
  gripEl.addEventListener('mousedown', (e: MouseEvent) => {
    beginResize(e.clientX);
    e.preventDefault();
  });
  document.addEventListener('mousemove', (e: MouseEvent) => {
    updateResize(e.clientX);
  });
  document.addEventListener('mouseup', endResize);

  // Touch events
  gripEl.addEventListener(
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

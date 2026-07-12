/**
 * Session Drag-and-Drop Module
 *
 * Handles drag-and-drop reordering of sidebar session items
 * and docking sessions into multi-panel layouts.
 */

import { dom, sessionTerminals } from '../../state';
import { reorderSessions, $sessionList, $activeSessionId } from '../../stores';
import { persistSessionOrder } from '../comms';
// The live filter state is owned by the active sidebar renderer; sessionList's
// filter controller instance is never initialized.
import { isSessionFilterActive } from './spacesTreeSidebar';
import {
  showDockOverlay,
  hideDockOverlay,
  getDockTarget,
  isDockOverlayVisible,
} from '../layout/dockOverlay';
import { dockSession, isLayoutActive, isSessionInLayout } from '../layout/layoutStore';
import { getLayoutRoot } from '../layout/layoutRenderer';

let draggedSessionId: string | null = null;
let draggedElement: HTMLElement | null = null;
let dropIndicatorPosition: 'above' | 'below' | null = null;
let dragImageElement: HTMLElement | null = null;
let layoutShownForDrag = false;

// Touch drag state
const TOUCH_DRAG_DELAY_MS = 200;
let touchDragTimer: ReturnType<typeof setTimeout> | null = null;
let touchGhost: HTMLElement | null = null;
let touchStartY = 0;
let touchActive = false;

/**
 * Check if a session drag is currently in progress.
 * Used by fileDrop to avoid showing file upload indicator during session docking.
 */
export function isSessionDragActive(): boolean {
  return draggedSessionId !== null;
}

function closestSessionItem(el: Element | null): HTMLElement | null {
  const found = el?.closest('.session-item');
  return found instanceof HTMLElement ? found : null;
}

function isInteractiveDragTarget(el: Element | null): boolean {
  return (
    el?.closest(
      'button, input, textarea, select, a, [contenteditable="true"], .session-actions, .session-notes-pane',
    ) != null
  );
}

function isSameControlMode(target: HTMLElement | null): boolean {
  if (!target || !draggedElement) {
    return false;
  }

  return target.dataset.controlMode === draggedElement.dataset.controlMode;
}

function isSameReorderScope(target: HTMLElement | null): boolean {
  if (!target || !draggedElement) {
    return false;
  }

  return (target.dataset.reorderScope ?? '') === (draggedElement.dataset.reorderScope ?? '');
}

function getReorderScope(item: HTMLElement | null): string {
  return item?.dataset.reorderScope ?? '';
}

function canReorderWithTarget(target: HTMLElement | null): target is HTMLElement {
  const draggedReorderScope = getReorderScope(draggedElement);
  return (
    draggedReorderScope !== '' &&
    !!target &&
    target !== draggedElement &&
    isSameControlMode(target) &&
    isSameReorderScope(target)
  );
}

// Track elements with active drop indicators (avoids full DOM scan)
const activeIndicators = new Set<HTMLElement>();

function showLayoutForDragPreview(): void {
  if (!isLayoutActive()) {
    return;
  }

  const layoutRoot = getLayoutRoot();
  if (!layoutRoot?.classList.contains('hidden')) {
    return;
  }

  layoutRoot.classList.remove('hidden');
  sessionTerminals.forEach((state, sessionId) => {
    if (!isSessionInLayout(sessionId)) {
      state.container.classList.add('hidden');
    }
  });
  layoutShownForDrag = true;
}

/**
 * Initialize drag-and-drop for the session list
 */
export function initSessionDrag(): void {
  const sessionList = dom.sessionList;
  if (!sessionList) return;

  sessionList.addEventListener('dragstart', handleDragStart);
  sessionList.addEventListener('dragend', handleDragEnd);
  sessionList.addEventListener('dragover', handleDragOver);
  sessionList.addEventListener('dragleave', handleDragLeave);
  sessionList.addEventListener('drop', handleDrop);

  // Touch-based drag for iOS/Android (HTML5 DnD has no touch support)
  sessionList.addEventListener('touchstart', handleTouchStart, { passive: false });
  sessionList.addEventListener('touchmove', handleTouchMove, { passive: false });
  sessionList.addEventListener('touchend', handleTouchEnd);
  sessionList.addEventListener('touchcancel', handleTouchEnd);

  // Global listeners for dock-to-layout (terminals area)
  document.addEventListener('dragover', handleGlobalDragOver);
  document.addEventListener('drop', handleGlobalDrop);
}

function handleDragStart(e: DragEvent): void {
  if (isSessionFilterActive()) {
    e.preventDefault();
    return;
  }

  const target = e.target as HTMLElement;
  if (isInteractiveDragTarget(target)) {
    e.preventDefault();
    return;
  }

  const sessionItem = closestSessionItem(target);
  if (!sessionItem) return;

  draggedSessionId = sessionItem.dataset.sessionId ?? null;
  draggedElement = sessionItem;

  sessionItem.classList.add('dragging');

  if (e.dataTransfer) {
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData('text/plain', draggedSessionId ?? '');

    // Create a custom drag image (clone of the item)
    const dragImage = sessionItem.cloneNode(true) as HTMLElement;
    dragImage.style.position = 'absolute';
    dragImage.style.top = '-1000px';
    dragImage.style.opacity = '0.9';
    dragImage.style.transform = 'scale(0.95)';
    dragImage.style.width = `${sessionItem.offsetWidth}px`;
    dragImage.classList.remove('dragging');
    document.body.appendChild(dragImage);
    e.dataTransfer.setDragImage(dragImage, 20, sessionItem.offsetHeight / 2);

    // Keep drag image until drag ends - some browsers need it to persist
    dragImageElement = dragImage;
  }
}

function handleDragEnd(_e: DragEvent): void {
  if (draggedElement) {
    draggedElement.classList.remove('dragging');
  }

  // Clean up drag image
  if (dragImageElement) {
    dragImageElement.remove();
    dragImageElement = null;
  }

  clearAllDropIndicators();
  hideDockOverlay();

  // Restore standalone view if layout was only shown for drag preview
  if (layoutShownForDrag) {
    const activeId = $activeSessionId.get();
    if (activeId && !isSessionInLayout(activeId)) {
      getLayoutRoot()?.classList.add('hidden');
      const activeState = sessionTerminals.get(activeId);
      if (activeState) activeState.container.classList.remove('hidden');
    }
    layoutShownForDrag = false;
  }

  draggedSessionId = null;
  draggedElement = null;
  dropIndicatorPosition = null;
}

function handleDragOver(e: DragEvent): void {
  e.preventDefault();

  if (!draggedSessionId) return;

  // When layout is active, sidebar drag should be dock-only (no reorder semantics).
  if (isLayoutActive()) {
    const target = e.target as HTMLElement;
    const sessionItem = closestSessionItem(target);
    const hoveredSessionId = sessionItem?.dataset.sessionId;
    if (hoveredSessionId && isSessionInLayout(hoveredSessionId)) {
      showLayoutForDragPreview();
    }
    clearAllDropIndicators();
    return;
  }

  const target = e.target as HTMLElement;
  const sessionItem = closestSessionItem(target);

  if (!canReorderWithTarget(sessionItem)) {
    clearAllDropIndicators();
    return;
  }

  if (e.dataTransfer) {
    e.dataTransfer.dropEffect = 'move';
  }

  const rect = sessionItem.getBoundingClientRect();
  const midY = rect.top + rect.height / 2;
  const isAbove = e.clientY < midY;

  clearAllDropIndicators();

  sessionItem.classList.add('drag-over');
  activeIndicators.add(sessionItem);
  if (isAbove) {
    sessionItem.classList.add('drag-over-above');
    dropIndicatorPosition = 'above';
  } else {
    sessionItem.classList.add('drag-over-below');
    dropIndicatorPosition = 'below';
  }

  // If hovering over a session in a layout, show the layout for dock targeting
  const hoveredSessionId = sessionItem.dataset.sessionId;
  if (hoveredSessionId && isSessionInLayout(hoveredSessionId)) {
    const layoutRoot = getLayoutRoot();
    if (layoutRoot?.classList.contains('hidden')) {
      layoutRoot.classList.remove('hidden');
      sessionTerminals.forEach((s, id) => {
        if (!isSessionInLayout(id)) s.container.classList.add('hidden');
      });
      layoutShownForDrag = true;
    }
  }
}

function handleDragLeave(e: DragEvent): void {
  const target = e.target as HTMLElement;
  const sessionItem = closestSessionItem(target);

  if (sessionItem) {
    const relatedTarget = e.relatedTarget as HTMLElement | null;
    if (!relatedTarget || !sessionItem.contains(relatedTarget)) {
      sessionItem.classList.remove('drag-over', 'drag-over-above', 'drag-over-below');
      activeIndicators.delete(sessionItem);
    }
  }
}

function handleDrop(e: DragEvent): void {
  e.preventDefault();

  if (!draggedSessionId) return;

  if (isLayoutActive()) {
    clearAllDropIndicators();
    return;
  }

  const target = e.target as HTMLElement;
  const targetItem = closestSessionItem(target);

  if (!canReorderWithTarget(targetItem)) return;

  const targetSessionId = targetItem.dataset.sessionId;
  if (!targetSessionId) return;

  const sessions = $sessionList.get();
  const fromIndex = sessions.findIndex((s) => s.id === draggedSessionId);
  let toIndex = sessions.findIndex((s) => s.id === targetSessionId);

  if (fromIndex === -1 || toIndex === -1) return;

  // Adjust toIndex based on drop position
  if (dropIndicatorPosition === 'below') {
    toIndex = fromIndex < toIndex ? toIndex : toIndex + 1;
  } else {
    toIndex = fromIndex > toIndex ? toIndex : toIndex - 1;
  }

  // Clamp to valid range
  toIndex = Math.max(0, Math.min(sessions.length - 1, toIndex));

  reorderSessions(fromIndex, toIndex);
  clearAllDropIndicators();

  // Persist new order to server
  const newOrder = $sessionList
    .get()
    .map((s) => s.id)
    .filter((id): id is string => !!id);
  persistSessionOrder(newOrder);
}

function handleTouchStart(e: TouchEvent): void {
  if (isSessionFilterActive()) {
    return;
  }

  const touch = e.touches[0];
  if (!touch) return;

  const target = touch.target as HTMLElement;
  if (isInteractiveDragTarget(target)) {
    return;
  }

  const sessionItem = closestSessionItem(target);
  if (!sessionItem) return;

  touchStartY = touch.clientY;

  touchDragTimer = setTimeout(() => {
    draggedSessionId = sessionItem.dataset.sessionId ?? null;
    draggedElement = sessionItem;
    touchActive = true;
    sessionItem.classList.add('dragging');

    touchGhost = sessionItem.cloneNode(true) as HTMLElement;
    touchGhost.style.position = 'fixed';
    touchGhost.style.left = '0';
    touchGhost.style.top = `${touch.clientY - sessionItem.offsetHeight / 2}px`;
    touchGhost.style.width = `${sessionItem.offsetWidth}px`;
    touchGhost.style.opacity = '0.85';
    touchGhost.style.pointerEvents = 'none';
    touchGhost.style.zIndex = '9999';
    touchGhost.style.transform = 'scale(0.95)';
    touchGhost.classList.remove('dragging');
    document.body.appendChild(touchGhost);
  }, TOUCH_DRAG_DELAY_MS);
}

function handleTouchMove(e: TouchEvent): void {
  const touch = e.touches[0];
  if (!touch) return;

  if (!touchActive) {
    if (Math.abs(touch.clientY - touchStartY) > 10) {
      cancelTouchDrag();
    }
    return;
  }

  e.preventDefault();

  if (touchGhost) {
    touchGhost.style.top = `${touch.clientY - (draggedElement?.offsetHeight ?? 40) / 2}px`;
  }

  if (touchGhost) touchGhost.style.display = 'none';
  const el = document.elementFromPoint(touch.clientX, touch.clientY) as HTMLElement | null;
  if (touchGhost) touchGhost.style.display = '';
  if (!el) return;

  if (isLayoutActive()) {
    clearAllDropIndicators();
    return;
  }

  const sessionItem = closestSessionItem(el);
  clearAllDropIndicators();

  if (canReorderWithTarget(sessionItem)) {
    const rect = sessionItem.getBoundingClientRect();
    const midY = rect.top + rect.height / 2;
    const isAbove = touch.clientY < midY;

    sessionItem.classList.add('drag-over');
    activeIndicators.add(sessionItem);
    if (isAbove) {
      sessionItem.classList.add('drag-over-above');
      dropIndicatorPosition = 'above';
    } else {
      sessionItem.classList.add('drag-over-below');
      dropIndicatorPosition = 'below';
    }
  }
}

function handleTouchEnd(e: TouchEvent): void {
  cancelTouchDrag();

  if (!touchActive || !draggedSessionId) {
    touchActive = false;
    return;
  }

  const touch = e.changedTouches[0];
  if (touch) {
    if (touchGhost) touchGhost.style.display = 'none';
    const el = document.elementFromPoint(touch.clientX, touch.clientY) as HTMLElement | null;
    if (touchGhost) touchGhost.style.display = '';
    finishTouchSessionReorder(closestSessionItem(el));
  }

  if (draggedElement) draggedElement.classList.remove('dragging');
  if (touchGhost) {
    touchGhost.remove();
    touchGhost = null;
  }
  clearAllDropIndicators();
  draggedSessionId = null;
  draggedElement = null;
  dropIndicatorPosition = null;
  touchActive = false;
}

function finishTouchSessionReorder(targetItem: HTMLElement | null): void {
  if (isLayoutActive() || !canReorderWithTarget(targetItem)) {
    return;
  }

  const targetSessionId = targetItem.dataset.sessionId;
  if (!targetSessionId || !draggedSessionId) {
    return;
  }

  const sessions = $sessionList.get();
  const fromIndex = sessions.findIndex((session) => session.id === draggedSessionId);
  const targetIndex = sessions.findIndex((session) => session.id === targetSessionId);
  const nextIndex = resolveTouchDropIndex(fromIndex, targetIndex);
  if (fromIndex === -1 || nextIndex === null) {
    return;
  }

  reorderSessions(fromIndex, nextIndex);
  const newOrder = $sessionList
    .get()
    .map((session) => session.id)
    .filter((id): id is string => !!id);
  persistSessionOrder(newOrder);
}

function resolveTouchDropIndex(fromIndex: number, targetIndex: number): number | null {
  if (fromIndex === -1 || targetIndex === -1) {
    return null;
  }

  const nextIndex =
    dropIndicatorPosition === 'below'
      ? fromIndex < targetIndex
        ? targetIndex
        : targetIndex + 1
      : fromIndex > targetIndex
        ? targetIndex
        : targetIndex - 1;
  const sessions = $sessionList.get();
  return Math.max(0, Math.min(sessions.length - 1, nextIndex));
}

function cancelTouchDrag(): void {
  if (touchDragTimer) {
    clearTimeout(touchDragTimer);
    touchDragTimer = null;
  }
}

function clearAllDropIndicators(): void {
  activeIndicators.forEach((item) => {
    item.classList.remove('drag-over', 'drag-over-above', 'drag-over-below');
  });
  activeIndicators.clear();
}

/**
 * Check if a point is over the terminals area (not sidebar).
 */
function isOverTerminalsArea(x: number, y: number): boolean {
  if (!dom.terminalsArea) return false;
  const rect = dom.terminalsArea.getBoundingClientRect();
  return x >= rect.left && x <= rect.right && y >= rect.top && y <= rect.bottom;
}

/**
 * Handle global dragover for dock-to-layout.
 */
function handleGlobalDragOver(e: DragEvent): void {
  if (!draggedSessionId) return;

  // Check if over terminals area
  if (isOverTerminalsArea(e.clientX, e.clientY)) {
    e.preventDefault();
    if (e.dataTransfer) {
      e.dataTransfer.dropEffect = 'move';
    }
    showLayoutForDragPreview();
    // Show dock overlay
    showDockOverlay(e.clientX, e.clientY, draggedSessionId);
  } else if (isDockOverlayVisible()) {
    // Hide dock overlay when leaving terminals area
    hideDockOverlay();
  }
}

/**
 * Handle global drop for dock-to-layout.
 */
function handleGlobalDrop(e: DragEvent): void {
  if (!draggedSessionId) return;

  // Check if drop is on dock overlay
  const dockTarget = getDockTarget();
  if (dockTarget && isOverTerminalsArea(e.clientX, e.clientY)) {
    e.preventDefault();
    e.stopPropagation();

    dockSession(dockTarget.targetSessionId, draggedSessionId, dockTarget.position);
    hideDockOverlay();
    layoutShownForDrag = false;
  }
}

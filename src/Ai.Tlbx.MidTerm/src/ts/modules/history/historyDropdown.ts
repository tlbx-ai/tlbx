/**
 * History Dropdown Module
 *
 * UI component for displaying and interacting with command history.
 * Uses backend API for persistence. Pinned entries support drag-and-drop reordering.
 */

import {
  fetchHistory,
  removeHistoryEntry,
  reorderHistory,
  renameHistoryEntry,
  type LaunchEntry,
} from './historyApi';
import { icon } from '../../constants';
import { t } from '../i18n';
import { createLogger } from '../logging';
import { formatRuntimeDisplay } from '../sidebar/processDisplay';
import { closeSpacesDropdown } from '../spaces';
import { formatHistoryDirectoryDisplay } from './historyPathDisplay';
import { getHistoryModeBadgeText, getHistoryModeDisplayText } from './launchMode';

const log = createLogger('history-dropdown');

let dropdownEl: HTMLElement | null = null;
let isOpen = false;
let cachedEntries: LaunchEntry[] = [];
let onSpawnSession: ((entry: LaunchEntry) => void) | null = null;
let onRenameEntry: ((entryId: string, newLabel: string) => void) | null = null;

// Drag state
let draggedId: string | null = null;
let draggedElement: HTMLElement | null = null;
let dropPosition: 'above' | 'below' | null = null;
const activeIndicators = new Set<HTMLElement>();

// Touch drag state
const TOUCH_DRAG_DELAY_MS = 200;
let touchDragTimer: ReturnType<typeof setTimeout> | null = null;
let touchGhost: HTMLElement | null = null;
let touchStartY = 0;
let touchActive = false;

/**
 * Initialize the history dropdown.
 */
export function initHistoryDropdown(
  spawnCallback: (entry: LaunchEntry) => void,
  renameCallback?: (entryId: string, newLabel: string) => void,
): void {
  onSpawnSession = spawnCallback;
  onRenameEntry = renameCallback ?? null;
  createDropdownElement();
  document.getElementById('btn-bookmarks')?.addEventListener('click', () => {
    closeSpacesDropdown();
    toggleHistoryDropdown();
  });
  void loadHistory();
  window.addEventListener('resize', handleViewportChange);
  window.addEventListener('orientationchange', handleViewportChange);
}

/**
 * Refresh history from backend.
 */
export function refreshHistory(): void {
  void loadHistory().then(() => {
    if (isOpen) {
      positionDropdown();
      renderDropdownContent();
    }
  });
}

/**
 * Toggle the history dropdown visibility.
 */
export function toggleHistoryDropdown(): void {
  if (isOpen) {
    closeHistoryDropdown();
  } else {
    openHistoryDropdown();
  }
}

/**
 * Open the history dropdown.
 */
export function openHistoryDropdown(): void {
  if (!dropdownEl) return;

  void loadHistory().then(() => {
    positionDropdown();
    renderDropdownContent();
    dropdownEl?.classList.add('visible');
    isOpen = true;

    setTimeout(() => {
      document.addEventListener('click', handleOutsideClick);
    }, 0);
  });
}

/**
 * Close the history dropdown.
 */
export function closeHistoryDropdown(): void {
  if (!dropdownEl) return;

  dropdownEl.classList.remove('visible');
  isOpen = false;
  document.removeEventListener('click', handleOutsideClick);
}

async function loadHistory(): Promise<void> {
  try {
    cachedEntries = await fetchHistory();
  } catch (e) {
    log.warn(() => `Failed to load history: ${String(e)}`);
    cachedEntries = [];
  }
}

function createDropdownElement(): void {
  dropdownEl = document.createElement('div');
  dropdownEl.className = 'history-dropdown';
  dropdownEl.innerHTML = `
    <div class="history-dropdown-header">${t('sidebar.loadBookmarked')}</div>
    <div class="history-dropdown-content"></div>
    <div class="history-dropdown-empty">${t('history.noHistory')}</div>
  `;

  const sidebar = document.getElementById('sidebar');
  if (sidebar) {
    sidebar.appendChild(dropdownEl);
  }
}

function positionDropdown(): void {
  if (!dropdownEl) return;

  const trigger = document.getElementById('btn-bookmarks');
  const sidebar = document.getElementById('sidebar');
  if (!(trigger instanceof HTMLElement) || !(sidebar instanceof HTMLElement)) {
    return;
  }

  const triggerRect = trigger.getBoundingClientRect();
  const sidebarRect = sidebar.getBoundingClientRect();
  const top = Math.round(triggerRect.bottom - sidebarRect.top + 4);
  const availableHeight = Math.max(160, Math.floor(sidebarRect.bottom - triggerRect.bottom - 12));

  dropdownEl.style.top = `${top}px`;
  dropdownEl.style.left = '8px';
  dropdownEl.style.right = '8px';
  dropdownEl.style.maxHeight = `${availableHeight}px`;
}

function renderDropdownContent(): void {
  if (!dropdownEl) return;

  const content = dropdownEl.querySelector('.history-dropdown-content');
  const empty = dropdownEl.querySelector('.history-dropdown-empty');
  if (!content || !empty) return;

  const adHocEntries = cachedEntries.filter(
    (entry) => (entry.launchOrigin ?? '').toLowerCase() !== 'space',
  );
  const pinnedEntries = adHocEntries.filter((e) => e.isStarred);
  const recentEntries = adHocEntries.filter((e) => !e.isStarred).slice(0, 5);

  if (pinnedEntries.length === 0 && recentEntries.length === 0) {
    content.classList.add('hidden');
    empty.classList.remove('hidden');
    return;
  }

  content.classList.remove('hidden');
  empty.classList.add('hidden');
  content.innerHTML = '';

  if (pinnedEntries.length > 0) {
    const pinnedHeader = document.createElement('div');
    pinnedHeader.className = 'history-section-header';
    pinnedHeader.textContent = '\u2b50 ' + t('history.pinned');
    content.appendChild(pinnedHeader);

    const pinnedContainer = document.createElement('div');
    pinnedContainer.className = 'history-entry-list history-pinned-list';
    pinnedEntries.forEach((entry) => {
      pinnedContainer.appendChild(createHistoryItem(entry, true));
    });
    content.appendChild(pinnedContainer);

    initDragHandlers(pinnedContainer);
  }

  if (recentEntries.length > 0) {
    const recentHeader = document.createElement('div');
    recentHeader.className = 'history-section-header';
    recentHeader.textContent = '\ud83d\udd70 ' + t('history.recent');
    content.appendChild(recentHeader);

    const recentContainer = document.createElement('div');
    recentContainer.className = 'history-entry-list';
    recentEntries.forEach((entry) => {
      recentContainer.appendChild(createHistoryItem(entry, false));
    });
    content.appendChild(recentContainer);
  }
}

function createHistoryItem(entry: LaunchEntry, isPinned: boolean): HTMLDivElement {
  const item = document.createElement('div');
  item.className = 'history-item';
  item.dataset.id = entry.id;

  if (isPinned) {
    item.draggable = true;
  }

  const infoDiv = document.createElement('div');
  infoDiv.className = 'history-item-info';

  const primaryRow = document.createElement('div');
  primaryRow.className = 'history-item-primary';

  const modeSpan = document.createElement('span');
  modeSpan.className = 'history-item-mode';
  modeSpan.textContent = getHistoryModeBadgeText(entry);
  modeSpan.title = getHistoryModeDisplayText(entry);
  primaryRow.appendChild(modeSpan);

  if (entry.label) {
    const labelSpan = document.createElement('span');
    labelSpan.className = 'history-item-label';
    labelSpan.textContent = entry.label;
    primaryRow.appendChild(labelSpan);
  }
  infoDiv.appendChild(primaryRow);

  const runtime = getHistoryRuntimeSummary(entry);
  const secondaryRow = document.createElement('div');
  secondaryRow.className = 'history-item-secondary';
  const fgIndicator = createForegroundIndicator(
    runtime.cwd,
    runtime.commandLine,
    runtime.processName,
  );
  secondaryRow.appendChild(fgIndicator);
  infoDiv.appendChild(secondaryRow);

  item.appendChild(infoDiv);

  const actionsDiv = document.createElement('div');
  actionsDiv.className = 'history-item-actions';

  const renameBtn = document.createElement('button');
  renameBtn.className = 'history-item-rename';
  renameBtn.title = t('sidebar.rename');
  renameBtn.innerHTML = icon('rename');
  renameBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    startHistoryInlineRename(item, entry);
  });
  actionsDiv.appendChild(renameBtn);

  const deleteBtn = document.createElement('button');
  deleteBtn.className = 'history-item-delete';
  deleteBtn.title = t('history.remove');
  deleteBtn.innerHTML = icon('close');
  deleteBtn.addEventListener('click', (e) => {
    e.stopPropagation();
    if (!entry.id) return;
    void (async () => {
      await removeHistoryEntry(entry.id);
      await loadHistory();
      renderDropdownContent();
    })();
  });
  actionsDiv.appendChild(deleteBtn);
  item.appendChild(actionsDiv);

  item.addEventListener('click', (e) => {
    const target = e.target as Element;
    if (target.closest('.history-item-delete') || target.closest('.history-item-rename')) {
      return;
    }
    if (onSpawnSession) {
      closeHistoryDropdown();
      onSpawnSession(entry);
    }
  });

  return item;
}

function startHistoryInlineRename(item: HTMLElement, entry: LaunchEntry): void {
  const infoEl = item.querySelector('.history-item-info');
  if (!infoEl) return;

  const info = infoEl;
  const currentLabel = entry.label || '';

  const input = document.createElement('input');
  input.type = 'text';
  input.className = 'history-rename-input';
  input.value = currentLabel;

  const originalContent = info.innerHTML;
  info.innerHTML = '';
  info.appendChild(input);

  let committed = false;
  function finishRename(): void {
    if (committed) return;
    committed = true;
    const newLabel = input.value.trim();
    info.innerHTML = originalContent;

    if (newLabel !== currentLabel) {
      const newLabelEl = info.querySelector('.history-item-label');
      if (newLabelEl) {
        newLabelEl.textContent = newLabel || '';
      } else if (newLabel) {
        const span = document.createElement('span');
        span.className = 'history-item-label';
        span.textContent = newLabel;
        info.insertBefore(span, info.firstChild);
      }

      entry.label = newLabel || null;

      void renameHistoryEntry(entry.id, newLabel).then(() => {
        if (onRenameEntry) onRenameEntry(entry.id, newLabel);
      });
    }
  }

  function cancelRename(): void {
    if (committed) return;
    committed = true;
    info.innerHTML = originalContent;
  }

  input.addEventListener('blur', finishRename);
  input.addEventListener('keydown', (e) => {
    if (e.key === 'Enter') {
      e.preventDefault();
      input.blur();
    } else if (e.key === 'Escape') {
      e.preventDefault();
      cancelRename();
    }
  });

  input.focus();
  input.select();
}

function createForegroundIndicator(
  cwd: string,
  commandLine: string | null,
  processName: string,
): HTMLElement {
  const container = document.createElement('span');
  container.className = 'session-foreground';

  const cmdDisplay = formatRuntimeDisplay(processName, commandLine);
  container.title = `${commandLine ?? processName}\n${cwd}`;

  const cwdSpan = document.createElement('span');
  cwdSpan.className = 'fg-cwd';
  cwdSpan.textContent = formatHistoryDirectoryDisplay(cwd);
  container.appendChild(cwdSpan);

  const separator = document.createElement('span');
  separator.className = 'fg-separator';
  separator.textContent = '>';
  container.appendChild(separator);

  const processSpan = document.createElement('span');
  processSpan.className = 'fg-process';
  processSpan.textContent = cmdDisplay;
  container.appendChild(processSpan);

  return container;
}

export function getHistoryRuntimeSummary(
  entry: Pick<
    LaunchEntry,
    | 'workingDirectory'
    | 'commandLine'
    | 'executable'
    | 'foregroundProcessCommandLine'
    | 'foregroundProcessName'
  >,
): { cwd: string; commandLine: string | null; processName: string } {
  return {
    cwd: entry.workingDirectory,
    commandLine: entry.foregroundProcessCommandLine ?? entry.commandLine ?? null,
    processName: entry.foregroundProcessName ?? entry.executable,
  };
}

// ---------------------------------------------------------------------------
// Drag-and-drop reordering for pinned entries
// ---------------------------------------------------------------------------

function initDragHandlers(container: HTMLElement): void {
  container.addEventListener('dragstart', onDragStart);
  container.addEventListener('dragend', onDragEnd);
  container.addEventListener('dragover', onDragOver);
  container.addEventListener('dragleave', onDragLeave);
  container.addEventListener('drop', onDrop);

  container.addEventListener('touchstart', onTouchStart, { passive: false });
  container.addEventListener('touchmove', onTouchMove, { passive: false });
  container.addEventListener('touchend', onTouchEnd);
  container.addEventListener('touchcancel', onTouchEnd);
}

function onDragStart(e: DragEvent): void {
  const item = (e.target as HTMLElement).closest<HTMLElement>('.history-item');
  if (!item) return;

  draggedId = item.dataset.id ?? null;
  draggedElement = item;
  item.classList.add('dragging');

  if (e.dataTransfer) {
    e.dataTransfer.effectAllowed = 'move';
    e.dataTransfer.setData('text/plain', draggedId ?? '');
  }
}

function onDragEnd(_e: DragEvent): void {
  if (draggedElement) draggedElement.classList.remove('dragging');
  clearAllIndicators();
  draggedId = null;
  draggedElement = null;
  dropPosition = null;
}

function onDragOver(e: DragEvent): void {
  e.preventDefault();
  if (!draggedId) return;

  const item = (e.target as HTMLElement).closest<HTMLElement>('.history-item');
  if (!item || item === draggedElement) {
    clearAllIndicators();
    return;
  }

  if (e.dataTransfer) e.dataTransfer.dropEffect = 'move';

  const rect = item.getBoundingClientRect();
  const isAbove = e.clientY < rect.top + rect.height / 2;

  clearAllIndicators();
  item.classList.add('drag-over');
  activeIndicators.add(item);
  if (isAbove) {
    item.classList.add('drag-over-above');
    dropPosition = 'above';
  } else {
    item.classList.add('drag-over-below');
    dropPosition = 'below';
  }
}

function onDragLeave(e: DragEvent): void {
  const item = (e.target as HTMLElement).closest<HTMLElement>('.history-item');
  if (!item) return;
  const related = e.relatedTarget as Node | null;
  if (!related || !item.contains(related)) {
    item.classList.remove('drag-over', 'drag-over-above', 'drag-over-below');
    activeIndicators.delete(item);
  }
}

function onDrop(e: DragEvent): void {
  e.preventDefault();
  if (!draggedId) return;

  const targetItem = (e.target as HTMLElement).closest<HTMLElement>('.history-item');
  if (!targetItem || targetItem === draggedElement) return;

  const targetId = targetItem.dataset.id;
  if (!targetId) return;

  applyReorder(draggedId, targetId, dropPosition);
  clearAllIndicators();
}

// Touch handlers (mirrors sessionDrag.ts pattern)

function onTouchStart(e: TouchEvent): void {
  const touch = e.touches[0];
  if (!touch) return;

  const grip = (touch.target as HTMLElement).closest('.history-item-grip');
  if (!grip) return;

  const item = grip.closest<HTMLElement>('.history-item');
  if (!item) return;

  touchStartY = touch.clientY;

  touchDragTimer = setTimeout(() => {
    draggedId = item.dataset.id ?? null;
    draggedElement = item;
    touchActive = true;
    item.classList.add('dragging');

    touchGhost = item.cloneNode(true) as HTMLElement;
    touchGhost.style.position = 'fixed';
    touchGhost.style.left = '0';
    touchGhost.style.top = `${touch.clientY - item.offsetHeight / 2}px`;
    touchGhost.style.width = `${item.offsetWidth}px`;
    touchGhost.style.opacity = '0.85';
    touchGhost.style.pointerEvents = 'none';
    touchGhost.style.zIndex = '9999';
    touchGhost.classList.remove('dragging');
    document.body.appendChild(touchGhost);
  }, TOUCH_DRAG_DELAY_MS);
}

function onTouchMove(e: TouchEvent): void {
  const touch = e.touches[0];
  if (!touch) return;

  if (!touchActive) {
    if (Math.abs(touch.clientY - touchStartY) > 10) cancelTouchDrag();
    return;
  }

  e.preventDefault();
  if (touchGhost) {
    touchGhost.style.top = `${touch.clientY - (draggedElement?.offsetHeight ?? 30) / 2}px`;
  }

  if (touchGhost) touchGhost.style.display = 'none';
  const el = document.elementFromPoint(touch.clientX, touch.clientY) as HTMLElement | null;
  if (touchGhost) touchGhost.style.display = '';
  if (!el) return;

  const item = el.closest<HTMLElement>('.history-item');
  clearAllIndicators();

  if (item && item !== draggedElement) {
    const rect = item.getBoundingClientRect();
    const isAbove = touch.clientY < rect.top + rect.height / 2;
    item.classList.add('drag-over');
    activeIndicators.add(item);
    if (isAbove) {
      item.classList.add('drag-over-above');
      dropPosition = 'above';
    } else {
      item.classList.add('drag-over-below');
      dropPosition = 'below';
    }
  }
}

function onTouchEnd(e: TouchEvent): void {
  cancelTouchDrag();

  if (!touchActive || !draggedId) {
    touchActive = false;
    return;
  }

  const touch = e.changedTouches[0];
  if (touch) {
    if (touchGhost) touchGhost.style.display = 'none';
    const el = document.elementFromPoint(touch.clientX, touch.clientY) as HTMLElement | null;
    if (touchGhost) touchGhost.style.display = '';
    const targetItem = el?.closest('.history-item') as HTMLElement | null;

    if (targetItem && targetItem !== draggedElement) {
      const targetId = targetItem.dataset.id;
      if (targetId) applyReorder(draggedId, targetId, dropPosition);
    }
  }

  if (draggedElement) draggedElement.classList.remove('dragging');
  if (touchGhost) {
    touchGhost.remove();
    touchGhost = null;
  }
  clearAllIndicators();
  draggedId = null;
  draggedElement = null;
  dropPosition = null;
  touchActive = false;
}

function cancelTouchDrag(): void {
  if (touchDragTimer) {
    clearTimeout(touchDragTimer);
    touchDragTimer = null;
  }
}

function clearAllIndicators(): void {
  activeIndicators.forEach((item) => {
    item.classList.remove('drag-over', 'drag-over-above', 'drag-over-below');
  });
  activeIndicators.clear();
}

function applyReorder(fromId: string, toId: string, position: 'above' | 'below' | null): void {
  const pinned = cachedEntries.filter((e) => e.isStarred);
  const fromIndex = pinned.findIndex((e) => e.id === fromId);
  let toIndex = pinned.findIndex((e) => e.id === toId);

  if (fromIndex === -1 || toIndex === -1 || fromIndex === toIndex) return;

  if (position === 'below') {
    toIndex = fromIndex < toIndex ? toIndex : toIndex + 1;
  } else {
    toIndex = fromIndex > toIndex ? toIndex : toIndex - 1;
  }
  toIndex = Math.max(0, Math.min(pinned.length - 1, toIndex));

  const moved = pinned.splice(fromIndex, 1)[0];
  if (!moved) return;
  pinned.splice(toIndex, 0, moved);

  const orderedIds = pinned.map((e) => e.id);

  // Update cached entries in-place to reflect new order
  const nonPinned = cachedEntries.filter((e) => !e.isStarred);
  cachedEntries = [...pinned, ...nonPinned];

  renderDropdownContent();

  void reorderHistory(orderedIds).catch((err: unknown) => {
    log.warn(() => `Failed to persist history reorder: ${String(err)}`);
  });
}

function handleOutsideClick(e: MouseEvent): void {
  if (!dropdownEl) return;

  const target = e.target as Element;
  if (!dropdownEl.contains(target) && !target.closest('#btn-bookmarks')) {
    closeHistoryDropdown();
  }
}

function handleViewportChange(): void {
  if (isOpen) {
    positionDropdown();
  }
}

// Re-export LaunchEntry for main.ts
export type { LaunchEntry };

/* eslint-disable max-lines -- managerBar.ts remains a legacy integration hub; small overflow-policy changes are safer than broad file splits in the same turn. */
/**
 * Manager Bar Module
 *
 * Renders customizable quick-action buttons below the terminal area.
 * Buttons can execute immediately or queue richer workflows against the
 * session that was active when the action was triggered.
 */

import {
  $activeSessionId,
  $currentSettings,
  $managerBarQueue,
  $settingsOpen,
  $sessions,
} from '../../stores';
import { updateSettings } from '../../api/client';
import { icon } from '../../constants';
import type { ManagerBarQueueEntry } from '../../types';
import { enqueueCommandBayAction, removeCommandBayQueueEntry } from '../commandBay/queue';
import { submitSessionText } from '../input/submit';
import { t } from '../i18n';
import { createLogger } from '../logging';
import { registerBackButtonLayer } from '../navigation/backButtonGuard';
import {
  createDefaultManagerButton,
  formatPromptPreview,
  isImmediateManagerAction,
  normalizeManagerBarButton,
  normalizeManagerBarButtons,
  type ManagerActionType,
  type ManagerBarScheduleEntry,
  type ManagerButton,
  type ManagerRepeatUnit,
  type ManagerScheduleRepeat,
  type ManagerTriggerKind,
  type NormalizedManagerButton,
} from './workflow';
import { shouldShowManagerBar } from './visibility';

const log = createLogger('managerBar');
const QUEUE_ENQUEUE_DEDUP_WINDOW_MS = 1500;
const OVERFLOW_LAYOUT_EPSILON_PX = 0.75;
const OVERFLOW_BUTTON_GAP_PX = 6;
const OVERFLOW_MENU_BUTTON_WIDTH_PX = 32;

let barEl: HTMLElement | null = null;
let queueEl: HTMLElement | null = null;
let buttonsEl: HTMLElement | null = null;
let addBtn: HTMLElement | null = null;
let overflowBtn: HTMLButtonElement | null = null;
let mobileDropdown: HTMLElement | null = null;
let menuPopoverEl: HTMLElement | null = null;
let overflowPopoverEl: HTMLElement | null = null;
let openMenuButtonId: string | null = null;
let openMenuAnchorEl: HTMLButtonElement | null = null;
let overflowActionIds: string[] = [];
let overflowProxyAnchorEl: HTMLElement | null = null;
let activeOverflowAnchorEl: HTMLElement | null = null;

interface ViewportBounds {
  top: number;
  right: number;
  bottom: number;
  left: number;
}
let managerBarResizeObserver: ResizeObserver | null = null;
let overflowLayoutFrameId: number | null = null;

let modalEl: HTMLElement | null = null;
let modalBackdrop: HTMLElement | null = null;
let modalCloseBtn: HTMLElement | null = null;
let modalCancelBtn: HTMLElement | null = null;
let modalSaveBtn: HTMLElement | null = null;
let modalTitleEl: HTMLElement | null = null;
let modalErrorEl: HTMLElement | null = null;
let labelInput: HTMLInputElement | null = null;
let typeSelect: HTMLSelectElement | null = null;
let triggerSelect: HTMLSelectElement | null = null;
let promptsTitleEl: HTMLElement | null = null;
let promptsCopyEl: HTMLElement | null = null;
let typeDescriptionEl: HTMLElement | null = null;
let triggerDescriptionEl: HTMLElement | null = null;
let promptsContainer: HTMLElement | null = null;
let addPromptBtn: HTMLButtonElement | null = null;
let repeatCountInput: HTMLInputElement | null = null;
let repeatEveryValueInput: HTMLInputElement | null = null;
let repeatEveryUnitSelect: HTMLSelectElement | null = null;
let scheduleContainer: HTMLElement | null = null;
let addScheduleBtn: HTMLButtonElement | null = null;
let cooldownHintEl: HTMLElement | null = null;
let chainHintEl: HTMLElement | null = null;
let triggerDetailsEl: HTMLElement | null = null;
let repeatCountGroupEl: HTMLElement | null = null;
let repeatIntervalGroupEl: HTMLElement | null = null;
let scheduleGroupEl: HTMLElement | null = null;

let editingActionId: string | null = null;
let renderedButtons: NormalizedManagerButton[] = [];
let queueEntries: ManagerBarQueueEntry[] = [];
let releaseBackButtonLayer: (() => void) | null = null;
const pendingEnqueueGuards = new Map<string, number>();
const pendingQueueRemovals = new Set<string>();

export function sendCommand(sessionId: string, text: string): void {
  void submitSessionText(sessionId, text).catch((error: unknown) => {
    log.error(() => `Failed to submit manager bar command: ${String(error)}`);
  });
}

export function setAutomationOverflowProxyAnchor(el: HTMLElement | null): void {
  overflowProxyAnchorEl = el;
  if (!el && activeOverflowAnchorEl && activeOverflowAnchorEl !== overflowBtn) {
    activeOverflowAnchorEl = null;
  }
}

export function triggerAutomationOverflow(anchor: HTMLElement | null = null): void {
  toggleOverflowMenu(anchor);
}

export function triggerAddAutomation(): void {
  openActionModal();
}

export function initManagerBar(): void {
  barEl = document.getElementById('manager-bar');
  queueEl = document.getElementById('manager-bar-queue');
  buttonsEl = document.getElementById('manager-bar-buttons');
  addBtn = document.getElementById('manager-bar-add');
  overflowBtn = document.getElementById('manager-bar-overflow') as HTMLButtonElement | null;
  mobileDropdown = document.getElementById('mobile-actions-dropdown');

  ensureManagerActionModalElements();
  ensureMenuPopover();
  ensureOverflowPopover();

  if (!barEl || !buttonsEl || !addBtn || !overflowBtn || !queueEl) return;

  const syncManagerBarVisibility = (): void => {
    const settings = $currentSettings.get();
    const visible =
      !$settingsOpen.get() &&
      shouldShowManagerBar(settings?.managerBarEnabled, $activeSessionId.get());
    barEl?.classList.toggle('hidden', !visible);
    if (!visible) {
      overflowBtn?.setAttribute('hidden', '');
      overflowActionIds = [];
      closeOpenManagerOverflow();
    }
    renderMobileButtons(visible ? renderedButtons : []);
    renderQueue();
    scheduleOverflowLayout();
  };

  $currentSettings.subscribe((settings) => {
    if (!settings) return;
    renderedButtons = normalizeManagerBarButtons(
      settings.managerBarButtons as unknown as ManagerButton[],
    );
    renderButtons(renderedButtons);
    syncManagerBarVisibility();
  });

  $activeSessionId.subscribe(() => {
    syncManagerBarVisibility();
  });

  $settingsOpen.subscribe(() => {
    syncManagerBarVisibility();
  });

  $managerBarQueue.subscribe((entries) => {
    queueEntries = [...entries];
    const liveQueueIds = new Set(entries.map((entry) => entry.queueId));
    for (const queueId of pendingQueueRemovals) {
      if (!liveQueueIds.has(queueId)) {
        pendingQueueRemovals.delete(queueId);
      }
    }
    syncManagerBarVisibility();
  });

  buttonsEl.addEventListener('click', (event) => {
    const target = resolveEventElement(event.target);
    if (!target) return;

    const menuBtn = target.closest<HTMLButtonElement>('.manager-btn-menu');
    if (menuBtn) {
      event.preventDefault();
      event.stopPropagation();
      const button = menuBtn.closest<HTMLElement>('.manager-btn');
      const buttonId = button?.dataset.id ?? null;
      if (buttonId) {
        toggleManagerActionMenu(menuBtn, buttonId);
      }
      return;
    }

    const button = target.closest<HTMLElement>('.manager-btn');
    if (button) {
      closeOpenManagerMenus();
      if (button.dataset.id) runButton(button.dataset.id);
    }
  });

  buttonsEl.addEventListener('pointerdown', (event) => {
    const target = resolveEventElement(event.target);
    if (!target?.closest('.manager-btn-menu')) {
      return;
    }

    event.stopPropagation();
  });

  document.addEventListener('click', handleDocumentClickForManagerMenu);
  document.addEventListener('click', handleDocumentClickForManagerOverflow);

  addBtn.addEventListener('click', (event) => {
    event.preventDefault();
    event.stopPropagation();
    closeOpenManagerMenus();
    openActionModal();
  });

  overflowBtn.addEventListener('click', (event) => {
    event.preventDefault();
    event.stopPropagation();
    toggleOverflowMenu(overflowBtn);
  });

  window.addEventListener('resize', () => {
    positionManagerActionMenu();
    scheduleOverflowLayout();
  });
  document.addEventListener(
    'scroll',
    () => {
      positionManagerActionMenu();
      positionManagerOverflowMenu();
    },
    true,
  );
  managerBarResizeObserver = new ResizeObserver(() => {
    scheduleOverflowLayout();
    positionManagerActionMenu();
    positionManagerOverflowMenu();
  });
  managerBarResizeObserver.observe(barEl);
  managerBarResizeObserver.observe(buttonsEl);

  if (mobileDropdown) {
    mobileDropdown.addEventListener('click', (event) => {
      const target = event.target as HTMLElement | null;
      const mobileBtn = target?.closest<HTMLElement>('.mobile-manager-item');
      if (!mobileBtn?.dataset.managerId) return;
      runButton(mobileBtn.dataset.managerId);
    });
  }

  bindModalEvents();
}

function ensureManagerActionModalElements(): boolean {
  const pickElement = <T extends HTMLElement>(current: T | null, id: string): T | null =>
    current ?? (document.getElementById(id) as T | null);

  modalEl = pickElement(modalEl, 'manager-action-modal');
  modalBackdrop ??= modalEl?.querySelector('.modal-backdrop') ?? null;
  modalCloseBtn = pickElement(modalCloseBtn, 'btn-close-manager-action');
  modalCancelBtn = pickElement(modalCancelBtn, 'btn-cancel-manager-action');
  modalSaveBtn = pickElement(modalSaveBtn, 'btn-save-manager-action');
  modalTitleEl = pickElement(modalTitleEl, 'manager-action-modal-title');
  modalErrorEl = pickElement(modalErrorEl, 'manager-action-error');
  labelInput = pickElement<HTMLInputElement>(labelInput, 'manager-action-label');
  typeSelect = pickElement<HTMLSelectElement>(typeSelect, 'manager-action-type');
  triggerSelect = pickElement<HTMLSelectElement>(triggerSelect, 'manager-action-trigger');
  promptsTitleEl = pickElement(promptsTitleEl, 'manager-action-prompts-title');
  promptsCopyEl = pickElement(promptsCopyEl, 'manager-action-prompts-copy');
  typeDescriptionEl = pickElement(typeDescriptionEl, 'manager-action-type-description');
  triggerDescriptionEl = pickElement(triggerDescriptionEl, 'manager-action-trigger-description');
  promptsContainer = pickElement(promptsContainer, 'manager-action-prompts');
  addPromptBtn = pickElement<HTMLButtonElement>(addPromptBtn, 'manager-action-add-prompt');
  repeatCountInput = pickElement<HTMLInputElement>(repeatCountInput, 'manager-action-repeat-count');
  repeatEveryValueInput = pickElement<HTMLInputElement>(
    repeatEveryValueInput,
    'manager-action-repeat-every-value',
  );
  repeatEveryUnitSelect = pickElement<HTMLSelectElement>(
    repeatEveryUnitSelect,
    'manager-action-repeat-every-unit',
  );
  scheduleContainer = pickElement(scheduleContainer, 'manager-action-schedule-list');
  addScheduleBtn = pickElement<HTMLButtonElement>(addScheduleBtn, 'manager-action-add-schedule');
  cooldownHintEl = pickElement(cooldownHintEl, 'manager-action-cooldown-hint');
  chainHintEl = pickElement(chainHintEl, 'manager-action-chain-hint');
  triggerDetailsEl = pickElement(triggerDetailsEl, 'manager-action-trigger-details');
  repeatCountGroupEl = pickElement(repeatCountGroupEl, 'manager-action-repeat-count-group');
  repeatIntervalGroupEl = pickElement(
    repeatIntervalGroupEl,
    'manager-action-repeat-interval-group',
  );
  scheduleGroupEl = pickElement(scheduleGroupEl, 'manager-action-schedule-group');

  return Boolean(
    modalEl &&
    modalTitleEl &&
    labelInput &&
    typeSelect &&
    triggerSelect &&
    repeatCountInput &&
    repeatEveryValueInput &&
    repeatEveryUnitSelect,
  );
}

function ensureMenuPopover(): void {
  if (menuPopoverEl) {
    return;
  }

  const popover = document.createElement('div');
  popover.className = 'manager-bar-action-popover hidden';

  const editBtn = document.createElement('button');
  editBtn.type = 'button';
  editBtn.className = 'manager-bar-action-popover-btn manager-bar-action-popover-edit';
  editBtn.innerHTML = `<span class="icon">\ue91f</span><span class="manager-bar-action-popover-label">${escapeHtml(t('managerBar.edit'))}</span>`;
  editBtn.addEventListener('click', () => {
    const actionId = openMenuButtonId;
    closeOpenManagerMenus();
    closeOpenManagerOverflow();
    if (!actionId) {
      return;
    }

    const action = renderedButtons.find((entry) => entry.id === actionId);
    if (action) {
      openActionModal(action);
    }
  });

  const deleteBtn = document.createElement('button');
  deleteBtn.type = 'button';
  deleteBtn.className = 'manager-bar-action-popover-btn manager-bar-action-popover-delete';
  deleteBtn.innerHTML = `<span class="icon">\ue909</span><span class="manager-bar-action-popover-label">${escapeHtml(t('managerBar.remove'))}</span>`;
  deleteBtn.addEventListener('click', () => {
    const actionId = openMenuButtonId;
    closeOpenManagerMenus();
    closeOpenManagerOverflow();
    if (actionId) {
      deleteButton(actionId);
    }
  });

  popover.appendChild(editBtn);
  popover.appendChild(deleteBtn);
  document.body.appendChild(popover);

  menuPopoverEl = popover;
}

function ensureOverflowPopover(): void {
  if (overflowPopoverEl) {
    return;
  }

  const popover = document.createElement('div');
  popover.className = 'manager-bar-action-popover manager-bar-overflow-popover hidden';
  popover.addEventListener('click', (event) => {
    const target = resolveEventElement(event.target);
    const menuButton = target?.closest<HTMLButtonElement>('.manager-bar-overflow-item-menu');
    if (menuButton?.dataset.actionId) {
      event.preventDefault();
      event.stopPropagation();
      toggleManagerActionMenu(menuButton, menuButton.dataset.actionId);
      return;
    }

    const actionButton = target?.closest<HTMLButtonElement>('.manager-bar-overflow-item');
    if (!actionButton?.dataset.actionId) {
      return;
    }

    event.preventDefault();
    event.stopPropagation();
    closeOpenManagerMenus();
    closeOpenManagerOverflow();
    runButton(actionButton.dataset.actionId);
  });
  document.body.appendChild(popover);
  overflowPopoverEl = popover;
}

function bindModalEvents(): void {
  modalCloseBtn?.addEventListener('click', closeActionModal);
  modalCancelBtn?.addEventListener('click', closeActionModal);
  modalBackdrop?.addEventListener('click', closeActionModal);
  modalSaveBtn?.addEventListener('click', saveModalAction);

  typeSelect?.addEventListener('change', () => {
    const prompts = readPromptValues();
    renderPromptEditors(
      typeSelect?.value === 'chain' ? Math.max(prompts.length, 1) : 1,
      prompts,
      typeSelect?.value === 'chain' ? 'chain' : 'single',
    );
    syncModalSections();
  });

  triggerSelect?.addEventListener('change', syncModalSections);

  addPromptBtn?.addEventListener('click', () => {
    const prompts = readPromptValues();
    prompts.push('');
    renderPromptEditors(prompts.length, prompts, getModalActionType());
    focusPrimaryPrompt(prompts.length - 1);
  });

  addScheduleBtn?.addEventListener('click', () => {
    const schedule = readScheduleValues();
    schedule.push({ timeOfDay: '09:00', repeat: 'daily' });
    renderScheduleEditors(schedule);
    focusNewestScheduleTime();
  });

  scheduleContainer?.addEventListener('click', (event) => {
    const target = event.target as HTMLElement | null;
    const removeBtn = target?.closest<HTMLButtonElement>('.manager-action-schedule-remove');
    if (!removeBtn) return;

    const index = Number.parseInt(removeBtn.dataset.index ?? '-1', 10);
    if (!Number.isInteger(index) || index < 0) return;

    const schedule = readScheduleValues();
    schedule.splice(index, 1);
    renderScheduleEditors(schedule);
  });

  promptsContainer?.addEventListener('click', (event) => {
    const target = event.target as HTMLElement | null;
    const removeBtn = target?.closest<HTMLButtonElement>('.manager-action-prompt-remove');
    if (!removeBtn) return;

    const index = Number.parseInt(removeBtn.dataset.index ?? '-1', 10);
    if (!Number.isInteger(index) || index < 0) return;

    const prompts = readPromptValues();
    prompts.splice(index, 1);
    renderPromptEditors(Math.max(prompts.length, 1), prompts, getModalActionType());
  });

  document.addEventListener('keydown', (event) => {
    if (event.key !== 'Escape') return;
    if (closeOpenManagerMenus()) return;
    if (!modalEl || modalEl.classList.contains('hidden')) return;
    closeActionModal();
  });
}

function renderButtons(buttons: NormalizedManagerButton[]): void {
  if (!buttonsEl) return;

  closeOpenManagerMenus();
  closeOpenManagerOverflow();
  buttonsEl.innerHTML = '';
  for (const button of buttons) {
    const wrapper = document.createElement('span');
    wrapper.className = 'manager-btn';
    wrapper.dataset.id = button.id;
    wrapper.innerHTML =
      `<span class="manager-btn-label">${escapeHtml(button.label)}</span>` +
      `<button class="manager-btn-menu" title="${escapeHtml(t('session.actions'))}" aria-label="${escapeHtml(t('session.actions'))}" aria-haspopup="menu" aria-expanded="false" type="button">${icon('menu')}</button>`;
    buttonsEl.appendChild(wrapper);
  }
  scheduleOverflowLayout();
}

function handleDocumentClickForManagerMenu(event: MouseEvent): void {
  const target = resolveEventElement(event.target);
  if (target?.closest('.manager-btn') || target?.closest('.manager-bar-action-popover')) {
    return;
  }

  closeOpenManagerMenus();
}

function handleDocumentClickForManagerOverflow(event: MouseEvent): void {
  const target = resolveEventElement(event.target);
  if (
    target?.closest('.manager-bar-overflow') ||
    target?.closest('.manager-bar-overflow-popover')
  ) {
    return;
  }

  closeOpenManagerOverflow();
}

function closeOpenManagerMenus(): boolean {
  let closedAny = false;
  document
    .querySelectorAll<HTMLElement>('.manager-btn.menu-open, .manager-bar-overflow-row.menu-open')
    .forEach((button) => {
      button.classList.remove('menu-open');
      closedAny = true;
    });
  document
    .querySelectorAll<HTMLButtonElement>(
      '.manager-btn-menu[aria-expanded="true"], .manager-bar-overflow-item-menu[aria-expanded="true"]',
    )
    .forEach((button) => {
      button.setAttribute('aria-expanded', 'false');
    });

  if (menuPopoverEl && !menuPopoverEl.classList.contains('hidden')) {
    menuPopoverEl.classList.add('hidden');
    menuPopoverEl.style.removeProperty('left');
    menuPopoverEl.style.removeProperty('top');
    closedAny = true;
  }

  openMenuButtonId = null;
  openMenuAnchorEl = null;

  return closedAny;
}

function closeOpenManagerOverflow(): boolean {
  if (!overflowBtn || !overflowPopoverEl) {
    return false;
  }

  const wasOpen = !overflowPopoverEl.classList.contains('hidden');
  overflowPopoverEl.classList.add('hidden');
  overflowPopoverEl.replaceChildren();
  overflowPopoverEl.style.removeProperty('left');
  overflowPopoverEl.style.removeProperty('top');
  overflowBtn.setAttribute('aria-expanded', 'false');
  activeOverflowAnchorEl = null;
  return wasOpen;
}

function toggleManagerActionMenu(anchor: HTMLButtonElement, actionId: string): void {
  const isSameMenu = openMenuButtonId === actionId && !menuPopoverEl?.classList.contains('hidden');
  closeOpenManagerMenus();
  if (isSameMenu) {
    return;
  }

  const button = anchor.closest<HTMLElement>('.manager-btn, .manager-bar-overflow-row');
  if (!button) {
    return;
  }

  ensureMenuPopover();
  button.classList.add('menu-open');
  anchor.setAttribute('aria-expanded', 'true');
  openMenuButtonId = actionId;
  openMenuAnchorEl = anchor;
  menuPopoverEl?.classList.remove('hidden');
  positionManagerActionMenu();
}

function toggleOverflowMenu(anchor: HTMLElement | null = null): void {
  if (!overflowBtn || !overflowPopoverEl) {
    return;
  }
  if (overflowActionIds.length === 0 && !(barEl && isMobileAppServerControlSurface(barEl))) {
    return;
  }

  const isOpen = !overflowPopoverEl.classList.contains('hidden');
  if (isOpen) {
    closeOpenManagerOverflow();
    return;
  }

  closeOpenManagerMenus();
  activeOverflowAnchorEl = resolveUsableOverflowAnchor(anchor) ?? resolveCurrentOverflowAnchor();
  renderOverflowMenuItems();
  overflowPopoverEl.classList.remove('hidden');
  overflowBtn.setAttribute('aria-expanded', 'true');
  positionManagerOverflowMenu();
}

function positionManagerActionMenu(): void {
  if (!menuPopoverEl || !openMenuAnchorEl || menuPopoverEl.classList.contains('hidden')) {
    return;
  }

  const viewport = getVisualViewportBounds();
  const viewportPadding = 12;
  const gap = 8;
  const triggerRect = openMenuAnchorEl.getBoundingClientRect();
  const popoverRect = menuPopoverEl.getBoundingClientRect();
  const availableBelow = viewport.bottom - triggerRect.bottom - viewportPadding - gap;
  const availableAbove = triggerRect.top - viewport.top - viewportPadding - gap;
  const openUp = availableBelow < popoverRect.height && availableAbove > availableBelow;

  let left = triggerRect.right - popoverRect.width;
  left = Math.max(
    viewport.left + viewportPadding,
    Math.min(left, viewport.right - viewportPadding - popoverRect.width),
  );

  let top = openUp ? triggerRect.top - popoverRect.height - gap : triggerRect.bottom + gap;
  top = Math.max(
    viewport.top + viewportPadding,
    Math.min(top, viewport.bottom - viewportPadding - popoverRect.height),
  );

  menuPopoverEl.style.left = `${String(Math.round(left))}px`;
  menuPopoverEl.style.top = `${String(Math.round(top))}px`;
}

function positionManagerOverflowMenu(): void {
  const anchorEl = resolveCurrentOverflowAnchor();
  if (!overflowPopoverEl || !anchorEl || overflowPopoverEl.classList.contains('hidden')) {
    return;
  }

  const viewport = getVisualViewportBounds();
  const viewportPadding = 12;
  const gap = 8;
  const triggerRect = anchorEl.getBoundingClientRect();
  const popoverRect = overflowPopoverEl.getBoundingClientRect();
  const availableBelow = viewport.bottom - triggerRect.bottom - viewportPadding - gap;
  const availableAbove = triggerRect.top - viewport.top - viewportPadding - gap;
  const openUp = availableBelow < popoverRect.height && availableAbove > availableBelow;

  let left = triggerRect.right - popoverRect.width;
  left = Math.max(
    viewport.left + viewportPadding,
    Math.min(left, viewport.right - viewportPadding - popoverRect.width),
  );

  let top = openUp ? triggerRect.top - popoverRect.height - gap : triggerRect.bottom + gap;
  top = Math.max(
    viewport.top + viewportPadding,
    Math.min(top, viewport.bottom - viewportPadding - popoverRect.height),
  );

  overflowPopoverEl.style.left = `${String(Math.round(left))}px`;
  overflowPopoverEl.style.top = `${String(Math.round(top))}px`;
}

function getVisualViewportBounds(): ViewportBounds {
  const vv = window.visualViewport;
  const top = vv?.offsetTop ?? 0;
  const left = vv?.offsetLeft ?? 0;
  const width = vv?.width ?? window.innerWidth;
  const height = vv?.height ?? window.innerHeight;
  return {
    top,
    right: left + width,
    bottom: top + height,
    left,
  };
}

function resolveCurrentOverflowAnchor(): HTMLElement | null {
  return (
    resolveUsableOverflowAnchor(activeOverflowAnchorEl) ??
    resolveUsableOverflowAnchor(overflowProxyAnchorEl) ??
    resolveUsableOverflowAnchor(overflowBtn)
  );
}

function resolveUsableOverflowAnchor(anchor: HTMLElement | null): HTMLElement | null {
  if (!anchor?.isConnected) {
    return null;
  }

  const rect = anchor.getBoundingClientRect();
  if (rect.width <= 0 || rect.height <= 0) {
    return null;
  }

  return anchor;
}

function renderOverflowMenuItems(): void {
  if (!overflowPopoverEl) {
    return;
  }

  overflowPopoverEl.replaceChildren();
  for (const actionId of overflowActionIds) {
    const action = renderedButtons.find((entry) => entry.id === actionId);
    if (!action) {
      continue;
    }

    const row = document.createElement('div');
    row.className = 'manager-bar-overflow-row';

    const runButton = document.createElement('button');
    runButton.type = 'button';
    runButton.className = 'manager-bar-action-popover-btn manager-bar-overflow-item';
    runButton.dataset.actionId = action.id;
    runButton.textContent = action.label;

    const menuButton = document.createElement('button');
    menuButton.type = 'button';
    menuButton.className = 'manager-bar-action-popover-btn manager-bar-overflow-item-menu';
    menuButton.dataset.actionId = action.id;
    menuButton.title = t('session.actions');
    menuButton.setAttribute('aria-label', t('session.actions'));
    menuButton.setAttribute('aria-haspopup', 'menu');
    menuButton.setAttribute('aria-expanded', 'false');
    menuButton.innerHTML = icon('menu');

    row.appendChild(runButton);
    row.appendChild(menuButton);
    overflowPopoverEl.appendChild(row);
  }

  if (barEl && isMobileAppServerControlSurface(barEl)) {
    const addItem = document.createElement('button');
    addItem.type = 'button';
    addItem.className = 'manager-bar-action-popover-btn manager-bar-overflow-item';
    addItem.textContent = t('managerBar.addButton');
    addItem.addEventListener('click', (event) => {
      event.preventDefault();
      event.stopPropagation();
      closeOpenManagerOverflow();
      openActionModal();
    });
    overflowPopoverEl.appendChild(addItem);
  }
}

function scheduleOverflowLayout(): void {
  if (overflowLayoutFrameId !== null) {
    window.cancelAnimationFrame(overflowLayoutFrameId);
  }

  overflowLayoutFrameId = window.requestAnimationFrame(() => {
    overflowLayoutFrameId = null;
    syncOverflowedButtons();
  });
}

function syncOverflowedButtons(): void {
  if (!barEl || !buttonsEl || !addBtn || !overflowBtn) {
    return;
  }
  const managerBar = barEl;
  const buttonStrip = buttonsEl;
  const addButton = addBtn;
  const overflowButton = overflowBtn;

  const isMobileSurface = shouldCollapseManagerButtonsToOverflow(managerBar);
  if (managerBar.classList.contains('hidden') && !isMobileSurface) {
    return;
  }

  const mobileAppServerControl = isMobileAppServerControlSurface(managerBar);
  addButton.classList.toggle('hidden', mobileAppServerControl);

  const buttonElements = [...buttonStrip.querySelectorAll<HTMLElement>('.manager-btn')];
  if (buttonElements.length === 0) {
    resetOverflowLayoutState(buttonStrip, overflowButton);
    if (mobileAppServerControl) {
      overflowButton.removeAttribute('hidden');
    }
    return;
  }

  for (const element of buttonElements) {
    element.classList.remove('manager-btn-overflow-hidden');
  }

  resetOverflowLayoutChrome(buttonStrip, overflowButton);
  if (collapseManagerButtonsToOverflow(managerBar, buttonStrip, buttonElements, overflowButton)) {
    return;
  }

  const fullAvailableWidth = getAvailableManagerRailWidth(managerBar, addButton);

  const buttonWidths = buttonElements.map((element) => getMeasuredWidth(element));
  const totalWidth = buttonWidths.reduce(
    (sum, width, index) => sum + width + (index > 0 ? OVERFLOW_BUTTON_GAP_PX : 0),
    0,
  );

  if (totalWidth <= fullAvailableWidth + OVERFLOW_LAYOUT_EPSILON_PX) {
    buttonStrip.style.maxWidth = `${String(fullAvailableWidth)}px`;
    resetOverflowMenuState(overflowButton);
    return;
  }

  const visibleBudget = Math.max(
    0,
    fullAvailableWidth - OVERFLOW_MENU_BUTTON_WIDTH_PX - OVERFLOW_BUTTON_GAP_PX,
  );
  const nextOverflowIds = collectOverflowActionIds(buttonElements, buttonWidths, visibleBudget);

  buttonStrip.style.maxWidth = `${String(Math.max(0, visibleBudget))}px`;
  overflowActionIds = nextOverflowIds;
  if (overflowActionIds.length === 0) {
    resetOverflowMenuState(overflowButton);
    return;
  }

  overflowButton.removeAttribute('hidden');
  if (overflowPopoverEl && !overflowPopoverEl.classList.contains('hidden')) {
    renderOverflowMenuItems();
    positionManagerOverflowMenu();
  }
}

function resetOverflowLayoutChrome(
  buttonStrip: HTMLElement,
  overflowButton: HTMLButtonElement,
): void {
  buttonStrip.style.maxWidth = '';
  overflowButton.setAttribute('hidden', '');
}

function resetOverflowMenuState(overflowButton: HTMLButtonElement): void {
  overflowButton.setAttribute('hidden', '');
  overflowActionIds = [];
  closeOpenManagerOverflow();
}

function resetOverflowLayoutState(
  buttonStrip: HTMLElement,
  overflowButton: HTMLButtonElement,
): void {
  resetOverflowLayoutChrome(buttonStrip, overflowButton);
  resetOverflowMenuState(overflowButton);
}

function shouldCollapseManagerButtonsToOverflow(managerBar: HTMLElement): boolean {
  const footerDock = managerBar.closest<HTMLElement>('.adaptive-footer-dock');
  return footerDock?.dataset.device === 'mobile';
}

function isMobileAppServerControlSurface(managerBar: HTMLElement): boolean {
  const footerDock = managerBar.closest<HTMLElement>('.adaptive-footer-dock');
  return (
    footerDock?.dataset.device === 'mobile' && footerDock.dataset.surface === 'appServerControl'
  );
}

function collapseManagerButtonsToOverflow(
  managerBar: HTMLElement,
  buttonStrip: HTMLElement,
  buttonElements: readonly HTMLElement[],
  overflowButton: HTMLButtonElement,
): boolean {
  if (!shouldCollapseManagerButtonsToOverflow(managerBar)) {
    return false;
  }

  overflowActionIds = buttonElements
    .map((element) => element.dataset.id ?? '')
    .filter((id) => id.length > 0);
  for (const element of buttonElements) {
    element.classList.add('manager-btn-overflow-hidden');
  }

  buttonStrip.style.maxWidth = '0px';
  if (overflowActionIds.length === 0) {
    resetOverflowMenuState(overflowButton);
    return true;
  }

  overflowButton.removeAttribute('hidden');
  if (overflowPopoverEl && !overflowPopoverEl.classList.contains('hidden')) {
    renderOverflowMenuItems();
    positionManagerOverflowMenu();
  }
  return true;
}

function getAvailableManagerRailWidth(managerBar: HTMLElement, addButton: HTMLElement): number {
  const railWidth = Math.max(
    0,
    Math.floor(managerBar.parentElement?.clientWidth ?? managerBar.clientWidth),
  );
  const measuredRailWidth = Math.max(
    0,
    managerBar.parentElement?.getBoundingClientRect().width ??
      managerBar.getBoundingClientRect().width,
  );
  const availableRailWidth = measuredRailWidth > 0 ? measuredRailWidth : railWidth;
  const addWidth = getMeasuredWidth(addButton);
  return Math.max(0, availableRailWidth - addWidth - OVERFLOW_BUTTON_GAP_PX);
}

function collectOverflowActionIds(
  buttonElements: HTMLElement[],
  buttonWidths: number[],
  visibleBudget: number,
): string[] {
  const nextOverflowIds: string[] = [];
  let consumedWidth = 0;

  buttonElements.forEach((element, index) => {
    const width = (buttonWidths[index] ?? 0) + (index > 0 ? OVERFLOW_BUTTON_GAP_PX : 0);
    const id = element.dataset.id ?? '';
    if (
      consumedWidth + width <= visibleBudget + OVERFLOW_LAYOUT_EPSILON_PX ||
      consumedWidth <= OVERFLOW_LAYOUT_EPSILON_PX
    ) {
      consumedWidth += width;
      element.classList.remove('manager-btn-overflow-hidden');
      return;
    }

    element.classList.add('manager-btn-overflow-hidden');
    if (id) {
      nextOverflowIds.push(id);
    }
  });

  return nextOverflowIds;
}

function getMeasuredWidth(element: HTMLElement): number {
  return Math.max(element.getBoundingClientRect().width, element.offsetWidth, 0);
}

function renderMobileButtons(buttons: NormalizedManagerButton[]): void {
  if (!mobileDropdown) return;

  mobileDropdown
    .querySelectorAll('.mobile-manager-item, .mobile-manager-separator')
    .forEach((element) => {
      element.remove();
    });

  if (buttons.length === 0) return;

  const separator = document.createElement('div');
  separator.className = 'mobile-manager-separator';
  mobileDropdown.appendChild(separator);

  for (const button of buttons) {
    const item = document.createElement('button');
    item.className = 'mobile-actions-item topbar-action mobile-manager-item';
    item.dataset.managerId = button.id;
    item.innerHTML =
      `<span class="mobile-actions-symbol">\u25B6</span>` +
      `<span class="mobile-actions-label">${escapeHtml(button.label)}</span>`;
    mobileDropdown.appendChild(item);
  }
}

function renderQueue(): void {
  if (!queueEl) return;

  const activeSessionId = $activeSessionId.get();
  const visibleQueue =
    !$settingsOpen.get() && activeSessionId
      ? queueEntries
          .filter((entry) => entry.sessionId === activeSessionId)
          .filter((entry) => !pendingQueueRemovals.has(entry.queueId))
      : [];

  queueEl.innerHTML = '';
  queueEl.classList.toggle('hidden', visibleQueue.length === 0);
  if (visibleQueue.length === 0) return;

  for (const entry of visibleQueue) {
    const item = document.createElement('div');
    item.className = 'manager-queue-item';
    item.dataset.queueId = entry.queueId;
    item.dataset.kind = entry.kind;

    const title = document.createElement('div');
    title.className = 'manager-queue-title';
    title.textContent = describeQueueTitle(entry);

    const condition = document.createElement('div');
    condition.className = 'manager-queue-condition';
    condition.textContent = describeQueueCondition(entry);

    const deleteBtn = document.createElement('button');
    deleteBtn.type = 'button';
    deleteBtn.className = 'manager-queue-delete';
    deleteBtn.dataset.queueId = entry.queueId;
    deleteBtn.title = t('managerBar.queue.dequeue');
    deleteBtn.setAttribute('aria-label', t('managerBar.queue.dequeue'));
    deleteBtn.innerHTML = '<span class="icon">\ue909</span>';
    deleteBtn.addEventListener('click', (event) => {
      event.preventDefault();
      event.stopPropagation();
      void removeQueueEntry(entry.queueId);
    });

    item.appendChild(title);
    item.appendChild(condition);
    item.appendChild(deleteBtn);
    queueEl.appendChild(item);
  }
}

function describeQueueTitle(entry: ManagerBarQueueEntry): string {
  if (entry.kind === 'prompt') {
    return describeQueuedPromptTitle(entry);
  }

  const action = entry.action;
  if (!action) {
    return t('managerBar.modal.singlePrompt');
  }

  if (action.actionType === 'chain') {
    const step = Math.min(action.prompts.length, entry.nextPromptIndex + 1);
    return `${action.label} (${step}/${action.prompts.length})`;
  }

  if (action.trigger.kind === 'repeatCount') {
    return `${action.label} (${entry.completedCycles + 1}/${action.trigger.repeatCount})`;
  }

  return action.label || formatPromptPreview(action.prompts[0] ?? '');
}

function describeQueueCondition(entry: ManagerBarQueueEntry): string {
  const usesTurnQueue = usesTurnQueueForSession(entry.sessionId);
  if (entry.kind === 'prompt' && entry.nextRunAt) {
    return formatQueuedPromptRunAt(entry.nextRunAt);
  }

  if (entry.phase === 'chainCooldown') {
    return t(usesTurnQueue ? 'managerBar.queue.turn' : 'managerBar.queue.chainCooldown');
  }
  if (entry.phase === 'pendingCooldown') {
    return t(usesTurnQueue ? 'managerBar.queue.turn' : 'managerBar.queue.cooldown');
  }

  if (!entry.action) {
    return t(usesTurnQueue ? 'managerBar.queue.turn' : 'managerBar.queue.cooldown');
  }

  const trigger = entry.action.trigger;
  if (trigger.kind === 'repeatCount') {
    const remaining = Math.max(0, trigger.repeatCount - entry.completedCycles);
    return `${t('managerBar.queue.repeatCountPrefix')} ${remaining}${t('managerBar.queue.repeatCountSuffix')}`;
  }
  if (trigger.kind === 'repeatInterval') {
    return `${t('managerBar.queue.every')} ${trigger.repeatEveryValue} ${t(
      `managerBar.intervalUnit.${trigger.repeatEveryUnit}`,
    )}`;
  }
  if (trigger.kind === 'schedule') {
    return trigger.schedule
      .map(
        (schedule) => `${t(`managerBar.scheduleRepeat.${schedule.repeat}`)} ${schedule.timeOfDay}`,
      )
      .join(' • ');
  }
  if (trigger.kind === 'fireAndForget' && entry.action.actionType === 'chain') {
    return t('managerBar.queue.chainRunning');
  }
  return t(usesTurnQueue ? 'managerBar.queue.turn' : 'managerBar.queue.cooldown');
}

function describeQueuedPromptTitle(entry: ManagerBarQueueEntry): string {
  const text = entry.turn?.text?.trim() ?? '';
  if (text.length > 0) {
    return formatPromptPreview(text);
  }

  const attachments = entry.turn?.attachments ?? [];
  const firstAttachment = attachments[0];
  if (firstAttachment) {
    const pathParts = firstAttachment.path
      .split(/[\\/]/)
      .map((part) => part.trim())
      .filter((part) => part.length > 0);
    const firstLabel =
      firstAttachment.displayName?.trim() ||
      pathParts[pathParts.length - 1] ||
      firstAttachment.path.trim();
    return attachments.length > 1 ? `${firstLabel} +${attachments.length - 1}` : firstLabel;
  }

  return t('managerBar.modal.singlePrompt');
}

function formatQueuedPromptRunAt(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return t('managerBar.queue.cooldown');
  }

  const now = new Date();
  const sameDay =
    date.getFullYear() === now.getFullYear() &&
    date.getMonth() === now.getMonth() &&
    date.getDate() === now.getDate();

  return sameDay
    ? date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' })
    : date.toLocaleString([], {
        month: 'short',
        day: 'numeric',
        hour: '2-digit',
        minute: '2-digit',
      });
}

function usesTurnQueueForSession(sessionId: string): boolean {
  return $sessions.get()[sessionId]?.appServerControlOnly === true;
}

function openActionModal(existing?: NormalizedManagerButton): void {
  if (!ensureManagerActionModalElements()) {
    return;
  }

  if (!hasManagerActionModalElements()) {
    return;
  }

  const action = existing ?? createDefaultManagerButton();
  editingActionId = existing?.id ?? null;
  clearModalError();

  populateManagerActionModal(action, existing);
  renderPromptEditors(
    action.actionType === 'chain' ? Math.max(action.prompts.length, 1) : 1,
    action.prompts,
    action.actionType,
  );
  renderScheduleEditors(action.trigger.schedule);
  syncModalSections();

  if (!releaseBackButtonLayer) {
    releaseBackButtonLayer = registerBackButtonLayer(closeActionModal);
  }

  const activeModalEl = modalEl;
  if (!activeModalEl) {
    return;
  }

  activeModalEl.classList.remove('hidden');
  const modalBody = activeModalEl.querySelector<HTMLElement>('.manager-action-modal-body');
  if (modalBody) {
    modalBody.scrollTop = 0;
  }
  focusPrimaryPrompt();
}

function hasManagerActionModalElements(): boolean {
  return !!(
    modalEl &&
    modalTitleEl &&
    labelInput &&
    typeSelect &&
    triggerSelect &&
    repeatCountInput &&
    repeatEveryValueInput &&
    repeatEveryUnitSelect
  );
}

function populateManagerActionModal(
  action: NormalizedManagerButton,
  existing?: NormalizedManagerButton,
): void {
  if (
    !modalTitleEl ||
    !labelInput ||
    !typeSelect ||
    !triggerSelect ||
    !repeatCountInput ||
    !repeatEveryValueInput ||
    !repeatEveryUnitSelect
  ) {
    return;
  }

  modalTitleEl.textContent = existing
    ? t('managerBar.modal.editTitle')
    : t('managerBar.modal.title');
  labelInput.value = action.label;
  typeSelect.value = action.actionType;
  triggerSelect.value = action.trigger.kind;
  repeatCountInput.value = String(action.trigger.repeatCount);
  repeatEveryValueInput.value = String(action.trigger.repeatEveryValue);
  repeatEveryUnitSelect.value = action.trigger.repeatEveryUnit;
}

function closeActionModal(): void {
  releaseBackButtonLayer?.();
  releaseBackButtonLayer = null;
  editingActionId = null;
  modalEl?.classList.add('hidden');
  clearModalError();
}

function saveModalAction(): void {
  const settings = $currentSettings.get();
  if (!settings) return;

  const actionType = getModalActionType();
  const prompts = readPromptValues();
  if (!hasValidManagerActionPrompts(prompts)) {
    showModalError(t('managerBar.modal.errorPromptRequired'));
    return;
  }

  const triggerKind = getModalTriggerKind();
  const schedule = readScheduleValues();
  if (triggerKind === 'schedule' && schedule.length === 0) {
    showModalError(t('managerBar.modal.errorScheduleRequired'));
    return;
  }

  const action = buildManagerActionFromModal(actionType, prompts, triggerKind, schedule);

  const currentButtons = normalizeManagerBarButtons(
    settings.managerBarButtons as unknown as ManagerButton[],
  );
  saveButtons(upsertManagerAction(currentButtons, action));
  closeActionModal();
}

function hasValidManagerActionPrompts(prompts: string[]): boolean {
  return prompts.length > 0 && prompts.some((prompt) => prompt.trim().length > 0);
}

function buildManagerActionFromModal(
  actionType: ManagerActionType,
  prompts: string[],
  triggerKind: ManagerTriggerKind,
  schedule: ManagerBarScheduleEntry[],
): NormalizedManagerButton {
  return normalizeManagerBarButton({
    id: editingActionId ?? generateActionId(),
    label: labelInput?.value ?? '',
    text: prompts[0] ?? '',
    actionType,
    prompts,
    trigger: {
      kind: triggerKind,
      repeatCount: Number.parseInt(repeatCountInput?.value ?? '1', 10),
      repeatEveryValue: Number.parseInt(repeatEveryValueInput?.value ?? '1', 10),
      repeatEveryUnit: (repeatEveryUnitSelect?.value ?? 'minutes') as ManagerRepeatUnit,
      schedule,
    },
  });
}

function upsertManagerAction(
  currentButtons: NormalizedManagerButton[],
  action: NormalizedManagerButton,
): NormalizedManagerButton[] {
  const nextButtons = [...currentButtons];
  const index = editingActionId
    ? nextButtons.findIndex((button) => button.id === editingActionId)
    : -1;
  if (index >= 0) {
    nextButtons[index] = action;
  } else {
    nextButtons.push(action);
  }
  return nextButtons;
}

function renderPromptEditors(count: number, values: string[], actionType: ManagerActionType): void {
  if (!promptsContainer || !addPromptBtn) return;

  promptsContainer.innerHTML = '';
  addPromptBtn.classList.toggle('hidden', actionType !== 'chain');

  const rows = Math.max(count, 1);
  for (let index = 0; index < rows; index += 1) {
    const row = document.createElement('div');
    row.className = `manager-action-prompt-row manager-action-prompt-row-${actionType}`;

    const header = document.createElement('div');
    header.className = 'manager-action-prompt-header';

    const label = document.createElement('span');
    label.className = 'manager-action-prompt-label';
    label.textContent =
      actionType === 'chain'
        ? `${t('managerBar.modal.chainPrompt')} ${index + 1}`
        : t('managerBar.modal.singlePrompt');
    header.appendChild(label);

    if (actionType === 'chain' && rows > 1) {
      const removeBtn = document.createElement('button');
      removeBtn.type = 'button';
      removeBtn.className = 'btn-secondary manager-action-prompt-remove';
      removeBtn.dataset.index = String(index);
      removeBtn.textContent = t('managerBar.modal.removePrompt');
      header.appendChild(removeBtn);
    }

    const textarea = document.createElement('textarea');
    textarea.className = 'manager-action-prompt-input';
    textarea.rows = actionType === 'chain' ? 3 : 7;
    textarea.value = values[index] ?? '';
    textarea.placeholder = t('managerBar.modal.promptPlaceholder');

    row.appendChild(header);
    row.appendChild(textarea);
    promptsContainer.appendChild(row);
  }
}

function renderScheduleEditors(schedule: ManagerBarScheduleEntry[]): void {
  const container = scheduleContainer;
  if (!container) return;

  const rows = schedule.length > 0 ? schedule : [{ timeOfDay: '09:00', repeat: 'daily' }];
  container.innerHTML = '';

  rows.forEach((entry, index) => {
    const row = document.createElement('div');
    row.className = 'manager-action-schedule-row';

    const timeInput = document.createElement('input');
    timeInput.type = 'time';
    timeInput.className = 'manager-action-schedule-time';
    timeInput.value = entry.timeOfDay;

    const repeatSelect = document.createElement('select');
    repeatSelect.className = 'manager-action-schedule-repeat';
    repeatSelect.innerHTML = [
      { value: 'daily', label: t('managerBar.scheduleRepeat.daily') },
      { value: 'weekdays', label: t('managerBar.scheduleRepeat.weekdays') },
      { value: 'weekends', label: t('managerBar.scheduleRepeat.weekends') },
    ]
      .map(
        (option) =>
          `<option value="${option.value}" ${option.value === entry.repeat ? 'selected' : ''}>${escapeHtml(option.label)}</option>`,
      )
      .join('');

    const removeBtn = document.createElement('button');
    removeBtn.type = 'button';
    removeBtn.className = 'btn-secondary manager-action-schedule-remove';
    removeBtn.dataset.index = String(index);
    removeBtn.textContent = t('managerBar.modal.removeSchedule');

    row.appendChild(timeInput);
    row.appendChild(repeatSelect);
    row.appendChild(removeBtn);
    container.appendChild(row);
  });
}

function syncModalSections(): void {
  const actionType = getModalActionType();
  const triggerKind = getModalTriggerKind();

  if (promptsTitleEl) {
    promptsTitleEl.textContent = t(
      actionType === 'chain'
        ? 'managerBar.modal.promptSectionChain'
        : 'managerBar.modal.promptSectionSingle',
    );
  }
  if (promptsCopyEl) {
    promptsCopyEl.textContent = t(
      actionType === 'chain'
        ? 'managerBar.modal.promptSectionChainCopy'
        : 'managerBar.modal.promptSectionSingleCopy',
    );
  }
  if (typeDescriptionEl) {
    typeDescriptionEl.textContent = t(
      actionType === 'chain'
        ? 'managerBar.modal.typeChainDescription'
        : 'managerBar.modal.typeSingleDescription',
    );
  }
  if (triggerDescriptionEl) {
    triggerDescriptionEl.textContent = t(`managerBar.modal.triggerDescription.${triggerKind}`);
  }

  cooldownHintEl?.classList.toggle('hidden', triggerKind !== 'onCooldown');
  chainHintEl?.classList.toggle('hidden', actionType !== 'chain');
  triggerDetailsEl?.classList.toggle('hidden', triggerKind === 'fireAndForget');
  repeatCountGroupEl?.classList.toggle('hidden', triggerKind !== 'repeatCount');
  repeatIntervalGroupEl?.classList.toggle('hidden', triggerKind !== 'repeatInterval');
  scheduleGroupEl?.classList.toggle('hidden', triggerKind !== 'schedule');
}

function readPromptValues(): string[] {
  if (!promptsContainer) return [];
  return [...promptsContainer.querySelectorAll<HTMLTextAreaElement>('.manager-action-prompt-input')]
    .map((input) => input.value)
    .filter((_prompt, index) => getModalActionType() === 'chain' || index === 0);
}

function readScheduleValues(): ManagerBarScheduleEntry[] {
  if (!scheduleContainer) return [];
  const rows = [...scheduleContainer.querySelectorAll<HTMLElement>('.manager-action-schedule-row')];
  return rows
    .map((row) => {
      const timeInput = row.querySelector<HTMLInputElement>('.manager-action-schedule-time');
      const repeatSelect = row.querySelector<HTMLSelectElement>('.manager-action-schedule-repeat');
      if (!timeInput || !repeatSelect || !timeInput.value) return null;
      return {
        timeOfDay: timeInput.value,
        repeat: repeatSelect.value as ManagerScheduleRepeat,
      };
    })
    .filter((entry): entry is ManagerBarScheduleEntry => entry !== null);
}

function deleteButton(id: string): void {
  const settings = $currentSettings.get();
  if (!settings) return;

  const nextButtons = normalizeManagerBarButtons(
    settings.managerBarButtons as unknown as ManagerButton[],
  ).filter((button) => button.id !== id);
  saveButtons(nextButtons);
}

function saveButtons(buttons: NormalizedManagerButton[]): void {
  const settings = $currentSettings.get();
  if (!settings) return;

  $currentSettings.set({ ...settings, managerBarButtons: buttons });

  updateSettings({ ...settings, managerBarButtons: buttons } as Parameters<
    typeof updateSettings
  >[0])
    .then(({ response }) => {
      if (!response.ok) {
        log.error(() => `Failed to save manager bar buttons: ${response.status}`);
      }
    })
    .catch((error: unknown) => {
      log.error(() => `Failed to save manager bar buttons: ${String(error)}`);
    });
}

function runButton(id: string): void {
  const action = renderedButtons.find((button) => button.id === id);
  const sessionId = $activeSessionId.get();
  if (!action || !sessionId) return;

  if (isImmediateManagerAction(action) && !usesTurnQueueForSession(sessionId)) {
    sendCommand(sessionId, action.prompts[0] ?? '');
    return;
  }

  void enqueueAction(sessionId, action);
}

async function enqueueAction(sessionId: string, action: NormalizedManagerButton): Promise<void> {
  const now = Date.now();
  pruneExpiredEnqueueGuards(now);
  const enqueueGuardKey = buildEnqueueGuardKey(sessionId, action);
  const blockedUntil = pendingEnqueueGuards.get(enqueueGuardKey) ?? 0;
  if (blockedUntil > now) {
    return;
  }

  pendingEnqueueGuards.set(enqueueGuardKey, now + QUEUE_ENQUEUE_DEDUP_WINDOW_MS);

  try {
    await enqueueCommandBayAction(sessionId, action);
  } catch (error) {
    log.error(() => `Failed to enqueue manager bar action: ${String(error)}`);
  }
}

function buildEnqueueGuardKey(sessionId: string, action: NormalizedManagerButton): string {
  return [
    sessionId,
    action.id,
    action.actionType,
    action.trigger.kind,
    action.prompts.join('\u001f'),
  ].join('\u001d');
}

function pruneExpiredEnqueueGuards(now: number): void {
  for (const [key, expiresAt] of pendingEnqueueGuards.entries()) {
    if (expiresAt <= now) {
      pendingEnqueueGuards.delete(key);
    }
  }
}

async function removeQueueEntry(queueId: string): Promise<void> {
  if (pendingQueueRemovals.has(queueId)) {
    return;
  }

  pendingQueueRemovals.add(queueId);
  renderQueue();

  try {
    await removeCommandBayQueueEntry(queueId);
    queueEntries = queueEntries.filter((entry) => entry.queueId !== queueId);
    pendingQueueRemovals.delete(queueId);
    renderQueue();
  } catch (error) {
    pendingQueueRemovals.delete(queueId);
    renderQueue();
    log.error(() => `Failed to dequeue manager bar action: ${String(error)}`);
  }
}

function getModalActionType(): ManagerActionType {
  return typeSelect?.value === 'chain' ? 'chain' : 'single';
}

function getModalTriggerKind(): ManagerTriggerKind {
  const trigger = triggerSelect?.value as ManagerTriggerKind | undefined;
  if (
    trigger === 'onCooldown' ||
    trigger === 'repeatCount' ||
    trigger === 'repeatInterval' ||
    trigger === 'schedule'
  ) {
    return trigger;
  }
  return 'fireAndForget';
}

function showModalError(message: string): void {
  if (!modalErrorEl) return;
  modalErrorEl.textContent = message;
  modalErrorEl.classList.remove('hidden');
}

function clearModalError(): void {
  if (!modalErrorEl) return;
  modalErrorEl.textContent = '';
  modalErrorEl.classList.add('hidden');
}

function generateActionId(): string {
  if (typeof crypto !== 'undefined' && typeof crypto.randomUUID === 'function') {
    return crypto.randomUUID();
  }
  return `manager-action-${Date.now()}`;
}

function resolveEventElement(target: EventTarget | null): Element | null {
  if (target instanceof Element) {
    return target;
  }

  if (target instanceof Node) {
    return target.parentElement;
  }

  return null;
}

function escapeHtml(value: string): string {
  const div = document.createElement('div');
  div.textContent = value;
  return div.innerHTML;
}

function focusPrimaryPrompt(index: number = 0): void {
  window.requestAnimationFrame(() => {
    const prompts = promptsContainer?.querySelectorAll<HTMLTextAreaElement>(
      '.manager-action-prompt-input',
    );
    const prompt = prompts?.[index] ?? prompts?.[0];
    if (!prompt) return;

    try {
      prompt.focus({ preventScroll: true });
    } catch {
      prompt.focus();
    }
    const cursor = prompt.value.length;
    prompt.setSelectionRange(cursor, cursor);
  });
}

function focusNewestScheduleTime(): void {
  window.requestAnimationFrame(() => {
    const times = scheduleContainer?.querySelectorAll<HTMLInputElement>(
      '.manager-action-schedule-time',
    );
    const input = times?.[times.length - 1];
    if (!input) return;

    input.focus();
  });
}

/* eslint-enable max-lines */

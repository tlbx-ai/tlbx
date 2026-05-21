import type {
  LaunchEntry,
  ShellType,
  Session,
  SpaceSummaryDto,
  SpaceWorkspaceDto,
} from '../../api/types';
import { t } from '../i18n';
import { getLaunchableHubMachines } from '../hub/runtime';
import { invalidateSidebarSpacesTree } from '../sidebar/spacesTreeSidebar';
import { showAlert } from '../../utils/dialog';
import { showImportSpaceDialog } from './spacesDialogs';
import { launchSpaceWorkspace, type SpaceSurface } from './runtime';
import {
  fetchHubSpaces,
  fetchLocalSpaces,
  importHubSpace,
  importLocalSpace,
  updateHubSpace,
  updateLocalSpace,
} from './spacesApi';

interface SpaceTargetSection {
  id: string;
  label: string;
  machineId: string | null;
  spaces: SpaceSummaryDto[];
}

interface SpacesDropdownOptions {
  resolveLaunchDimensions: () => Promise<{ cols: number; rows: number }>;
  resolveShell: () => ShellType | null;
  onOpenLocalSession: (session: Session, surface: SpaceSurface) => void | Promise<void>;
  onOpenRemoteSession: (
    machineId: string,
    sessionId: string,
    surface: SpaceSurface,
  ) => void | Promise<void>;
  onSelectLocalSession: (sessionId: string) => void;
  onSelectRemoteSession: (machineId: string, sessionId: string) => void;
  onLaunchRecent: (machineId: string | null, entry: LaunchEntry) => void;
}

let dropdownEl: HTMLElement | null = null;
let targetPickerEl: HTMLElement | null = null;
let isOpen = false;
let activeLoadToken = 0;
let sections: SpaceTargetSection[] = [];

export function initSpacesDropdown(_options: SpacesDropdownOptions): void {
  createDropdownElement();
}

export function toggleSpacesDropdown(): void {
  if (isOpen) {
    closeSpacesDropdown();
  } else {
    openSpacesDropdown();
  }
}

export function closeSpacesDropdown(): void {
  if (!dropdownEl) {
    return;
  }

  dropdownEl.classList.remove('visible');
  closeTargetPicker();
  isOpen = false;
  document.removeEventListener('click', handleOutsideClick);
}

export function openSpacesDropdown(): void {
  if (!dropdownEl) {
    return;
  }

  void refreshSpacesDropdown().then(() => {
    if (!dropdownEl) {
      return;
    }

    positionDropdown();
    dropdownEl.classList.add('visible');
    isOpen = true;
    window.setTimeout(() => {
      document.addEventListener('click', handleOutsideClick);
    }, 0);
  });
}

async function refreshSpacesDropdown(): Promise<void> {
  if (!dropdownEl) {
    return;
  }

  const currentToken = ++activeLoadToken;
  const nextSections = await loadSections();
  if (currentToken !== activeLoadToken) {
    return;
  }

  sections = nextSections;
  renderDropdownContent();
}

async function loadSections(): Promise<SpaceTargetSection[]> {
  const machines = getLaunchableHubMachines();
  const results: SpaceTargetSection[] = [];

  try {
    results.push({
      id: 'local',
      label: t('sessionLauncher.localTargetTitle'),
      machineId: null,
      spaces: sortSpaces(await fetchLocalSpaces({ includeWorkspaces: false })),
    });
  } catch {
    results.push({
      id: 'local',
      label: t('sessionLauncher.localTargetTitle'),
      machineId: null,
      spaces: [],
    });
  }

  const remoteSections = await Promise.all(
    machines.map(async (machine) => {
      try {
        return {
          id: machine.machine.id,
          label: machine.machine.name,
          machineId: machine.machine.id,
          spaces: sortSpaces(
            await fetchHubSpaces(machine.machine.id, { includeWorkspaces: false }),
          ),
        } satisfies SpaceTargetSection;
      } catch {
        return {
          id: machine.machine.id,
          label: machine.machine.name,
          machineId: machine.machine.id,
          spaces: [],
        } satisfies SpaceTargetSection;
      }
    }),
  );

  return [...results, ...remoteSections];
}

function createDropdownElement(): void {
  dropdownEl = document.createElement('div');
  dropdownEl.className = 'history-dropdown spaces-dropdown';
  dropdownEl.innerHTML = `
    <div class="history-dropdown-header spaces-dropdown-header">
      <span>${escapeHtml(t('spaces.title'))}</span>
      <button type="button" class="spaces-add-btn" data-action="add-space">
        ${escapeHtml(t('spaces.addNew'))}
      </button>
    </div>
    <div class="history-dropdown-content"></div>
    <div class="history-dropdown-empty hidden">${escapeHtml(t('spaces.empty'))}</div>
  `;

  dropdownEl.addEventListener('click', (event) => {
    const target = event.target as HTMLElement | null;
    if (!target) {
      return;
    }

    const actionEl = target.closest<HTMLElement>('[data-action]');
    if (!actionEl) {
      return;
    }

    const action = actionEl.dataset.action;
    if (!action) {
      return;
    }

    const machineId = normalizeMachineId(actionEl.dataset.machineId);
    const spaceId = actionEl.dataset.spaceId ?? null;
    void handleAction({
      action,
      machineId,
      spaceId,
      trigger: actionEl,
    });
  });

  const sidebar = document.getElementById('sidebar');
  if (sidebar) {
    sidebar.appendChild(dropdownEl);
  }

  targetPickerEl = document.createElement('div');
  targetPickerEl.className = 'manager-bar-action-popover spaces-target-picker hidden';
  targetPickerEl.addEventListener('click', (event) => {
    const target = event.target as HTMLElement | null;
    const machineId = normalizeMachineId(
      target?.closest<HTMLElement>('[data-machine-id]')?.dataset.machineId,
    );
    closeTargetPicker();
    void promptAndImportSpace(machineId);
  });
  document.body.appendChild(targetPickerEl);
}

async function handleAction(args: {
  action: string;
  machineId: string | null;
  spaceId: string | null;
  trigger: HTMLElement;
}): Promise<void> {
  switch (args.action) {
    case 'open-space':
      if (args.spaceId) {
        await openSpace(args.machineId, args.spaceId);
      }
      return;
    case 'add-space':
      await openAddTargetPicker(args.trigger);
      return;
    case 'toggle-pin':
      if (args.spaceId) {
        await toggleSpacePinned(args.machineId, args.spaceId);
      }
      return;
  }
}

async function promptAndImportSpace(machineId: string | null): Promise<void> {
  const request = await showImportSpaceDialog({ machineId });
  if (!request) {
    return;
  }

  try {
    if (machineId) {
      await importHubSpace(machineId, request);
    } else {
      await importLocalSpace(request);
    }

    await refreshSpacesDropdown();
    invalidateSidebarSpacesTree();
  } catch (error) {
    await showAlert(error instanceof Error ? error.message : String(error), {
      title: t('spaces.importFailed'),
    });
  }
}

async function toggleSpacePinned(machineId: string | null, spaceId: string): Promise<void> {
  const space = findSpace(machineId, spaceId);
  if (!space) {
    return;
  }

  try {
    if (machineId) {
      await updateHubSpace(machineId, space.id, { isPinned: !space.isPinned });
    } else {
      await updateLocalSpace(space.id, { isPinned: !space.isPinned });
    }

    await refreshSpacesDropdown();
    invalidateSidebarSpacesTree();
  } catch (error) {
    await showAlert(error instanceof Error ? error.message : String(error), {
      title: space.isPinned ? t('spaces.unpinSpace') : t('spaces.pinSpace'),
    });
  }
}

async function openSpace(machineId: string | null, spaceId: string): Promise<void> {
  const space = findSpace(machineId, spaceId);
  if (!space) {
    return;
  }

  const workspace = resolvePrimaryWorkspace(space);
  const launched = await launchSpaceWorkspace(machineId, space.id, workspace, 'terminal');
  if (launched) {
    closeSpacesDropdown();
    invalidateSidebarSpacesTree();
  }
}

function positionDropdown(): void {
  if (!dropdownEl) {
    return;
  }

  const trigger = document.getElementById('btn-spaces');
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
  if (!dropdownEl) {
    return;
  }

  const content = dropdownEl.querySelector('.history-dropdown-content');
  const empty = dropdownEl.querySelector('.history-dropdown-empty');
  if (!(content instanceof HTMLElement) || !(empty instanceof HTMLElement)) {
    return;
  }

  content.innerHTML = '';

  const visibleSections = sections.filter((section) => section.spaces.length > 0);
  const totalSpaces = visibleSections.reduce((count, section) => count + section.spaces.length, 0);
  empty.classList.toggle('hidden', totalSpaces > 0);

  for (const section of visibleSections) {
    const sectionEl = document.createElement('section');
    sectionEl.className = 'spaces-section';
    sectionEl.innerHTML = `
      <div class="history-section-header spaces-section-header">
        <span>${escapeHtml(section.label)}</span>
      </div>
    `;

    const list = document.createElement('div');
    list.className = 'spaces-section-list';
    for (const space of section.spaces) {
      list.appendChild(createSpaceRow(space, section.machineId));
    }

    sectionEl.appendChild(list);
    content.appendChild(sectionEl);
  }
}

function createSpaceRow(space: SpaceSummaryDto, machineId: string | null): HTMLElement {
  const row = document.createElement('div');
  row.className = `history-item spaces-space-row${space.isPinned ? ' pinned' : ''}`;
  row.title = buildSpaceRowTitle(space);

  const pinButton = document.createElement('button');
  pinButton.type = 'button';
  pinButton.className = `history-item-star${space.isPinned ? ' starred' : ''}`;
  pinButton.dataset.action = 'toggle-pin';
  pinButton.dataset.spaceId = space.id;
  pinButton.dataset.machineId = machineId ?? '';
  pinButton.title = space.isPinned ? t('spaces.unpinSpace') : t('spaces.pinSpace');
  pinButton.setAttribute('aria-label', pinButton.title);
  pinButton.setAttribute('aria-pressed', space.isPinned ? 'true' : 'false');
  pinButton.textContent = space.isPinned ? '★' : '☆';
  row.appendChild(pinButton);

  const launchButton = document.createElement('button');
  launchButton.type = 'button';
  launchButton.className = 'spaces-space-launch';
  launchButton.dataset.action = 'open-space';
  launchButton.dataset.spaceId = space.id;
  launchButton.dataset.machineId = machineId ?? '';
  launchButton.title = buildSpaceRowTitle(space);

  const info = document.createElement('div');
  info.className = 'history-item-info spaces-space-info';

  const path = document.createElement('span');
  path.className = 'history-item-text spaces-space-path';
  path.textContent = space.rootPath;
  info.appendChild(path);

  launchButton.appendChild(info);
  row.appendChild(launchButton);
  return row;
}

function sortSpaces(spaces: SpaceSummaryDto[]): SpaceSummaryDto[] {
  return [...spaces].sort((left, right) => {
    if (left.isPinned !== right.isPinned) {
      return left.isPinned ? -1 : 1;
    }

    return left.rootPath.localeCompare(right.rootPath);
  });
}

function findSpace(machineId: string | null, spaceId: string): SpaceSummaryDto | undefined {
  return sections
    .find((section) => section.machineId === machineId)
    ?.spaces.find((space) => space.id === spaceId);
}

function normalizeMachineId(value: string | null | undefined): string | null {
  const trimmed = value?.trim();
  return trimmed ? trimmed : null;
}

function buildSpaceRowTitle(space: SpaceSummaryDto): string {
  if (space.displayName.trim() && space.displayName.trim() !== space.rootPath.trim()) {
    return `${space.displayName}\n${space.rootPath}`;
  }

  return space.rootPath;
}

function resolvePrimaryWorkspace(space: SpaceSummaryDto) {
  return (
    space.workspaces.find((workspace) => workspace.key === space.primaryWorkspaceKey) ??
    space.workspaces.find((workspace) => workspace.path === space.rootPath) ??
    space.workspaces.find((workspace) => workspace.isMain) ??
    space.workspaces[0] ??
    buildFallbackWorkspace(space)
  );
}

function buildFallbackWorkspace(space: SpaceSummaryDto): SpaceWorkspaceDto {
  return {
    key: space.primaryWorkspaceKey ?? buildWorkspaceKey(space.rootPath),
    displayName: 'Main',
    path: space.rootPath,
    kind: space.kind === 'git' ? 'worktree' : 'plain',
    branch: null,
    head: null,
    isMain: true,
    isDetached: false,
    locked: false,
    prunable: false,
    changeCount: 0,
    hasChanges: false,
    hasActiveAiSession: false,
    activeSessions: [],
  };
}

function buildWorkspaceKey(path: string): string {
  const normalized = path.trim().replace(/\\/g, '/').replace(/\/+$/, '').toLowerCase();
  const bytes = new TextEncoder().encode(normalized);
  let binary = '';
  for (const byte of bytes) {
    binary += String.fromCharCode(byte);
  }

  return `ws_${btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/g, '')}`;
}

async function openAddTargetPicker(trigger: HTMLElement): Promise<void> {
  if (!targetPickerEl) {
    await promptAndImportSpace(null);
    return;
  }

  const machines = getLaunchableHubMachines();
  if (machines.length === 0) {
    await promptAndImportSpace(null);
    return;
  }

  targetPickerEl.replaceChildren();

  const localButton = document.createElement('button');
  localButton.type = 'button';
  localButton.className = 'manager-bar-action-popover-btn';
  localButton.dataset.machineId = '';
  localButton.textContent = t('sessionLauncher.localTargetTitle');
  targetPickerEl.appendChild(localButton);

  for (const machine of machines) {
    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'manager-bar-action-popover-btn';
    button.dataset.machineId = machine.machine.id;
    button.textContent = machine.machine.name;
    targetPickerEl.appendChild(button);
  }

  targetPickerEl.classList.remove('hidden');

  const triggerRect = trigger.getBoundingClientRect();
  const popoverRect = targetPickerEl.getBoundingClientRect();
  const gap = 6;
  const viewportPadding = 8;
  const top = Math.min(
    Math.max(viewportPadding, triggerRect.bottom + gap),
    window.innerHeight - viewportPadding - popoverRect.height,
  );
  const left = Math.min(
    Math.max(viewportPadding, triggerRect.right - popoverRect.width),
    window.innerWidth - viewportPadding - popoverRect.width,
  );
  targetPickerEl.style.top = `${Math.round(top)}px`;
  targetPickerEl.style.left = `${Math.round(left)}px`;
}

function closeTargetPicker(): void {
  targetPickerEl?.classList.add('hidden');
}

function handleOutsideClick(event: MouseEvent): void {
  if (!dropdownEl) {
    return;
  }

  const target = event.target as HTMLElement | null;
  if (!target) {
    return;
  }

  if (
    !dropdownEl.contains(target) &&
    !target.closest('#btn-spaces') &&
    !target.closest('.spaces-target-picker')
  ) {
    closeSpacesDropdown();
  }
}

function escapeHtml(value: string): string {
  return value
    .replace(/&/g, '&amp;')
    .replace(/</g, '&lt;')
    .replace(/>/g, '&gt;')
    .replace(/"/g, '&quot;');
}

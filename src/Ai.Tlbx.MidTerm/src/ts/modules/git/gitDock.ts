/**
 * Git Dock
 *
 * Dock logic for snapping the git panel to the right sidebar.
 */

import {
  $gitPanelDocked,
  $activeSessionId,
  $fileViewerDocked,
  $dockedFilePath,
  $commandsPanelDocked,
} from '../../stores';
import { handleDockLayoutChange } from '../terminal/scaling';
import { setActionButtonActive } from '../sessionTabs';
import {
  refreshGitPanel,
  renderGitPanelInto,
  showCommitInGitPanel,
  suspendGitPanel,
} from './gitPanel';
import { subscribeToSession } from './gitChannel';
import { closeCommandsDock } from '../commands/dock';
import { adjustInnerDockPositions, updateAllDockMargins } from '../web';
import { createLogger } from '../logging';

const log = createLogger('gitDock');

const DOCK_MIN_WIDTH = 250;
const DOCK_MAX_WIDTH = 600;
const DOCK_WIDTH_KEY = 'mt-git-dock-width';

let activeUnsub: (() => void) | null = null;
let currentDockSessionId: string | null = null;
let currentDockRepoRoot: string | undefined;

function closeFileViewerDockIfOpen(): void {
  if (!$fileViewerDocked.get()) return;
  $fileViewerDocked.set(false);
  $dockedFilePath.set(null);
  const fvDock = document.getElementById('file-viewer-dock');
  if (fvDock) fvDock.classList.add('hidden');
  document.getElementById('app')?.classList.remove('file-viewer-docked');
}

function closeCommandsDockIfOpen(): void {
  if ($commandsPanelDocked.get()) {
    closeCommandsDock();
  }
}

export function toggleGitDock(sessionId: string, repoRoot?: string): void {
  if ($gitPanelDocked.get()) {
    if (currentDockSessionId === sessionId && currentDockRepoRoot === repoRoot) {
      closeGitDock();
    } else {
      openGitDock(sessionId, repoRoot);
    }
  } else {
    openGitDock(sessionId, repoRoot);
  }
}

export function openGitDock(sessionId: string, repoRoot?: string): void {
  closeFileViewerDockIfOpen();
  closeCommandsDockIfOpen();

  $gitPanelDocked.set(true);
  setActionButtonActive('git', true);
  currentDockSessionId = sessionId;
  currentDockRepoRoot = repoRoot;

  const dockPanel = document.getElementById('git-dock');
  const app = document.getElementById('app');
  if (!dockPanel) return;

  dockPanel.classList.remove('hidden');
  app?.classList.add('git-docked');

  const savedWidth = localStorage.getItem(DOCK_WIDTH_KEY);
  if (savedWidth) {
    const w = parseInt(savedWidth, 10);
    if (w >= DOCK_MIN_WIDTH && w <= DOCK_MAX_WIDTH) {
      dockPanel.style.width = `${w}px`;
    }
  }

  const body = dockPanel.querySelector<HTMLElement>('.git-dock-body');
  if (body) {
    subscribeToSession(sessionId);
    void renderGitPanelInto(body, sessionId, repoRoot);
  }

  adjustInnerDockPositions();
  updateAllDockMargins();
  handleDockLayoutChange();

  activeUnsub?.();
  activeUnsub = $activeSessionId.subscribe((newId) => {
    if (!$gitPanelDocked.get() || !newId || newId === currentDockSessionId) return;
    currentDockSessionId = newId;
    currentDockRepoRoot = undefined;
    const dockBody = document
      .getElementById('git-dock')
      ?.querySelector('.git-dock-body') as HTMLElement | null;
    if (dockBody) {
      subscribeToSession(newId);
      void renderGitPanelInto(dockBody, newId);
    }
  });

  log.info(() => 'Git dock opened');
}

export async function openGitCommitDock(
  sessionId: string,
  hash: string,
  repoRoot?: string,
): Promise<void> {
  openGitDock(sessionId, repoRoot);
  await showCommitInGitPanel(sessionId, hash, repoRoot);
}

export function closeGitDock(): void {
  activeUnsub?.();
  activeUnsub = null;
  currentDockSessionId = null;
  currentDockRepoRoot = undefined;

  $gitPanelDocked.set(false);
  setActionButtonActive('git', false);

  const dockPanel = document.getElementById('git-dock');
  const app = document.getElementById('app');

  if (dockPanel) {
    const body = dockPanel.querySelector<HTMLElement>('.git-dock-body');
    if (body) suspendGitPanel(body);
    dockPanel.classList.add('hidden');
    dockPanel.style.width = '';
  }
  app?.classList.remove('git-docked');

  adjustInnerDockPositions();
  updateAllDockMargins();
  handleDockLayoutChange();

  log.info(() => 'Git dock closed');
}

export function setupGitDockResize(): void {
  const dockPanel = document.getElementById('git-dock');
  const gripEl = dockPanel?.querySelector('.git-dock-resize-grip') as HTMLElement | null;
  const refreshBtn = document.getElementById('git-dock-refresh');
  const closeBtn = document.getElementById('git-dock-close');
  if (!dockPanel || !gripEl) return;

  const panel = dockPanel;
  const grip = gripEl;
  let isResizing = false;
  let startX = 0;
  let startWidth = 0;

  function beginResize(clientX: number): void {
    isResizing = true;
    startX = clientX;
    startWidth = panel.offsetWidth;
    grip.classList.add('active');
    document.body.style.cursor = 'ew-resize';
    document.body.style.userSelect = 'none';
  }

  function updateResize(clientX: number): void {
    if (!isResizing) return;
    const delta = startX - clientX;
    const newWidth = Math.max(DOCK_MIN_WIDTH, Math.min(DOCK_MAX_WIDTH, startWidth + delta));
    panel.style.width = `${newWidth}px`;
    adjustInnerDockPositions();
    updateAllDockMargins();
  }

  function endResize(): void {
    if (!isResizing) return;
    isResizing = false;
    grip.classList.remove('active');
    document.body.style.cursor = '';
    document.body.style.userSelect = '';
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

  refreshBtn?.addEventListener('click', () => {
    if (!$gitPanelDocked.get() || !currentDockSessionId) return;
    void refreshGitPanel(currentDockSessionId, currentDockRepoRoot);
  });

  closeBtn?.addEventListener('click', () => {
    closeGitDock();
  });
}

import type {
  SpaceCreateWorktreeRequest,
  SpaceImportRequest,
  SpaceSummaryDto,
} from '../../api/types';
import { $currentSettings } from '../../stores';
import { t } from '../i18n';
import { registerBackButtonLayer } from '../navigation/backButtonGuard';
import { getLaunchableHubMachines } from '../hub/runtime';
import { showAlert, showTextPrompt } from '../../utils/dialog';
import { escapeHtml } from '../../utils/dom';

interface LauncherDirectoryEntry {
  name: string;
  fullPath: string;
  isRoot: boolean;
}

interface LauncherDirectoryListResponse {
  path: string;
  parentPath: string | null;
  entries: LauncherDirectoryEntry[];
}

interface LauncherPathResponse {
  path: string;
  homePath: string;
  startPath: string;
}

interface LauncherDirectoryAccessResponse {
  path: string;
  canWrite: boolean;
}

interface LauncherDirectoryMutationResponse {
  path: string;
}

interface BrowserState {
  currentPath: string;
  pathDraft: string;
  parentPath: string | null;
  roots: LauncherDirectoryEntry[];
  entries: LauncherDirectoryEntry[];
  loading: boolean;
  loadingMessage: string | null;
  error: string | null;
  requestToken: number;
}

interface BrowserElements {
  pathInput: HTMLInputElement;
  rootsEl: HTMLElement;
  statusEl: HTMLElement;
  listEl: HTMLElement;
}

export async function showImportSpaceDialog(args: {
  machineId: string | null;
  initialPath?: string | null;
  initialLabel?: string | null;
}): Promise<SpaceImportRequest | null> {
  return new Promise((resolve) => {
    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    let releaseBackButtonLayer: (() => void) | null = null;

    const targetLabel = getTargetLabel(args.machineId);
    overlay.innerHTML = `
      <div class="modal session-launcher-modal space-dialog-modal" role="dialog" aria-modal="true" aria-labelledby="space-import-title">
        <div class="modal-content session-launcher-content space-dialog-content">
          <div class="modal-header">
            <div>
              <div class="space-dialog-kicker">${escapeHtml(targetLabel)}</div>
              <h3 id="space-import-title">${escapeHtml(t('spaces.addTitle'))}</h3>
            </div>
            <button class="modal-close" type="button" data-role="cancel" aria-label="${escapeHtml(t('dialog.cancel'))}">&times;</button>
          </div>
          <div class="modal-body space-dialog-body">
            <div class="space-dialog-form space-dialog-form-compact">
              <p class="space-dialog-hint">${escapeHtml(t('spaces.addHint'))}</p>
            </div>
            ${renderBrowserMarkup({ includeClone: true })}
          </div>
          <div class="modal-footer space-dialog-footer">
            <button type="button" class="btn-secondary" data-role="cancel">${escapeHtml(t('dialog.cancel'))}</button>
            <button type="button" class="btn-primary" data-role="confirm">${escapeHtml(t('spaces.add'))}</button>
          </div>
        </div>
      </div>
    `;

    const browser = getBrowserElements(overlay);
    if (!browser) {
      overlay.remove();
      resolve(null);
      return;
    }

    const state = createBrowserState();

    const close = (result: SpaceImportRequest | null): void => {
      document.removeEventListener('keydown', onKeyDown);
      releaseBackButtonLayer?.();
      releaseBackButtonLayer = null;
      overlay.remove();
      resolve(result);
    };

    const render = (): void => {
      renderBrowser(browser, state);
    };

    const loadDirectory = async (
      path: string,
      options?: { suppressErrors?: boolean; recordPathDraft?: boolean },
    ): Promise<boolean> => {
      const requestToken = beginLoad(state, options?.suppressErrors, render);
      try {
        const response = await fetchDirectories(args.machineId, path);
        if (requestToken !== state.requestToken) {
          return false;
        }

        state.currentPath = response.path;
        state.pathDraft = options?.recordPathDraft === false ? state.pathDraft : response.path;
        state.parentPath = response.parentPath;
        state.entries = response.entries;
        state.error = null;
        return true;
      } catch (error) {
        if (requestToken !== state.requestToken || options?.suppressErrors) {
          return false;
        }

        state.error = error instanceof Error ? error.message : String(error);
      } finally {
        endLoad(state, requestToken, render);
      }

      return false;
    };

    const loadInitial = async (): Promise<void> => {
      const requestToken = beginLoad(state, false, render);
      try {
        const [pathResponse, rootsResponse] = await Promise.all([
          fetchHomePath(args.machineId),
          fetchLauncherRoots(args.machineId),
        ]);
        if (requestToken !== state.requestToken) {
          return;
        }

        state.roots = rootsResponse.entries;
        state.currentPath = pathResponse.startPath || pathResponse.homePath || pathResponse.path;
        state.pathDraft = state.currentPath;
        state.parentPath = null;
        state.entries = [];
        const initialPath = args.initialPath?.trim() || state.currentPath;
        await loadDirectory(initialPath);
      } catch (error) {
        if (requestToken !== state.requestToken) {
          return;
        }

        state.error = error instanceof Error ? error.message : String(error);
      } finally {
        endLoad(state, requestToken, render);
      }
    };

    const onKeyDown = (event: KeyboardEvent): void => {
      if (event.key === 'Escape') {
        event.preventDefault();
        close(null);
      } else if (event.key === 'Enter' && !state.loading) {
        const target = event.target as HTMLElement | null;
        if (target?.closest('[data-role="path"]')) {
          event.preventDefault();
          void loadDirectory(browser.pathInput.value.trim());
        }
      }
    };

    const confirm = (): void => {
      if (!state.currentPath.trim()) {
        state.error = t('spaces.pathRequired');
        render();
        return;
      }

      close({
        path: state.currentPath.trim(),
        label: null,
      });
    };

    bindBrowserEvents({
      overlay,
      browser,
      state,
      machineId: args.machineId,
      render,
      loadDirectory,
      includeClone: true,
    });

    overlay.addEventListener('click', (event) => {
      const target = event.target as HTMLElement | null;
      if (!target) {
        return;
      }

      if (target === overlay) {
        close(null);
        return;
      }

      const role = target.closest<HTMLElement>('[data-role]')?.dataset.role;
      if (role === 'cancel') {
        close(null);
      } else if (role === 'confirm') {
        confirm();
      }
    });

    document.addEventListener('keydown', onKeyDown);
    document.body.appendChild(overlay);
    releaseBackButtonLayer = registerBackButtonLayer(() => {
      close(null);
    });
    render();
    void loadInitial();
    browser.pathInput.focus();
  });
}

export async function showCreateWorktreeDialog(args: {
  machineId: string | null;
  space: SpaceSummaryDto;
}): Promise<SpaceCreateWorktreeRequest | null> {
  return new Promise((resolve) => {
    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay';
    let releaseBackButtonLayer: (() => void) | null = null;

    const initialParentPath =
      getConfiguredWorktreeRootDirectory(args.machineId) ?? getParentDirectory(args.space.rootPath);
    overlay.innerHTML = `
      <div class="modal session-launcher-modal space-dialog-modal" role="dialog" aria-modal="true" aria-labelledby="space-worktree-title">
        <div class="modal-content session-launcher-content space-dialog-content">
            <div class="modal-header">
              <div>
              <div class="space-dialog-kicker">${escapeHtml(args.space.displayName)}</div>
              <h3 id="space-worktree-title">${escapeHtml(t('spaces.newWorktreeTitle'))}</h3>
              </div>
            <button class="modal-close" type="button" data-role="cancel" aria-label="${escapeHtml(t('dialog.cancel'))}">&times;</button>
          </div>
          <div class="modal-body space-dialog-body">
            <div class="space-dialog-form">
              <label class="space-dialog-field">
                <span class="space-dialog-label">${escapeHtml(t('spaces.worktreeNameLabel'))}</span>
                <input
                  type="text"
                  class="dialog-input space-dialog-input"
                  data-role="name"
                  spellcheck="false"
                  autocomplete="off"
                  placeholder="${escapeHtml(t('spaces.worktreeNamePlaceholder'))}"
                />
              </label>
              <div class="space-dialog-grid">
                <label class="space-dialog-field">
                  <span class="space-dialog-label">${escapeHtml(t('spaces.branchLabel'))}</span>
                  <input
                    type="text"
                    class="dialog-input space-dialog-input"
                    data-role="branch"
                    spellcheck="false"
                    autocomplete="off"
                    placeholder="${escapeHtml(t('spaces.branchPlaceholder'))}"
                  />
                </label>
                <label class="space-dialog-field">
                  <span class="space-dialog-label">${escapeHtml(t('spaces.folderLabel'))}</span>
                  <input
                    type="text"
                    class="dialog-input space-dialog-input"
                    data-role="folder"
                    spellcheck="false"
                    autocomplete="off"
                    placeholder="${escapeHtml(t('spaces.folderPlaceholder'))}"
                  />
                </label>
              </div>
              <label class="space-dialog-field">
                <span class="space-dialog-label">${escapeHtml(t('spaces.targetPathLabel'))}</span>
                <input
                  type="text"
                  class="dialog-input space-dialog-input"
                  data-role="target-path"
                  readonly
                />
              </label>
            </div>
            ${renderBrowserMarkup({ includeClone: false })}
          </div>
          <div class="modal-footer space-dialog-footer">
            <button type="button" class="btn-secondary" data-role="cancel">${escapeHtml(t('dialog.cancel'))}</button>
            <button type="button" class="btn-primary" data-role="confirm">${escapeHtml(t('spaces.newWorktree'))}</button>
          </div>
        </div>
      </div>
    `;

    const browser = getBrowserElements(overlay);
    const nameInput = overlay.querySelector<HTMLInputElement>('[data-role="name"]');
    const branchInput = overlay.querySelector<HTMLInputElement>('[data-role="branch"]');
    const folderInput = overlay.querySelector<HTMLInputElement>('[data-role="folder"]');
    const targetPathInput = overlay.querySelector<HTMLInputElement>('[data-role="target-path"]');
    if (!browser || !nameInput || !branchInput || !folderInput || !targetPathInput) {
      overlay.remove();
      resolve(null);
      return;
    }

    const state = createBrowserState();
    let branchDirty = false;
    let folderDirty = false;

    const close = (result: SpaceCreateWorktreeRequest | null): void => {
      document.removeEventListener('keydown', onKeyDown);
      releaseBackButtonLayer?.();
      releaseBackButtonLayer = null;
      overlay.remove();
      resolve(result);
    };

    const updateDerivedFields = (): void => {
      const slug = slugifyWorktreeName(nameInput.value);
      if (!branchDirty) {
        branchInput.value = slug;
      }

      if (!folderDirty) {
        const repoName = getPathTail(args.space.rootPath) || 'worktree';
        folderInput.value = slug ? `${repoName}-${slug}` : repoName;
      }

      targetPathInput.value = buildChildPath(state.currentPath, folderInput.value);
    };

    const render = (): void => {
      renderBrowser(browser, state);
      targetPathInput.value = buildChildPath(state.currentPath, folderInput.value);
    };

    const loadDirectory = async (
      path: string,
      options?: { suppressErrors?: boolean; recordPathDraft?: boolean },
    ): Promise<boolean> => {
      const requestToken = beginLoad(state, options?.suppressErrors, render);
      try {
        const response = await fetchDirectories(args.machineId, path);
        if (requestToken !== state.requestToken) {
          return false;
        }

        state.currentPath = response.path;
        state.pathDraft = options?.recordPathDraft === false ? state.pathDraft : response.path;
        state.parentPath = response.parentPath;
        state.entries = response.entries;
        state.error = null;
        updateDerivedFields();
        return true;
      } catch (error) {
        if (requestToken !== state.requestToken || options?.suppressErrors) {
          return false;
        }

        state.error = error instanceof Error ? error.message : String(error);
      } finally {
        endLoad(state, requestToken, render);
      }

      return false;
    };

    const loadInitial = async (): Promise<void> => {
      const requestToken = beginLoad(state, false, render);
      try {
        const [pathResponse, rootsResponse] = await Promise.all([
          fetchHomePath(args.machineId),
          fetchLauncherRoots(args.machineId),
        ]);
        if (requestToken !== state.requestToken) {
          return;
        }

        state.roots = rootsResponse.entries;
        state.currentPath = pathResponse.startPath || pathResponse.homePath || pathResponse.path;
        state.pathDraft = state.currentPath;
        state.parentPath = null;
        state.entries = [];
        await loadDirectory(initialParentPath || state.currentPath);
      } catch (error) {
        if (requestToken !== state.requestToken) {
          return;
        }

        state.error = error instanceof Error ? error.message : String(error);
      } finally {
        endLoad(state, requestToken, render);
      }
    };

    const confirm = (): void => {
      const name = nameInput.value.trim();
      const branch = branchInput.value.trim();
      const folder = folderInput.value.trim();
      const parentPath = state.currentPath.trim();
      if (!name || !branch || !folder || !parentPath) {
        state.error = t('spaces.worktreeDialogRequired');
        render();
        return;
      }

      close({
        branchName: branch,
        name,
        path: buildChildPath(parentPath, folder),
      });
    };

    const onKeyDown = (event: KeyboardEvent): void => {
      if (event.key === 'Escape') {
        event.preventDefault();
        close(null);
      } else if (event.key === 'Enter' && !state.loading) {
        const target = event.target as HTMLElement | null;
        if (target?.closest('[data-role="path"]')) {
          event.preventDefault();
          void loadDirectory(browser.pathInput.value.trim());
        }
      }
    };

    nameInput.addEventListener('input', () => {
      updateDerivedFields();
    });

    branchInput.addEventListener('input', () => {
      branchDirty = true;
    });

    folderInput.addEventListener('input', () => {
      folderDirty = true;
      targetPathInput.value = buildChildPath(state.currentPath, folderInput.value);
    });

    bindBrowserEvents({
      overlay,
      browser,
      state,
      machineId: args.machineId,
      render,
      loadDirectory,
      includeClone: false,
    });

    overlay.addEventListener('click', (event) => {
      const target = event.target as HTMLElement | null;
      if (!target) {
        return;
      }

      if (target === overlay) {
        close(null);
        return;
      }

      const role = target.closest<HTMLElement>('[data-role]')?.dataset.role;
      if (role === 'cancel') {
        close(null);
      } else if (role === 'confirm') {
        confirm();
      }
    });

    document.addEventListener('keydown', onKeyDown);
    document.body.appendChild(overlay);
    releaseBackButtonLayer = registerBackButtonLayer(() => {
      close(null);
    });
    updateDerivedFields();
    render();
    void loadInitial();
    nameInput.focus();
  });
}

function renderBrowserMarkup(options: { includeClone: boolean }): string {
  const cloneButton = options.includeClone
    ? `
        <button type="button" class="btn-secondary session-launcher-nav-btn" data-action="clone-repo" title="${escapeHtml(t('sessionLauncher.cloneRepo'))}">
          <span class="session-launcher-nav-icon" aria-hidden="true">&#9099;</span>
          <span>${escapeHtml(t('sessionLauncher.cloneRepo'))}</span>
        </button>
      `
    : '';

  return `
    <div class="session-launcher-browser space-dialog-browser">
      <div class="session-launcher-toolbar">
        <button type="button" class="btn-secondary session-launcher-nav-btn" data-action="home" title="${escapeHtml(t('sessionLauncher.home'))}">
          <span class="session-launcher-nav-icon" aria-hidden="true">&#8962;</span>
          <span>${escapeHtml(t('sessionLauncher.home'))}</span>
        </button>
        <button type="button" class="btn-secondary session-launcher-nav-btn" data-action="up" title="${escapeHtml(t('sessionLauncher.up'))}">
          <span class="session-launcher-nav-icon" aria-hidden="true">&#8593;</span>
          <span>${escapeHtml(t('sessionLauncher.up'))}</span>
        </button>
        <button type="button" class="btn-secondary session-launcher-nav-btn" data-action="new-folder" title="${escapeHtml(t('sessionLauncher.newFolder'))}">
          <span class="session-launcher-nav-icon" aria-hidden="true">+</span>
          <span>${escapeHtml(t('sessionLauncher.newFolder'))}</span>
        </button>
        ${cloneButton}
        <input type="text" class="session-launcher-path" data-role="path" spellcheck="false" autocomplete="off" />
      </div>
      <div class="session-launcher-roots" data-role="roots"></div>
      <div class="session-launcher-status" data-role="status" hidden></div>
      <div class="session-launcher-list" data-role="list"></div>
    </div>
  `;
}

function getBrowserElements(host: HTMLElement): BrowserElements | null {
  const pathInput = host.querySelector<HTMLInputElement>('[data-role="path"]');
  const rootsEl = host.querySelector<HTMLElement>('[data-role="roots"]');
  const statusEl = host.querySelector<HTMLElement>('[data-role="status"]');
  const listEl = host.querySelector<HTMLElement>('[data-role="list"]');
  if (!pathInput || !rootsEl || !statusEl || !listEl) {
    return null;
  }

  return {
    pathInput,
    rootsEl,
    statusEl,
    listEl,
  };
}

function createBrowserState(): BrowserState {
  return {
    currentPath: '',
    pathDraft: '',
    parentPath: null,
    roots: [],
    entries: [],
    loading: false,
    loadingMessage: null,
    error: null,
    requestToken: 0,
  };
}

function renderBrowser(elements: BrowserElements, state: BrowserState): void {
  if (elements.pathInput.value !== state.pathDraft) {
    elements.pathInput.value = state.pathDraft;
  }

  elements.pathInput.title = state.pathDraft;
  elements.rootsEl.innerHTML = state.roots
    .map((entry) => {
      const active = entry.fullPath === state.currentPath ? ' active' : '';
      return `
        <button type="button" class="session-launcher-root${active}" data-root-path="${escapeHtml(entry.fullPath)}">
          ${escapeHtml(entry.name)}
        </button>
      `;
    })
    .join('');

  const showStatus = state.loading || Boolean(state.error);
  elements.statusEl.hidden = !showStatus;
  elements.statusEl.classList.toggle('error', Boolean(state.error));
  elements.statusEl.textContent = state.loading
    ? (state.loadingMessage ?? t('sessionLauncher.loading'))
    : (state.error ?? '');

  if (state.entries.length === 0) {
    elements.listEl.innerHTML = `<div class="session-launcher-empty">${escapeHtml(t('sessionLauncher.empty'))}</div>`;
    return;
  }

  elements.listEl.innerHTML = state.entries
    .map(
      (entry) => `
        <button type="button" class="session-launcher-row" data-open-path="${escapeHtml(entry.fullPath)}" title="${escapeHtml(entry.fullPath)}">
          <span class="icon icon-svg session-launcher-row-icon" aria-hidden="true"><svg viewBox="0 0 24 24" width="1em" height="1em" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round"><path d="M3 7V5h6l2 2h10v12H3V7Z" /></svg></span>
          <span class="session-launcher-row-label">${escapeHtml(entry.name)}</span>
        </button>
      `,
    )
    .join('');
}

function bindBrowserEvents(args: {
  overlay: HTMLElement;
  browser: BrowserElements;
  state: BrowserState;
  machineId: string | null;
  render: () => void;
  loadDirectory: (
    path: string,
    options?: { suppressErrors?: boolean; recordPathDraft?: boolean },
  ) => Promise<boolean>;
  includeClone: boolean;
}): void {
  const { overlay, browser, state, machineId, render, loadDirectory, includeClone } = args;

  browser.pathInput.addEventListener('input', () => {
    state.pathDraft = browser.pathInput.value;
  });

  browser.pathInput.addEventListener('keydown', (event) => {
    event.stopPropagation();
    if (event.key === 'Enter') {
      event.preventDefault();
      void loadDirectory(browser.pathInput.value.trim());
    }
  });

  overlay.addEventListener('click', (event) => {
    const target = event.target as HTMLElement | null;
    if (!target) {
      return;
    }
    handleBrowserActionClick(target, machineId, state, render, loadDirectory, includeClone);
  });
}

function handleBrowserActionClick(
  target: HTMLElement,
  machineId: string | null,
  state: BrowserState,
  render: () => void,
  loadDirectory: (
    path: string,
    options?: { suppressErrors?: boolean; recordPathDraft?: boolean },
  ) => Promise<boolean>,
  includeClone: boolean,
): void {
  const rootPath = target.closest<HTMLElement>('[data-root-path]')?.dataset.rootPath;
  if (rootPath) {
    void loadDirectory(rootPath);
    return;
  }

  const openPath = target.closest<HTMLElement>('[data-open-path]')?.dataset.openPath;
  if (openPath) {
    void loadDirectory(openPath);
    return;
  }

  const action = target.closest<HTMLElement>('[data-action]')?.dataset.action;
  if (!action) {
    return;
  }

  switch (action) {
    case 'home':
      void loadDirectory(state.roots[0]?.fullPath || state.currentPath);
      return;
    case 'up':
      if (state.parentPath) {
        void loadDirectory(state.parentPath);
      }
      return;
    case 'new-folder':
      void promptAndCreateFolder(machineId, state, render, loadDirectory);
      return;
    case 'clone-repo':
      if (includeClone) {
        void promptAndCloneRepository(machineId, state, render, loadDirectory);
      }
      return;
    default:
      return;
  }
}

async function promptAndCreateFolder(
  machineId: string | null,
  state: BrowserState,
  render: () => void,
  loadDirectory: (
    path: string,
    options?: { suppressErrors?: boolean; recordPathDraft?: boolean },
  ) => Promise<boolean>,
): Promise<void> {
  const parentPath = state.currentPath.trim();
  if (!parentPath) {
    state.error = t('spaces.pathRequired');
    render();
    return;
  }

  const writable = await fetchWritableDirectory(machineId, parentPath);
  if (!writable.canWrite) {
    await showAlert(t('sessionLauncher.directoryNotWritable'), {
      title: t('sessionLauncher.newFolderTitle'),
    });
    return;
  }

  const folderName = await showTextPrompt({
    title: t('sessionLauncher.newFolderTitle'),
    message: t('sessionLauncher.newFolderPrompt'),
    placeholder: t('sessionLauncher.newFolderPlaceholder'),
    confirmLabel: t('sessionLauncher.createFolder'),
    validate: (value) => {
      if (!value.trim()) {
        return t('sessionLauncher.folderNameRequired');
      }

      if (/[\\/]/.test(value)) {
        return t('sessionLauncher.folderNameInvalid');
      }

      return null;
    },
  });
  if (!folderName) {
    return;
  }

  state.loading = true;
  state.loadingMessage = t('sessionLauncher.creatingFolder');
  state.error = null;
  render();
  try {
    const response = await createLauncherFolder(machineId, writable.path, folderName);
    state.loading = false;
    state.loadingMessage = null;
    render();
    await loadDirectory(response.path);
  } catch (error) {
    state.loading = false;
    state.loadingMessage = null;
    state.error = error instanceof Error ? error.message : String(error);
    render();
  }
}

async function promptAndCloneRepository(
  machineId: string | null,
  state: BrowserState,
  render: () => void,
  loadDirectory: (
    path: string,
    options?: { suppressErrors?: boolean; recordPathDraft?: boolean },
  ) => Promise<boolean>,
): Promise<void> {
  const parentPath = state.currentPath.trim();
  if (!parentPath) {
    state.error = t('spaces.pathRequired');
    render();
    return;
  }

  const writable = await fetchWritableDirectory(machineId, parentPath);
  if (!writable.canWrite) {
    await showAlert(t('sessionLauncher.directoryNotWritable'), {
      title: t('sessionLauncher.cloneRepoTitle'),
    });
    return;
  }

  const repositoryUrl = await showTextPrompt({
    title: t('sessionLauncher.cloneRepoTitle'),
    message: t('sessionLauncher.cloneRepoPrompt'),
    placeholder: t('sessionLauncher.cloneRepoPlaceholder'),
    confirmLabel: t('sessionLauncher.cloneRepoAction'),
    validate: (value) => (value.trim() ? null : t('sessionLauncher.repoUrlRequired')),
  });
  if (!repositoryUrl) {
    return;
  }

  state.loading = true;
  state.loadingMessage = t('sessionLauncher.cloningRepo');
  state.error = null;
  render();
  try {
    const response = await cloneLauncherRepository(machineId, writable.path, repositoryUrl);
    state.loading = false;
    state.loadingMessage = null;
    render();
    await loadDirectory(response.path);
  } catch (error) {
    state.loading = false;
    state.loadingMessage = null;
    state.error = error instanceof Error ? error.message : String(error);
    render();
  }
}

function beginLoad(
  state: BrowserState,
  suppressErrors: boolean | undefined,
  render: () => void,
): number {
  const requestToken = ++state.requestToken;
  state.loading = true;
  state.loadingMessage = t('sessionLauncher.loading');
  if (!suppressErrors) {
    state.error = null;
  }
  render();
  return requestToken;
}

function endLoad(state: BrowserState, requestToken: number, render: () => void): void {
  if (requestToken !== state.requestToken) {
    return;
  }

  state.loading = false;
  state.loadingMessage = null;
  render();
}

function getTargetLabel(machineId: string | null): string {
  if (!machineId) {
    return t('sessionLauncher.localTargetTitle');
  }

  return (
    getLaunchableHubMachines().find((machine) => machine.machine.id === machineId)?.machine.name ||
    machineId
  );
}

function getPickerApiBasePath(machineId: string | null): string {
  return machineId
    ? `/api/hub/machines/${encodeURIComponent(machineId)}/files/picker`
    : '/api/files/picker';
}

async function fetchHomePath(machineId: string | null): Promise<LauncherPathResponse> {
  const response = await fetch(`${getPickerApiBasePath(machineId)}/home`);
  if (!response.ok) {
    throw new Error(t('sessionLauncher.loadFailed'));
  }

  return (await response.json()) as LauncherPathResponse;
}

async function fetchLauncherRoots(
  machineId: string | null,
): Promise<LauncherDirectoryListResponse> {
  const response = await fetch(`${getPickerApiBasePath(machineId)}/roots`);
  if (!response.ok) {
    throw new Error(t('sessionLauncher.loadFailed'));
  }

  return (await response.json()) as LauncherDirectoryListResponse;
}

async function fetchDirectories(
  machineId: string | null,
  path: string,
): Promise<LauncherDirectoryListResponse> {
  const response = await fetch(
    `${getPickerApiBasePath(machineId)}/directories?path=${encodeURIComponent(path)}`,
  );
  if (!response.ok) {
    throw new Error(await response.text());
  }

  return (await response.json()) as LauncherDirectoryListResponse;
}

async function fetchWritableDirectory(
  machineId: string | null,
  path: string,
): Promise<LauncherDirectoryAccessResponse> {
  const response = await fetch(
    `${getPickerApiBasePath(machineId)}/writable?path=${encodeURIComponent(path)}`,
  );
  if (!response.ok) {
    throw new Error(await response.text());
  }

  return (await response.json()) as LauncherDirectoryAccessResponse;
}

async function createLauncherFolder(
  machineId: string | null,
  parentPath: string,
  name: string,
): Promise<LauncherDirectoryMutationResponse> {
  const response = await fetch(`${getPickerApiBasePath(machineId)}/folders`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({
      parentPath,
      name,
    }),
  });
  if (!response.ok) {
    throw new Error(await response.text());
  }

  return (await response.json()) as LauncherDirectoryMutationResponse;
}

async function cloneLauncherRepository(
  machineId: string | null,
  parentPath: string,
  repositoryUrl: string,
): Promise<LauncherDirectoryMutationResponse> {
  const response = await fetch(`${getPickerApiBasePath(machineId)}/clone`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({
      parentPath,
      repositoryUrl,
    }),
  });
  if (!response.ok) {
    throw new Error(await response.text());
  }

  return (await response.json()) as LauncherDirectoryMutationResponse;
}

function slugifyWorktreeName(value: string): string {
  const slug = value
    .trim()
    .toLowerCase()
    .replace(/[^a-z0-9]+/g, '-')
    .replace(/^-+|-+$/g, '');

  return slug || 'worktree';
}

function buildChildPath(parentPath: string, childName: string): string {
  const trimmedParent = parentPath.trim();
  const trimmedChild = childName.trim().replace(/^[\\/]+/, '');
  if (!trimmedParent || !trimmedChild) {
    return trimmedParent;
  }

  const separator = trimmedParent.includes('\\') ? '\\' : '/';
  return `${trimmedParent.replace(/[\\/]+$/, '')}${separator}${trimmedChild}`;
}

function getParentDirectory(path: string): string {
  const normalized = path.replace(/[\\/]+$/, '');
  const separatorIndex = Math.max(normalized.lastIndexOf('/'), normalized.lastIndexOf('\\'));
  if (separatorIndex <= 0) {
    return normalized;
  }

  return normalized.slice(0, separatorIndex);
}

function getPathTail(path: string): string {
  const normalized = path.replace(/[\\/]+$/, '');
  const separatorIndex = Math.max(normalized.lastIndexOf('/'), normalized.lastIndexOf('\\'));
  return separatorIndex >= 0 ? normalized.slice(separatorIndex + 1) : normalized;
}

function getConfiguredWorktreeRootDirectory(machineId: string | null): string | null {
  if (machineId) {
    return null;
  }

  const value = $currentSettings.get()?.worktreeRootDirectory.trim();
  return value ? value : null;
}

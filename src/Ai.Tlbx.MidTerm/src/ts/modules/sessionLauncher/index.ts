import { escapeHtml } from '../../utils/dom';
import { showTextPrompt } from '../../utils/dialog';
import { JS_BUILD_VERSION } from '../../constants';
import { t } from '../i18n';
import { registerBackButtonLayer } from '../navigation/backButtonGuard';
import { getLaunchableHubMachines, refreshHubState, subscribeHubState } from '../hub/runtime';
import type { HubMachineState } from '../hub/types';
import { openProviderResumePicker, type ResumeProvider } from '../providerResume';

export type LauncherProvider = 'terminal' | 'codex' | 'claude' | 'grok';
export type LauncherLaunchMode = 'new' | 'resume';

const LOCAL_TARGET_ID = 'local';

export interface LocalSessionLauncherTarget {
  id: typeof LOCAL_TARGET_ID;
  kind: 'local';
}

export interface HubSessionLauncherTarget {
  id: string;
  kind: 'hub';
  machineId: string;
  machineName: string;
  baseUrl: string;
  currentVersion: string | null;
}

export type SessionLauncherTarget = LocalSessionLauncherTarget | HubSessionLauncherTarget;

export interface SessionLauncherSelection {
  provider: LauncherProvider;
  launchMode: LauncherLaunchMode;
  workingDirectory: string | null;
  resumeThreadId?: string | null;
  target: SessionLauncherTarget;
}

let activeLauncherPromise: Promise<SessionLauncherSelection | null> | null = null;

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

interface LauncherState {
  homePath: string;
  startPath: string;
  currentPath: string;
  pathDraft: string;
  pathHistory: string[];
  parentPath: string | null;
  roots: LauncherDirectoryEntry[];
  entries: LauncherDirectoryEntry[];
  loading: boolean;
  loadingMessage: string | null;
  error: string | null;
  requestToken: number;
  targets: SessionLauncherTarget[];
  selectedTargetId: string;
}

interface LauncherLocalActionContext {
  clearBusy(): void;
  clearPendingPathFollow(): void;
  closeWithTerminalPath(path: string): void;
  getCurrentPath(): string;
  getPathDraft(): string;
  getTarget(): SessionLauncherTarget;
  loadDirectory(path: string): Promise<boolean>;
  setBusy(message: string): void;
  setError(message: string | null): void;
}

export function buildSessionLauncherTargets(
  machines: ReadonlyArray<HubMachineState>,
): SessionLauncherTarget[] {
  return [
    {
      id: LOCAL_TARGET_ID,
      kind: 'local',
    },
    ...machines.map((machine) => ({
      id: `hub:${machine.machine.id}`,
      kind: 'hub' as const,
      machineId: machine.machine.id,
      machineName: machine.machine.name,
      baseUrl: machine.machine.baseUrl,
      currentVersion: machine.currentVersion ?? null,
    })),
  ];
}

export function isProviderSupportedOnTarget(
  provider: LauncherProvider,
  target: SessionLauncherTarget,
): boolean {
  return target.kind === 'local' || provider === 'terminal';
}

async function promptForLauncherFolderName(): Promise<string | null> {
  return showTextPrompt({
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
}

async function promptForLauncherRepositoryUrl(): Promise<string | null> {
  return showTextPrompt({
    title: t('sessionLauncher.cloneRepoTitle'),
    message: t('sessionLauncher.cloneRepoPrompt'),
    placeholder: t('sessionLauncher.cloneRepoPlaceholder'),
    confirmLabel: t('sessionLauncher.cloneRepoAction'),
    validate: (value) => (value.trim() ? null : t('sessionLauncher.repoUrlRequired')),
  });
}

async function ensureCommittedLauncherPath(
  context: LauncherLocalActionContext,
): Promise<string | null> {
  const candidatePath = context.getPathDraft().trim();
  if (!candidatePath) {
    context.setError('Path is required');
    return null;
  }

  if (candidatePath !== context.getCurrentPath()) {
    const loaded = await context.loadDirectory(candidatePath);
    if (!loaded) {
      return null;
    }
  }

  return context.getCurrentPath();
}

async function ensureWritableLauncherDirectory(
  context: LauncherLocalActionContext,
  path: string,
): Promise<string | null> {
  context.setBusy(t('sessionLauncher.checkingDirectory'));

  try {
    const response = await fetchWritableDirectory(context.getTarget(), path);
    if (!response.canWrite) {
      context.clearBusy();
      context.setError(t('sessionLauncher.directoryNotWritable'));
      return null;
    }

    context.clearBusy();
    return response.path;
  } catch (error) {
    context.clearBusy();
    context.setError(error instanceof Error ? error.message : String(error));
    return null;
  }
}

async function runCreateFolderLauncherAction(context: LauncherLocalActionContext): Promise<void> {
  context.clearPendingPathFollow();

  const parentPath = await ensureCommittedLauncherPath(context);
  if (!parentPath) {
    return;
  }

  const writablePath = await ensureWritableLauncherDirectory(context, parentPath);
  if (!writablePath) {
    return;
  }

  const folderName = await promptForLauncherFolderName();
  if (!folderName) {
    return;
  }

  context.setBusy(t('sessionLauncher.creatingFolder'));

  try {
    const response = await createLauncherFolder(context.getTarget(), writablePath, folderName);
    context.clearBusy();
    await context.loadDirectory(response.path);
  } catch (error) {
    context.clearBusy();
    context.setError(error instanceof Error ? error.message : String(error));
  }
}

async function runCloneRepositoryLauncherAction(
  context: LauncherLocalActionContext,
): Promise<void> {
  context.clearPendingPathFollow();

  const parentPath = await ensureCommittedLauncherPath(context);
  if (!parentPath) {
    return;
  }

  const writablePath = await ensureWritableLauncherDirectory(context, parentPath);
  if (!writablePath) {
    return;
  }

  const repositoryUrl = await promptForLauncherRepositoryUrl();
  if (!repositoryUrl) {
    return;
  }

  context.setBusy(t('sessionLauncher.cloningRepo'));

  try {
    const response = await cloneLauncherRepository(
      context.getTarget(),
      writablePath,
      repositoryUrl,
    );
    context.closeWithTerminalPath(response.path);
  } catch (error) {
    context.clearBusy();
    context.setError(error instanceof Error ? error.message : String(error));
  }
}

async function navigateLauncherBackInHistory(
  state: LauncherState,
  clearPendingPathFollow: () => void,
  loadDirectory: (path: string, options?: { recordHistory?: boolean }) => Promise<boolean>,
): Promise<boolean> {
  clearPendingPathFollow();
  while (state.pathHistory.length > 0) {
    const previousPath = state.pathHistory.pop();
    if (!previousPath || previousPath === state.currentPath) {
      continue;
    }

    const loaded = await loadDirectory(previousPath, { recordHistory: false });
    if (loaded) {
      return true;
    }
  }

  return false;
}

async function commitLauncherPathDraft(
  state: LauncherState,
  clearPendingPathFollow: () => void,
  loadDirectory: (path: string) => Promise<boolean>,
  render: () => void,
): Promise<void> {
  clearPendingPathFollow();

  const candidatePath = state.pathDraft.trim();
  if (!candidatePath) {
    state.error = 'Path is required';
    render();
    return;
  }

  if (candidatePath === state.currentPath) {
    state.error = null;
    render();
    return;
  }

  await loadDirectory(candidatePath);
}

function clearLauncherPendingPathFollow(
  pathFollowTimer: number | null,
  setPathFollowTimer: (timer: number | null) => void,
): void {
  if (pathFollowTimer !== null) {
    window.clearTimeout(pathFollowTimer);
    setPathFollowTimer(null);
  }
}

function handleLauncherEscapeKey(event: KeyboardEvent, isBusy: boolean, close: () => void): void {
  if (event.key !== 'Escape') {
    return;
  }

  event.preventDefault();
  if (!isBusy) {
    close();
  }
}

function queueLauncherPathFollow(
  state: LauncherState,
  clearPendingPathFollow: () => void,
  setPathFollowTimer: (timer: number | null) => void,
  loadDirectory: (
    path: string,
    options?: {
      suppressErrors?: boolean;
    },
  ) => Promise<boolean>,
): void {
  clearPendingPathFollow();

  const timer = window.setTimeout(() => {
    setPathFollowTimer(null);
    const candidatePath = state.pathDraft.trim();
    if (!candidatePath || candidatePath === state.currentPath) {
      return;
    }

    void loadDirectory(candidatePath, { suppressErrors: true });
  }, 280);

  setPathFollowTimer(timer);
}

function getSelectedTargetOrLocal(
  targets: ReadonlyArray<SessionLauncherTarget>,
  selectedTargetId: string,
): SessionLauncherTarget {
  return (
    targets.find((target) => target.id === selectedTargetId) ?? {
      id: LOCAL_TARGET_ID,
      kind: 'local',
    }
  );
}

function resetLauncherBrowserState(
  state: LauncherState,
  pathResponse: LauncherPathResponse,
  roots: LauncherDirectoryEntry[],
): void {
  const homePath = pathResponse.homePath || pathResponse.path;
  const startPath = pathResponse.startPath || homePath;
  state.homePath = homePath;
  state.startPath = startPath;
  state.currentPath = startPath;
  state.pathDraft = startPath;
  state.pathHistory = [];
  state.parentPath = null;
  state.roots = roots;
  state.entries = [];
}

function getMajorMinorVersion(version: string | null | undefined): string | null {
  const match = version?.trim().match(/^v?(\d+)\.(\d+)/i);
  return match ? `${match[1]}.${match[2]}` : null;
}

export function hasMatchingMajorMinorVersion(
  localVersion: string | null | undefined,
  remoteVersion: string | null | undefined,
): boolean {
  const localMajorMinor = getMajorMinorVersion(localVersion);
  const remoteMajorMinor = getMajorMinorVersion(remoteVersion);
  return !localMajorMinor || !remoteMajorMinor || localMajorMinor === remoteMajorMinor;
}

function getLauncherTargetWarning(target: SessionLauncherTarget): string | null {
  if (target.kind !== 'hub') {
    return null;
  }

  const messages = [t('sessionLauncher.remoteTerminalOnly')];
  const localVersion = JS_BUILD_VERSION;
  if (!hasMatchingMajorMinorVersion(localVersion, target.currentVersion)) {
    messages.push(
      `${t('sessionLauncher.remoteVersionWarning')} (${target.currentVersion ?? '?'} vs ${localVersion})`,
    );
  }

  return messages.join('\n');
}

function isLauncherRequestStale(
  state: LauncherState,
  requestToken: number,
  targetId: string,
): boolean {
  return requestToken !== state.requestToken || state.selectedTargetId !== targetId;
}

function beginLauncherDirectoryLoad(
  state: LauncherState,
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

function finalizeLauncherDirectoryLoad(
  state: LauncherState,
  requestToken: number,
  render: () => void,
): void {
  if (requestToken === state.requestToken) {
    state.loading = false;
    state.loadingMessage = null;
    render();
  }
}

function applyLauncherDirectoryResponse(
  state: LauncherState,
  response: LauncherDirectoryListResponse,
  previousPath: string,
  recordHistory: boolean | undefined,
): void {
  state.currentPath = response.path;
  state.pathDraft = response.path;
  state.parentPath = response.parentPath;
  state.entries = response.entries;
  if (
    recordHistory !== false &&
    previousPath &&
    previousPath !== response.path &&
    state.pathHistory[state.pathHistory.length - 1] !== previousPath
  ) {
    state.pathHistory.push(previousPath);
  }
  state.error = null;
}

export async function openSessionLauncher(): Promise<SessionLauncherSelection | null> {
  if (activeLauncherPromise) {
    return activeLauncherPromise;
  }

  activeLauncherPromise = openSessionLauncherInternal();
  try {
    return await activeLauncherPromise;
  } finally {
    activeLauncherPromise = null;
  }
}

async function openSessionLauncherInternal(): Promise<SessionLauncherSelection | null> {
  return new Promise((resolve) => {
    const overlay = document.createElement('div');
    overlay.className = 'modal-overlay session-launcher-overlay';
    let releaseBackButtonLayer: (() => void) | null = null;

    overlay.innerHTML = `
      <div class="modal session-launcher-modal" role="dialog" aria-modal="true" aria-labelledby="session-launcher-title">
        <div class="modal-content session-launcher-content">
          <div class="modal-header">
            <div>
              <div class="session-launcher-kicker">${escapeHtml(t('sidebar.newTerminal'))}</div>
              <h3 id="session-launcher-title">${escapeHtml(t('sessionLauncher.title'))}</h3>
            </div>
            <button class="modal-close" type="button" data-role="cancel" aria-label="${escapeHtml(t('dialog.cancel'))}">&times;</button>
          </div>
          <div class="modal-body session-launcher-body">
            <div class="session-launcher-launch" data-role="targets-section" hidden>
              <div class="session-launcher-launch-label">${escapeHtml(t('sessionLauncher.chooseTarget'))}</div>
              <div class="session-launcher-targets" data-role="targets"></div>
            </div>
            <div class="session-launcher-browser" data-role="browser">
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
                <button type="button" class="btn-secondary session-launcher-nav-btn" data-action="clone-repo" title="${escapeHtml(t('sessionLauncher.cloneRepo'))}">
                  <span class="session-launcher-nav-icon" aria-hidden="true">&#9099;</span>
                  <span>${escapeHtml(t('sessionLauncher.cloneRepo'))}</span>
                </button>
                <input
                  type="text"
                  class="session-launcher-path"
                  data-role="path"
                  title=""
                  spellcheck="false"
                  autocomplete="off"
                />
              </div>
              <div class="session-launcher-roots" data-role="roots"></div>
              <div class="session-launcher-status" data-role="status" hidden></div>
              <div class="session-launcher-list" data-role="list"></div>
            </div>
            <div class="session-launcher-launch">
              <div class="session-launcher-launch-label">${escapeHtml(t('sessionLauncher.chooseProvider'))}</div>
              <div class="session-launcher-providers" data-role="providers"></div>
              <div class="session-launcher-provider-hint" data-role="provider-hint" hidden></div>
            </div>
          </div>
          <div class="modal-footer session-launcher-footer">
            <button type="button" class="btn-secondary" data-role="cancel">${escapeHtml(t('dialog.cancel'))}</button>
          </div>
        </div>
      </div>
    `;

    const state: LauncherState = {
      homePath: '',
      startPath: '',
      currentPath: '',
      pathDraft: '',
      pathHistory: [],
      parentPath: null,
      roots: [],
      entries: [],
      loading: false,
      loadingMessage: null,
      error: null,
      requestToken: 0,
      targets: buildSessionLauncherTargets(getLaunchableHubMachines()),
      selectedTargetId: LOCAL_TARGET_ID,
    };

    const providersEl = overlay.querySelector<HTMLElement>('[data-role="providers"]');
    const targetsSectionEl = overlay.querySelector<HTMLElement>('[data-role="targets-section"]');
    const targetsEl = overlay.querySelector<HTMLElement>('[data-role="targets"]');
    const providerHintEl = overlay.querySelector<HTMLElement>('[data-role="provider-hint"]');
    const pathEl = overlay.querySelector<HTMLInputElement>('[data-role="path"]');
    const rootsEl = overlay.querySelector<HTMLElement>('[data-role="roots"]');
    const statusEl = overlay.querySelector<HTMLElement>('[data-role="status"]');
    const listEl = overlay.querySelector<HTMLElement>('[data-role="list"]');
    const cancelButtons = overlay.querySelectorAll<HTMLElement>('[data-role="cancel"]');

    if (
      !providersEl ||
      !targetsSectionEl ||
      !targetsEl ||
      !providerHintEl ||
      !pathEl ||
      !rootsEl ||
      !statusEl ||
      !listEl
    ) {
      overlay.remove();
      resolve(null);
      return;
    }

    const safeProvidersEl = providersEl;
    const safeTargetsSectionEl = targetsSectionEl;
    const safeTargetsEl = targetsEl;
    const safeProviderHintEl = providerHintEl;
    const safePathEl = pathEl;
    const safeRootsEl = rootsEl;
    const safeStatusEl = statusEl;
    const safeListEl = listEl;
    const releaseHubStateSubscription = subscribeHubState(() => {
      render();
    });

    let pathFollowTimer: number | null = null;
    let skipNextPathCommit = false;

    function clearPendingPathFollow(): void {
      clearLauncherPendingPathFollow(pathFollowTimer, (timer) => {
        pathFollowTimer = timer;
      });
    }

    function close(result: SessionLauncherSelection | null): void {
      clearPendingPathFollow();
      document.removeEventListener('keydown', onKeyDown);
      releaseHubStateSubscription();
      releaseBackButtonLayer?.();
      releaseBackButtonLayer = null;
      overlay.remove();
      resolve(result);
    }

    function onKeyDown(event: KeyboardEvent): void {
      handleLauncherEscapeKey(event, state.loading, () => {
        close(null);
      });
    }

    function getSelectedTarget(): SessionLauncherTarget {
      return getSelectedTargetOrLocal(state.targets, state.selectedTargetId);
    }

    function refreshTargets(): void {
      state.targets = buildSessionLauncherTargets(getLaunchableHubMachines());
      if (!state.targets.some((target) => target.id === state.selectedTargetId)) {
        state.selectedTargetId = LOCAL_TARGET_ID;
      }
    }

    function renderTargets(): void {
      const showTargets = state.targets.length > 1;
      safeTargetsSectionEl.hidden = !showTargets;
      if (!showTargets) {
        safeTargetsEl.innerHTML = '';
        return;
      }

      safeTargetsEl.innerHTML = state.targets
        .map((target) => {
          const active = state.selectedTargetId === target.id ? ' active' : '';
          if (target.kind === 'local') {
            return `
              <button type="button" class="session-launcher-target${active}" data-target-id="${escapeHtml(target.id)}">
                <span class="session-launcher-target-title">${escapeHtml(t('sessionLauncher.localTargetTitle'))}</span>
                <span class="session-launcher-target-description">${escapeHtml(t('sessionLauncher.localTargetDescription'))}</span>
              </button>
            `;
          }

          return `
            <button type="button" class="session-launcher-target${active}" data-target-id="${escapeHtml(target.id)}">
              <span class="session-launcher-target-title">${escapeHtml(target.machineName)}</span>
              <span class="session-launcher-target-description">${escapeHtml(target.baseUrl)}</span>
            </button>
          `;
        })
        .join('');
    }

    function renderProviders(): void {
      const target = getSelectedTarget();
      safeProvidersEl.innerHTML = getProviders()
        .map((definition) => {
          const supported = isProviderSupportedOnTarget(definition.provider, target);
          const badge = definition.beta
            ? `<span class="feature-beta-badge">${escapeHtml(t('common.beta'))}</span>`
            : '';
          const disabled = !supported || state.loading || !state.currentPath;
          const actions =
            definition.provider === 'terminal' || definition.supportsResume === false
              ? `
                <button
                  type="button"
                  class="btn-secondary session-launcher-provider-action"
                  data-provider="${definition.provider}"
                  data-launch-mode="new"
                  ${disabled ? 'disabled' : ''}
                  >
                    ${escapeHtml(definition.provider === 'terminal' ? definition.launchLabel : 'New Conversation')}
                  </button>
                `
              : `
                <div class="session-launcher-provider-actions">
                  <button
                    type="button"
                    class="btn-secondary session-launcher-provider-action"
                    data-provider="${definition.provider}"
                    data-launch-mode="new"
                    ${disabled ? 'disabled' : ''}
                  >
                    New Conversation
                  </button>
                  <button
                    type="button"
                    class="btn-secondary session-launcher-provider-action"
                    data-provider="${definition.provider}"
                    data-launch-mode="resume"
                    ${disabled ? 'disabled' : ''}
                  >
                    Resume Conversation
                  </button>
                </div>
              `;
          return `
            <div
              class="session-launcher-provider"
            >
              <span class="session-launcher-provider-heading">
                <span class="session-launcher-provider-title">${escapeHtml(definition.title)}</span>
                ${badge}
              </span>
              <span class="session-launcher-provider-description">${escapeHtml(definition.description)}</span>
              ${actions}
            </div>
          `;
        })
        .join('');

      const targetWarning = getLauncherTargetWarning(target);
      safeProviderHintEl.hidden = !targetWarning;
      safeProviderHintEl.textContent = targetWarning ?? '';
    }

    function renderRoots(): void {
      safeRootsEl.innerHTML = state.roots
        .map((entry) => {
          const active = entry.fullPath === state.currentPath ? ' active' : '';
          return `
            <button type="button" class="session-launcher-root${active}" data-root-path="${escapeHtml(entry.fullPath)}">
              ${escapeHtml(entry.name)}
            </button>
          `;
        })
        .join('');
    }

    function renderStatus(): void {
      const shouldShow = state.loading || Boolean(state.error);
      safeStatusEl.hidden = !shouldShow;
      safeStatusEl.classList.toggle('error', Boolean(state.error));
      safeStatusEl.textContent = state.loading
        ? (state.loadingMessage ?? t('sessionLauncher.loading'))
        : (state.error ?? '');
    }

    function renderList(): void {
      if (state.entries.length === 0) {
        safeListEl.innerHTML = `<div class="session-launcher-empty">${escapeHtml(t('sessionLauncher.empty'))}</div>`;
        return;
      }

      safeListEl.innerHTML = state.entries
        .map((entry) => {
          return `
            <button
              type="button"
              class="session-launcher-row"
              data-open-path="${escapeHtml(entry.fullPath)}"
              title="${escapeHtml(entry.fullPath)}"
            >
              <span class="session-launcher-row-icon" aria-hidden="true">&#xea83;</span>
              <span class="session-launcher-row-label">${escapeHtml(entry.name)}</span>
            </button>
          `;
        })
        .join('');
    }

    function render(): void {
      refreshTargets();
      renderTargets();
      renderProviders();
      renderRoots();
      renderStatus();
      renderList();

      if (safePathEl.value !== state.pathDraft) {
        safePathEl.value = state.pathDraft;
      }
      safePathEl.title = state.pathDraft;
    }

    async function loadDirectory(
      path: string,
      options?: {
        recordHistory?: boolean;
        suppressErrors?: boolean;
        target?: SessionLauncherTarget;
      },
    ): Promise<boolean> {
      const target = options?.target ?? getSelectedTarget();
      const targetId = target.id;
      const previousPath = state.currentPath;
      const requestToken = beginLauncherDirectoryLoad(state, options?.suppressErrors, render);

      try {
        const response = await fetchDirectories(target, path);
        if (isLauncherRequestStale(state, requestToken, targetId)) {
          return false;
        }

        applyLauncherDirectoryResponse(state, response, previousPath, options?.recordHistory);
        return true;
      } catch (error) {
        if (isLauncherRequestStale(state, requestToken, targetId)) {
          return false;
        }

        if (!options?.suppressErrors) {
          state.error = error instanceof Error ? error.message : String(error);
        }
      } finally {
        finalizeLauncherDirectoryLoad(state, requestToken, render);
      }

      return false;
    }

    async function loadSelectedTargetBrowser(
      target: SessionLauncherTarget,
      options?: { path?: string | null },
    ): Promise<void> {
      const requestToken = ++state.requestToken;
      state.loading = true;
      state.loadingMessage = t('sessionLauncher.loading');
      state.error = null;
      render();

      try {
        const [pathResponse, rootsResponse] = await Promise.all([
          fetchHomePath(target),
          fetchLauncherRoots(target),
        ]);
        if (requestToken !== state.requestToken || state.selectedTargetId !== target.id) {
          return;
        }

        resetLauncherBrowserState(state, pathResponse, rootsResponse.entries);
        const initialPath = options?.path?.trim() || state.startPath;
        await loadDirectory(initialPath, {
          recordHistory: false,
          target,
        });
      } catch (error) {
        if (requestToken !== state.requestToken || state.selectedTargetId !== target.id) {
          return;
        }

        state.loading = false;
        state.loadingMessage = null;
        state.error = error instanceof Error ? error.message : String(error);
        render();
      }
    }

    const localActionContext: LauncherLocalActionContext = {
      clearBusy: () => {
        state.loading = false;
        state.loadingMessage = null;
        render();
      },
      clearPendingPathFollow,
      closeWithTerminalPath: (path) => {
        const target = getSelectedTarget();
        close({
          provider: 'terminal',
          launchMode: 'new',
          workingDirectory: path,
          target,
        });
      },
      getCurrentPath: () => state.currentPath,
      getPathDraft: () => state.pathDraft,
      getTarget: getSelectedTarget,
      loadDirectory,
      setBusy: (message) => {
        state.loading = true;
        state.loadingMessage = message;
        state.error = null;
        render();
      },
      setError: (message) => {
        state.error = message;
        render();
      },
    };

    render();
    document.body.appendChild(overlay);
    document.addEventListener('keydown', onKeyDown);
    releaseBackButtonLayer = registerBackButtonLayer(() => {
      if (state.loading) {
        return;
      }

      void (async () => {
        const navigated = await navigateLauncherBackInHistory(
          state,
          clearPendingPathFollow,
          loadDirectory,
        );
        if (!navigated) {
          close(null);
        }
      })();
    });
    void loadSelectedTargetBrowser(getSelectedTarget());
    void refreshHubState().catch(() => {});

    function launch(
      provider: LauncherProvider,
      launchMode: LauncherLaunchMode,
      resumeThreadId?: string | null,
    ): void {
      const target = getSelectedTarget();
      if (!isProviderSupportedOnTarget(provider, target)) {
        return;
      }

      if (state.loading || !state.currentPath) {
        return;
      }

      close({
        provider,
        launchMode,
        workingDirectory: state.currentPath,
        resumeThreadId: resumeThreadId?.trim() || null,
        target,
      });
    }

    safeProvidersEl.addEventListener('click', (event) => {
      const actionEl = (event.target as HTMLElement | null)?.closest<HTMLElement>(
        '[data-provider][data-launch-mode]',
      );
      if (!actionEl) {
        return;
      }

      const provider = actionEl.dataset.provider as LauncherProvider;
      const launchMode = actionEl.dataset.launchMode as LauncherLaunchMode;
      if (launchMode !== 'resume' || provider === 'terminal') {
        launch(provider, launchMode);
        return;
      }

      const workingDirectory = state.currentPath;
      if (!workingDirectory) {
        return;
      }

      void (async () => {
        const resumeEntry = await openProviderResumePicker({
          provider: provider as ResumeProvider,
          workingDirectory,
          initialScope: 'current',
        });
        if (!resumeEntry) {
          return;
        }

        launch(provider, 'resume', resumeEntry.sessionId);
      })();
    });

    safeTargetsEl.addEventListener('click', (event) => {
      const targetId = (event.target as HTMLElement | null)?.closest<HTMLElement>(
        '[data-target-id]',
      )?.dataset.targetId;
      if (!targetId || state.selectedTargetId === targetId) {
        return;
      }

      clearPendingPathFollow();
      state.selectedTargetId = targetId;
      state.pathHistory = [];
      render();
      void loadSelectedTargetBrowser(getSelectedTarget());
    });

    safeRootsEl.addEventListener('click', (event) => {
      const target = (event.target as HTMLElement | null)?.closest<HTMLElement>('[data-root-path]');
      const rootPath = target?.dataset.rootPath;
      if (!rootPath) {
        return;
      }

      clearPendingPathFollow();
      void loadDirectory(rootPath);
    });

    safeListEl.addEventListener('click', (event) => {
      const target = event.target as HTMLElement | null;
      const openPath = target?.closest<HTMLElement>('[data-open-path]')?.dataset.openPath;
      if (openPath) {
        clearPendingPathFollow();
        void loadDirectory(openPath);
      }
    });

    safePathEl.addEventListener('input', () => {
      state.pathDraft = safePathEl.value;
      queueLauncherPathFollow(
        state,
        clearPendingPathFollow,
        (timer) => {
          pathFollowTimer = timer;
        },
        loadDirectory,
      );
    });

    safePathEl.addEventListener('keydown', (event) => {
      if (event.key === 'Enter') {
        event.preventDefault();
        void commitLauncherPathDraft(state, clearPendingPathFollow, loadDirectory, render);
        return;
      }

      if (event.key === 'Escape') {
        event.preventDefault();
        state.pathDraft = state.currentPath;
        state.error = null;
        render();
      }
    });

    safePathEl.addEventListener('blur', () => {
      if (skipNextPathCommit) {
        skipNextPathCommit = false;
        return;
      }

      void commitLauncherPathDraft(state, clearPendingPathFollow, loadDirectory, render);
    });

    overlay.addEventListener('pointerdown', (event) => {
      const target = event.target as HTMLElement | null;
      if (
        document.activeElement === safePathEl &&
        target?.closest(
          '[data-open-path], [data-root-path], [data-action], [data-provider], [data-target-id], [data-role="cancel"]',
        )
      ) {
        skipNextPathCommit = true;
      }
    });

    overlay.addEventListener('click', (event) => {
      const target = event.target as HTMLElement | null;
      if (target === overlay) {
        if (state.loading) {
          return;
        }

        close(null);
        return;
      }

      if (target?.closest('[data-role="cancel"]')) {
        if (state.loading) {
          return;
        }

        close(null);
        return;
      }

      const action = target?.closest<HTMLElement>('[data-action]')?.dataset.action;
      if (action === 'home') {
        clearPendingPathFollow();
        void loadDirectory(state.homePath);
      } else if (action === 'up' && state.parentPath) {
        clearPendingPathFollow();
        void loadDirectory(state.parentPath);
      } else if (action === 'new-folder') {
        void runCreateFolderLauncherAction(localActionContext);
      } else if (action === 'clone-repo') {
        void runCloneRepositoryLauncherAction(localActionContext);
      }
    });

    cancelButtons.forEach((button) => {
      button.setAttribute('type', 'button');
    });
  });
}

function getLauncherApiBasePath(target: SessionLauncherTarget): string {
  return target.kind === 'local'
    ? '/api/files/picker'
    : `/api/hub/machines/${encodeURIComponent(target.machineId)}/files/picker`;
}

async function fetchHomePath(target: SessionLauncherTarget): Promise<LauncherPathResponse> {
  const response = await fetch(`${getLauncherApiBasePath(target)}/home`);
  if (!response.ok) {
    throw new Error(t('sessionLauncher.loadFailed'));
  }

  return (await response.json()) as LauncherPathResponse;
}

function getProviders(): ReadonlyArray<{
  provider: LauncherProvider;
  title: string;
  description: string;
  launchLabel: string;
  beta?: boolean;
  supportsResume?: boolean;
}> {
  return [
    {
      provider: 'terminal',
      title: t('sessionLauncher.terminalTitle'),
      description: t('sessionLauncher.terminalDescription'),
      launchLabel: t('sessionLauncher.startTerminal'),
    },
    {
      provider: 'codex',
      title: t('sessionLauncher.codexTitle'),
      description: t('sessionLauncher.codexDescription'),
      launchLabel: t('sessionLauncher.startCodex'),
      beta: true,
    },
    {
      provider: 'claude',
      title: t('sessionLauncher.claudeTitle'),
      description: t('sessionLauncher.claudeDescription'),
      launchLabel: t('sessionLauncher.startClaude'),
      beta: true,
    },
    {
      provider: 'grok',
      title: 'Grok',
      description: 'Start an Agent Controller Session for Grok Build in a chosen folder.',
      launchLabel: 'Start Grok',
      beta: true,
      supportsResume: false,
    },
  ];
}

async function fetchLauncherRoots(
  target: SessionLauncherTarget,
): Promise<LauncherDirectoryListResponse> {
  const response = await fetch(`${getLauncherApiBasePath(target)}/roots`);
  if (!response.ok) {
    throw new Error(t('sessionLauncher.loadFailed'));
  }

  return (await response.json()) as LauncherDirectoryListResponse;
}

async function fetchDirectories(
  target: SessionLauncherTarget,
  path: string,
): Promise<LauncherDirectoryListResponse> {
  const response = await fetch(
    `${getLauncherApiBasePath(target)}/directories?path=${encodeURIComponent(path)}`,
  );
  if (!response.ok) {
    throw new Error(await response.text());
  }

  return (await response.json()) as LauncherDirectoryListResponse;
}

async function fetchWritableDirectory(
  target: SessionLauncherTarget,
  path: string,
): Promise<LauncherDirectoryAccessResponse> {
  const response = await fetch(
    `${getLauncherApiBasePath(target)}/writable?path=${encodeURIComponent(path)}`,
  );
  if (!response.ok) {
    throw new Error(await response.text());
  }

  return (await response.json()) as LauncherDirectoryAccessResponse;
}

async function createLauncherFolder(
  target: SessionLauncherTarget,
  parentPath: string,
  name: string,
): Promise<LauncherDirectoryMutationResponse> {
  const response = await fetch(`${getLauncherApiBasePath(target)}/folders`, {
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
  target: SessionLauncherTarget,
  parentPath: string,
  repositoryUrl: string,
): Promise<LauncherDirectoryMutationResponse> {
  const response = await fetch(`${getLauncherApiBasePath(target)}/clone`, {
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

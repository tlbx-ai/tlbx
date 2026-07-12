/**
 * Session Tab Bar
 *
 * Creates and manages the session bar UI for each session.
 * Tabs: Primary session surface | Files
 * Right-aligned actions: Browser | Share | Git dock toggle
 */

import type { GitRepoBinding, GitStatusResponse } from '../git/types';
import { t } from '../i18n';
import { reconcileKeyedChildren } from '../../utils/domReconcile';

export type SessionTabId = 'terminal' | 'agent' | 'files';

export type IdeBarActionId = 'git' | 'commands' | 'inputHistory' | 'web' | 'share';

const INPUT_HISTORY_BUTTON_ICON =
  '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.85" stroke-linecap="round" stroke-linejoin="round">' +
  '<path d="M3 12a9 9 0 1 0 3-6.7"></path>' +
  '<path d="M3 4v5h5"></path>' +
  '<path d="M12 7v5l3 2"></path>' +
  '</svg>';

const WEB_BUTTON_ICON =
  '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.85" stroke-linecap="round" stroke-linejoin="round">' +
  '<circle cx="12" cy="12" r="8"></circle>' +
  '<path d="M4 12h16"></path>' +
  '<path d="M12 4a11.5 11.5 0 0 1 0 16"></path>' +
  '<path d="M12 4a11.5 11.5 0 0 0 0 16"></path>' +
  '</svg>';

const SHARE_BUTTON_ICON =
  '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.85" stroke-linecap="round" stroke-linejoin="round">' +
  '<circle cx="18" cy="5.5" r="2"></circle>' +
  '<circle cx="6" cy="12" r="2"></circle>' +
  '<circle cx="18" cy="18.5" r="2"></circle>' +
  '<path d="m7.75 11 8-4.25"></path>' +
  '<path d="m7.75 13 8 4.25"></path>' +
  '</svg>';

const GIT_BUTTON_ICON =
  '<svg viewBox="0 0 24 24" fill="none" stroke="currentColor" stroke-width="1.85" stroke-linecap="round" stroke-linejoin="round">' +
  '<circle cx="7" cy="6" r="1.75"></circle>' +
  '<circle cx="7" cy="18" r="1.75"></circle>' +
  '<circle cx="17" cy="12" r="1.75"></circle>' +
  '<path d="M8.75 6h3a4 4 0 0 1 4 4v2"></path>' +
  '<path d="M8.75 18h3a4 4 0 0 0 4-4v-2"></path>' +
  '</svg>';

function getTabLabels(): Record<SessionTabId, string> {
  return {
    terminal: t('session.terminal'),
    agent: t('sessionTabs.agent'),
    files: t('sessionTabs.files'),
  };
}

function getVisibleTabs(): SessionTabId[] {
  return ['terminal', 'agent', 'files'];
}

function createActionIcon(svgMarkup: string): HTMLSpanElement {
  const icon = document.createElement('span');
  icon.className = 'ide-bar-btn-icon';
  icon.setAttribute('aria-hidden', 'true');
  icon.innerHTML = svgMarkup;
  return icon;
}

function createActionLabel(text: string): HTMLSpanElement {
  const label = document.createElement('span');
  label.className = 'ide-bar-btn-label';
  label.textContent = text;
  return label;
}

function createTextNode(className: string, text: string): HTMLSpanElement {
  const node = document.createElement('span');
  node.className = className;
  node.textContent = text;
  return node;
}

function createBetaBadge(): HTMLSpanElement {
  const badge = document.createElement('span');
  badge.className = 'feature-beta-badge';
  badge.textContent = t('common.beta');
  return badge;
}

function buildGitStatsMarkup(additions: number, deletions: number): string {
  return (
    `<span class="git-indicator-added">+${additions}</span>` +
    `<span class="git-indicator-deleted">-${deletions}</span>`
  );
}

interface GitIndicatorViewModel {
  repoRoot: string;
  primaryText: string;
  tertiaryText: string;
  statusText: string;
  additions: number;
  deletions: number;
  title: string;
  isEmpty: boolean;
  isPrimary: boolean;
  canRemove: boolean;
}

type GitIndicatorInput = GitRepoBinding[] | GitStatusResponse | null;

interface GitRepoActionHandlers {
  add?: (sessionId: string) => void;
  remove?: (sessionId: string, repoRoot: string) => void;
  refresh?: (sessionId: string, repoRoot?: string) => void;
}

const gitChipModels = new WeakMap<HTMLElement, Pick<GitIndicatorViewModel, 'repoRoot'> | null>();
const gitIndicatorResizeObservers = new WeakMap<HTMLElement, ResizeObserver>();
const pendingGitIndicatorOverflowSyncs = new WeakSet<HTMLElement>();
let gitRepoActionHandlers: GitRepoActionHandlers = {};

function createGitIndicatorButton(sessionId: string): HTMLButtonElement {
  const btn = document.createElement('button');
  btn.className = 'ide-bar-btn git-indicator git-repo-chip';
  btn.dataset.action = 'git';
  btn.appendChild(createActionIcon(GIT_BUTTON_ICON));

  const meta = document.createElement('span');
  meta.className = 'git-indicator-meta';

  const primaryLine = document.createElement('span');
  primaryLine.className = 'git-indicator-line git-indicator-line-primary';
  primaryLine.appendChild(createTextNode('git-indicator-branch', t('git.noRepoShort')));
  primaryLine.appendChild(createTextNode('git-indicator-separator', ''));
  primaryLine.appendChild(createTextNode('git-indicator-status', ''));

  const secondaryLine = document.createElement('span');
  secondaryLine.className = 'git-indicator-line git-indicator-line-secondary git-indicator-stats';
  secondaryLine.innerHTML = buildGitStatsMarkup(0, 0);

  const tertiaryLine = document.createElement('span');
  tertiaryLine.className = 'git-indicator-line git-indicator-line-repo';
  tertiaryLine.appendChild(createTextNode('git-indicator-label', ''));

  meta.appendChild(primaryLine);
  meta.appendChild(secondaryLine);
  meta.appendChild(tertiaryLine);
  btn.appendChild(meta);
  btn.title = t('sessionTabs.git');
  btn.setAttribute('aria-label', t('sessionTabs.git'));
  btn.addEventListener('click', () => {
    const repoRoot = gitChipModels.get(btn)?.repoRoot;
    gitClickHandler?.(repoRoot);
  });
  btn.addEventListener('contextmenu', (event) => {
    event.preventDefault();
    const repoRoot = gitChipModels.get(btn)?.repoRoot;
    if (repoRoot) {
      gitRepoActionHandlers.refresh?.(sessionId, repoRoot);
    }
  });
  return btn;
}

function createGitIndicatorGroup(sessionId: string): HTMLDivElement {
  const group = document.createElement('div');
  group.className = 'git-indicator-group';
  group.dataset.action = 'git';
  group.dataset.sessionId = sessionId;

  const strip = document.createElement('div');
  strip.className = 'git-indicator-strip';
  renderGitRepoChips(strip, sessionId, normalizeGitIndicatorRepos(null));
  group.appendChild(strip);

  const overflow = document.createElement('button');
  overflow.className = 'ide-bar-btn git-indicator-overflow';
  overflow.type = 'button';
  overflow.title = 'Git repositories';
  overflow.setAttribute('aria-label', 'Git repositories');
  overflow.textContent = '+';
  overflow.addEventListener('click', (event) => {
    event.stopPropagation();
    toggleGitRepoPopover(group, sessionId);
  });
  group.appendChild(overflow);
  bindGitIndicatorOverflowSync(group);

  return group;
}

function hasGitStatus(status: GitStatusResponse | null): status is GitStatusResponse {
  if (!status) {
    return false;
  }

  return Boolean(status.repoRoot || status.branch);
}

// eslint-disable-next-line complexity -- the git chip compresses conflicts, dirty state, sync state, and empty repos into one compact view model.
function buildGitIndicatorViewModel(
  repo: GitRepoBinding | null,
  status: GitStatusResponse | null,
): GitIndicatorViewModel {
  if (!hasGitStatus(status)) {
    const labelText = repo?.repoRoot ? repo.label || repo.role : '';
    return {
      repoRoot: repo?.repoRoot ?? '',
      primaryText: labelText || t('git.noRepoShort'),
      tertiaryText: '',
      statusText: '',
      additions: 0,
      deletions: 0,
      title: `${t('sessionTabs.git')}: ${labelText}`,
      isEmpty: true,
      isPrimary: repo?.isPrimary === true,
      canRemove: repo?.isPrimary !== true && Boolean(repo?.repoRoot),
    };
  }

  const changedCount =
    status.staged.length +
    status.modified.length +
    status.untracked.length +
    status.conflicted.length;
  let statusText = t('git.cleanShort');

  if (status.conflicted.length > 0) {
    statusText = `!${status.conflicted.length}`;
  } else if (changedCount > 0) {
    statusText = `~${changedCount}`;
  } else if (status.ahead > 0 || status.behind > 0) {
    const syncParts: string[] = [];
    if (status.ahead > 0) {
      syncParts.push(`↑${status.ahead}`);
    }
    if (status.behind > 0) {
      syncParts.push(`↓${status.behind}`);
    }
    statusText = syncParts.join(' ');
  }

  const branchText = status.branch || 'HEAD';
  const labelText =
    repo?.label || status.label || getRepoNameFromRoot(status.repoRoot) || repo?.role || '';
  const isPrimary = status.isPrimary ?? repo?.isPrimary === true;
  const primaryText = isPrimary ? branchText : labelText || branchText;
  const tertiaryText = isPrimary ? labelText : branchText;
  const additions = status.totalAdditions;
  const deletions = status.totalDeletions;
  const title =
    `${t('sessionTabs.git')}: ${labelText} / ${branchText}` +
    (statusText ? ` / ${statusText}` : '') +
    `, +${additions} -${deletions}` +
    (status.repoRoot ? `\n${status.repoRoot}` : '');

  return {
    repoRoot: status.repoRoot,
    primaryText,
    tertiaryText,
    statusText,
    additions,
    deletions,
    title,
    isEmpty: false,
    isPrimary,
    canRemove: !isPrimary && Boolean(status.repoRoot),
  };
}

function getRepoNameFromRoot(repoRoot: string): string {
  const trimmed = repoRoot.replace(/[\\/]+$/, '');
  const parts = trimmed.split(/[\\/]/);
  return parts[parts.length - 1] ?? '';
}

function statusToRepoBinding(status: GitStatusResponse): GitRepoBinding {
  return {
    repoRoot: status.repoRoot,
    label: status.label || getRepoNameFromRoot(status.repoRoot) || status.role || 'repo',
    role: status.role || (status.isPrimary ? 'cwd' : 'target'),
    source: status.source || 'auto',
    isPrimary: status.isPrimary !== false,
    status,
  };
}

function normalizeGitIndicatorRepos(input: GitIndicatorInput): GitRepoBinding[] {
  if (!input) {
    return [
      {
        repoRoot: '',
        label: t('git.noRepoShort'),
        role: '',
        source: '',
        isPrimary: true,
        status: null,
      },
    ];
  }

  if (Array.isArray(input)) {
    return input;
  }

  return hasGitStatus(input) ? [statusToRepoBinding(input)] : [];
}

function createGitChip(sessionId: string, repo: GitRepoBinding): HTMLButtonElement {
  const btn = createGitIndicatorButton(sessionId);
  patchGitChip(btn, repo);
  return btn;
}

function patchGitChip(btn: HTMLButtonElement, repo: GitRepoBinding): void {
  const status = repo.status ?? null;
  const viewModel = buildGitIndicatorViewModel(repo, status);
  gitChipModels.set(btn, { repoRoot: viewModel.repoRoot });

  const branchSpan = btn.querySelector('.git-indicator-branch');
  const labelSpan = btn.querySelector('.git-indicator-label');
  const separatorSpan = btn.querySelector('.git-indicator-separator');
  const statusSpan = btn.querySelector('.git-indicator-status');
  const statsSpan = btn.querySelector('.git-indicator-stats');
  if (!branchSpan || !separatorSpan || !statusSpan || !statsSpan) return;

  if (labelSpan) {
    labelSpan.textContent = viewModel.tertiaryText;
  }
  branchSpan.textContent = viewModel.primaryText;
  statusSpan.textContent = viewModel.statusText;
  separatorSpan.textContent = viewModel.statusText ? '/' : '';
  statsSpan.innerHTML = buildGitStatsMarkup(viewModel.additions, viewModel.deletions);
  btn.dataset.repoRoot = viewModel.repoRoot;
  btn.title = viewModel.title;
  btn.setAttribute('aria-label', viewModel.title);
  btn.classList.toggle('git-indicator-empty', viewModel.isEmpty);
  btn.classList.toggle('git-indicator-primary', viewModel.isPrimary);
  btn.classList.toggle('git-indicator-removable', viewModel.canRemove);
}

function renderGitRepoChips(strip: HTMLElement, sessionId: string, repos: GitRepoBinding[]): void {
  if (typeof strip.insertBefore !== 'function') {
    const fakeChildren = (strip as unknown as { children?: unknown }).children;
    if (Array.isArray(fakeChildren)) {
      fakeChildren.length = 0;
    }
    for (const repo of repos) {
      strip.appendChild(createGitChip(sessionId, repo));
    }
    return;
  }

  reconcileKeyedChildren(strip, repos, {
    key: (repo) => repo.repoRoot || repo.label,
    create: (repo) => createGitChip(sessionId, repo),
    patch: (element, repo) => {
      patchGitChip(element, repo);
    },
  });
}

function bindGitIndicatorOverflowSync(group: HTMLElement): void {
  if (typeof ResizeObserver !== 'undefined') {
    const observer = new ResizeObserver(() => {
      queueGitIndicatorOverflowSync(group);
    });
    observer.observe(group);
    gitIndicatorResizeObservers.set(group, observer);
  }
}

function queueGitIndicatorOverflowSync(group: HTMLElement): void {
  if (pendingGitIndicatorOverflowSyncs.has(group)) {
    return;
  }

  pendingGitIndicatorOverflowSyncs.add(group);
  const callback = () => {
    pendingGitIndicatorOverflowSyncs.delete(group);
    syncGitIndicatorOverflow(group);
  };

  if (typeof requestAnimationFrame === 'function') {
    requestAnimationFrame(callback);
    return;
  }

  setTimeout(callback, 0);
}

function syncGitIndicatorOverflow(group: HTMLElement): void {
  const strip = group.querySelector<HTMLElement>('.git-indicator-strip');
  const overflow = group.querySelector<HTMLButtonElement>('.git-indicator-overflow');
  if (!strip || !overflow) {
    return;
  }
  const repos = normalizeGitIndicatorRepos(readGitIndicatorData(group));
  const chips = getGitRepoChipElements(strip);
  for (const chip of chips) {
    chip.hidden = false;
    chip.style.display = '';
  }

  const visibleCount = resolveVisibleGitChipCount(strip, chips);
  for (let index = 0; index < chips.length; index += 1) {
    const chip = chips[index];
    if (!chip) {
      continue;
    }

    const visible = index < visibleCount;
    chip.hidden = !visible;
    chip.style.display = visible ? '' : 'none';
  }

  const hiddenCount = Math.max(0, repos.length - visibleCount);
  overflow.hidden = false;
  overflow.textContent = hiddenCount > 0 ? `+${hiddenCount}` : '+';
  overflow.title = hiddenCount > 0 ? `${hiddenCount} more repositories` : 'Git repositories';
}

function getGitRepoChipElements(strip: HTMLElement): HTMLElement[] {
  return Array.from(strip.children).filter(
    (child): child is HTMLElement =>
      'className' in child &&
      typeof child.className === 'string' &&
      child.className.split(/\s+/).includes('git-repo-chip'),
  );
}

function resolveVisibleGitChipCount(strip: HTMLElement, chips: HTMLElement[]): number {
  if (chips.length <= 1) {
    return chips.length;
  }

  const availableWidth = strip.clientWidth;
  if (!Number.isFinite(availableWidth) || availableWidth <= 0) {
    return chips.length;
  }

  const gap = getGitIndicatorStripGap(strip);
  let usedWidth = 0;
  let visibleCount = 0;
  for (const chip of chips) {
    const width = chip.offsetWidth;
    if (!Number.isFinite(width) || width <= 0) {
      return chips.length;
    }

    const nextWidth = usedWidth + (visibleCount > 0 ? gap : 0) + width;
    if (nextWidth > availableWidth) {
      break;
    }

    usedWidth = nextWidth;
    visibleCount += 1;
  }

  return Math.max(1, visibleCount);
}

function getGitIndicatorStripGap(strip: HTMLElement): number {
  if (typeof getComputedStyle !== 'function') {
    return 0;
  }

  const style = getComputedStyle(strip);
  const gap = Number.parseFloat(style.columnGap || style.gap || '0');
  return Number.isFinite(gap) ? gap : 0;
}

function toggleGitRepoPopover(group: HTMLElement, sessionId: string): void {
  const existing = group.querySelector<HTMLElement>('.git-repo-popover');
  if (existing) {
    existing.remove();
    return;
  }

  document.querySelectorAll('.git-repo-popover').forEach((node) => {
    node.remove();
  });
  const repos = normalizeGitIndicatorRepos(readGitIndicatorData(group));
  const popover = document.createElement('div');
  popover.className = 'manager-bar-action-popover git-repo-popover';
  popover.innerHTML = buildGitRepoPopoverHtml(repos);
  popover.addEventListener('click', (event) => {
    const target = event.target as HTMLElement | null;
    const button = target?.closest<HTMLButtonElement>('[data-git-repo-action]');
    if (!button) return;
    const action = button.dataset.gitRepoAction;
    const repoRoot = button.dataset.repoRoot;
    if (action === 'add') {
      gitRepoActionHandlers.add?.(sessionId);
    } else if (action === 'refresh') {
      gitRepoActionHandlers.refresh?.(sessionId, repoRoot);
    } else if (action === 'remove' && repoRoot) {
      gitRepoActionHandlers.remove?.(sessionId, repoRoot);
    } else if (action === 'open' && repoRoot) {
      gitClickHandler?.(repoRoot);
      popover.remove();
    }
  });
  group.appendChild(popover);
}

function readGitIndicatorData(group: HTMLElement): GitRepoBinding[] {
  const raw = group.dataset.gitRepos;
  if (!raw) {
    return [];
  }

  try {
    return JSON.parse(raw) as GitRepoBinding[];
  } catch {
    return [];
  }
}

function buildGitRepoPopoverHtml(repos: GitRepoBinding[]): string {
  const rows =
    repos.length === 0
      ? '<div class="git-repo-popover-empty">No repository tracked</div>'
      : repos
          .map((repo) => {
            const status = repo.status ?? null;
            const branch = status?.branch || 'HEAD';
            const changeCount = status
              ? status.staged.length +
                status.modified.length +
                status.untracked.length +
                status.conflicted.length
              : 0;
            const removeButton = repo.isPrimary
              ? ''
              : `<button type="button" class="git-repo-menu-btn" data-git-repo-action="remove" data-repo-root="${escapeAttribute(repo.repoRoot)}">Remove</button>`;
            return `<div class="git-repo-popover-row">
              <button type="button" class="git-repo-popover-main" data-git-repo-action="open" data-repo-root="${escapeAttribute(repo.repoRoot)}">
                <span class="git-repo-popover-label">${escapeHtml(repo.label || repo.role)}</span>
                <span class="git-repo-popover-meta">${escapeHtml(branch)} · ${changeCount}</span>
              </button>
              <button type="button" class="git-repo-menu-btn" data-git-repo-action="refresh" data-repo-root="${escapeAttribute(repo.repoRoot)}">Refresh</button>
              ${removeButton}
            </div>`;
          })
          .join('');

  return `<div class="git-repo-popover-list">${rows}</div>
    <div class="git-repo-popover-actions">
      <button type="button" class="git-repo-menu-btn git-repo-add" data-git-repo-action="add">Add repo</button>
    </div>`;
}

function escapeHtml(value: string): string {
  return value.replace(/[&<>"']/g, (char) => {
    switch (char) {
      case '&':
        return '&amp;';
      case '<':
        return '&lt;';
      case '>':
        return '&gt;';
      case '"':
        return '&quot;';
      default:
        return '&#39;';
    }
  });
}

function escapeAttribute(value: string): string {
  return escapeHtml(value);
}

function createActionButton(
  actionId: IdeBarActionId,
  className: string,
  title: string,
  label: string,
  iconMarkup: string,
  onClick: () => void,
): HTMLButtonElement {
  const btn = document.createElement('button');
  btn.className = className;
  btn.dataset.action = actionId;
  btn.title = title;
  btn.setAttribute('aria-label', title);
  btn.appendChild(createActionIcon(iconMarkup));
  btn.appendChild(createActionLabel(label));
  btn.addEventListener('click', onClick);
  return btn;
}

let gitClickHandler: ((repoRoot?: string) => void) | null = null;
let webClickHandler: (() => void) | null = null;
let shareClickHandler: ((sessionId: string) => void) | null = null;
let inputHistoryClickHandler: ((sessionId: string, anchor: HTMLElement) => void) | null = null;
export function setCommandsClickHandler(_handler: () => void): void {
  // Commands is temporarily hidden from the IDE bar, so registration is ignored.
}

export function setGitClickHandler(handler: (repoRoot?: string) => void): void {
  gitClickHandler = handler;
}

export function setGitRepoActionHandlers(handlers: GitRepoActionHandlers): void {
  gitRepoActionHandlers = handlers;
}

export function setWebClickHandler(handler: () => void): void {
  webClickHandler = handler;
}

export function setShareClickHandler(handler: (sessionId: string) => void): void {
  shareClickHandler = handler;
}

export function setInputHistoryClickHandler(
  handler: (sessionId: string, anchor: HTMLElement) => void,
): void {
  inputHistoryClickHandler = handler;
}

export function createTabBar(
  sessionId: string,
  onTabSelect: (tab: SessionTabId) => void,
  initialTab: SessionTabId = 'terminal',
): HTMLDivElement {
  const bar = document.createElement('div');
  bar.className = 'session-tab-bar';
  bar.dataset.sessionId = sessionId;

  const cwdSpan = document.createElement('span');
  cwdSpan.className = 'session-cwd';
  cwdSpan.addEventListener('click', () => {
    const cwd = cwdSpan.textContent.trim();
    if (!cwd || typeof navigator.clipboard === 'undefined') {
      return;
    }

    void navigator.clipboard.writeText(cwd).catch(() => {});
  });
  bar.appendChild(cwdSpan);

  const labels = getTabLabels();
  for (const tabId of getVisibleTabs()) {
    const label = labels[tabId];
    const btn = document.createElement('button');
    btn.className = 'session-tab';
    if (tabId === initialTab) btn.classList.add('active');
    btn.dataset.tab = tabId;
    const labelNode = document.createElement('span');
    labelNode.className = 'session-tab-label';
    labelNode.textContent = label;
    btn.appendChild(labelNode);
    if (tabId === 'agent') {
      btn.appendChild(createBetaBadge());
    }
    btn.addEventListener('click', () => {
      onTabSelect(tabId);
    });
    bar.appendChild(btn);
  }

  const actions = document.createElement('div');
  actions.className = 'ide-bar-actions';

  const inputHistoryBtn = createActionButton(
    'inputHistory',
    'ide-bar-btn ide-bar-input-history',
    t('sidebar.inputHistory'),
    t('sidebar.inputHistory'),
    INPUT_HISTORY_BUTTON_ICON,
    () => inputHistoryClickHandler?.(sessionId, inputHistoryBtn),
  );
  inputHistoryBtn.setAttribute('aria-haspopup', 'menu');
  inputHistoryBtn.setAttribute('aria-expanded', 'false');
  actions.appendChild(inputHistoryBtn);

  const webBtn = createActionButton(
    'web',
    'ide-bar-btn ide-bar-web',
    t('sessionTabs.web'),
    t('sessionTabs.webShort'),
    WEB_BUTTON_ICON,
    () => webClickHandler?.(),
  );
  actions.appendChild(webBtn);

  const shareBtn = createActionButton(
    'share',
    'ide-bar-btn ide-bar-share',
    t('sessionTabs.share'),
    t('sessionTabs.share'),
    SHARE_BUTTON_ICON,
    () => shareClickHandler?.(sessionId),
  );
  actions.appendChild(shareBtn);

  const gitGroup = createGitIndicatorGroup(sessionId);
  actions.appendChild(gitGroup);

  bar.appendChild(actions);

  return bar;
}

export function setActiveTab(bar: HTMLDivElement, tabId: SessionTabId): void {
  bar.querySelectorAll('.session-tab').forEach((btn) => {
    btn.classList.toggle('active', btn.getAttribute('data-tab') === tabId);
  });
}

export function setTabVisible(bar: HTMLDivElement, tabId: SessionTabId, visible: boolean): void {
  const btn = bar.querySelector<HTMLButtonElement>(`.session-tab[data-tab="${tabId}"]`);
  if (!btn) {
    return;
  }

  btn.hidden = !visible;
  btn.style.display = visible ? '' : 'none';
}

export function setTabLabel(bar: HTMLDivElement, tabId: SessionTabId, label: string): void {
  const btn = bar.querySelector<HTMLButtonElement>(`.session-tab[data-tab="${tabId}"]`);
  if (!btn) {
    return;
  }

  const labelNode = btn.querySelector<HTMLElement>('.session-tab-label');
  if (labelNode) {
    labelNode.textContent = label;
  }

  btn.title = label;
  btn.setAttribute('aria-label', label);
}

export function isTabVisible(bar: HTMLDivElement, tabId: SessionTabId): boolean {
  const btn = bar.querySelector<HTMLButtonElement>(`.session-tab[data-tab="${tabId}"]`);
  return btn?.hidden !== true;
}

export function setActionActive(
  bar: HTMLDivElement,
  actionId: IdeBarActionId,
  active: boolean,
): void {
  const btn = bar.querySelector(`[data-action="${actionId}"]`);
  btn?.classList.toggle('sidebar-active', active);
}

export function setActionVisible(
  bar: HTMLDivElement,
  actionId: IdeBarActionId,
  visible: boolean,
): void {
  const btn = bar.querySelector<HTMLButtonElement>(`[data-action="${actionId}"]`);
  if (!btn) {
    return;
  }

  btn.hidden = !visible;
  btn.style.display = visible ? '' : 'none';
}

export function updateCwd(bar: HTMLDivElement, cwd: string): void {
  const cwdSpan = bar.querySelector('.session-cwd');
  if (cwdSpan) {
    cwdSpan.textContent = cwd;
    cwdSpan.setAttribute('title', cwd);
  }
}

export function updateGitIndicator(bar: HTMLDivElement, input: GitIndicatorInput): void {
  const group = bar.querySelector<HTMLElement>('.git-indicator-group');
  const strip = group?.querySelector<HTMLElement>('.git-indicator-strip');
  const overflow = group?.querySelector<HTMLButtonElement>('.git-indicator-overflow');
  if (!group || !strip || !overflow) return;

  const repos = normalizeGitIndicatorRepos(input);
  renderGitRepoChips(strip, group.dataset.sessionId ?? '', repos);
  group.dataset.gitRepos = JSON.stringify(repos);
  group.classList.toggle('git-indicator-group-empty', repos.length === 0);
  overflow.hidden = false;
  overflow.textContent = '+';
  overflow.title = 'Git repositories';
  queueGitIndicatorOverflowSync(group);

  const openPopover = group.querySelector<HTMLElement>('.git-repo-popover');
  if (openPopover) {
    openPopover.innerHTML = buildGitRepoPopoverHtml(repos);
  }
}

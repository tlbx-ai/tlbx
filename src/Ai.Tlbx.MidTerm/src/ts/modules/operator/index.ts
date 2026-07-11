import type { Session } from '../../api/types';
import { sessionTerminals } from '../../state';
import { $activeSessionId, $operatorOpen, $sessionList } from '../../stores';
import { formatRelativeAge } from '../history/inputHistoryPanel';
import { closeHistoryDropdown } from '../history';
import { getHubMachines, getHubSidebarSections, subscribeHubState } from '../hub/runtime';
import { t } from '../i18n';
import { createLogger } from '../logging';
import { registerBackButtonLayer } from '../navigation/backButtonGuard';
import { closeSettings } from '../settings';
import { closeSpacesDropdown } from '../spaces';
import {
  fetchControlPlane,
  type ControlPlaneCheckpoint,
  type ControlPlaneSessionStatus,
  type ControlPlaneSnapshot,
  type ControlPlaneWorkItem,
} from './controlPlaneApi';
import { clearControlPlaneNotificationBadge, initControlPlaneNotifications } from './notifications';

interface OperatorViewOptions {
  onSelectSession: (sessionId: string) => void;
}

interface OperatorOrigin {
  machineId: string | null;
  machineName: string;
  snapshot: ControlPlaneSnapshot;
}

interface OperatorSession {
  id: string;
  sourceSessionId: string;
  machineId: string | null;
  machineName: string;
  machineStatus: string;
  session: Session;
}

const log = createLogger('operator');
let options: OperatorViewOptions | null = null;
let view: HTMLElement | null = null;
let refreshTimer: number | null = null;
let refreshGeneration = 0;
let refreshAbortController: AbortController | null = null;
let releaseBackButtonLayer: (() => void) | null = null;
let unsubscribeSessions: (() => void) | null = null;
let unsubscribeHub: (() => void) | null = null;
let origins: OperatorOrigin[] = [];

export function initOperatorView(nextOptions: OperatorViewOptions): void {
  options = nextOptions;
  initControlPlaneNotifications(openOperatorView);
  view = document.getElementById('operator-view');
  document.getElementById('operator-refresh')?.addEventListener('click', () => {
    void refreshOperator();
  });
  document.getElementById('operator-close')?.addEventListener('click', closeOperatorView);
  document.getElementById('btn-operator')?.addEventListener('click', () => {
    closeSpacesDropdown();
    closeHistoryDropdown();
    toggleOperatorView();
  });
  for (const id of ['btn-settings', 'btn-spaces', 'btn-bookmarks', 'btn-input-history']) {
    document.getElementById(id)?.addEventListener('click', closeOperatorView);
  }
  unsubscribeSessions ??= $sessionList.subscribe(() => {
    if ($operatorOpen.get()) renderOperator();
  });
  unsubscribeHub ??= subscribeHubState(() => {
    if ($operatorOpen.get()) void refreshOperator();
  });
}

export function toggleOperatorView(): void {
  if ($operatorOpen.get()) closeOperatorView();
  else openOperatorView();
}

export function openOperatorView(): void {
  if (!view) return;
  closeSettings();
  $operatorOpen.set(true);
  clearControlPlaneNotificationBadge();
  view.classList.remove('hidden');
  document.getElementById('btn-operator')?.classList.add('active');
  document.getElementById('empty-state')?.classList.add('hidden');
  releaseBackButtonLayer ??= registerBackButtonLayer(closeOperatorView);
  startRefreshTimer();
  void refreshOperator();
}

export function closeOperatorView(): void {
  if (!$operatorOpen.get() && view?.classList.contains('hidden')) return;
  $operatorOpen.set(false);
  view?.classList.add('hidden');
  document.getElementById('btn-operator')?.classList.remove('active');
  releaseBackButtonLayer?.();
  releaseBackButtonLayer = null;
  stopRefreshTimer();
  refreshGeneration++;
  refreshAbortController?.abort();
  refreshAbortController = null;
  setLoading(false);

  const activeId = $activeSessionId.get();
  const terminal = activeId ? sessionTerminals.get(activeId) : null;
  if (terminal)
    requestAnimationFrame(() => {
      terminal.terminal.focus();
    });
  else if ($sessionList.get().length === 0)
    document.getElementById('empty-state')?.classList.remove('hidden');
}

async function refreshOperator(): Promise<void> {
  const generation = ++refreshGeneration;
  refreshAbortController?.abort();
  const abortController = new AbortController();
  refreshAbortController = abortController;
  setLoading(true);
  const remoteMachines = getHubMachines().filter(
    (machine) => machine.machine.enabled && machine.status === 'online' && !machine.requiresTrust,
  );
  const requests = [
    fetchControlPlane(null, abortController.signal).then((snapshot) => ({
      machineId: null,
      machineName: t('operator.localMachine'),
      snapshot,
    })),
    ...remoteMachines.map((machine) =>
      fetchControlPlane(machine.machine.id, abortController.signal).then((snapshot) => ({
        machineId: machine.machine.id,
        machineName: machine.machine.name,
        snapshot,
      })),
    ),
  ];

  const results = await Promise.allSettled(requests);
  if (generation !== refreshGeneration || !$operatorOpen.get()) return;
  origins = results
    .filter(
      (result): result is PromiseFulfilledResult<OperatorOrigin> => result.status === 'fulfilled',
    )
    .map((result) => result.value);
  const failures = results.filter((result) => result.status === 'rejected');
  if (failures.length > 0) {
    log.warn(() => `${failures.length.toString()} control-plane source(s) could not be loaded.`);
  }
  refreshAbortController = null;
  setLoading(false);
  renderOperator();
}

function renderOperator(): void {
  if (!view) return;
  const sessions = collectSessions();
  const statuses = collectStatuses();
  const workItems = origins.flatMap((origin) =>
    origin.snapshot.workItems.map((item) => ({ origin, item })),
  );
  const checkpoints = origins.flatMap((origin) =>
    origin.snapshot.checkpoints.map((checkpoint) => ({ origin, checkpoint })),
  );
  renderMetrics(
    sessions,
    statuses,
    workItems.map(({ item }) => item),
  );
  renderSessions(sessions, statuses, workItems);
  renderWorkItems(workItems);
  renderCheckpoints(checkpoints);
}

function collectSessions(): OperatorSession[] {
  const local: OperatorSession[] = $sessionList.get().map((session) => ({
    id: session.id,
    sourceSessionId: session.id,
    machineId: null,
    machineName: t('operator.localMachine'),
    machineStatus: 'online',
    session,
  }));
  const remote = getHubSidebarSections().flatMap((section) =>
    section.sessions.map((session, index) => ({
      id: session.id,
      sourceSessionId: section.machine.sessions[index]?.id ?? session.id,
      machineId: section.machine.machine.id,
      machineName: section.machine.machine.name,
      machineStatus: section.machine.status,
      session,
    })),
  );
  return [...local, ...remote];
}

function collectStatuses(): Map<string, ControlPlaneSessionStatus> {
  const result = new Map<string, ControlPlaneSessionStatus>();
  for (const origin of origins) {
    for (const status of origin.snapshot.sessionStatuses) {
      result.set(originKey(origin.machineId, status.sessionId), status);
    }
  }
  return result;
}

function renderMetrics(
  sessions: readonly OperatorSession[],
  statuses: ReadonlyMap<string, ControlPlaneSessionStatus>,
  workItems: readonly ControlPlaneWorkItem[],
): void {
  const metrics = document.getElementById('operator-metrics');
  if (!metrics) return;
  const openWork = workItems.filter(
    (item) => item.state !== 'done' && item.state !== 'dismissed',
  ).length;
  const needsInput = [...statuses.values()].filter(
    (status) => status.state === 'needsInput',
  ).length;
  metrics.textContent = t('operator.metrics')
    .replace('{sessions}', sessions.length.toString())
    .replace('{work}', openWork.toString())
    .replace('{attention}', needsInput.toString());
}

function renderSessions(
  sessions: readonly OperatorSession[],
  statuses: ReadonlyMap<string, ControlPlaneSessionStatus>,
  workItems: ReadonlyArray<{ origin: OperatorOrigin; item: ControlPlaneWorkItem }>,
): void {
  const container = document.getElementById('operator-sessions');
  if (!container) return;
  if (sessions.length === 0) {
    container.replaceChildren(emptyMessage(t('operator.noSessions')));
    return;
  }

  const fragment = document.createDocumentFragment();
  for (const record of sessions) {
    const status = statuses.get(originKey(record.machineId, record.sourceSessionId));
    const itemCount = workItems.filter(
      ({ origin, item }) =>
        origin.machineId === record.machineId &&
        item.sessionId === record.sourceSessionId &&
        item.state !== 'done' &&
        item.state !== 'dismissed',
    ).length;
    fragment.appendChild(createSessionRow(record, status, itemCount));
  }
  container.replaceChildren(fragment);
}

function createSessionRow(
  record: OperatorSession,
  status: ControlPlaneSessionStatus | undefined,
  itemCount: number,
): HTMLButtonElement {
  const row = document.createElement('button');
  row.type = 'button';
  row.className = 'operator-session-row';
  row.addEventListener('click', () => {
    closeOperatorView();
    options?.onSelectSession(record.id);
  });

  const identity = column('operator-session-identity');
  const name = document.createElement('strong');
  name.textContent = record.session.name || record.session.topic || record.session.shellType;
  identity.append(
    name,
    small(`${record.machineName} · ${record.session.currentDirectory || record.session.id}`),
  );

  const machine = column('operator-session-machine');
  const process =
    record.session.foregroundDisplayName ||
    record.session.foregroundName ||
    record.session.shellType;
  machine.append(strongText(process), small(machineFact(record)));

  const publication = column('operator-session-publication');
  if (status) {
    publication.append(stateBadge(status.state), strongText(status.summary));
    if (status.nextAction) publication.append(small(status.nextAction));
  } else {
    publication.append(small(t('operator.noPublishedStatus')));
  }

  const work = column('operator-session-work');
  work.append(strongText(itemCount.toString()), small(t('operator.openItems')));
  row.append(identity, machine, publication, work);
  return row;
}

function renderWorkItems(
  items: ReadonlyArray<{ origin: OperatorOrigin; item: ControlPlaneWorkItem }>,
): void {
  const container = document.getElementById('operator-work-items');
  if (!container) return;
  const visible = items
    .filter(({ item }) => item.state !== 'done' && item.state !== 'dismissed')
    .sort((a, b) => Date.parse(b.item.updatedAt) - Date.parse(a.item.updatedAt));
  if (visible.length === 0) {
    container.replaceChildren(emptyMessage(t('operator.noWorkItems')));
    return;
  }

  const fragment = document.createDocumentFragment();
  for (const { origin, item } of visible) {
    const row = document.createElement('div');
    row.className = 'operator-work-row';
    const lead = column('operator-work-lead');
    const labels = document.createElement('div');
    labels.className = 'operator-inline-labels';
    labels.append(stateBadge(item.state), kindBadge(item.kind), priorityBadge(item.priority));
    lead.append(labels, strongText(item.title));
    if (item.summary) lead.append(small(item.summary));

    const next = column('operator-work-next');
    next.append(
      small(t('operator.nextAction')),
      strongText(item.nextAction || t('operator.nonePublished')),
    );
    const meta = column('operator-work-meta');
    meta.append(
      strongText(item.project || origin.machineName),
      small(`${item.source} · ${formatAge(item.updatedAt)}`),
    );
    const href = safeHttpUrl(item.url);
    if (href) {
      const link = document.createElement('a');
      link.className = 'operator-work-link';
      link.href = href;
      link.target = '_blank';
      link.rel = 'noopener noreferrer';
      link.textContent = t('operator.open');
      meta.append(link);
    }
    row.append(lead, next, meta);
    fragment.appendChild(row);
  }
  container.replaceChildren(fragment);
}

function renderCheckpoints(
  checkpoints: ReadonlyArray<{ origin: OperatorOrigin; checkpoint: ControlPlaneCheckpoint }>,
): void {
  const container = document.getElementById('operator-checkpoints');
  if (!container) return;
  const visible = [...checkpoints]
    .sort((a, b) => Date.parse(b.checkpoint.createdAt) - Date.parse(a.checkpoint.createdAt))
    .slice(0, 30);
  if (visible.length === 0) {
    container.replaceChildren(emptyMessage(t('operator.noCheckpoints')));
    return;
  }

  const fragment = document.createDocumentFragment();
  for (const { origin, checkpoint } of visible) {
    const row = document.createElement('div');
    row.className = 'operator-checkpoint-row';
    row.append(
      kindBadge(checkpoint.kind),
      strongText(checkpoint.summary),
      small(
        `${checkpoint.project || origin.machineName} · ${checkpoint.source} · ${formatAge(checkpoint.createdAt)}`,
      ),
    );
    fragment.appendChild(row);
  }
  container.replaceChildren(fragment);
}

function machineFact(record: OperatorSession): string {
  if (record.machineStatus !== 'online') return record.machineStatus;
  if (record.session.isRunning) return t('operator.processRunning');
  return record.session.exitCode === null
    ? t('operator.processExited')
    : t('operator.processExitedCode').replace('{code}', record.session.exitCode.toString());
}

function originKey(machineId: string | null, sessionId: string): string {
  return `${machineId ?? 'local'}\u0000${sessionId}`;
}

function column(className: string): HTMLDivElement {
  const element = document.createElement('div');
  element.className = className;
  return element;
}

function strongText(value: string): HTMLElement {
  const element = document.createElement('span');
  element.className = 'operator-primary-text';
  element.textContent = value;
  return element;
}

function small(value: string): HTMLElement {
  const element = document.createElement('span');
  element.className = 'operator-secondary-text';
  element.textContent = value;
  return element;
}

function stateBadge(state: string): HTMLElement {
  const element = document.createElement('span');
  element.className = `operator-badge operator-state operator-state-${state}`;
  element.textContent = formatToken(state);
  return element;
}

function kindBadge(kind: string): HTMLElement {
  const element = document.createElement('span');
  element.className = 'operator-badge operator-kind';
  element.textContent = formatToken(kind);
  return element;
}

function formatToken(value: string): string {
  return value
    .replace(/([a-z0-9])([A-Z])/g, '$1 $2')
    .replace(/[-_]+/g, ' ')
    .toLowerCase();
}

function safeHttpUrl(value: string | null): string | null {
  if (!value) return null;
  try {
    const url = new URL(value, window.location.origin);
    return url.protocol === 'http:' || url.protocol === 'https:' ? url.toString() : null;
  } catch {
    return null;
  }
}

function priorityBadge(priority: string): HTMLElement {
  const element = document.createElement('span');
  element.className = `operator-badge operator-priority operator-priority-${priority}`;
  element.textContent = priority;
  return element;
}

function emptyMessage(value: string): HTMLElement {
  const element = document.createElement('div');
  element.className = 'operator-empty';
  element.textContent = value;
  return element;
}

function formatAge(value: string): string {
  return formatRelativeAge(value);
}

function setLoading(loading: boolean): void {
  document.getElementById('operator-refresh')?.classList.toggle('loading', loading);
}

function startRefreshTimer(): void {
  stopRefreshTimer();
  refreshTimer = window.setInterval(() => {
    void refreshOperator();
  }, 5000);
}

function stopRefreshTimer(): void {
  if (refreshTimer !== null) window.clearInterval(refreshTimer);
  refreshTimer = null;
}

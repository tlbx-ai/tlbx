import type {
  AppServerControlHistorySnapshot,
  AppServerControlUsageSummary,
  AppServerControlUsageLimit,
} from '../../api/types';
import { getAppServerControlUsage } from '../../api/client';
import { $activeSessionId, getSession } from '../../stores';
import { reconcileKeyedChildren } from '../../utils/domReconcile';
import { registerBackButtonLayer } from '../navigation/backButtonGuard';
import { t } from '../i18n';

export type SessionTool = 'status' | 'usage' | 'resume' | 'compact' | 'goal' | 'help';
export interface AgentCommand {
  name: string;
  description: string;
  inputHint?: string | null | undefined;
  local: boolean;
}
interface SessionView {
  snapshot: AppServerControlHistorySnapshot;
  connected: boolean;
  refreshing: boolean;
  error: string;
}
interface DetailRow {
  key: string;
  label: string;
  value: string;
  percent?: number | undefined;
}
const views = new Map<string, SessionView>();
const triggers = new Map<string, HTMLButtonElement>();
let actionHandler: ((sessionId: string, action: SessionTool) => void | Promise<void>) | null = null;
let opened: {
  sessionId: string;
  section: 'status' | 'usage' | 'help';
  dialog: HTMLDialogElement;
  release: () => void;
} | null = null;

export function sessionToolText(key: string): string {
  return t(`agentSession.${key}`);
}

export function setAgentSessionActionHandler(handler: typeof actionHandler): void {
  actionHandler = handler;
}

export function findOpenAgentSession(provider: string, threadId: string): string | null {
  for (const [sessionId, view] of views) {
    if (
      view.snapshot.provider === provider &&
      view.snapshot.thread.threadId === threadId &&
      getSession(sessionId)
    )
      return sessionId;
  }
  return null;
}

export function getAgentSessionCommands(sessionId: string): AgentCommand[] {
  const snapshot = views.get(sessionId)?.snapshot;
  const commands: AgentCommand[] = ['status', 'usage', 'help'].map((name) => ({
    name,
    description: sessionToolText(`command.${name}`),
    local: true,
  }));
  if (snapshot?.provider === 'codex' || snapshot?.provider === 'claude') {
    for (const name of ['resume', 'compact', 'goal'])
      commands.push({ name, description: sessionToolText(`command.${name}`), local: true });
  }
  for (const command of snapshot?.session.availableCommands ?? []) {
    const name = command.name.replace(/^\/+/, '');
    if (!commands.some((entry) => entry.name === name))
      commands.push({
        name,
        description: command.description ?? '',
        inputHint: command.inputHint,
        local: false,
      });
  }
  return commands;
}

export function syncAgentSessionTools(
  snapshot: AppServerControlHistorySnapshot,
  connected: boolean,
): void {
  const view = views.get(snapshot.sessionId);
  if (view) {
    view.snapshot = snapshot;
    view.connected = connected;
  } else views.set(snapshot.sessionId, { snapshot, connected, refreshing: false, error: '' });
  updateTrigger(snapshot.sessionId);
  if (opened?.sessionId === snapshot.sessionId) renderDetails();
}

export function removeAgentSessionTools(sessionId: string): void {
  views.delete(sessionId);
  triggers.get(sessionId)?.remove();
  triggers.delete(sessionId);
  if (opened?.sessionId === sessionId) closeAgentSessionDetails();
}

export function createAgentSessionInfoButton(sessionId: string): HTMLButtonElement {
  let button = triggers.get(sessionId);
  if (!button) {
    button = document.createElement('button');
    button.type = 'button';
    button.className = 'agent-session-info-trigger';
    button.setAttribute('aria-haspopup', 'dialog');
    button.addEventListener('click', () => {
      openAgentSessionDetails(sessionId, 'status');
    });
    triggers.set(sessionId, button);
  }
  updateTrigger(sessionId);
  return button;
}

function updateTrigger(sessionId: string): void {
  const button = triggers.get(sessionId);
  if (!button) return;
  const view = views.get(sessionId);
  const usage = view?.snapshot.session.usage;
  const percent = contextPercent(usage);
  button.textContent = `${sessionToolText('context')}${percent === null ? '' : ` ${percent}%`} ⓘ`;
  button.title = sessionToolText('title');
  button.setAttribute('aria-label', sessionToolText('title'));
  button.disabled = !view;
  button.setAttribute('aria-expanded', String(opened?.sessionId === sessionId));
}

function contextPercent(usage: AppServerControlUsageSummary | null | undefined): number | null {
  if (usage?.contextUsedTokens == null || !usage.contextWindowTokens) return null;
  return Math.round((usage.contextUsedTokens / usage.contextWindowTokens) * 100);
}

export function closeAgentSessionDetails(): void {
  if (!opened) return;
  const { dialog, release, sessionId } = opened;
  opened = null;
  release();
  dialog.close();
  dialog.remove();
  updateTrigger(sessionId);
}

export function openAgentSessionDetails(
  sessionId: string,
  section: 'status' | 'usage' | 'help' = 'status',
): void {
  if ($activeSessionId.get() !== sessionId || !views.has(sessionId)) return;
  if (opened?.sessionId === sessionId) {
    opened.section = section;
    renderDetails();
    if (section === 'usage') void refreshUsage(sessionId);
    return;
  }
  closeAgentSessionDetails();
  const dialog = document.createElement('dialog');
  dialog.className = 'agent-session-details';
  dialog.setAttribute('aria-labelledby', 'agent-session-details-title');
  const title = document.createElement('h2');
  title.id = 'agent-session-details-title';
  title.textContent = sessionToolText('title');
  const close = document.createElement('button');
  close.type = 'button';
  close.className = 'agent-session-close';
  close.textContent = '×';
  close.setAttribute('aria-label', sessionToolText('close'));
  close.addEventListener('click', closeAgentSessionDetails);
  const header = document.createElement('header');
  header.append(title, close);
  const tabs = document.createElement('div');
  tabs.className = 'agent-session-tabs';
  tabs.setAttribute('role', 'tablist');
  for (const tab of ['status', 'usage', 'help'] as const) {
    const button = document.createElement('button');
    button.type = 'button';
    button.dataset.section = tab;
    button.setAttribute('role', 'tab');
    button.id = `agent-session-tab-${tab}`;
    button.setAttribute('aria-controls', 'agent-session-detail-panel');
    button.textContent = sessionToolText(`tab.${tab}`);
    button.addEventListener('click', () => {
      openAgentSessionDetails(sessionId, tab);
    });
    tabs.append(button);
  }
  tabs.addEventListener('keydown', (event) => {
    const buttons = [...tabs.querySelectorAll<HTMLButtonElement>('button')];
    const index = buttons.indexOf(document.activeElement as HTMLButtonElement);
    const next = {
      ArrowRight: (index + 1) % buttons.length,
      ArrowLeft: (index + buttons.length - 1) % buttons.length,
      Home: 0,
      End: buttons.length - 1,
    }[event.key];
    if (next === undefined) return;
    event.preventDefault();
    buttons[next]?.click();
    buttons[next]?.focus();
  });
  const content = document.createElement('div');
  content.className = 'agent-session-content';
  content.setAttribute('role', 'tabpanel');
  content.id = 'agent-session-detail-panel';
  const rows = document.createElement('dl');
  rows.className = 'agent-session-rows';
  const commands = document.createElement('div');
  commands.className = 'agent-session-command-list';
  const notice = document.createElement('p');
  notice.className = 'agent-session-notice';
  notice.setAttribute('role', 'status');
  content.append(rows, commands, notice);
  const footer = document.createElement('footer');
  const refresh = document.createElement('button');
  refresh.type = 'button';
  refresh.dataset.action = 'refresh';
  refresh.textContent = sessionToolText('refresh');
  refresh.addEventListener('click', () => void refreshUsage(sessionId));
  footer.append(refresh);
  dialog.append(header, tabs, content, footer);
  dialog.addEventListener('cancel', (event) => {
    event.preventDefault();
    closeAgentSessionDetails();
  });
  dialog.addEventListener('click', (event) => {
    if (event.target !== dialog) return;
    const bounds = dialog.getBoundingClientRect();
    if (
      event.clientX < bounds.left ||
      event.clientX > bounds.right ||
      event.clientY < bounds.top ||
      event.clientY > bounds.bottom
    )
      closeAgentSessionDetails();
  });
  document.body.append(dialog);
  opened = {
    sessionId,
    section,
    dialog,
    release: registerBackButtonLayer(closeAgentSessionDetails),
  };
  dialog.showModal();
  renderDetails();
  updateTrigger(sessionId);
  tabs.querySelector<HTMLButtonElement>(`[data-section="${section}"]`)?.focus();
  if (section === 'usage') void refreshUsage(sessionId);
}

async function refreshUsage(sessionId: string): Promise<void> {
  const view = views.get(sessionId);
  if (!view || view.refreshing) return;
  const threadId = view.snapshot.thread.threadId;
  view.refreshing = true;
  view.error = '';
  renderDetails();
  try {
    const usage = await getAppServerControlUsage(sessionId);
    if (views.get(sessionId) !== view || view.snapshot.thread.threadId !== threadId) return;
    view.snapshot = { ...view.snapshot, session: { ...view.snapshot.session, usage } };
    updateTrigger(sessionId);
  } catch (error) {
    if (views.get(sessionId) === view)
      view.error = error instanceof Error ? error.message : String(error);
  } finally {
    view.refreshing = false;
    if (opened?.sessionId === sessionId) renderDetails();
  }
}

function count(value: number | null | undefined): string {
  return value == null ? sessionToolText('notReported') : new Intl.NumberFormat().format(value);
}

function statusRows(view: SessionView): DetailRow[] {
  const snapshot = view.snapshot;
  const settings = snapshot.quickSettings;
  return [
    {
      key: 'provider',
      label: sessionToolText('provider'),
      value: snapshot.session.agentName || snapshot.provider,
    },
    {
      key: 'model',
      label: sessionToolText('model'),
      value: settings.model || sessionToolText('notReported'),
    },
    {
      key: 'effort',
      label: sessionToolText('effort'),
      value: settings.effort || sessionToolText('notReported'),
    },
    { key: 'permissions', label: sessionToolText('permissions'), value: settings.permissionMode },
    { key: 'plan', label: sessionToolText('plan'), value: settings.planMode },
    {
      key: 'fast',
      label: sessionToolText('fast'),
      value: settings.fastMode ?? sessionToolText('notReported'),
    },
    {
      key: 'directory',
      label: sessionToolText('directory'),
      value: getSession(snapshot.sessionId)?.currentDirectory || sessionToolText('notReported'),
    },
    {
      key: 'connection',
      label: sessionToolText('connection'),
      value: sessionToolText(view.connected ? 'connected' : 'reconnecting'),
    },
    {
      key: 'state',
      label: sessionToolText('state'),
      value: snapshot.session.stateLabel || snapshot.session.state,
    },
    { key: 'thread', label: sessionToolText('conversation'), value: snapshot.thread.threadId },
  ];
}

function usageRows(usage: AppServerControlUsageSummary | null | undefined): DetailRow[] {
  const data = usage ?? {};
  const rows: DetailRow[] = [
    {
      key: 'context',
      label: sessionToolText('context'),
      value: `${count(data.contextUsedTokens)} / ${count(data.contextWindowTokens)}`,
      percent: contextPercent(usage) ?? undefined,
    },
    { key: 'input', label: sessionToolText('input'), value: count(data.inputTokens) },
    { key: 'output', label: sessionToolText('output'), value: count(data.outputTokens) },
    {
      key: 'cachedRead',
      label: sessionToolText('cachedRead'),
      value: count(data.cachedReadTokens),
    },
    {
      key: 'cachedWrite',
      label: sessionToolText('cachedWrite'),
      value: count(data.cachedWriteTokens),
    },
    { key: 'reasoning', label: sessionToolText('reasoning'), value: count(data.reasoningTokens) },
    {
      key: 'cost',
      label: sessionToolText('cost'),
      value:
        data.costAmount != null && data.costCurrency
          ? `${new Intl.NumberFormat(undefined, { maximumSignificantDigits: 5 }).format(data.costAmount)} ${data.costCurrency} · ${sessionToolText('reported')}`
          : sessionToolText('notReported'),
    },
    {
      key: 'account',
      label: sessionToolText('account'),
      value:
        [data.accountLabel, data.accountPlan].filter(Boolean).join(' · ') ||
        sessionToolText('notReported'),
    },
  ];
  for (const limit of data.limits ?? []) rows.push(...limitRows(limit));
  if (!data.limits?.length)
    rows.push({
      key: 'limits',
      label: sessionToolText('limits'),
      value: sessionToolText('notReported'),
    });
  if (data.limitsCheckedAt)
    rows.push({
      key: 'checked',
      label: sessionToolText('checked'),
      value: new Date(data.limitsCheckedAt).toLocaleString(),
    });
  return rows;
}

function limitRows(limit: AppServerControlUsageLimit): DetailRow[] {
  const window = limit.windowMinutes;
  const duration = window ? (window >= 1440 ? `${window / 1440}d` : `${window / 60}h`) : '';
  const extra = limit.label !== limit.id && !['5h', '7d'].includes(limit.label) ? limit.label : '';
  const label = duration ? [duration, extra].filter(Boolean).join(' · ') : limit.label;
  const rows: DetailRow[] = [
    {
      key: `limit:${limit.id}`,
      label: `${sessionToolText('limit')} ${label}`,
      value:
        limit.usedPercent == null
          ? sessionToolText('notReported')
          : `${Math.max(0, 100 - limit.usedPercent).toFixed(0)}% ${sessionToolText('remaining')}`,
      percent: limit.usedPercent ?? undefined,
    },
  ];
  if (limit.resetsAt)
    rows.push({
      key: `reset:${limit.id}`,
      label: sessionToolText('resets'),
      value: new Date(limit.resetsAt).toLocaleString(),
    });
  return rows;
}

function patchDetailRow(element: HTMLElement, row: DetailRow): void {
  const label = element.querySelector('dt');
  const value = element.querySelector('dd');
  if (label) label.textContent = row.label;
  if (value) value.textContent = row.value;
  element.dataset.field = row.key;
  element.style.setProperty('--usage', `${Math.min(100, Math.max(0, row.percent ?? 0))}%`);
  element.classList.toggle('agent-session-meter', row.percent !== undefined);
}

function detailNotice(view: SessionView, section: string): string {
  if (view.error) return view.error;
  if (view.refreshing) return sessionToolText('loading');
  return section === 'usage' ? sessionToolText('usageScope') : '';
}

function renderDetails(): void {
  if (!opened) return;
  const { sessionId, section, dialog } = opened;
  const view = views.get(sessionId);
  if (!view) {
    closeAgentSessionDetails();
    return;
  }
  for (const button of dialog.querySelectorAll<HTMLButtonElement>('[data-section]')) {
    const selected = button.dataset.section === section;
    button.setAttribute('aria-selected', String(selected));
    button.tabIndex = selected ? 0 : -1;
  }
  const panel = dialog.querySelector('[role=tabpanel]');
  if (panel) panel.setAttribute('aria-labelledby', `agent-session-tab-${section}`);
  const host = dialog.querySelector<HTMLDListElement>('.agent-session-rows');
  const commands = dialog.querySelector<HTMLElement>('.agent-session-command-list');
  const refresh = dialog.querySelector<HTMLButtonElement>('[data-action="refresh"]');
  const notice = dialog.querySelector<HTMLElement>('.agent-session-notice');
  if (!host || !commands || !refresh || !notice) return;
  const rows =
    section === 'help'
      ? []
      : section === 'usage'
        ? usageRows(view.snapshot.session.usage)
        : statusRows(view);
  reconcileKeyedChildren(host, rows, {
    key: (row) => row.key,
    create: () => {
      const row = document.createElement('div');
      row.append(document.createElement('dt'), document.createElement('dd'));
      return row;
    },
    patch: patchDetailRow,
  });
  reconcileKeyedChildren(commands, section === 'help' ? getAgentSessionCommands(sessionId) : [], {
    key: (command) => command.name,
    create: (command) => {
      const button = document.createElement('button');
      button.type = 'button';
      button.addEventListener('click', () => {
        closeAgentSessionDetails();
        if (command.local) void actionHandler?.(sessionId, command.name as SessionTool);
        else
          window.dispatchEvent(
            new CustomEvent('tlbx:insert-agent-command', {
              detail: { sessionId, text: `/${command.name} ` },
            }),
          );
      });
      return button;
    },
    patch: (button, command) => {
      button.textContent = `/${command.name} — ${command.description}`;
    },
  });
  refresh.hidden = section !== 'usage';
  refresh.disabled = view.refreshing;
  notice.textContent = detailNotice(view, section);
}

if (typeof window !== 'undefined')
  $activeSessionId.subscribe((sessionId) => {
    if (opened && opened.sessionId !== sessionId) closeAgentSessionDetails();
  });

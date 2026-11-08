import { readGoalDraft, writeGoalDraft, clearGoalDraft, type GoalDraft } from './goalDrafts';
import type {
  AppServerControlGoalSetRequest,
  AppServerControlGoalResponse,
  AppServerControlGoalState,
  AppServerControlHistorySnapshot,
} from '../../api/types';
import { setAppServerControlGoal } from '../../api/client';
import { registerBackButtonLayer } from '../navigation/backButtonGuard';
import { t } from '../i18n';

export const GOAL_CHANGED_EVENT = 'tlbx:agent-goal-changed';
interface GoalView {
  provider: string;
  threadId: string;
  turnId: string | null;
  connected: boolean;
  state: AppServerControlGoalState | null;
  revision: number;
  commandRevision: number;
  commandOperation: string | null;
  pending: boolean;
  error: string;
}
const views = new Map<string, GoalView>();
interface GoalMutation {
  threadId: string;
  patch: Omit<AppServerControlGoalSetRequest, 'expectedThreadId' | 'expectedTurnId'>;
  draft: GoalDraft | null;
  revision: number;
}
const mutations = new Map<string, GoalMutation>();
function clearConfirmedDraft(sessionId: string, view: GoalView): void {
  const sent = mutations.get(sessionId);
  if (!sent || sent.threadId !== view.threadId || !goalConfirmationSettled(view.state)) return;
  const goal = view.state?.goal;
  const matches =
    sent.patch.operation === 'clear'
      ? !goal
      : goal &&
        goal.objective === sent.patch.objective &&
        (sent.patch.status === undefined || goal.status === sent.patch.status) &&
        (sent.patch.tokenBudget === undefined ||
          (goal.tokenBudget ?? null) === sent.patch.tokenBudget);
  if (!matches || view.revision <= sent.revision) return;
  clearGoalDraft(sessionId, view.threadId, sent.draft);
  mutations.delete(sessionId);
}

function changed(sessionId: string): void {
  if (typeof window === 'undefined' || typeof window.dispatchEvent !== 'function') return;
  window.dispatchEvent(new CustomEvent(GOAL_CHANGED_EVENT, { detail: { sessionId } }));
}

function goalConfirmationSettled(state: AppServerControlGoalState | null): boolean {
  return Boolean(
    state && state.confirmation === 'confirmed' && !state.pendingOperation && !state.detail,
  );
}

function nextGoalView(
  target: Pick<GoalView, 'provider' | 'threadId' | 'turnId' | 'connected' | 'state'>,
  previous: GoalView | undefined,
): GoalView {
  const revision = previous ? previous.revision : 0;
  const sameState = previous && JSON.stringify(previous.state) === JSON.stringify(target.state);
  const sameThread = previous && previous.threadId === target.threadId;
  return {
    ...target,
    revision: revision + (sameState ? 0 : 1),
    commandRevision: previous ? previous.commandRevision : 0,
    commandOperation: previous ? previous.commandOperation : null,
    pending: sameThread ? previous.pending : false,
    error:
      sameThread && (sameState || !goalConfirmationSettled(target.state)) ? previous.error : '',
  };
}

export function syncAppServerControlGoal(
  snapshot: AppServerControlHistorySnapshot,
  connected: boolean,
): void {
  const previous = views.get(snapshot.sessionId);
  const active = ['running', 'in_progress', 'submitted'].includes(snapshot.currentTurn.state);
  const target = {
    provider: snapshot.provider,
    threadId: snapshot.thread.threadId === snapshot.sessionId ? '' : snapshot.thread.threadId,
    turnId: active ? (snapshot.currentTurn.turnId ?? null) : null,
    connected,
    state: snapshot.thread.goal ?? null,
  };
  const next = nextGoalView(target, previous);
  if (JSON.stringify(next) === JSON.stringify(previous)) return;
  views.set(snapshot.sessionId, next);
  clearConfirmedDraft(snapshot.sessionId, next);
  changed(snapshot.sessionId);
}

export function goalControlLabel(sessionId: string): string {
  const view = views.get(sessionId);
  if (!view) return t('goal.label');
  if (view.pending || view.state?.pendingOperation)
    return `${t('goal.label')} · ${t('goal.pending')}`;
  const goal = view.state?.goal;
  return goal ? `${t('goal.label')} · ${t(`goal.state.${goal.status}`)}` : t('goal.label');
}

export function goalControlTitle(sessionId: string): string {
  const view = views.get(sessionId);
  return view
    ? [view.state?.goal?.objective, goalStatusText(view)].filter(Boolean).join('\n')
    : t('goal.editor');
}

export function goalGuard(
  view: Pick<GoalView, 'threadId' | 'turnId' | 'connected'>,
): Pick<AppServerControlGoalSetRequest, 'expectedThreadId' | 'expectedTurnId'> {
  if (!view.connected || !view.threadId) throw new Error(t('goal.targetUnavailable'));
  return { expectedThreadId: view.threadId, expectedTurnId: view.turnId };
}

function currentGoalCommand(
  sessionId: string,
  threadId: string,
  revision: number,
): GoalView | null {
  const current = views.get(sessionId);
  if (!current || current.threadId !== threadId || current.commandRevision !== revision)
    return null;
  return current;
}

export async function controlAppServerControlGoal(
  sessionId: string,
  patch: Omit<AppServerControlGoalSetRequest, 'expectedThreadId' | 'expectedTurnId'>,
): Promise<void> {
  const view = views.get(sessionId);
  if (!view) return;
  const revision = view.revision;
  const commandRevision = ++view.commandRevision;
  view.commandOperation = patch.operation ?? 'set';
  try {
    const guard = goalGuard(view);
    if (patch.operation === 'set' || patch.operation === 'clear')
      mutations.set(sessionId, {
        threadId: view.threadId,
        patch,
        draft: readGoalDraft(sessionId, view.threadId),
        revision,
      });
    view.pending = true;
    view.error = '';
    changed(sessionId);
    const receipt = await setAppServerControlGoal(sessionId, { ...patch, ...guard });
    const current = currentGoalCommand(sessionId, guard.expectedThreadId, commandRevision);
    if (!current) return;
    if (receipt.status === 'targetUnavailable') throw new Error(t('goal.targetUnavailable'));
    applyGoalReceipt(sessionId, current, revision, receipt);
  } catch (error) {
    const current = currentGoalCommand(sessionId, view.threadId, commandRevision);
    if (current) {
      mutations.delete(sessionId);
      current.pending = false;
      current.error = error instanceof Error ? error.message : t('goal.unconfirmed');
    }
  }
  changed(sessionId);
}

function applyGoalReceipt(
  sessionId: string,
  current: GoalView,
  revision: number,
  receipt: AppServerControlGoalResponse,
): void {
  if (current.revision === revision && receipt.state) current.state = receipt.state;
  current.pending = false;
  if (receipt.status === 'unconfirmed') {
    current.error = t('goal.unconfirmed');
    mutations.delete(sessionId);
  }
  if (receipt.status === 'applied' && goalConfirmationSettled(receipt.state ?? null)) {
    current.revision++;
    clearConfirmedDraft(sessionId, current);
  }
}

function goalStatusText(view: GoalView): string {
  const state = view.state;
  const parts = [
    view.error,
    view.pending || state?.pendingOperation ? t('goal.pending') : '',
    t(`goal.${state?.confirmation ?? 'unknown'}`),
  ];
  if (state?.goal) parts.push(...goalMetrics(state.goal));
  if (state?.detail) parts.push(goalDetail(state.detail));
  return parts.filter(Boolean).join(' · ');
}

function goalDetail(detail: string): string {
  if (detail === 'targetUnavailable') return t('goal.targetUnavailable');
  if (detail === 'waitingForTurn') return t('goal.waitingForTurn');
  if (detail === 'timeout') return t('goal.timeout');
  if (detail === 'recovering') return t('goal.recovering');
  if (detail === 'recoveryFailed') return t('goal.recoveryFailed');
  if (detail === 'clearReadbackMismatch') return t('goal.clearReadbackMismatch');
  return detail;
}

function goalMetrics(goal: NonNullable<AppServerControlGoalState['goal']>): string[] {
  const parts = [`${t('goal.status')}: ${t(`goal.state.${goal.status}`)}`];
  const fields = [
    ['iterations', 'iterations'],
    ['tokensUsed', 'tokens'],
    ['timeUsedSeconds', 'seconds'],
  ] as const;
  for (const [field, label] of fields) {
    const value = goal[field];
    if (value !== null && value !== undefined) parts.push(`${t(`goal.${label}`)}: ${value}`);
  }
  if (goal.tokenBudget !== null && goal.tokenBudget !== undefined)
    parts.push(`${t('goal.budget')}: ${goal.tokenBudget}`);
  if (goal.lastReason) parts.push(goal.lastReason);
  return parts;
}

function goalStatusSelect(view: GoalView): HTMLSelectElement {
  const status = document.createElement('select');
  status.ariaLabel = t('goal.status');
  for (const value of [
    'active',
    'paused',
    'blocked',
    'usageLimited',
    'budgetLimited',
    'complete',
  ]) {
    const option = document.createElement('option');
    option.value = value;
    option.textContent = t(`goal.state.${value}`);
    status.append(option);
  }
  status.value = view.state?.goal?.status ?? 'active';
  status.hidden = view.provider !== 'codex';
  return status;
}

function goalBudgetInput(view: GoalView): HTMLInputElement {
  const budget = document.createElement('input');
  budget.type = 'number';
  budget.min = '0';
  budget.step = '1';
  budget.ariaLabel = budget.placeholder = t('goal.budget');
  budget.hidden = view.provider !== 'codex';
  budget.value = view.state?.goal?.tokenBudget?.toString() ?? '';
  return budget;
}

function button(label: string, action: () => void): HTMLButtonElement {
  const result = document.createElement('button');
  result.type = 'button';
  result.className = 'btn-secondary';
  result.textContent = label;
  result.addEventListener('click', action);
  return result;
}

function focusGuard(event: KeyboardEvent, modal: HTMLElement, close: () => void): void {
  if (event.key === 'Escape') {
    event.preventDefault();
    event.stopPropagation();
    close();
    return;
  }
  if (event.key !== 'Tab') return;
  const controls = Array.from(
    modal.querySelectorAll<HTMLElement>(
      'button:not(:disabled),textarea,select:not([hidden]),input:not([hidden])',
    ),
  );
  const first = controls[0];
  const last = controls[controls.length - 1];
  if (event.shiftKey && document.activeElement === first) {
    event.preventDefault();
    last?.focus();
  } else if (!event.shiftKey && document.activeElement === last) {
    event.preventDefault();
    first?.focus();
  }
}

interface GoalEditorFields {
  objective: HTMLTextAreaElement;
  status: HTMLSelectElement;
  budget: HTMLInputElement;
  edited: boolean;
  statusEdited: boolean;
  budgetEdited: boolean;
}

function syncGoalEditorFields(
  fields: GoalEditorFields,
  state: AppServerControlGoalState | null,
): void {
  const goal = state?.goal;
  if (!fields.edited) fields.objective.value = goal?.objective ?? '';
  if (!fields.statusEdited) fields.status.value = goal?.status ?? 'active';
  if (!fields.budgetEdited) fields.budget.value = goal?.tokenBudget?.toString() ?? '';
}

function saveGoalEditor(sessionId: string, provider: string, fields: GoalEditorFields): void {
  const objective = fields.objective.value.trim();
  if (!objective) throw new Error(t('goal.objectiveRequired'));
  const patch: Omit<AppServerControlGoalSetRequest, 'expectedThreadId' | 'expectedTurnId'> = {
    operation: 'set',
    objective,
  };
  if (provider === 'codex') {
    const value = fields.budget.value ? Number(fields.budget.value) : null;
    if (value !== null && (!Number.isSafeInteger(value) || value < 0))
      throw new Error(t('goal.invalidBudget'));
    patch.status = fields.status.value;
    patch.tokenBudget = value;
  }
  void controlAppServerControlGoal(sessionId, patch);
}

function goalEditorUnavailable(view: GoalView): boolean {
  return !view.connected || !view.threadId;
}

function canStopGoal(view: GoalView): boolean {
  if (goalEditorUnavailable(view) || view.state?.pendingOperation === 'clear') return false;
  if (view.pending && view.commandOperation === 'clear') return false;
  return Boolean(view.state?.goal || view.pending || view.state?.pendingOperation);
}

function goalRecoveryAvailable(view: GoalView): boolean {
  return (
    view.provider === 'claude' &&
    (view.state?.confirmation === 'unconfirmed' || Boolean(view.state?.pendingOperation))
  );
}

export function openAppServerControlGoalEditor(sessionId: string): void {
  const initial = views.get(sessionId);
  if (!initial) return;
  const editorThreadId = initial.threadId;
  const editorProvider = initial.provider;
  const priorFocus = document.activeElement instanceof HTMLElement ? document.activeElement : null;
  const overlay = document.createElement('div');
  overlay.className = 'modal-overlay agent-goal-overlay';
  const modal = document.createElement('div');
  modal.className = 'modal agent-goal-editor';
  modal.setAttribute('role', 'dialog');
  modal.setAttribute('aria-modal', 'true');
  modal.ariaLabel = t('goal.editor');
  const header = document.createElement('div');
  header.className = 'modal-header';
  header.textContent = t('goal.editor');
  const body = document.createElement('div');
  body.className = 'modal-body';
  const current = document.createElement('p');
  const readback = document.createElement('p');
  readback.className = 'agent-goal-status';
  readback.setAttribute('role', 'status');
  const scope = document.createElement('p');
  scope.className = 'agent-goal-scope';
  scope.textContent =
    t(editorProvider === 'claude' ? 'goal.scopeClaude' : 'goal.scopeCodex') +
    (editorProvider === 'claude' ? ` ${t('goal.recoverScope')}` : '');
  const objective = document.createElement('textarea');
  objective.className = 'agent-goal-objective';
  objective.rows = 4;
  objective.ariaLabel = t('goal.objective');
  objective.placeholder = t('goal.objective');
  const draft = readGoalDraft(sessionId, editorThreadId);
  objective.value = draft?.objective ?? initial.state?.goal?.objective ?? '';

  const status = goalStatusSelect(initial);
  const budget = goalBudgetInput(initial);
  if (draft) {
    status.value = draft.status;
    budget.value = draft.budget;
  }
  const fields: GoalEditorFields = {
    objective,
    status,
    budget,
    edited: Boolean(draft),
    statusEdited: Boolean(draft),
    budgetEdited: Boolean(draft),
  };
  const persist = (): void => {
    writeGoalDraft(sessionId, editorThreadId, {
      objective: objective.value,
      status: status.value,
      budget: budget.value,
    });
  };
  objective.addEventListener('input', () => {
    fields.edited = true;
    persist();
  });
  status.addEventListener('change', () => {
    fields.statusEdited = true;
    persist();
  });
  budget.addEventListener('input', () => {
    fields.budgetEdited = true;
    persist();
  });
  const footer = document.createElement('div');
  footer.className = 'modal-footer';
  let release = (): void => {
    /* Registered after mounting. */
  };
  const close = (): void => {
    window.removeEventListener(GOAL_CHANGED_EVENT, render);
    release();
    overlay.remove();
    if (priorFocus?.isConnected) priorFocus.focus({ preventScroll: true });
  };
  const save = button(t('goal.apply'), () => {
    try {
      saveGoalEditor(sessionId, editorProvider, fields);
    } catch (error) {
      readback.textContent = error instanceof Error ? error.message : t('goal.unconfirmed');
    }
  });
  const refresh = button(t('goal.refresh'), () => {
    void controlAppServerControlGoal(sessionId, { operation: 'get' });
  });
  const stop = button(
    t(editorProvider === 'claude' ? 'goal.interruptAndStop' : 'goal.stop'),
    () => {
      void controlAppServerControlGoal(sessionId, {
        operation: 'clear',
        interruptActiveTurn: editorProvider === 'claude',
      });
    },
  );
  const recover = button(t('goal.recover'), () => {
    void controlAppServerControlGoal(sessionId, { operation: 'recover' });
  });
  recover.hidden = editorProvider !== 'claude';
  recover.title = t('goal.recoverScope');
  footer.append(refresh, button(t('dialog.cancel'), close), recover, stop, save);
  body.append(current, readback, scope, objective, status, budget);
  modal.append(header, body, footer);
  overlay.append(modal);
  function render(): void {
    const view = views.get(sessionId);
    if (!view || view.threadId !== editorThreadId) {
      close();
      return;
    }
    if (!readGoalDraft(sessionId, editorThreadId)) {
      fields.edited = fields.statusEdited = fields.budgetEdited = false;
    }
    const goal = view.state?.goal;
    syncGoalEditorFields(fields, view.state);
    current.textContent = goal
      ? `${t('goal.current')}: ${goal.objective}`
      : t(view.state?.confirmation === 'confirmed' ? 'goal.none' : 'goal.unknown');
    readback.textContent = goalStatusText(view);
    const waiting = view.pending || Boolean(view.state?.pendingOperation);
    save.disabled = refresh.disabled = goalEditorUnavailable(view) || waiting;
    stop.disabled = !canStopGoal(view);
    recover.disabled = goalEditorUnavailable(view) || view.pending;
    recover.hidden = !goalRecoveryAvailable(view);
  }
  overlay.addEventListener('keydown', (event) => {
    focusGuard(event, modal, close);
  });
  document.body.append(overlay);
  release = registerBackButtonLayer(close);
  window.addEventListener(GOAL_CHANGED_EVENT, render);
  render();
  objective.focus();
  if (!initial.pending && !initial.state?.pendingOperation)
    void controlAppServerControlGoal(sessionId, { operation: 'get' });
}

import type { AppServerControlAttachmentReference } from '../../api/types';
import { AppServerControlHttpError } from '../../api/client';
import type {
  HistoryKind,
  HistoryTone,
  AppServerControlActivationIssue,
  AppServerControlHistoryAction,
  AppServerControlLayoutMode,
  SessionAppServerControlViewState,
} from './types';
import { t } from '../i18n';

export const STALE_APP_SERVER_CONTROL_ACTIVATION = '__midterm_stale_appServerControl_activation__';

function appServerControlText(key: string, fallback: string): string {
  const translated = t(key);
  if (!translated || translated === key) {
    return fallback;
  }

  return translated;
}

function appServerControlFormat(
  key: string,
  fallback: string,
  replacements: Record<string, string | number>,
): string {
  return Object.entries(replacements).reduce(
    (text, [name, value]) => text.split(`{${name}}`).join(String(value)),
    appServerControlText(key, fallback),
  );
}

export function prettify(value: string): string {
  return value
    .replace(/[_./-]+/g, ' ')
    .replace(/\b\w/g, (char) => char.toUpperCase())
    .trim();
}

export function formatAbsoluteTime(value: string): string {
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) {
    return value;
  }

  return new Intl.DateTimeFormat(undefined, {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  }).format(date);
}

export function formatClockTime(value: Date): string {
  return new Intl.DateTimeFormat(undefined, {
    hour: '2-digit',
    minute: '2-digit',
    second: '2-digit',
  }).format(value);
}

export function appendActivationTrace(
  state: SessionAppServerControlViewState,
  tone: HistoryTone,
  phase: string,
  summary: string,
  detail: string,
): void {
  state.activationTrace = [
    ...state.activationTrace,
    {
      tone,
      meta: `${prettify(phase)} • ${formatClockTime(new Date())}`,
      summary,
      detail,
    },
  ].slice(-12);
}

export function setActivationState(
  state: SessionAppServerControlViewState,
  activationState: SessionAppServerControlViewState['activationState'],
  activationDetail: string,
  summary: string,
  detail: string,
  tone: HistoryTone = 'info',
): void {
  state.activationState = activationState;
  state.activationDetail = activationDetail;
  appendActivationTrace(state, tone, activationState, summary, detail);
}

export function describeError(error: unknown): string {
  if (error instanceof Error) {
    const message = error.message.trim();
    if (message) {
      return message;
    }

    const firstStackLine = error.stack?.split('\n', 1)[0]?.trim();
    return firstStackLine || error.name;
  }

  return typeof error === 'string' ? error : JSON.stringify(error, null, 2);
}

export function classifyAppServerControlActivationIssue(
  error: unknown,
  hasReadonlyHistory: boolean,
): AppServerControlActivationIssue {
  const description = describeError(error);
  const detail =
    error instanceof AppServerControlHttpError && error.detail.trim()
      ? error.detail.trim()
      : description;
  const normalizedDetail = detail.toLowerCase();
  const actions: AppServerControlHistoryAction[] = [
    {
      id: 'retry-appServerControl',
      label: appServerControlText('appServerControl.action.retry', 'Retry AppServerControl'),
      style: 'primary',
      busyLabel: appServerControlText('appServerControl.action.retryBusy', 'Retrying...'),
    },
  ];

  if (
    normalizedDetail.includes(
      'finish or interrupt the terminal codex turn before opening appservercontrol',
    )
  ) {
    return {
      kind: 'busy-terminal-turn',
      tone: 'warning',
      meta: hasReadonlyHistory
        ? appServerControlText('appServerControl.issue.readonlyHistory', 'Read-only history')
        : appServerControlText('appServerControl.issue.terminalBusy', 'Terminal busy'),
      title: appServerControlText(
        'appServerControl.issue.busyTerminalTurn.title',
        'Terminal owns the live Codex turn',
      ),
      body: hasReadonlyHistory
        ? appServerControlText(
            'appServerControl.issue.busyTerminalTurn.bodyReadonly',
            'AppServerControl is showing the last stable history while the terminal Codex turn is still running. Finish or interrupt that turn in Terminal, then retry live AppServerControl attach.',
          )
        : appServerControlText(
            'appServerControl.issue.busyTerminalTurn.body',
            'AppServerControl cannot take over while Terminal still owns the active Codex turn. Finish or interrupt that turn in Terminal, then retry.',
          ),
      actions,
    };
  }

  if (normalizedDetail.includes('could not determine the codex resume id for this session')) {
    return {
      kind: 'missing-resume-id',
      tone: 'warning',
      meta: hasReadonlyHistory
        ? appServerControlText('appServerControl.issue.readonlyHistory', 'Read-only history')
        : appServerControlText(
            'appServerControl.issue.liveAttachUnavailable',
            'Live attach unavailable',
          ),
      title: appServerControlText(
        'appServerControl.issue.missingResumeId.title',
        'No resumable Codex thread is known yet',
      ),
      body: hasReadonlyHistory
        ? appServerControlText(
            'appServerControl.issue.missingResumeId.bodyReadonly',
            'AppServerControl can still show canonical history, but tlbx does not yet know a resumable Codex thread id for live handoff in this session. Keep using Terminal for the live lane, or retry after the thread identity becomes known.',
          )
        : appServerControlText(
            'appServerControl.issue.missingResumeId.body',
            'tlbx cannot determine a resumable Codex thread id for this session yet, so live AppServerControl attach is unavailable. Use Terminal for the live lane, or retry later.',
          ),
      actions,
    };
  }

  if (normalizedDetail.includes('terminal shell did not recover after stopping codex')) {
    return {
      kind: 'shell-recovery-failed',
      tone: 'warning',
      meta: appServerControlText(
        'appServerControl.issue.terminalRecoveryFailed',
        'Terminal recovery failed',
      ),
      title: appServerControlText(
        'appServerControl.issue.shellRecoveryFailed.title',
        'Terminal did not recover cleanly after handoff',
      ),
      body: appServerControlText(
        'appServerControl.issue.shellRecoveryFailed.body',
        'tlbx stopped the foreground Codex process but the session did not settle back into a clean live lane. Retry AppServerControl once the lane is stable again.',
      ),
      actions,
    };
  }

  if (
    normalizedDetail.includes('appservercontrol native runtime is not available for this session')
  ) {
    return {
      kind: 'native-runtime-unavailable',
      tone: 'warning',
      meta: appServerControlText(
        'appServerControl.issue.nativeRuntimeUnavailable',
        'Native runtime unavailable',
      ),
      title: appServerControlText(
        'appServerControl.issue.nativeRuntimeUnavailable.title',
        'This session cannot start a live AppServerControl runtime yet',
      ),
      body: appServerControlText(
        'appServerControl.issue.nativeRuntimeUnavailable.body',
        'tlbx could not start the native AppServerControl runtime for this session. Retry after the session becomes native-runtime-capable.',
      ),
      actions,
    };
  }

  if (hasReadonlyHistory) {
    return {
      kind: 'readonly-history',
      tone: 'warning',
      meta: appServerControlText('appServerControl.issue.readonlyHistory', 'Read-only history'),
      title: appServerControlText(
        'appServerControl.issue.readonlyHistory.title',
        'Live AppServerControl attach is unavailable right now',
      ),
      body: appServerControlFormat(
        'appServerControl.issue.readonlyHistory.body',
        '{detail} AppServerControl is staying open on canonical history, so you can still inspect the last stable history while Terminal remains the live fallback.',
        { detail },
      ),
      actions,
    };
  }

  return {
    kind: 'startup-failed',
    tone: 'attention',
    meta: appServerControlText(
      'appServerControl.issue.attachFailed',
      'AppServerControl attach failed',
    ),
    title: appServerControlText(
      'appServerControl.issue.startupFailed.title',
      'AppServerControl could not open',
    ),
    body: detail,
    actions,
  };
}

export function shouldShowAppServerControlDevErrorDialog(
  issue: AppServerControlActivationIssue | null,
): boolean {
  return issue?.kind === 'startup-failed';
}

export function ensureAppServerControlActivationIsCurrent(
  state: SessionAppServerControlViewState,
  activationRunId: number,
): void {
  if (state.debugScenarioActive || state.activationRunId !== activationRunId) {
    throw new Error(STALE_APP_SERVER_CONTROL_ACTIVATION);
  }
}

export function isStaleAppServerControlActivationError(error: unknown): boolean {
  return error instanceof Error && error.message === STALE_APP_SERVER_CONTROL_ACTIVATION;
}

export function toneFromState(state: string | null | undefined): HistoryTone {
  const normalized = (state || '').toLowerCase();
  if (
    normalized.includes('error') ||
    normalized.includes('failed') ||
    normalized.includes('declined')
  ) {
    return 'attention';
  }
  if (
    normalized.includes('running') ||
    normalized.includes('active') ||
    normalized.includes('open') ||
    normalized.includes('in_progress')
  ) {
    return 'warning';
  }
  if (
    normalized.includes('ready') ||
    normalized.includes('completed') ||
    normalized.includes('resolved') ||
    normalized.includes('idle')
  ) {
    return 'positive';
  }
  return 'info';
}

export function normalizeSnapshotHistoryKind(kind: string | null | undefined): HistoryKind {
  const normalized = (kind || '').toLowerCase();
  switch (normalized) {
    case 'user':
    case 'assistant':
    case 'reasoning':
    case 'tool':
    case 'request':
    case 'plan':
    case 'diff':
    case 'system':
    case 'notice':
      return normalized as HistoryKind;
    default:
      return 'system';
  }
}

export function isImageAttachment(attachment: AppServerControlAttachmentReference): boolean {
  if (attachment.kind.toLowerCase() === 'image') {
    return true;
  }

  if ((attachment.mimeType || '').toLowerCase().startsWith('image/')) {
    return true;
  }

  return /\.(png|jpe?g|gif|bmp|webp|svg|tiff?|heic|heif|avif)$/i.test(attachment.path);
}

export function buildAppServerControlAttachmentUrl(
  sessionId: string,
  attachment: AppServerControlAttachmentReference,
): string {
  return (
    `/api/files/view?path=${encodeURIComponent(attachment.path)}` +
    `&sessionId=${encodeURIComponent(sessionId)}`
  );
}

export function resolveAttachmentLabel(attachment: AppServerControlAttachmentReference): string {
  if (attachment.displayName?.trim()) {
    return attachment.displayName.trim();
  }

  const normalizedPath = attachment.path.replace(/\\/g, '/');
  const slashIndex = normalizedPath.lastIndexOf('/');
  return slashIndex >= 0 ? normalizedPath.slice(slashIndex + 1) : normalizedPath;
}

export function normalizeAppServerControlProvider(provider: string | null | undefined): string {
  return (provider || '').trim().toLowerCase();
}

export function resolveAppServerControlLayoutMode(
  provider: string | null | undefined,
): AppServerControlLayoutMode {
  return normalizeAppServerControlProvider(provider) === 'codex' ? 'full-width-left' : 'default';
}

export function historyLabel(kind: HistoryKind): string {
  switch (kind) {
    case 'user':
      return appServerControlText('appServerControl.label.user', 'You');
    case 'assistant':
      return appServerControlText('appServerControl.label.assistant', 'Assistant');
    case 'reasoning':
      return appServerControlText('appServerControl.label.reasoning', 'Reasoning');
    case 'tool':
      return appServerControlText('appServerControl.label.tool', 'Tool');
    case 'request':
      return appServerControlText('appServerControl.label.request', 'Request');
    case 'plan':
      return appServerControlText('appServerControl.label.plan', 'Plan');
    case 'diff':
      return appServerControlText('appServerControl.label.diff', 'Diff');
    case 'system':
      return appServerControlText('appServerControl.label.system', 'System');
    case 'notice':
      return appServerControlText('appServerControl.label.error', 'Error');
  }
}

export function resolveHistoryBadgeLabel(
  kind: HistoryKind,
  provider: string | null | undefined,
): string {
  if (resolveAppServerControlLayoutMode(provider) === 'full-width-left') {
    if (kind === 'user') {
      return appServerControlText('appServerControl.label.userShort', 'User');
    }

    if (kind === 'assistant') {
      return appServerControlText('appServerControl.label.agent', 'Agent');
    }
  }

  return historyLabel(kind);
}

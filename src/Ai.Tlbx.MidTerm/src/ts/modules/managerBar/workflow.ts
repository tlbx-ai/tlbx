export type ManagerActionType = 'single' | 'chain';
export type ManagerTriggerKind =
  'fireAndForget' | 'onCooldown' | 'repeatCount' | 'repeatInterval' | 'schedule';
export type ManagerRepeatUnit = 'seconds' | 'minutes' | 'hours' | 'days';
export type ManagerScheduleRepeat = 'daily' | 'weekdays' | 'weekends';

export interface ManagerBarScheduleEntry {
  timeOfDay: string;
  repeat: ManagerScheduleRepeat;
}

export interface ManagerBarTrigger {
  kind: ManagerTriggerKind;
  repeatCount: number;
  repeatEveryValue: number;
  repeatEveryUnit: ManagerRepeatUnit;
  schedule: ManagerBarScheduleEntry[];
}

export interface ManagerButton {
  id: string;
  label: string;
  text?: string;
  actionType?: string;
  prompts?: string[];
  trigger?: Partial<ManagerBarTrigger>;
}

export interface NormalizedManagerButton {
  id: string;
  label: string;
  text: string;
  actionType: ManagerActionType;
  prompts: string[];
  trigger: ManagerBarTrigger;
}

const VALID_ACTION_TYPES = new Set<ManagerActionType>(['single', 'chain']);
const VALID_TRIGGER_KINDS = new Set<ManagerTriggerKind>([
  'fireAndForget',
  'onCooldown',
  'repeatCount',
  'repeatInterval',
  'schedule',
]);
const VALID_INTERVAL_UNITS = new Set<ManagerRepeatUnit>(['seconds', 'minutes', 'hours', 'days']);
const VALID_SCHEDULE_REPEATS = new Set<ManagerScheduleRepeat>(['daily', 'weekdays', 'weekends']);

export const MANAGER_BAR_COOLDOWN_HEAT_THRESHOLD = 0.25;
export const MANAGER_BAR_POST_TRIGGER_IGNORE_HEAT_MS = 5000;

export interface ManagerBarCooldownEvaluation {
  ready: boolean;
  awaitingHeatRise: boolean;
}

export function createDefaultManagerButton(): NormalizedManagerButton {
  return {
    id: '',
    label: '',
    text: '',
    actionType: 'single',
    prompts: [''],
    trigger: {
      kind: 'fireAndForget',
      repeatCount: 2,
      repeatEveryValue: 5,
      repeatEveryUnit: 'minutes',
      schedule: [{ timeOfDay: '09:00', repeat: 'daily' }],
    },
  };
}

export function normalizeManagerBarButtons(
  buttons: ManagerButton[] | undefined | null,
): NormalizedManagerButton[] {
  return (buttons ?? []).map((button) => normalizeManagerBarButton(button));
}

export function normalizeManagerBarButton(
  button: Partial<ManagerButton> | null | undefined,
): NormalizedManagerButton {
  const defaults = createDefaultManagerButton();
  const actionType = normalizeActionType(button?.actionType);
  const prompts = buildNormalizedPrompts(button, actionType);
  const normalizedPrompts = actionType === 'single' ? [prompts[0] ?? ''] : prompts;
  const label = (button?.label ?? '').trim() || buildFallbackLabel(normalizedPrompts[0] ?? '');

  return {
    id: (button?.id ?? '').trim(),
    label,
    text: normalizedPrompts[0] ?? '',
    actionType,
    prompts: normalizedPrompts,
    trigger: normalizeTrigger(button?.trigger, defaults.trigger),
  };
}

function normalizeActionType(actionType: string | undefined): ManagerActionType {
  return VALID_ACTION_TYPES.has(actionType as ManagerActionType)
    ? (actionType as ManagerActionType)
    : 'single';
}

function buildNormalizedPrompts(
  button: Partial<ManagerButton> | null | undefined,
  actionType: ManagerActionType,
): string[] {
  const prompts = (button?.prompts ?? [])
    .map((prompt) => normalizePrompt(prompt))
    .filter((prompt) => prompt.length > 0);
  const legacyText = normalizePrompt(button?.text);
  const labelPrompt =
    typeof button?.label === 'string' && button.label.trim().length > 0 ? button.label.trim() : '';

  if (prompts.length === 0 && legacyText.length > 0) {
    prompts.push(legacyText);
  }
  if (prompts.length === 0 && labelPrompt.length > 0) {
    prompts.push(labelPrompt);
  }
  if (prompts.length === 0) {
    prompts.push('');
  }

  return actionType === 'single' ? [prompts[0] ?? ''] : prompts;
}

export function isImmediateManagerAction(button: NormalizedManagerButton): boolean {
  return button.actionType === 'single' && button.trigger.kind === 'fireAndForget';
}

export function shouldManagerActionWaitForInitialCooldown(
  button: NormalizedManagerButton,
): boolean {
  return (
    button.trigger.kind === 'onCooldown' ||
    button.trigger.kind === 'repeatCount' ||
    button.trigger.kind === 'repeatInterval'
  );
}

export function getManagerBarHeatResumeAt(triggeredAtMs: number): number {
  return triggeredAtMs + MANAGER_BAR_POST_TRIGGER_IGNORE_HEAT_MS;
}

export function isManagerBarCooldownReady(
  currentHeat: number,
  nowMs: number,
  ignoreHeatUntilMs: number | null,
): boolean {
  if (ignoreHeatUntilMs !== null && nowMs < ignoreHeatUntilMs) {
    return false;
  }

  return currentHeat <= MANAGER_BAR_COOLDOWN_HEAT_THRESHOLD;
}

export function evaluateManagerBarCooldown(
  currentHeat: number,
  nowMs: number,
  ignoreHeatUntilMs: number | null,
  awaitingHeatRise: boolean,
): ManagerBarCooldownEvaluation {
  let nextAwaitingHeatRise = awaitingHeatRise;
  if (nextAwaitingHeatRise && currentHeat > MANAGER_BAR_COOLDOWN_HEAT_THRESHOLD) {
    nextAwaitingHeatRise = false;
  }

  if (!isManagerBarCooldownReady(currentHeat, nowMs, ignoreHeatUntilMs)) {
    return {
      ready: false,
      awaitingHeatRise: nextAwaitingHeatRise,
    };
  }

  if (nextAwaitingHeatRise) {
    return {
      ready: false,
      awaitingHeatRise: true,
    };
  }

  return {
    ready: true,
    awaitingHeatRise: false,
  };
}

export function intervalToMs(trigger: ManagerBarTrigger): number {
  const unit = trigger.repeatEveryUnit;
  const value = Math.max(1, Math.trunc(trigger.repeatEveryValue));
  if (unit === 'seconds') return value * 1000;
  if (unit === 'hours') return value * 60 * 60 * 1000;
  if (unit === 'days') return value * 24 * 60 * 60 * 1000;
  return value * 60 * 1000;
}

export function computeNextScheduleTime(
  schedule: ManagerBarScheduleEntry[],
  fromMs: number,
): number | null {
  const base = new Date(fromMs);
  let best: number | null = null;

  for (let dayOffset = 0; dayOffset < 8; dayOffset += 1) {
    const day = new Date(base);
    day.setHours(0, 0, 0, 0);
    day.setDate(day.getDate() + dayOffset);

    for (const entry of schedule) {
      if (!isScheduleRepeatActive(entry.repeat, day)) continue;
      const [hours = Number.NaN, minutes = Number.NaN] = entry.timeOfDay
        .split(':')
        .map((part) => Number.parseInt(part, 10));
      if (!Number.isFinite(hours) || !Number.isFinite(minutes)) continue;

      const candidate = new Date(day);
      candidate.setHours(hours, minutes, 0, 0);
      const time = candidate.getTime();
      if (time <= fromMs) continue;
      if (best === null || time < best) {
        best = time;
      }
    }
  }

  return best;
}

export function formatPromptPreview(prompt: string, maxLength: number = 72): string {
  const singleLine = prompt.replace(/\s+/g, ' ').trim();
  if (singleLine.length <= maxLength) return singleLine;
  return `${singleLine.slice(0, Math.max(0, maxLength - 1)).trimEnd()}...`;
}

function normalizeTrigger(
  trigger: Partial<ManagerBarTrigger> | undefined,
  defaults: ManagerBarTrigger,
): ManagerBarTrigger {
  const schedule = (trigger?.schedule ?? defaults.schedule)
    .map((entry) => normalizeScheduleEntry(entry))
    .filter((entry): entry is ManagerBarScheduleEntry => entry !== null);

  const kind = VALID_TRIGGER_KINDS.has(trigger?.kind as ManagerTriggerKind)
    ? (trigger?.kind as ManagerTriggerKind)
    : defaults.kind;

  return {
    kind,
    repeatCount: Math.max(1, Math.trunc(trigger?.repeatCount ?? defaults.repeatCount)),
    repeatEveryValue: Math.max(
      1,
      Math.trunc(trigger?.repeatEveryValue ?? defaults.repeatEveryValue),
    ),
    repeatEveryUnit: VALID_INTERVAL_UNITS.has(trigger?.repeatEveryUnit as ManagerRepeatUnit)
      ? (trigger?.repeatEveryUnit as ManagerRepeatUnit)
      : defaults.repeatEveryUnit,
    schedule: kind === 'schedule' && schedule.length === 0 ? defaults.schedule : schedule,
  };
}

function normalizeScheduleEntry(
  entry: Partial<ManagerBarScheduleEntry> | undefined,
): ManagerBarScheduleEntry | null {
  const timeOfDay = (entry?.timeOfDay ?? '').trim();
  if (!/^(?:[01]\d|2[0-3]):[0-5]\d$/.test(timeOfDay)) {
    return null;
  }

  return {
    timeOfDay,
    repeat: VALID_SCHEDULE_REPEATS.has(entry?.repeat as ManagerScheduleRepeat)
      ? (entry?.repeat as ManagerScheduleRepeat)
      : 'daily',
  };
}

function normalizePrompt(prompt: string | undefined): string {
  return (prompt ?? '').replace(/\r\n/g, '\n').trim();
}

function buildFallbackLabel(prompt: string): string {
  const firstLine = prompt
    .split('\n')
    .map((line) => line.trim())
    .find((line) => line.length > 0);
  return firstLine ?? 'Action';
}

function isScheduleRepeatActive(repeat: ManagerScheduleRepeat, day: Date): boolean {
  const dayOfWeek = day.getDay();
  if (repeat === 'weekdays') {
    return dayOfWeek >= 1 && dayOfWeek <= 5;
  }
  if (repeat === 'weekends') {
    return dayOfWeek === 0 || dayOfWeek === 6;
  }
  return true;
}

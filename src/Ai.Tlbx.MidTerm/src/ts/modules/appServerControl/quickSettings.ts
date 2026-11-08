import type {
  AppServerControlAttachmentReference,
  AppServerControlQuickSettingsOption,
  AppServerControlQuickSettingsSummary,
  MidTermSettingsPublic,
  MidTermSettingsUpdate,
  AppServerControlTurnRequest,
  AppServerControlConfigOption,
  AppServerControlConfigValue,
  AppServerControlConfigureResponse,
  AppServerControlThreadSummary,
  AppServerControlTurnSummary,
} from '../../api/types';
import { configureAppServerControlRuntime, updateSettings } from '../../api/client';
import * as stores from '../../stores';
import { resolveAppServerControlModelEffortCatalog } from './modelOptions';

export const APP_SERVER_CONTROL_QUICK_SETTINGS_CHANGED_EVENT =
  'midterm:appServerControl-quick-settings-changed';

export interface AppServerControlQuickSettingsChangedEventDetail {
  sessionId: string;
  provider: string | null;
  effective: AppServerControlQuickSettingsSummary;
  draft: AppServerControlQuickSettingsSummary;
  draftDirty: boolean;
  source: 'seed' | 'sync' | 'draft' | 'remove';
}

interface AppServerControlQuickSettingsSessionState {
  provider: string | null;
  effective: AppServerControlQuickSettingsSummary;
  draft: AppServerControlQuickSettingsSummary;
  draftDirty: boolean;
  runtime?: RuntimeSettingsState;
}

const runtimeFields = ['model', 'effort', 'planMode', 'permissionMode', 'fastMode'] as const;
export type AppServerControlRuntimeSettingField = (typeof runtimeFields)[number];
export interface AppServerControlRuntimeSettingStatus {
  state: 'pending' | 'applied' | 'acknowledged' | 'partial' | 'unconfirmed' | 'targetUnavailable';
  scope: 'subsequentTurns' | 'subsequentCaptures' | 'subsequentResponses';
  detail?: string | undefined;
}
interface RuntimeSettingsState {
  target: { threadId: string; turnId: string | null; connected: boolean } | null;
  revision: number;
  revisions: Partial<Record<AppServerControlRuntimeSettingField, number>>;
  statuses: Partial<
    Record<AppServerControlRuntimeSettingField, AppServerControlRuntimeSettingStatus>
  >;
  confirmedDefaults: Partial<Record<AppServerControlRuntimeSettingField, string | null>>;
  confirmedValues: Partial<Record<AppServerControlRuntimeSettingField, string | null>>;
  observedDefaults: Set<AppServerControlRuntimeSettingField>;
  managed: boolean;
  queue: Promise<void>;
}

interface AppServerControlSessionProviderHint {
  profileHint?: string | null;
  foregroundName?: string | null;
}

interface CurrentSettingsStoreLike {
  get?: () => MidTermSettingsPublic | null;
  set?: (value: MidTermSettingsPublic | null) => void;
}

type AppServerControlPlanMode = AppServerControlQuickSettingsSummary['planMode'];
type AppServerControlPermissionMode = AppServerControlQuickSettingsSummary['permissionMode'];
type AppServerControlFastMode = 'off' | 'on';

const QUICK_SETTINGS_PROVIDER_STORAGE_PREFIX = 'midterm:appServerControl-quick-settings:provider:';
const DEFAULT_PLAN_MODE: AppServerControlPlanMode = 'off';
const DEFAULT_PERMISSION_MODE: AppServerControlPermissionMode = 'manual';
const DEFAULT_FAST_MODE: AppServerControlFastMode = 'off';
const sessionStates = new Map<string, AppServerControlQuickSettingsSessionState>();

function getOptionalStoreExport(key: string): unknown {
  if (!Reflect.has(stores, key)) {
    return undefined;
  }

  return Reflect.get(stores, key);
}

function normalizeOptionalValue(value: string | null | undefined): string | null {
  if (typeof value !== 'string') {
    return null;
  }

  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}

function normalizeProviderModel(
  _provider: string | null,
  value: string | null | undefined,
): string | null {
  return normalizeOptionalValue(value);
}

function normalizePlanMode(value: string | null | undefined): AppServerControlPlanMode {
  return value?.trim().toLowerCase() === 'on' ? 'on' : 'off';
}

function normalizePermissionMode(value: string | null | undefined): AppServerControlPermissionMode {
  return value?.trim().toLowerCase() === 'auto' ? 'auto' : 'manual';
}

function normalizeFastMode(value: string | null | undefined): AppServerControlFastMode {
  return value?.trim().toLowerCase() === 'on' ? 'on' : 'off';
}

function cloneQuickSettings(
  settings: AppServerControlQuickSettingsSummary,
): AppServerControlQuickSettingsSummary {
  return {
    model: settings.model ?? null,
    effort: settings.effort ?? null,
    planMode: normalizePlanMode(settings.planMode),
    permissionMode: normalizePermissionMode(settings.permissionMode),
    fastMode: normalizeFastMode(settings.fastMode),
    modelOptions: cloneQuickSettingsOptions(settings.modelOptions),
    effortOptions: cloneQuickSettingsOptions(settings.effortOptions),
    configOptions: structuredClone(settings.configOptions ?? []),
  };
}

function cloneQuickSettingsOptions(
  options: readonly AppServerControlQuickSettingsOption[] | null | undefined,
): AppServerControlQuickSettingsOption[] {
  if (!options) {
    return [];
  }

  const seen = new Set<string>();
  const cloned: AppServerControlQuickSettingsOption[] = [];
  for (const option of options) {
    const value = normalizeOptionalValue(option.value);
    if (!value || seen.has(value)) {
      continue;
    }

    seen.add(value);
    cloned.push({
      value,
      label: normalizeOptionalValue(option.label) ?? value,
      description: normalizeOptionalValue(option.description),
      hidden: option.hidden === true,
      isDefault: option.isDefault === true,
      ...(option.effortOptions !== undefined
        ? {
            effortOptions:
              option.effortOptions === null
                ? null
                : cloneQuickSettingsOptions(option.effortOptions),
          }
        : {}),
    });
  }

  return cloned;
}

function dispatchQuickSettingsChange(
  sessionId: string,
  state: AppServerControlQuickSettingsSessionState,
  source: AppServerControlQuickSettingsChangedEventDetail['source'],
): void {
  if (typeof window === 'undefined' || typeof window.dispatchEvent !== 'function') {
    return;
  }

  window.dispatchEvent(
    new CustomEvent<AppServerControlQuickSettingsChangedEventDetail>(
      APP_SERVER_CONTROL_QUICK_SETTINGS_CHANGED_EVENT,
      {
        detail: {
          sessionId,
          provider: state.provider,
          effective: cloneQuickSettings(state.effective),
          draft: cloneQuickSettings(state.draft),
          draftDirty: state.draftDirty,
          source,
        },
      },
    ),
  );
}

function readProviderStickyQuickSettings(
  provider: string | null,
): Partial<AppServerControlQuickSettingsSummary> {
  if (!provider || typeof localStorage === 'undefined') {
    return {};
  }

  try {
    const raw = localStorage.getItem(`${QUICK_SETTINGS_PROVIDER_STORAGE_PREFIX}${provider}`);
    if (!raw) {
      return {
        model: resolveRememberedProviderModel(provider, null),
      };
    }

    const parsed = JSON.parse(raw) as unknown;
    const legacySticky =
      typeof parsed === 'object' && parsed !== null
        ? (parsed as Partial<AppServerControlQuickSettingsSummary>)
        : {};
    return {
      ...legacySticky,
      model: resolveRememberedProviderModel(provider, normalizeOptionalValue(legacySticky.model)),
    };
  } catch {
    return {
      model: resolveRememberedProviderModel(provider, null),
    };
  }
}

function writeProviderStickyQuickSettings(
  provider: string | null,
  settings: AppServerControlQuickSettingsSummary,
): void {
  if (!provider || typeof localStorage === 'undefined') {
    return;
  }

  try {
    localStorage.setItem(
      `${QUICK_SETTINGS_PROVIDER_STORAGE_PREFIX}${provider}`,
      JSON.stringify({
        model: settings.model ?? null,
        effort: settings.effort ?? null,
        planMode: normalizePlanMode(settings.planMode),
        permissionMode: normalizePermissionMode(settings.permissionMode),
        fastMode: normalizeFastMode(settings.fastMode),
      }),
    );
  } catch {
    // Ignore quota/storage failures and keep the in-memory session draft.
  }
}

function resolveRememberedProviderModel(
  provider: string | null,
  legacyModel: string | null,
): string | null {
  const currentSettingsStore = getOptionalStoreExport('$currentSettings') as
    CurrentSettingsStoreLike | undefined;
  const currentSettings =
    currentSettingsStore && typeof currentSettingsStore.get === 'function'
      ? currentSettingsStore.get()
      : null;

  if (provider === 'codex') {
    return normalizeOptionalValue(currentSettings?.codexDefaultAppServerControlModel);
  }

  return legacyModel;
}

export function getAppServerControlResolvedProviderModel(provider: string | null): string | null {
  return resolveRememberedProviderModel(provider, null);
}

function persistRememberedProviderModel(provider: string | null, model: string | null): void {
  if (provider !== 'codex') {
    return;
  }

  const currentSettingsStore = getOptionalStoreExport('$currentSettings') as
    CurrentSettingsStoreLike | undefined;
  if (
    !currentSettingsStore ||
    typeof currentSettingsStore.get !== 'function' ||
    typeof currentSettingsStore.set !== 'function'
  ) {
    return;
  }
  const getCurrentSettings = currentSettingsStore.get;
  const setCurrentSettings = currentSettingsStore.set;

  const currentSettings = getCurrentSettings();
  if (!currentSettings) {
    return;
  }

  const nextStoredValue = normalizeOptionalValue(model) ?? '';
  const currentStoredValue = currentSettings.codexDefaultAppServerControlModel;
  if (currentStoredValue === nextStoredValue) {
    return;
  }

  const nextSettings: MidTermSettingsPublic = {
    ...currentSettings,
    codexDefaultAppServerControlModel: nextStoredValue,
  };

  setCurrentSettings(nextSettings);
  void updateSettings(nextSettings as MidTermSettingsUpdate)
    .then(({ response }) => {
      if (response.ok) {
        return;
      }

      const latestSettings = getCurrentSettings();
      const latestStoredValue = latestSettings?.codexDefaultAppServerControlModel ?? '';
      if (latestStoredValue === nextStoredValue) {
        setCurrentSettings(currentSettings);
      }
    })
    .catch(() => {
      const latestSettings = getCurrentSettings();
      const latestStoredValue = latestSettings?.codexDefaultAppServerControlModel ?? '';
      if (latestStoredValue === nextStoredValue) {
        setCurrentSettings(currentSettings);
      }
    });
}

function resolveSessionProvider(sessionId: string | null | undefined): string | null {
  if (!sessionId) {
    return null;
  }

  const getSession = getOptionalStoreExport('getSession') as
    ((sessionId: string) => AppServerControlSessionProviderHint | null) | undefined;
  const session = getSession?.(sessionId);
  const hinted =
    typeof session?.profileHint === 'string' && session.profileHint.trim().length > 0
      ? session.profileHint
      : session?.foregroundName;
  if (!hinted) {
    return null;
  }

  const normalized = hinted.trim().toLowerCase();
  return /^[a-z0-9][a-z0-9._-]*$/.test(normalized) ? normalized : null;
}

function resolveDefaultPermissionMode(provider: string | null): AppServerControlPermissionMode {
  const currentSettingsStore = getOptionalStoreExport('$currentSettings') as
    { get?: () => MidTermSettingsPublic | null } | undefined;
  const settings =
    currentSettingsStore && typeof currentSettingsStore.get === 'function'
      ? currentSettingsStore.get()
      : null;
  if (!settings) {
    return DEFAULT_PERMISSION_MODE;
  }

  if (provider === 'codex') {
    return settings.codexYoloDefault ? 'auto' : 'manual';
  }

  return DEFAULT_PERMISSION_MODE;
}

function normalizeQuickSettingsEffort(
  settings: Partial<AppServerControlQuickSettingsSummary> | null | undefined,
  provider: string | null,
  model: string | null,
): string | null {
  const efforts = resolveAppServerControlModelEffortCatalog(model, settings?.modelOptions);
  const requestedEffort =
    normalizeOptionalValue(settings?.effort) ?? (provider === 'codex' ? 'medium' : null);
  return requestedEffort && efforts && !efforts.some((option) => option.value === requestedEffort)
    ? null
    : requestedEffort;
}

function normalizeQuickSettings(
  settings: Partial<AppServerControlQuickSettingsSummary> | null | undefined,
  provider: string | null,
): AppServerControlQuickSettingsSummary {
  const model = normalizeProviderModel(provider, settings?.model);
  return {
    model,
    effort: normalizeQuickSettingsEffort(settings, provider, model),
    planMode: normalizePlanMode(settings?.planMode ?? DEFAULT_PLAN_MODE),
    permissionMode: normalizePermissionMode(
      settings?.permissionMode ?? resolveDefaultPermissionMode(provider),
    ),
    fastMode: provider === 'codex' ? normalizeFastMode(settings?.fastMode) : DEFAULT_FAST_MODE,
    modelOptions: cloneQuickSettingsOptions(settings?.modelOptions),
    effortOptions: cloneQuickSettingsOptions(settings?.effortOptions),
    configOptions: structuredClone(settings?.configOptions ?? []),
  };
}

function getOrCreateSessionState(sessionId: string): AppServerControlQuickSettingsSessionState {
  const existing = sessionStates.get(sessionId);
  if (existing) {
    return existing;
  }

  const provider = resolveSessionProvider(sessionId);
  const seeded = normalizeQuickSettings(readProviderStickyQuickSettings(provider), provider);
  const created: AppServerControlQuickSettingsSessionState = {
    provider,
    effective: cloneQuickSettings(seeded),
    draft: cloneQuickSettings(seeded),
    draftDirty: false,
  };
  sessionStates.set(sessionId, created);
  return created;
}

function ensureProviderSeeded(
  state: AppServerControlQuickSettingsSessionState,
  provider: string | null,
): AppServerControlQuickSettingsSessionState {
  if (state.provider === provider) {
    return state;
  }

  const providerSeed = normalizeQuickSettings(readProviderStickyQuickSettings(provider), provider);
  const nextEffective = normalizeQuickSettings(state.effective, provider);
  const nextDraft = state.draftDirty
    ? normalizeQuickSettings(state.draft, provider)
    : cloneQuickSettings(
        providerSeed.model !== null ||
          providerSeed.effort !== null ||
          providerSeed.planMode !== DEFAULT_PLAN_MODE ||
          providerSeed.permissionMode !== resolveDefaultPermissionMode(provider) ||
          providerSeed.fastMode !== DEFAULT_FAST_MODE
          ? providerSeed
          : nextEffective,
      );
  delete state.runtime;
  state.provider = provider;
  state.effective = nextEffective;
  state.draft = nextDraft;
  return state;
}

export function getAppServerControlQuickSettingsDraft(
  sessionId: string,
): AppServerControlQuickSettingsSummary {
  const state = ensureProviderSeeded(
    getOrCreateSessionState(sessionId),
    resolveSessionProvider(sessionId),
  );
  return cloneQuickSettings(state.draft);
}

export function getAppServerControlQuickSettingsEffective(
  sessionId: string,
): AppServerControlQuickSettingsSummary {
  const state = ensureProviderSeeded(
    getOrCreateSessionState(sessionId),
    resolveSessionProvider(sessionId),
  );
  return cloneQuickSettings(state.effective);
}

export function getAppServerControlQuickSettingsProvider(sessionId: string): string | null {
  const state = ensureProviderSeeded(
    getOrCreateSessionState(sessionId),
    resolveSessionProvider(sessionId),
  );
  return state.provider;
}

export function setAppServerControlQuickSettingsDraft(
  sessionId: string,
  patch: Partial<AppServerControlQuickSettingsSummary>,
): void {
  const state = ensureProviderSeeded(
    getOrCreateSessionState(sessionId),
    resolveSessionProvider(sessionId),
  );
  if (state.runtime) {
    const target = state.runtime.target;
    delete state.runtime;
    getRuntimeSettingsState(state).target = target;
  }
  state.draft = normalizeQuickSettings({ ...state.draft, ...patch }, state.provider);
  state.draftDirty = true;
  writeProviderStickyQuickSettings(state.provider, state.draft);
  if (Object.prototype.hasOwnProperty.call(patch, 'model')) {
    persistRememberedProviderModel(state.provider, state.draft.model ?? null);
  }
  dispatchQuickSettingsChange(sessionId, state, 'draft');
}

export function syncAppServerControlQuickSettingsFromSnapshot(
  sessionId: string,
  provider: string | null | undefined,
  quickSettings: Partial<AppServerControlQuickSettingsSummary> | null | undefined,
  target?: {
    thread: AppServerControlThreadSummary;
    currentTurn: AppServerControlTurnSummary;
    connected?: boolean;
  },
): void {
  const state = ensureProviderSeeded(
    getOrCreateSessionState(sessionId),
    provider ?? resolveSessionProvider(sessionId),
  );
  syncRuntimeSettingsTarget(state, target);
  const pendingConfigValues = getNativeConfigChanges(state.draft, state.effective);
  state.effective = normalizeQuickSettings(quickSettings, state.provider);
  if (!state.draftDirty) {
    state.draft = cloneQuickSettings(state.effective);
  } else {
    state.draft.configOptions = (state.effective.configOptions ?? []).map((option) => {
      const id = option.configId ?? option.id ?? '';
      const value = pendingConfigValues[id];
      return value !== undefined && isNativeConfigValueAvailable(option, value)
        ? { ...structuredClone(option), currentValue: value }
        : structuredClone(option);
    });
    state.draft.modelOptions = cloneQuickSettingsOptions(state.effective.modelOptions);
    state.draft.effortOptions = cloneQuickSettingsOptions(state.effective.effortOptions);
    normalizeSessionQuickSettingsDraft(state);
  }
  reconcileCanonicalRuntimeDefaults(state, quickSettings);
  syncConfirmedRuntimeDraft(state);
  writeProviderStickyQuickSettings(state.provider, getConfirmedQuickSettings(state));
  dispatchQuickSettingsChange(sessionId, state, 'sync');
}

export function acceptAppServerControlQuickSettings(
  sessionId: string,
  provider: string | null | undefined,
  quickSettings: Partial<AppServerControlQuickSettingsSummary> | null | undefined,
): void {
  const state = ensureProviderSeeded(
    getOrCreateSessionState(sessionId),
    provider ?? resolveSessionProvider(sessionId),
  );
  state.effective = normalizeQuickSettings(quickSettings, state.provider);
  const pendingDraft = cloneQuickSettings(state.draft);
  state.draft = getConfirmedQuickSettings(state);
  state.draftDirty = false;
  for (const field of runtimeFields) {
    if (state.runtime?.statuses[field]?.state !== 'pending') continue;
    assignRuntimeField(state.draft, field, pendingDraft[field] ?? null);
    state.draftDirty = true;
  }
  writeProviderStickyQuickSettings(state.provider, getConfirmedQuickSettings(state));
  dispatchQuickSettingsChange(sessionId, state, 'sync');
}

export function removeAppServerControlQuickSettingsSessionState(sessionId: string): void {
  const state = sessionStates.get(sessionId);
  if (!state) {
    return;
  }

  sessionStates.delete(sessionId);
  dispatchQuickSettingsChange(sessionId, state, 'remove');
}

export function createAppServerControlTurnRequestWithQuickSettings(
  sessionId: string,
  text: string,
  attachments: AppServerControlAttachmentReference[] = [],
): AppServerControlTurnRequest {
  const quickSettings = getAppServerControlQuickSettingsDraft(sessionId);
  const effective = getAppServerControlQuickSettingsEffective(sessionId);
  const configValues = getNativeConfigChanges(quickSettings, effective);
  const runtimeManaged = sessionStates.get(sessionId)?.runtime?.managed === true;
  return {
    text,
    ...(!runtimeManaged
      ? {
          model: quickSettings.model ?? null,
          effort: quickSettings.effort ?? null,
          planMode: quickSettings.planMode,
          permissionMode: quickSettings.permissionMode,
          fastMode: normalizeFastMode(quickSettings.fastMode),
        }
      : {}),
    ...(Object.keys(configValues).length ? { configValues } : {}),
    attachments,
  };
}

type RuntimeSettingsSnapshotTarget = {
  thread: AppServerControlThreadSummary;
  currentTurn: AppServerControlTurnSummary;
  connected?: boolean;
};

function syncRuntimeSettingsTarget(
  state: AppServerControlQuickSettingsSessionState,
  target: RuntimeSettingsSnapshotTarget | undefined,
): void {
  if (target) {
    const runtime = getRuntimeSettingsState(state);
    if (runtime.target && runtime.target.threadId !== target.thread.threadId) {
      delete state.runtime;
      state.draftDirty = false;
    }
    const current = getRuntimeSettingsState(state);
    const active = ['running', 'in_progress', 'submitted'].includes(target.currentTurn.state);
    const turnId = active ? (target.currentTurn.turnId ?? null) : null;
    if (current.target && current.target.turnId !== turnId) {
      for (const field of runtimeFields) {
        if (!Object.prototype.hasOwnProperty.call(current.confirmedDefaults, field)) {
          current.confirmedValues = Object.fromEntries(
            Object.entries(current.confirmedValues).filter(([key]) => key !== field),
          );
          const status = current.statuses[field];
          if (status && status.state !== 'pending')
            current.statuses[field] = { state: 'unconfirmed', scope: 'subsequentTurns' };
        } else {
          const status = current.statuses[field];
          if (status?.scope === 'subsequentCaptures')
            current.statuses[field] = { ...status, scope: 'subsequentTurns' };
        }
      }
    }
    current.target = {
      threadId: target.thread.threadId,
      turnId,
      connected: target.connected !== false,
    };
  }
}

function normalizeSessionQuickSettingsDraft(
  state: AppServerControlQuickSettingsSessionState,
): void {
  const previous = state.draft;
  state.draft = normalizeQuickSettings(previous, state.provider);
  for (const field of runtimeFields) {
    if (state.runtime?.statuses[field]?.state === 'pending')
      assignRuntimeField(state.draft, field, previous[field] ?? null);
  }
}

function reconcileCanonicalRuntimeDefaults(
  state: AppServerControlQuickSettingsSessionState,
  snapshot: Partial<AppServerControlQuickSettingsSummary> | null | undefined,
): void {
  const runtime = state.runtime;
  if (state.provider !== 'codex' || !runtime?.managed || !snapshot) return;
  for (const field of runtimeFields) {
    const value = snapshot[field];
    if (
      value === undefined ||
      !Object.prototype.hasOwnProperty.call(runtime.confirmedDefaults, field)
    )
      continue;
    if (value === runtime.confirmedDefaults[field]) runtime.observedDefaults.add(field);
    if (!runtime.observedDefaults.has(field)) continue;
    // Once the receipt appears in the canonical stream, later actual changes may supersede it.
    runtime.confirmedDefaults[field] = value;
    runtime.confirmedValues[field] = value;
  }
}

function syncConfirmedRuntimeDraft(state: AppServerControlQuickSettingsSessionState): void {
  if (state.runtime?.managed) {
    for (const field of runtimeFields) {
      if (state.runtime.statuses[field]?.state === 'pending') continue;
      if (Object.prototype.hasOwnProperty.call(state.runtime.confirmedValues, field)) {
        assignRuntimeField(state.draft, field, state.runtime.confirmedValues[field] ?? null);
      }
    }
  }
}

function getRuntimeSettingsState(
  state: AppServerControlQuickSettingsSessionState,
): RuntimeSettingsState {
  return (state.runtime ??= {
    target: null,
    revision: 0,
    revisions: {},
    statuses: {},
    confirmedDefaults: {},
    confirmedValues: {},
    observedDefaults: new Set(),
    managed: false,
    queue: Promise.resolve(),
  });
}

function getConfirmedQuickSettings(
  state: AppServerControlQuickSettingsSessionState,
): AppServerControlQuickSettingsSummary {
  const confirmed = cloneQuickSettings(state.effective);
  if (state.runtime?.managed) {
    for (const field of runtimeFields) {
      if (Object.prototype.hasOwnProperty.call(state.runtime.confirmedDefaults, field))
        assignRuntimeField(confirmed, field, state.runtime.confirmedDefaults[field] ?? null);
    }
  }
  return confirmed;
}

function assignRuntimeField(
  settings: AppServerControlQuickSettingsSummary,
  field: AppServerControlRuntimeSettingField,
  value: string | null,
): void {
  if (field === 'model' || field === 'effort') settings[field] = value;
  else settings[field] = value ?? (field === 'permissionMode' ? 'manual' : 'off');
}

export function getAppServerControlRuntimeSettingStatus(
  sessionId: string,
  field: AppServerControlRuntimeSettingField,
): AppServerControlRuntimeSettingStatus | undefined {
  const status = sessionStates.get(sessionId)?.runtime?.statuses[field];
  return status ? { ...status } : undefined;
}

function readObjectProperty(value: unknown, key: string): unknown {
  return typeof value === 'object' && value !== null
    ? (value as Record<string, unknown>)[key]
    : undefined;
}

function readNullableSettingString(value: unknown): string | null | undefined {
  return typeof value === 'string' || value === null ? value : undefined;
}

function readActualRuntimeSetting(
  actual: Record<string, unknown>,
  field: AppServerControlRuntimeSettingField,
): string | null | undefined {
  switch (field) {
    case 'model':
    case 'effort':
      return readNullableSettingString(actual[field]);
    case 'planMode': {
      const mode = readObjectProperty(actual.collaborationMode, 'mode');
      return mode === 'plan' ? 'on' : mode === 'default' ? 'off' : undefined;
    }
    case 'fastMode': {
      const tier = readNullableSettingString(actual.serviceTier);
      return tier === undefined ? undefined : tier === 'fast' ? 'on' : 'off';
    }
    case 'permissionMode': {
      const sandbox = readObjectProperty(actual.sandboxPolicy, 'type');
      if (actual.approvalPolicy === 'never' && sandbox === 'dangerFullAccess') return 'auto';
      if (actual.approvalPolicy === 'on-request' && sandbox === 'workspaceWrite') return 'manual';
      return undefined;
    }
  }
}

interface RuntimeSettingAcceptance {
  value: string | null;
  actual: boolean;
  defaults: boolean;
}

function readRuntimeSetting(
  receipt: AppServerControlConfigureResponse,
  field: AppServerControlRuntimeSettingField,
): RuntimeSettingAcceptance | null {
  if (receipt.threadStatus === 'applied') {
    const value = readActualRuntimeSetting(receipt.effectiveSettings, field);
    if (value !== undefined) return { value, actual: true, defaults: true };
  }
  const liveOnly = receipt.turnStatus === 'applied' && receipt.threadStatus !== 'applied';
  if ((!liveOnly && receipt.unconfirmedFields.includes(field)) || receipt.fieldErrors[field])
    return null;
  const confirmed = readNullableSettingString(receipt.confirmedSettings[field]);
  if (confirmed !== undefined)
    return { value: confirmed, actual: false, defaults: receipt.threadStatus === 'acknowledged' };
  if (field !== 'fastMode') return null;
  const tier = readNullableSettingString(receipt.confirmedSettings.serviceTier);
  return tier === undefined
    ? null
    : { value: tier === 'fast' ? 'on' : 'off', actual: false, defaults: false };
}

function rejectRuntimeFields(
  state: AppServerControlQuickSettingsSessionState,
  runtime: RuntimeSettingsState,
  fields: readonly AppServerControlRuntimeSettingField[],
  revision: number,
  status: 'unconfirmed' | 'targetUnavailable',
): void {
  for (const field of fields) {
    if (runtime.revisions[field] !== revision) continue;
    assignRuntimeField(
      state.draft,
      field,
      Object.prototype.hasOwnProperty.call(runtime.confirmedValues, field)
        ? (runtime.confirmedValues[field] ?? null)
        : (state.effective[field] ?? null),
    );
    runtime.statuses[field] = { state: status, scope: 'subsequentTurns' };
  }
}

function acceptRelatedRuntimeFields(
  state: AppServerControlQuickSettingsSessionState,
  runtime: RuntimeSettingsState,
  receipt: AppServerControlConfigureResponse,
  fields: readonly AppServerControlRuntimeSettingField[],
): void {
  // Readback can normalize related fields; Claude plan/permission share one SDK control.
  for (const field of runtimeFields) {
    const accepted = readRuntimeSetting(receipt, field);
    if (!accepted || (!accepted.actual && !accepted.defaults)) continue;
    runtime.confirmedValues[field] = accepted.value;
    if (accepted.defaults) {
      runtime.confirmedDefaults[field] = accepted.value;
      if (accepted.actual) runtime.observedDefaults.delete(field);
    }
    if (accepted.actual) assignRuntimeField(state.effective, field, accepted.value);
    if (!fields.includes(field) && runtime.statuses[field]?.state !== 'pending') {
      assignRuntimeField(state.draft, field, accepted.value);
      runtime.statuses[field] = {
        state: accepted.actual ? 'applied' : 'acknowledged',
        scope:
          receipt.threadScope === 'subsequentResponses' ? 'subsequentResponses' : 'subsequentTurns',
      };
    }
  }
}

function getReceiptFieldStatus(
  receipt: AppServerControlConfigureResponse,
  field: AppServerControlRuntimeSettingField,
  accepted: RuntimeSettingAcceptance,
  activeTarget: boolean,
  turnChanged: boolean,
): AppServerControlRuntimeSettingStatus {
  const liveField = !['planMode', 'permissionMode'].includes(field);
  const activeScope = liveField || receipt.turnScope === 'subsequentResponses';
  const activeConfirmed = receipt.turnStatus === 'applied' || receipt.turnStatus === 'acknowledged';
  const partial =
    !accepted.defaults || (activeTarget && activeScope && (!activeConfirmed || turnChanged));
  const defaultsOnly = accepted.defaults && (!activeScope || !activeTarget || partial);
  const scope = defaultsOnly ? receipt.threadScope : receipt.turnScope;
  return {
    state: partial ? 'partial' : accepted.actual ? 'applied' : 'acknowledged',
    scope:
      scope === 'subsequentResponses'
        ? 'subsequentResponses'
        : defaultsOnly
          ? 'subsequentTurns'
          : 'subsequentCaptures',
  };
}

function acceptRuntimeFieldReceipt(
  state: AppServerControlQuickSettingsSessionState,
  runtime: RuntimeSettingsState,
  receipt: AppServerControlConfigureResponse,
  field: AppServerControlRuntimeSettingField,
  revision: number,
  turnId: string | null,
): void {
  const accepted = readRuntimeSetting(receipt, field);
  if (accepted) {
    runtime.confirmedValues[field] = accepted.value;
    if (accepted.defaults) {
      runtime.confirmedDefaults[field] = accepted.value;
      if (accepted.actual) runtime.observedDefaults.delete(field);
    }
    if (accepted.actual) assignRuntimeField(state.effective, field, accepted.value);
    if (accepted.defaults) {
      writeProviderStickyQuickSettings(state.provider, getConfirmedQuickSettings(state));
      if (field === 'model') persistRememberedProviderModel(state.provider, accepted.value);
    }
    if (runtime.revisions[field] !== revision) return;
    assignRuntimeField(state.draft, field, accepted.value);
    runtime.statuses[field] = getReceiptFieldStatus(
      receipt,
      field,
      accepted,
      turnId !== null,
      turnId !== null && runtime.target?.turnId !== turnId,
    );
  } else if (runtime.revisions[field] === revision) {
    rejectRuntimeFields(state, runtime, [field], revision, 'unconfirmed');
    runtime.statuses[field] = {
      state: 'unconfirmed',
      scope: 'subsequentTurns',
      detail: receipt.fieldErrors[field] ?? receipt.detail ?? undefined,
    };
  }
}

function isCurrentRuntimeState(
  sessionId: string,
  state: AppServerControlQuickSettingsSessionState,
  runtime: RuntimeSettingsState,
): boolean {
  return sessionStates.get(sessionId) === state && state.runtime === runtime;
}

function getRuntimeTargetProblem(
  runtime: RuntimeSettingsState,
  target: RuntimeSettingsState['target'],
  provider: string | null,
): 'targetUnavailable' | 'unconfirmed' | null {
  if (!target?.threadId || !target.connected || !['codex', 'claude'].includes(provider ?? ''))
    return 'unconfirmed';
  const current = runtime.target;
  if (!current) return 'targetUnavailable';
  return current.threadId !== target.threadId || current.turnId !== target.turnId
    ? 'targetUnavailable'
    : null;
}

export function configureAppServerControlQuickSettings(
  sessionId: string,
  patch: Partial<Pick<AppServerControlQuickSettingsSummary, AppServerControlRuntimeSettingField>>,
): Promise<void> {
  const state = ensureProviderSeeded(
    getOrCreateSessionState(sessionId),
    resolveSessionProvider(sessionId),
  );
  const runtime = getRuntimeSettingsState(state);
  const selected = normalizeQuickSettings({ ...state.draft, ...patch }, state.provider);
  if (state.draft.effort === null && !Object.prototype.hasOwnProperty.call(patch, 'effort'))
    selected.effort = null;
  const fields = runtimeFields.filter((field) =>
    Object.prototype.hasOwnProperty.call(patch, field),
  );
  if (
    Object.prototype.hasOwnProperty.call(patch, 'model') &&
    selected.effort !== state.draft.effort &&
    !fields.includes('effort')
  )
    fields.push('effort');
  const requestedTarget = runtime.target ? { ...runtime.target } : null;
  const revision = ++runtime.revision;
  const settings: Record<string, string | null> = {};
  runtime.managed = true;
  for (const field of fields) {
    const value =
      (field === 'model' || field === 'effort') &&
      Object.prototype.hasOwnProperty.call(patch, field)
        ? normalizeOptionalValue(patch[field])
        : (selected[field] ?? null);
    settings[field] = value;
    assignRuntimeField(state.draft, field, value);
    runtime.revisions[field] = revision;
    runtime.statuses[field] = { state: 'pending', scope: 'subsequentTurns' };
  }
  state.draftDirty = true;
  dispatchQuickSettingsChange(sessionId, state, 'draft');

  const apply = async (): Promise<void> => {
    if (!isCurrentRuntimeState(sessionId, state, runtime)) return;
    const currentFields = fields.filter((field) => runtime.revisions[field] === revision);
    if (!currentFields.length) return;
    const target = requestedTarget;
    const problem = getRuntimeTargetProblem(runtime, target, state.provider);
    if (problem || !target) {
      rejectRuntimeFields(state, runtime, currentFields, revision, problem ?? 'unconfirmed');
      state.draftDirty = Object.values(runtime.statuses).some(
        (status) => status.state === 'pending',
      );
      dispatchQuickSettingsChange(sessionId, state, 'sync');
      return;
    }
    try {
      const receipt = await configureAppServerControlRuntime(sessionId, {
        expectedThreadId: target.threadId,
        expectedTurnId: target.turnId,
        settings: Object.fromEntries(currentFields.map((field) => [field, settings[field]])),
      });
      if (!isCurrentRuntimeState(sessionId, state, runtime)) return;
      if (runtime.target?.threadId !== target.threadId || receipt.status === 'targetUnavailable') {
        rejectRuntimeFields(state, runtime, currentFields, revision, 'targetUnavailable');
      } else {
        acceptRelatedRuntimeFields(state, runtime, receipt, currentFields);
        for (const field of currentFields)
          acceptRuntimeFieldReceipt(state, runtime, receipt, field, revision, target.turnId);
      }
    } catch {
      if (!isCurrentRuntimeState(sessionId, state, runtime)) return;
      rejectRuntimeFields(state, runtime, currentFields, revision, 'unconfirmed'); // Outcome can be unknown. Never replay a configure mutation.
    }
    state.draftDirty =
      Object.values(runtime.statuses).some((status) => status.state === 'pending') ||
      Object.keys(getNativeConfigChanges(state.draft, state.effective)).length > 0;
    dispatchQuickSettingsChange(sessionId, state, 'sync');
  };
  const operation = runtime.queue.then(apply);
  runtime.queue = operation.catch(() => {
    // Provider outcomes are reported above; an unexpected local failure must not strand later explicit edits.
  });
  return operation;
}

function getNativeConfigChanges(
  draft: AppServerControlQuickSettingsSummary,
  effective: AppServerControlQuickSettingsSummary,
): Record<string, string | boolean> {
  const changes: Record<string, string | boolean> = {};
  for (const option of draft.configOptions ?? []) {
    const id = option.configId ?? option.id;
    if (!id) continue;
    const previous = effective.configOptions?.find((entry) => (entry.configId ?? entry.id) === id);
    if (previous && previous.currentValue !== option.currentValue)
      changes[id] = option.currentValue;
  }
  return changes;
}

function isNativeConfigValueAvailable(
  option: AppServerControlConfigOption,
  value: string | boolean,
): boolean {
  return option.type === 'boolean'
    ? typeof value === 'boolean'
    : option.type === 'select' &&
        typeof value === 'string' &&
        hasConfigValue(option.options ?? [], value);
}

function hasConfigValue(options: readonly AppServerControlConfigValue[], value: string): boolean {
  return options.some(
    (option) => option.value === value || hasConfigValue(option.options ?? [], value),
  );
}

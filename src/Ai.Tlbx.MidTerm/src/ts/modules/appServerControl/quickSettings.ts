import type {
  AppServerControlAttachmentReference,
  AppServerControlQuickSettingsOption,
  AppServerControlQuickSettingsSummary,
  MidTermSettingsPublic,
  MidTermSettingsUpdate,
  AppServerControlTurnRequest,
} from '../../api/types';
import { updateSettings } from '../../api/client';
import * as stores from '../../stores';

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

const QUICK_SETTINGS_PROVIDER_STORAGE_PREFIX = 'midterm:appServerControl-quick-settings:provider:';
const DEFAULT_PLAN_MODE: AppServerControlPlanMode = 'off';
const DEFAULT_PERMISSION_MODE: AppServerControlPermissionMode = 'manual';
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

function normalizePlanMode(value: string | null | undefined): AppServerControlPlanMode {
  return value?.trim().toLowerCase() === 'on' ? 'on' : 'off';
}

function normalizePermissionMode(value: string | null | undefined): AppServerControlPermissionMode {
  return value?.trim().toLowerCase() === 'auto' ? 'auto' : 'manual';
}

function cloneQuickSettings(
  settings: AppServerControlQuickSettingsSummary,
): AppServerControlQuickSettingsSummary {
  return {
    model: settings.model ?? null,
    effort: settings.effort ?? null,
    planMode: normalizePlanMode(settings.planMode),
    permissionMode: normalizePermissionMode(settings.permissionMode),
    modelOptions: cloneQuickSettingsOptions(settings.modelOptions),
    effortOptions: cloneQuickSettingsOptions(settings.effortOptions),
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
    | CurrentSettingsStoreLike
    | undefined;
  const currentSettings =
    currentSettingsStore && typeof currentSettingsStore.get === 'function'
      ? currentSettingsStore.get()
      : null;

  if (provider === 'codex') {
    return (
      normalizeOptionalValue(currentSettings?.codexDefaultAppServerControlModel) ??
      legacyModel ??
      'gpt-5.5'
    );
  }

  if (provider === 'claude') {
    return (
      normalizeOptionalValue(currentSettings?.claudeDefaultAppServerControlModel) ?? legacyModel
    );
  }

  return legacyModel;
}

export function getAppServerControlResolvedProviderModel(provider: string | null): string | null {
  return resolveRememberedProviderModel(provider, null);
}

function persistRememberedProviderModel(provider: string | null, model: string | null): void {
  if (provider !== 'codex' && provider !== 'claude') {
    return;
  }

  const currentSettingsStore = getOptionalStoreExport('$currentSettings') as
    | CurrentSettingsStoreLike
    | undefined;
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
  const currentStoredValue =
    provider === 'codex'
      ? currentSettings.codexDefaultAppServerControlModel
      : currentSettings.claudeDefaultAppServerControlModel;
  if (currentStoredValue === nextStoredValue) {
    return;
  }

  const nextSettings: MidTermSettingsPublic = {
    ...currentSettings,
    ...(provider === 'codex'
      ? { codexDefaultAppServerControlModel: nextStoredValue }
      : { claudeDefaultAppServerControlModel: nextStoredValue }),
  };

  setCurrentSettings(nextSettings);
  void updateSettings(nextSettings as MidTermSettingsUpdate)
    .then(({ response }) => {
      if (response.ok) {
        return;
      }

      const latestSettings = getCurrentSettings();
      const latestStoredValue =
        provider === 'codex'
          ? (latestSettings?.codexDefaultAppServerControlModel ?? '')
          : (latestSettings?.claudeDefaultAppServerControlModel ?? '');
      if (latestStoredValue === nextStoredValue) {
        setCurrentSettings(currentSettings);
      }
    })
    .catch(() => {
      const latestSettings = getCurrentSettings();
      const latestStoredValue =
        provider === 'codex'
          ? (latestSettings?.codexDefaultAppServerControlModel ?? '')
          : (latestSettings?.claudeDefaultAppServerControlModel ?? '');
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
    | ((sessionId: string) => AppServerControlSessionProviderHint | null)
    | undefined;
  const session = getSession?.(sessionId);
  const hinted =
    typeof session?.profileHint === 'string' && session.profileHint.trim().length > 0
      ? session.profileHint
      : session?.foregroundName;
  if (!hinted) {
    return null;
  }

  const normalized = hinted.trim().toLowerCase();
  return normalized === 'codex' || normalized === 'claude' ? normalized : null;
}

function resolveDefaultPermissionMode(provider: string | null): AppServerControlPermissionMode {
  const currentSettingsStore = getOptionalStoreExport('$currentSettings') as
    | { get?: () => MidTermSettingsPublic | null }
    | undefined;
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

  if (provider === 'claude') {
    return settings.claudeDangerouslySkipPermissionsDefault ? 'auto' : 'manual';
  }

  return DEFAULT_PERMISSION_MODE;
}

function normalizeQuickSettings(
  settings: Partial<AppServerControlQuickSettingsSummary> | null | undefined,
  provider: string | null,
): AppServerControlQuickSettingsSummary {
  return {
    model: normalizeOptionalValue(settings?.model),
    effort: normalizeOptionalValue(settings?.effort),
    planMode: normalizePlanMode(settings?.planMode ?? DEFAULT_PLAN_MODE),
    permissionMode: normalizePermissionMode(
      settings?.permissionMode ?? resolveDefaultPermissionMode(provider),
    ),
    modelOptions: cloneQuickSettingsOptions(settings?.modelOptions),
    effortOptions: cloneQuickSettingsOptions(settings?.effortOptions),
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
          providerSeed.permissionMode !== resolveDefaultPermissionMode(provider)
          ? providerSeed
          : nextEffective,
      );
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
): void {
  const state = ensureProviderSeeded(
    getOrCreateSessionState(sessionId),
    provider ?? resolveSessionProvider(sessionId),
  );
  state.effective = normalizeQuickSettings(quickSettings, state.provider);
  if (!state.draftDirty) {
    state.draft = cloneQuickSettings(state.effective);
  }
  writeProviderStickyQuickSettings(state.provider, state.effective);
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
  state.draft = cloneQuickSettings(state.effective);
  state.draftDirty = false;
  writeProviderStickyQuickSettings(state.provider, state.effective);
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
  return {
    text,
    model: quickSettings.model ?? null,
    effort: quickSettings.effort ?? null,
    planMode: quickSettings.planMode,
    permissionMode: quickSettings.permissionMode,
    attachments,
  };
}

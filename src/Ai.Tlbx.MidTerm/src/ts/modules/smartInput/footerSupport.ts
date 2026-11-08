import { $voiceAvailable } from '../../stores';
import type {
  AppServerControlQuickSettingsOption,
  AppServerControlQuickSettingsSummary,
} from '../../api/types';
import { t } from '../i18n';
import {
  getAppServerControlRuntimeSettingStatus,
  type AppServerControlRuntimeSettingStatus,
  type AppServerControlRuntimeSettingField,
  getAppServerControlQuickSettingsDraft,
  getAppServerControlQuickSettingsEffective,
  getAppServerControlQuickSettingsProvider,
  getAppServerControlResolvedProviderModel,
} from '../appServerControl/quickSettings';
import { hasInterruptibleAppServerControlTurnWork } from '../appServerControl/input';
import { syncNativeConfigControls } from './nativeConfigControls';
import {
  getAppServerControlEffortOptions,
  getAppServerControlModelOptions,
} from '../appServerControl/modelOptions';
import {
  ADAPTIVE_FOOTER_RESERVED_HEIGHT_CHANGED_EVENT,
  calculateAdaptiveFooterReservedHeight,
} from './layout';
import {
  shouldShowAppServerControlQuickSettings,
  type SmartInputVisibilityState,
} from './visibility';
import {
  formatAppServerControlQuickSettingsSummary,
  setAppServerControlQuickSettingsDropdownDisabled,
  setAppServerControlQuickSettingsDropdownOptions,
} from './smartInputView';
import { getCollapsedSmartInputTextareaHeight } from './smartInputMetrics';

export function canUseSmartInputVoice(): boolean {
  return $voiceAvailable.get();
}

export function getMicButtons(footerDock: HTMLDivElement | null): HTMLButtonElement[] {
  return footerDock
    ? Array.from(footerDock.querySelectorAll<HTMLButtonElement>('.smart-input-mic-btn'))
    : [];
}

export function updateAutoSendVisibility(args: {
  dockedBar: HTMLDivElement | null;
  sendBtn: HTMLButtonElement | null;
  autoSendEnabled: boolean;
}): void {
  const active = args.autoSendEnabled && canUseSmartInputVoice();
  args.dockedBar?.classList.toggle('autosend-active', active);
  args.sendBtn?.classList.toggle('autosend-latched', active);
  if (args.sendBtn) {
    args.sendBtn.setAttribute('data-autosend', active ? 'true' : 'false');
    args.sendBtn.title = active ? t('smartInput.autoSendOnHint') : t('smartInput.sendGestureHint');
  }
}

export function syncVoiceInputAvailability(args: {
  footerDock: HTMLDivElement | null;
  dockedBar: HTMLDivElement | null;
  sendBtn: HTMLButtonElement | null;
  autoSendEnabled: boolean;
  isRecording: boolean;
  endRecording: () => void;
}): void {
  const enabled = canUseSmartInputVoice();
  getMicButtons(args.footerDock).forEach((button) => {
    button.hidden = !enabled;
  });

  if (!enabled && args.isRecording) {
    args.endRecording();
  }

  updateAutoSendVisibility({
    dockedBar: args.dockedBar,
    sendBtn: args.sendBtn,
    autoSendEnabled: args.autoSendEnabled,
  });
}

export function shouldIgnoreFooterTransientUiDocumentClick(target: Node): boolean {
  return (
    target instanceof HTMLElement && Boolean(target.closest('.provider-resume-picker-overlay'))
  );
}

export function syncAppServerControlQuickSettingsControls(args: {
  appServerControlQuickSettingsRow: HTMLDivElement | null;
  appServerControlQuickSettingsActions: HTMLDivElement | null;
  appServerControlModelSelect: HTMLSelectElement | null;
  appServerControlEffortSelect: HTMLSelectElement | null;
  appServerControlFastToggle: HTMLButtonElement | null;
  appServerControlPlanSelect: HTMLSelectElement | null;
  appServerControlPermissionSelect: HTMLSelectElement | null;
  appServerControlSettingsSummaryBtn: HTMLButtonElement | null;
  dockedBar: HTMLDivElement | null;
  getVisibilityState: () => SmartInputVisibilityState;
  setAppServerControlQuickSettingsSheetOpen: (open: boolean) => void;
}): void {
  const {
    appServerControlQuickSettingsRow,
    appServerControlQuickSettingsActions,
    appServerControlModelSelect,
    appServerControlEffortSelect,
    appServerControlFastToggle,
    appServerControlPlanSelect,
    appServerControlPermissionSelect,
    appServerControlSettingsSummaryBtn,
    dockedBar,
  } = args;
  if (
    !appServerControlQuickSettingsRow ||
    !appServerControlQuickSettingsActions ||
    !appServerControlModelSelect ||
    !appServerControlEffortSelect ||
    !appServerControlPlanSelect ||
    !appServerControlPermissionSelect
  ) {
    return;
  }

  const visibilityState = args.getVisibilityState();
  if (!shouldShowAppServerControlQuickSettings(visibilityState)) {
    if (dockedBar) {
      dockedBar.dataset.appServerControlSession = 'false';
    }
    appServerControlQuickSettingsRow.hidden = true;
    appServerControlQuickSettingsActions.replaceChildren();
    appServerControlQuickSettingsActions.hidden = true;
    delete appServerControlQuickSettingsRow.dataset.provider;
    args.setAppServerControlQuickSettingsSheetOpen(false);
    return;
  }

  const sessionId = visibilityState.activeSessionId as string;
  const provider = getAppServerControlQuickSettingsProvider(sessionId);
  const draft = getAppServerControlQuickSettingsDraft(sessionId);
  const effective = getAppServerControlQuickSettingsEffective(sessionId);
  const resolvedProviderModel = getAppServerControlResolvedProviderModel(provider);
  const nativeConfigLocked = hasInterruptibleAppServerControlTurnWork(sessionId);
  if (dockedBar) {
    dockedBar.dataset.appServerControlSession = 'true';
  }
  appServerControlQuickSettingsRow.dataset.provider = provider ?? '';
  syncNativeConfigControls(
    appServerControlQuickSettingsRow,
    sessionId,
    draft.configOptions,
    nativeConfigLocked,
  );

  setAppServerControlQuickSettingsDropdownOptions(
    appServerControlModelSelect,
    getAppServerControlModelOptions({
      provider,
      currentValues: [draft.model, effective.model],
      defaultLabel: resolvedProviderModel,
      catalogOptions: preferQuickSettingsOptions(draft, effective, 'modelOptions'),
    }),
  );

  setAppServerControlQuickSettingsDropdownOptions(
    appServerControlEffortSelect,
    getAppServerControlEffortOptions({
      model: draft.model,
      modelOptions: preferQuickSettingsOptions(draft, effective, 'modelOptions'),
      currentValues: [draft.effort, effective.effort],
      catalogOptions: preferQuickSettingsOptions(draft, effective, 'effortOptions'),
    }),
  );

  syncAppServerControlQuickSettingSelect(appServerControlModelSelect, draft.model ?? '');
  syncAppServerControlQuickSettingSelect(appServerControlEffortSelect, draft.effort ?? '');
  syncAppServerControlQuickSettingSelect(appServerControlPlanSelect, draft.planMode);
  syncAppServerControlQuickSettingSelect(appServerControlPermissionSelect, draft.permissionMode);
  setAppServerControlQuickSettingsDropdownDisabled(appServerControlModelSelect, false);
  syncEffortControlAvailability(appServerControlEffortSelect, false);
  setAppServerControlQuickSettingsDropdownDisabled(appServerControlPlanSelect, false);
  setAppServerControlQuickSettingsDropdownDisabled(appServerControlPermissionSelect, false);
  syncAppServerControlFastModeToggle({
    toggle: appServerControlFastToggle,
    available: provider === 'codex',
    locked: false,
    enabled: draft.fastMode === 'on',
  });

  syncRuntimeSettingControlStatus(appServerControlModelSelect, sessionId, 'model');
  syncRuntimeSettingControlStatus(appServerControlEffortSelect, sessionId, 'effort');
  syncRuntimeSettingControlStatus(appServerControlPlanSelect, sessionId, 'planMode');
  syncRuntimeSettingControlStatus(appServerControlPermissionSelect, sessionId, 'permissionMode');
  syncRuntimeSettingControlStatus(appServerControlFastToggle, sessionId, 'fastMode');

  if (appServerControlSettingsSummaryBtn) {
    appServerControlSettingsSummaryBtn.textContent = formatAppServerControlQuickSettingsSummary({
      ...draft,
      model: draft.model ?? resolvedProviderModel,
    });
    appServerControlSettingsSummaryBtn.dataset.planMode = draft.planMode;
  }
}

export function formatRuntimeSettingStatus(
  status: AppServerControlRuntimeSettingStatus | undefined,
): string {
  if (!status) return '';
  const key =
    status.state === 'pending'
      ? 'settingPending'
      : status.state === 'targetUnavailable'
        ? 'settingTargetUnavailable'
        : status.state === 'unconfirmed'
          ? 'settingUnconfirmed'
          : status.state === 'partial'
            ? 'settingPartial'
            : status.state === 'acknowledged'
              ? 'settingAcknowledged'
              : 'settingApplied';
  if (
    status.state === 'pending' ||
    status.state === 'unconfirmed' ||
    status.state === 'targetUnavailable'
  )
    return [t(`smartInput.${key}`), status.detail].filter(Boolean).join(' — ');
  const scopeKey =
    status.scope === 'subsequentResponses'
      ? 'settingScopeResponses'
      : status.scope === 'subsequentCaptures'
        ? 'settingScopeCaptures'
        : 'settingScopeTurns';
  return [t(`smartInput.${key}`), t(`smartInput.${scopeKey}`), status.detail]
    .filter(Boolean)
    .join(' — ');
}

function syncRuntimeSettingControlStatus(
  control: HTMLElement | null,
  sessionId: string,
  field: AppServerControlRuntimeSettingField,
): void {
  const container = control?.closest<HTMLElement>('.smart-input-appServerControl-field');
  if (!container) return;
  const status = getAppServerControlRuntimeSettingStatus(sessionId, field);
  const description = [
    formatRuntimeSettingStatus(status),
    field === 'permissionMode' &&
    getAppServerControlQuickSettingsDraft(sessionId).planMode === 'on' &&
    getAppServerControlQuickSettingsProvider(sessionId) === 'claude'
      ? t('smartInput.settingPermissionAfterPlan')
      : '',
  ]
    .filter(Boolean)
    .join(' — ');
  container.title = description;
  container.dataset.configureState = status?.state ?? '';
  container.setAttribute('aria-busy', status?.state === 'pending' ? 'true' : 'false');
  const trigger =
    container.querySelector<HTMLElement>('.smart-input-appServerControl-dropdown-trigger') ??
    control;
  if (description) trigger?.setAttribute('aria-description', description);
  else trigger?.removeAttribute('aria-description');
}

function syncEffortControlAvailability(select: HTMLSelectElement, locked: boolean): void {
  setAppServerControlQuickSettingsDropdownDisabled(select, locked || select.options.length <= 1);
}

function syncAppServerControlFastModeToggle(args: {
  toggle: HTMLButtonElement | null;
  available: boolean;
  locked: boolean;
  enabled: boolean;
}): void {
  if (!args.toggle) {
    return;
  }
  const field = args.toggle.closest<HTMLElement>('[data-app-server-control-field="fast-mode"]');
  if (field) {
    field.hidden = !args.available;
  }
  args.toggle.disabled = args.locked || !args.available;
  args.toggle.setAttribute('aria-pressed', args.enabled ? 'true' : 'false');
  args.toggle.textContent = args.enabled
    ? t('smartInput.appServerControlFastModeOn')
    : t('smartInput.appServerControlFastModeOff');
}

function preferQuickSettingsOptions(
  draft: AppServerControlQuickSettingsSummary,
  effective: AppServerControlQuickSettingsSummary,
  key: 'modelOptions' | 'effortOptions',
): readonly AppServerControlQuickSettingsOption[] | undefined {
  const draftOptions = draft[key];
  return draftOptions && draftOptions.length > 0 ? draftOptions : effective[key];
}

function syncAppServerControlQuickSettingSelect(
  select: HTMLSelectElement,
  nextValue: string,
): void {
  if (select.value === nextValue) {
    return;
  }

  select.value = nextValue;
  select.dispatchEvent(new Event('midterm:sync'));
}

export function queueFooterReserveSync(args: {
  footerResizeQueued: boolean;
  setFooterResizeQueued: (queued: boolean) => void;
  updateFooterReservedHeight: () => void;
}): void {
  if (args.footerResizeQueued) {
    return;
  }

  args.setFooterResizeQueued(true);
  requestAnimationFrame(() => {
    args.setFooterResizeQueued(false);
    args.updateFooterReservedHeight();
  });
}

export function updateFooterReservedHeight(args: {
  footerDock: HTMLDivElement | null;
  activeTextarea: HTMLTextAreaElement | null;
  composerExpanded: boolean;
  lastReservedFooterHeightPx: number;
  setLastReservedFooterHeightPx: (value: number) => void;
}): void {
  const root = document.documentElement;
  if (!args.footerDock || args.footerDock.hidden || args.composerExpanded) {
    setAdaptiveFooterReservedHeight(
      root,
      0,
      args.lastReservedFooterHeightPx,
      args.setLastReservedFooterHeightPx,
    );
    return;
  }

  const textareaHeight = args.activeTextarea?.offsetHeight ?? null;
  const collapsedTextareaHeight = args.activeTextarea
    ? getCollapsedSmartInputTextareaHeight(args.activeTextarea)
    : null;
  const reserveHeight = calculateAdaptiveFooterReservedHeight({
    dockHeight: measureFooterDockHeight(args.footerDock),
    textareaHeight,
    collapsedTextareaHeight,
  });

  setAdaptiveFooterReservedHeight(
    root,
    reserveHeight,
    args.lastReservedFooterHeightPx,
    args.setLastReservedFooterHeightPx,
  );
}

function setAdaptiveFooterReservedHeight(
  root: HTMLElement,
  reserveHeight: number,
  lastReservedFooterHeightPx: number,
  setLastReservedFooterHeightPx: (value: number) => void,
): void {
  const normalizedReserveHeight = normalizeCssPixelNumber(reserveHeight);
  root.style.setProperty(
    '--adaptive-footer-reserved-height',
    formatCssPixelValue(normalizedReserveHeight),
  );

  if (Math.abs(lastReservedFooterHeightPx - normalizedReserveHeight) < 0.001) {
    return;
  }

  setLastReservedFooterHeightPx(normalizedReserveHeight);
  window.dispatchEvent(
    new CustomEvent(ADAPTIVE_FOOTER_RESERVED_HEIGHT_CHANGED_EVENT, {
      detail: { reservedHeightPx: normalizedReserveHeight },
    }),
  );
}

function measureFooterDockHeight(footerDock: HTMLElement): number {
  if (typeof footerDock.getBoundingClientRect === 'function') {
    const rect = footerDock.getBoundingClientRect();
    if (Number.isFinite(rect.height) && rect.height > 0) {
      return rect.height;
    }
  }

  return footerDock.offsetHeight;
}

function normalizeCssPixelNumber(value: number): number {
  const normalized = Math.max(0, value);
  const rounded = Math.round(normalized);
  if (Math.abs(normalized - rounded) < 0.001) {
    return rounded;
  }

  return Math.round(normalized * 1000) / 1000;
}

function formatCssPixelValue(value: number): string {
  return `${String(normalizeCssPixelNumber(value))}px`;
}

import { $voiceServerPassword } from '../../stores';
import type {
  AppServerControlQuickSettingsOption,
  AppServerControlQuickSettingsSummary,
} from '../../api/types';
import { t } from '../i18n';
import {
  getAppServerControlQuickSettingsDraft,
  getAppServerControlQuickSettingsEffective,
  getAppServerControlQuickSettingsProvider,
  getAppServerControlResolvedProviderModel,
} from '../appServerControl/quickSettings';
import { hasInterruptibleAppServerControlTurnWork } from '../appServerControl/input';
import {
  getAppServerControlEffortOptions,
  getAppServerControlModelOptions,
} from '../appServerControl/modelOptions';
import { isDevMode } from '../sidebar/voiceSection';
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
  return isDevMode() && Boolean($voiceServerPassword.get());
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
  const quickSettingsLocked = hasInterruptibleAppServerControlTurnWork(sessionId);
  if (dockedBar) {
    dockedBar.dataset.appServerControlSession = 'true';
  }
  appServerControlQuickSettingsRow.dataset.provider = provider ?? '';

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
      currentValues: [draft.effort, effective.effort],
      catalogOptions: preferQuickSettingsOptions(draft, effective, 'effortOptions'),
    }),
  );

  syncAppServerControlQuickSettingSelect(appServerControlModelSelect, draft.model ?? '');
  syncAppServerControlQuickSettingSelect(appServerControlEffortSelect, draft.effort ?? '');
  syncAppServerControlQuickSettingSelect(appServerControlPlanSelect, draft.planMode);
  syncAppServerControlQuickSettingSelect(appServerControlPermissionSelect, draft.permissionMode);
  setAppServerControlQuickSettingsDropdownDisabled(
    appServerControlModelSelect,
    quickSettingsLocked,
  );
  setAppServerControlQuickSettingsDropdownDisabled(
    appServerControlEffortSelect,
    quickSettingsLocked,
  );
  setAppServerControlQuickSettingsDropdownDisabled(appServerControlPlanSelect, quickSettingsLocked);
  setAppServerControlQuickSettingsDropdownDisabled(
    appServerControlPermissionSelect,
    quickSettingsLocked,
  );

  if (appServerControlSettingsSummaryBtn) {
    appServerControlSettingsSummaryBtn.textContent = formatAppServerControlQuickSettingsSummary({
      ...draft,
      model: draft.model ?? resolvedProviderModel,
    });
    appServerControlSettingsSummaryBtn.dataset.planMode = draft.planMode;
  }
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

/* eslint-disable max-lines -- smartInput.ts remains a legacy integration hub; this change isolates new composer logic without risking a broad same-turn file split. */
/**
 * Smart Input UI
 *
 * The dock can expose an input row, a mode-specific context row,
 * manager automation, and a status rail without splitting those concerns
 * into unrelated sibling bars.
 */

import {
  $currentSettings,
  $activeSessionId,
  $settingsOpen,
  $voiceServerPassword,
} from '../../stores';
import { setAppServerControlGoal } from '../../api/client';
import { t } from '../i18n';
import { enqueueCommandBayTurn } from '../commandBay/queue';
import { submitSessionText } from '../input/submit';
import { isBracketedPasteEnabled, sendInput } from '../comms';
import {
  createAppServerControlTurnRequest,
  handleAppServerControlEscape,
  hasInterruptibleAppServerControlTurnWork,
  isAppServerControlActiveSession,
} from '../appServerControl/input';
import {
  APP_SERVER_CONTROL_QUICK_SETTINGS_CHANGED_EVENT,
  getAppServerControlQuickSettingsDraft,
  getAppServerControlQuickSettingsProvider,
  setAppServerControlQuickSettingsDraft,
} from '../appServerControl/quickSettings';
import {
  setAutomationOverflowProxyAnchor,
  triggerAddAutomation,
  triggerAutomationOverflow,
} from '../managerBar';
import { shouldShowManagerBar } from '../managerBar/visibility';
import { getActiveTab, onTabActivated } from '../sessionTabs';
import { onDevModeChanged } from '../sidebar/voiceSection';
import { showDropToast, uploadFile } from '../terminal';
import { shouldShowTouchController } from '../touchController/detection';
import { closePopup as closeTouchControllerPopup } from '../touchController/popups';
import { registerBackButtonLayer } from '../navigation/backButtonGuard';
import { isEmbeddedWebPreviewContext } from '../web/webContext';
import { getAdaptiveFooterRailSequence } from './layout';
import {
  clipboardDataMayContainAppServerControlComposerImage,
  extractAppServerControlComposerPasteImageFiles,
  type AppServerControlComposerDraftAttachment,
  MAX_APP_SERVER_CONTROL_IMAGE_BYTES,
  cloneAppServerControlComposerDraftAttachments,
  createAppServerControlComposerDraftAttachment,
  isAppServerControlComposerImageFile,
  releaseAppServerControlComposerDraftAttachmentPreviews,
} from './appServerControlAttachments';
import { submitAppServerControlComposerDraft } from './appServerControlAttachmentSubmission';
import { prepareSmartInputTerminalTurn } from './smartInputOutboundReferences';
import { startHistoryion, stopHistoryion } from './transcription';
import { shouldShowDockedSmartInput, type SmartInputVisibilityState } from './visibility';
import { captureImageFromWebcam } from './cameraCapture';
import { openAppServerControlDraftAttachment } from './attachmentDraftOpen';
import { renderAppServerControlAttachmentDraftView } from './attachmentDraftView';
import {
  createTerminalTouchToggleButton,
  createSmartInputDom,
  createToolButton,
  createToolButtonsStrip,
  formatAppServerControlQuickSettingsSummary,
  openFileInputPicker as showSmartInputFilePicker,
  renderTerminalStatusRow,
  syncSmartInputComposerExpandToggleState,
  type ToolKind,
} from './smartInputView';
import {
  clearAppServerControlDraftAttachmentsForSession,
  cloneSmartInputPromptHistoryEntry,
  detachAppServerControlDraftAttachmentsForSession,
  getAppServerControlDraftAttachmentsForSession,
  getSmartInputPromptHistoryForSession,
  loadAppServerControlDraftAttachmentsForSession,
  loadSmartInputPromptHistoryForSession,
  pushSmartInputPromptHistoryEntryForSession,
  setAppServerControlDraftAttachmentsForSession,
  type SmartInputPromptHistoryEntry,
} from './smartInputDraftStore';
import {
  isMobileViewport,
  isTouchPrimaryDevice,
  resizeSmartInputTextarea,
} from './smartInputMetrics';
import type { ResumeProvider } from '../providerResume';
import { bindSmartInputGlobalKeyBindings } from './smartInputKeyBindings';
import {
  canUseSmartInputVoice as canUseSmartInputVoiceSupport,
  getMicButtons as getMicButtonsSupport,
  queueFooterReserveSync as queueFooterReserveSyncSupport,
  shouldIgnoreFooterTransientUiDocumentClick as shouldIgnoreFooterTransientUiDocumentClickSupport,
  syncAppServerControlQuickSettingsControls as syncAppServerControlQuickSettingsControlsSupport,
  syncVoiceInputAvailability as syncVoiceInputAvailabilitySupport,
  updateAutoSendVisibility as updateAutoSendVisibilitySupport,
  updateFooterReservedHeight as updateFooterReservedHeightSupport,
} from './footerSupport';
import { createAppServerControlResumeButton } from './appServerControlResumeButton';
import {
  insertSmartInputLineBreak,
  shouldInsertLineBreakOnEnter,
  shouldSubmitSmartInputOnEnter,
} from './enterBehavior';
import { resolveSmartInputShiftTabAction } from './smartInputTextareaShortcuts';
import {
  allocateSmartInputComposerReferenceOrdinal,
  cloneSmartInputComposerDraft,
  createSmartInputComposerDraft,
  deleteSmartInputComposerBackward,
  deleteSmartInputComposerForward,
  getSmartInputComposerReferenceIdsInSelection,
  getSmartInputComposerText,
  hasSmartInputComposerReferences,
  insertSmartInputComposerReferences,
  insertSmartInputComposerText,
  normalizeSmartInputComposerSelection,
  pruneSmartInputComposerReferences,
  replaceSmartInputComposerText,
  type SmartInputComposerDraft,
  type SmartInputComposerReferenceKind,
  type SmartInputComposerResolvedReference,
  type SmartInputComposerSelection,
} from './smartInputComposerDraft';
import {
  buildSmartInputTextReferenceFile,
  getSmartInputTextReferenceStats,
  shouldConvertPastedTextToSmartInputReference,
} from './smartInputTextReferences';

let footerDock: HTMLDivElement | null = null;
let footerPrimaryHost: HTMLDivElement | null = null;
let footerContextHost: HTMLDivElement | null = null;
let footerStatusHost: HTMLDivElement | null = null;
let dockedBar: HTMLDivElement | null = null;
let touchControllerEl: HTMLElement | null = null;
let activeTextarea: HTMLTextAreaElement | null = null;
let composerExpandBtn: HTMLButtonElement | null = null;
let sendBtn: HTMLButtonElement | null = null;
let toolsToggleBtn: HTMLButtonElement | null = null;
let toolsPanel: HTMLDivElement | null = null;
let toolButtonsStrip: HTMLDivElement | null = null;
let inlineToolHost: HTMLDivElement | null = null;
let appServerControlAttachmentHost: HTMLDivElement | null = null;
let sharedPhotoInput: HTMLInputElement | null = null;
let sharedAttachInput: HTMLInputElement | null = null;
let toolsPanelOpen = false;
let appServerControlQuickSettingsRow: HTMLDivElement | null = null;
let appServerControlQuickSettingsActions: HTMLDivElement | null = null;
let appServerControlModelSelect: HTMLSelectElement | null = null;
let appServerControlEffortSelect: HTMLSelectElement | null = null;
let appServerControlPlanSelect: HTMLSelectElement | null = null;
let appServerControlPermissionSelect: HTMLSelectElement | null = null;
let appServerControlSettingsSummaryBtn: HTMLButtonElement | null = null;
let autoSendEnabled = localStorage.getItem('smartinput-autosend') === 'true';
let keysExpanded = localStorage.getItem('smartinput-keys-expanded') === 'true';
let isRecording = false;
let pendingMicPinSessionId: string | null = null;
let lastSessionId: string | null = null;
let appServerControlQuickSettingsSheetOpen = false;
let appServerControlGoalComposeSessionId: string | null = null;
let sendAutoSendLongPressTimer: number | null = null;
let suppressNextSendClick = false;
let suppressNextToolsToggleClick = false;
let footerResizeQueued = false;
let footerResizeObserver: ResizeObserver | null = null;
let lastReservedFooterHeightPx = Number.NaN;
let releaseComposerExpandedBackButtonLayer: (() => void) | null = null;
let lastAppliedComposerExpanded = false;

const AUTO_SEND_LONG_PRESS_MS = 520;
const sessionDrafts = new Map<string, SmartInputComposerDraft>();
const appServerControlAttachmentDrafts = new Map<
  string,
  AppServerControlComposerDraftAttachment[]
>();
const sessionPromptHistories = new Map<string, SmartInputPromptHistoryEntry[]>();
const sessionPromptHistoryNavigation = new Map<string, SmartInputPromptHistoryNavigationState>();
const sessionPinnedTools = new Map<string, ToolKind[]>();
const sessionComposerExpanded = new Map<string, boolean>();
let appServerControlResumeConversationHandler:
  | ((args: {
      sessionId: string;
      provider: ResumeProvider;
      workingDirectory: string;
    }) => void | Promise<void>)
  | null = null;

function setTouchKeysExpanded(expanded: boolean): void {
  keysExpanded = expanded;
  localStorage.setItem('smartinput-keys-expanded', String(expanded));
  if (!expanded) {
    closeTouchControllerPopup();
    touchControllerEl?.classList.remove('visible');
  }
  syncSmartInputVisibility();
}

function isComposerExpanded(sessionId: string | null | undefined): boolean {
  return sessionId ? sessionComposerExpanded.get(sessionId) === true : false;
}

function setComposerExpandedForSession(sessionId: string, expanded: boolean): void {
  if (expanded) {
    sessionComposerExpanded.set(sessionId, true);
    return;
  }

  sessionComposerExpanded.delete(sessionId);
}

function setActiveSessionComposerExpanded(expanded: boolean): void {
  const sessionId = $activeSessionId.get();
  if (!sessionId || expanded === isComposerExpanded(sessionId)) {
    return;
  }

  if (expanded) {
    closeFooterTransientUi();
  }

  setComposerExpandedForSession(sessionId, expanded);
  syncSmartInputVisibility(true);
}

function collapseComposerAfterSuccessfulSend(sessionId: string): void {
  if (!isComposerExpanded(sessionId)) {
    return;
  }

  if ($activeSessionId.get() === sessionId) {
    setActiveSessionComposerExpanded(false);
    return;
  }

  setComposerExpandedForSession(sessionId, false);
}

function syncComposerExpandedBackButtonLayer(expanded: boolean): void {
  if (expanded) {
    if (!releaseComposerExpandedBackButtonLayer) {
      releaseComposerExpandedBackButtonLayer = registerBackButtonLayer(() => {
        setActiveSessionComposerExpanded(false);
      });
    }
    return;
  }

  releaseComposerExpandedBackButtonLayer?.();
  releaseComposerExpandedBackButtonLayer = null;
}

interface SmartInputPromptHistoryNavigationState {
  baseline: SmartInputPromptHistoryEntry;
  index: number;
}

interface AdaptiveFooterLayoutState {
  activeSessionId: string | null | undefined;
  appServerControlActive: boolean;
  showInput: boolean;
  showAutomation: boolean;
  showContext: boolean;
  showStatus: boolean;
  showFooter: boolean;
  isMobile: boolean;
  glassEnabled: boolean;
  inputMode: string | null | undefined;
  touchControlsAvailable: boolean;
  touchControlsExpanded: boolean;
}

export function setAppServerControlResumeConversationHandler(
  handler:
    | ((args: {
        sessionId: string;
        provider: ResumeProvider;
        workingDirectory: string;
      }) => void | Promise<void>)
    | null,
): void {
  appServerControlResumeConversationHandler = handler;
  syncSmartInputVisibility();
}

function getSmartInputVisibilityState(): SmartInputVisibilityState {
  const activeSessionId = $activeSessionId.get();
  return {
    activeSessionId,
    inputMode: $currentSettings.get()?.inputMode,
    appServerControlActive: isAppServerControlActiveSession(activeSessionId),
  };
}

function getAdaptiveFooterLayoutState(): AdaptiveFooterLayoutState {
  const visibilityState = getSmartInputVisibilityState();
  const settings = $currentSettings.get();
  const settingsOpen = $settingsOpen.get();
  const activeSessionId = visibilityState.activeSessionId ?? null;
  const isMobile = isMobileViewport();
  const appServerControlActive = visibilityState.appServerControlActive;
  const showInput = !settingsOpen && shouldShowDockedSmartInput(visibilityState);
  const showAutomation =
    !settingsOpen && shouldShowManagerBar(settings?.managerBarEnabled, activeSessionId);
  const touchControlsAvailable = resolveTouchControlsAvailable({
    activeSessionId,
    isMobile,
    appServerControlActive,
  });
  const showContext = settingsOpen
    ? false
    : resolveShowContext({
        isMobile,
        appServerControlActive,
        touchControlsAvailable,
      });
  const showStatus = settingsOpen
    ? false
    : resolveShowStatus({
        activeSessionId,
        isMobile,
        appServerControlActive,
        showInput,
      });
  const showFooter = settingsOpen
    ? false
    : resolveShowFooter({
        activeSessionId,
        showAutomation,
        showContext,
        showInput,
        showStatus,
      });
  const transparency = settings?.uiTransparency ?? 0;

  return {
    activeSessionId,
    appServerControlActive,
    showInput,
    showAutomation,
    showContext,
    showStatus,
    showFooter,
    isMobile,
    glassEnabled: !isMobile && transparency > 0,
    inputMode: settings?.inputMode,
    touchControlsAvailable,
    touchControlsExpanded: touchControlsAvailable && keysExpanded,
  };
}

function setFooterResizeQueued(queued: boolean): void {
  footerResizeQueued = queued;
}

function setLastReservedFooterHeightPx(value: number): void {
  lastReservedFooterHeightPx = value;
}

function resolveTouchControlsAvailable(args: {
  activeSessionId: string | null;
  isMobile: boolean;
  appServerControlActive: boolean;
}): boolean {
  return (
    Boolean(args.activeSessionId) &&
    !args.appServerControlActive &&
    args.isMobile &&
    shouldShowTouchController()
  );
}

function resolveShowContext(args: {
  isMobile: boolean;
  appServerControlActive: boolean;
  touchControlsAvailable: boolean;
}): boolean {
  return args.touchControlsAvailable;
}

function resolveShowStatus(args: {
  activeSessionId: string | null;
  isMobile: boolean;
  appServerControlActive: boolean;
  showInput: boolean;
}): boolean {
  return (
    args.appServerControlActive ||
    (Boolean(args.activeSessionId) && (args.isMobile || args.showInput))
  );
}

function resolveShowFooter(args: {
  activeSessionId: string | null;
  showAutomation: boolean;
  showContext: boolean;
  showInput: boolean;
  showStatus: boolean;
}): boolean {
  return (
    Boolean(args.activeSessionId) &&
    (args.showInput || args.showAutomation || args.showContext || args.showStatus)
  );
}

function getAppServerControlDraftAttachments(
  sessionId: string | null,
): AppServerControlComposerDraftAttachment[] {
  if (sessionId && !appServerControlAttachmentDrafts.has(sessionId)) {
    const persistedAttachments = loadAppServerControlDraftAttachmentsForSession(sessionId);
    if (persistedAttachments.length > 0) {
      appServerControlAttachmentDrafts.set(sessionId, persistedAttachments);
    }
  }

  return getAppServerControlDraftAttachmentsForSession(appServerControlAttachmentDrafts, sessionId);
}

function setAppServerControlDraftAttachments(
  sessionId: string,
  attachments: readonly AppServerControlComposerDraftAttachment[],
): void {
  setAppServerControlDraftAttachmentsForSession(
    appServerControlAttachmentDrafts,
    sessionId,
    attachments,
  );
}

function clearAppServerControlDraftAttachments(sessionId: string, revokePreviews = true): void {
  clearAppServerControlDraftAttachmentsForSession(
    appServerControlAttachmentDrafts,
    sessionId,
    revokePreviews,
  );
}

function detachAppServerControlDraftAttachments(
  sessionId: string,
): AppServerControlComposerDraftAttachment[] {
  return detachAppServerControlDraftAttachmentsForSession(
    appServerControlAttachmentDrafts,
    sessionId,
  );
}

function getSessionPromptHistory(sessionId: string | null): SmartInputPromptHistoryEntry[] {
  if (sessionId && !sessionPromptHistories.has(sessionId)) {
    const persistedHistory = loadSmartInputPromptHistoryForSession(sessionId);
    if (persistedHistory.length > 0) {
      sessionPromptHistories.set(sessionId, persistedHistory);
    }
  }

  return getSmartInputPromptHistoryForSession(sessionPromptHistories, sessionId);
}

function resetPromptHistoryNavigation(sessionId: string | null): void {
  if (!sessionId) {
    return;
  }

  sessionPromptHistoryNavigation.delete(sessionId);
}

function getPromptHistoryQuickSettingsSnapshot(
  sessionId: string,
): SmartInputPromptHistoryEntry['quickSettings'] {
  if (!isAppServerControlActiveSession(sessionId)) {
    return null;
  }

  return getAppServerControlQuickSettingsDraft(sessionId);
}

function createCurrentPromptHistoryEntry(sessionId: string): SmartInputPromptHistoryEntry {
  return {
    composerDraft: cloneSmartInputComposerDraft(getSessionDraft(sessionId)),
    attachments: cloneAppServerControlComposerDraftAttachments(
      getAppServerControlDraftAttachments(sessionId),
    ),
    quickSettings: getPromptHistoryQuickSettingsSnapshot(sessionId),
  };
}

function pushCurrentPromptToHistory(sessionId: string): void {
  pushSmartInputPromptHistoryEntryForSession(
    sessionPromptHistories,
    sessionId,
    createCurrentPromptHistoryEntry(sessionId),
  );
  resetPromptHistoryNavigation(sessionId);
}

function buildSmartInputReferenceTokenText(label: string): string {
  return `[${label}]`;
}

function formatSmartInputReferenceCountLabel(
  count: number,
  singularKey: string,
  pluralKey: string,
): string {
  return `${count.toString()} ${t(count === 1 ? singularKey : pluralKey)}`;
}

function formatTextReferenceDetail(args: { charCount: number; lineCount: number }): string {
  const lineLabel = formatSmartInputReferenceCountLabel(
    args.lineCount,
    'smartInput.referenceLineLabelSingular',
    'smartInput.referenceLineLabelPlural',
  );
  const charLabel = formatSmartInputReferenceCountLabel(
    args.charCount,
    'smartInput.referenceCharLabelSingular',
    'smartInput.referenceCharLabelPlural',
  );
  return `${lineLabel} - ${charLabel}`;
}

function formatSmartInputReferenceLabel(
  kind: SmartInputComposerReferenceKind,
  ordinal: number,
): string {
  const prefix =
    kind === 'image'
      ? t('smartInput.referenceImage')
      : kind === 'file'
        ? t('smartInput.referenceFile')
        : t('smartInput.referenceText');
  return `${prefix} ${ordinal.toString()}`;
}

function resolveComposerReference(
  sessionId: string,
  referenceId: string,
): SmartInputComposerResolvedReference | null {
  const attachment = getAppServerControlDraftAttachments(sessionId).find(
    (candidate) => candidate.id === referenceId,
  );
  if (!attachment?.referenceLabel || !attachment.referenceKind) {
    return null;
  }

  const tokenLabel =
    attachment.referenceKind === 'text' &&
    attachment.referenceLineCount !== null &&
    attachment.referenceCharCount !== null
      ? `${attachment.referenceLabel} - ${formatTextReferenceDetail({
          lineCount: attachment.referenceLineCount,
          charCount: attachment.referenceCharCount,
        })}`
      : attachment.referenceLabel;

  return {
    referenceId,
    kind: attachment.referenceKind,
    label: tokenLabel,
    tokenText: buildSmartInputReferenceTokenText(tokenLabel),
  };
}

function getSessionDraft(sessionId: string | null): SmartInputComposerDraft {
  if (!sessionId) {
    return createSmartInputComposerDraft();
  }

  return sessionDrafts.get(sessionId) ?? createSmartInputComposerDraft();
}

function getSessionDraftText(sessionId: string | null): string {
  if (!sessionId) {
    return '';
  }

  return getSmartInputComposerText(getSessionDraft(sessionId), (referenceId) =>
    resolveComposerReference(sessionId, referenceId),
  );
}

function setSessionDraft(sessionId: string, draft: SmartInputComposerDraft): void {
  const validReferenceIds = new Set(
    getAppServerControlDraftAttachments(sessionId).map((attachment) => attachment.id),
  );
  const normalizedDraft = pruneSmartInputComposerReferences(draft, validReferenceIds);
  if (normalizedDraft.parts.length === 0) {
    sessionDrafts.delete(sessionId);
    return;
  }

  sessionDrafts.set(sessionId, normalizedDraft);
}

function setSessionDraftText(sessionId: string, text: string): void {
  setSessionDraft(sessionId, replaceSmartInputComposerText(getSessionDraft(sessionId), text));
}

function isPromptHistoryNavigationStartable(sessionId: string): boolean {
  return (
    getSessionDraftText(sessionId).length === 0 &&
    getAppServerControlDraftAttachments(sessionId).length === 0
  );
}

function applyPromptHistoryEntry(
  sessionId: string,
  entry: SmartInputPromptHistoryEntry,
  textarea: HTMLTextAreaElement | null = activeTextarea,
): void {
  setAppServerControlDraftAttachments(sessionId, entry.attachments);
  setSessionDraft(sessionId, entry.composerDraft);
  if (isAppServerControlActiveSession(sessionId) && entry.quickSettings) {
    setAppServerControlQuickSettingsDraft(sessionId, entry.quickSettings);
  }

  if ($activeSessionId.get() !== sessionId) {
    return;
  }

  const nextSelection = {
    start: getSessionDraftText(sessionId).length,
    end: getSessionDraftText(sessionId).length,
  };
  renderSessionDraftIntoTextarea(sessionId, textarea, nextSelection);
  renderAppServerControlAttachmentDrafts(sessionId);
  syncAppServerControlQuickSettingsControls();
}

function navigatePromptHistory(
  sessionId: string,
  direction: 'older' | 'newer',
  textarea: HTMLTextAreaElement,
): boolean {
  const history = getSessionPromptHistory(sessionId);
  if (history.length === 0) {
    return false;
  }

  let navigation = sessionPromptHistoryNavigation.get(sessionId) ?? null;
  if (!navigation) {
    if (!isPromptHistoryNavigationStartable(sessionId)) {
      return false;
    }

    navigation = {
      baseline: createCurrentPromptHistoryEntry(sessionId),
      index: -1,
    };
    sessionPromptHistoryNavigation.set(sessionId, navigation);
  }

  if (direction === 'older') {
    if (navigation.index >= history.length - 1) {
      return false;
    }
    navigation.index += 1;
  } else {
    if (navigation.index < 0) {
      return false;
    }
    navigation.index -= 1;
  }

  const historyEntry = navigation.index >= 0 ? history[navigation.index] : navigation.baseline;
  if (!historyEntry) {
    return false;
  }

  const entry = cloneSmartInputPromptHistoryEntry(historyEntry);
  applyPromptHistoryEntry(sessionId, entry, textarea);
  textarea.focus({ preventScroll: true });
  return true;
}

function handlePromptHistoryKeydown(event: KeyboardEvent, textarea: HTMLTextAreaElement): boolean {
  const sessionId = $activeSessionId.get();
  if (
    !sessionId ||
    event.shiftKey ||
    event.ctrlKey ||
    event.altKey ||
    event.metaKey ||
    event.isComposing
  ) {
    return false;
  }

  if (event.key === 'ArrowUp' && navigatePromptHistory(sessionId, 'older', textarea)) {
    event.preventDefault();
    return true;
  }

  if (event.key === 'ArrowDown' && navigatePromptHistory(sessionId, 'newer', textarea)) {
    event.preventDefault();
    return true;
  }

  return false;
}

function draftHasInlineReferences(sessionId: string | null): boolean {
  return sessionId ? hasSmartInputComposerReferences(getSessionDraft(sessionId)) : false;
}

function getSmartInputComposerSelection(
  textarea: HTMLTextAreaElement,
): SmartInputComposerSelection {
  return {
    start: textarea.selectionStart,
    end: textarea.selectionEnd,
  };
}

function renderSessionDraftIntoTextarea(
  sessionId: string | null,
  textarea: HTMLTextAreaElement | null,
  selection: SmartInputComposerSelection | null = null,
): void {
  if (!textarea) {
    return;
  }

  const preserveScrollTop = textarea.scrollTop;
  const nextValue = getSessionDraftText(sessionId);
  if (textarea.value !== nextValue) {
    textarea.value = nextValue;
  }
  resizeSmartInputTextarea(textarea, { preserveScrollTop });
  if (selection) {
    textarea.setSelectionRange(selection.start, selection.end);
  }
}

function updateSessionDraftAndTextarea(
  sessionId: string,
  draft: SmartInputComposerDraft,
  textarea: HTMLTextAreaElement | null = null,
  selection: SmartInputComposerSelection | null = null,
): void {
  setSessionDraft(sessionId, draft);
  if ($activeSessionId.get() === sessionId) {
    renderSessionDraftIntoTextarea(sessionId, textarea ?? activeTextarea, selection);
  }
}

function setActiveTextareaSelection(
  textarea: HTMLTextAreaElement,
  selection: SmartInputComposerSelection,
): void {
  textarea.setSelectionRange(selection.start, selection.end);
}

function syncTextareaSelectionToComposerBoundaries(textarea: HTMLTextAreaElement): void {
  const sessionId = $activeSessionId.get();
  if (!sessionId || !draftHasInlineReferences(sessionId)) {
    return;
  }

  const normalizedSelection = normalizeSmartInputComposerSelection(
    getSessionDraft(sessionId),
    getSmartInputComposerSelection(textarea),
    (referenceId) => resolveComposerReference(sessionId, referenceId),
  );
  if (
    normalizedSelection.start !== textarea.selectionStart ||
    normalizedSelection.end !== textarea.selectionEnd
  ) {
    setActiveTextareaSelection(textarea, normalizedSelection);
  }
}

function createImageDraftAttachmentWithReference(
  sessionId: string,
  file: File,
  uploadedPath: string,
): AppServerControlComposerDraftAttachment {
  const draft = getSessionDraft(sessionId);
  const attachment = createAppServerControlComposerDraftAttachment(sessionId, file, uploadedPath);
  const referenceOrdinal = allocateSmartInputComposerReferenceOrdinal(draft, 'image');
  attachment.referenceKind = 'image';
  attachment.referenceOrdinal = referenceOrdinal;
  attachment.referenceLabel = formatSmartInputReferenceLabel('image', referenceOrdinal);
  setSessionDraft(sessionId, draft);
  return attachment;
}

function createTextDraftAttachmentWithReference(
  sessionId: string,
  file: File,
  uploadedPath: string,
  text: string,
): AppServerControlComposerDraftAttachment {
  const draft = getSessionDraft(sessionId);
  const attachment = createAppServerControlComposerDraftAttachment(
    sessionId,
    file,
    uploadedPath,
    file,
  );
  const stats = getSmartInputTextReferenceStats(text);
  const referenceOrdinal = allocateSmartInputComposerReferenceOrdinal(draft, 'text');
  attachment.referenceKind = 'text';
  attachment.referenceOrdinal = referenceOrdinal;
  attachment.referenceLabel = formatSmartInputReferenceLabel('text', referenceOrdinal);
  attachment.referenceLineCount = stats.lineCount;
  attachment.referenceCharCount = stats.charCount;
  setSessionDraft(sessionId, draft);
  return attachment;
}

function removeAttachmentsByIds(sessionId: string, attachmentIds: readonly string[]): void {
  if (attachmentIds.length === 0) {
    return;
  }

  resetPromptHistoryNavigation(sessionId);
  const toRemove = new Set(attachmentIds);
  const attachments = getAppServerControlDraftAttachments(sessionId);
  const nextAttachments: AppServerControlComposerDraftAttachment[] = [];
  for (const attachment of attachments) {
    if (toRemove.has(attachment.id)) {
      releaseAppServerControlComposerDraftAttachmentPreviews([attachment]);
      continue;
    }

    nextAttachments.push(attachment);
  }

  setAppServerControlDraftAttachments(sessionId, nextAttachments);
  setSessionDraft(
    sessionId,
    pruneSmartInputComposerReferences(
      getSessionDraft(sessionId),
      new Set(nextAttachments.map((attachment) => attachment.id)),
    ),
  );
  if ($activeSessionId.get() === sessionId) {
    renderSessionDraftIntoTextarea(sessionId, activeTextarea);
    renderAppServerControlAttachmentDrafts(sessionId);
  }
}

/**
 * Treats AppServerControl as a conversation-first composer surface even when the global
 * input mode is not Smart Input, so agent turns always use the docked composer.
 */
export function isSmartInputMode(): boolean {
  const state = getSmartInputVisibilityState();
  return (
    state.appServerControlActive ||
    (Boolean(state.activeSessionId) && state.inputMode === 'smartinput')
  );
}

/**
 * Prevents AppServerControl sessions from falling into dual-focus input semantics because
 * the conversation lane needs one clear place to type and submit.
 */
export function isBothMode(): boolean {
  if (isAppServerControlActiveSession($activeSessionId.get())) {
    return false;
  }

  return $currentSettings.get()?.inputMode === 'both';
}

/**
 * Keeps the footer dock aligned with session changes, AppServerControl activation, mobile
 * touch controls, and dev-only voice affordances.
 */
export function initSmartInput(): void {
  ensureFooterHosts();

  $activeSessionId.subscribe((sessionId) => {
    persistDraftForSession(lastSessionId);
    lastSessionId = sessionId;
    syncDraftForActiveSession();
    syncSmartInputVisibility(
      isAppServerControlActiveSession(sessionId) && shouldAllowProgrammaticSmartInputFocus(),
    );
  });

  $currentSettings.subscribe((settings) => {
    if (!settings) return;
    persistDraftForSession($activeSessionId.get());
    syncSmartInputVisibility();
  });

  $settingsOpen.subscribe(() => {
    syncSmartInputVisibility();
  });

  $voiceServerPassword.subscribe(() => {
    syncVoiceInputAvailability();
  });

  onDevModeChanged(() => {
    syncVoiceInputAvailability();
  });

  if (typeof window !== 'undefined') {
    window.addEventListener(APP_SERVER_CONTROL_QUICK_SETTINGS_CHANGED_EVENT, () => {
      syncAppServerControlQuickSettingsControls();
      syncSmartInputVisibility();
    });
    window.addEventListener('resize', () => {
      syncSmartInputVisibility();
    });
    window.addEventListener('orientationchange', () => {
      syncSmartInputVisibility();
    });
  }

  onTabActivated('agent', (sessionId) => {
    if ($activeSessionId.get() === sessionId) {
      syncSmartInputVisibility(shouldAllowProgrammaticSmartInputFocus());
    }
  });

  onTabActivated('terminal', (sessionId) => {
    if ($activeSessionId.get() === sessionId) {
      syncSmartInputVisibility();
    }
  });

  onTabActivated('files', (sessionId) => {
    if ($activeSessionId.get() === sessionId) {
      syncSmartInputVisibility();
    }
  });

  bindSmartInputGlobalKeyBindings({
    beginRecording,
    canUseVoice: canUseSmartInputVoiceSupport,
    closeFooterTransientUi,
    endRecording,
    getInterruptibleAppServerControlSessionId: () => {
      const sessionId = $activeSessionId.get();
      if (
        !sessionId ||
        !isAppServerControlActiveSession(sessionId) ||
        !hasInterruptibleAppServerControlTurnWork(sessionId)
      ) {
        return null;
      }

      return sessionId;
    },
    hasVisibleInput: () => getAdaptiveFooterLayoutState().showInput,
    isRecording: () => isRecording,
    onAppServerControlEscape: (sessionId) => {
      void handleAppServerControlEscape(sessionId);
    },
  });

  document.addEventListener('click', (event) => {
    const target = event.target as Node | null;
    if (!target) {
      return;
    }

    if (shouldIgnoreFooterTransientUiDocumentClickSupport(target)) {
      return;
    }

    if (
      toolsPanel &&
      toolsToggleBtn &&
      !(toolsPanel.contains(target) || toolsToggleBtn.contains(target))
    ) {
      setToolsPanelOpen(false);
    }

    if (appServerControlQuickSettingsRow && appServerControlSettingsSummaryBtn) {
      const clickedInsideAppServerControlSettings =
        appServerControlQuickSettingsRow.contains(target) ||
        appServerControlSettingsSummaryBtn.contains(target);
      if (!clickedInsideAppServerControlSettings) {
        setAppServerControlQuickSettingsSheetOpen(false);
      }
    }
  });
}

export function showSmartInput(): void {
  syncSmartInputVisibility(shouldAllowProgrammaticSmartInputFocus());
}

export function hideSmartInput(): void {
  syncSmartInputVisibility();
}
function syncSmartInputVisibility(focusTextarea: boolean = false): void {
  ensureFooterHosts();
  const preserveTextareaFocus = document.activeElement === activeTextarea;

  const layoutState = getAdaptiveFooterLayoutState();
  if (!layoutState.showFooter) {
    hideAdaptiveFooter();
    updateFooterReservedHeight();
    return;
  }

  if (!dockedBar) {
    createDockedDOM();
  }

  applyFooterPresentation(layoutState);
  syncInputRow(layoutState);
  syncContextRow(layoutState);
  syncComposerExpandedPresentation(layoutState);

  const managerBar = document.getElementById('manager-bar');
  managerBar?.classList.toggle('hidden', !layoutState.showAutomation || layoutState.isMobile);
  syncFooterRailOrder(layoutState);
  syncStatusRow(layoutState);
  footerDock?.toggleAttribute('hidden', false);

  syncVoiceInputAvailability();
  updateAutoSendVisibilitySupport({ dockedBar, sendBtn, autoSendEnabled });
  queueFooterReserveSync();

  if (
    layoutState.showInput &&
    (preserveTextareaFocus || (focusTextarea && shouldAllowProgrammaticSmartInputFocus()))
  ) {
    requestAnimationFrame(() => {
      activeTextarea?.focus({ preventScroll: true });
    });
  }
}

function hideAdaptiveFooter(): void {
  if (!footerDock) {
    return;
  }
  setToolsPanelOpen(false);
  setAppServerControlQuickSettingsSheetOpen(false);
  footerDock.dataset.composerExpanded = 'false';
  if (dockedBar) {
    dockedBar.dataset.composerExpanded = 'false';
  }
  lastAppliedComposerExpanded = false;
  syncComposerExpandedBackButtonLayer(false);
  footerDock.hidden = true;
  footerPrimaryHost?.setAttribute('hidden', '');
  footerContextHost?.setAttribute('hidden', '');
  footerStatusHost?.setAttribute('hidden', '');
}

function ensureFooterHosts(): void {
  footerDock ??= document.getElementById('adaptive-footer-dock') as HTMLDivElement | null;
  footerPrimaryHost ??= document.getElementById('adaptive-footer-primary') as HTMLDivElement | null;
  footerContextHost ??= document.getElementById('adaptive-footer-context') as HTMLDivElement | null;
  footerStatusHost ??= document.getElementById('adaptive-footer-status') as HTMLDivElement | null;
  if (footerDock && !('composerBackdropClickBound' in footerDock.dataset)) {
    footerDock.dataset.composerBackdropClickBound = 'true';
    footerDock.addEventListener('click', (event) => {
      if (event.target === footerDock && isComposerExpanded($activeSessionId.get())) {
        setActiveSessionComposerExpanded(false);
      }
    });
  }
  ensureFooterResizeObserver();
}

function ensureFooterResizeObserver(): void {
  if (footerResizeObserver || typeof ResizeObserver === 'undefined' || !footerDock) {
    return;
  }
  footerResizeObserver = new ResizeObserver(() => {
    queueFooterReserveSync();
  });
  footerResizeObserver.observe(footerDock);
}

function applyFooterPresentation(layoutState: AdaptiveFooterLayoutState): void {
  if (!footerDock) {
    return;
  }
  footerDock.dataset.surface = layoutState.appServerControlActive ? 'appServerControl' : 'terminal';
  footerDock.dataset.device = layoutState.isMobile ? 'mobile' : 'desktop';
  footerDock.dataset.material = layoutState.glassEnabled ? 'glass' : 'solid';
  footerDock.dataset.inputMode = layoutState.inputMode ?? 'keyboard';
  footerDock.classList.toggle('keys-expanded', layoutState.touchControlsExpanded);
}

function syncComposerExpandedPresentation(layoutState: AdaptiveFooterLayoutState): void {
  const composerExpanded = layoutState.showInput && isComposerExpanded(layoutState.activeSessionId);
  footerDock?.setAttribute('data-composer-expanded', composerExpanded ? 'true' : 'false');
  dockedBar?.setAttribute('data-composer-expanded', composerExpanded ? 'true' : 'false');
  if (composerExpandBtn) {
    syncSmartInputComposerExpandToggleState(composerExpandBtn, composerExpanded);
  }
  syncComposerExpandedBackButtonLayer(composerExpanded);

  const stateChanged = composerExpanded !== lastAppliedComposerExpanded;
  lastAppliedComposerExpanded = composerExpanded;
  if (!stateChanged || !activeTextarea) {
    return;
  }

  const textarea = activeTextarea;
  const preserveScrollTop = textarea.scrollTop;
  const preserveComposerFocus = document.activeElement === textarea;
  requestAnimationFrame(() => {
    resizeSmartInputTextarea(textarea, { preserveScrollTop });
    if (preserveComposerFocus || shouldAllowProgrammaticSmartInputFocus()) {
      textarea.focus({ preventScroll: true });
    }
    if (!footerResizeObserver) {
      queueFooterReserveSync();
    }
  });
}

function shouldAllowProgrammaticSmartInputFocus(): boolean {
  return !isEmbeddedWebPreviewContext();
}

function shouldKeepFocusedComposerVisibleOnMobileAppServerControl(): boolean {
  const sessionId = $activeSessionId.get();
  return (
    Boolean(sessionId) &&
    isAppServerControlActiveSession(sessionId) &&
    isMobileViewport() &&
    document.body.classList.contains('keyboard-visible')
  );
}

function scrollFooterDockForTextareaFocus(): void {
  if (!footerDock) {
    return;
  }

  footerDock.scrollTo({
    top: shouldKeepFocusedComposerVisibleOnMobileAppServerControl() ? footerDock.scrollHeight : 0,
    behavior: 'auto',
  });
}

function createDockedDOM(): void {
  ensureFooterHosts();
  if (!footerPrimaryHost || !footerContextHost || !footerStatusHost) {
    return;
  }

  dockedBar = document.createElement('div');
  dockedBar.className = 'smart-input-docked';

  const dom = createSmartInputDom({
    createToolsStrip: () => createToolButtonsStrip(getToolButtonRenderArgs()),
    onAttachInputChange: (files) => {
      void handleSmartInputSelectedFiles(files);
    },
    onExpandToggleClick: (event) => {
      event.preventDefault();
      event.stopPropagation();
      setActiveSessionComposerExpanded(!isComposerExpanded($activeSessionId.get()));
    },
    onAppServerControlEffortChange: () => {
      const sessionId = $activeSessionId.get();
      if (!sessionId || !isAppServerControlActiveSession(sessionId)) {
        return;
      }

      setAppServerControlQuickSettingsDraft(sessionId, {
        effort: appServerControlEffortSelect?.value ?? null,
      });
    },
    onAppServerControlModelChange: () => {
      const sessionId = $activeSessionId.get();
      if (!sessionId || !isAppServerControlActiveSession(sessionId)) {
        return;
      }

      setAppServerControlQuickSettingsDraft(sessionId, {
        model: appServerControlModelSelect?.value ?? null,
      });
    },
    onAppServerControlPermissionChange: () => {
      const sessionId = $activeSessionId.get();
      if (!sessionId || !isAppServerControlActiveSession(sessionId)) {
        return;
      }

      setAppServerControlQuickSettingsDraft(sessionId, {
        permissionMode: appServerControlPermissionSelect?.value ?? 'manual',
      });
    },
    onAppServerControlPlanChange: () => {
      const sessionId = $activeSessionId.get();
      if (!sessionId || !isAppServerControlActiveSession(sessionId)) {
        return;
      }

      setAppServerControlQuickSettingsDraft(sessionId, {
        planMode: appServerControlPlanSelect?.value ?? 'off',
      });
      syncSmartInputVisibility();
    },
    onPhotoInputChange: (files) => {
      void handleSmartInputSelectedFiles(files);
    },
    onSendClick: () => {
      if (suppressNextSendClick) {
        suppressNextSendClick = false;
        return;
      }

      if (activeTextarea) {
        void sendText(activeTextarea);
      }
    },
    onSendDoubleClick: (event) => {
      if (isMobileViewport()) {
        return;
      }

      event.preventDefault();
      toggleAutoSendEnabled();
    },
    onSendPointerDown: () => {
      if (!isMobileViewport()) {
        return;
      }

      clearSendAutoSendLongPressTimer();
      sendAutoSendLongPressTimer = window.setTimeout(() => {
        toggleAutoSendEnabled();
        suppressNextSendClick = true;
        sendAutoSendLongPressTimer = null;
      }, AUTO_SEND_LONG_PRESS_MS);
    },
    onSendPointerEnd: () => {
      clearSendAutoSendLongPressTimer();
    },
    onTextareaFocus: () => {
      queueFooterReserveSync();
      requestAnimationFrame(() => {
        scrollFooterDockForTextareaFocus();
      });
    },
    onTextareaBeforeInput: (event, textarea) => {
      handleSmartInputBeforeInput(event, textarea);
    },
    onTextareaCut: (event, textarea) => {
      handleSmartInputCut(event, textarea);
    },
    onTextareaInput: (textarea) => {
      let draftRenderedIntoTextarea = false;
      const sessionId = $activeSessionId.get();
      if (sessionId) {
        resetPromptHistoryNavigation(sessionId);
        if (draftHasInlineReferences(sessionId)) {
          draftRenderedIntoTextarea = true;
          renderSessionDraftIntoTextarea(
            sessionId,
            textarea,
            normalizeSmartInputComposerSelection(
              getSessionDraft(sessionId),
              getSmartInputComposerSelection(textarea),
              (referenceId) => resolveComposerReference(sessionId, referenceId),
            ),
          );
        } else {
          setSessionDraftText(sessionId, textarea.value);
        }
      }
      if (!draftRenderedIntoTextarea) {
        resizeSmartInputTextarea(textarea);
      }
      if (!footerResizeObserver) {
        queueFooterReserveSync();
      }
    },
    onTextareaKeydown: (event, textarea) => {
      if (handleSmartInputShiftTabShortcut(event)) {
        return;
      }

      if (handlePromptHistoryKeydown(event, textarea)) {
        return;
      }

      const sessionId = $activeSessionId.get();
      if (
        event.key === 'Escape' &&
        !event.shiftKey &&
        !event.ctrlKey &&
        !event.altKey &&
        !event.metaKey
      ) {
        if (isComposerExpanded(sessionId)) {
          event.preventDefault();
          setActiveSessionComposerExpanded(false);
          return;
        }

        if (sessionId && isAppServerControlActiveSession(sessionId)) {
          event.preventDefault();
          void handleAppServerControlEscape(sessionId);
          return;
        }
      }

      if (shouldInsertLineBreakOnEnter(event)) {
        event.preventDefault();
        const sessionId = $activeSessionId.get();
        if (sessionId && draftHasInlineReferences(sessionId)) {
          insertComposerTextAtSelection(sessionId, textarea, '\n');
        } else {
          insertSmartInputLineBreak(textarea);
        }
        return;
      }

      if (shouldSubmitSmartInputOnEnter(event)) {
        event.preventDefault();
        void sendText(textarea);
      }
    },
    onTextareaPaste: (event) => {
      const sessionId = $activeSessionId.get();
      if (!sessionId) {
        return;
      }

      if (clipboardDataMayContainAppServerControlComposerImage(event.clipboardData)) {
        event.preventDefault();
        const selection = activeTextarea ? getSmartInputComposerSelection(activeTextarea) : null;
        void (async () => {
          const files = await extractAppServerControlComposerPasteImageFiles(event.clipboardData);
          if (files.length === 0) {
            return;
          }

          await addAppServerControlComposerFiles(sessionId, files, selection);
        })();
        return;
      }

      const text = event.clipboardData?.getData('text/plain') ?? '';
      if (text && shouldConvertPastedTextToSmartInputReference(text)) {
        event.preventDefault();
        const selection = activeTextarea ? getSmartInputComposerSelection(activeTextarea) : null;
        void addAppServerControlComposerTextReference(sessionId, text, selection);
        return;
      }

      if (draftHasInlineReferences(sessionId)) {
        if (!text) {
          return;
        }

        event.preventDefault();
        insertComposerTextAtSelection(sessionId, activeTextarea, text);
      }
    },
    onTextareaSelect: (textarea) => {
      syncTextareaSelectionToComposerBoundaries(textarea);
    },
    onToolsTogglePointerDown: (event) => {
      if (!isMobileViewport()) {
        return;
      }

      event.preventDefault();
      event.stopPropagation();
      suppressNextToolsToggleClick = true;
      setToolsPanelOpen(!toolsPanelOpen);

      const preserveComposerFocus =
        activeTextarea === document.activeElement ||
        document.body.classList.contains('keyboard-visible');
      if (preserveComposerFocus) {
        requestAnimationFrame(() => {
          activeTextarea?.focus({ preventScroll: true });
        });
      }
    },
    onToolsToggleClick: (event) => {
      event.preventDefault();
      event.stopPropagation();
      if (suppressNextToolsToggleClick) {
        suppressNextToolsToggleClick = false;
        return;
      }
      setToolsPanelOpen(!toolsPanelOpen);
    },
    resizeTextarea: resizeSmartInputTextarea,
  });
  dockedBar.appendChild(dom.inputRow);
  footerPrimaryHost.appendChild(dockedBar);

  appServerControlQuickSettingsRow = dom.appServerControlQuickSettingsRow;
  appServerControlQuickSettingsActions = dom.appServerControlQuickSettingsActions;
  appServerControlModelSelect = dom.appServerControlModelSelect;
  appServerControlEffortSelect = dom.appServerControlEffortSelect;
  appServerControlPlanSelect = dom.appServerControlPlanSelect;
  appServerControlPermissionSelect = dom.appServerControlPermissionSelect;
  appServerControlAttachmentHost = dom.appServerControlAttachmentHost;
  composerExpandBtn = dom.composerExpandBtn;
  activeTextarea = dom.textarea;
  sendBtn = dom.sendBtn;
  toolsToggleBtn = dom.toolsToggleBtn;
  inlineToolHost = dom.inlineToolHost;
  sharedPhotoInput = dom.photoInput;
  sharedAttachInput = dom.attachInput;
  toolsPanel = dom.toolsPanel;
  toolButtonsStrip = dom.toolsStrip;
  toolsPanel.appendChild(toolButtonsStrip);

  touchControllerEl ??= document.getElementById('touch-controller');
  if (touchControllerEl && touchControllerEl.parentElement !== footerContextHost) {
    footerContextHost.appendChild(touchControllerEl);
    touchControllerEl.classList.add('embedded');
  }
}

function openFileInputPicker(input: HTMLInputElement): void {
  showSmartInputFilePicker(input);
}

function getToolButtonRenderArgs(): Parameters<typeof createToolButtonsStrip>[0] {
  return {
    canUseVoice: canUseSmartInputVoiceSupport(),
    onAttachClick: (pinOnUse, event) => {
      event.preventDefault();
      event.stopPropagation();
      maybePinToolForActiveSession('attach', pinOnUse);
      if (sharedAttachInput) {
        openFileInputPicker(sharedAttachInput);
      }
    },
    onMicPointerDown: (pinOnUse, event) => {
      event.preventDefault();
      event.stopPropagation();
      beginRecording(pinOnUse);
    },
    onMicPointerCancel: () => {
      endRecording();
    },
    onMicPointerLeave: () => {
      if (isRecording) {
        endRecording();
      }
    },
    onMicPointerUp: () => {
      endRecording();
    },
    onPhotoClick: (pinOnUse, event) => {
      event.preventDefault();
      event.stopPropagation();
      maybePinToolForActiveSession('photo', pinOnUse);
      if (isTouchPrimaryDevice()) {
        if (sharedPhotoInput) {
          openFileInputPicker(sharedPhotoInput);
        }
        return;
      }

      void captureImageFromWebcam((files) => handleSmartInputSelectedFiles(files));
    },
  };
}

function createAutomationOverflowProxy(): HTMLButtonElement {
  const btn = document.createElement('button');
  btn.type = 'button';
  btn.className = 'manager-bar-overflow adaptive-footer-status-automation-proxy';
  btn.title = t('managerBar.more');
  btn.innerHTML = '<span class="icon">&#xe910;</span>';
  btn.addEventListener('click', (event) => {
    event.preventDefault();
    event.stopPropagation();
    triggerAutomationOverflow(btn);
  });
  return btn;
}

function createAutomationAddProxy(): HTMLButtonElement {
  const btn = document.createElement('button');
  btn.type = 'button';
  btn.className = 'manager-bar-add adaptive-footer-status-automation-proxy';
  btn.title = t('managerBar.addButton');
  btn.textContent = '+';
  btn.addEventListener('click', (event) => {
    event.preventDefault();
    event.stopPropagation();
    triggerAddAutomation();
  });
  return btn;
}

function syncInputRow(layoutState: AdaptiveFooterLayoutState): void {
  if (!footerPrimaryHost || !dockedBar) {
    return;
  }

  footerPrimaryHost.toggleAttribute('hidden', !layoutState.showInput);
  dockedBar.classList.toggle('visible', layoutState.showInput);

  if (!layoutState.showInput) {
    setToolsPanelOpen(false);
    return;
  }

  dockedBar.dataset.surface = layoutState.appServerControlActive ? 'appServerControl' : 'terminal';
  dockedBar.dataset.device = layoutState.isMobile ? 'mobile' : 'desktop';

  activeTextarea = dockedBar.querySelector('.smart-input-textarea');
  if (activeTextarea) {
    activeTextarea.placeholder = t(
      layoutState.isMobile ? 'smartInput.placeholderMobile' : 'smartInput.placeholder',
    );
    applyDraftToTextarea(activeTextarea, layoutState.activeSessionId ?? null);
  }

  renderPinnedToolsForSession(layoutState.activeSessionId ?? null);
  if (layoutState.appServerControlActive && layoutState.isMobile && inlineToolHost) {
    inlineToolHost.hidden = true;
  }
  toolsToggleBtn?.removeAttribute('hidden');
  setToolsPanelOpen(toolsPanelOpen);
}

function syncContextRow(layoutState: AdaptiveFooterLayoutState): void {
  if (!footerContextHost) {
    return;
  }

  footerContextHost.replaceChildren();

  if (toolButtonsStrip && toolsPanel && toolButtonsStrip.parentElement !== toolsPanel) {
    toolsPanel.appendChild(toolButtonsStrip);
  }

  if (layoutState.isMobile && !layoutState.touchControlsExpanded) {
    touchControllerEl?.classList.remove('visible');
    footerContextHost.hidden = true;
    return;
  }

  if (layoutState.showContext && layoutState.touchControlsAvailable) {
    if (keysExpanded && touchControllerEl) {
      touchControllerEl.classList.add('embedded', 'visible');
      footerContextHost.appendChild(touchControllerEl);
      footerContextHost.hidden = false;
      return;
    }
  }

  touchControllerEl?.classList.remove('visible');
  footerContextHost.hidden = true;
}

function syncFooterRailOrder(layoutState: AdaptiveFooterLayoutState): void {
  if (!footerDock || !footerPrimaryHost || !footerContextHost || !footerStatusHost) {
    return;
  }
  const currentFooterDock = footerDock;

  const managerBar = document.getElementById('manager-bar');
  if (!managerBar) {
    return;
  }

  const rails = {
    primary: footerPrimaryHost,
    automation: managerBar,
    context: footerContextHost,
    status: footerStatusHost,
  } satisfies Record<ReturnType<typeof getAdaptiveFooterRailSequence>[number], HTMLElement>;

  const desiredOrder = getAdaptiveFooterRailSequence(layoutState);
  const needsReorder = desiredOrder.some(
    (key, index) => currentFooterDock.children.item(index) !== rails[key],
  );
  if (!needsReorder) {
    return;
  }

  for (const key of desiredOrder) {
    currentFooterDock.appendChild(rails[key]);
  }
}

function syncStatusRow(layoutState: AdaptiveFooterLayoutState): void {
  if (!footerStatusHost) {
    return;
  }

  footerStatusHost.replaceChildren();
  footerStatusHost.classList.remove('adaptive-footer-status-sheet-open');
  footerStatusHost.dataset.appServerControlCompact = 'false';
  footerStatusHost.toggleAttribute('hidden', !layoutState.showStatus);
  syncAppServerControlQuickSettingsControls();

  if (!layoutState.showStatus || !layoutState.activeSessionId) {
    setAutomationOverflowProxyAnchor(null);
    setAppServerControlQuickSettingsSheetOpen(false);
    return;
  }

  if (layoutState.appServerControlActive) {
    renderAppServerControlStatusRow(layoutState);
    return;
  }

  if (layoutState.isMobile) {
    renderMobileTerminalStatusRow(layoutState);
    return;
  }

  setAutomationOverflowProxyAnchor(null);
  const renderedTerminalStatus = renderTerminalStatusRow({
    autoSendEnabled: canUseSmartInputVoiceSupport() && autoSendEnabled,
    footerStatusHost,
  });
  footerStatusHost.toggleAttribute('hidden', !renderedTerminalStatus);
}

function renderMobileTerminalStatusRow(layoutState: AdaptiveFooterLayoutState): void {
  if (!footerStatusHost) {
    return;
  }

  setAutomationOverflowProxyAnchor(null);

  const leftCluster = document.createElement('div');
  leftCluster.className = 'adaptive-footer-status-left';

  if (canUseSmartInputVoiceSupport() && autoSendEnabled) {
    const autoSendPill = document.createElement('div');
    autoSendPill.className = 'adaptive-footer-status-pill';
    autoSendPill.textContent = t('smartInput.autoSend');
    leftCluster.appendChild(autoSendPill);
  }

  if (layoutState.touchControlsAvailable) {
    const keysToggle = createTerminalTouchToggleButton({
      expanded: layoutState.touchControlsExpanded,
      onToggle: () => {
        setTouchKeysExpanded(!layoutState.touchControlsExpanded);
      },
    });
    leftCluster.appendChild(keysToggle);
  }

  if (leftCluster.childElementCount > 0) {
    footerStatusHost.appendChild(leftCluster);
  }

  if (layoutState.showAutomation) {
    const rightCluster = document.createElement('div');
    rightCluster.className = 'adaptive-footer-status-right';

    const overflowProxy = createAutomationOverflowProxy();
    rightCluster.appendChild(overflowProxy);
    setAutomationOverflowProxyAnchor(overflowProxy);

    const addProxy = createAutomationAddProxy();
    rightCluster.appendChild(addProxy);

    footerStatusHost.appendChild(rightCluster);
  }

  footerStatusHost.toggleAttribute('hidden', false);
}

function renderAppServerControlStatusRow(layoutState: AdaptiveFooterLayoutState): void {
  if (
    !footerStatusHost ||
    !appServerControlQuickSettingsRow ||
    !appServerControlQuickSettingsActions ||
    !appServerControlModelSelect ||
    !appServerControlEffortSelect ||
    !appServerControlPlanSelect ||
    !appServerControlPermissionSelect
  ) {
    return;
  }

  const sessionId = layoutState.activeSessionId as string;
  const draft = getAppServerControlQuickSettingsDraft(sessionId);
  syncAppServerControlQuickSettingsActions(sessionId);
  const useCompactRail = shouldUseCompactAppServerControlStatusRail(layoutState);
  footerStatusHost.dataset.appServerControlCompact = useCompactRail ? 'true' : 'false';

  if (!useCompactRail) {
    appServerControlQuickSettingsRow.classList.remove(
      'smart-input-appServerControl-settings-sheet',
    );
    appServerControlQuickSettingsRow.hidden = false;
    footerStatusHost.appendChild(appServerControlQuickSettingsRow);
    return;
  }

  const summaryBtn = document.createElement('button');
  summaryBtn.type = 'button';
  summaryBtn.className =
    'adaptive-footer-status-summary adaptive-footer-status-summary-appServerControl';
  summaryBtn.textContent = formatAppServerControlQuickSettingsSummary(draft);
  summaryBtn.dataset.planMode = draft.planMode;
  summaryBtn.setAttribute(
    'aria-expanded',
    appServerControlQuickSettingsSheetOpen ? 'true' : 'false',
  );
  summaryBtn.addEventListener('click', (event) => {
    event.preventDefault();
    event.stopPropagation();
    setAppServerControlQuickSettingsSheetOpen(!appServerControlQuickSettingsSheetOpen);
  });
  appServerControlSettingsSummaryBtn = summaryBtn;
  footerStatusHost.appendChild(summaryBtn);

  setAutomationOverflowProxyAnchor(null);
  if (layoutState.isMobile && layoutState.showAutomation) {
    const overflowProxy = createAutomationOverflowProxy();
    footerStatusHost.appendChild(overflowProxy);
    setAutomationOverflowProxyAnchor(overflowProxy);
  }

  appServerControlQuickSettingsRow.classList.add('smart-input-appServerControl-settings-sheet');
  appServerControlQuickSettingsRow.hidden = !appServerControlQuickSettingsSheetOpen;
  if (appServerControlQuickSettingsSheetOpen) {
    footerStatusHost.classList.add('adaptive-footer-status-sheet-open');
    footerStatusHost.appendChild(appServerControlQuickSettingsRow);
  }
}

function shouldUseCompactAppServerControlStatusRail(
  layoutState: AdaptiveFooterLayoutState,
): boolean {
  if (layoutState.isMobile) {
    return true;
  }

  const availableWidth = Math.round(footerDock?.getBoundingClientRect().width ?? window.innerWidth);
  return availableWidth <= 720;
}

function syncAppServerControlQuickSettingsActions(sessionId: string): void {
  if (!appServerControlQuickSettingsActions) {
    return;
  }

  appServerControlQuickSettingsActions.replaceChildren();
  const quickSettingsLocked = hasInterruptibleAppServerControlTurnWork(sessionId);
  const draft = getAppServerControlQuickSettingsDraft(sessionId);
  const provider = getAppServerControlQuickSettingsProvider(sessionId);
  appServerControlQuickSettingsActions.appendChild(
    createAppServerControlActionButton(
      'Plan',
      t('smartInput.appServerControlPlanCommand'),
      () => {
        toggleAppServerControlPlanMode(sessionId);
      },
      quickSettingsLocked,
      { pressed: draft.planMode === 'on' },
    ),
  );
  if (provider === 'codex') {
    appServerControlQuickSettingsActions.appendChild(
      createAppServerControlActionButton(
        'Goal',
        t('smartInput.appServerControlGoalCommand'),
        () => {
          void prepareAppServerControlGoal(sessionId);
        },
        false,
        { pressed: appServerControlGoalComposeSessionId === sessionId },
      ),
    );
  }
  const resumeButton = createAppServerControlResumeButton(
    sessionId,
    appServerControlResumeConversationHandler,
  );
  if (resumeButton) {
    appServerControlQuickSettingsActions.appendChild(resumeButton);
  }
  appServerControlQuickSettingsActions.hidden =
    appServerControlQuickSettingsActions.childElementCount === 0;
}

function createAppServerControlActionButton(
  label: string,
  title: string,
  onClick: () => void,
  disabled: boolean,
  options: { pressed?: boolean } = {},
): HTMLButtonElement {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'smart-input-appServerControl-action';
  button.textContent = label;
  button.title = title;
  button.setAttribute('aria-label', title);
  button.setAttribute('aria-pressed', options.pressed ? 'true' : 'false');
  button.disabled = disabled;
  button.addEventListener('click', (event) => {
    event.preventDefault();
    event.stopPropagation();
    if (!disabled) {
      onClick();
    }
  });
  return button;
}

async function prepareAppServerControlGoal(sessionId: string): Promise<void> {
  if (!activeTextarea) {
    return;
  }

  if (appServerControlGoalComposeSessionId === sessionId) {
    appServerControlGoalComposeSessionId = null;
    activeTextarea.placeholder = t(
      isMobileViewport() ? 'smartInput.placeholderMobile' : 'smartInput.placeholder',
    );
    activeTextarea.focus({ preventScroll: true });
    syncAppServerControlQuickSettingsActions(sessionId);
    return;
  }

  const currentText = activeTextarea.value.trim();
  if (currentText.length === 0) {
    appServerControlGoalComposeSessionId = sessionId;
    activeTextarea.placeholder = 'Set AppServerControl goal...';
    activeTextarea.focus({ preventScroll: true });
    syncAppServerControlQuickSettingsActions(sessionId);
    return;
  }

  await submitAppServerControlGoal(sessionId, currentText, activeTextarea);
}

async function submitAppServerControlGoal(
  sessionId: string,
  objective: string,
  textarea: HTMLTextAreaElement,
): Promise<void> {
  try {
    await setAppServerControlGoal(sessionId, { objective });
    appServerControlGoalComposeSessionId = null;
    pushCurrentPromptToHistory(sessionId);
    clearSubmittedSmartInputState(sessionId, textarea);
    showDropToast('Goal set.');
    syncAppServerControlQuickSettingsActions(sessionId);
  } catch (error) {
    showDropToast(error instanceof Error && error.message.trim() ? error.message : String(error));
  }
}

function setToolsPanelOpen(open: boolean): void {
  if (!toolsPanel || !toolsToggleBtn) {
    return;
  }

  const canOpen = Boolean(toolButtonsStrip) && !toolsToggleBtn.hidden;
  const shouldOpen = open && canOpen;
  toolsPanelOpen = shouldOpen;
  if (toolButtonsStrip && toolButtonsStrip.parentElement !== toolsPanel) {
    toolsPanel.appendChild(toolButtonsStrip);
  }
  toolsPanel.hidden = !shouldOpen;
  toolsPanel.parentElement?.classList.toggle('smart-input-row-tools-open', shouldOpen);
  toolsToggleBtn.setAttribute('aria-expanded', shouldOpen ? 'true' : 'false');
  toolsToggleBtn.classList.toggle('open', shouldOpen);
  if (!footerResizeObserver) {
    queueFooterReserveSync();
  }
}

function maybePinToolForActiveSession(
  tool: ToolKind,
  pinOnUse: boolean,
  closePanel: boolean = true,
): void {
  if (!pinOnUse) {
    return;
  }
  const sessionId = $activeSessionId.get();
  if (!sessionId) {
    return;
  }
  pinToolForSession(sessionId, tool);
  if (closePanel && toolsPanelOpen) {
    setToolsPanelOpen(false);
  }
}

function renderPinnedToolsForSession(sessionId: string | null): void {
  if (!inlineToolHost) {
    return;
  }
  inlineToolHost.replaceChildren();
  if (!sessionId) {
    inlineToolHost.hidden = true;
    return;
  }
  const pinnedTools = sessionPinnedTools.get(sessionId) ?? [];
  let visibleToolCount = 0;
  for (const tool of pinnedTools) {
    const button = createToolButton(tool, false, getToolButtonRenderArgs());
    if (!button.hidden) {
      visibleToolCount += 1;
    }
    inlineToolHost.appendChild(button);
  }

  inlineToolHost.hidden = visibleToolCount === 0;
  syncVoiceInputAvailability();
}

function setAppServerControlQuickSettingsSheetOpen(open: boolean): void {
  appServerControlQuickSettingsSheetOpen = open;
  if (appServerControlSettingsSummaryBtn) {
    appServerControlSettingsSummaryBtn.setAttribute('aria-expanded', open ? 'true' : 'false');
  }
  if (appServerControlQuickSettingsRow) {
    appServerControlQuickSettingsRow.hidden = !open;
    if (
      appServerControlQuickSettingsRow.classList.contains(
        'smart-input-appServerControl-settings-sheet',
      )
    ) {
      footerStatusHost?.classList.toggle('adaptive-footer-status-sheet-open', open);
      if (
        open &&
        footerStatusHost &&
        !footerStatusHost.contains(appServerControlQuickSettingsRow)
      ) {
        footerStatusHost.appendChild(appServerControlQuickSettingsRow);
      }
    }
  }
  if (!footerResizeObserver) {
    queueFooterReserveSync();
  }
}

function closeFooterTransientUi(): boolean {
  let closedAny = false;
  if (toolsPanel && !toolsPanel.hidden) {
    setToolsPanelOpen(false);
    closedAny = true;
  }
  if (appServerControlQuickSettingsSheetOpen) {
    setAppServerControlQuickSettingsSheetOpen(false);
    closedAny = true;
  }
  return closedAny;
}

function clearSendAutoSendLongPressTimer(): void {
  if (sendAutoSendLongPressTimer !== null) {
    window.clearTimeout(sendAutoSendLongPressTimer);
    sendAutoSendLongPressTimer = null;
  }
}

function toggleAutoSendEnabled(): void {
  if (!canUseSmartInputVoiceSupport()) {
    return;
  }

  autoSendEnabled = !autoSendEnabled;
  localStorage.setItem('smartinput-autosend', String(autoSendEnabled));
  updateAutoSendVisibilitySupport({ dockedBar, sendBtn, autoSendEnabled });
  syncSmartInputVisibility();
}

function toggleAppServerControlPlanMode(sessionId: string): void {
  const draft = getAppServerControlQuickSettingsDraft(sessionId);
  setAppServerControlQuickSettingsDraft(sessionId, {
    planMode: draft.planMode === 'on' ? 'off' : 'on',
  });
  syncSmartInputVisibility();
}

function handleSmartInputShiftTabShortcut(event: KeyboardEvent): boolean {
  const sessionId = $activeSessionId.get();
  const shiftTabAction = resolveSmartInputShiftTabAction(
    event,
    sessionId ? getActiveTab(sessionId) : null,
  );
  if (shiftTabAction === 'toggle-appServerControl-plan-mode' && sessionId) {
    event.preventDefault();
    toggleAppServerControlPlanMode(sessionId);
    return true;
  }
  if (shiftTabAction === 'forward-to-terminal' && sessionId) {
    event.preventDefault();
    sendInput(sessionId, '\x1b[Z');
    return true;
  }

  return false;
}

function renderAppServerControlAttachmentDrafts(sessionId: string | null): void {
  renderAppServerControlAttachmentDraftView({
    attachments: sessionId ? getAppServerControlDraftAttachments(sessionId) : [],
    host: appServerControlAttachmentHost,
    onOpenAttachment: (currentSessionId, attachment) => {
      void openAppServerControlDraftAttachment(currentSessionId, attachment);
    },
    onFocusTextarea: () => {
      activeTextarea?.focus({ preventScroll: true });
    },
    onRemoveAttachment: removeAppServerControlComposerFile,
    sessionId,
  });
}

function removeAppServerControlComposerFile(sessionId: string, attachmentId: string): void {
  removeAttachmentsByIds(sessionId, [attachmentId]);
}

function finalizeInsertedComposerReferences(
  sessionId: string,
  selection: SmartInputComposerSelection,
  insertedReferenceIds: readonly string[],
): void {
  if (insertedReferenceIds.length === 0) {
    if ($activeSessionId.get() === sessionId) {
      renderSessionDraftIntoTextarea(sessionId, activeTextarea);
    }
    return;
  }

  const draft = getSessionDraft(sessionId);
  const removedReferenceIds = getSmartInputComposerReferenceIdsInSelection(
    draft,
    selection,
    (referenceId) => resolveComposerReference(sessionId, referenceId),
  );
  const insertResult = insertSmartInputComposerReferences(
    draft,
    selection,
    insertedReferenceIds,
    (referenceId) => resolveComposerReference(sessionId, referenceId),
  );
  updateSessionDraftAndTextarea(
    sessionId,
    insertResult.draft,
    activeTextarea,
    $activeSessionId.get() === sessionId ? insertResult.selection : null,
  );
  removeAttachmentsByIds(sessionId, removedReferenceIds);
}

async function addAppServerControlComposerFiles(
  sessionId: string,
  files: readonly File[],
  selection: SmartInputComposerSelection | null = null,
): Promise<void> {
  resetPromptHistoryNavigation(sessionId);
  const nextAttachments = [...getAppServerControlDraftAttachments(sessionId)];
  const nextSelection =
    selection ??
    (() => {
      const draftTextLength = getSessionDraftText(sessionId).length;
      return { start: draftTextLength, end: draftTextLength };
    })();
  const insertedReferenceIds: string[] = [];
  let errorMessage: string | null = null;

  for (const file of files) {
    if (
      isAppServerControlComposerImageFile(file) &&
      file.size > MAX_APP_SERVER_CONTROL_IMAGE_BYTES
    ) {
      errorMessage = `${t('smartInput.imageTooLarge')}: ${file.name}`;
      continue;
    }

    const uploadedPath = await uploadFile(sessionId, file);
    if (!uploadedPath) {
      errorMessage ??= `${t('smartInput.attachmentUploadFailed')}: ${file.name}`;
      continue;
    }

    const attachment = isAppServerControlComposerImageFile(file)
      ? createImageDraftAttachmentWithReference(sessionId, file, uploadedPath)
      : createAppServerControlComposerDraftAttachment(sessionId, file, uploadedPath);
    nextAttachments.push(attachment);
    if (attachment.referenceLabel) {
      insertedReferenceIds.push(attachment.id);
    }
  }

  setAppServerControlDraftAttachments(sessionId, nextAttachments);
  finalizeInsertedComposerReferences(sessionId, nextSelection, insertedReferenceIds);
  renderAppServerControlAttachmentDrafts($activeSessionId.get());

  if (errorMessage) {
    showDropToast(errorMessage);
  }

  if ($activeSessionId.get() === sessionId) {
    activeTextarea?.focus({ preventScroll: true });
  }
}

async function addAppServerControlComposerTextReference(
  sessionId: string,
  text: string,
  selection: SmartInputComposerSelection | null = null,
): Promise<void> {
  const nextSelection =
    selection ??
    (() => {
      const draftTextLength = getSessionDraftText(sessionId).length;
      return { start: draftTextLength, end: draftTextLength };
    })();
  const textFile = buildSmartInputTextReferenceFile(text);
  const uploadedPath = await uploadFile(sessionId, textFile);
  if (!uploadedPath) {
    showDropToast(`${t('smartInput.attachmentUploadFailed')}: ${textFile.name}`);
    return;
  }

  const nextAttachments = [...getAppServerControlDraftAttachments(sessionId)];
  const attachment = createTextDraftAttachmentWithReference(
    sessionId,
    textFile,
    uploadedPath,
    text,
  );
  nextAttachments.push(attachment);

  setAppServerControlDraftAttachments(sessionId, nextAttachments);
  finalizeInsertedComposerReferences(sessionId, nextSelection, [attachment.id]);
  renderAppServerControlAttachmentDrafts($activeSessionId.get());

  if ($activeSessionId.get() === sessionId) {
    activeTextarea?.focus({ preventScroll: true });
  }
}

function insertComposerTextAtSelection(
  sessionId: string,
  textarea: HTMLTextAreaElement | null,
  text: string,
): void {
  resetPromptHistoryNavigation(sessionId);
  const targetTextarea = textarea ?? activeTextarea;
  if (!targetTextarea) {
    setSessionDraftText(sessionId, `${getSessionDraftText(sessionId)}${text}`);
    return;
  }

  const draft = getSessionDraft(sessionId);
  const selection = getSmartInputComposerSelection(targetTextarea);
  const removedReferenceIds = getSmartInputComposerReferenceIdsInSelection(
    draft,
    selection,
    (referenceId) => resolveComposerReference(sessionId, referenceId),
  );
  const insertResult = insertSmartInputComposerText(draft, selection, text, (referenceId) =>
    resolveComposerReference(sessionId, referenceId),
  );
  updateSessionDraftAndTextarea(
    sessionId,
    insertResult.draft,
    targetTextarea,
    insertResult.selection,
  );
  removeAttachmentsByIds(sessionId, removedReferenceIds);
}

function deleteComposerRangeFromSelection(
  sessionId: string,
  textarea: HTMLTextAreaElement,
  direction: 'backward' | 'forward',
): void {
  resetPromptHistoryNavigation(sessionId);
  const deleteResult =
    direction === 'backward'
      ? deleteSmartInputComposerBackward(
          getSessionDraft(sessionId),
          getSmartInputComposerSelection(textarea),
          (referenceId) => resolveComposerReference(sessionId, referenceId),
        )
      : deleteSmartInputComposerForward(
          getSessionDraft(sessionId),
          getSmartInputComposerSelection(textarea),
          (referenceId) => resolveComposerReference(sessionId, referenceId),
        );

  updateSessionDraftAndTextarea(sessionId, deleteResult.draft, textarea, deleteResult.selection);
  removeAttachmentsByIds(sessionId, deleteResult.removedReferenceIds);
}

// eslint-disable-next-line complexity -- beforeinput normalization must branch on browser input types to keep inline references atomic inside the textarea.
function handleSmartInputBeforeInput(event: InputEvent, textarea: HTMLTextAreaElement): void {
  const sessionId = $activeSessionId.get();
  if (!sessionId || !draftHasInlineReferences(sessionId)) {
    return;
  }

  switch (event.inputType) {
    case 'deleteContentBackward':
    case 'deleteWordBackward':
    case 'deleteSoftLineBackward':
    case 'deleteHardLineBackward':
      event.preventDefault();
      deleteComposerRangeFromSelection(sessionId, textarea, 'backward');
      return;
    case 'deleteContentForward':
    case 'deleteWordForward':
    case 'deleteSoftLineForward':
    case 'deleteHardLineForward':
      event.preventDefault();
      deleteComposerRangeFromSelection(sessionId, textarea, 'forward');
      return;
    case 'insertText':
    case 'insertCompositionText':
    case 'insertReplacementText':
      event.preventDefault();
      insertComposerTextAtSelection(sessionId, textarea, event.data ?? '');
      return;
    case 'insertLineBreak':
    case 'insertParagraph':
      event.preventDefault();
      insertComposerTextAtSelection(sessionId, textarea, '\n');
      return;
    default:
      syncTextareaSelectionToComposerBoundaries(textarea);
  }
}

function handleSmartInputCut(event: ClipboardEvent, textarea: HTMLTextAreaElement): void {
  const sessionId = $activeSessionId.get();
  if (!sessionId || !draftHasInlineReferences(sessionId)) {
    return;
  }

  const draft = getSessionDraft(sessionId);
  const selection = normalizeSmartInputComposerSelection(
    draft,
    getSmartInputComposerSelection(textarea),
    (referenceId) => resolveComposerReference(sessionId, referenceId),
  );
  if (selection.start === selection.end) {
    return;
  }

  event.preventDefault();
  event.clipboardData?.setData(
    'text/plain',
    getSessionDraftText(sessionId).slice(selection.start, selection.end),
  );
  const deleteResult = deleteSmartInputComposerBackward(draft, selection, (referenceId) =>
    resolveComposerReference(sessionId, referenceId),
  );
  updateSessionDraftAndTextarea(sessionId, deleteResult.draft, textarea, deleteResult.selection);
  removeAttachmentsByIds(sessionId, deleteResult.removedReferenceIds);
}

async function handleSmartInputSelectedFiles(files: FileList): Promise<void> {
  const sessionId = $activeSessionId.get();
  if (!sessionId || files.length === 0) {
    return;
  }

  await addAppServerControlComposerFiles(
    sessionId,
    Array.from(files),
    activeTextarea ? getSmartInputComposerSelection(activeTextarea) : null,
  );
}

function clearSubmittedSmartInputState(sessionId: string, ta: HTMLTextAreaElement): void {
  resetPromptHistoryNavigation(sessionId);
  sessionDrafts.delete(sessionId);
  ta.value = '';
  clearAppServerControlDraftAttachments(sessionId);
  syncDraftForActiveSession();
  renderAppServerControlAttachmentDrafts($activeSessionId.get());
  ta.scrollTop = 0;
  resizeSmartInputTextarea(ta);
  if (!footerResizeObserver) {
    queueFooterReserveSync();
  }
  ta.focus();
}

async function sendTerminalComposerTurn(
  sessionId: string,
  ta: HTMLTextAreaElement,
  draft: SmartInputComposerDraft,
  attachments: readonly AppServerControlComposerDraftAttachment[],
): Promise<boolean> {
  const request = await prepareSmartInputTerminalTurn({
    sessionId,
    draft,
    attachments,
    bracketedPasteModeEnabled: isBracketedPasteEnabled(sessionId),
    uploadFailureMessage: t('smartInput.attachmentUploadFailed'),
    uploadFile,
  });
  if (!request.text && (request.terminalReplay?.length ?? 0) === 0) {
    return false;
  }

  await enqueueCommandBayTurn(sessionId, request);
  pushCurrentPromptToHistory(sessionId);
  clearSubmittedSmartInputState(sessionId, ta);
  collapseComposerAfterSuccessfulSend(sessionId);
  return true;
}

async function sendText(ta: HTMLTextAreaElement): Promise<void> {
  const sessionId = $activeSessionId.get();
  if (!sessionId) return;
  const draft = getSessionDraft(sessionId);
  const renderedText = getSmartInputComposerText(draft, (referenceId) =>
    resolveComposerReference(sessionId, referenceId),
  );

  const appServerControlAttachments = getAppServerControlDraftAttachments(sessionId);
  if (!renderedText && appServerControlAttachments.length === 0) {
    return;
  }

  if (!isAppServerControlActiveSession(sessionId)) {
    try {
      await sendTerminalComposerTurn(sessionId, ta, draft, appServerControlAttachments);
    } catch (error) {
      showDropToast(error instanceof Error && error.message.trim() ? error.message : String(error));
    }
    return;
  }

  if (appServerControlGoalComposeSessionId === sessionId) {
    if (appServerControlAttachments.length > 0) {
      showDropToast('Goals use text only.');
      return;
    }

    await submitAppServerControlGoal(sessionId, renderedText, ta);
    return;
  }

  const attachmentDrafts = detachAppServerControlDraftAttachments(sessionId);
  const draftSnapshot = cloneSmartInputComposerDraft(draft);
  renderAppServerControlAttachmentDrafts($activeSessionId.get());

  try {
    const { queuedTurn } = await submitAppServerControlComposerDraft({
      sessionId,
      draft: draftSnapshot,
      attachments: attachmentDrafts,
      uploadFailureMessage: t('smartInput.attachmentUploadFailed'),
      attachmentReadFailureMessage: t('smartInput.attachmentSendFailed'),
      uploadFile,
      createTurnRequest: createAppServerControlTurnRequest,
      submitQueuedTurn: enqueueCommandBayTurn,
    });

    await queuedTurn;
    pushCurrentPromptToHistory(sessionId);
    clearSubmittedSmartInputState(sessionId, ta);
    collapseComposerAfterSuccessfulSend(sessionId);
    releaseAppServerControlComposerDraftAttachmentPreviews(attachmentDrafts);
  } catch (error) {
    const shouldRestore =
      !getSessionDraftText(sessionId) &&
      getAppServerControlDraftAttachments(sessionId).length === 0;
    if (shouldRestore) {
      setAppServerControlDraftAttachments(sessionId, attachmentDrafts);
      setSessionDraft(sessionId, draftSnapshot);
    } else {
      releaseAppServerControlComposerDraftAttachmentPreviews(attachmentDrafts);
    }
    syncDraftForActiveSession();
    renderAppServerControlAttachmentDrafts($activeSessionId.get());
    showDropToast(
      error instanceof Error && error.message.trim()
        ? error.message
        : t('smartInput.attachmentSendFailed'),
    );
  }
}

function persistDraftForSession(sessionId: string | null, draftOverride?: string): void {
  if (!sessionId) {
    return;
  }

  if (typeof draftOverride === 'string') {
    setSessionDraftText(sessionId, draftOverride);
    return;
  }

  if (
    !activeTextarea ||
    $activeSessionId.get() !== sessionId ||
    draftHasInlineReferences(sessionId)
  ) {
    return;
  }

  setSessionDraftText(sessionId, activeTextarea.value);
}

function applyDraftToTextarea(
  textarea: HTMLTextAreaElement | null,
  sessionId: string | null,
): void {
  renderSessionDraftIntoTextarea(sessionId, textarea);
}
function syncDraftForActiveSession(): void {
  const sessionId = $activeSessionId.get();
  applyDraftToTextarea(activeTextarea, sessionId);
  renderAppServerControlAttachmentDrafts(sessionId);
  syncAppServerControlQuickSettingsControls();
}

export function removeSmartInputSessionState(sessionId: string): void {
  sessionDrafts.delete(sessionId);
  clearAppServerControlDraftAttachments(sessionId);
  sessionPromptHistories.delete(sessionId);
  resetPromptHistoryNavigation(sessionId);
  sessionPinnedTools.delete(sessionId);
  sessionComposerExpanded.delete(sessionId);
  if ($activeSessionId.get() === sessionId) {
    syncDraftForActiveSession();
    renderPinnedToolsForSession(sessionId);
  }
}
function beginRecording(pinOnUse: boolean = false): void {
  if (!canUseSmartInputVoiceSupport()) return;
  if (isRecording) return;
  isRecording = true;
  pendingMicPinSessionId = pinOnUse ? ($activeSessionId.get() ?? null) : null;
  getMicButtonsSupport(footerDock).forEach((button) => {
    button.classList.add('recording');
  });

  const ta = activeTextarea;
  startHistoryion(
    (delta) => {
      const sessionId = $activeSessionId.get();
      if (ta && sessionId && !autoSendEnabled) {
        insertComposerTextAtSelection(sessionId, ta, delta);
      }
    },
    (completed) => {
      if (!completed) return;
      if (autoSendEnabled) {
        sendDirectly(completed);
      } else if (ta) {
        const sessionId = $activeSessionId.get();
        if (!sessionId) {
          return;
        }

        resetPromptHistoryNavigation(sessionId);
        setSessionDraftText(sessionId, completed);
        renderSessionDraftIntoTextarea(sessionId, ta);
      }
    },
  );
}

function sendDirectly(text: string): void {
  const sessionId = $activeSessionId.get();
  if (!sessionId) return;
  void submitSessionText(sessionId, text);
}

function endRecording(): void {
  if (!isRecording) return;
  isRecording = false;
  getMicButtonsSupport(footerDock).forEach((button) => {
    button.classList.remove('recording');
  });
  const sessionIdToPin = pendingMicPinSessionId;
  pendingMicPinSessionId = null;
  if (sessionIdToPin) {
    pinToolForSession(sessionIdToPin, 'mic');
  }
  void stopHistoryion();
}

function syncVoiceInputAvailability(): void {
  syncVoiceInputAvailabilitySupport({
    footerDock,
    dockedBar,
    sendBtn,
    autoSendEnabled,
    isRecording,
    endRecording,
  });
}

/* eslint-enable max-lines */

function pinToolForSession(sessionId: string, tool: ToolKind): void {
  const currentTools = sessionPinnedTools.get(sessionId) ?? [];
  if (!currentTools.includes(tool)) {
    sessionPinnedTools.set(sessionId, [...currentTools, tool]);
    renderPinnedToolsForSession(sessionId);
  }
}

function syncAppServerControlQuickSettingsControls(): void {
  syncAppServerControlQuickSettingsControlsSupport({
    appServerControlQuickSettingsRow,
    appServerControlQuickSettingsActions,
    appServerControlModelSelect,
    appServerControlEffortSelect,
    appServerControlPlanSelect,
    appServerControlPermissionSelect,
    appServerControlSettingsSummaryBtn,
    dockedBar,
    getVisibilityState: getSmartInputVisibilityState,
    setAppServerControlQuickSettingsSheetOpen,
  });
}

function queueFooterReserveSync(): void {
  queueFooterReserveSyncSupport({
    footerResizeQueued,
    setFooterResizeQueued,
    updateFooterReservedHeight: () => {
      updateFooterReservedHeight();
    },
  });
}

function updateFooterReservedHeight(): void {
  updateFooterReservedHeightSupport({
    footerDock,
    activeTextarea,
    composerExpanded: isComposerExpanded($activeSessionId.get()),
    lastReservedFooterHeightPx,
    setLastReservedFooterHeightPx,
  });
}

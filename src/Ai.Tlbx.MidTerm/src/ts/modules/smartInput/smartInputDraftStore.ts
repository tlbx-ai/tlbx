import { draftFromRichDocument, getRichComposerDocument, parsePersistedRichDocument } from './richComposerDocument';
import type { AppServerControlQuickSettingsSummary } from '../../api/types';
import type { AppServerControlComposerDraftAttachment } from './appServerControlAttachments';
import {
  cloneAppServerControlComposerDraftAttachments,
  hydrateAppServerControlComposerDraftAttachment,
  releaseAppServerControlComposerDraftAttachmentPreviews,
  toPersistedAppServerControlComposerDraftAttachment,
  type PersistedAppServerControlComposerDraftAttachment,
} from './appServerControlAttachments';
import {
  cloneSmartInputComposerDraft,
  type SmartInputComposerDraft,
  type SmartInputComposerPart,
  type SmartInputComposerReferenceKind,
} from './smartInputComposerDraft';

const APP_SERVER_CONTROL_ATTACHMENT_STORAGE_KEY_PREFIX = 'smartinput-appServerControl-attachments:';
const PROMPT_HISTORY_STORAGE_KEY_PREFIX = 'smartinput-prompt-history:';
export const MAX_SMART_INPUT_PROMPT_HISTORY_ENTRIES = 5;

interface PersistedPromptHistoryQuickSettings {
  effort: string | null;
  model: string | null;
  permissionMode: string;
  planMode: string;
  fastMode: string;
}

interface PersistedSmartInputPromptHistoryEntry {
  attachments: PersistedAppServerControlComposerDraftAttachment[];
  composerDraft: SmartInputComposerDraft;
  quickSettings: PersistedPromptHistoryQuickSettings | null;
}

export interface SmartInputPromptHistoryEntry {
  attachments: AppServerControlComposerDraftAttachment[];
  composerDraft: SmartInputComposerDraft;
  quickSettings: AppServerControlQuickSettingsSummary | null;
}

function getAppServerControlAttachmentStorageKey(sessionId: string): string {
  return `${APP_SERVER_CONTROL_ATTACHMENT_STORAGE_KEY_PREFIX}${sessionId}`;
}

function getPromptHistoryStorageKey(sessionId: string): string {
  return `${PROMPT_HISTORY_STORAGE_KEY_PREFIX}${sessionId}`;
}

function clonePromptHistoryQuickSettings(
  quickSettings: AppServerControlQuickSettingsSummary | null,
): AppServerControlQuickSettingsSummary | null {
  if (!quickSettings) {
    return null;
  }

  return {
    model: typeof quickSettings.model === 'string' ? quickSettings.model : null,
    effort: typeof quickSettings.effort === 'string' ? quickSettings.effort : null,
    planMode: typeof quickSettings.planMode === 'string' ? quickSettings.planMode : 'off',
    permissionMode:
      typeof quickSettings.permissionMode === 'string' ? quickSettings.permissionMode : 'manual',
    fastMode: typeof quickSettings.fastMode === 'string' ? quickSettings.fastMode : 'off',
  };
}

export function cloneSmartInputPromptHistoryEntry(
  entry: SmartInputPromptHistoryEntry,
): SmartInputPromptHistoryEntry {
  return {
    composerDraft: cloneSmartInputComposerDraft(entry.composerDraft),
    attachments: cloneAppServerControlComposerDraftAttachments(entry.attachments),
    quickSettings: clonePromptHistoryQuickSettings(entry.quickSettings),
  };
}

function cloneSmartInputPromptHistoryEntries(
  entries: readonly SmartInputPromptHistoryEntry[],
): SmartInputPromptHistoryEntry[] {
  return entries.map((entry) => cloneSmartInputPromptHistoryEntry(entry));
}

function toPersistedPromptHistoryQuickSettings(
  quickSettings: AppServerControlQuickSettingsSummary | null,
): PersistedPromptHistoryQuickSettings | null {
  if (!quickSettings) {
    return null;
  }

  return {
    model: typeof quickSettings.model === 'string' ? quickSettings.model : null,
    effort: typeof quickSettings.effort === 'string' ? quickSettings.effort : null,
    planMode: typeof quickSettings.planMode === 'string' ? quickSettings.planMode : 'off',
    permissionMode:
      typeof quickSettings.permissionMode === 'string' ? quickSettings.permissionMode : 'manual',
    fastMode: typeof quickSettings.fastMode === 'string' ? quickSettings.fastMode : 'off',
  };
}

function persistAppServerControlDraftAttachmentsForSession(
  sessionId: string,
  attachments: readonly AppServerControlComposerDraftAttachment[],
): void {
  if (typeof localStorage === 'undefined') {
    return;
  }

  const persisted = attachments
    .map((attachment) => toPersistedAppServerControlComposerDraftAttachment(attachment))
    .filter(
      (attachment): attachment is PersistedAppServerControlComposerDraftAttachment =>
        attachment !== null,
    );

  if (persisted.length === 0) {
    localStorage.removeItem(getAppServerControlAttachmentStorageKey(sessionId));
    return;
  }

  localStorage.setItem(
    getAppServerControlAttachmentStorageKey(sessionId),
    JSON.stringify(persisted),
  );
}

function clearPersistedAppServerControlDraftAttachmentsForSession(sessionId: string): void {
  if (typeof localStorage === 'undefined') {
    return;
  }

  localStorage.removeItem(getAppServerControlAttachmentStorageKey(sessionId));
}

function persistPromptHistoryForSession(
  sessionId: string,
  entries: readonly SmartInputPromptHistoryEntry[],
): void {
  if (typeof localStorage === 'undefined') {
    return;
  }

  if (entries.length === 0) {
    localStorage.removeItem(getPromptHistoryStorageKey(sessionId));
    return;
  }

  const persisted: PersistedSmartInputPromptHistoryEntry[] = entries.map((entry) => ({
    composerDraft: cloneSmartInputComposerDraft(entry.composerDraft),
    attachments: entry.attachments
      .map((attachment) => toPersistedAppServerControlComposerDraftAttachment(attachment))
      .filter(
        (attachment): attachment is PersistedAppServerControlComposerDraftAttachment =>
          attachment !== null,
      ),
    quickSettings: toPersistedPromptHistoryQuickSettings(entry.quickSettings),
  }));

  localStorage.setItem(getPromptHistoryStorageKey(sessionId), JSON.stringify(persisted));
}

function clearPersistedPromptHistoryForSession(sessionId: string): void {
  if (typeof localStorage === 'undefined') {
    return;
  }

  localStorage.removeItem(getPromptHistoryStorageKey(sessionId));
}

// eslint-disable-next-line complexity -- persisted attachment hydration validates several optional reference metadata fields while keeping the storage schema explicit.
function tryParsePersistedAppServerControlDraftAttachment(
  value: unknown,
): PersistedAppServerControlComposerDraftAttachment | null {
  if (!value || typeof value !== 'object') {
    return null;
  }

  const attachment = value as Record<string, unknown>;
  const kind = attachment.kind;
  if (kind !== 'image' && kind !== 'file') {
    return null;
  }

  const uploadedPath = attachment.uploadedPath;
  const displayName = attachment.displayName;
  const sizeBytes = attachment.sizeBytes;
  if (
    typeof attachment.id !== 'string' ||
    typeof uploadedPath !== 'string' ||
    typeof displayName !== 'string' ||
    typeof sizeBytes !== 'number'
  ) {
    return null;
  }

  return {
    id: attachment.id,
    kind,
    uploadedPath,
    displayName,
    mimeType: typeof attachment.mimeType === 'string' ? attachment.mimeType : null,
    referenceKind:
      attachment.referenceKind === 'image' ||
      attachment.referenceKind === 'file' ||
      attachment.referenceKind === 'text'
        ? attachment.referenceKind
        : null,
    referenceLineCount:
      typeof attachment.referenceLineCount === 'number' ? attachment.referenceLineCount : null,
    referenceCharCount:
      typeof attachment.referenceCharCount === 'number' ? attachment.referenceCharCount : null,
    referenceLabel:
      typeof attachment.referenceLabel === 'string' ? attachment.referenceLabel : null,
    referenceOrdinal:
      typeof attachment.referenceOrdinal === 'number' ? attachment.referenceOrdinal : null,
    sizeBytes,
  };
}

function tryParseComposerPart(value: unknown): SmartInputComposerPart | null {
  if (!value || typeof value !== 'object') {
    return null;
  }

  const part = value as Record<string, unknown>;
  if (part.kind === 'text' && typeof part.text === 'string') {
    return {
      kind: 'text',
      text: part.text,
    };
  }

  if (part.kind === 'reference' && typeof part.referenceId === 'string') {
    return {
      kind: 'reference',
      referenceId: part.referenceId,
    };
  }

  return null;
}

function tryParseComposerDraft(value: unknown): SmartInputComposerDraft | null {
  if (!value || typeof value !== 'object') {
    return null;
  }

  const draft = value as Record<string, unknown>;
  if (!Array.isArray(draft.parts)) {
    return null;
  }

  const parts: SmartInputComposerPart[] = [];
  for (const valuePart of draft.parts) {
    const parsedPart = tryParseComposerPart(valuePart);
    if (!parsedPart) {
      return null;
    }

    parts.push(parsedPart);
  }

  const nextOrdinalByKind: Partial<Record<SmartInputComposerReferenceKind, number>> = {};
  const ordinalSource =
    draft.nextOrdinalByKind && typeof draft.nextOrdinalByKind === 'object'
      ? (draft.nextOrdinalByKind as Record<string, unknown>)
      : {};
  for (const kind of ['image', 'file', 'text'] as const) {
    const rawOrdinal = ordinalSource[kind];
    if (
      typeof rawOrdinal === 'number' &&
      Number.isFinite(rawOrdinal) &&
      rawOrdinal >= 1 &&
      Number.isInteger(rawOrdinal)
    ) {
      nextOrdinalByKind[kind] = rawOrdinal;
    }
  }

  const richDoc = parsePersistedRichDocument(draft.richDoc);
  const result: SmartInputComposerDraft = { nextOrdinalByKind, parts, ...(richDoc ? { richDoc } : {}) };
  return richDoc ? draftFromRichDocument(result, getRichComposerDocument(result), richDoc.selection) : result;
}

function tryParsePromptHistoryQuickSettings(
  value: unknown,
): AppServerControlQuickSettingsSummary | null {
  if (value === null) {
    return null;
  }

  if (!value || typeof value !== 'object') {
    return null;
  }

  const quickSettings = value as Record<string, unknown>;
  return {
    model: typeof quickSettings.model === 'string' ? quickSettings.model : null,
    effort: typeof quickSettings.effort === 'string' ? quickSettings.effort : null,
    planMode: typeof quickSettings.planMode === 'string' ? quickSettings.planMode : 'off',
    permissionMode:
      typeof quickSettings.permissionMode === 'string' ? quickSettings.permissionMode : 'manual',
    fastMode: typeof quickSettings.fastMode === 'string' ? quickSettings.fastMode : 'off',
  };
}

function tryParsePromptHistoryEntry(
  sessionId: string,
  value: unknown,
): SmartInputPromptHistoryEntry | null {
  if (!value || typeof value !== 'object') {
    return null;
  }

  const entry = value as Record<string, unknown>;
  if (!Array.isArray(entry.attachments)) {
    return null;
  }

  const composerDraft = tryParseComposerDraft(entry.composerDraft);
  if (!composerDraft) {
    return null;
  }

  const attachments = entry.attachments
    .map((attachment) => tryParsePersistedAppServerControlDraftAttachment(attachment))
    .filter(
      (attachment): attachment is PersistedAppServerControlComposerDraftAttachment =>
        attachment !== null,
    )
    .map((attachment) => hydrateAppServerControlComposerDraftAttachment(sessionId, attachment));
  const quickSettings = tryParsePromptHistoryQuickSettings(entry.quickSettings ?? null);
  if (entry.quickSettings !== null && entry.quickSettings !== undefined && !quickSettings) {
    return null;
  }

  return {
    composerDraft,
    attachments,
    quickSettings,
  };
}

export function loadAppServerControlDraftAttachmentsForSession(
  sessionId: string,
): AppServerControlComposerDraftAttachment[] {
  if (typeof localStorage === 'undefined') {
    return [];
  }

  const raw = localStorage.getItem(getAppServerControlAttachmentStorageKey(sessionId));
  if (!raw) {
    return [];
  }

  try {
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) {
      return [];
    }

    return parsed
      .map((value) => tryParsePersistedAppServerControlDraftAttachment(value))
      .filter(
        (attachment): attachment is PersistedAppServerControlComposerDraftAttachment =>
          attachment !== null,
      )
      .map((attachment) => hydrateAppServerControlComposerDraftAttachment(sessionId, attachment));
  } catch {
    return [];
  }
}

export function loadSmartInputPromptHistoryForSession(
  sessionId: string,
): SmartInputPromptHistoryEntry[] {
  if (typeof localStorage === 'undefined') {
    return [];
  }

  const raw = localStorage.getItem(getPromptHistoryStorageKey(sessionId));
  if (!raw) {
    return [];
  }

  try {
    const parsed: unknown = JSON.parse(raw);
    if (!Array.isArray(parsed)) {
      return [];
    }

    return parsed
      .map((value) => tryParsePromptHistoryEntry(sessionId, value))
      .filter((entry): entry is SmartInputPromptHistoryEntry => entry !== null);
  } catch {
    return [];
  }
}

export function getAppServerControlDraftAttachmentsForSession(
  drafts: ReadonlyMap<string, AppServerControlComposerDraftAttachment[]>,
  sessionId: string | null,
): AppServerControlComposerDraftAttachment[] {
  return sessionId ? (drafts.get(sessionId) ?? []) : [];
}

export function setAppServerControlDraftAttachmentsForSession(
  drafts: Map<string, AppServerControlComposerDraftAttachment[]>,
  sessionId: string,
  attachments: readonly AppServerControlComposerDraftAttachment[],
): void {
  if (attachments.length === 0) {
    drafts.delete(sessionId);
    clearPersistedAppServerControlDraftAttachmentsForSession(sessionId);
    return;
  }

  drafts.set(sessionId, [...attachments]);
  persistAppServerControlDraftAttachmentsForSession(sessionId, attachments);
}

export function clearAppServerControlDraftAttachmentsForSession(
  drafts: Map<string, AppServerControlComposerDraftAttachment[]>,
  sessionId: string,
  revokePreviews: boolean = true,
): void {
  const attachments = drafts.get(sessionId);
  drafts.delete(sessionId);
  clearPersistedAppServerControlDraftAttachmentsForSession(sessionId);
  if (!attachments) {
    return;
  }
  if (revokePreviews) {
    releaseAppServerControlComposerDraftAttachmentPreviews(attachments);
  }
}

export function detachAppServerControlDraftAttachmentsForSession(
  drafts: Map<string, AppServerControlComposerDraftAttachment[]>,
  sessionId: string,
): AppServerControlComposerDraftAttachment[] {
  const attachments = getAppServerControlDraftAttachmentsForSession(drafts, sessionId);
  drafts.delete(sessionId);
  clearPersistedAppServerControlDraftAttachmentsForSession(sessionId);
  return cloneAppServerControlComposerDraftAttachments(attachments);
}

export function getSmartInputPromptHistoryForSession(
  histories: ReadonlyMap<string, SmartInputPromptHistoryEntry[]>,
  sessionId: string | null,
): SmartInputPromptHistoryEntry[] {
  if (!sessionId) {
    return [];
  }

  return cloneSmartInputPromptHistoryEntries(histories.get(sessionId) ?? []);
}

export function setSmartInputPromptHistoryForSession(
  histories: Map<string, SmartInputPromptHistoryEntry[]>,
  sessionId: string,
  entries: readonly SmartInputPromptHistoryEntry[],
): void {
  if (entries.length === 0) {
    histories.delete(sessionId);
    clearPersistedPromptHistoryForSession(sessionId);
    return;
  }

  const normalizedEntries = cloneSmartInputPromptHistoryEntries(
    entries.slice(0, MAX_SMART_INPUT_PROMPT_HISTORY_ENTRIES),
  );
  histories.set(sessionId, normalizedEntries);
  persistPromptHistoryForSession(sessionId, normalizedEntries);
}

export function pushSmartInputPromptHistoryEntryForSession(
  histories: Map<string, SmartInputPromptHistoryEntry[]>,
  sessionId: string,
  entry: SmartInputPromptHistoryEntry,
): void {
  const existingEntries =
    histories.get(sessionId) ?? loadSmartInputPromptHistoryForSession(sessionId);
  setSmartInputPromptHistoryForSession(histories, sessionId, [
    cloneSmartInputPromptHistoryEntry(entry),
    ...existingEntries,
  ]);
}

export function clearSmartInputPromptHistoryForSession(
  histories: Map<string, SmartInputPromptHistoryEntry[]>,
  sessionId: string,
): void {
  const entries = histories.get(sessionId);
  histories.delete(sessionId);
  clearPersistedPromptHistoryForSession(sessionId);
  if (!entries) {
    return;
  }

  for (const entry of entries) {
    releaseAppServerControlComposerDraftAttachmentPreviews(entry.attachments);
  }
}

export function persistSessionDraft(
  drafts: Map<string, string>,
  sessionId: string | null,
  draft: string,
): void {
  if (!sessionId) {
    return;
  }

  if (draft) {
    drafts.set(sessionId, draft);
    return;
  }

  drafts.delete(sessionId);
}

export function applySessionDraftToTextarea(
  drafts: ReadonlyMap<string, string>,
  textarea: HTMLTextAreaElement | null,
  sessionId: string | null,
  resizeTextarea: (textarea: HTMLTextAreaElement) => void,
): void {
  if (!textarea) {
    return;
  }

  const nextValue = sessionId ? (drafts.get(sessionId) ?? '') : '';
  if (textarea.value !== nextValue) {
    textarea.value = nextValue;
  }
  textarea.scrollTop = 0;
  resizeTextarea(textarea);
}

export function loadSmartInputCurrentDraft(sessionId: string): SmartInputComposerDraft | null {
  try {
    return tryParseComposerDraft(JSON.parse(localStorage.getItem(`smartinput-composer:${sessionId}`) ?? 'null') as unknown);
  } catch { return null; }
}

export function persistSmartInputCurrentDraft(sessionId: string, draft: SmartInputComposerDraft | null): void {
  try {
    if (draft) localStorage.setItem(`smartinput-composer:${sessionId}`, JSON.stringify(draft));
    else localStorage.removeItem(`smartinput-composer:${sessionId}`);
  } catch { /* Keep the in-memory draft usable if browser storage is unavailable. */ }
}

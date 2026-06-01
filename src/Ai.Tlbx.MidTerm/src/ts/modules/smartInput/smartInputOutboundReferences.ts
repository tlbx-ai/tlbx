import type {
  AppServerControlAttachmentReference,
  AppServerControlTerminalReplayStep,
  AppServerControlTurnRequest,
} from '../../api/types';
import type { AppServerControlComposerDraftAttachment } from './appServerControlAttachments';
import {
  buildAppServerControlComposerAttachmentFileUrl,
  toAppServerControlAttachmentReference,
} from './appServerControlAttachments';
import type { SmartInputComposerDraft, SmartInputComposerPart } from './smartInputComposerDraft';

export interface PrepareSmartInputOutboundPromptArgs {
  sessionId: string;
  draft: SmartInputComposerDraft;
  attachments: readonly AppServerControlComposerDraftAttachment[];
  uploadFailureMessage: string;
  attachmentReadFailureMessage: string;
  uploadFile: (sessionId: string, file: File) => Promise<string | null>;
}

export interface PreparedSmartInputOutboundPrompt {
  attachments: AppServerControlAttachmentReference[];
  text: string;
}

export interface PrepareSmartInputTerminalTurnArgs {
  sessionId: string;
  draft: SmartInputComposerDraft;
  attachments: readonly AppServerControlComposerDraftAttachment[];
  bracketedPasteModeEnabled: boolean;
  uploadFailureMessage: string;
  uploadFile: (sessionId: string, file: File) => Promise<string | null>;
}

export async function prepareSmartInputOutboundPrompt(
  args: PrepareSmartInputOutboundPromptArgs,
): Promise<PreparedSmartInputOutboundPrompt> {
  const uploadedPaths = await Promise.all(
    args.attachments.map((attachment) => ensureUploadedAttachmentPath(args, attachment)),
  );
  const attachmentPathById = new Map<string, string>();
  args.attachments.forEach((attachment, index) => {
    attachmentPathById.set(attachment.id, uploadedPaths[index] ?? '');
  });

  const attachments = args.attachments
    .filter((attachment) => attachment.referenceKind !== 'text')
    .map((attachment, index) =>
      toAppServerControlAttachmentReference(attachment, uploadedPaths[index] ?? ''),
    );

  const referencedTextBlocks: string[] = [];
  const seenExtraReferenceIds = new Set<string>();

  const textParts = await Promise.all(
    args.draft.parts.map((part) =>
      resolveOutboundPartText(
        args,
        part,
        attachmentPathById,
        seenExtraReferenceIds,
        referencedTextBlocks,
      ),
    ),
  );

  const sections = [joinPromptParts(textParts), referencedTextBlocks.join('\n\n')].filter(
    (section) => section.length > 0,
  );

  return {
    attachments,
    text: sections.join('\n\n'),
  };
}

export async function prepareSmartInputTerminalTurn(
  args: PrepareSmartInputTerminalTurnArgs,
): Promise<AppServerControlTurnRequest> {
  const uploadedPaths = await Promise.all(
    args.attachments.map((attachment) => ensureUploadedAttachmentPath(args, attachment)),
  );
  const attachmentById = new Map<string, AppServerControlComposerDraftAttachment>();
  const attachmentPathById = new Map<string, string>();
  args.attachments.forEach((attachment, index) => {
    attachmentById.set(attachment.id, attachment);
    attachmentPathById.set(attachment.id, uploadedPaths[index] ?? '');
  });

  const referencedAttachmentIds = new Set<string>();
  const replay: AppServerControlTerminalReplayStep[] = [];
  const previewParts: string[] = [];

  for (const part of args.draft.parts) {
    if (part.kind === 'text') {
      previewParts.push(part.text);
      appendTerminalReplayText(replay, part.text);
      continue;
    }

    const attachment = attachmentById.get(part.referenceId);
    if (!attachment) {
      continue;
    }

    const path = attachmentPathById.get(attachment.id) ?? '';
    if (!path) {
      continue;
    }

    referencedAttachmentIds.add(attachment.id);
    previewParts.push(getAttachmentReferenceToken(attachment));
    appendTerminalReplayAttachment(replay, attachment, path);
  }

  let needsExtraSeparator = replay.length > 0;
  for (const attachment of args.attachments) {
    if (referencedAttachmentIds.has(attachment.id)) {
      continue;
    }

    const path = attachmentPathById.get(attachment.id) ?? '';
    if (!path) {
      continue;
    }

    if (needsExtraSeparator) {
      previewParts.push('\n\n');
      appendTerminalReplayText(replay, '\n\n');
    }

    previewParts.push(getAttachmentReferenceToken(attachment));
    appendTerminalReplayAttachment(replay, attachment, path);
    needsExtraSeparator = true;
  }

  return {
    text: joinPromptParts(previewParts),
    attachments: [],
    terminalReplay: replay,
  };
}

async function ensureUploadedAttachmentPath(
  args: PrepareSmartInputOutboundPromptArgs | PrepareSmartInputTerminalTurnArgs,
  attachment: AppServerControlComposerDraftAttachment,
): Promise<string> {
  if (attachment.uploadedPath) {
    return attachment.uploadedPath;
  }

  if (!attachment.file) {
    throw new Error(args.uploadFailureMessage);
  }

  const path = await args.uploadFile(args.sessionId, attachment.file);
  if (!path) {
    throw new Error(args.uploadFailureMessage);
  }

  return path;
}

async function resolveOutboundPartText(
  args: PrepareSmartInputOutboundPromptArgs,
  part: SmartInputComposerPart,
  attachmentPathById: ReadonlyMap<string, string>,
  seenExtraReferenceIds: Set<string>,
  referencedTextBlocks: string[],
): Promise<string> {
  if (part.kind === 'text') {
    return part.text;
  }

  const attachment = args.attachments.find((candidate) => candidate.id === part.referenceId);
  if (!attachment) {
    return '';
  }

  const token = getAttachmentReferenceToken(attachment);
  const path = attachmentPathById.get(attachment.id) ?? '';

  if (!seenExtraReferenceIds.has(attachment.id) && attachment.referenceKind === 'text') {
    referencedTextBlocks.push(
      buildTextReferenceBlock(token, await loadAttachmentTextContent(args, attachment, path)),
    );
    seenExtraReferenceIds.add(attachment.id);
  }

  return token;
}

function joinPromptParts(parts: readonly string[]): string {
  return parts.join('');
}

function buildTextReferenceBlock(token: string, text: string): string {
  return `${token}\n${text}`;
}

function getAttachmentReferenceToken(attachment: AppServerControlComposerDraftAttachment): string {
  const label = attachment.referenceLabel?.trim() || attachment.displayName.trim() || 'Attachment';
  return `[${label}]`;
}

function appendTerminalReplayText(
  replay: AppServerControlTerminalReplayStep[],
  text: string,
): void {
  if (!text) {
    return;
  }

  const last = replay.length > 0 ? replay[replay.length - 1] : undefined;
  if (last?.kind === 'text') {
    last.text = `${last.text ?? ''}${text}`;
    return;
  }

  replay.push({
    kind: 'text',
    text,
  });
}

function appendTerminalReplayAttachment(
  replay: AppServerControlTerminalReplayStep[],
  attachment: AppServerControlComposerDraftAttachment,
  path: string,
): void {
  if (attachment.referenceKind === 'text') {
    replay.push({
      kind: 'textFile',
      path,
      useBracketedPaste: true,
    });
    return;
  }

  if (attachment.referenceKind === 'image' || attachment.kind === 'image') {
    replay.push({
      kind: 'image',
      path,
      mimeType: attachment.mimeType,
    });
    return;
  }

  replay.push({
    kind: 'filePath',
    path,
  });
}

async function loadAttachmentTextContent(
  args: PrepareSmartInputOutboundPromptArgs,
  attachment: AppServerControlComposerDraftAttachment,
  path: string,
): Promise<string> {
  if (attachment.file) {
    return normalizeTextReferenceContent(await attachment.file.text());
  }

  if (!path) {
    throw new Error(args.attachmentReadFailureMessage);
  }

  const response = await fetch(
    buildAppServerControlComposerAttachmentFileUrl(args.sessionId, path),
  );
  if (!response.ok) {
    throw new Error(args.attachmentReadFailureMessage);
  }

  return normalizeTextReferenceContent(await response.text());
}

function normalizeTextReferenceContent(text: string): string {
  return text.replace(/\r\n/g, '\n').replace(/\r/g, '\n');
}

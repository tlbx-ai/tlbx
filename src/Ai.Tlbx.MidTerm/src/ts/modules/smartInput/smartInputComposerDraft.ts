import { draftFromRichDocument, getRichComposerDocument, pruneRichComposerDraft, type SmartInputRichDocument } from './richComposerDocument';
export type SmartInputComposerReferenceKind = 'image' | 'file' | 'text';

export interface SmartInputComposerSelection {
  end: number;
  start: number;
}

export interface SmartInputComposerResolvedReference {
  kind: SmartInputComposerReferenceKind;
  label: string;
  referenceId: string;
  tokenText: string;
}

export interface SmartInputComposerTextPart {
  kind: 'text';
  text: string;
}

export interface SmartInputComposerReferencePart {
  kind: 'reference';
  referenceId: string;
}

export type SmartInputComposerPart = SmartInputComposerReferencePart | SmartInputComposerTextPart;

export interface SmartInputComposerDraft {
  richDoc?: SmartInputRichDocument;
  nextOrdinalByKind: Partial<Record<SmartInputComposerReferenceKind, number>>;
  parts: SmartInputComposerPart[];
}

type SelectionAffinity = 'after' | 'before' | 'nearest';

interface RenderedComposerSegment {
  end: number;
  kind: SmartInputComposerPart['kind'];
  partIndex: number;
  referenceId: string | null;
  start: number;
  text: string;
}

export function createSmartInputComposerDraft(text: string = ''): SmartInputComposerDraft {
  return {
    nextOrdinalByKind: {},
    parts: text ? [{ kind: 'text', text }] : [],
  };
}

export function cloneSmartInputComposerDraft(
  draft: SmartInputComposerDraft,
): SmartInputComposerDraft {
  if (draft.richDoc) return draftFromRichDocument(draft, getRichComposerDocument(draft), draft.richDoc.selection);
  return {
    nextOrdinalByKind: { ...draft.nextOrdinalByKind },
    parts: draft.parts.map((part) => ({ ...part })),
  };
}

export function getSmartInputComposerText(
  draft: SmartInputComposerDraft,
  resolveReference: (referenceId: string) => SmartInputComposerResolvedReference | null,
): string {
  return draft.parts
    .map((part) => {
      if (part.kind === 'text') {
        return part.text;
      }

      return resolveReference(part.referenceId)?.tokenText ?? '';
    })
    .join('');
}

export function hasSmartInputComposerReferences(draft: SmartInputComposerDraft): boolean {
  return draft.parts.some((part) => part.kind === 'reference');
}

export function isSmartInputComposerEmpty(draft: SmartInputComposerDraft): boolean {
  return (
    draft.parts.length === 0 || draft.parts.every((part) => part.kind === 'text' && !part.text)
  );
}

export function getNextSmartInputComposerReferenceOrdinal(
  draft: SmartInputComposerDraft,
  kind: SmartInputComposerReferenceKind,
): number {
  return Math.max(1, draft.nextOrdinalByKind[kind] ?? 1);
}

export function allocateSmartInputComposerReferenceOrdinal(
  draft: SmartInputComposerDraft,
  kind: SmartInputComposerReferenceKind,
): number {
  const nextOrdinal = getNextSmartInputComposerReferenceOrdinal(draft, kind);
  draft.nextOrdinalByKind[kind] = nextOrdinal + 1;
  return nextOrdinal;
}

export function replaceSmartInputComposerText(
  draft: SmartInputComposerDraft,
  text: string,
): SmartInputComposerDraft {
  return {
    nextOrdinalByKind: { ...draft.nextOrdinalByKind },
    parts: text ? [{ kind: 'text', text }] : [],
  };
}

export function insertSmartInputComposerText(
  draft: SmartInputComposerDraft,
  selection: SmartInputComposerSelection,
  text: string,
  resolveReference: (referenceId: string) => SmartInputComposerResolvedReference | null,
): { draft: SmartInputComposerDraft; selection: SmartInputComposerSelection } {
  return replaceSmartInputComposerRange(
    draft,
    selection,
    [{ kind: 'text', text }],
    resolveReference,
  );
}

export function insertSmartInputComposerReference(
  draft: SmartInputComposerDraft,
  selection: SmartInputComposerSelection,
  referenceId: string,
  resolveReference: (referenceId: string) => SmartInputComposerResolvedReference | null,
): { draft: SmartInputComposerDraft; selection: SmartInputComposerSelection } {
  return replaceSmartInputComposerRange(
    draft,
    selection,
    [{ kind: 'reference', referenceId }],
    resolveReference,
  );
}

export function insertSmartInputComposerReferences(
  draft: SmartInputComposerDraft,
  selection: SmartInputComposerSelection,
  referenceIds: readonly string[],
  resolveReference: (referenceId: string) => SmartInputComposerResolvedReference | null,
): { draft: SmartInputComposerDraft; selection: SmartInputComposerSelection } {
  if (referenceIds.length === 0) {
    return { draft: cloneSmartInputComposerDraft(draft), selection: { ...selection } };
  }

  const parts: SmartInputComposerPart[] = [];
  referenceIds.forEach((referenceId, index) => {
    if (index > 0) {
      parts.push({ kind: 'text', text: ' ' });
    }
    parts.push({ kind: 'reference', referenceId });
  });

  return replaceSmartInputComposerRange(draft, selection, parts, resolveReference);
}

export function deleteSmartInputComposerBackward(
  draft: SmartInputComposerDraft,
  selection: SmartInputComposerSelection,
  resolveReference: (referenceId: string) => SmartInputComposerResolvedReference | null,
): {
  draft: SmartInputComposerDraft;
  removedReferenceIds: string[];
  selection: SmartInputComposerSelection;
} {
  return deleteSmartInputComposer(draft, selection, 'backward', resolveReference);
}

export function deleteSmartInputComposerForward(
  draft: SmartInputComposerDraft,
  selection: SmartInputComposerSelection,
  resolveReference: (referenceId: string) => SmartInputComposerResolvedReference | null,
): {
  draft: SmartInputComposerDraft;
  removedReferenceIds: string[];
  selection: SmartInputComposerSelection;
} {
  return deleteSmartInputComposer(draft, selection, 'forward', resolveReference);
}

export function normalizeSmartInputComposerSelection(
  draft: SmartInputComposerDraft,
  selection: SmartInputComposerSelection,
  resolveReference: (referenceId: string) => SmartInputComposerResolvedReference | null,
  affinity: SelectionAffinity = 'nearest',
): SmartInputComposerSelection {
  const normalizedStart = Math.max(0, Math.min(selection.start, selection.end));
  const normalizedEnd = Math.max(0, Math.max(selection.start, selection.end));
  if (normalizedStart === normalizedEnd) {
    const collapsed = normalizeCollapsedOffset(draft, normalizedStart, resolveReference, affinity);
    return {
      start: collapsed,
      end: collapsed,
    };
  }

  const segments = buildRenderedSegments(draft, resolveReference);
  let start = normalizedStart;
  let end = normalizedEnd;
  for (const segment of segments) {
    if (segment.kind !== 'reference') {
      continue;
    }

    if (segment.start < end && segment.end > start) {
      start = Math.min(start, segment.start);
      end = Math.max(end, segment.end);
    }
  }

  return { start, end };
}

export function removeSmartInputComposerReference(
  draft: SmartInputComposerDraft,
  referenceId: string,
): SmartInputComposerDraft {
  return {
    nextOrdinalByKind: { ...draft.nextOrdinalByKind },
    parts: normalizeSmartInputComposerParts(
      draft.parts.filter(
        (part) => !(part.kind === 'reference' && part.referenceId === referenceId),
      ),
    ),
  };
}

export function pruneSmartInputComposerReferences(
  draft: SmartInputComposerDraft,
  validReferenceIds: ReadonlySet<string>,
): SmartInputComposerDraft {
  if (draft.richDoc) return pruneRichComposerDraft(draft, validReferenceIds);
  return {
    nextOrdinalByKind: { ...draft.nextOrdinalByKind },
    parts: normalizeSmartInputComposerParts(
      draft.parts.filter(
        (part) => part.kind !== 'reference' || validReferenceIds.has(part.referenceId),
      ),
    ),
  };
}

export function getSmartInputComposerReferenceIds(draft: SmartInputComposerDraft): string[] {
  return draft.parts
    .filter((part): part is SmartInputComposerReferencePart => part.kind === 'reference')
    .map((part) => part.referenceId);
}

export function getSmartInputComposerReferenceIdsInSelection(
  draft: SmartInputComposerDraft,
  selection: SmartInputComposerSelection,
  resolveReference: (referenceId: string) => SmartInputComposerResolvedReference | null,
): string[] {
  return getRemovedReferenceIds(
    draft,
    normalizeSmartInputComposerSelection(draft, selection, resolveReference, 'nearest'),
    resolveReference,
  );
}

function deleteSmartInputComposer(
  draft: SmartInputComposerDraft,
  selection: SmartInputComposerSelection,
  direction: 'backward' | 'forward',
  resolveReference: (referenceId: string) => SmartInputComposerResolvedReference | null,
): {
  draft: SmartInputComposerDraft;
  removedReferenceIds: string[];
  selection: SmartInputComposerSelection;
} {
  const normalized = normalizeSmartInputComposerSelection(
    draft,
    selection,
    resolveReference,
    'nearest',
  );
  if (normalized.start !== normalized.end) {
    return removeSmartInputComposerRange(draft, normalized, resolveReference);
  }

  if (direction === 'backward' && normalized.start === 0) {
    return {
      draft: cloneSmartInputComposerDraft(draft),
      removedReferenceIds: [],
      selection: normalized,
    };
  }

  const text = getSmartInputComposerText(draft, resolveReference);
  if (direction === 'forward' && normalized.start >= text.length) {
    return {
      draft: cloneSmartInputComposerDraft(draft),
      removedReferenceIds: [],
      selection: normalized,
    };
  }

  const segments = buildRenderedSegments(draft, resolveReference);
  const tokenSegment = segments.find((segment) =>
    direction === 'backward'
      ? segment.kind === 'reference' &&
        normalized.start > segment.start &&
        normalized.start <= segment.end
      : segment.kind === 'reference' &&
        normalized.start >= segment.start &&
        normalized.start < segment.end,
  );

  if (tokenSegment) {
    return removeSmartInputComposerRange(
      draft,
      { start: tokenSegment.start, end: tokenSegment.end },
      resolveReference,
    );
  }

  const range =
    direction === 'backward'
      ? { start: normalized.start - 1, end: normalized.start }
      : { start: normalized.start, end: normalized.start + 1 };
  return removeSmartInputComposerRange(draft, range, resolveReference);
}

function replaceSmartInputComposerRange(
  draft: SmartInputComposerDraft,
  selection: SmartInputComposerSelection,
  insertParts: readonly SmartInputComposerPart[],
  resolveReference: (referenceId: string) => SmartInputComposerResolvedReference | null,
): { draft: SmartInputComposerDraft; selection: SmartInputComposerSelection } {
  const normalizedSelection = normalizeSmartInputComposerSelection(
    draft,
    selection,
    resolveReference,
    'after',
  );
  const [beforeParts, afterStartParts] = splitSmartInputComposerPartsAtOffset(
    draft.parts,
    normalizedSelection.start,
    resolveReference,
  );
  const [, afterParts] = splitSmartInputComposerPartsAtOffset(
    afterStartParts,
    normalizedSelection.end - normalizedSelection.start,
    resolveReference,
  );
  const nextDraft: SmartInputComposerDraft = {
    nextOrdinalByKind: { ...draft.nextOrdinalByKind },
    parts: normalizeSmartInputComposerParts([...beforeParts, ...insertParts, ...afterParts]),
  };
  const insertedLength = getRenderedLengthForParts(insertParts, resolveReference);
  const nextSelectionStart = normalizedSelection.start + insertedLength;

  return {
    draft: nextDraft,
    selection: {
      start: nextSelectionStart,
      end: nextSelectionStart,
    },
  };
}

function removeSmartInputComposerRange(
  draft: SmartInputComposerDraft,
  selection: SmartInputComposerSelection,
  resolveReference: (referenceId: string) => SmartInputComposerResolvedReference | null,
): {
  draft: SmartInputComposerDraft;
  removedReferenceIds: string[];
  selection: SmartInputComposerSelection;
} {
  const normalizedSelection = normalizeSmartInputComposerSelection(
    draft,
    selection,
    resolveReference,
    'nearest',
  );
  const removedReferenceIds = getRemovedReferenceIds(draft, normalizedSelection, resolveReference);
  const [beforeParts, afterStartParts] = splitSmartInputComposerPartsAtOffset(
    draft.parts,
    normalizedSelection.start,
    resolveReference,
  );
  const [, afterParts] = splitSmartInputComposerPartsAtOffset(
    afterStartParts,
    normalizedSelection.end - normalizedSelection.start,
    resolveReference,
  );
  return {
    draft: {
      nextOrdinalByKind: { ...draft.nextOrdinalByKind },
      parts: normalizeSmartInputComposerParts([...beforeParts, ...afterParts]),
    },
    removedReferenceIds,
    selection: {
      start: normalizedSelection.start,
      end: normalizedSelection.start,
    },
  };
}

function getRemovedReferenceIds(
  draft: SmartInputComposerDraft,
  selection: SmartInputComposerSelection,
  resolveReference: (referenceId: string) => SmartInputComposerResolvedReference | null,
): string[] {
  const segments = buildRenderedSegments(draft, resolveReference);
  return segments
    .filter(
      (segment) =>
        segment.kind === 'reference' &&
        segment.start < selection.end &&
        segment.end > selection.start &&
        segment.referenceId,
    )
    .map((segment) => segment.referenceId as string);
}

function normalizeCollapsedOffset(
  draft: SmartInputComposerDraft,
  offset: number,
  resolveReference: (referenceId: string) => SmartInputComposerResolvedReference | null,
  affinity: SelectionAffinity,
): number {
  const segments = buildRenderedSegments(draft, resolveReference);
  const tokenSegment = segments.find(
    (segment) => segment.kind === 'reference' && offset > segment.start && offset < segment.end,
  );
  if (!tokenSegment) {
    return offset;
  }

  if (affinity === 'before') {
    return tokenSegment.start;
  }

  if (affinity === 'after') {
    return tokenSegment.end;
  }

  return offset - tokenSegment.start <= tokenSegment.end - offset
    ? tokenSegment.start
    : tokenSegment.end;
}

function buildRenderedSegments(
  draft: SmartInputComposerDraft,
  resolveReference: (referenceId: string) => SmartInputComposerResolvedReference | null,
): RenderedComposerSegment[] {
  const segments: RenderedComposerSegment[] = [];
  let cursor = 0;
  draft.parts.forEach((part, partIndex) => {
    const text =
      part.kind === 'text' ? part.text : (resolveReference(part.referenceId)?.tokenText ?? '');
    const nextCursor = cursor + text.length;
    segments.push({
      start: cursor,
      end: nextCursor,
      kind: part.kind,
      partIndex,
      referenceId: part.kind === 'reference' ? part.referenceId : null,
      text,
    });
    cursor = nextCursor;
  });
  return segments;
}

function splitSmartInputComposerPartsAtOffset(
  parts: readonly SmartInputComposerPart[],
  offset: number,
  resolveReference: (referenceId: string) => SmartInputComposerResolvedReference | null,
): [SmartInputComposerPart[], SmartInputComposerPart[]] {
  if (offset <= 0) {
    return [[], parts.map((part) => ({ ...part }))];
  }

  const beforeParts: SmartInputComposerPart[] = [];
  const afterParts: SmartInputComposerPart[] = [];
  let remaining = offset;

  for (const part of parts) {
    const renderedText =
      part.kind === 'text' ? part.text : (resolveReference(part.referenceId)?.tokenText ?? '');
    const partLength = renderedText.length;
    if (remaining <= 0) {
      afterParts.push({ ...part });
      continue;
    }

    if (remaining >= partLength) {
      beforeParts.push({ ...part });
      remaining -= partLength;
      continue;
    }

    if (part.kind !== 'text') {
      throw new Error('Attempted to split inside an atomic composer reference token.');
    }

    const leftText = part.text.slice(0, remaining);
    const rightText = part.text.slice(remaining);
    if (leftText) {
      beforeParts.push({ kind: 'text', text: leftText });
    }
    if (rightText) {
      afterParts.push({ kind: 'text', text: rightText });
    }
    remaining = 0;
  }

  return [beforeParts, afterParts];
}

function normalizeSmartInputComposerParts(
  parts: readonly SmartInputComposerPart[],
): SmartInputComposerPart[] {
  const normalized: SmartInputComposerPart[] = [];
  for (const part of parts) {
    if (part.kind === 'text' && !part.text) {
      continue;
    }

    const previousPart = normalized[normalized.length - 1];
    if (part.kind === 'text' && previousPart?.kind === 'text') {
      previousPart.text += part.text;
      continue;
    }

    normalized.push({ ...part });
  }

  return normalized;
}

function getRenderedLengthForParts(
  parts: readonly SmartInputComposerPart[],
  resolveReference: (referenceId: string) => SmartInputComposerResolvedReference | null,
): number {
  return parts.reduce((total, part) => {
    if (part.kind === 'text') {
      return total + part.text.length;
    }

    return total + (resolveReference(part.referenceId)?.tokenText.length ?? 0);
  }, 0);
}

import { afterEach, describe, expect, it, vi } from 'vitest';
import { EditorState, NodeSelection, TextSelection, Selection } from 'prosemirror-state';
import { history, undo, redo, closeHistory } from 'prosemirror-history';
import { CellSelection, tableEditing, TableMap, isInTable } from 'prosemirror-tables';
import { draftFromRichDocument, getRichComposerDocument, parsePersistedRichDocument, isSafeComposerLink, richComposerSchema, richDocumentToParts, richNode } from './richComposerDocument';
import { cloneSmartInputComposerDraft, pruneSmartInputComposerReferences } from './smartInputComposerDraft';
import { loadSmartInputCurrentDraft, persistSmartInputCurrentDraft } from './smartInputDraftStore';
import { moveRichTableCell, pasteRichSlice, replaceBookmarkedRichAttachment, replaceRichAttachment, richComposerOwnsShortcut, restoreRichComposerSelection, richComposerTrailingParagraph } from './richComposerTransactions';
import { Slice, Fragment } from 'prosemirror-model';
import { submitAppServerControlComposerDraft } from './appServerControlAttachmentSubmission';
import { RichComposer } from './richComposer';
import type { AppServerControlTurnRequest } from '../../api/types';
import { openImageAnnotation } from './imageAnnotation';
vi.mock('./imageAnnotation', () => ({ openImageAnnotation: vi.fn() }));
import { orderedRichAttachments, prepareSmartInputOutboundPrompt } from './smartInputOutboundReferences';
import type { AppServerControlComposerDraftAttachment } from './appServerControlAttachments';

function paragraph(text: string) { return richNode('paragraph').create(null, text ? richComposerSchema.text(text) : undefined); }
function atom(id: string) { return richNode('attachment').create({ referenceId: id }); }
function table() {
  return richNode('table').create(null, [0, 1].map((row) => richNode('table_row').create(null,
    [0, 1].map((column) => richNode(row ? 'table_cell' : 'table_header').create(null, paragraph(`${row}:${column}`))))));
}
function document() {
  return richNode('doc').create(null, [richNode('paragraph').create(null, [richComposerSchema.text('before '), atom('image-A'), richComposerSchema.text(' after')]),
    table(), richNode('paragraph').create(null, [atom('image-B'), richComposerSchema.text(' end')])]);
}
function draft() { return draftFromRichDocument({ parts: [], nextOrdinalByKind: { image: 3 } }, document()); }
function attachment(id: string, path: string): AppServerControlComposerDraftAttachment {
  return { id, uploadedPath: path, displayName: 'same-name.png', kind: 'image', mimeType: 'image/png', file: null,
    previewUrl: null, referenceKind: 'image', referenceLabel: id, referenceOrdinal: 1,
    referenceCharCount: null, referenceLineCount: null, sizeBytes: 10 };
}
function tableState() {
  const doc = richNode('doc').create(null, [table(), paragraph('after')]);
  return EditorState.create({ doc, selection: TextSelection.create(doc, 4), plugins: [history(), tableEditing()] });
}

afterEach(() => vi.unstubAllGlobals());

describe('rich composer document contracts', () => {
  it.each(['a|b', String.raw`a\|b`, String.raw`a\\|b`, String.raw`C:\temp\file`, '<script>alert(1)</script>'])
    ('preserves literal table characters through provider Markdown: %s', (text) => {
      const doc = richNode('doc').create(null, richNode('table').create(null, [
        richNode('table_row').create(null, richNode('table_header').create(null, paragraph('Header'))),
        richNode('table_row').create(null, richNode('table_cell').create(null, paragraph(text))),
      ]));
      const restored = getRichComposerDocument({ parts: richDocumentToParts(doc), nextOrdinalByKind: {} });
      expect(restored.child(0).child(1).childCount).toBe(1);
      expect(restored.child(0).child(1).child(0).textContent).toBe(text);
    });
  it.each(['a|b', String.raw`a\|b`, String.raw`a\\|b`])
    ('preserves inline-code table characters through provider Markdown: %s', (text) => {
      const code = richNode('paragraph').create(null, richComposerSchema.text(text, [richComposerSchema.marks.code!.create()]));
      const doc = richNode('doc').create(null, richNode('table').create(null, [
        richNode('table_row').create(null, richNode('table_header').create(null, paragraph('Header'))),
        richNode('table_row').create(null, richNode('table_cell').create(null, code)),
      ]));
      const restored = getRichComposerDocument({ parts: richDocumentToParts(doc), nextOrdinalByKind: {} });
      const cell = restored.child(0).child(1).child(0);
      expect(cell.textContent).toBe(text);
      expect(cell.child(0).child(0).marks[0]?.type.name).toBe('code');
    });
  it('supports headings with attachment atoms after replacing the Markdown image node', () => {
    const doc = richNode('doc').create(null, richNode('heading').create({ level: 2 },
      [richComposerSchema.text('Heading '), atom('image-A')]));
    doc.check();
    expect(richDocumentToParts(doc).some((part) => part.kind === 'reference' && part.referenceId === 'image-A')).toBe(true);
  });
  function annotationHarness() {
    const original = attachment('original', '/original.png'); original.previewUrl = 'original-preview';
    const doc = richNode('doc').create(null, richNode('paragraph').create(null, [atom('original'), richComposerSchema.text(' after')]));
    const state = EditorState.create({ doc, plugins: [history()] });
    const editors = new Map([['source', { state, draft: draftFromRichDocument(draft(), doc), generation: {} }]]);
    let finish!: (edited: AppServerControlComposerDraftAttachment) => void;
    const stageFile = vi.fn(() => new Promise<AppServerControlComposerDraftAttachment>((resolve) => { finish = resolve; }));
    const composer = Object.create(RichComposer.prototype) as RichComposer;
    Object.assign(composer, { editors, sessionId: 'source', view: null, bookmarks: new Set(),
      lifetimes: new Map([['source', new AbortController()]]), callbacks: {
        attachments: () => [original], stageFile, changed: vi.fn(),
        pending: (_session: string, action: () => Promise<void>) => action(),
      } });
    const internal = composer as unknown as {
      sessionId: string;
      annotate(position: number, id: string): void;
      applySessionTransaction(session: string, tr: import('prosemirror-state').Transaction): void;
    };
    internal.annotate(1, 'original');
    const options = vi.mocked(openImageAnnotation).mock.calls.at(-1)![0];
    return { composer, internal, editors, options, finish: () => finish(attachment('edited', '/edited.png')), stageFile, original };
  }

  it('commits a delayed annotation to the mapped original session after switching away', async () => {
    const f = annotationHarness();
    const save = f.options.save(new File(['png'], 'edited.png'), new AbortController().signal);
    f.internal.sessionId = 'other';
    f.internal.applySessionTransaction('source', f.editors.get('source')!.state.tr.insertText('prefix ', 1));
    f.finish(); await save;
    expect(f.editors.get('source')!.state.doc.nodeAt(8)?.attrs.referenceId).toBe('edited');
    expect(f.editors.has('other')).toBe(false);
    expect(f.original.uploadedPath).toBe('/original.png');
    expect(f.stageFile).toHaveBeenCalledTimes(1);
    f.options.closed?.();
  });

  it.each(['cancel', 'replace', 'close'])('rejects late annotation save after %s without changing the replacement draft', async (kind) => {
    const f = annotationHarness();
    const controller = new AbortController();
    const save = f.options.save(new File(['png'], 'edited.png'), controller.signal);
    const rejected = expect(save).rejects.toThrow();
    const original = f.editors.get('source')!;
    if (kind === 'cancel') { controller.abort(); f.options.closed?.(); }
    if (kind === 'replace') f.editors.set('source', { ...original, generation: {} });
    if (kind === 'close') f.editors.delete('source');
    f.finish(); await rejected;
    expect(f.editors.get('source')?.state.doc.eq(original.state.doc) ?? true).toBe(true);
    expect(f.original.uploadedPath).toBe('/original.png');
  });
  it('keeps the original session generation across edits and switching, invalidating replacement or closed drafts', () => {
    const generation = {};
    const editors = new Map<string, { generation: object }>([['original', { generation }]]);
    // Exercise the actual lifecycle guard without mounting an EditorView.
    const composer = Object.create(RichComposer.prototype) as RichComposer;
    Object.assign(composer, { editors });
    const valid = composer.captureDocumentGuard('original');
    editors.set('other-session', { generation: {} });
    editors.set('original', { generation }); // Ordinary transactions preserve this generation.
    expect(valid()).toBe(true);
    editors.set('original', { generation: {} });
    expect(valid()).toBe(false);
    const next = composer.captureDocumentGuard('original');
    editors.delete('original');
    expect(next()).toBe(false);
    editors.set('original', { generation: {} });
    expect(next()).toBe(false);
  });
  it('replaces the mapped original annotation atom after intervening edits, never an adjacent equal ID', () => {
    let state = EditorState.create({ doc: richNode('doc').create(null,
      richNode('paragraph').create(null, [atom('same'), richComposerSchema.text(' gap '), atom('same')])), plugins: [history()] });
    let bookmark = NodeSelection.create(state.doc, 1).getBookmark();
    const prefix = state.tr.insertText('prefix ', 1);
    bookmark = bookmark.map(prefix.mapping);
    state = state.applyTransaction(prefix).state;
    const original = state.doc;
    state = state.applyTransaction(replaceBookmarkedRichAttachment(state, bookmark, 'same', 'edited')!).state;
    expect(richDocumentToParts(state.doc).filter((part) => part.kind === 'reference').map((part) => part.referenceId))
      .toEqual(['edited', 'same']);
    undo(state, (tr) => { state = state.applyTransaction(tr).state; });
    expect(state.doc.eq(original)).toBe(true);
    const deletion = state.tr.delete(8, 9);
    bookmark = bookmark.map(deletion.mapping);
    state = state.applyTransaction(deletion).state;
    expect(replaceBookmarkedRichAttachment(state, bookmark, 'same', 'wrong')).toBeNull();
  });

  it('pastes through a mapped saved table selection without any mounted/visible editor', () => {
    let saved = tableState();
    const other = tableState();
    let bookmark = CellSelection.create(saved.doc, 2).getBookmark();
    const edit = saved.tr.insertText('prefix ', saved.doc.content.size - 1);
    bookmark = bookmark.map(edit.mapping);
    saved = saved.applyTransaction(edit).state;
    const slice = new Slice(Fragment.from(paragraph('pasted text')), 0, 0);
    pasteRichSlice(() => saved, (tr) => { saved = saved.applyTransaction(tr).state; }, bookmark.resolve(saved.doc), slice);
    saved.doc.check();
    expect(saved.doc.child(0).child(0).child(0).textContent).toBe('pasted text');
    expect(other.doc.textContent).not.toContain('pasted text');
  });

  it('uses rich JSON for outbound refs despite stale parts and retains uploads across failed explicit submits', async () => {
    const current = draft();
    current.parts = [{ kind: 'reference', referenceId: 'orphan' }];
    const registry = [attachment('orphan', '/orphan.png'), attachment('image-A', '/A.png'), attachment('image-B', '/B.png')];
    const upload = vi.fn();
    const submit = vi.fn().mockRejectedValueOnce(new Error('queue rejected')).mockResolvedValue(undefined);
    const create = vi.fn((text: string, attachments: AppServerControlTurnRequest['attachments']) => ({ text, attachments }));
    const args = { sessionId: 'original-session', draft: current, attachments: registry,
      uploadFile: upload, uploadFailureMessage: 'upload', attachmentReadFailureMessage: 'read',
      createTurnRequest: create, submitQueuedTurn: submit };
    const first = await submitAppServerControlComposerDraft(args);
    await expect(first.queuedTurn).rejects.toThrow('queue rejected');
    expect(submit).toHaveBeenCalledTimes(1); // No automatic replay after uncertain delivery.
    const second = await submitAppServerControlComposerDraft(args);
    await second.queuedTurn;
    expect(second.request.attachments.map((entry) => entry.path)).toEqual(['/A.png', '/B.png']);
    expect(upload).not.toHaveBeenCalled();
    expect(current.parts).toEqual([{ kind: 'reference', referenceId: 'orphan' }]);
  });
  it('roundtrips editable table structure, atom IDs and cell selection through the existing draft store', () => {
    const values = new Map<string, string>();
    vi.stubGlobal('localStorage', { getItem: (key: string) => values.get(key) ?? null,
      setItem: (key: string, value: string) => values.set(key, value), removeItem: (key: string) => values.delete(key) });
    const doc = document();
    const tablePosition = doc.child(0).nodeSize;
    const selection = CellSelection.create(doc, tablePosition + 2);
    const current = draftFromRichDocument(draft(), doc, selection.toJSON());
    persistSmartInputCurrentDraft('s', current);
    const restored = loadSmartInputCurrentDraft('s')!;
    expect(getRichComposerDocument(restored).eq(doc)).toBe(true);
    expect(Selection.fromJSON(doc, restored.richDoc!.selection)).toBeInstanceOf(CellSelection);
    expect(JSON.stringify(restored)).not.toContain('blob:');
    expect(restored.parts.filter((part) => part.kind === 'reference').map((part) => part.referenceId)).toEqual(['image-A', 'image-B']);
  });

  it('derives one ordered text/image/table projection and sends only referenced attachments in document order', async () => {
    const current = draft();
    const parts = richDocumentToParts(document());
    const text = parts.map((part) => part.kind === 'text' ? part.text : `[${part.referenceId}]`).join('');
    expect(text.indexOf('before')).toBeLessThan(text.indexOf('[image-A]'));
    expect(text.indexOf('[image-A]')).toBeLessThan(text.indexOf('| 0:0'));
    expect(text.indexOf('| 0:0')).toBeLessThan(text.indexOf('[image-B]'));
    const registry = [attachment('image-B', '/B.png'), attachment('undo-only', '/old.png'), attachment('image-A', '/A.png')];
    expect(orderedRichAttachments(current, registry).map((entry) => entry.id)).toEqual(['image-A', 'image-B']);
    const upload = vi.fn();
    const prepared = await prepareSmartInputOutboundPrompt({ sessionId: 's', draft: current, attachments: registry,
      uploadFile: upload, uploadFailureMessage: 'failed', attachmentReadFailureMessage: 'read failed' });
    expect(prepared.attachments.map((entry) => entry.path)).toEqual(['/A.png', '/B.png']);
    expect(prepared.text).toBe(text);
    expect(upload).not.toHaveBeenCalled();
  });

  it('preserves an empty rich document and independent saved selections instead of remigrating staged refs', () => {
    const empty = richNode('doc').create(null, paragraph(''));
    const current = draftFromRichDocument({ parts: [], nextOrdinalByKind: {} }, empty,
      { type: 'text', anchor: 1, head: 1 });
    const cloned = cloneSmartInputComposerDraft(current);
    expect(cloned.richDoc).toBeDefined();
    expect(getRichComposerDocument(cloned).eq(empty)).toBe(true);
    cloned.richDoc!.selection!.anchor = 2;
    expect(current.richDoc!.selection!.anchor).toBe(1);
  });

  it('maps an asynchronous paste bookmark through edits before its original caret', () => {
    const doc = richNode('doc').create(null, paragraph('before after'));
    const selection = TextSelection.create(doc, 8);
    const state = EditorState.create({ doc, selection });
    const transaction = state.tr.insertText('prefix ', 1);
    const mapped = selection.getBookmark().map(transaction.mapping).resolve(transaction.doc);
    expect(mapped.from).toBe(15);
    expect(mapped.$from.doc).toBe(transaction.doc);
  });

  it('treats rich JSON as authoritative even if a cached parts projection is stale', () => {
    const current = draft(); current.parts = [{ kind: 'text', text: 'stale projection' }];
    const cloned = cloneSmartInputComposerDraft(current);
    expect(cloned.parts).toEqual(richDocumentToParts(document()));
    expect(pruneSmartInputComposerReferences(current, new Set(['image-B'])).parts
      .filter((part) => part.kind === 'reference').map((part) => part.referenceId)).toEqual(['image-B']);
  });

  it('replaces exactly one selected atom, preserves the original document and supports native undo/redo', () => {
    const original = richNode('doc').create(null, richNode('paragraph').create(null, [atom('same-ref'), richComposerSchema.text(' / '), atom('same-ref')]));
    let state = EditorState.create({ doc: original, plugins: [history()] });
    const tr = replaceRichAttachment(state, 1, 'same-ref', 'edited-ref')!;
    state = state.applyTransaction(tr).state;
    expect(state.selection).toBeInstanceOf(NodeSelection);
    expect(richDocumentToParts(state.doc).filter((part) => part.kind === 'reference').map((part) => part.referenceId)).toEqual(['edited-ref', 'same-ref']);
    expect(original.child(0).child(0).attrs.referenceId).toBe('same-ref');
    expect(replaceRichAttachment(state, 1, 'same-ref', 'wrong')).toBeNull();
    expect(undo(state, (undoTr) => { state = state.applyTransaction(undoTr).state; })).toBe(true);
    expect(state.doc.eq(original)).toBe(true);
    expect(redo(state, (redoTr) => { state = state.applyTransaction(redoTr).state; })).toBe(true);
    expect(state.doc.eq(original)).toBe(false);
  });

  it('keeps cell Tab and Shift-Tab within the table and adds a final row through table commands', () => {
    let state = tableState();
    const dispatch = (tr: import('prosemirror-state').Transaction) => { state = state.applyTransaction(tr).state; };
    expect(moveRichTableCell(1)(state, dispatch)).toBe(true);
    expect(isInTable(state)).toBe(true);
    expect(moveRichTableCell(-1)(state, dispatch)).toBe(true);
    const map = TableMap.get(state.doc.child(0));
    state = state.apply(state.tr.setSelection(TextSelection.create(state.doc, (map.map[3] ?? 0) + 3)));
    expect(moveRichTableCell(1)(state, dispatch)).toBe(true);
    expect(TableMap.get(state.doc.child(0)).height).toBe(3);
  });

  it('leaves IME and table editing events to ProseMirror while retaining the outside-table Plan shortcut', () => {
    const cells = tableState();
    expect(richComposerOwnsShortcut(cells, { key: 'Enter', isComposing: true }, false)).toBe(false);
    expect(richComposerOwnsShortcut(cells, { key: 'Tab', isComposing: false }, false)).toBe(false);
    expect(richComposerOwnsShortcut(cells, { key: 'Enter', isComposing: false }, false)).toBe(false);
    const plain = EditorState.create({ doc: richNode('doc').create(null, paragraph('text')) });
    expect(richComposerOwnsShortcut(plain, { key: 'Tab', isComposing: false }, false)).toBe(true);
    expect(richComposerOwnsShortcut(plain, { key: 'Enter', isComposing: false }, true)).toBe(false);
    let state = EditorState.create({ doc: richNode('doc').create(null, paragraph('')), plugins: [history()] });
    state = state.applyTransaction(state.tr.insertText('IME commit', 1)).state;
    state = state.applyTransaction(closeHistory(state.tr).insertText(' pasted', 11)).state;
    undo(state, (tr) => { state = state.applyTransaction(tr).state; });
    expect(state.doc.textContent).toBe('IME commit');
  });

  it('preserves legacy refs inside link syntax and keeps unstaged image URLs as text', () => {
    const current = { parts: [{ kind: 'text' as const, text: '[link](' },
      { kind: 'reference' as const, referenceId: 'A' }, { kind: 'text' as const, text: ')' }], nextOrdinalByKind: {} };
    const restored = getRichComposerDocument(current); restored.check();
    expect(richDocumentToParts(restored).some((part) => part.kind === 'reference' && part.referenceId === 'A')).toBe(true);
    const image = getRichComposerDocument({ parts: [{ kind: 'text', text: '![alt](https://example.test/image.png)' }], nextOrdinalByKind: {} });
    expect(image.textContent).toContain('https://example.test/image.png');
  });

  it('resolves restored selection against the exact live document and falls back after pruning', () => {
    const first = document(); const equivalent = richComposerSchema.nodeFromJSON(first.toJSON());
    const restored = restoreRichComposerSelection(equivalent, { type: 'text', anchor: 3, head: 5 });
    expect(restored.$from.doc).toBe(equivalent);
    expect(restored.from).toBe(3);
    expect(restoreRichComposerSelection(equivalent, { type: 'text', anchor: 999999, head: 999999 }).$from.doc).toBe(equivalent);
  });

  it('keeps an editable paragraph after a final table in the same native undo event', () => {
    const empty = richNode('doc').create(null, paragraph(''));
    let state = EditorState.create({ doc: empty, plugins: [history(), richComposerTrailingParagraph(), tableEditing()] });
    state = state.applyTransaction(closeHistory(state.tr).replaceSelectionWith(table())).state;
    expect(state.doc.lastChild?.type.name).toBe('paragraph');
    expect(state.doc.childCount).toBe(2);
    undo(state, (tr) => { state = state.applyTransaction(tr).state; });
    expect(state.doc.eq(empty)).toBe(true);
  });

  it('parses legacy Markdown table projection back to editable cells with reference identities', () => {
    const legacy = { parts: richDocumentToParts(document()), nextOrdinalByKind: {} };
    const restored = getRichComposerDocument(legacy);
    restored.check();
    expect(restored.child(1).type.name).toBe('table');
    expect(TableMap.get(restored.child(1)).width).toBe(2);
    expect(richDocumentToParts(restored).filter((part) => part.kind === 'reference').map((part) => part.referenceId)).toEqual(['image-A', 'image-B']);
  });

  it('rejects active URL schemes and saved links that could execute scripts', () => {
    for (const value of ['javascript:alert(1)', 'data:text/html,evil', '//remote.test', '/\\remote'])
      expect(isSafeComposerLink(value)).toBe(false);
    expect(isSafeComposerLink('https://example.test/path?q=1')).toBe(true);
    expect(parsePersistedRichDocument({ version: 1, doc: { type: 'doc', content: [{ type: 'paragraph',
      content: [{ type: 'text', text: 'unsafe', marks: [{ type: 'link', attrs: { href: 'javascript:alert(1)' } }] }] }] } })).toBeNull();
  });
});

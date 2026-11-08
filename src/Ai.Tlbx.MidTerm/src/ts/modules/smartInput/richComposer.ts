import { Fragment, Slice, type Node as ProseMirrorNode } from 'prosemirror-model';
import {
  EditorState,
  NodeSelection,
  type Selection,
  TextSelection,
  type Command,
  type SelectionBookmark,
  type Transaction,
  type Plugin,
} from 'prosemirror-state';
import { EditorView } from 'prosemirror-view';
import { baseKeymap, chainCommands, toggleMark } from 'prosemirror-commands';
import { history, undo, redo, closeHistory } from 'prosemirror-history';
import { keymap } from 'prosemirror-keymap';
import { wrapInList, splitListItem, sinkListItem, liftListItem } from 'prosemirror-schema-list';
import {
  addColumnAfter,
  addRowAfter,
  deleteTable,
  isInTable,
  tableEditing,
} from 'prosemirror-tables';
import { t } from '../i18n';
import type { SmartInputComposerDraft } from './smartInputComposerDraft';
import type { AppServerControlComposerDraftAttachment } from './appServerControlAttachments';
import {
  draftFromRichDocument,
  getRichComposerDocument,
  richDocumentFromClipboardSlice,
  richComposerSchema,
  richNode,
} from './richComposerDocument';
import {
  captureRichClipboard,
  materializeRichClipboardImages,
  richClipboardReferenceIds,
  richClipboardSlice,
  type RichClipboardData,
} from './richComposerClipboard';
import { richAttachmentView } from './richComposerAttachmentView';
import { openImageAnnotation } from './imageAnnotation';
import { shouldConvertPastedTextToSmartInputReference } from './smartInputTextReferences';
import {
  moveRichTableCell,
  pasteRichSlice,
  replaceBookmarkedRichAttachment,
  richComposerOwnsShortcut,
  restoreRichComposerSelection,
  richComposerTrailingParagraph,
} from './richComposerTransactions';

export interface RichComposerCallbacks {
  changed: (sessionId: string, draft: SmartInputComposerDraft, edited: boolean) => void;
  keydown: (event: KeyboardEvent) => boolean;
  focus: () => void;
  submissionChanged: () => void;
  stageFile: (
    sessionId: string,
    file: File,
    original?: AppServerControlComposerDraftAttachment,
    signal?: AbortSignal,
  ) => Promise<AppServerControlComposerDraftAttachment>;
  stageText: (
    sessionId: string,
    text: string,
    signal: AbortSignal,
  ) => Promise<AppServerControlComposerDraftAttachment>;
  attachments: (sessionId: string) => readonly AppServerControlComposerDraftAttachment[];
  open: (sessionId: string, attachment: AppServerControlComposerDraftAttachment) => void;
  pending: (sessionId: string, action: () => Promise<void>) => Promise<void>;
}
interface SessionEditor {
  state: EditorState;
  draft: SmartInputComposerDraft;
  generation: object;
}
interface PendingBookmark {
  sessionId: string;
  bookmark: SelectionBookmark;
  generation: object;
}

export class RichComposer {
  readonly host = document.createElement('div');
  private readonly toolbar = document.createElement('div');
  private readonly mount = document.createElement('div');
  private readonly error = document.createElement('span');
  private readonly retry = document.createElement('button');
  private readonly cancelPaste = document.createElement('button');
  private readonly failures = new Map<PendingBookmark, { message: string; retry: () => void }>();
  private readonly editors = new Map<string, SessionEditor>();
  private readonly lifetimes = new Map<string, AbortController>();
  private readonly bookmarks = new Set<PendingBookmark>();
  private readonly refreshers = new Set<() => void>();
  private view: EditorView | null = null;
  private sessionId: string | null = null;
  private readonly submitting = new Set<string>();

  constructor(private readonly callbacks: RichComposerCallbacks) {
    this.mount.className = 'rich-composer-content';
    this.host.className = 'smart-input-rich-composer';
    this.host.hidden = true;
    this.toolbar.className = 'rich-composer-tools';
    this.toolbar.setAttribute('role', 'toolbar');
    this.toolbar.setAttribute('aria-label', t('smartInput.richFormatting'));
    this.error.className = 'rich-composer-error';
    this.error.setAttribute('role', 'status');
    this.retry.className = 'btn-secondary';
    this.cancelPaste.className = 'btn-secondary';
    this.retry.type = 'button';
    this.retry.hidden = true;
    this.retry.textContent = t('smartInput.richRetry');
    this.cancelPaste.type = 'button';
    this.cancelPaste.hidden = true;
    this.cancelPaste.textContent = t('dialog.cancel');
    this.addTool('B', 'richBold', () => {
      this.mark('strong');
    });
    this.addTool('I', 'richItalic', () => {
      this.mark('em');
    });
    this.addTool('•', 'richList', () => {
      this.command(wrapInList(richNode('bullet_list')));
    });
    this.addTool('▦', 'richTable', () => {
      this.insertTable();
    });
    this.addTool(
      '+↔',
      'richColumn',
      () => {
        this.command(addColumnAfter);
      },
      true,
    );
    this.addTool(
      '+↕',
      'richRow',
      () => {
        this.command(addRowAfter);
      },
      true,
    );
    this.addTool(
      '×▦',
      'richDeleteTable',
      () => {
        this.command(deleteTable);
      },
      true,
    );
    this.host.append(this.toolbar, this.mount, this.error, this.retry, this.cancelPaste);
  }

  private addTool(label: string, key: string, action: () => void, tableOnly = false): void {
    const button = document.createElement('button');
    button.type = 'button';
    button.textContent = label;
    button.title = button.ariaLabel = t(`smartInput.${key}`);
    button.dataset.tableOnly = tableOnly ? 'true' : 'false';
    button.addEventListener('mousedown', (event) => {
      event.preventDefault();
    });
    button.addEventListener('click', action);
    this.toolbar.append(button);
  }

  private plugins(): Plugin[] {
    return [
      history(),
      keymap({
        'Mod-z': undo,
        'Mod-Shift-z': redo,
        'Mod-y': redo,
        'Mod-b': (state, dispatch) => this.markCommand('strong')(state, dispatch),
        'Mod-i': (state, dispatch) => this.markCommand('em')(state, dispatch),
        Tab: chainCommands(moveRichTableCell(1), sinkListItem(richNode('list_item'))),
        'Shift-Tab': chainCommands(moveRichTableCell(-1), liftListItem(richNode('list_item'))),
        Enter: splitListItem(richNode('list_item')),
      }),
      keymap(baseKeymap),
      richComposerTrailingParagraph(),
      tableEditing(),
    ];
  }

  sync(sessionId: string | null, draft: SmartInputComposerDraft, active: boolean): void {
    this.host.hidden = !active;
    if (!active || !sessionId) return;
    if (!this.lifetimes.has(sessionId)) this.lifetimes.set(sessionId, new AbortController());
    const doc = getRichComposerDocument(draft);
    const saved = this.editors.get(sessionId);
    const document = this.withLegacyAttachments(sessionId, draft, doc);
    const state = this.restoreState(saved, document, draft);
    if (saved && !state.doc.eq(saved.state.doc)) {
      this.submitting.delete(sessionId);
      for (const pending of this.bookmarks)
        if (pending.sessionId === sessionId) {
          this.bookmarks.delete(pending);
          this.failures.delete(pending);
        }
    }
    const canonicalDraft = draftFromRichDocument(
      draft,
      state.doc,
      state.selection.toJSON() as Record<string, unknown>,
    );
    const generation = saved?.state.doc.eq(document) ? saved.generation : {};
    this.editors.set(sessionId, { state, draft: canonicalDraft, generation });
    this.sessionId = sessionId;
    if (!draft.richDoc) this.callbacks.changed(sessionId, canonicalDraft, false);
    if (!this.view) this.createView(state);
    else if (this.view.state !== state) this.view.updateState(state);
    for (const refresh of this.refreshers) refresh();
    this.setSubmitting(sessionId, this.submitting.has(sessionId));
    this.syncTools();
    this.renderError();
  }

  private restoreState(
    saved: SessionEditor | undefined,
    doc: ProseMirrorNode,
    draft: SmartInputComposerDraft,
  ): EditorState {
    if (!saved?.state.doc.eq(doc))
      return EditorState.create({
        schema: richComposerSchema,
        doc,
        selection: restoreRichComposerSelection(doc, draft.richDoc?.selection),
        plugins: this.plugins(),
      });
    if (JSON.stringify(saved.draft.richDoc?.selection) !== JSON.stringify(draft.richDoc?.selection))
      return saved.state.apply(
        saved.state.tr.setSelection(
          restoreRichComposerSelection(saved.state.doc, draft.richDoc?.selection),
        ),
      );
    return saved.state;
  }

  private withLegacyAttachments(
    sessionId: string,
    draft: SmartInputComposerDraft,
    doc: ProseMirrorNode,
  ): ProseMirrorNode {
    if (draft.richDoc) return doc;
    const present = new Set<string>();
    doc.descendants((node) => {
      if (node.type.name === 'attachment') present.add(String(node.attrs.referenceId));
    });
    const missing = this.callbacks
      .attachments(sessionId)
      .filter((attachment) => !present.has(attachment.id));
    if (!missing.length) return doc;
    const atoms = missing.map((attachment) =>
      richNode('attachment').create({ referenceId: attachment.id }),
    );
    return doc.copy(doc.content.append(Fragment.from(richNode('paragraph').create(null, atoms))));
  }

  private createView(state: EditorState): void {
    this.view = new EditorView(this.mount, {
      state,
      dispatchTransaction: (tr) => {
        this.dispatch(tr);
      },
      attributes: {
        'aria-label': t('smartInput.placeholder'),
        'aria-multiline': 'true',
        role: 'textbox',
      },
      handleKeyDown: (view, event) => {
        if (!richComposerOwnsShortcut(view.state, event, view.composing)) return false;
        return this.callbacks.keydown(event);
      },
      handleDOMEvents: {
        focus: () => {
          this.callbacks.focus();
          return false;
        },
      },
      clipboardTextSerializer: (slice) => {
        const doc = richDocumentFromClipboardSlice(slice);
        return draftFromRichDocument({ parts: [], nextOrdinalByKind: {} }, doc)
          .parts.map((part) =>
            part.kind === 'text'
              ? part.text
              : `[${this.resolve(part.referenceId)?.referenceLabel ?? ''}]`,
          )
          .join('');
      },
      handlePaste: (_view, event) => this.handlePaste(event),
      nodeViews: {
        attachment: (node, _view, getPos) =>
          richAttachmentView(node, getPos, {
            resolve: (id) => this.resolve(id),
            open: (id) => {
              this.open(id);
            },
            annotate: (position, id) => {
              this.annotate(position, id);
            },
            remove: (position, id) => {
              this.remove(position, id);
            },
            refreshers: this.refreshers,
          }),
      },
    });
  }

  private dispatch(tr: Transaction): void {
    const id = this.sessionId;
    const view = this.view;
    if (!id || !view) return;
    const saved = this.editors.get(id);
    if (!saved) return;
    const next = view.state.applyTransaction(tr);
    this.mapBookmarks(id, next.transactions);
    view.updateState(next.state);
    const draft = draftFromRichDocument(
      this.editors.get(id)?.draft ?? { parts: [], nextOrdinalByKind: {} },
      next.state.doc,
      next.state.selection.toJSON() as Record<string, unknown>,
    );
    const generation = saved.generation;
    this.editors.set(id, { state: next.state, draft, generation });
    this.callbacks.changed(
      id,
      draft,
      next.transactions.some((transaction) => transaction.docChanged),
    );
    this.syncTools();
  }

  private mapBookmarks(sessionId: string, transactions: readonly Transaction[]): void {
    for (const transaction of transactions) {
      for (const pending of this.bookmarks) {
        if (pending.sessionId === sessionId)
          pending.bookmark = pending.bookmark.map(transaction.mapping);
      }
    }
  }

  private syncTools(): void {
    const active = this.view ? isInTable(this.view.state) : false;
    for (const button of this.toolbar.querySelectorAll<HTMLButtonElement>(
      '[data-table-only="true"]',
    ))
      button.hidden = !active;
  }

  focus(): void {
    this.view?.focus();
  }
  hasFocus(): boolean {
    return this.view?.hasFocus() ?? false;
  }
  isSubmitting(sessionId: string): boolean {
    return this.submitting.has(sessionId);
  }
  captureDocumentGuard(sessionId: string): () => boolean {
    const generation = this.editors.get(sessionId)?.generation;
    return () => generation !== undefined && this.editors.get(sessionId)?.generation === generation;
  }
  setSubmitting(sessionId: string, value: boolean): void {
    if (value) this.submitting.add(sessionId);
    else this.submitting.delete(sessionId);
    if (this.sessionId === sessionId) {
      this.host.inert = value;
      this.host.setAttribute('aria-busy', value ? 'true' : 'false');
      this.view?.setProps({ editable: () => !this.submitting.has(sessionId) });
    }
    this.callbacks.submissionChanged();
  }
  isActive(sessionId: string): boolean {
    return !this.host.hidden && this.sessionId === sessionId;
  }
  forget(sessionId: string): void {
    this.lifetimes.get(sessionId)?.abort();
    this.lifetimes.delete(sessionId);
    this.editors.delete(sessionId);
    this.submitting.delete(sessionId);
    for (const pending of this.failures.keys())
      if (pending.sessionId === sessionId) this.failures.delete(pending);
    for (const pending of this.bookmarks)
      if (pending.sessionId === sessionId) this.bookmarks.delete(pending);
    if (this.sessionId === sessionId) {
      this.view?.destroy();
      this.view = null;
      this.sessionId = null;
    }
  }
  insertText(text: string): void {
    const view = this.view;
    if (!view) return;
    if (text === '\n')
      view.dispatch(
        view.state.tr.replaceSelectionWith(richNode('hard_break').create()).scrollIntoView(),
      );
    else view.pasteText(text);
    view.focus();
  }
  insertReferences(sessionId: string, ids: readonly string[]): void {
    if (!this.isActive(sessionId) || !this.view) return;
    const content = Fragment.fromArray(
      ids.map((referenceId) => richNode('attachment').create({ referenceId })),
    );
    this.view.dispatch(
      closeHistory(this.view.state.tr)
        .replaceSelection(new Slice(content, 0, 0))
        .scrollIntoView(),
    );
  }

  private command(command: Command): void {
    const view = this.view;
    if (!view) return;
    command(view.state, view.dispatch, view);
    view.focus();
  }
  private markCommand(name: string): Command {
    const mark = richComposerSchema.marks[name];
    return mark ? toggleMark(mark) : () => false;
  }
  private mark(name: string): void {
    this.command(this.markCommand(name));
  }
  private insertTable(): void {
    const view = this.view;
    if (!view) return;
    const rows = [0, 1].map((row) =>
      richNode('table_row').create(
        null,
        [0, 1].map(() =>
          richNode(row === 0 ? 'table_header' : 'table_cell').create(
            null,
            richNode('paragraph').create(),
          ),
        ),
      ),
    );
    const table = richNode('table').create(null, rows);
    const from = view.state.selection.from;
    const tr = closeHistory(view.state.tr).replaceSelectionWith(table);
    const positions: number[] = [];
    tr.doc.descendants((node, offset) => {
      if (node.eq(table) && offset >= from - 1) positions.push(offset + 4);
    });
    const position = positions[0];
    if (position !== undefined) tr.setSelection(TextSelection.create(tr.doc, position));
    view.dispatch(tr.scrollIntoView());
    view.focus();
  }
  private resolve(id: string): AppServerControlComposerDraftAttachment | undefined {
    return this.sessionId
      ? this.callbacks.attachments(this.sessionId).find((attachment) => attachment.id === id)
      : undefined;
  }
  private open(id: string): void {
    const attachment = this.resolve(id);
    if (attachment && this.sessionId) this.callbacks.open(this.sessionId, attachment);
  }
  private remove(position: number, id: string): void {
    const view = this.view;
    if (!view || String(view.state.doc.nodeAt(position)?.attrs.referenceId) !== id) return;
    view.dispatch(closeHistory(view.state.tr).delete(position, position + 1));
    view.focus();
  }
  private annotate(position: number, id: string): void {
    const attachment = this.resolve(id);
    const sessionId = this.sessionId;
    if (!attachment?.previewUrl || !sessionId) return;
    const saved = this.editors.get(sessionId);
    if (!saved || saved.state.doc.nodeAt(position)?.attrs.referenceId !== id) return;
    const pending = {
      sessionId,
      generation: saved.generation,
      bookmark: NodeSelection.create(saved.state.doc, position).getBookmark(),
    };
    this.bookmarks.add(pending);
    const lifetime = this.lifetimes.get(sessionId);
    const uploaded = new Map<File, AppServerControlComposerDraftAttachment>();
    const target = (): SessionEditor => {
      const current = this.editors.get(sessionId);
      if (!current || current.generation !== pending.generation || !this.bookmarks.has(pending))
        throw new Error(t('smartInput.richTargetChanged'));
      return current;
    };
    openImageAnnotation({
      src: attachment.previewUrl,
      name: attachment.displayName,
      ...(lifetime ? { signal: lifetime.signal } : {}),
      closed: () => {
        this.bookmarks.delete(pending);
      },
      save: (file, signal) =>
        this.callbacks.pending(sessionId, async () => {
          signal.throwIfAborted();
          if (!replaceBookmarkedRichAttachment(target().state, pending.bookmark, id, id))
            throw new Error(t('smartInput.richTargetChanged'));
          let edited = uploaded.get(file);
          if (!edited) {
            edited = await this.callbacks.stageFile(sessionId, file, attachment, signal);
            uploaded.set(file, edited);
          }
          signal.throwIfAborted();
          const tr = replaceBookmarkedRichAttachment(
            target().state,
            pending.bookmark,
            id,
            edited.id,
          );
          if (!tr) throw new Error(t('smartInput.richTargetChanged'));
          this.applySessionTransaction(sessionId, tr);
        }),
    });
  }

  private applySessionTransaction(sessionId: string, tr: Transaction): void {
    if (this.sessionId === sessionId && this.view) {
      this.dispatch(tr);
      return;
    }
    const saved = this.editors.get(sessionId);
    if (!saved) throw new Error(t('smartInput.richTargetChanged'));
    const applied = saved.state.applyTransaction(tr);
    this.mapBookmarks(sessionId, applied.transactions);
    const state = applied.state;
    const draft = draftFromRichDocument(
      saved.draft,
      state.doc,
      state.selection.toJSON() as Record<string, unknown>,
    );
    this.editors.set(sessionId, { state, draft, generation: saved.generation });
    this.callbacks.changed(sessionId, draft, tr.docChanged);
  }

  hasUnresolvedInput(sessionId: string): boolean {
    return Array.from(this.failures.keys()).some((pending) => pending.sessionId === sessionId);
  }

  private renderError(): void {
    const entry = Array.from(this.failures).find(
      ([pending]) => pending.sessionId === this.sessionId,
    );
    this.error.textContent = entry?.[1].message ?? '';
    this.retry.hidden = this.cancelPaste.hidden = !entry;
    this.retry.onclick = entry ? entry[1].retry : null;
    this.cancelPaste.onclick = entry
      ? () => {
          this.bookmarks.delete(entry[0]);
          this.failures.delete(entry[0]);
          this.renderError();
        }
      : null;
  }

  private pasteSlice(sessionId: string, slice: Slice, selection: Selection): void {
    const saved = this.editors.get(sessionId);
    if (!saved) throw new Error(t('smartInput.richTargetChanged'));
    pasteRichSlice(
      () => {
        const current = this.editors.get(sessionId);
        if (!current || current.generation !== saved.generation)
          throw new Error(t('smartInput.richTargetChanged'));
        return current.state;
      },
      (tr) => {
        this.applySessionTransaction(sessionId, tr);
      },
      selection,
      slice,
    );
  }

  private handlePaste(event: ClipboardEvent): boolean {
    if (!event.clipboardData || !this.sessionId) return false;
    const data = captureRichClipboard(event.clipboardData);
    if (!data.html && !data.text && !data.files.length) return false;
    const needsStaging =
      data.files.length > 0 ||
      /<img\b/i.test(data.html) ||
      (!data.html && shouldConvertPastedTextToSmartInputReference(data.text));
    if (!needsStaging && this.view) {
      try {
        const valid = new Set(
          this.callbacks.attachments(this.sessionId).map((attachment) => attachment.id),
        );
        this.pasteSlice(
          this.sessionId,
          richClipboardSlice(data, [], valid),
          this.view.state.selection,
        );
      } catch {
        this.insertClipboard(this.sessionId, data);
      }
    } else this.insertClipboard(this.sessionId, data);
    return true;
  }

  insertFiles(sessionId: string, files: readonly File[]): void {
    this.insertClipboard(sessionId, { html: '', text: '', files: [...files] });
  }

  private insertClipboard(sessionId: string, data: RichClipboardData): void {
    const saved = this.editors.get(sessionId);
    if (!saved) return;
    const pending = {
      sessionId,
      bookmark: saved.state.selection.getBookmark(),
      generation: saved.generation,
    };
    this.bookmarks.add(pending);
    const uploaded = new Map<File, AppServerControlComposerDraftAttachment>();
    let resolvedData: RichClipboardData | null = null;
    let textAttachment: AppServerControlComposerDraftAttachment | null = null;
    const signal = this.lifetimes.get(sessionId)?.signal;
    if (!signal) {
      this.bookmarks.delete(pending);
      return;
    }
    const assertTarget = (): void => {
      signal.throwIfAborted();
      if (
        this.editors.get(sessionId)?.generation !== pending.generation ||
        !this.bookmarks.has(pending)
      )
        throw new Error(t('smartInput.richTargetChanged'));
    };
    const stage = async (): Promise<{ data: RichClipboardData; ids: string[] }> => {
      assertTarget();
      const largeText =
        !data.html && !data.files.length && shouldConvertPastedTextToSmartInputReference(data.text);
      if (largeText)
        textAttachment ??= await this.callbacks.stageText(sessionId, data.text, signal);
      resolvedData ??= largeText
        ? { html: '', text: '', files: [] }
        : materializeRichClipboardImages(data);
      for (const file of resolvedData.files) {
        assertTarget();
        if (!uploaded.has(file))
          uploaded.set(file, await this.callbacks.stageFile(sessionId, file, undefined, signal));
      }
      return {
        data: resolvedData,
        ids: textAttachment
          ? [textAttachment.id]
          : richClipboardReferenceIds(resolvedData, (file) => uploaded.get(file)?.id ?? ''),
      };
    };
    const action = async (): Promise<void> => {
      try {
        signal.throwIfAborted();
        const staged = await stage();
        signal.throwIfAborted();
        const current = this.editors.get(sessionId);
        if (!current || current.generation !== pending.generation || !this.bookmarks.has(pending))
          throw new Error(t('smartInput.richTargetChanged'));
        const valid = new Set(
          this.callbacks.attachments(sessionId).map((attachment) => attachment.id),
        );
        const slice = richClipboardSlice(staged.data, staged.ids, valid);
        const selection = pending.bookmark.resolve(current.state.doc);
        this.pasteSlice(sessionId, slice, selection);
        this.failures.delete(pending);
        this.bookmarks.delete(pending);
        this.renderError();
      } catch (error) {
        if (signal.aborted || this.editors.get(sessionId)?.generation !== pending.generation)
          return;
        this.failures.set(pending, {
          message: error instanceof Error ? error.message : t('smartInput.attachmentUploadFailed'),
          retry: () => {
            const current = this.editors.get(sessionId);
            if (!current || current.generation !== pending.generation) return;
            if (!this.bookmarks.has(pending)) return;
            this.bookmarks.add(pending);
            this.failures.delete(pending);
            this.renderError();
            void this.callbacks.pending(sessionId, action);
          },
        });
        this.renderError();
      }
    };
    void this.callbacks.pending(sessionId, action);
  }
}

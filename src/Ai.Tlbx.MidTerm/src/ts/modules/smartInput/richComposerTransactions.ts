import type { Node as ProseMirrorNode, Slice } from 'prosemirror-model';
import { type EditorState, NodeSelection, Selection, Plugin, type Command, type Transaction, type SelectionBookmark } from 'prosemirror-state';
import type { EditorView } from 'prosemirror-view';
import { closeHistory } from 'prosemirror-history';
import { addRowAfter, goToNextCell, isInTable, handlePaste as pasteTable } from 'prosemirror-tables';
import { richNode } from './richComposerDocument';

export function restoreRichComposerSelection(doc: ProseMirrorNode, selection?: Record<string, unknown>): Selection {
  try { if (selection) return Selection.fromJSON(doc, selection); }
  catch { /* Pruning and older drafts can invalidate a persisted selection. */ }
  return Selection.atEnd(doc);
}

export function richComposerTrailingParagraph(): Plugin {
  return new Plugin({ appendTransaction: (transactions, _previous, state) => {
    if (!transactions.some((tr) => tr.docChanged) || state.doc.lastChild?.type.name !== 'table') return null;
    return state.tr.insert(state.doc.content.size, richNode('paragraph').create());
  } });
}

export function replaceRichAttachment(
  state: EditorState, position: number, expectedId: string, replacementId: string,
): Transaction | null {
  if (position < 0 || position >= state.doc.content.size) return null;
  const node = state.doc.nodeAt(position);
  if (node?.type.name !== 'attachment' || String(node.attrs.referenceId) !== expectedId) return null;
  const tr = closeHistory(state.tr).replaceWith(position, position + node.nodeSize,
    richNode('attachment').create({ referenceId: replacementId }));
  return tr.setSelection(NodeSelection.create(tr.doc, position));
}

export function replaceBookmarkedRichAttachment(
  state: EditorState, bookmark: SelectionBookmark, expectedId: string, replacementId: string,
): Transaction | null {
  const selection = bookmark.resolve(state.doc);
  return selection instanceof NodeSelection
    ? replaceRichAttachment(state, selection.from, expectedId, replacementId) : null;
}

export function pasteRichSlice(
  getState: () => EditorState, dispatch: (tr: Transaction) => void, selection: Selection, slice: Slice,
): void {
  dispatch(closeHistory(getState().tr).setSelection(selection));
  // prosemirror-tables' paste handler uses only state/dispatch, including CellSelection.
  // Reuse it for a saved session without mounting or retargeting a DOM EditorView.
  const target = { get state() { return getState(); }, dispatch } as EditorView;
  if (!pasteTable(target, {} as ClipboardEvent, slice)) dispatch(getState().tr.replaceSelection(slice));
}

export function moveRichTableCell(direction: -1 | 1): Command {
  return (state, dispatch) => {
    if (!isInTable(state)) return false;
    if (goToNextCell(direction)(state, dispatch)) return true;
    if (direction < 0) return false;
    if (!dispatch) return addRowAfter(state);
    let next = state;
    const added = addRowAfter(state, (tr) => { next = state.applyTransaction(tr).state; dispatch(tr); });
    return added && goToNextCell(1)(next, dispatch);
  };
}

export function richComposerOwnsShortcut(
  state: EditorState, event: Pick<KeyboardEvent, 'isComposing' | 'key'>, composing: boolean,
): boolean {
  if (composing || event.isComposing || event.key === 'Process') return false;
  if (event.key === 'Enter') {
    for (let depth = state.selection.$from.depth; depth > 0; depth -= 1)
      if (state.selection.$from.node(depth).type.name === 'list_item') return false;
  }
  return !isInTable(state) || !['Tab', 'Enter', 'ArrowUp', 'ArrowDown'].includes(event.key);
}

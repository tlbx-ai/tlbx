import type { HistoryRenderedNode, SessionAppServerControlViewState } from './types';

interface HistorySelection {
  anchor: Node;
  anchorOffset: number;
  focus: Node;
  focusOffset: number;
  rows: Map<string, HistoryRenderedNode>;
}

// Pin only the rows selected when these endpoints were established. Scrolling
// must not keep adding new rows to a selection that extends outside the history.
const selections = new WeakMap<HTMLElement, HistorySelection>();

export function captureHistorySelection(
  state: SessionAppServerControlViewState,
): HistorySelection | null {
  const canvas = state.historyCanvas;
  const selection = window.getSelection();
  if (!canvas) return null;
  if (
    !selection?.rangeCount ||
    selection.isCollapsed ||
    !selection.anchorNode ||
    !selection.focusNode ||
    (!canvas.contains(selection.anchorNode) && !canvas.contains(selection.focusNode))
  ) {
    selections.delete(canvas);
    return null;
  }
  const previous = selections.get(canvas);
  if (previous && matchesSelection(previous, selection)) return previous;
  const range = selection.getRangeAt(0);
  const next: HistorySelection = {
    anchor: selection.anchorNode,
    anchorOffset: selection.anchorOffset,
    focus: selection.focusNode,
    focusOffset: selection.focusOffset,
    rows: new Map(
      [...new Map([...(previous?.rows ?? []), ...state.historyRenderedNodes])].filter(([, row]) =>
        range.intersectsNode(row.node),
      ),
    ),
  };
  selections.set(canvas, next);
  return next;
}

function matchesSelection(saved: HistorySelection, selection: Selection): boolean {
  return (
    saved.anchor === selection.anchorNode &&
    saved.anchorOffset === selection.anchorOffset &&
    saved.focus === selection.focusNode &&
    saved.focusOffset === selection.focusOffset
  );
}

export function restoreHistorySelection(saved: HistorySelection | null): void {
  if (!saved?.anchor.isConnected || !saved.focus.isConnected) return;
  const selection = window.getSelection();
  if (!selection || matchesSelection(saved, selection)) return;
  // insertBefore can move a selected row. Restore exact endpoints/direction,
  // rather than freezing the whole viewport to protect those text nodes.
  selection.setBaseAndExtent(saved.anchor, saved.anchorOffset, saved.focus, saved.focusOffset);
}

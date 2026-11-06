import type { BoardDocument } from './boardApi';

/** Two-finger camera movement shares the exact board coordinates used by mouse and persisted geometry. */
export function bindBoardPinch(
  surface: HTMLElement,
  board: BoardDocument,
  render: () => void,
  changed: () => void,
  cancelDrag: () => void,
  signal: AbortSignal,
): void {
  const pointers = new Map<number, { x: number; y: number }>();
  const options = { signal, capture: true };
  let pinching = false;
  let previous: { x: number; y: number; distance: number } | null = null;
  const position = (): { x: number; y: number; distance: number } | null => {
    const [first, second] = [...pointers.values()];
    if (!first || !second) return null;
    const rect = surface.getBoundingClientRect();
    return {
      x: (first.x + second.x) / 2 - rect.left,
      y: (first.y + second.y) / 2 - rect.top,
      distance: Math.hypot(first.x - second.x, first.y - second.y),
    };
  };
  surface.addEventListener(
    'pointerdown',
    (event) => {
      if (event.pointerType !== 'touch') return;
      pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
      if (pointers.size < 2) return;
      cancelDrag();
      surface.classList.add('is-dragging');
      pinching = true;
      previous = position();
      for (const id of pointers.keys()) surface.setPointerCapture(id);
      event.preventDefault();
      event.stopImmediatePropagation();
    },
    options,
  );
  surface.addEventListener(
    'pointermove',
    (event) => {
      if (!pointers.has(event.pointerId)) return;
      pointers.set(event.pointerId, { x: event.clientX, y: event.clientY });
      if (!pinching) return;
      event.preventDefault();
      event.stopImmediatePropagation();
      const current = position();
      if (!current || !previous || previous.distance < 1) return;
      const next = Math.min(8, Math.max(0.05, (board.zoom * current.distance) / previous.distance));
      const ratio = next / board.zoom;
      board.panX = current.x - (previous.x - board.panX) * ratio;
      board.panY = current.y - (previous.y - board.panY) * ratio;
      board.zoom = next;
      previous = current;
      render();
    },
    options,
  );
  const finish = (event: PointerEvent): void => {
    pointers.delete(event.pointerId);
    if (!pinching) return;
    event.stopImmediatePropagation();
    previous = position();
    if (pointers.size === 0) {
      pinching = false;
      surface.classList.remove('is-dragging');
      changed();
    }
  };
  surface.addEventListener('pointerup', finish, options);
  surface.addEventListener('pointercancel', finish, options);
}

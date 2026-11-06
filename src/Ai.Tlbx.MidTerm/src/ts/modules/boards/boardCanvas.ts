import type { BoardDocument, BoardItem } from './boardApi';
import { bindBoardPinch } from './boardTouch';

export interface CanvasCallbacks {
  viewportChanged: () => void;
  itemChanged: (item: BoardItem) => void;
  createNote: (x: number, y: number) => void;
  selected: (id: string | null) => void;
}

/** CSS coordinates stay in board space; zoom is applied once to the vector plane. */
export function bindBoardCanvas(
  surface: HTMLElement,
  plane: HTMLElement,
  board: BoardDocument,
  callbacks: CanvasCallbacks,
): { render: () => void; dispose: () => void } {
  const abort = new AbortController();
  const options = { signal: abort.signal };
  let space = false;
  let drag: {
    pointer: number;
    x: number;
    y: number;
    startX: number;
    startY: number;
    item: BoardItem | undefined;
    element: HTMLElement | undefined;
    resize: boolean;
    moved: boolean;
  } | null = null;
  const render = (): void => {
    plane.style.transform = `translate(${board.panX}px, ${board.panY}px) scale(${board.zoom})`;
    surface.dispatchEvent(new CustomEvent('boardzoom', { detail: board.zoom }));
  };
  bindBoardPinch(
    surface,
    board,
    render,
    callbacks.viewportChanged,
    () => {
      if (drag?.item && drag.moved) callbacks.itemChanged(drag.item);
      drag = null;
      surface.classList.remove('is-dragging');
    },
    abort.signal,
  );
  surface.addEventListener(
    'wheel',
    (event) => {
      if (
        (event.target as HTMLElement).closest('textarea, input, iframe, .board-entry-list') &&
        !event.ctrlKey &&
        !event.metaKey
      )
        return;
      event.preventDefault();
      if (event.ctrlKey || event.metaKey) {
        const rect = surface.getBoundingClientRect();
        const x = event.clientX - rect.left;
        const y = event.clientY - rect.top;
        const next = Math.min(8, Math.max(0.05, board.zoom * Math.exp(-event.deltaY * 0.002)));
        const ratio = next / board.zoom;
        board.panX = x - (x - board.panX) * ratio;
        board.panY = y - (y - board.panY) * ratio;
        board.zoom = next;
      } else {
        board.panX -= event.deltaX;
        board.panY -= event.deltaY;
      }
      render();
      callbacks.viewportChanged();
    },
    { ...options, passive: false },
  );
  surface.addEventListener(
    'dblclick',
    (event) => {
      if (event.target !== surface && event.target !== plane) return;
      const rect = surface.getBoundingClientRect();
      callbacks.createNote(
        (event.clientX - rect.left - board.panX) / board.zoom,
        (event.clientY - rect.top - board.panY) / board.zoom,
      );
    },
    options,
  );
  surface.addEventListener(
    'keydown',
    (event) => {
      if (
        event.code === 'Space' &&
        event.target instanceof HTMLElement &&
        !event.target.closest('input,textarea,select,button,a,[contenteditable="true"]')
      ) {
        space = true;
        surface.classList.add('is-panning');
        event.preventDefault();
      }
    },
    options,
  );
  window.addEventListener(
    'keyup',
    (event) => {
      if (event.code === 'Space') {
        space = false;
        surface.classList.remove('is-panning');
      }
    },
    options,
  );
  window.addEventListener(
    'blur',
    () => {
      space = false;
      surface.classList.remove('is-panning');
    },
    options,
  );
  surface.addEventListener(
    'pointerdown',
    (event) => {
      const target = event.target as HTMLElement;
      const tile = target.closest<HTMLElement>('.board-item');
      const item = board.items.find((entry) => entry.id === tile?.dataset.id);
      if (![0, 1].includes(event.button)) return;
      if (event.button === 0 && !space) callbacks.selected(item?.id ?? null);
      const resize = !!target.closest('.board-resize');
      const pan = space || event.button === 1;
      if (item && !pan && !isItemHandle(target)) return;
      if (!item && target !== surface && target !== plane) return;
      event.preventDefault();
      surface.focus({ preventScroll: true });
      surface.setPointerCapture(event.pointerId);
      const moving = pan ? undefined : item;
      const start = dragOrigin(moving, resize, board);
      drag = {
        pointer: event.pointerId,
        x: event.clientX,
        y: event.clientY,
        startX: start.x,
        startY: start.y,
        item: moving,
        element: tile ?? undefined,
        resize,
        moved: false,
      };
      surface.classList.add('is-dragging');
    },
    options,
  );
  surface.addEventListener(
    'pointermove',
    (event) => {
      if (!drag || drag.pointer !== event.pointerId) return;
      const dx = event.clientX - drag.x;
      const dy = event.clientY - drag.y;
      if (dx === 0 && dy === 0) return;
      drag.moved = true;
      if (drag.item && drag.element) {
        if (drag.resize) {
          drag.item.width = Math.min(10000, Math.max(160, drag.startX + dx / board.zoom));
          drag.item.height = Math.min(10000, Math.max(100, drag.startY + dy / board.zoom));
          drag.element.style.width = `${drag.item.width}px`;
          drag.element.style.height = `${drag.item.height}px`;
        } else {
          drag.item.x = drag.startX + dx / board.zoom;
          drag.item.y = drag.startY + dy / board.zoom;
          drag.element.style.transform = `translate(${drag.item.x}px, ${drag.item.y}px)`;
        }
      } else {
        board.panX = drag.startX + dx;
        board.panY = drag.startY + dy;
        render();
      }
    },
    options,
  );
  const finish = (event: PointerEvent): void => {
    if (!drag || drag.pointer !== event.pointerId) return;
    if (drag.moved) {
      if (drag.item) callbacks.itemChanged(drag.item);
      else callbacks.viewportChanged();
    }
    drag = null;
    surface.classList.remove('is-dragging');
  };
  surface.addEventListener('pointerup', finish, options);
  surface.addEventListener('pointercancel', finish, options);
  render();
  return {
    render,
    dispose: () => {
      abort.abort();
      surface.classList.remove('is-dragging', 'is-panning');
    },
  };
}

function isItemHandle(target: HTMLElement): boolean {
  return !!target.closest('.board-resize, .board-item-grip');
}

function dragOrigin(
  item: BoardItem | undefined,
  resize: boolean,
  board: BoardDocument,
): { x: number; y: number } {
  if (!item) return { x: board.panX, y: board.panY };
  return resize ? { x: item.width, y: item.height } : { x: item.x, y: item.y };
}

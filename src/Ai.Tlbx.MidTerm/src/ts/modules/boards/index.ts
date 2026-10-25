import {
  BoardApiError,
  boardRequest,
  newBoardItem,
  type BoardDocument,
  type BoardItem,
} from './boardApi';
import { bindBoardCanvas } from './boardCanvas';
import { BoardMenu, type BoardMenuAction } from './boardMenu';
import { renderBoardItems } from './boardItems';
import { t } from '../i18n';
import { showConfirm } from '../../utils/dialog';
import { connectBoardSocket, type BoardBrowserInstruction } from './boardSocket';
import {
  executeBoardBrowserInstruction,
  mountBoardBrowser,
  resetBoardBrowser,
} from './boardBrowser';
import { getWebPreviewTarget } from '../web/webApi';
import { showBoardConflict } from './boardConflict';
import { BoardWindows } from './boardWindows';
import {
  BoardSessions,
  hasBoardSessionSettings,
  renderBoardLaunches,
  renderBoardSettings,
} from './boardSession';
import { registerBackButtonLayer } from '../navigation/backButtonGuard';
import {
  $actionGraphsOpen,
  $boardsOpen,
  $operatorOpen,
  $sessionList,
  $settingsOpen,
} from '../../stores';
import { closeSettings } from '../settings';
import { closeOperatorView } from '../operator';
import { closeActionGraphsView } from '../actionGraphs';
import { isBoardEditing, trapBoardFocus } from './boardKeys';

let view: BoardView | null = null;

export function initBoards(onSelectSession: (id: string) => void): void {
  for (const state of [$settingsOpen, $operatorOpen, $actionGraphsOpen]) {
    state.subscribe((open) => {
      if (open) closeBoardsView();
    });
  }
  const requestedSession = new URLSearchParams(location.search).get('session');
  if (requestedSession) {
    let selected = false;
    $sessionList.subscribe((sessions) => {
      if (selected || !sessions.some((session) => session.id === requestedSession)) return;
      selected = true;
      queueMicrotask(() => {
        onSelectSession(requestedSession);
      });
    });
  }
  document.getElementById('btn-boards')?.addEventListener('click', () => {
    view ??= new BoardView();
    void view.open();
  });
}

export function closeBoardsView(): void {
  void view?.close(false);
}

export async function initDetachedBoard(): Promise<boolean> {
  const id = new URLSearchParams(location.search).get('board');
  if (!id) return false;
  view = new BoardView(true);
  await view.open(id);
  return true;
}

class BoardView {
  private readonly root = document.createElement('section');
  private readonly surface = document.createElement('div');
  private readonly plane = document.createElement('div');
  private readonly picker = document.createElement('select');
  private readonly title = document.createElement('input');
  private readonly status = document.createElement('span');
  private readonly contextMenu = new BoardMenu(this.root, 'boards-context-menu');
  private readonly actionsMenu = new BoardMenu(this.root, 'boards-actions-menu');
  private readonly menuButton = this.button(
    '⋯',
    () => {
      this.showActions();
    },
    'menu',
  );
  private readonly empty = document.createElement('div');
  private readonly inspector = document.createElement('aside');
  private board: BoardDocument | null = null;
  private canvas: ReturnType<typeof bindBoardCanvas> | null = null;
  private poll: ReturnType<typeof setInterval> | null = null;
  private saveTimer: ReturnType<typeof setTimeout> | null = null;
  private readonly dirty = new Map<string, number>();
  private viewportDirty = 0;
  private titleDirty = 0;
  private saving = false;
  private saveOperation: Promise<void> | null = null;
  private refreshing = false;
  private refreshQueued = false;
  private selected: string | null = null;
  private generation = 0;
  private loadGeneration = 0;
  private readonly windows: BoardWindows;
  private readonly sessions = new BoardSessions();
  private removeBackLayer: (() => void) | null = null;
  private socket: ReturnType<typeof connectBoardSocket> | null = null;
  private conflict: { item: BoardItem; remote: BoardItem | null } | null = null;
  private titleConflict: BoardDocument | null = null;
  private conflictOverlay: (() => void) | null = null;
  private readonly resolveButton = this.button(t('boards.resolve'), () => {
    this.presentConflict();
  });

  constructor(private readonly detached = false) {
    this.windows = new BoardWindows({
      boardId: () => this.board?.id ?? null,
      save: async () => {
        await this.flush();
        return !this.hasPendingChanges();
      },
      detached: async (id) => {
        if (!this.detached && this.board?.id === id) await this.close();
      },
      dock: (id) => this.open(id, true),
      error: (error) => {
        this.error(error);
      },
    });
    this.root.className = 'boards-workspace';
    this.root.classList.toggle('boards-detached', detached);
    this.root.setAttribute('role', 'region');
    this.root.setAttribute('aria-label', t('boards.title'));
    this.root.hidden = true;
    const header = document.createElement('header');
    header.className = 'boards-header';
    const brand = document.createElement('span');
    brand.className = 'session-cwd';
    brand.textContent = t('boards.title');
    this.picker.setAttribute('aria-label', t('boards.chooseBoard'));
    this.picker.addEventListener('change', () => {
      void this.open(this.picker.value);
    });
    this.title.className = 'boards-title';
    this.title.hidden = true;
    this.title.setAttribute('aria-label', t('boards.boardTitle'));
    this.title.maxLength = 256;
    this.title.addEventListener('change', () => {
      if (this.board && this.title.value.trim()) {
        this.board.title = this.title.value.trim();
        this.titleDirty = ++this.generation;
        this.schedule();
        this.syncPickerTitle();
      }
    });
    this.title.addEventListener('blur', () => {
      this.endRename();
    });
    this.title.addEventListener('keydown', (event) => {
      if (!['Enter', 'Escape'].includes(event.key)) return;
      event.stopPropagation();
      event.preventDefault();
      if (event.key === 'Escape') this.title.value = this.board?.title ?? '';
      this.title.blur();
      this.picker.focus();
    });
    this.status.className = 'boards-save-state';
    this.status.setAttribute('role', 'status');
    this.resolveButton.hidden = true;
    this.menuButton.className = 'ide-bar-btn boards-menu-trigger';
    this.menuButton.setAttribute('aria-label', t('boards.actions'));
    this.menuButton.setAttribute('aria-haspopup', 'menu');
    this.menuButton.setAttribute('aria-expanded', 'false');
    const identity = document.createElement('div');
    identity.className = 'boards-identity';
    const rename = this.button(
      '✎',
      () => {
        this.beginRename();
      },
      'rename',
    );
    rename.setAttribute('aria-label', t('boards.rename'));
    rename.title = t('boards.rename');
    const create = this.button(
      '+',
      () => {
        void this.createBoard();
      },
      'new',
    );
    create.setAttribute('aria-label', t('boards.newBoard'));
    create.title = t('boards.newBoard');
    identity.append(this.picker, this.title, rename, create);
    header.append(brand, identity, this.status, this.resolveButton, this.menuButton);
    this.surface.className = 'boards-surface';
    this.surface.tabIndex = 0;
    this.surface.setAttribute('aria-label', t('boards.canvasHint'));
    this.plane.className = 'boards-plane';
    this.empty.className = 'boards-empty';
    this.empty.hidden = true;
    const firstItem = this.button(t('boards.addItem'), () => {
      this.showCreationMenu();
    });
    const hint = document.createElement('p');
    hint.className = 'boards-pointer-hint';
    hint.textContent = t('boards.canvasHint');
    const touchHint = document.createElement('p');
    touchHint.className = 'boards-touch-hint';
    touchHint.textContent = t('boards.canvasTouchHint');
    this.empty.append(firstItem, hint, touchHint);
    this.surface.append(this.plane, this.empty);
    this.surface.addEventListener('contextmenu', (event) => {
      if (event.target !== this.surface && event.target !== this.plane) return;
      event.preventDefault();
      this.showCreationMenu(event.clientX, event.clientY);
    });
    this.surface.addEventListener('keydown', (event) => {
      if (event.target !== this.surface) return;
      if (event.key !== 'ContextMenu' && !(event.shiftKey && event.key === 'F10')) return;
      event.preventDefault();
      event.stopPropagation();
      this.showCreationMenu();
    });
    this.inspector.className = 'boards-inspector';
    this.inspector.hidden = true;
    this.root.append(header, this.surface, this.inspector);
    const host = detached ? document.body : document.querySelector('.terminals-area');
    if (!host) throw new Error('Boards workspace host is missing.');
    host.append(this.root);
    window.addEventListener('beforeunload', (event) => {
      if (!this.dirty.size && !this.viewportDirty && !this.titleDirty) return;
      event.preventDefault();
    });
    this.root.addEventListener('keydown', (event) => {
      event.stopPropagation();
      if (this.detached) trapBoardFocus(this.root, event);
      this.handleCanvasKey(event);
      if (event.key === 'Escape') {
        if (!this.inspector.hidden) this.hideInspector();
        else if (this.selected) this.select(null);
        else void this.close();
        event.stopPropagation();
      }
    });
    this.root.addEventListener('focusout', () => {
      queueMicrotask(() => {
        void this.refresh();
      });
    });
  }

  async open(id?: string, takeControl = false): Promise<boolean> {
    const generation = ++this.loadGeneration;
    if (this.board && this.hasPendingChanges()) {
      await this.flush();
      if (this.hasPendingChanges() || generation !== this.loadGeneration) return false;
    }
    this.endRename();
    this.contextMenu.close(false);
    this.actionsMenu.close(false);
    this.root.hidden = false;
    this.showPane();
    this.removeBackLayer ??= registerBackButtonLayer(() => {
      void this.close();
    });
    this.surface.inert = true;
    this.title.disabled = true;
    this.root.setAttribute('aria-busy', 'true');
    try {
      const loaded = await this.readBoard(id, generation);
      if (!loaded || generation !== this.loadGeneration) return false;
      return await this.activateBoard(loaded.board, loaded.boards, takeControl, generation);
    } catch (error) {
      this.error(error);
      this.render();
      return false;
    } finally {
      if (generation === this.loadGeneration) {
        this.surface.inert = false;
        this.title.disabled = false;
        this.root.setAttribute('aria-busy', 'false');
        if (this.refreshQueued) void this.refresh();
      }
    }
  }

  private beginRename(): void {
    this.picker.hidden = true;
    this.title.hidden = false;
    this.title.value = this.board?.title ?? '';
    this.title.focus();
    this.title.select();
  }

  private endRename(): void {
    this.title.hidden = true;
    this.picker.hidden = false;
  }

  private showCreationMenu(x?: number, y?: number): void {
    this.actionsMenu.close(false);
    const rect = this.surface.getBoundingClientRect();
    const clientX = x ?? rect.left + rect.width / 2;
    const clientY = y ?? rect.top + rect.height / 2;
    const board = this.board;
    if (!board) return;
    const boardX = (clientX - rect.left - board.panX) / board.zoom - (x === undefined ? 160 : 0);
    const boardY = (clientY - rect.top - board.panY) / board.zoom - (y === undefined ? 100 : 0);
    const kinds: BoardItem['kind'][] = ['note', 'todo', 'calendar', 'mail', 'timeline', 'browser'];
    const actions: BoardMenuAction[] = kinds.map((kind) => ({
      label: t(`boards.${kind}`),
      id: kind,
      run: () => {
        void this.add(kind, boardX, boardY);
      },
    }));
    actions.push({
      id: 'reset',
      label: `${t('boards.resetView')} (${Math.round(board.zoom * 100)}%)`,
      run: () => {
        this.resetView();
      },
    });
    this.contextMenu.open(actions, clientX, clientY, this.surface);
  }

  private showActions(): void {
    if (this.actionsMenu.isOpen) {
      this.actionsMenu.close();
      return;
    }
    this.contextMenu.close(false);
    const rect = this.menuButton.getBoundingClientRect();
    const dock = this.windows.canDock;
    const actions: BoardMenuAction[] = [
      {
        id: 'add',
        label: t('boards.addItem'),
        run: () => {
          this.showCreationMenu();
        },
      },
      {
        id: dock ? 'dock' : 'detach',
        label: t(dock ? 'boards.dock' : 'boards.detach'),
        run: () => {
          void (dock ? this.windows.dock() : this.windows.detach()).catch((error: unknown) => {
            this.error(error);
          });
        },
      },
      {
        id: 'settings',
        label: t('boards.boardSettings'),
        run: () => {
          void this.settings();
        },
      },
      {
        id: 'reset',
        label: `${t('boards.resetView')} (${Math.round((this.board?.zoom ?? 1) * 100)}%)`,
        run: () => {
          this.resetView();
        },
      },
    ];
    this.actionsMenu.open(actions, rect.right - 200, rect.bottom, this.menuButton);
  }

  private resetView(): void {
    if (!this.board) return;
    this.board.zoom = 1;
    this.board.panX = 80;
    this.board.panY = 80;
    this.canvas?.render();
    this.markViewport();
    this.surface.focus();
  }

  private showPane(): void {
    if (this.detached) return;
    closeSettings();
    closeOperatorView();
    closeActionGraphsView();
    this.setPaneOpen(true);
    document.getElementById('app')?.classList.remove('sidebar-open');
  }

  private setPaneOpen(open: boolean): void {
    this.root.parentElement?.classList.toggle('boards-open', open);
    document.getElementById('btn-boards')?.classList.toggle('active', open);
    $boardsOpen.set(open);
  }

  private async readBoard(
    id: string | undefined,
    generation: number,
  ): Promise<{ board: BoardDocument; boards: BoardDocument[] } | null> {
    const boards = await boardRequest<BoardDocument[]>();
    if (generation !== this.loadGeneration) return null;
    const next = id ?? this.board?.id ?? boards[0]?.id;
    if (next) return { board: await boardRequest<BoardDocument>(`/${next}`), boards };
    const board = await boardRequest<BoardDocument>('', 'POST', {
      title: t('boards.untitled'),
      panX: 80,
      panY: 80,
    });
    boards.unshift(board);
    return { board, boards };
  }

  private async activateBoard(
    board: BoardDocument,
    boards: BoardDocument[],
    takeControl: boolean,
    generation: number,
  ): Promise<boolean> {
    this.canvas?.dispose();
    this.socket?.dispose();
    this.board = board;
    this.selected = null;
    this.inspector.hidden = true;
    this.title.value = board.title;
    this.picker.replaceChildren(
      ...boards.map((entry) => {
        const option = document.createElement('option');
        option.value = entry.id;
        option.textContent = entry.title;
        return option;
      }),
    );
    this.picker.value = board.id;
    const socket = connectBoardSocket(
      board.id,
      () => {
        void this.refresh();
      },
      (instruction) => this.handleBrowserInstruction(instruction),
    );
    this.socket = socket;
    this.bindCanvas(board);
    await socket.ready;
    if (generation !== this.loadGeneration) return false;
    const query = new URLSearchParams(location.search);
    if (takeControl || (query.get('board') === board.id && query.get('handoff') === '1')) {
      for (const item of board.items.filter((entry) => entry.kind === 'browser')) {
        await boardRequest(`/${board.id}/items/${item.id}/browser-claim`, 'POST', {});
        if (generation !== this.loadGeneration) return false;
      }
      for (const frame of this.plane.querySelectorAll<HTMLIFrameElement>('iframe'))
        resetBoardBrowser(frame);
    }
    this.render();
    if (!this.poll)
      this.poll = setInterval(() => {
        void this.refresh();
      }, 30000);
    if (this.detached) {
      const locationUrl = new URL(location.href);
      locationUrl.searchParams.set('board', board.id);
      history.replaceState(history.state, '', locationUrl);
    }
    this.windows.announceReady(board.id);
    this.title.disabled = false;
    this.picker.focus();
    const targetId = query.get('board') === board.id ? query.get('item') : null;
    const target = board.items.find((item) => item.id === targetId);
    if (target) {
      board.panX = this.surface.clientWidth / 2 - (target.x + target.width / 2) * board.zoom;
      board.panY = this.surface.clientHeight / 2 - (target.y + target.height / 2) * board.zoom;
      this.canvas?.render();
      this.select(target.id);
    }
    return true;
  }

  private bindCanvas(board: BoardDocument): void {
    this.canvas = bindBoardCanvas(this.surface, this.plane, board, {
      viewportChanged: () => {
        this.markViewport();
      },
      itemChanged: (item) => {
        this.markItem(item);
      },
      createNote: (x, y) => {
        void this.add('note', x, y);
      },
      selected: (id) => {
        this.select(id);
      },
    });
  }

  private handleCanvasKey(event: KeyboardEvent): void {
    if (isBoardEditing(event) || !this.board) return;
    const item = this.board.items.find((entry) => entry.id === this.selected);
    if (event.key.toLowerCase() === 'n' && !event.ctrlKey && !event.metaKey) {
      event.preventDefault();
      void this.add('note');
    } else if (item && ['Delete', 'Backspace'].includes(event.key)) {
      event.preventDefault();
      void this.remove(item);
    } else if (item && event.key.startsWith('Arrow')) {
      event.preventDefault();
      const step = event.shiftKey ? 10 : 1;
      if (event.key === 'ArrowLeft') item.x -= step;
      if (event.key === 'ArrowRight') item.x += step;
      if (event.key === 'ArrowUp') item.y -= step;
      if (event.key === 'ArrowDown') item.y += step;
      this.markItem(item);
      this.render();
    }
  }
  private async createBoard(): Promise<void> {
    try {
      await this.flush();
      if (this.hasPendingChanges()) return;
      const board = await boardRequest<BoardDocument>('', 'POST', {
        title: t('boards.untitled'),
        panX: 80,
        panY: 80,
      });
      await this.open(board.id);
      this.beginRename();
    } catch (error) {
      this.error(error);
    }
  }

  private async add(kind: BoardItem['kind'], x?: number, y?: number): Promise<void> {
    if (!this.board) return;
    const board = this.board;
    const item = newBoardItem(
      kind,
      x ?? (this.surface.clientWidth / 2 - board.panX) / board.zoom - 160,
      y ?? (this.surface.clientHeight / 2 - board.panY) / board.zoom - 100,
    );
    item.z = Math.max(0, ...board.items.map((entry) => entry.z)) + 1;
    try {
      const created = await boardRequest<BoardItem>(`/${board.id}/items`, 'POST', { item });
      if (this.board !== board) return;
      if (!board.items.some((entry) => entry.id === created.id)) board.items.push(created);
      this.render();
      this.select(created.id);
      this.plane
        .querySelector<HTMLInputElement | HTMLTextAreaElement>(
          `[data-id="${created.id}"] ${kind === 'note' ? '.board-item-text' : '.board-item-title'}`,
        )
        ?.focus();
    } catch (error) {
      this.error(error);
    }
  }

  private render(): void {
    this.empty.hidden = !this.board || this.board.items.length > 0;
    if (!this.board) return;
    this.syncPickerTitle();
    renderBoardItems(this.plane, this.board.items, this.board.id, {
      changed: (item) => {
        this.markItem(item);
      },
      remove: (item) => {
        void this.remove(item);
      },
      launch: (item, entryId) => {
        void this.launch(item, entryId);
      },
      settings: (item) => {
        this.select(item.id, true);
      },
      claim: (item) => {
        void this.takeBrowserControl(item).catch((error: unknown) => {
          this.error(error);
        });
      },
    });
  }

  private async takeBrowserControl(item: BoardItem): Promise<void> {
    const board = this.board;
    if (!board) return;
    if (!item.url && !item.localDirectory && !item.browserSessionId) {
      this.select(item.id, true);
      return;
    }
    await boardRequest(`/${board.id}/items/${item.id}/browser-claim`, 'POST', {});
    const target = await getWebPreviewTarget(
      item.browserSessionId ?? `board-${board.id}`,
      item.browserPreviewName ?? item.id,
    );
    const frame = this.plane.querySelector<HTMLIFrameElement>(`[data-id="${item.id}"] iframe`);
    if (frame) await mountBoardBrowser(frame, board.id, item, target ?? undefined);
  }

  private select(id: string | null, inspect = false): void {
    if (this.selected === id && !inspect) return;
    this.selected = id;
    for (const tile of this.plane.querySelectorAll<HTMLElement>('.board-item'))
      tile.classList.toggle('is-selected', tile.dataset.id === id);
    const item = this.board?.items.find((entry) => entry.id === id);
    this.inspector.hidden = !item || !inspect;
    if (!item || !inspect) return;
    this.renderInspector(item);
  }

  private renderInspector(item: BoardItem): void {
    this.inspector.replaceChildren();
    const heading = document.createElement('strong');
    heading.textContent = t('boards.itemSettings');
    this.inspector.append(
      heading,
      this.button('×', () => {
        this.select(null);
      }),
    );
    const fields = [
      'url',
      'localDirectory',
      'workingDirectory',
      'launchCommand',
      'sessionProfile',
      'prompt',
    ] as const;
    for (const field of fields) {
      if ((field === 'url' || field === 'localDirectory') && item.kind !== 'browser') continue;
      const label = document.createElement('label');
      label.textContent = t(`boards.${field}`);
      const input =
        field === 'prompt' ? document.createElement('textarea') : document.createElement('input');
      input.name = field;
      input.value = item[field] ?? '';
      input.addEventListener('change', () => {
        item[field] = input.value || null;
        this.markItem(item);
        this.render();
      });
      label.append(input);
      this.inspector.append(label);
    }
    const palette = document.createElement('div');
    palette.className = 'boards-palette';
    for (const color of ['sand', 'rose', 'sage', 'blue', 'plain']) {
      const swatch = this.button('', () => {
        item.color = color;
        this.markItem(item);
        this.render();
      });
      swatch.dataset.color = color;
      swatch.title = t(`boards.color.${color}`);
      swatch.setAttribute('aria-label', swatch.title);
      palette.append(swatch);
    }
    this.inspector.append(palette);
  }

  private async settings(): Promise<void> {
    await this.flush();
    const board = this.board;
    if (!board || this.hasPendingChanges()) return;
    renderBoardSettings(
      this.inspector,
      board,
      async (patch) => {
        const saved = await boardRequest<BoardDocument>(`/${board.id}`, 'PATCH', {
          expectedRevision: board.revision,
          ...patch,
        });
        Object.assign(board, {
          ...saved,
          items: board.items,
          panX: board.panX,
          panY: board.panY,
          zoom: board.zoom,
        });
      },
      () => {
        this.hideInspector();
      },
      (error) => {
        this.error(error);
      },
      () => {
        void this.deleteBoard();
      },
    );
  }

  private async deleteBoard(): Promise<void> {
    const board = this.board;
    if (
      !board ||
      !(await showConfirm(t('boards.confirmDeleteBoard'), {
        title: t('boards.deleteBoard'),
        confirmLabel: t('boards.deleteBoard'),
        danger: true,
      }))
    )
      return;
    try {
      await this.flush();
      if (this.hasPendingChanges()) return;
      await boardRequest(`/${board.id}?expectedRevision=${board.revision}`, 'DELETE');
      this.board = null;
      await this.open();
    } catch (error) {
      this.error(error);
    }
  }

  private async launch(item: BoardItem, entryId?: string, force = false): Promise<void> {
    const board = this.board;
    if (!board) return;
    this.select(item.id);
    const show = (): void => {
      renderBoardLaunches(
        this.inspector,
        item,
        () => {
          void this.launch(item, entryId, true);
        },
        () => {
          this.hideInspector();
        },
      );
    };
    if (!force && item.launches?.some((launch) => (launch.entryId ?? undefined) === entryId)) {
      show();
      return;
    }
    try {
      await this.flush();
      if (this.hasPendingChanges()) return;
      if (!hasBoardSessionSettings(board, item)) {
        await this.settings();
        return;
      }
      this.status.textContent = t('boards.launchState.starting');
      const launch = await this.sessions.launch(board, item, entryId);
      if (!launch || this.board !== board) return;
      this.status.textContent = t(`boards.launchState.${launch.state}`);
      show();
    } catch (error) {
      this.error(error);
    }
  }

  private hideInspector(): void {
    this.inspector.hidden = true;
    this.surface.focus({ preventScroll: true });
  }

  private async remove(item: BoardItem): Promise<void> {
    if (
      !this.board ||
      !(await showConfirm(t('boards.confirmDelete'), {
        title: t('boards.deleteItem'),
        confirmLabel: t('boards.deleteItem'),
        danger: true,
      }))
    )
      return;
    await this.flush();
    if (this.dirty.has(item.id)) return;
    try {
      await boardRequest(
        `/${this.board.id}/items/${item.id}?expectedRevision=${item.revision}`,
        'DELETE',
      );
      this.board.items = this.board.items.filter((entry) => entry.id !== item.id);
      this.select(null);
      this.render();
    } catch (error) {
      this.error(error);
    }
  }

  private markItem(item: BoardItem): void {
    this.dirty.set(item.id, ++this.generation);
    this.schedule();
  }

  private markViewport(): void {
    this.viewportDirty = ++this.generation;
    this.schedule();
  }

  private schedule(): void {
    this.status.textContent = t('boards.saving');
    this.status.dataset.state = 'saving';
    if (this.saveTimer) clearTimeout(this.saveTimer);
    this.saveTimer = setTimeout(() => {
      void this.flush();
    }, 450);
  }

  private flush(): Promise<void> {
    if (this.saveOperation) return this.saveOperation;
    this.saveOperation = this.performFlush().finally(() => {
      this.saveOperation = null;
    });
    return this.saveOperation;
  }

  private async performFlush(): Promise<void> {
    if (this.saving || !this.board || this.conflict || this.titleConflict) return;
    this.saving = true;
    const board = this.board;
    let activeItem: BoardItem | null = null;
    try {
      for (const [id, generation] of this.dirty) {
        const item = board.items.find((entry) => entry.id === id);
        if (!item) {
          this.dirty.delete(id);
          continue;
        }
        activeItem = item;
        const saved = await boardRequest<BoardItem>(`/${board.id}/items/${id}`, 'PUT', {
          expectedRevision: item.revision,
          item,
        });
        item.revision = saved.revision;
        if (this.dirty.get(id) === generation) this.dirty.delete(id);
      }
      activeItem = null;
      await this.flushTitle(board);
      if (this.viewportDirty) {
        const generation = this.viewportDirty;
        await boardRequest(`/${board.id}/viewport`, 'PATCH', {
          panX: board.panX,
          panY: board.panY,
          zoom: board.zoom,
        });
        if (this.viewportDirty === generation) this.viewportDirty = 0;
      }
      this.syncPickerTitle();
      this.status.textContent = t('boards.saved');
      this.status.dataset.state = 'saved';
      if (this.hasPendingChanges()) this.schedule();
    } catch (error) {
      await this.handleSaveError(error, activeItem);
    } finally {
      this.saving = false;
      if (this.refreshQueued) void this.refresh();
    }
  }

  private hasPendingChanges(): boolean {
    return this.dirty.size > 0 || this.viewportDirty !== 0 || this.titleDirty !== 0;
  }

  private syncPickerTitle(): void {
    const option = this.picker.selectedOptions[0];
    if (option && this.board) option.textContent = this.board.title;
  }

  private async flushTitle(board: BoardDocument): Promise<void> {
    if (!this.titleDirty) return;
    const generation = this.titleDirty;
    const saved = await boardRequest<BoardDocument>(`/${board.id}`, 'PATCH', {
      expectedRevision: board.revision,
      title: board.title,
    });
    board.revision = saved.revision;
    if (this.titleDirty === generation) this.titleDirty = 0;
  }
  private async handleSaveError(error: unknown, item: BoardItem | null): Promise<void> {
    if (!item && this.titleDirty && error instanceof BoardApiError && error.status === 409) {
      try {
        if (!this.board) return;
        this.titleConflict = await boardRequest<BoardDocument>(`/${this.board.id}`);
        this.resolveButton.hidden = false;
        this.status.textContent = t('boards.titleConflict');
        this.status.dataset.state = 'conflict';
        this.presentConflict();
      } catch (readError) {
        this.error(readError);
      }
      return;
    }
    if (item && error instanceof BoardApiError && [404, 409].includes(error.status)) {
      try {
        const remote = await this.readConflictItem(item.id);
        this.conflict = { item: item, remote };
        this.resolveButton.hidden = false;
        this.status.textContent = t('boards.conflictTitle');
        this.status.dataset.state = 'conflict';
        this.presentConflict();
      } catch (readError) {
        this.error(readError);
      }
    } else this.error(error);
  }

  private async readConflictItem(id: string): Promise<BoardItem | null> {
    if (!this.board) return null;
    try {
      return await boardRequest<BoardItem>(`/${this.board.id}/items/${id}`);
    } catch (error) {
      if (error instanceof BoardApiError && error.status === 404) return null;
      throw error;
    }
  }

  private presentConflict(): void {
    this.conflictOverlay?.();
    if (this.titleConflict) {
      this.presentTitleConflict(this.titleConflict);
      return;
    }
    const conflict = this.conflict;
    if (!conflict) return;
    this.conflictOverlay = showBoardConflict(this.root, conflict.item, conflict.remote, {
      keepMine: async () => {
        if (!conflict.remote) return;
        conflict.item.revision = conflict.remote.revision;
        this.clearConflict();
        await this.flush();
      },
      useServer: async () => {
        await this.acceptServerItem(conflict.item);
      },
      saveCopy: async () => {
        if (!this.board) return;
        const item = {
          ...structuredClone(conflict.item),
          id: '',
          revision: 0,
          x: conflict.item.x + 24,
          y: conflict.item.y + 24,
        };
        const copy = await boardRequest<BoardItem>(`/${this.board.id}/items`, 'POST', { item });
        await this.acceptServerItem(conflict.item);
        if (!this.board.items.some((entry) => entry.id === copy.id)) this.board.items.push(copy);
        this.render();
        this.select(copy.id);
      },
      dismiss: () => {
        this.conflictOverlay?.();
        this.conflictOverlay = null;
        this.resolveButton.focus();
      },
    });
  }

  private clearConflict(): void {
    this.conflictOverlay?.();
    this.conflictOverlay = null;
    this.conflict = null;
    this.titleConflict = null;
    this.resolveButton.hidden = true;
  }

  private presentTitleConflict(remote: BoardDocument): void {
    const board = this.board;
    if (!board) return;
    this.conflictOverlay = showBoardConflict(
      this.root,
      { title: board.title, text: '', entries: [] },
      { title: remote.title, text: '', entries: [] },
      {
        titleKey: 'boards.titleConflict',
        hintKey: 'boards.titleConflictHint',
        keepMine: async () => {
          board.revision = remote.revision;
          this.clearConflict();
          await this.flush();
        },
        useServer: async () => {
          const latest = await boardRequest<BoardDocument>(`/${board.id}`);
          board.title = latest.title;
          board.revision = latest.revision;
          this.title.value = latest.title;
          this.titleDirty = 0;
          this.clearConflict();
          await this.flush();
        },
        dismiss: () => {
          this.conflictOverlay?.();
          this.conflictOverlay = null;
          this.resolveButton.focus();
        },
      },
    );
  }

  private async acceptServerItem(item: BoardItem): Promise<void> {
    const remote = await this.readConflictItem(item.id);
    if (!this.board) return;
    if (remote) Object.assign(item, remote);
    else this.board.items = this.board.items.filter((entry) => entry.id !== item.id);
    this.dirty.delete(item.id);
    this.clearConflict();
    this.picker.focus();
    this.select(null);
    this.render();
    this.status.textContent = t('boards.saved');
    this.status.dataset.state = 'saved';
    await this.flush();
  }

  private async refresh(): Promise<void> {
    if (!this.board || this.root.hidden) return;
    this.refreshQueued = true;
    if (this.refreshing || !this.canReadBoard()) return;
    this.refreshQueued = false;
    this.refreshing = true;
    const board = this.board;
    try {
      const fresh = await boardRequest<BoardDocument>(`/${board.id}`);
      if (!this.canApplyRefresh(board)) {
        this.refreshQueued = true;
        return;
      }
      const existing = new Map(board.items.map((item) => [item.id, item]));
      const editingId = this.editingItemId();
      const items = fresh.items.map((item) => {
        const previous = existing.get(item.id);
        if (previous && item.id === editingId) return previous;
        return previous ? Object.assign(previous, item) : item;
      });
      const editing = editingId ? existing.get(editingId) : undefined;
      if (editing && !items.some((item) => item.id === editingId)) items.push(editing);
      board.items = items;
      if (!items.some((item) => item.id === this.selected)) this.select(null);
      if (fresh.revision !== board.revision && document.activeElement !== this.title) {
        const camera = { panX: board.panX, panY: board.panY, zoom: board.zoom };
        Object.assign(board, { ...fresh, ...camera, items });
        this.title.value = board.title;
        this.canvas?.render();
      }
      this.render();
    } catch (error) {
      this.error(error);
    } finally {
      this.refreshing = false;
      if (this.refreshQueued) void this.refresh();
    }
  }

  async close(restoreFocus = true): Promise<void> {
    if (this.root.hidden) return;
    await this.flush();
    if (this.saving || this.hasPendingChanges()) return;
    ++this.loadGeneration;
    if (this.detached) {
      if (window.opener) window.close();
      else location.assign('/');
      return;
    }
    this.root.hidden = true;
    this.contextMenu.close(false);
    this.actionsMenu.close(false);
    this.setPaneOpen(false);
    this.removeBackLayer?.();
    this.removeBackLayer = null;
    for (const frame of this.plane.querySelectorAll<HTMLIFrameElement>('iframe')) {
      resetBoardBrowser(frame);
      frame.src = 'about:blank';
    }
    this.canvas?.dispose();
    this.canvas = null;
    this.socket?.dispose();
    this.socket = null;
    if (this.poll) clearInterval(this.poll);
    this.poll = null;
    if (restoreFocus) document.getElementById('btn-boards')?.focus();
  }

  private canApplyRefresh(board: BoardDocument): boolean {
    return this.board === board && this.canReadBoard();
  }

  private canReadBoard(): boolean {
    return (
      !this.saving &&
      !this.dirty.size &&
      !this.viewportDirty &&
      !this.titleDirty &&
      !this.surface.inert &&
      !this.surface.classList.contains('is-dragging') &&
      !this.inspector.contains(document.activeElement)
    );
  }

  private editingItemId(): string | null {
    const active = document.activeElement;
    if (
      active instanceof HTMLTextAreaElement ||
      (active instanceof HTMLInputElement && active.type !== 'checkbox')
    )
      return active.closest<HTMLElement>('.board-item')?.dataset.id ?? null;
    return null;
  }

  private async handleBrowserInstruction(message: BoardBrowserInstruction): Promise<void> {
    let failure: string | undefined;
    try {
      if (!this.board) throw new Error('This board is closed.');
      await executeBoardBrowserInstruction(this.plane, this.board, message);
    } catch (error) {
      failure = error instanceof Error ? error.message : String(error);
    }
    if (message.requestId) {
      const response = await fetch('/api/browser/ui-result', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          requestId: message.requestId,
          command: message.command,
          success: !failure,
          error: failure,
        }),
      });
      if (!response.ok) this.error(new Error('Browser command acknowledgement failed.'));
    }
  }

  private error(error: unknown): void {
    this.status.dataset.state = 'error';
    this.status.textContent = `${t('boards.saveError')} ${error instanceof Error ? error.message : String(error)}`;
    this.status.title = this.status.textContent;
  }

  private button(label: string, action: () => void, key?: string): HTMLButtonElement {
    const element = document.createElement('button');
    element.type = 'button';
    element.textContent = label;
    if (key) element.dataset.boardAction = key;
    element.addEventListener('click', action);
    return element;
  }
}

import { wrapConnectWorkspaceUrl } from '../web/connectPreview';

interface BoardWindowActions {
  boardId: () => string | null;
  save: () => Promise<boolean>;
  detached: (boardId: string) => Promise<void>;
  dock: (boardId: string) => Promise<boolean>;
  error: (error: unknown) => void;
}

/** Owns only windows opened by this board controller. Messages require exact same-origin window identity. */
export class BoardWindows {
  private readonly popups = new Map<Window, { boardId: string; pending: boolean }>();
  private readonly opener: Window | null;
  readonly canDock: boolean;

  constructor(private readonly actions: BoardWindowActions) {
    this.opener = window.opener as Window | null;
    try {
      this.canDock = this.opener?.location.origin === location.origin;
    } catch {
      this.canDock = false;
    }
    window.addEventListener('message', (event) => {
      if (event.origin !== location.origin || !event.data || typeof event.data !== 'object') return;
      const data = event.data as { type?: string; boardId?: string };
      if (typeof data.boardId !== 'string' || !/^[a-f0-9]{32}$/.test(data.boardId)) return;
      if (this.canDock && event.source === this.opener && data.type === 'tlbx-board-dock-ready') {
        if (data.boardId === this.actions.boardId()) window.close();
        return;
      }
      const popup = [...this.popups.keys()].find((candidate) => candidate === event.source);
      if (!popup) return;
      void this.handleMessage(popup, data.type, data.boardId).catch(actions.error);
    });
    window.addEventListener('pagehide', () => {
      if (this.canDock && this.opener)
        this.opener.postMessage(
          { type: 'tlbx-board-window-closed', boardId: this.actions.boardId() },
          location.origin,
        );
    });
  }

  async detach(): Promise<void> {
    const id = this.actions.boardId();
    if (!id) return;
    for (const [candidate, value] of this.popups) {
      if (candidate.closed) this.popups.delete(candidate);
      else if (value.boardId === id) {
        candidate.focus();
        return;
      }
    }
    // Reserve the window while this click still has browser user activation.
    const popup = window.open('about:blank', '_blank', 'popup,width=1400,height=900');
    if (!popup) throw new Error('Allow popups for tlbx to detach this board.');
    this.popups.set(popup, { boardId: id, pending: true });
    try {
      if (!(await this.actions.save()) || popup.closed) {
        popup.close();
        this.popups.delete(popup);
        return;
      }
      popup.location.replace(
        wrapConnectWorkspaceUrl(`/?board=${encodeURIComponent(id)}&handoff=1`),
      );
    } catch (error) {
      popup.close();
      this.popups.delete(popup);
      throw error;
    }
  }

  async dock(): Promise<void> {
    if (!this.canDock || !this.opener || !(await this.actions.save())) return;
    this.opener.postMessage(
      { type: 'tlbx-board-dock-request', boardId: this.actions.boardId() },
      location.origin,
    );
  }

  announceReady(boardId: string): void {
    const url = new URL(location.href);
    if (!this.canDock || !this.opener) return;
    url.searchParams.delete('handoff');
    history.replaceState(history.state, '', url);
    this.opener.postMessage({ type: 'tlbx-board-window-ready', boardId }, location.origin);
  }

  private async handleMessage(
    popup: Window,
    type: string | undefined,
    boardId: string,
  ): Promise<void> {
    const current = this.popups.get(popup);
    if (!current) return;
    current.boardId = boardId;
    if (type === 'tlbx-board-window-ready') {
      if (current.pending) {
        current.pending = false;
        await this.actions.detached(boardId);
      }
    } else if (type === 'tlbx-board-dock-request') {
      if (await this.actions.dock(boardId)) {
        popup.postMessage({ type: 'tlbx-board-dock-ready', boardId }, location.origin);
        window.focus();
      }
    } else if (type === 'tlbx-board-window-closed') this.popups.delete(popup);
  }
}

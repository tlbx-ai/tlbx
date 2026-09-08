/**
 * Terminal Presentation Refresh
 *
 * Provides a focused renderer refresh path for settings-driven visual changes
 * without triggering terminal resize/layout flows.
 */

import type { TerminalState } from '../../types';

/** Focus within a visible app is not a renderer-loss signal. */
export function setupTerminalForegroundRecovery(onRecover: () => void): () => void {
  let wasHidden = document.visibilityState === 'hidden';
  const recover = () => {
    if (!wasHidden || document.visibilityState === 'hidden') return;
    wasHidden = false;
    onRecover();
  };
  const visibilityChanged = () => {
    if (document.visibilityState === 'hidden') wasHidden = true;
    else recover();
  };
  const pageHidden = () => {
    wasHidden = true;
  };
  const pageShown = (event: PageTransitionEvent) => {
    if (event.persisted) wasHidden = true;
    recover();
  };
  document.addEventListener('visibilitychange', visibilityChanged);
  window.addEventListener('pagehide', pageHidden);
  window.addEventListener('pageshow', pageShown);
  window.addEventListener('focus', recover);
  return () => {
    document.removeEventListener('visibilitychange', visibilityChanged);
    window.removeEventListener('pagehide', pageHidden);
    window.removeEventListener('pageshow', pageShown);
    window.removeEventListener('focus', recover);
  };
}

type TerminalWithPrivateCore = TerminalState['terminal'] & {
  _core?: {
    _charSizeService?: { measure: () => void };
    _renderService?: {
      clear: () => void;
      handleResize: (cols: number, rows: number) => void;
    };
  };
};

export function isTerminalVisible(state: Pick<TerminalState, 'container'>): boolean {
  return (
    state.container.isConnected &&
    !state.container.classList.contains('hidden') &&
    state.container.getClientRects().length > 0
  );
}

export function remeasureTerminalCells(state: Pick<TerminalState, 'terminal' | 'container'>): void {
  const privateTerminal = state.terminal as TerminalWithPrivateCore;

  // Force layout so xterm remeasures against the current container geometry.
  // eslint-disable-next-line @typescript-eslint/no-meaningless-void-operator -- Reading the layout property intentionally forces synchronous layout.
  void state.container.offsetWidth;

  try {
    privateTerminal._core?._charSizeService?.measure();
  } catch {
    // xterm internals are unavailable while the terminal is still initializing.
  }
}

export function refreshTerminalRenderer(
  state: Pick<TerminalState, 'terminal' | 'container'>,
  options?: {
    preserveTextureAtlas?: boolean;
  },
): void {
  const terminal = state.terminal;
  const privateTerminal = terminal as TerminalWithPrivateCore;
  remeasureTerminalCells(state);

  try {
    privateTerminal._core?._renderService?.clear();
    privateTerminal._core?._renderService?.handleResize(terminal.cols, terminal.rows);
  } catch {
    // Renderer may not be ready yet.
  }

  if (!options?.preserveTextureAtlas) {
    try {
      terminal.clearTextureAtlas();
    } catch {
      // Non-WebGL renderers do not expose a texture atlas.
    }
  }

  try {
    terminal.refresh(0, Math.max(terminal.rows - 1, 0));
  } catch {
    // Terminal may have been disposed between frames.
  }
}

import type { Terminal } from '@xterm/xterm';

// xterm normally resumes from IntersectionObserver and requestAnimationFrame.
// A stale pause or lost frame must not leave a visible, parsing terminal frozen
// until the user switches sessions. Keep this adapter beside the pinned xterm
// integration; it never writes control bytes or resets the terminal buffer.
interface RecoverableRenderService {
  _isPaused: boolean;
  _needsFullRefresh: boolean;
  _renderDebouncer: { _rowStart?: number; dispose: () => void };
  _handleIntersectionChange: (entry: {
    isIntersecting: boolean;
    intersectionRatio: number;
  }) => void;
  refreshRows: (start: number, end: number, sync: boolean) => void;
}

const RENDER_RECOVERY_DELAY_MS = 1500;

function isOnScreen(container: HTMLElement): boolean {
  if (document.visibilityState === 'hidden' || !container.isConnected) return false;
  const rect = container.getBoundingClientRect();
  return (
    rect.width > 0 &&
    rect.height > 0 &&
    rect.bottom > 0 &&
    rect.right > 0 &&
    rect.top < window.innerHeight &&
    rect.left < window.innerWidth
  );
}

export function setupTerminalRenderRecovery(
  terminal: Terminal,
  container: HTMLElement,
  onRecover: () => void,
): { dispose: () => void } {
  let timer: ReturnType<typeof setTimeout> | undefined;
  let disposed = false;
  const clear = (): void => {
    if (timer !== undefined) clearTimeout(timer);
    timer = undefined;
  };
  const schedule = (): void => {
    if (disposed || timer !== undefined) return;
    timer = setTimeout(recover, RENDER_RECOVERY_DELAY_MS);
  };
  const recover = (): void => {
    timer = undefined;
    if (disposed || !isOnScreen(container)) return;
    const renderer = (
      terminal as Terminal & { _core?: { _renderService?: RecoverableRenderService } }
    )._core?._renderService;
    if (
      !renderer ||
      (!renderer._needsFullRefresh && renderer._renderDebouncer._rowStart === undefined)
    ) {
      return;
    }

    if (renderer._isPaused) {
      // Also flush xterm's deferred resize and restore renderer visibility.
      renderer._handleIntersectionChange({ isIntersecting: true, intersectionRatio: 1 });
    }
    if (terminal.modes.synchronizedOutputMode) {
      // Let the application's frame end (or xterm's existing safety timeout)
      // release synchronized output. Never force a partial protocol frame.
      schedule();
      return;
    }

    // Release a pending RAF handle before drawing synchronously. A refresh()
    // alone would queue behind that same lost callback and remain frozen.
    renderer._renderDebouncer.dispose();
    renderer.refreshRows(0, terminal.rows - 1, true);
    onRecover();
  };
  const parsed = terminal.onWriteParsed(schedule);
  const rendered = terminal.onRender(clear);
  return {
    dispose: () => {
      disposed = true;
      clear();
      parsed.dispose();
      rendered.dispose();
    },
  };
}

/** Bound a connection's handshake and initial synchronization, not just onopen. */
export const SOCKET_PROGRESS_TIMEOUT_MS = 1500;
export const RESUME_PROBE_TIMEOUT_MS = 1000;

export class SocketReadiness {
  private stalledAttempts = 0;

  watch(
    socket: WebSocket,
    isCurrent: () => boolean,
    isReady: () => boolean,
    reconnect: () => void,
    progressTimeoutMs: () => number = () => SOCKET_PROGRESS_TIMEOUT_MS,
  ): () => void {
    let timer: ReturnType<typeof setTimeout> | undefined;
    let disposed = false;

    const dispose = (): void => {
      disposed = true;
      clearTimeout(timer);
      socket.removeEventListener('message', progress);
      socket.removeEventListener('close', dispose);
      document.removeEventListener('visibilitychange', progress);
    };
    const progress = (): void => {
      clearTimeout(timer);
      if (disposed) return;
      if (!isCurrent() || isReady()) {
        if (isReady()) this.stalledAttempts = 0;
        dispose();
        return;
      }
      // Hidden pages may intentionally stop parsing output. Start a fresh deadline
      // on visibility return instead of expiring a timer paused by the browser.
      if (document.visibilityState === 'hidden') return;
      timer = setTimeout(
        () => {
          if (!isCurrent() || isReady()) {
            if (isReady()) this.stalledAttempts = 0;
            dispose();
            return;
          }
          if (document.visibilityState === 'hidden') return;
          dispose();
          this.stalledAttempts = Math.min(3, this.stalledAttempts + 1);
          reconnect();
        },
        Math.max(
          progressTimeoutMs(),
          Math.min(10000, SOCKET_PROGRESS_TIMEOUT_MS * 2 ** this.stalledAttempts),
        ),
      );
    };

    socket.addEventListener('message', progress);
    socket.addEventListener('close', dispose);
    document.addEventListener('visibilitychange', progress);
    progress();
    return dispose;
  }
}

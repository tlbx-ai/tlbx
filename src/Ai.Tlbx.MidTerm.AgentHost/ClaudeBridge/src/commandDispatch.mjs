// SDK setting writes share ordering; callbacks and interruption must stay live.
export function createSettingsCommandDispatcher(handle) {
  let settings = Promise.resolve();
  const pendingTurns = new Map();
  const cancelTurn = (turnId) => {
    if (!turnId) return;
    for (const token of pendingTurns.get(turnId) ?? []) { token.cancelled = true; token.abort.abort(); }
    pendingTurns.delete(turnId);
  };
  const dispatch = (command) => {
    if (command.type === "turn.interrupt") cancelTurn(command.turnId);
    const isTurnInput = command.type === "turn.start" || command.type === "turn.steer";
    const token = isTurnInput ? { cancelled: false, abort: new AbortController() } : null;
    if (token) {
      if (!pendingTurns.has(command.turnId)) pendingTurns.set(command.turnId, new Set());
      pendingTurns.get(command.turnId).add(token);
    }
    const run = async () => {
      try {
        if (token?.cancelled) return;
        return await handle(command, () => token?.cancelled === true, token?.abort.signal);
      } catch (error) {
        // A stopped input may reject its outstanding SDK control afterwards.
        // Do not turn that old cancellation into a failure of a newer turn.
        if (!token?.cancelled) throw error;
      } finally {
        if (token) {
          const turn = pendingTurns.get(command.turnId);
          turn?.delete(token);
          if (turn?.size === 0) pendingTurns.delete(command.turnId);
        }
      }
    };
    if (!isTurnInput && command.type !== "runtime.configure")
      return Promise.resolve().then(run);
    const pending = settings.then(run);
    settings = pending.catch(() => {});
    return pending;
  };
  dispatch.cancelTurn = cancelTurn;
  return dispatch;
}

import type { TerminalState } from '../../types';

const MIN_REPLAY_ROWS = 10;
const MAX_REPLAY_ROWS = 500;

function normalizeReplayRows(rows: unknown): number | null {
  if (typeof rows !== 'number' || !Number.isFinite(rows)) {
    return null;
  }

  const normalized = Math.trunc(rows);
  return normalized > 0 ? Math.min(MAX_REPLAY_ROWS, Math.max(MIN_REPLAY_ROWS, normalized)) : null;
}

function getStateReplayRows(state: TerminalState | undefined): number | null {
  if (!state) {
    return null;
  }

  return normalizeReplayRows(state.terminal.rows) ?? normalizeReplayRows(state.serverRows);
}

export function resolveReplayRowsFromTerminals(
  sessionId: string | null | undefined,
  activeSessionId: string | null,
  terminals: ReadonlyMap<string, TerminalState>,
  isRemoteSessionId: (sessionId: string) => boolean,
): number | null {
  if (sessionId) {
    const sessionRows = getStateReplayRows(terminals.get(sessionId));
    if (sessionRows !== null) {
      return sessionRows;
    }
  }

  if (activeSessionId && activeSessionId !== sessionId && !isRemoteSessionId(activeSessionId)) {
    const activeRows = getStateReplayRows(terminals.get(activeSessionId));
    if (activeRows !== null) {
      return activeRows;
    }
  }

  for (const [id, state] of terminals) {
    if (isRemoteSessionId(id)) {
      continue;
    }

    const rows = getStateReplayRows(state);
    if (rows !== null) {
      return rows;
    }
  }

  return null;
}

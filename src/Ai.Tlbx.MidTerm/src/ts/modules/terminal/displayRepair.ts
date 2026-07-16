import { redrawSession } from '../../api/client';
import { sessionTerminals } from '../../state';
import { $activeSessionId } from '../../stores';
import { getActiveTab } from '../sessionTabs';
import { refreshTerminalPresentation } from './scaling';

/**
 * Ask the owning shell to repaint, then repair only the browser renderer.
 * Terminal dimensions and retained output remain untouched.
 */
export async function repairTerminalDisplay(sessionId: string): Promise<void> {
  const state = sessionTerminals.get(sessionId);
  const restoreTerminalFocus =
    $activeSessionId.get() === sessionId && getActiveTab(sessionId) === 'terminal';

  await redrawSession(sessionId);

  if (!state) {
    return;
  }

  refreshTerminalPresentation(sessionId, state);

  if (!restoreTerminalFocus) {
    return;
  }

  requestAnimationFrame(() => {
    if (
      $activeSessionId.get() === sessionId &&
      getActiveTab(sessionId) === 'terminal' &&
      sessionTerminals.get(sessionId) === state
    ) {
      state.terminal.focus();
    }
  });
}

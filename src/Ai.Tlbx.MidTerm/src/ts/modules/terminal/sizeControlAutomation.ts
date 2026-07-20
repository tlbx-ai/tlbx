import { $terminalSizeControls } from '../../stores';
import { sessionTerminals } from '../../state';
import { requestTerminalSizeControl } from '../comms';
import { createLogger } from '../logging';
import { isTerminalVisible } from './presentationRefresh';

const log = createLogger('terminalSizeControl');
const observedEpochs = new Map<string, number>();
const takeoversInFlight = new Set<string>();
let onControlAcquired: (sessionId: string) => void = () => {};

export function configureTerminalSizeControlAutomation(
  controlAcquired: (sessionId: string) => void,
): void {
  onControlAcquired = controlAcquired;
}

function canTakeOverInCurrentBrowser(): boolean {
  if (typeof document === 'undefined') return true;
  if (document.visibilityState === 'hidden') return false;
  return typeof document.hasFocus !== 'function' || document.hasFocus();
}

/**
 * Claim only terminals that are already eligible when this visible browser
 * first observes their ownership epoch. This deliberately creates no timer
 * that could make a passive tablet take over several minutes later.
 */
export function claimEligibleVisibleTerminalSizes(reconsiderCurrentView = false): void {
  if (!canTakeOverInCurrentBrowser()) return;

  const statuses = $terminalSizeControls.get();
  sessionTerminals.forEach((state, sessionId) => {
    if (!state.opened || !isTerminalVisible(state)) return;
    const status = statuses[sessionId];
    if (!status || status.isOwner) return;

    const observedEpoch = observedEpochs.get(sessionId);
    observedEpochs.set(sessionId, status.epoch);
    if (!reconsiderCurrentView && observedEpoch === status.epoch) return;
    if (
      !status.canTakeOverAutomatically ||
      (status.ownerOnline && status.ownerInSameBrowserProfile) ||
      takeoversInFlight.has(sessionId)
    ) {
      return;
    }

    takeoversInFlight.add(sessionId);
    requestTerminalSizeControl(sessionId, false)
      .then((result) => {
        if (result.status.isOwner) onControlAcquired(sessionId);
      })
      .catch((error: unknown) => {
        log.warn(() => `Automatic terminal size takeover failed: ${String(error)}`);
      })
      .finally(() => {
        takeoversInFlight.delete(sessionId);
      });
  });
}

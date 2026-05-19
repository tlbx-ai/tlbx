import type { LaunchEntry } from '../../api/types';
import { sendInput } from '../comms';
import { refreshHubState, setRemoteSessionBookmark } from '../hub';
import { buildReplayCommand } from '../sidebar/processDisplay';

export function linkAndReplayRemoteBookmark(
  machineId: string,
  remoteSessionId: string,
  compositeSessionId: string,
  entry: LaunchEntry,
): void {
  void setRemoteSessionBookmark(machineId, remoteSessionId, entry.id)
    .then(() => refreshHubState())
    .catch(() => {});

  if (!entry.commandLine) {
    return;
  }

  const replayCommand = buildReplayCommand(entry.executable, entry.commandLine);
  window.setTimeout(() => {
    sendInput(compositeSessionId, replayCommand + '\r');
  }, 100);
}

import type { CreateSessionRequest, LaunchEntry } from '../../api/types';
import { buildReplayCommand } from '../sidebar/processDisplay';
import { isPlainShellExecutable } from './bookmarkSession';

export function buildLocalBookmarkLaunchRequest(
  entry: LaunchEntry,
  cols: number,
  rows: number,
): CreateSessionRequest {
  const isPlainShell =
    !entry.commandLine?.trim() || isPlainShellExecutable(entry.executable, entry.shellType);
  const launchCommand =
    !isPlainShell && entry.commandLine
      ? buildReplayCommand(entry.executable, entry.commandLine)
      : null;

  return {
    cols,
    rows,
    shell: entry.shellType || null,
    workingDirectory: entry.workingDirectory || null,
    launchCommand,
  };
}

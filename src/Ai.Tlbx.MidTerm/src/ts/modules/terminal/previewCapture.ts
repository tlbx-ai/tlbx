import { sessionTerminals } from '../../state';
import { $activeSessionId } from '../../stores';
import { prepareMuxTerminalCapture } from '../comms/muxChannel';
import { paintTerminalForCapture } from './renderRecovery';

export async function prepareTerminalPreviewCapture(visibleIds: string[]): Promise<void> {
  const activeId = $activeSessionId.get();
  const ids = [...new Set([...visibleIds, ...(activeId ? [activeId] : [])])].filter((id) => {
    const container = sessionTerminals.get(id)?.container;
    const rect = container?.isConnected ? container.getBoundingClientRect() : null;
    return !!rect && rect.width > 0 && rect.height > 0;
  });
  await prepareMuxTerminalCapture(ids);
  ids.forEach((id) => {
    const state = sessionTerminals.get(id);
    if (state?.opened) paintTerminalForCapture(state.terminal);
  });
}

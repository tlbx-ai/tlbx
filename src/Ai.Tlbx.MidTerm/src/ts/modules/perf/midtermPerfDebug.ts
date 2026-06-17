import { sessionTerminals } from '../../state';
import { $activeSessionId } from '../../stores';
import { createPerfDebugApi, type PerfDebugApi, type PerfTerminalSummary } from './frameRecorder';

export function createMidtermPerfDebugApi(): PerfDebugApi {
  return createPerfDebugApi(getPerfTerminalSummary);
}

function getPerfTerminalSummary(): PerfTerminalSummary {
  const activeId = $activeSessionId.get();
  const terminals = Array.from(sessionTerminals.entries()).map(([id, state]) => {
    const rect = state.container.getBoundingClientRect();
    const style = window.getComputedStyle(state.container);
    const buffer = state.terminal.buffer.active;
    const bufferWithViewport = buffer as { viewportY?: number };

    return {
      id,
      active: id === activeId,
      opened: state.opened,
      hasWebgl: state.hasWebgl === true,
      cols: state.terminal.cols,
      rows: state.terminal.rows,
      bufferLength: buffer.length,
      baseY: buffer.baseY,
      viewportY: bufferWithViewport.viewportY ?? null,
      visible:
        rect.width > 0 &&
        rect.height > 0 &&
        style.display !== 'none' &&
        style.visibility !== 'hidden' &&
        !state.container.classList.contains('hidden'),
      width: Math.round(rect.width),
      height: Math.round(rect.height),
    };
  });

  return {
    activeId,
    terminalCount: terminals.length,
    terminals,
  };
}

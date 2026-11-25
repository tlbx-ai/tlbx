import { $currentSettings } from '../../stores';
import { DEFAULT_TOOL_CALL_OUTPUT_LINES, MAX_TOOL_CALL_OUTPUT_LINES } from './historyConstants';
import type { AppServerControlHistoryEntry } from './types';

export function resolveToolCallOutputLineLimit(): number {
  const configured = $currentSettings.get()?.toolCallOutputLines;
  if (typeof configured !== 'number' || !Number.isFinite(configured)) {
    return DEFAULT_TOOL_CALL_OUTPUT_LINES;
  }

  return Math.max(0, Math.min(MAX_TOOL_CALL_OUTPUT_LINES, Math.trunc(configured)));
}

export function clampToolCallOutputLines(lines: readonly string[], fromEnd = false): string[] {
  const lineLimit = resolveToolCallOutputLineLimit();
  if (lineLimit <= 0) return [];
  return fromEnd ? lines.slice(-lineLimit) : lines.slice(0, lineLimit);
}

export function selectProjectedToolEvidenceLines(entry: AppServerControlHistoryEntry): string[] {
  const presentation = entry.toolPresentation;
  if (!presentation?.evidence) {
    return [];
  }

  const lines = presentation.evidence.replace(/\r\n/g, '\n').replace(/\r/g, '\n').split('\n');
  const lineLimit = resolveToolCallOutputLineLimit();
  if (lineLimit <= 0 || lines.length === 0) {
    return [];
  }
  if (lines.length <= lineLimit) {
    return lines;
  }
  if (presentation.evidenceKind === 'error' || presentation.category === 'command') {
    return lines.slice(lines.length - lineLimit);
  }
  if (presentation.category === 'read' || lineLimit === 1) {
    return lines.slice(0, lineLimit);
  }
  return [lines[0] ?? '', ...lines.slice(lines.length - (lineLimit - 1))];
}

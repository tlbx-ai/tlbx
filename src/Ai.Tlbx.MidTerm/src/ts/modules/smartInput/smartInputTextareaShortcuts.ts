import type { SessionTabId } from '../sessionTabs';

export interface SmartInputTextareaShortcutEvent {
  altKey: boolean;
  ctrlKey: boolean;
  key: string;
  metaKey: boolean;
  shiftKey: boolean;
}

export type SmartInputShiftTabAction =
  'toggle-appServerControl-plan-mode' | 'forward-to-terminal' | null;

export function resolveSmartInputShiftTabAction(
  event: SmartInputTextareaShortcutEvent,
  activeTab: SessionTabId | null | undefined,
): SmartInputShiftTabAction {
  if (event.key !== 'Tab' || !event.shiftKey || event.ctrlKey || event.altKey || event.metaKey) {
    return null;
  }

  if (activeTab === 'agent') {
    return 'toggle-appServerControl-plan-mode';
  }

  if (activeTab === 'terminal') {
    return 'forward-to-terminal';
  }

  return null;
}

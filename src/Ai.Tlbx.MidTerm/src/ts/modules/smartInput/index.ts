/**
 * Smart Input Module
 *
 * Docked text input for Smart Input and Both modes.
 * Keeps terminal and text-entry behavior coordinated without
 * relying on direct terminal keyboard focus.
 */

export {
  initSmartInput,
  toggleSmartInputVoiceRecording,
  canToggleSmartInputVoiceRecording,
  showSmartInput,
  hideSmartInput,
  isSmartInputMode,
  isBothMode,
  removeSmartInputSessionState,
  setAppServerControlResumeConversationHandler,
} from './smartInput';
export { startHistoryion, stopHistoryion } from './transcription';

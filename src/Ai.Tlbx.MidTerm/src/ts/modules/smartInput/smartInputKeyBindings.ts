import { hasTransientUi } from '../shortcuts/uiContext';

export interface SmartInputGlobalKeyBindingArgs {
  closeFooterTransientUi(): boolean;
  endRecording(): void;
  getInterruptibleAppServerControlSessionId(): string | null;
  isRecording(): boolean;
  onAppServerControlEscape(sessionId: string): void;
}

export function bindSmartInputGlobalKeyBindings(args: SmartInputGlobalKeyBindingArgs): void {
  document.addEventListener(
    'keydown',
    (event) => {
      if (
        !isBareEscapeKey(event) ||
        event.isComposing ||
        event.repeat ||
        event.defaultPrevented ||
        hasTransientUi()
      )
        return;
      if (args.closeFooterTransientUi()) {
        event.preventDefault();
        event.stopImmediatePropagation();
        return;
      }
      const appServerControlSessionId = args.getInterruptibleAppServerControlSessionId();
      if (!appServerControlSessionId) {
        return;
      }

      event.preventDefault();
      event.stopPropagation();
      event.stopImmediatePropagation();
      args.onAppServerControlEscape(appServerControlSessionId);
    },
    true,
  );

  window.addEventListener('blur', () => {
    if (args.isRecording()) args.endRecording();
  });
}

export function isBareEscapeKey(event: KeyboardEvent): boolean {
  return (
    event.key === 'Escape' && !event.shiftKey && !event.ctrlKey && !event.altKey && !event.metaKey
  );
}

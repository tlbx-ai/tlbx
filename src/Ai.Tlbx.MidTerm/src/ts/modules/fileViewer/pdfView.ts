import { wrapConnectWorkspaceUrl } from '../web/connectPreview';

const escapeHandlers = new WeakMap<Window, () => boolean>();
let listeningForEscape = false;
function registerEscape(frame: HTMLIFrameElement, escape: () => boolean): void {
  if (!listeningForEscape) {
    window.addEventListener('message', (event: MessageEvent) => {
      if (event.data === 'tlbx:pdf:escape' && event.source)
        escapeHandlers.get(event.source as Window)?.();
    });
    listeningForEscape = true;
  }
  if (frame.contentWindow)
    escapeHandlers.set(frame.contentWindow, () => frame.isConnected && escape());
}

/** Isolate PDF rendering and its worker from the terminal's document/lifecycle. */
export function createPdfView(
  viewUrl: string,
  className: string,
  signal?: AbortSignal,
  escape?: () => boolean,
): HTMLIFrameElement {
  const frame = document.createElement('iframe');
  frame.className = className;
  frame.title = 'PDF';
  const params = new URLSearchParams({
    file: viewUrl,
    lang: document.documentElement.lang,
    scheme: document.documentElement.dataset.nativeColorScheme ?? 'dark',
  });
  frame.src = wrapConnectWorkspaceUrl(`/pdf/index.html?${params}`);
  if (escape) {
    frame.addEventListener('load', () => {
      registerEscape(frame, escape);
    });
  }
  signal?.addEventListener(
    'abort',
    () => {
      if (frame.contentWindow) escapeHandlers.delete(frame.contentWindow);
      frame.remove();
    },
    { once: true },
  );
  return frame;
}

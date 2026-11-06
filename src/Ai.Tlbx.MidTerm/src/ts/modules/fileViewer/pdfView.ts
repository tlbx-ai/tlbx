import { wrapConnectWorkspaceUrl } from '../web/connectPreview';

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
      frame.contentDocument?.addEventListener('keydown', (event) => {
        if (event.key === 'Escape' && escape()) {
          event.preventDefault();
          event.stopPropagation();
        }
      });
    });
  }
  signal?.addEventListener(
    'abort',
    () => {
      frame.remove();
    },
    { once: true },
  );
  return frame;
}

import { createLogger } from './modules/logging';
// Separate entry point: PDF.js is loaded only inside an opened PDF frame.
import { getDocument, GlobalWorkerOptions, PasswordResponses, AnnotationMode } from 'pdfjs-dist';
import {
  EventBus,
  PDFViewer,
  PDFLinkService,
  PDFFindController,
} from 'pdfjs-dist/web/pdf_viewer.mjs';

function element(id: string): HTMLElement {
  const control = document.getElementById(id);
  if (!control) throw new Error(`Missing PDF control: ${id}`);
  return control;
}

const params = new URLSearchParams(location.search);
document.documentElement.style.colorScheme = params.get('scheme') === 'light' ? 'light' : 'dark';
const german = params.get('lang')?.startsWith('de');
document.documentElement.lang = german ? 'de' : 'en';
const labels: Record<string, string> = german
  ? {
      previous: 'Vorherige Seite',
      next: 'Nächste Seite',
      page: 'Seite',
      zoomOut: 'Verkleinern',
      zoomIn: 'Vergrößern',
      zoom: 'Zoom',
      search: 'Im Dokument suchen',
      findPrevious: 'Vorheriger Treffer',
      findNext: 'Nächster Treffer',
      selectText: 'Text auswählen',
      handTool: 'Handwerkzeug',
      copyText: 'Markierten Text kopieren',
    }
  : {};
for (const [id, label] of Object.entries(labels)) {
  const control = element(id);
  control.setAttribute('aria-label', label);
  control.title = label;
  if (control instanceof HTMLInputElement && control.type === 'search') control.placeholder = label;
}
const zoom = element('zoom') as HTMLSelectElement;
if (german) {
  element('handTool').textContent = 'Hand';
  element('copyText').textContent = 'Kopieren';
  zoom.options.item(0)?.replaceChildren('Seitenbreite');
  zoom.options.item(1)?.replaceChildren('Ganze Seite');
  document.querySelector('label[for="password"]')?.replaceChildren('PDF-Passwort');
  element('passwordSubmit').textContent = 'Öffnen';
}
const status = element('status');
status.textContent = german ? 'PDF wird geladen…' : 'Loading PDF…';
const container = element('viewerContainer') as HTMLDivElement;
const bus = new EventBus();
const linkService = new PDFLinkService({
  eventBus: bus,
  externalLinkTarget: 2,
  externalLinkRel: 'noopener noreferrer',
});
const findController = new PDFFindController({ eventBus: bus, linkService });
const viewer = new PDFViewer({
  container,
  eventBus: bus,
  linkService,
  findController,
  annotationMode: AnnotationMode.ENABLE,
  imageResourcesPath: '/pdf/images/',
});
linkService.setViewer(viewer);
GlobalWorkerOptions.workerSrc = '/pdf/worker.js';
const page = element('page') as HTMLInputElement;
const search = element('search') as HTMLInputElement;
const copyText = element('copyText') as HTMLButtonElement;
let handMode = false;
let drag: { pointerId: number; x: number; y: number; left: number; top: number } | null = null;
function selectedText(): string {
  const selection = window.getSelection();
  return selection?.anchorNode &&
    selection.focusNode &&
    container.contains(selection.anchorNode) &&
    container.contains(selection.focusNode)
    ? selection.toString()
    : '';
}
document.addEventListener('selectionchange', () => {
  copyText.disabled = !selectedText();
});
function endPan(): void {
  if (drag && container.hasPointerCapture(drag.pointerId))
    container.releasePointerCapture(drag.pointerId);
  drag = null;
  container.classList.remove('pdf-dragging');
}
function setHandMode(hand: boolean): void {
  endPan();
  handMode = hand;
  container.classList.toggle('pdf-hand', hand);
  element('selectText').setAttribute('aria-pressed', String(!hand));
  element('handTool').setAttribute('aria-pressed', String(hand));
  if (hand) window.getSelection()?.removeAllRanges();
}
element('selectText').onclick = () => {
  setHandMode(false);
};
element('handTool').onclick = () => {
  setHandMode(true);
};
// Retain the document selection while the toolbar button takes the click.
copyText.addEventListener('pointerdown', (event) => {
  event.preventDefault();
});
copyText.onclick = () => {
  const text = selectedText();
  if (!text) return;
  void navigator.clipboard
    .writeText(text)
    .then(() => {
      element('toolStatus').textContent = german ? 'Text kopiert.' : 'Text copied.';
    })
    .catch(() => {
      element('toolStatus').textContent = german
        ? 'Kopieren fehlgeschlagen. Strg+C verwenden.'
        : 'Copy failed. Use Ctrl+C.';
    });
};
container.addEventListener('pointerdown', (event) => {
  if (
    !handMode ||
    event.button !== 0 ||
    event.pointerType === 'touch' ||
    (event.target instanceof Element && event.target.closest('a, input, button, select, textarea'))
  )
    return;
  event.preventDefault();
  drag = {
    pointerId: event.pointerId,
    x: event.clientX,
    y: event.clientY,
    left: container.scrollLeft,
    top: container.scrollTop,
  };
  container.setPointerCapture(event.pointerId);
  container.classList.add('pdf-dragging');
});
container.addEventListener('pointermove', (event) => {
  if (!drag || drag.pointerId !== event.pointerId) return;
  container.scrollLeft = drag.left + drag.x - event.clientX;
  container.scrollTop = drag.top + drag.y - event.clientY;
});
container.addEventListener('pointerup', endPan);
container.addEventListener('pointercancel', endPan);
container.addEventListener('lostpointercapture', endPan);
const controls = document.querySelectorAll<
  HTMLInputElement | HTMLButtonElement | HTMLSelectElement
>('#toolbar button, #toolbar input, #toolbar select');
for (const control of controls) control.disabled = true;
const customZoom = new Option('', 'custom');
customZoom.hidden = true;
zoom.add(customZoom);
let pageCount = 0;
const toolbar = element('toolbar');
const resize = new ResizeObserver(() => {
  document.documentElement.style.setProperty(
    '--toolbar-height',
    `${toolbar.getBoundingClientRect().height}px`,
  );
  if (
    pageCount &&
    container.clientWidth > 0 &&
    container.clientHeight > 0 &&
    zoom.value.startsWith('page-')
  )
    viewer.currentScaleValue = zoom.value;
});
resize.observe(toolbar);
resize.observe(container);
bus.on('pagesinit', () => {
  for (const control of controls) control.disabled = false;
  copyText.disabled = !selectedText();
  element('previous').setAttribute('disabled', '');
  (element('next') as HTMLButtonElement).disabled = pageCount <= 1;
  if (container.clientWidth > 0 && container.clientHeight > 0)
    viewer.currentScaleValue = 'page-width';
  status.hidden = true;
});
bus.on('pagechanging', ({ pageNumber }: { pageNumber: number }) => {
  page.value = String(pageNumber);
  (element('previous') as HTMLButtonElement).disabled = pageNumber <= 1;
  (element('next') as HTMLButtonElement).disabled = pageNumber >= pageCount;
});
function find(backwards: boolean, again: boolean): void {
  bus.dispatch('find', {
    source: window,
    type: again ? 'again' : '',
    query: search.value,
    caseSensitive: false,
    entireWord: false,
    highlightAll: true,
    findPrevious: backwards,
    matchDiacritics: false,
  });
}
bus.on(
  'updatefindmatchescount',
  ({ matchesCount }: { matchesCount: { current: number; total: number } }) => {
    element('matches').textContent = search.value
      ? `${matchesCount.current}/${matchesCount.total}`
      : '';
  },
);
bus.on('updatefindcontrolstate', ({ state }: { state: number }) => {
  if (state === 1) element('matches').textContent = german ? 'Keine Treffer' : 'No matches';
});
element('previous').onclick = () => viewer.previousPage();
element('next').onclick = () => viewer.nextPage();
page.onchange = () => {
  viewer.currentPageNumber = Math.max(1, Math.min(pageCount, Number(page.value) || 1));
  page.value = String(viewer.currentPageNumber);
};
zoom.onchange = () => {
  viewer.currentScaleValue = zoom.value;
};
function changeZoom(factor: number, origin?: [number, number]): void {
  if (!pageCount) return;
  const scale = Math.max(0.25, Math.min(5, viewer.currentScale * factor));
  viewer.updateScale({ scaleFactor: scale / viewer.currentScale, ...(origin ? { origin } : {}) });
  customZoom.text = `${Math.round(viewer.currentScale * 100)}%`;
  zoom.value = 'custom';
}
// A non-passive listener in the PDF document cancels browser zoom even when
// the pointer is over its toolbar, or the PDF is loading/at its zoom limit.
document.addEventListener(
  'wheel',
  (event) => {
    if (!event.ctrlKey && !event.metaKey) return;
    event.preventDefault();
    const unit =
      event.deltaMode === WheelEvent.DOM_DELTA_LINE
        ? 16
        : event.deltaMode === WheelEvent.DOM_DELTA_PAGE
          ? container.clientHeight
          : 1;
    const factor = Math.exp(Math.max(-0.5, Math.min(0.5, -event.deltaY * unit * 0.002)));
    changeZoom(factor, [event.clientX, event.clientY]);
  },
  { passive: false, capture: true },
);
element('zoomIn').onclick = () => {
  changeZoom(1.25);
};
element('zoomOut').onclick = () => {
  changeZoom(0.8);
};
search.oninput = () => {
  find(false, false);
};
search.onkeydown = (event) => {
  if (event.key === 'Enter') {
    event.preventDefault();
    find(event.shiftKey, true);
  }
};
element('findPrevious').onclick = () => {
  find(true, true);
};
element('findNext').onclick = () => {
  find(false, true);
};
document.addEventListener('keydown', (event) => {
  if ((event.ctrlKey || event.metaKey) && event.key.toLowerCase() === 'f') {
    event.preventDefault();
    search.focus();
    search.select();
  }
});

async function open(): Promise<void> {
  // Only the authenticated file endpoint is accepted; never turn this into a URL proxy.
  const file = new URL(params.get('file') ?? '', location.origin);
  if (file.origin !== location.origin || file.pathname !== '/api/files/view')
    throw new Error('Invalid PDF URL');
  const loading = getDocument({
    url: file.href,
    cMapUrl: '/pdf/cmaps/',
    cMapPacked: true,
    standardFontDataUrl: '/pdf/standard_fonts/',
    wasmUrl: '/pdf/wasm/',
  });
  window.addEventListener(
    'pagehide',
    () => {
      resize.disconnect();
      void loading.destroy();
    },
    { once: true },
  );
  loading.onPassword = (updatePassword: (password: string) => void, reason: number) => {
    const form = element('passwordForm') as HTMLFormElement;
    status.hidden = false;
    status.textContent =
      reason === PasswordResponses.INCORRECT_PASSWORD
        ? german
          ? 'Falsches Passwort.'
          : 'Incorrect password.'
        : german
          ? 'Dieses PDF ist passwortgeschützt.'
          : 'This PDF is password protected.';
    form.hidden = false;
    const password = element('password') as HTMLInputElement;
    password.focus();
    form.onsubmit = (event) => {
      event.preventDefault();
      form.hidden = true;
      updatePassword(password.value);
      password.value = '';
    };
  };
  const pdf = await loading.promise;
  pageCount = pdf.numPages;
  page.max = String(pageCount);
  element('pages').textContent = `/ ${pageCount}`;
  linkService.setDocument(pdf);
  viewer.setDocument(pdf);
}
void open().catch((error: unknown) => {
  status.hidden = false;
  status.setAttribute('role', 'alert');
  status.textContent = german
    ? 'PDF konnte nicht geladen werden. Bitte erneut öffnen oder herunterladen.'
    : 'Could not load PDF. Please reopen or download the file.';
  createLogger('pdfViewer').error(() => `PDF loading failed: ${String(error)}`);
});

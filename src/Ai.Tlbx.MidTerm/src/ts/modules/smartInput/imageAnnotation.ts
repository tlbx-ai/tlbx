import { t } from '../i18n';
import { registerBackButtonLayer } from '../navigation/backButtonGuard';
import {
  annotationHasChanges,
  annotationPoint,
  drawAnnotationStroke,
  undoAnnotationStroke,
  type AnnotationStroke,
} from './imageAnnotationModel';

export interface ImageAnnotationOptions {
  src: string;
  name: string;
  signal?: AbortSignal;
  closed?: () => void;
  save: (file: File, signal: AbortSignal) => Promise<void>;
}

function button(label: string, action: () => void): HTMLButtonElement {
  const element = document.createElement('button');
  element.type = 'button';
  element.className = 'btn-secondary';
  element.textContent = label;
  element.addEventListener('click', action);
  return element;
}

function trapAnnotationFocus(event: KeyboardEvent, overlay: HTMLElement): void {
  if (event.key !== 'Tab') return;
  const controls = Array.from(
    overlay.querySelectorAll<HTMLElement>(
      'button:not(:disabled), input:not(:disabled), [tabindex="0"]',
    ),
  );
  const first = controls[0];
  const last = controls[controls.length - 1];
  if (!first || !last) return;
  if (event.shiftKey && document.activeElement === first) {
    event.preventDefault();
    last.focus();
  } else if (!event.shiftKey && document.activeElement === last) {
    event.preventDefault();
    first.focus();
  }
}

export function openImageAnnotation(options: ImageAnnotationOptions): void {
  if (options.signal?.aborted) return;
  const previousFocus =
    document.activeElement instanceof HTMLElement ? document.activeElement : null;
  const controller = new AbortController();
  const isClosed = (): boolean => controller.signal.aborted;
  const overlay = document.createElement('div');
  overlay.className = 'modal-overlay rich-image-annotation-overlay';
  const modal = document.createElement('div');
  modal.className = 'modal rich-image-annotation';
  modal.setAttribute('role', 'dialog');
  modal.setAttribute('aria-modal', 'true');
  modal.setAttribute('aria-label', t('smartInput.richAnnotate'));
  const title = document.createElement('h3');
  title.textContent = t('smartInput.richAnnotate');
  const header = document.createElement('div');
  header.className = 'modal-header';
  header.append(title);
  const body = document.createElement('div');
  body.className = 'modal-body';
  const canvas = document.createElement('canvas');
  canvas.tabIndex = 0;
  canvas.setAttribute('aria-label', options.name);
  const status = document.createElement('div');
  status.className = 'rich-composer-error';
  status.setAttribute('role', 'status');
  const footer = document.createElement('div');
  footer.className = 'modal-footer';
  const image = new Image();
  let strokes: AnnotationStroke[] = [];
  let current: AnnotationStroke | null = null;
  let pointerId: number | null = null;
  let loaded = false;
  let saving = false;
  let revision = 0;
  let exported: { revision: number; file: File } | null = null;
  let releaseBack = (): void => {
    /* Registered after the modal is mounted. */
  };
  const close = (): void => {
    if (isClosed()) return;
    options.signal?.removeEventListener('abort', close);
    controller.abort();
    options.closed?.();
    releaseBack();
    overlay.remove();
    previousFocus?.focus({ preventScroll: true });
  };
  const redraw = (): void => {
    const context = canvas.getContext('2d');
    if (!context || !loaded) return;
    context.clearRect(0, 0, canvas.width, canvas.height);
    context.drawImage(image, 0, 0);
    for (const stroke of strokes) drawAnnotationStroke(context, stroke);
    if (current) drawAnnotationStroke(context, current);
    undo.disabled = saving || strokes.length === 0;
    save.disabled = saving || !annotationHasChanges(strokes);
  };
  const color = document.createElement('input');
  color.type = 'color';
  color.value = '#ff453a';
  color.setAttribute('aria-label', t('smartInput.richPenColor'));
  const width = document.createElement('input');
  width.type = 'range';
  width.min = '1';
  width.max = '16';
  width.value = '3';
  width.setAttribute('aria-label', t('smartInput.richPenWidth'));
  const undo = button(t('smartInput.richUndo'), () => {
    strokes = undoAnnotationStroke(strokes);
    revision++;
    redraw();
  });
  undo.disabled = true;
  const save = button(t('smartInput.richSave'), () => {
    void exportImage();
  });
  save.disabled = true;
  const cancel = button(t('dialog.cancel'), close);
  footer.append(color, width, undo, cancel, save);
  body.append(canvas, status);
  modal.append(header, body, footer);
  overlay.append(modal);
  document.body.append(overlay);
  releaseBack = registerBackButtonLayer(close);
  options.signal?.addEventListener('abort', close, { once: true });
  overlay.addEventListener('keydown', (event) => {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      close();
      return;
    }
    trapAnnotationFocus(event, overlay);
  });
  image.onload = () => {
    if (isClosed()) return;
    if (image.naturalWidth * image.naturalHeight > 32_000_000) {
      status.textContent = t('smartInput.imageTooLarge');
      return;
    }
    canvas.width = image.naturalWidth;
    canvas.height = image.naturalHeight;
    loaded = true;
    redraw();
  };
  image.onerror = () => {
    status.textContent = t('smartInput.richImageFailed');
  };
  image.src = options.src;
  cancel.focus();

  canvas.addEventListener('pointerdown', (event) => {
    if (!loaded || saving || pointerId !== null) return;
    event.preventDefault();
    pointerId = event.pointerId;
    canvas.setPointerCapture(event.pointerId);
    current = {
      points: [
        annotationPoint(
          event.clientX,
          event.clientY,
          event.pressure,
          canvas.getBoundingClientRect(),
        ),
      ],
      color: color.value,
      width: Number(width.value) / Math.max(1, Math.min(canvas.clientWidth, canvas.clientHeight)),
    };
    redraw();
  });
  canvas.addEventListener('pointermove', (event) => {
    if (event.pointerId !== pointerId || !current) return;
    const coalesced =
      typeof event.getCoalescedEvents === 'function' ? event.getCoalescedEvents() : [];
    const events = coalesced.length ? coalesced : [event];
    for (const point of events)
      current.points.push(
        annotationPoint(
          point.clientX,
          point.clientY,
          point.pressure,
          canvas.getBoundingClientRect(),
        ),
      );
    redraw();
  });
  canvas.addEventListener('pointerup', (event) => {
    finishStroke(event, true);
  });
  canvas.addEventListener('pointercancel', (event) => {
    finishStroke(event, false);
  });
  canvas.addEventListener('lostpointercapture', (event) => {
    finishStroke(event, false);
  });
  function finishStroke(event: PointerEvent, keep: boolean): void {
    if (event.pointerId !== pointerId) return;
    if (current && keep) {
      current.points.push(
        annotationPoint(
          event.clientX,
          event.clientY,
          event.pressure,
          canvas.getBoundingClientRect(),
        ),
      );
      strokes.push(current);
      revision++;
    }
    current = null;
    pointerId = null;
    if (canvas.hasPointerCapture(event.pointerId)) canvas.releasePointerCapture(event.pointerId);
    redraw();
  }
  async function exportImage(): Promise<void> {
    if (!loaded || saving || current || isClosed()) return;
    if (!annotationHasChanges(strokes)) return;
    saving = true;
    save.disabled = true;
    undo.disabled = true;
    color.disabled = true;
    width.disabled = true;
    status.textContent = t('smartInput.richSaving');
    try {
      if (exported?.revision !== revision) {
        const blob = await new Promise<Blob>((resolve, reject) => {
          canvas.toBlob((value) => {
            if (value) resolve(value);
            else reject(new Error(t('smartInput.richImageFailed')));
          }, 'image/png');
        });
        controller.signal.throwIfAborted();
        exported = {
          revision,
          file: new File([blob], `annotated-${crypto.randomUUID()}.png`, { type: 'image/png' }),
        };
      }
      controller.signal.throwIfAborted();
      await options.save(exported.file, controller.signal);
      if (!isClosed()) close();
    } catch (error) {
      if (!isClosed())
        status.textContent =
          error instanceof Error ? error.message : t('smartInput.richImageFailed');
    } finally {
      saving = false;
      save.disabled = false;
      color.disabled = false;
      width.disabled = false;
      redraw();
    }
  }
}

import { $webPreviewViewport } from '../../stores';
import { getSessionPreview } from './webSessionState';

function getFrameViewport(frame: HTMLIFrameElement): { width: number; height: number } | null {
  const frameKey = frame.dataset.previewFrameKey;
  const separator = frameKey?.indexOf('::') ?? -1;
  if (frameKey && separator > 0) {
    const sessionId = frameKey.slice(0, separator);
    const previewName = frameKey.slice(separator + 2);
    return getSessionPreview(sessionId, previewName)?.viewport ?? null;
  }

  return $webPreviewViewport.get();
}

/**
 * Apply the stored responsive viewport to a preview frame.
 * Navigation and target changes recreate frames, so the fixed-size styling
 * must be derived from the store instead of living only on the old element.
 */
export function applyStoredViewportToFrame(frame: HTMLIFrameElement): void {
  const viewport = getFrameViewport(frame);
  frame.style.setProperty('--preview-background-width', `${viewport?.width ?? 1280}px`);
  frame.style.setProperty('--preview-background-height', `${viewport?.height ?? 720}px`);
  if (!viewport) {
    frame.style.width = '';
    frame.style.height = '';
    frame.style.maxWidth = '';
    frame.style.maxHeight = '';
    frame.style.left = '';
    frame.style.top = '';
    frame.style.transform = '';
    return;
  }

  frame.style.width = `${viewport.width}px`;
  frame.style.height = `${viewport.height}px`;
  frame.style.maxWidth = `${viewport.width}px`;
  frame.style.maxHeight = `${viewport.height}px`;
  frame.style.left = '50%';
  frame.style.top = '50%';
  frame.style.transform = 'translate(-50%, -50%)';
}

/** Apply a named preview's stored viewport without changing visibility or focus. */
export function applySessionViewportToFrame(sessionId: string, previewName: string): boolean {
  const frameKey = `${sessionId}::${previewName}`;
  const frame = Array.from(
    document.querySelectorAll<HTMLIFrameElement>('.web-preview-iframe'),
  ).find((candidate) => candidate.dataset.previewFrameKey === frameKey);
  if (!frame) {
    return false;
  }

  applyStoredViewportToFrame(frame);
  return true;
}

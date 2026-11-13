import { $webPreviewViewport } from '../../stores';
import { getSessionPreview } from './webSessionState';
export interface PreviewViewportMetrics {
  width: number;
  height: number;
  scale: number;
  frame: number[];
  host: number[];
  visible: boolean;
}

const containerScalingSupported =
  typeof CSS !== 'undefined' &&
  CSS.supports('transform', 'scale(min(1,calc(100cqw / 1440px),calc(100cqh / 900px)))');
const observedViewportHosts = new WeakSet<HTMLElement>();
const viewportObserver =
  containerScalingSupported || typeof ResizeObserver === 'undefined'
    ? null
    : new ResizeObserver((entries) => {
        for (const entry of entries) {
          for (const frame of entry.target.querySelectorAll<HTMLIFrameElement>(
            '.web-preview-iframe',
          )) {
            applyStoredViewportToFrame(frame);
          }
        }
      });

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
function configureViewportContainer(host: HTMLElement | null): void {
  if (host && containerScalingSupported) host.style.containerType = 'size';
}

export function applyStoredViewportToFrame(frame: HTMLIFrameElement): void {
  const viewport = getFrameViewport(frame);
  const host = frame.parentElement;
  configureViewportContainer(host);
  if (host && viewportObserver && !observedViewportHosts.has(host)) {
    observedViewportHosts.add(host);
    viewportObserver.observe(host);
  }
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
  // CSS container math also updates in background documents where ResizeObserver is suspended.
  if (containerScalingSupported) {
    frame.style.transform = `translate(-50%, -50%) scale(min(1, calc(100cqw / ${viewport.width}px), calc(100cqh / ${viewport.height}px)))`;
    return;
  }
  const scale = Math.min(
    1,
    (host?.clientWidth || viewport.width) / viewport.width,
    (host?.clientHeight || viewport.height) / viewport.height,
  );
  frame.style.transform = `translate(-50%, -50%) scale(${scale})`;
}

export function getSessionPreviewFrame(
  sessionId: string,
  previewName: string,
): HTMLIFrameElement | null {
  const frameKey = `${sessionId}::${previewName}`;
  return (
    Array.from(document.querySelectorAll<HTMLIFrameElement>('.web-preview-iframe')).find(
      (candidate) => candidate.dataset.previewFrameKey === frameKey,
    ) ?? null
  );
}

/** Apply a named preview's stored viewport without changing visibility or focus. */
export function applySessionViewportToFrame(sessionId: string, previewName: string): boolean {
  const frame = getSessionPreviewFrame(sessionId, previewName);
  if (!frame) {
    return false;
  }

  applyStoredViewportToFrame(frame);
  return true;
}

/** Measure the actual owning dock, not just the sandbox document's logical viewport. */
export function readPreviewViewportMetrics(
  sessionId: string,
  previewName: string,
): PreviewViewportMetrics | undefined {
  const frame = getSessionPreviewFrame(sessionId, previewName);
  const host = frame?.parentElement;
  if (!frame || !host) return undefined;
  const bounds = frame.getBoundingClientRect();
  const hostBounds = host.getBoundingClientRect();
  const transform = getComputedStyle(frame).transform;
  const rect = (value: DOMRect): number[] =>
    [value.x, value.y, value.width, value.height].map((n) => Math.round(n * 1000000) / 1000000);
  return {
    width: frame.clientWidth,
    height: frame.clientHeight,
    scale: transform === 'none' ? 1 : new DOMMatrixReadOnly(transform).a,
    frame: rect(bounds),
    host: rect(hostBounds),
    visible:
      !frame.classList.contains('hidden') &&
      bounds.width > 0 &&
      bounds.height > 0 &&
      document.visibilityState !== 'hidden',
  };
}

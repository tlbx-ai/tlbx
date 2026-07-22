/**
 * File Viewer Image View
 *
 * Interactive pan/zoom surface for image previews in the file viewer modal
 * and dock. Mouse wheel and trackpad pinch zoom anchor on the cursor,
 * pointer drag pans, two touch pointers pinch-zoom around the gesture
 * midpoint, and double click/tap toggles between fit and pixel zoom.
 * The transform math is exported as pure helpers for tests.
 */

import { t } from '../i18n';

export interface ViewTransform {
  scale: number;
  tx: number;
  ty: number;
}

export interface ViewGeometry {
  viewWidth: number;
  viewHeight: number;
  imageWidth: number;
  imageHeight: number;
}

export const MAX_ZOOM_SCALE = 16;
export const ZOOM_STEP_FACTOR = 1.5;
const WHEEL_ZOOM_SENSITIVITY = 0.0022;
const PIXELATED_MIN_SCALE = 3;
const TAP_MAX_TRAVEL_PX = 12;
const DOUBLE_TAP_MAX_DELAY_MS = 350;
const DOUBLE_TAP_MAX_DISTANCE_PX = 24;

export function computeFitScale(geometry: ViewGeometry): number {
  if (
    geometry.viewWidth <= 0 ||
    geometry.viewHeight <= 0 ||
    geometry.imageWidth <= 0 ||
    geometry.imageHeight <= 0
  ) {
    return 1;
  }
  return Math.min(
    geometry.viewWidth / geometry.imageWidth,
    geometry.viewHeight / geometry.imageHeight,
    1,
  );
}

export function clampTransform(transform: ViewTransform, geometry: ViewGeometry): ViewTransform {
  const width = geometry.imageWidth * transform.scale;
  const height = geometry.imageHeight * transform.scale;
  const tx =
    width <= geometry.viewWidth
      ? (geometry.viewWidth - width) / 2
      : Math.min(0, Math.max(geometry.viewWidth - width, transform.tx));
  const ty =
    height <= geometry.viewHeight
      ? (geometry.viewHeight - height) / 2
      : Math.min(0, Math.max(geometry.viewHeight - height, transform.ty));
  return { scale: transform.scale, tx, ty };
}

export function fitTransform(geometry: ViewGeometry): ViewTransform {
  return clampTransform({ scale: computeFitScale(geometry), tx: 0, ty: 0 }, geometry);
}

export function zoomAtPoint(
  current: ViewTransform,
  targetScale: number,
  anchorX: number,
  anchorY: number,
  geometry: ViewGeometry,
): ViewTransform {
  const scale = Math.min(Math.max(targetScale, computeFitScale(geometry)), MAX_ZOOM_SCALE);
  const imageX = (anchorX - current.tx) / current.scale;
  const imageY = (anchorY - current.ty) / current.scale;
  return clampTransform(
    { scale, tx: anchorX - imageX * scale, ty: anchorY - imageY * scale },
    geometry,
  );
}

export function panBy(
  current: ViewTransform,
  dx: number,
  dy: number,
  geometry: ViewGeometry,
): ViewTransform {
  return clampTransform(
    { scale: current.scale, tx: current.tx + dx, ty: current.ty + dy },
    geometry,
  );
}

export interface PinchBaseline {
  view: ViewTransform;
  midX: number;
  midY: number;
  distance: number;
}

export function pinchTransform(
  baseline: PinchBaseline,
  midX: number,
  midY: number,
  distance: number,
  geometry: ViewGeometry,
): ViewTransform {
  if (baseline.distance <= 0) {
    return clampTransform(baseline.view, geometry);
  }
  const scale = Math.min(
    Math.max(baseline.view.scale * (distance / baseline.distance), computeFitScale(geometry)),
    MAX_ZOOM_SCALE,
  );
  const imageX = (baseline.midX - baseline.view.tx) / baseline.view.scale;
  const imageY = (baseline.midY - baseline.view.ty) / baseline.view.scale;
  return clampTransform({ scale, tx: midX - imageX * scale, ty: midY - imageY * scale }, geometry);
}

export function normalizeWheelDeltaY(deltaY: number, deltaMode: number): number {
  if (deltaMode === 1) {
    return deltaY * 16;
  }
  if (deltaMode === 2) {
    return deltaY * 800;
  }
  return deltaY;
}

export function wheelZoomScale(currentScale: number, normalizedDeltaY: number): number {
  return currentScale * Math.exp(-normalizedDeltaY * WHEEL_ZOOM_SENSITIVITY);
}

export function doubleTapTargetScale(currentScale: number, fitScale: number): number {
  const atFit = currentScale <= fitScale * 1.01;
  if (!atFit) {
    return fitScale;
  }
  return fitScale < 0.995 ? 1 : Math.min(2, MAX_ZOOM_SCALE);
}

interface TrackedPointer {
  x: number;
  y: number;
  startX: number;
  startY: number;
  pinched: boolean;
}

function createHudButton(iconPath: string, label: string): HTMLButtonElement {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'file-viewer-zoom-btn';
  button.title = label;
  button.setAttribute('aria-label', label);
  button.innerHTML = `<svg width="16" height="16" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" aria-hidden="true"><path d="${iconPath}" /></svg>`;
  return button;
}

export function createImageView(viewUrl: string, fileName: string): HTMLElement {
  const stage = document.createElement('div');
  stage.className = 'file-viewer-image-stage';

  const img = document.createElement('img');
  img.className = 'file-viewer-image';
  img.alt = fileName;
  img.draggable = false;
  img.decoding = 'async';
  img.style.visibility = 'hidden';

  const hud = document.createElement('div');
  hud.className = 'file-viewer-zoom-hud';
  const zoomOutBtn = createHudButton('M3.5 8h9', t('fileViewer.zoomOut'));
  const levelBtn = document.createElement('button');
  levelBtn.type = 'button';
  levelBtn.className = 'file-viewer-zoom-level';
  levelBtn.title = t('fileViewer.zoomReset');
  levelBtn.setAttribute('aria-label', t('fileViewer.zoomReset'));
  levelBtn.textContent = '—';
  const zoomInBtn = createHudButton('M8 3.5v9M3.5 8h9', t('fileViewer.zoomIn'));
  hud.appendChild(zoomOutBtn);
  hud.appendChild(levelBtn);
  hud.appendChild(zoomInBtn);
  hud.addEventListener('pointerdown', (e) => {
    e.stopPropagation();
  });
  hud.addEventListener('dblclick', (e) => {
    e.stopPropagation();
  });

  const allowPixelated = !fileName.toLowerCase().endsWith('.svg');
  let geometry: ViewGeometry = { viewWidth: 0, viewHeight: 0, imageWidth: 0, imageHeight: 0 };
  let view: ViewTransform = { scale: 1, tx: 0, ty: 0 };
  let loaded = false;
  let followFit = true;

  const applyView = (next: ViewTransform): void => {
    view = next;
    img.style.transform = `translate(${view.tx}px, ${view.ty}px) scale(${view.scale})`;
    img.classList.toggle(
      'file-viewer-image-pixelated',
      allowPixelated && view.scale >= PIXELATED_MIN_SCALE,
    );
    levelBtn.textContent = `${Math.round(view.scale * 100)}%`;
    followFit = view.scale <= computeFitScale(geometry) * 1.001;
  };

  const refreshGeometry = (): void => {
    const rect = stage.getBoundingClientRect();
    geometry = {
      viewWidth: rect.width,
      viewHeight: rect.height,
      imageWidth: geometry.imageWidth,
      imageHeight: geometry.imageHeight,
    };
  };

  const fitWhenMeasurable = (attempt: number): void => {
    refreshGeometry();
    if ((geometry.viewWidth > 0 && geometry.viewHeight > 0) || attempt >= 20) {
      applyView(fitTransform(geometry));
      img.style.visibility = 'visible';
      return;
    }
    // setTimeout instead of requestAnimationFrame: layout retries must also run in background tabs
    setTimeout(() => {
      fitWhenMeasurable(attempt + 1);
    }, 40);
  };

  const handleLoad = (): void => {
    if (loaded) {
      return;
    }
    loaded = true;
    const width = img.naturalWidth > 0 ? img.naturalWidth : 512;
    const height = img.naturalHeight > 0 ? img.naturalHeight : 512;
    geometry = { ...geometry, imageWidth: width, imageHeight: height };
    img.style.width = `${width}px`;
    img.style.height = `${height}px`;
    fitWhenMeasurable(0);
  };

  img.addEventListener('load', handleLoad);
  img.addEventListener('error', () => {
    const error = document.createElement('div');
    error.className = 'file-viewer-error';
    error.textContent = t('fileViewer.failedToLoadFile');
    stage.replaceChildren(error);
  });

  const observer = new ResizeObserver(() => {
    if (!loaded) {
      return;
    }
    const keepFit = followFit;
    refreshGeometry();
    applyView(keepFit ? fitTransform(geometry) : clampTransform(view, geometry));
  });
  observer.observe(stage);

  const localPoint = (e: { clientX: number; clientY: number }): { x: number; y: number } => {
    const rect = stage.getBoundingClientRect();
    return { x: e.clientX - rect.left, y: e.clientY - rect.top };
  };

  stage.addEventListener(
    'wheel',
    (e) => {
      if (!loaded) {
        return;
      }
      e.preventDefault();
      const point = localPoint(e);
      const target = wheelZoomScale(view.scale, normalizeWheelDeltaY(e.deltaY, e.deltaMode));
      applyView(zoomAtPoint(view, target, point.x, point.y, geometry));
    },
    { passive: false },
  );

  const pointers = new Map<number, TrackedPointer>();
  let pinchBaseline: PinchBaseline | null = null;
  let lastTapTime = 0;
  let lastTapX = 0;
  let lastTapY = 0;
  let suppressDblclickUntil = 0;

  const armPinch = (): void => {
    const values = [...pointers.values()];
    const a = values[0];
    const b = values[1];
    if (a === undefined || b === undefined) {
      return;
    }
    pinchBaseline = {
      view,
      midX: (a.x + b.x) / 2,
      midY: (a.y + b.y) / 2,
      distance: Math.hypot(a.x - b.x, a.y - b.y),
    };
    for (const pointer of pointers.values()) {
      pointer.pinched = true;
    }
  };

  const toggleZoom = (x: number, y: number): void => {
    const target = doubleTapTargetScale(view.scale, computeFitScale(geometry));
    applyView(zoomAtPoint(view, target, x, y, geometry));
  };

  stage.addEventListener('pointerdown', (e) => {
    if (!loaded || (e.pointerType === 'mouse' && e.button !== 0)) {
      return;
    }
    e.preventDefault();
    try {
      stage.setPointerCapture(e.pointerId);
    } catch {
      // pointer already released before capture; pan still works while it hovers the stage
    }
    const point = localPoint(e);
    pointers.set(e.pointerId, {
      x: point.x,
      y: point.y,
      startX: point.x,
      startY: point.y,
      pinched: false,
    });
    if (pointers.size === 2) {
      armPinch();
    } else {
      pinchBaseline = null;
    }
    if (pointers.size === 1) {
      stage.classList.add('is-panning');
    }
  });

  stage.addEventListener('pointermove', (e) => {
    const tracked = pointers.get(e.pointerId);
    if (!tracked) {
      return;
    }
    const point = localPoint(e);
    if (pointers.size === 2 && pinchBaseline) {
      tracked.x = point.x;
      tracked.y = point.y;
      const values = [...pointers.values()];
      const a = values[0];
      const b = values[1];
      if (a === undefined || b === undefined) {
        return;
      }
      applyView(
        pinchTransform(
          pinchBaseline,
          (a.x + b.x) / 2,
          (a.y + b.y) / 2,
          Math.hypot(a.x - b.x, a.y - b.y),
          geometry,
        ),
      );
    } else if (pointers.size === 1) {
      const dx = point.x - tracked.x;
      const dy = point.y - tracked.y;
      tracked.x = point.x;
      tracked.y = point.y;
      applyView(panBy(view, dx, dy, geometry));
    }
  });

  const releasePointer = (e: PointerEvent): void => {
    const tracked = pointers.get(e.pointerId);
    if (!tracked) {
      return;
    }
    pointers.delete(e.pointerId);
    if (stage.hasPointerCapture(e.pointerId)) {
      stage.releasePointerCapture(e.pointerId);
    }
    if (pointers.size === 2) {
      armPinch();
    } else if (pointers.size < 2) {
      pinchBaseline = null;
    }
    if (pointers.size === 0) {
      stage.classList.remove('is-panning');
    }

    const point = localPoint(e);
    const isTap =
      e.type === 'pointerup' &&
      e.pointerType === 'touch' &&
      !tracked.pinched &&
      pointers.size === 0 &&
      Math.hypot(point.x - tracked.startX, point.y - tracked.startY) <= TAP_MAX_TRAVEL_PX;
    if (!isTap) {
      return;
    }

    const now = performance.now();
    const isDoubleTap =
      now - lastTapTime <= DOUBLE_TAP_MAX_DELAY_MS &&
      Math.hypot(point.x - lastTapX, point.y - lastTapY) <= DOUBLE_TAP_MAX_DISTANCE_PX;
    if (isDoubleTap) {
      lastTapTime = 0;
      suppressDblclickUntil = now + 700;
      toggleZoom(point.x, point.y);
    } else {
      lastTapTime = now;
      lastTapX = point.x;
      lastTapY = point.y;
    }
  };

  stage.addEventListener('pointerup', releasePointer);
  stage.addEventListener('pointercancel', releasePointer);

  stage.addEventListener('dblclick', (e) => {
    if (!loaded || performance.now() < suppressDblclickUntil) {
      return;
    }
    e.preventDefault();
    const point = localPoint(e);
    toggleZoom(point.x, point.y);
  });

  const zoomFromCenter = (factor: number): void => {
    applyView(
      zoomAtPoint(
        view,
        view.scale * factor,
        geometry.viewWidth / 2,
        geometry.viewHeight / 2,
        geometry,
      ),
    );
  };

  zoomInBtn.addEventListener('click', () => {
    zoomFromCenter(ZOOM_STEP_FACTOR);
  });
  zoomOutBtn.addEventListener('click', () => {
    zoomFromCenter(1 / ZOOM_STEP_FACTOR);
  });
  levelBtn.addEventListener('click', () => {
    applyView(fitTransform(geometry));
  });

  img.src = viewUrl;
  stage.appendChild(img);
  stage.appendChild(hud);
  if (img.complete && img.naturalWidth > 0) {
    handleLoad();
  }
  return stage;
}

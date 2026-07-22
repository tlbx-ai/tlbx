import { describe, expect, it, vi } from 'vitest';

vi.mock('../i18n', () => ({ t: (key: string) => key }));

import {
  MAX_ZOOM_SCALE,
  clampTransform,
  computeFitScale,
  doubleTapTargetScale,
  fitTransform,
  normalizeWheelDeltaY,
  panBy,
  pinchTransform,
  wheelZoomScale,
  zoomAtPoint,
  type ViewGeometry,
} from './imageView';

const geometry = (
  viewWidth: number,
  viewHeight: number,
  imageWidth: number,
  imageHeight: number,
): ViewGeometry => ({ viewWidth, viewHeight, imageWidth, imageHeight });

describe('image view transform math', () => {
  it('fits large images inside the viewport and never upscales past 100%', () => {
    expect(computeFitScale(geometry(800, 600, 1600, 1200))).toBe(0.5);
    expect(computeFitScale(geometry(800, 600, 3200, 600))).toBe(0.25);
    expect(computeFitScale(geometry(800, 600, 100, 100))).toBe(1);
    expect(computeFitScale(geometry(0, 600, 100, 100))).toBe(1);
  });

  it('centers images smaller than the viewport on both axes', () => {
    const clamped = clampTransform({ scale: 1, tx: -500, ty: 999 }, geometry(800, 600, 400, 200));
    expect(clamped.tx).toBe(200);
    expect(clamped.ty).toBe(200);
  });

  it('clamps pan so a zoomed image cannot leave the viewport', () => {
    const g = geometry(800, 600, 1600, 1200);
    expect(panBy({ scale: 1, tx: -300, ty: -200 }, 5000, 5000, g)).toEqual({
      scale: 1,
      tx: 0,
      ty: 0,
    });
    expect(panBy({ scale: 1, tx: -300, ty: -200 }, -5000, -5000, g)).toEqual({
      scale: 1,
      tx: -800,
      ty: -600,
    });
  });

  it('keeps the anchor point on the same image pixel while zooming', () => {
    const g = geometry(800, 600, 4000, 3000);
    const before = { scale: 0.5, tx: -600, ty: -450 };
    const anchorX = 400;
    const anchorY = 300;
    const after = zoomAtPoint(before, 1, anchorX, anchorY, g);
    const imageXBefore = (anchorX - before.tx) / before.scale;
    const imageXAfter = (anchorX - after.tx) / after.scale;
    const imageYBefore = (anchorY - before.ty) / before.scale;
    const imageYAfter = (anchorY - after.ty) / after.scale;
    expect(after.scale).toBe(1);
    expect(imageXAfter).toBeCloseTo(imageXBefore, 6);
    expect(imageYAfter).toBeCloseTo(imageYBefore, 6);
  });

  it('clamps zoom between the fit scale and the maximum scale', () => {
    const g = geometry(800, 600, 1600, 1200);
    expect(zoomAtPoint({ scale: 1, tx: 0, ty: 0 }, 0.01, 400, 300, g).scale).toBe(0.5);
    expect(zoomAtPoint({ scale: 1, tx: 0, ty: 0 }, 999, 400, 300, g).scale).toBe(MAX_ZOOM_SCALE);
  });

  it('recenters when zooming all the way back out to fit', () => {
    const g = geometry(800, 600, 1600, 1200);
    const zoomedOut = zoomAtPoint({ scale: 2, tx: -900, ty: -700 }, 0.1, 123, 456, g);
    expect(zoomedOut).toEqual(fitTransform(g));
  });

  it('scales with pinch distance around the gesture midpoint', () => {
    const g = geometry(800, 600, 4000, 3000);
    const baseline = {
      view: { scale: 0.5, tx: -600, ty: -450 },
      midX: 400,
      midY: 300,
      distance: 100,
    };
    const pinched = pinchTransform(baseline, 400, 300, 200, g);
    expect(pinched.scale).toBe(1);
    const imageXBefore = (baseline.midX - baseline.view.tx) / baseline.view.scale;
    const imageXAfter = (400 - pinched.tx) / pinched.scale;
    expect(imageXAfter).toBeCloseTo(imageXBefore, 6);
  });

  it('pans with the pinch midpoint while scaling', () => {
    const g = geometry(800, 600, 4000, 3000);
    const baseline = {
      view: { scale: 1, tx: -1600, ty: -1200 },
      midX: 400,
      midY: 300,
      distance: 100,
    };
    const moved = pinchTransform(baseline, 300, 250, 100, g);
    expect(moved.scale).toBe(1);
    expect(moved.tx).toBe(-1700);
    expect(moved.ty).toBe(-1250);
  });

  it('ignores pinch baselines without distance', () => {
    const g = geometry(800, 600, 1600, 1200);
    const baseline = { view: { scale: 1, tx: -100, ty: -100 }, midX: 0, midY: 0, distance: 0 };
    expect(pinchTransform(baseline, 50, 50, 300, g)).toEqual(clampTransform(baseline.view, g));
  });

  it('normalizes line and page wheel deltas to pixels', () => {
    expect(normalizeWheelDeltaY(3, 0)).toBe(3);
    expect(normalizeWheelDeltaY(3, 1)).toBe(48);
    expect(normalizeWheelDeltaY(1, 2)).toBe(800);
  });

  it('zooms in on negative wheel deltas and out on positive ones', () => {
    expect(wheelZoomScale(1, -100)).toBeGreaterThan(1);
    expect(wheelZoomScale(1, 100)).toBeLessThan(1);
    expect(wheelZoomScale(2, 0)).toBe(2);
  });

  it('toggles double tap between fit, pixel zoom, and back', () => {
    expect(doubleTapTargetScale(0.5, 0.5)).toBe(1);
    expect(doubleTapTargetScale(1, 0.5)).toBe(0.5);
    expect(doubleTapTargetScale(1, 1)).toBe(2);
    expect(doubleTapTargetScale(2, 1)).toBe(1);
  });
});

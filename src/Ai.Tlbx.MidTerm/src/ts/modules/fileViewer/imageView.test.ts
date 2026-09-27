import { describe, expect, it, vi } from 'vitest';

vi.mock('../i18n', () => ({ t: (key: string) => key }));

import { panBy, pinchTransform, zoomAtPoint, type ViewGeometry } from './imageView';

const geometry = (
  viewWidth: number,
  viewHeight: number,
  imageWidth: number,
  imageHeight: number,
): ViewGeometry => ({ viewWidth, viewHeight, imageWidth, imageHeight });

describe('image view transform math', () => {
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
});

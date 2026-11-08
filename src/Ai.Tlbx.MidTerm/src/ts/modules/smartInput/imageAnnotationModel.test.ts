import { describe, expect, it, vi } from 'vitest';
import { annotationHasChanges, annotationPoint, undoAnnotationStroke, type AnnotationStroke } from './imageAnnotationModel';
import { uploadRichComposerFile } from './richComposerUpload';

describe('image annotation staging contracts', () => {
  it('requires actual retained pen marks before exporting or uploading', () => {
    expect(annotationHasChanges([])).toBe(false);
    const strokes = [{ color: '#000000', width: 1, points: [{ x: 0, y: 0, pressure: 1 }] }];
    expect(annotationHasChanges(strokes)).toBe(true);
    expect(annotationHasChanges(undoAnnotationStroke(strokes))).toBe(false);
  });
  it('maps pointer pen and touch coordinates independently of viewport scaling and clamps edges', () => {
    expect(annotationPoint(250, 175, 0.7, { left: 100, top: 100, width: 300, height: 150 }))
      .toEqual({ x: 0.5, y: 0.5, pressure: 0.7 });
    expect(annotationPoint(-1, 999, 0, { left: 0, top: 0, width: 100, height: 100 }))
      .toEqual({ x: 0, y: 1, pressure: 0.5 });
  });
  it('undoes only the last completed stroke without mutating earlier stroke data', () => {
    const original: AnnotationStroke[] = [
      { color: '#ff0000', width: 0.01, points: [{ x: 0, y: 0, pressure: 0.5 }] },
      { color: '#0000ff', width: 0.02, points: [{ x: 1, y: 1, pressure: 1 }] },
    ];
    expect(undoAnnotationStroke(original)).toEqual([original[0]]);
    expect(original).toHaveLength(2);
  });
  it('cancels before upload without calling the uploader', async () => {
    const controller = new AbortController(); controller.abort(); const upload = vi.fn();
    await expect(uploadRichComposerFile({ sessionId: 's', file: new File(['png'], 'new.png', { type: 'image/png' }),
      signal: controller.signal, uploadFile: upload, tooLarge: 'large', failed: 'failed' })).rejects.toHaveProperty('name', 'AbortError');
    expect(upload).not.toHaveBeenCalled();
  });
  it('cancels an in-flight save before any new ref is committed and leaves the original untouched', async () => {
    const original = { id: 'original', uploadedPath: '/original.png', displayName: 'same.png' };
    const before = structuredClone(original); const controller = new AbortController();
    let finish!: (path: string) => void;
    const upload = vi.fn(() => new Promise<string>((resolve) => { finish = resolve; }));
    const commit = vi.fn();
    const pending = uploadRichComposerFile({ sessionId: 's', file: new File(['png'], 'annotated-new.png', { type: 'image/png' }),
      originalPath: original.uploadedPath, signal: controller.signal, uploadFile: upload, tooLarge: 'large', failed: 'failed' }).then(commit);
    const rejected = expect(pending).rejects.toHaveProperty('name', 'AbortError');
    controller.abort(); finish('/new.png'); await rejected;
    expect(commit).not.toHaveBeenCalled(); expect(original).toEqual(before);
  });
  it('saves to a fresh path and refuses an uploader result that aliases the original', async () => {
    const file = new File(['png'], 'annotated-new.png', { type: 'image/png' });
    const options = { sessionId: 's', file, originalPath: '/original.png', tooLarge: 'large', failed: 'failed' };
    expect(await uploadRichComposerFile({ ...options, uploadFile: vi.fn().mockResolvedValue('/new.png') })).toBe('/new.png');
    await expect(uploadRichComposerFile({ ...options, uploadFile: vi.fn().mockResolvedValue('/original.png') })).rejects.toThrow('failed');
  });
});

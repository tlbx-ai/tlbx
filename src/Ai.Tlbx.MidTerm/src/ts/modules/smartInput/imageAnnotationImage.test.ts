import { afterEach, describe, expect, it, vi } from 'vitest';
vi.mock('../i18n', () => ({ t: (key: string) => key }));
vi.mock('../navigation/backButtonGuard', () => ({ registerBackButtonLayer: vi.fn() }));
import { loadAnnotationImageUrl } from './imageAnnotation';

afterEach(() => {
  vi.unstubAllGlobals();
  vi.restoreAllMocks();
});

describe('annotation attachment image loading', () => {
  it('loads the exact attachment bytes into an origin-local object URL for canvas export', async () => {
    const blob = new Blob(['owned-image-bytes'], { type: 'image/png' });
    const fetchImage = vi.fn().mockResolvedValue({ ok: true, blob: async () => blob });
    vi.stubGlobal('fetch', fetchImage);
    const create = vi.spyOn(URL, 'createObjectURL').mockReturnValue('blob:owned-annotation');
    const controller = new AbortController();
    expect(await loadAnnotationImageUrl('/api/files/view?path=own.png', controller.signal)).toBe(
      'blob:owned-annotation',
    );
    expect(fetchImage).toHaveBeenCalledWith('/api/files/view?path=own.png', {
      signal: controller.signal,
    });
    expect(create).toHaveBeenCalledWith(blob);
  });
  it('does not fetch or allocate a resource after the dialog was cancelled', async () => {
    const fetchImage = vi.fn();
    vi.stubGlobal('fetch', fetchImage);
    const create = vi.spyOn(URL, 'createObjectURL');
    const controller = new AbortController();
    controller.abort();
    await expect(loadAnnotationImageUrl('/own.png', controller.signal)).rejects.toHaveProperty(
      'name',
      'AbortError',
    );
    expect(fetchImage).not.toHaveBeenCalled();
    expect(create).not.toHaveBeenCalled();
  });
  it('rejects a failed attachment response without allocating an image resource', async () => {
    vi.stubGlobal('fetch', vi.fn().mockResolvedValue({ ok: false }));
    const create = vi.spyOn(URL, 'createObjectURL');
    await expect(loadAnnotationImageUrl('/own.png', new AbortController().signal)).rejects.toThrow(
      'smartInput.richImageFailed',
    );
    expect(create).not.toHaveBeenCalled();
  });
  it('does not allocate a late body after cancellation while downloading', async () => {
    let finish!: (blob: Blob) => void;
    vi.stubGlobal(
      'fetch',
      vi.fn().mockResolvedValue({
        ok: true,
        blob: () =>
          new Promise<Blob>((resolve) => {
            finish = resolve;
          }),
      }),
    );
    const create = vi.spyOn(URL, 'createObjectURL');
    const controller = new AbortController();
    const pending = loadAnnotationImageUrl('/own.png', controller.signal);
    const rejected = expect(pending).rejects.toHaveProperty('name', 'AbortError');
    await Promise.resolve();
    controller.abort();
    finish(new Blob(['late']));
    await rejected;
    expect(create).not.toHaveBeenCalled();
  });
});

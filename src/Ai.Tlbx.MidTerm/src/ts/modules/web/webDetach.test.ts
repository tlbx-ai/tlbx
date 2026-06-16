import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('./webPanel', () => ({
  loadPreview: vi.fn(),
}));

vi.mock('./webDock', () => ({
  hideWebPreviewDockForDetach: vi.fn(),
  openWebPreviewDock: vi.fn(),
}));

vi.mock('../logging', () => ({
  createLogger: () => ({
    info: vi.fn(),
    warn: vi.fn(),
  }),
}));

vi.mock('./webApi', () => ({
  createBrowserPreviewClient: vi.fn(),
}));

vi.mock('../sidebar/voiceSection', () => ({
  isDevMode: vi.fn(() => false),
}));

vi.mock('./previewSandbox', () => ({
  shouldSandboxPreviewFrame: vi.fn(() => false),
}));

vi.mock('./webSessionState', () => ({
  getActivePreviewName: vi.fn(() => 'default'),
  getSessionPreview: vi.fn(),
  setSessionDockedClient: vi.fn(),
  setSessionMode: vi.fn(),
  setSessionSelectedPreviewName: vi.fn(
    (_sessionId: string, previewName?: string) => previewName ?? 'default',
  ),
  setSessionUrl: vi.fn(),
}));

import { $activeSessionId, $webPreviewDetached } from '../../stores';
import { createBrowserPreviewClient } from './webApi';
import { hideWebPreviewDockForDetach, openWebPreviewDock } from './webDock';
import { cleanupDetach, detachPreview, dockBack } from './webDetach';
import { loadPreview } from './webPanel';
import { getSessionPreview, setSessionDockedClient, setSessionMode } from './webSessionState';

describe('webDetach dock layout sync', () => {
  const popupReplace = vi.fn();
  const windowOpen = vi.fn();

  beforeEach(() => {
    $activeSessionId.set('s1');
    $webPreviewDetached.set(false);

    vi.mocked(getSessionPreview).mockReturnValue({
      mode: 'docked',
      previewName: 'default',
      sessionId: 's1',
      url: 'https://example.com/',
    });

    vi.mocked(createBrowserPreviewClient).mockResolvedValue({
      sessionId: 's1',
      previewName: 'default',
      routeKey: 'route-1',
      previewId: 'preview-1',
      previewToken: 'token-1',
      origin: 'https://preview.midterm.test',
    });

    popupReplace.mockReset();
    vi.stubGlobal(
      'BroadcastChannel',
      class {
        public onmessage: ((event: MessageEvent) => void) | null = null;

        close(): void {}

        postMessage(): void {}
      },
    );
    windowOpen.mockImplementation(
      () =>
        ({
          closed: false,
          close: vi.fn(),
          focus: vi.fn(),
          document: {
            title: '',
            body: {
              textContent: '',
            },
          },
          location: {
            replace: popupReplace,
          },
        }) as unknown as Window,
    );
    vi.stubGlobal('window', {
      open: windowOpen,
      focus: vi.fn(),
    });
  });

  afterEach(() => {
    cleanupDetach();
    vi.restoreAllMocks();
    $activeSessionId.set(null);
    $webPreviewDetached.set(false);
  });

  it('collapses dock reserve immediately when the active preview detaches', async () => {
    await detachPreview('s1', 'default');

    expect(setSessionDockedClient).toHaveBeenCalledWith(
      's1',
      'default',
      expect.objectContaining({
        previewId: 'preview-1',
        previewToken: 'token-1',
        routeKey: 'route-1',
      }),
    );
    expect(setSessionMode).toHaveBeenCalledWith('s1', 'default', 'detached');
    expect(hideWebPreviewDockForDetach).toHaveBeenCalledTimes(1);
    expect($webPreviewDetached.get()).toBe(true);
    expect(popupReplace).toHaveBeenCalledTimes(1);
    expect(String(popupReplace.mock.calls[0]?.[0] ?? '')).not.toContain('&mobile=1');
  });

  it('passes mobile mode to the detached popup when requested', async () => {
    await detachPreview('s1', 'default', { mobileMode: true });

    expect(popupReplace).toHaveBeenCalledTimes(1);
    expect(String(popupReplace.mock.calls[0]?.[0] ?? '')).toContain('&mobile=1');
  });

  it('reopens the dock immediately when the active preview docks back', () => {
    $webPreviewDetached.set(true);

    dockBack('s1', 'default');

    expect(setSessionMode).toHaveBeenCalledWith('s1', 'default', 'docked');
    expect(openWebPreviewDock).toHaveBeenCalledTimes(1);
    expect(loadPreview).toHaveBeenCalledTimes(1);
    expect($webPreviewDetached.get()).toBe(false);
  });
});

import fs from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';
import { shouldReloadPreviewFrame } from './previewLoadToken';
import { buildProxyUrl } from './previewProxyUrl';
import type { BrowserStatusResponse } from './webApi';
import { buildBrowserPreviewStatusIndicatorState } from './webPreviewStatus';

describe('webPanel preview reload decision', () => {
  it('reloads when the upstream target revision changes even if the proxy URL stays the same', () => {
    const frame = {
      src: 'https://localhost:2000/webpreview/route/',
      dataset: {
        mtPreviewLoadToken: '1:https://example.com/',
      },
    } as Pick<HTMLIFrameElement, 'src' | 'dataset'>;

    expect(
      shouldReloadPreviewFrame(
        frame,
        'https://localhost:2000/webpreview/route/',
        'https://example.org/',
        2,
      ),
    ).toBe(true);
  });

  it('does not reload when the proxy URL and target revision token are unchanged', () => {
    const frame = {
      src: 'https://localhost:2000/webpreview/route/',
      dataset: {
        mtPreviewLoadToken: '2:https://example.org/',
      },
    } as Pick<HTMLIFrameElement, 'src' | 'dataset'>;

    expect(
      shouldReloadPreviewFrame(
        frame,
        'https://localhost:2000/webpreview/route/',
        'https://example.org/',
        2,
      ),
    ).toBe(false);
  });

  it('changes the iframe proxy URL when the target revision changes', () => {
    const previewClient = {
      routeKey: 'route',
      previewId: 'preview-1',
      previewToken: 'token-1',
    } as const;

    const first = buildProxyUrl('https://example.com/', previewClient, 1, 'https://localhost:2000');
    const second = buildProxyUrl(
      'https://example.com/',
      previewClient,
      2,
      'https://localhost:2000',
    );

    expect(first).not.toBe(second);
    expect(first).toContain('__mtTargetRevision=1');
    expect(second).toContain('__mtTargetRevision=2');
  });
});

function createBrowserStatus(
  overrides: Partial<BrowserStatusResponse> = {},
): BrowserStatusResponse {
  return {
    connected: true,
    controllable: true,
    hasTarget: true,
    hasUiClient: true,
    isScoped: true,
    state: 'ready',
    scopeDescription: null,
    statusMessage: null,
    connectedClientCount: 1,
    totalConnectedClientCount: 1,
    connectedUiClientCount: 1,
    targetUrl: 'https://localhost:3000',
    ownerBrowserId: 'b1',
    ownerConnected: true,
    defaultClient: {
      sessionId: 's1',
      previewName: 'default',
      previewId: 'p1',
      browserId: 'b1',
      connectedAtUtc: '2026-01-01T00:00:00Z',
      isMainBrowser: false,
      isVisible: true,
      hasFocus: true,
      isTopLevel: true,
    },
    clients: [],
    ...overrides,
  };
}

describe('webPanel browser status indicator', () => {
  it('reports a hard error when no MidTerm browser UI is attached', () => {
    expect(
      buildBrowserPreviewStatusIndicatorState(createBrowserStatus({ hasUiClient: false })),
    ).toEqual({
      severity: 'error',
      message:
        'No MidTerm browser tab is connected to /ws/state. The dev browser cannot work until a live MidTerm tab is open.',
    });
  });

  it('reports a warning when the preview target is not controllable yet', () => {
    expect(
      buildBrowserPreviewStatusIndicatorState(
        createBrowserStatus({
          controllable: false,
          statusMessage: 'Target exists, but the preview is not controllable yet.',
        }),
      ),
    ).toEqual({
      severity: 'warn',
      message: 'Target exists, but the preview is not controllable yet.',
    });
  });

  it('reports an info state when the attached preview is backgrounded', () => {
    expect(
      buildBrowserPreviewStatusIndicatorState(
        createBrowserStatus({
          defaultClient: {
            sessionId: 's1',
            previewName: 'default',
            previewId: 'p1',
            browserId: 'b1',
            connectedAtUtc: '2026-01-01T00:00:00Z',
            isMainBrowser: false,
            isVisible: false,
            hasFocus: false,
            isTopLevel: true,
          },
        }),
      ),
    ).toEqual({
      severity: 'info',
      message:
        'The attached browser preview is currently in a background tab or window. Automation may be slower or throttled there.',
    });
  });

  it('returns null when the preview is healthy and controllable', () => {
    expect(buildBrowserPreviewStatusIndicatorState(createBrowserStatus())).toBeNull();
  });
});

describe('webPanel preview tabs', () => {
  it('keeps Go navigation aligned with the backend target revision', () => {
    const source = fs.readFileSync(path.resolve(__dirname, './webPanel.ts'), 'utf8');

    expect(source).toContain('upsertSessionPreview(result);');
    expect(source).toContain('setCurrentPreviewUrl(result.url ?? url);');
  });

  it('keeps URL bar shortcuts browser-like', () => {
    const source = fs.readFileSync(path.resolve(__dirname, './webPanel.ts'), 'utf8');

    expect(source).toContain("if (e.key === 'Escape')");
    expect(source).toContain('restoreCurrentUrlToInput();');
    expect(source).toContain("e.key.toLowerCase() === 'l'");
    expect(source).toContain('urlInput.select();');
  });

  it('asks the embedded bridge to refresh browser state when a frame is shown', () => {
    const source = fs.readFileSync(path.resolve(__dirname, './webPanel.ts'), 'utf8');

    expect(source).toContain(
      'const PREVIEW_VISIBILITY_REFRESH_DELAYS_MS = [0, 50, 200, 500] as const;',
    );
    expect(source).toContain("{ type: 'mt-refresh-browser-state', force: true, visible }");
    expect(source).toContain('refreshPreviewBridgeVisibility(frame, isActive);');
  });

  it('wires a close button for every preview tab', () => {
    const source = fs.readFileSync(path.resolve(__dirname, './webPanel.ts'), 'utf8');

    expect(source).toContain(
      'let previewTabCloseHandler: ((previewName: string) => void) | null = null;',
    );
    expect(source).toContain("closeButton.className = 'web-preview-tab-close';");
    expect(source).toContain('previewTabCloseHandler?.(preview.previewName);');
  });
});

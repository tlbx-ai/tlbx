import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { createWsUrl } from './websocket';

describe('createWsUrl', () => {
  const originalLocation = globalThis.location;
  const storage = new Map<string, string>();

  beforeEach(() => {
    storage.clear();

    vi.stubGlobal('location', {
      protocol: 'https:',
      host: 'midterm.example',
      pathname: '/',
    });

    vi.stubGlobal('sessionStorage', {
      getItem: vi.fn((key: string) => storage.get(key) ?? null),
      setItem: vi.fn((key: string, value: string) => {
        storage.set(key, value);
      }),
    });

    vi.stubGlobal('crypto', {
      randomUUID: vi.fn(() => 'tab-123'),
    });
    vi.stubGlobal('navigator', {
      userAgent:
        'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/140.0.0.0 Safari/537.36',
      platform: 'Win32',
      maxTouchPoints: 0,
    });
  });

  afterEach(() => {
    vi.unstubAllGlobals();
    if (originalLocation) {
      vi.stubGlobal('location', originalLocation);
    }
  });

  it('appends a stable tabId query parameter', () => {
    const url = createWsUrl('/ws/state?existing=1');
    const parsed = new URL(url);

    expect(parsed.protocol).toBe('wss:');
    expect(parsed.host).toBe('midterm.example');
    expect(parsed.pathname).toBe('/ws/state');
    expect(parsed.searchParams.get('existing')).toBe('1');
    expect(parsed.searchParams.get('tabId')).toBe('tab-123');
    expect(parsed.searchParams.get('deviceLabel')).toBe('Windows PC · Chrome');
    expect(createWsUrl('/ws/state').includes('tabId=tab-123')).toBe(true);
  });

  it('routes websocket paths through the active web preview proxy prefix', () => {
    vi.stubGlobal('location', {
      protocol: 'https:',
      host: 'midterm.example',
      pathname: '/webpreview/route-123/',
    });

    const parsed = new URL(createWsUrl('/ws/state'));

    expect(parsed.protocol).toBe('wss:');
    expect(parsed.host).toBe('midterm.example');
    expect(parsed.pathname).toBe('/webpreview/route-123/ws/state');
    expect(parsed.searchParams.get('tabId')).toBe('tab-123');
  });
});

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

interface IdentityMessage {
  type: 'probe' | 'occupied';
  tabId: string;
  runtimeId: string;
  targetRuntimeId?: string;
}

class FakeBroadcastChannel {
  static channels = new Map<string, Set<FakeBroadcastChannel>>();

  onmessage: ((event: MessageEvent<IdentityMessage>) => void) | null = null;

  constructor(private readonly name: string) {
    const peers = FakeBroadcastChannel.channels.get(name) ?? new Set<FakeBroadcastChannel>();
    peers.add(this);
    FakeBroadcastChannel.channels.set(name, peers);
  }

  postMessage(message: IdentityMessage): void {
    FakeBroadcastChannel.channels.get(this.name)?.forEach((peer) => {
      if (peer !== this) peer.onmessage?.({ data: message } as MessageEvent<IdentityMessage>);
    });
  }

  close(): void {
    FakeBroadcastChannel.channels.get(this.name)?.delete(this);
  }
}

function createStorage(initialTabId?: string): Storage {
  const values = new Map<string, string>();
  if (initialTabId) values.set('mt-tab-id', initialTabId);
  return {
    getItem: (key) => values.get(key) ?? null,
    setItem: (key, value) => values.set(key, value),
    removeItem: (key) => values.delete(key),
    clear: () => values.clear(),
    key: (index) => Array.from(values.keys())[index] ?? null,
    get length() {
      return values.size;
    },
  };
}

describe('browser tab identity', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    vi.resetModules();
    FakeBroadcastChannel.channels.clear();
    vi.stubGlobal('BroadcastChannel', FakeBroadcastChannel);
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    FakeBroadcastChannel.channels.clear();
  });

  it('rekeys a duplicated tab whose sessionStorage ID is already occupied', async () => {
    const randomUUID = vi
      .fn()
      .mockReturnValueOnce('runtime-existing')
      .mockReturnValueOnce('runtime-duplicate')
      .mockReturnValueOnce('tab-rekeyed');
    vi.stubGlobal('crypto', { randomUUID });

    vi.stubGlobal('sessionStorage', createStorage('tab-copied'));
    const existingModule = await import('./cookies');
    const existingIdentity = existingModule.initializeTabIdentity();
    await vi.advanceTimersByTimeAsync(60);
    await expect(existingIdentity).resolves.toBe('tab-copied');

    vi.resetModules();
    const duplicateStorage = createStorage('tab-copied');
    vi.stubGlobal('sessionStorage', duplicateStorage);
    const duplicateModule = await import('./cookies');
    const duplicateIdentity = duplicateModule.initializeTabIdentity();
    await vi.advanceTimersByTimeAsync(60);

    await expect(duplicateIdentity).resolves.toBe('tab-rekeyed');
    expect(duplicateStorage.getItem('mt-tab-id')).toBe('tab-rekeyed');
  });

  it('keeps the tab identity when BroadcastChannel is unavailable at runtime', async () => {
    vi.stubGlobal(
      'BroadcastChannel',
      class {
        constructor() {
          throw new Error('blocked');
        }
      },
    );
    vi.stubGlobal('sessionStorage', createStorage('tab-existing'));
    vi.stubGlobal('crypto', { randomUUID: () => 'runtime-id' });

    const { initializeTabIdentity } = await import('./cookies');

    await expect(initializeTabIdentity()).resolves.toBe('tab-existing');
  });

  it('describes common browser devices without exposing an opaque identifier', async () => {
    vi.stubGlobal('navigator', {
      userAgent:
        'Mozilla/5.0 (iPad; CPU OS 18_0 like Mac OS X) AppleWebKit/605.1.15 Version/18.0 Mobile/15E148 Safari/604.1',
      platform: 'MacIntel',
      maxTouchPoints: 5,
    });
    vi.stubGlobal('sessionStorage', createStorage());
    vi.stubGlobal('crypto', { randomUUID: () => 'unused' });

    const { getBrowserDeviceLabel } = await import('./cookies');

    expect(getBrowserDeviceLabel()).toBe('iPad · Safari');
  });
});

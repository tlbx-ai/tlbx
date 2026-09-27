import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

interface IdentityMessage {
  type: 'probe' | 'occupied';
  tabId: string;
  runtimeId: string;
  targetRuntimeId?: string;
}

class FakeBroadcastChannel {
  static channels = new Map<string, Set<FakeBroadcastChannel>>();
  static deliveryDelayMs = 0;

  onmessage: ((event: MessageEvent<IdentityMessage>) => void) | null = null;

  constructor(private readonly name: string) {
    const peers = FakeBroadcastChannel.channels.get(name) ?? new Set<FakeBroadcastChannel>();
    peers.add(this);
    FakeBroadcastChannel.channels.set(name, peers);
  }

  postMessage(message: IdentityMessage): void {
    FakeBroadcastChannel.channels.get(this.name)?.forEach((peer) => {
      if (peer === this) return;
      const deliver = () => peer.onmessage?.({ data: message } as MessageEvent<IdentityMessage>);
      if (FakeBroadcastChannel.deliveryDelayMs > 0) {
        globalThis.setTimeout(deliver, FakeBroadcastChannel.deliveryDelayMs);
      } else {
        deliver();
      }
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
    FakeBroadcastChannel.deliveryDelayMs = 0;
    vi.stubGlobal('BroadcastChannel', FakeBroadcastChannel);
    vi.stubGlobal('navigator', { userAgent: '', maxTouchPoints: 0 });
  });

  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
    FakeBroadcastChannel.channels.clear();
    FakeBroadcastChannel.deliveryDelayMs = 0;
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

  it('rekeys and requests recovery when an occupied response arrives after the probe window', async () => {
    const randomUUID = vi
      .fn()
      .mockReturnValueOnce('runtime-existing')
      .mockReturnValueOnce('runtime-duplicate')
      .mockReturnValueOnce('tab-late-rekey');
    vi.stubGlobal('crypto', { randomUUID });

    vi.stubGlobal('sessionStorage', createStorage('tab-copied'));
    const existingModule = await import('./cookies');
    const existingIdentity = existingModule.initializeTabIdentity();
    await vi.advanceTimersByTimeAsync(60);
    await expect(existingIdentity).resolves.toBe('tab-copied');

    vi.resetModules();
    FakeBroadcastChannel.deliveryDelayMs = 100;
    const duplicateStorage = createStorage('tab-copied');
    const dispatchEvent = vi.fn(() => true);
    vi.stubGlobal('sessionStorage', duplicateStorage);
    vi.stubGlobal('dispatchEvent', dispatchEvent);
    const duplicateModule = await import('./cookies');
    const duplicateIdentity = duplicateModule.initializeTabIdentity();
    await vi.advanceTimersByTimeAsync(60);
    await expect(duplicateIdentity).resolves.toBe('tab-copied');

    await vi.advanceTimersByTimeAsync(200);

    expect(duplicateStorage.getItem('mt-tab-id')).toBe('tab-late-rekey');
    expect(dispatchEvent).toHaveBeenCalledWith(
      expect.objectContaining({ type: duplicateModule.TAB_ID_COLLISION_EVENT }),
    );
  });
});

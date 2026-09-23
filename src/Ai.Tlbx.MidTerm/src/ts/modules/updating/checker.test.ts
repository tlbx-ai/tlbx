import { beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
  applyUpdate: vi.fn(),
  checkUpdate: vi.fn(),
  showConfirm: vi.fn(),
  showAlert: vi.fn(),
  beginServerRestartLifecycle: vi.fn(),
}));

vi.mock('../../api/client', () => ({ ...mocks }));
vi.mock('../../utils/dialog', () => ({ ...mocks }));
vi.mock('../../utils', () => ({ escapeHtml: (value: string) => value }));
vi.mock('../logging', () => ({ createLogger: () => ({ error: vi.fn() }) }));
vi.mock('../i18n', () => ({ t: (key: string) => key }));
vi.mock('../settings', () => ({}));
vi.mock('../navigation/backButtonGuard', () => ({}));
vi.mock('./runtime', () => ({ ...mocks }));

import { $updateInfo } from '../../stores';
import { applyFullUpdate, applyUpdate, applyLocalUpdate } from './checker';

describe('full update action', () => {
  const button = { id: 'btn-full-update', disabled: false, textContent: '' };
  const cardButton = { id: '', disabled: false, textContent: '' };

  beforeEach(() => {
    vi.clearAllMocks();
    button.disabled = false;
    $updateInfo.set(null);
    vi.stubGlobal('document', {
      querySelectorAll: () => [button, cardButton],
      getElementById: () => null,
    });
    vi.stubGlobal('localStorage', { setItem: vi.fn() });
    mocks.showConfirm.mockResolvedValue(true);
    mocks.applyUpdate.mockResolvedValue({ response: { ok: true } });
    mocks.checkUpdate.mockResolvedValue({ data: null });
  });

  it('can reinstall when no newer update is available', async () => {
    await applyFullUpdate();
    expect(mocks.showConfirm).toHaveBeenCalledWith('update.fullUpdateConfirm', expect.any(Object));
    expect(mocks.applyUpdate).toHaveBeenCalledWith(undefined, true);
    expect(mocks.beginServerRestartLifecycle).toHaveBeenCalledWith('update', {
      updateType: 'full',
      expectedServerVersion: null,
    });
  });

  it('does not start an update when confirmation is cancelled', async () => {
    mocks.showConfirm.mockResolvedValue(false);
    await applyFullUpdate();
    expect(mocks.applyUpdate).not.toHaveBeenCalled();
    expect(button.disabled).toBe(false);
  });

  it('shows the server failure and allows retry without announcing a restart', async () => {
    mocks.applyUpdate.mockResolvedValue({
      response: { ok: false, status: 500 },
      error: { detail: 'Download failed' },
    });
    await applyFullUpdate();
    expect(mocks.showAlert).toHaveBeenCalledWith('update.applyFailed', {
      details: 'Download failed',
    });
    expect(mocks.beginServerRestartLifecycle).not.toHaveBeenCalled();
    expect(button.disabled).toBe(false);
  });
  it('applies even when browser discovery is missing', async () => {
    await applyUpdate();
    expect(mocks.applyUpdate).toHaveBeenCalledWith(undefined, false);
    expect(mocks.beginServerRestartLifecycle).toHaveBeenCalled();
  });
  it('restores every button after a rejected request and permits retry', async () => {
    mocks.applyUpdate.mockRejectedValueOnce(new Error('Network unavailable'));
    await applyUpdate();
    expect(mocks.showAlert).toHaveBeenCalledWith('update.applyFailed', {
      details: 'Network unavailable',
    });
    expect(cardButton.disabled).toBe(false);
    await applyUpdate();
    expect(mocks.applyUpdate).toHaveBeenCalledTimes(2);
  });
  it('prevents concurrent requests across different update buttons', async () => {
    let resolve!: (value: unknown) => void;
    mocks.applyUpdate.mockReturnValueOnce(
      new Promise((done) => {
        resolve = done;
      }),
    );
    const pending = applyUpdate();
    expect(cardButton.disabled).toBe(true);
    await applyLocalUpdate();
    await applyFullUpdate();
    expect(mocks.applyUpdate).toHaveBeenCalledTimes(1);
    resolve({ response: { ok: true } });
    await pending;
  });
  it('continues restart recovery when browser storage is blocked', async () => {
    vi.stubGlobal('localStorage', {
      setItem: () => {
        throw new Error('Storage denied');
      },
    });
    await applyUpdate();
    expect(mocks.beginServerRestartLifecycle).toHaveBeenCalled();
    expect(mocks.showAlert).not.toHaveBeenCalled();
  });
  it('shows a local update server error and restores the buttons', async () => {
    mocks.applyUpdate.mockResolvedValue({
      response: { ok: false },
      error: 'No local update available',
    });
    await applyLocalUpdate();
    expect(mocks.showAlert).toHaveBeenCalledWith('update.applyFailed', {
      details: 'No local update available',
    });
    expect(cardButton.disabled).toBe(false);
  });

  it('refreshes a stale tab when the update finished before its click was handled', async () => {
    $updateInfo.set({
      available: true,
      currentVersion: '10.16.21-dev',
      latestVersion: '10.16.22-dev',
      type: 'webOnly',
    } as never);
    mocks.applyUpdate.mockResolvedValue({
      response: { ok: false, status: 400 },
      error: 'No update available',
    });
    mocks.checkUpdate.mockResolvedValue({
      data: {
        available: false,
        currentVersion: '10.16.22-dev',
        latestVersion: '10.16.22-dev',
      },
    });

    await applyUpdate();

    expect(mocks.showAlert).not.toHaveBeenCalled();
    expect(mocks.beginServerRestartLifecycle).toHaveBeenCalledWith('update', {
      updateType: 'webOnly',
      expectedServerVersion: '10.16.22-dev',
    });
    expect($updateInfo.get()?.available).toBe(false);
  });

  it('still reports an unavailable update when the server remains on the old version', async () => {
    $updateInfo.set({
      available: true,
      currentVersion: '10.16.21-dev',
      latestVersion: '10.16.22-dev',
      type: 'webOnly',
    } as never);
    mocks.applyUpdate.mockResolvedValue({
      response: { ok: false, status: 400 },
      error: 'No update available',
    });
    mocks.checkUpdate.mockResolvedValue({
      data: {
        available: false,
        currentVersion: '10.16.21-dev',
        latestVersion: '10.16.21-dev',
      },
    });

    await applyUpdate();

    expect(mocks.showAlert).toHaveBeenCalledWith('update.applyFailed', {
      details: 'update.noLongerAvailable',
    });
    expect(mocks.beginServerRestartLifecycle).not.toHaveBeenCalled();
  });
});

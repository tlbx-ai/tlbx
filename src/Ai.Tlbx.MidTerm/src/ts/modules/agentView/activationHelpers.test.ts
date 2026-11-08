import { describe, expect, it, vi } from 'vitest';
import { resumeAppServerControlActivationAfterTransport } from './activationHelpers';

function deferred() {
  let resolve!: () => void;
  const promise = new Promise<void>((done) => {
    resolve = done;
  });
  return { promise, resolve };
}

describe('ACP lifecycle attach recovery barrier', () => {
  it('waits for the interrupted activation before starting one fresh attach/read', async () => {
    const old = deferred();
    const activate = vi.fn().mockResolvedValue(undefined);
    const recover = vi.fn().mockResolvedValue(undefined);
    const pending = resumeAppServerControlActivationAfterTransport(
      recover,
      old.promise,
      () => true,
      activate,
    );
    await Promise.resolve();
    expect(recover).toHaveBeenCalledOnce();
    expect(activate).not.toHaveBeenCalled();
    old.resolve();
    await pending;
    expect(activate).toHaveBeenCalledOnce();
  });

  it('drops pending attach/read when the selected-view or lifecycle guard is invalidated', async () => {
    const old = deferred();
    let current = true;
    const activate = vi.fn();
    const pending = resumeAppServerControlActivationAfterTransport(
      async () => {},
      old.promise,
      () => current,
      activate,
    );
    await Promise.resolve();
    current = false;
    old.resolve();
    await pending;
    expect(activate).not.toHaveBeenCalled();
  });

  it('does not activate if the captured view became stale while connecting', async () => {
    const connection = deferred();
    const activate = vi.fn();
    const pending = resumeAppServerControlActivationAfterTransport(
      () => connection.promise,
      undefined,
      () => false,
      activate,
    );
    connection.resolve();
    await pending;
    expect(activate).not.toHaveBeenCalled();
  });

  it('reports transport failure without scheduling retries or invoking attach', async () => {
    const activate = vi.fn();
    await expect(
      resumeAppServerControlActivationAfterTransport(
        async () => {
          throw new Error('offline');
        },
        undefined,
        () => true,
        activate,
      ),
    ).rejects.toThrow('offline');
    expect(activate).not.toHaveBeenCalled();
  });
});

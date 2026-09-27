import { afterEach, describe, expect, it, vi } from 'vitest';

describe('api client appServerControl helpers', () => {
  afterEach(() => {
    vi.unstubAllGlobals();
    vi.resetModules();
  });

  it('surfaces structured session launch problem details', async () => {
    vi.stubGlobal(
      'fetch',
      vi.fn(async () => ({
        ok: false,
        status: 500,
        statusText: 'Internal Server Error',
        async text() {
          return JSON.stringify({
            title: 'Session launch failed',
            detail: 'Windows blocked the mthost process launch.',
            errorDetails: 'CreateProcess failed with Win32 error 5: Access is denied.',
            errorStage: 'spawn',
            exceptionType: 'Win32Exception',
            nativeErrorCode: 5,
          });
        },
      })),
    );

    const { createSession, ApiProblemError } = await import('./client');

    let thrown: unknown;
    try {
      await createSession({ cols: 120, rows: 30, shell: 'Pwsh', workingDirectory: null });
    } catch (error) {
      thrown = error;
    }

    expect(thrown).toBeInstanceOf(ApiProblemError);
    expect((thrown as ApiProblemError).detail).toBe('Windows blocked the mthost process launch.');
    expect((thrown as ApiProblemError).errorDetails).toBe(
      'CreateProcess failed with Win32 error 5: Access is denied.',
    );
    expect((thrown as ApiProblemError).nativeErrorCode).toBe(5);
  });

  it('waits out a transient API disconnect before a non-idempotent launch', async () => {
    const fetchMock = vi
      .fn()
      .mockRejectedValueOnce(new TypeError('Failed to fetch'))
      .mockResolvedValueOnce({ ok: true, status: 200 });
    vi.stubGlobal('fetch', fetchMock);

    const { waitForApiReachability } = await import('./client');
    await waitForApiReachability();

    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(fetchMock).toHaveBeenLastCalledWith(
      '/api/version',
      expect.objectContaining({ cache: 'no-store', signal: expect.any(AbortSignal) }),
    );
  });

  it('retries a disconnected session launch with the same idempotency key', async () => {
    vi.stubGlobal('crypto', { randomUUID: () => 'launch-retry' });
    const requestBodies: string[] = [];
    const fetchMock = vi.fn(async (_input: RequestInfo | URL, init?: RequestInit) => {
      requestBodies.push(String(init?.body));
      if (requestBodies.length === 1) {
        throw new TypeError('connection reset');
      }
      return {
        ok: true,
        async text() {
          return JSON.stringify({ id: 'session-reconciled' });
        },
      };
    });
    vi.stubGlobal('fetch', fetchMock);

    const { createSession } = await import('./client');
    const result = await createSession({ cols: 120, rows: 30, shell: 'Pwsh' });

    expect(result.data?.id).toBe('session-reconciled');
    expect(fetchMock).toHaveBeenCalledTimes(2);
    expect(requestBodies[0]).toBe(requestBodies[1]);
    expect(requestBodies[0]).toContain('"launchRequestId":"launch-retry"');
  });
});

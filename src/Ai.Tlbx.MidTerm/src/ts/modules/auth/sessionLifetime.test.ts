import { afterEach, describe, expect, it } from 'vitest';

import {
  handleAuthenticatedWebSocketClose,
  isAuthenticationRequiredResponse,
} from './sessionLifetime';

const originalWindow = globalThis.window;

afterEach(() => {
  Object.defineProperty(globalThis, 'window', {
    value: originalWindow,
    configurable: true,
    writable: true,
  });
});

describe('auth session lifetime', () => {
  it('recognizes only MidTerm authentication 401 responses', () => {
    expect(
      isAuthenticationRequiredResponse(
        new Response(null, {
          status: 401,
          headers: { 'X-MidTerm-Auth-Required': 'true' },
        }),
      ),
    ).toBe(true);
    expect(isAuthenticationRequiredResponse(new Response(null, { status: 401 }))).toBe(false);
    expect(
      isAuthenticationRequiredResponse(
        new Response(null, {
          status: 403,
          headers: { 'X-MidTerm-Auth-Required': 'true' },
        }),
      ),
    ).toBe(false);
  });

  it('stops reconnect for auth close code 4401 only', () => {
    Object.defineProperty(globalThis, 'window', {
      value: { setTimeout: () => 0 },
      configurable: true,
      writable: true,
    });

    expect(handleAuthenticatedWebSocketClose({ code: 4401 } as CloseEvent)).toBe(true);
    expect(handleAuthenticatedWebSocketClose({ code: 1006 } as CloseEvent)).toBe(false);
  });
});

import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const { changePassword } = vi.hoisted(() => ({ changePassword: vi.fn() }));
vi.mock('../../api/client', () => ({ changePassword }));
vi.mock('../../stores', () => ({ $authStatus: { get: () => ({ passwordSet: false }) } }));
vi.mock('../../utils', () => ({ bindClick: vi.fn() }));
vi.mock('./status', () => ({
  checkAuthStatus: vi.fn(),
  dismissSecurityWarning: vi.fn(),
  logout: vi.fn(),
}));
vi.mock('../i18n', () => ({ t: (key: string) => key }));
vi.mock('../navigation/backButtonGuard', () => ({ registerBackButtonLayer: vi.fn() }));

import { handlePasswordSubmit } from './password';

beforeEach(() => {
  changePassword.mockReset();
  changePassword.mockResolvedValue({
    response: new Response(null, { status: 200 }),
    data: { success: true },
  });
});
afterEach(() => vi.unstubAllGlobals());

function submit(password: string, confirmation = password) {
  const error = { textContent: '', classList: { add: vi.fn(), remove: vi.fn() } };
  const fields: Record<string, unknown> = {
    'new-password': { value: password },
    'confirm-password': { value: confirmation },
    'password-error': error,
  };
  vi.stubGlobal('document', { getElementById: (id: string) => fields[id] ?? null });
  return { error, pending: handlePasswordSubmit({ preventDefault: vi.fn() } as unknown as Event) };
}

describe('password-change request policy', () => {
  it.each([
    [9, false],
    [10, true],
    [1024, true],
    [1025, false],
  ])('accepts length %i: %s', async (length, accepted) => {
    const password = 'a'.repeat(length as number);
    const result = submit(password);
    await result.pending;
    if (accepted) {
      expect(changePassword).toHaveBeenCalledExactlyOnceWith(null, password);
      expect(result.error.textContent).toBe('');
    } else {
      expect(changePassword).not.toHaveBeenCalled();
      expect(result.error.textContent).toBe('error.passwordLength');
    }
  });
  it('rejects a mismatched confirmation before sending credentials', async () => {
    const result = submit('ten-letter-passphrase', 'different-passphrase');
    await result.pending;
    expect(changePassword).not.toHaveBeenCalled();
    expect(result.error.textContent).toBe('error.passwordsNoMatch');
  });
});

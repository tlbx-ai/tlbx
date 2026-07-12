import { WS_CLOSE_AUTH_FAILED } from '../../constants';

const AUTH_REQUIRED_HEADER = 'X-MidTerm-Auth-Required';
const SESSION_REFRESH_INTERVAL_MS = 12 * 60 * 60 * 1000;

let fetchGuardInstalled = false;
let redirectPending = false;
let authenticationRecoveryPending = false;
let refreshInFlight: Promise<boolean> | null = null;
let lastRefreshAt = Date.now();

export function initAuthSessionLifetime(): void {
  installAuthFetchGuard();

  window.setInterval(() => {
    void refreshSessionIfDue();
  }, SESSION_REFRESH_INTERVAL_MS);

  document.addEventListener('visibilitychange', () => {
    if (document.visibilityState === 'visible') {
      void refreshSessionIfDue();
    }
  });
  window.addEventListener('focus', () => {
    void refreshSessionIfDue();
  });
}

export function handleAuthenticatedWebSocketClose(event: CloseEvent): boolean {
  if (event.code !== WS_CLOSE_AUTH_FAILED) {
    return false;
  }

  scheduleAuthenticationRecovery();
  return true;
}

export function isAuthRedirectPending(): boolean {
  return redirectPending;
}

export function isAuthenticationRequiredResponse(response: Response): boolean {
  return response.status === 401 && response.headers.get(AUTH_REQUIRED_HEADER) === 'true';
}

function installAuthFetchGuard(): void {
  if (fetchGuardInstalled) return;
  fetchGuardInstalled = true;

  const originalFetch = window.fetch.bind(window);
  window.fetch = async (...args: Parameters<typeof fetch>): Promise<Response> => {
    const response = await originalFetch(...args);
    if (isAuthenticationRequiredResponse(response)) {
      redirectToLogin();
    }
    return response;
  };
}

async function refreshSessionIfDue(): Promise<void> {
  if (
    redirectPending ||
    document.visibilityState !== 'visible' ||
    Date.now() - lastRefreshAt < SESSION_REFRESH_INTERVAL_MS
  ) {
    return;
  }

  await refreshSession();
}

async function refreshSession(): Promise<boolean> {
  if (refreshInFlight) return refreshInFlight;
  refreshInFlight = (async () => {
    try {
      const response = await fetch('/api/auth/refresh', {
        method: 'POST',
        cache: 'no-store',
        credentials: 'same-origin',
      });
      if (response.ok) {
        lastRefreshAt = Date.now();
        return true;
      }
    } catch {
      // Network loss is not authentication loss. Retry on the next focus/interval.
    }
    return false;
  })();

  try {
    return await refreshInFlight;
  } finally {
    refreshInFlight = null;
  }
}

async function recoverFromAuthenticationClose(): Promise<void> {
  // Logout/password-change responses may still be updating the cookie when the
  // server closes sockets. Give that response a moment to settle before probing.
  await new Promise((resolve) => window.setTimeout(resolve, 250));
  const refreshed = await refreshSession();
  if (refreshed && !redirectPending) {
    window.location.reload();
  } else if (!redirectPending) {
    window.addEventListener(
      'online',
      () => {
        authenticationRecoveryPending = false;
        scheduleAuthenticationRecovery();
      },
      { once: true },
    );
  }
}

function scheduleAuthenticationRecovery(): void {
  if (authenticationRecoveryPending) return;
  authenticationRecoveryPending = true;
  void recoverFromAuthenticationClose();
}

function redirectToLogin(): void {
  if (redirectPending || isLoginPage()) return;
  redirectPending = true;

  try {
    window.sessionStorage.setItem(
      'midterm-auth-return-url',
      `${window.location.pathname}${window.location.search}${window.location.hash}`,
    );
  } catch {
    // Session storage can be unavailable in hardened/private browser contexts.
  }

  window.location.assign('/login.html');
}

function isLoginPage(): boolean {
  return window.location.pathname === '/login' || window.location.pathname === '/login.html';
}

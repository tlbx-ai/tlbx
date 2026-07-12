/**
 * Login Page Module
 *
 * Handles login form submission and certificate TOFU display.
 */

import { login, getBootstrapLogin } from '../api/client';
import { t, initI18n } from './i18n';

const CERT_HIDDEN_KEY = 'mt-cert-info-hidden';
const AUTH_RETURN_URL_KEY = 'midterm-auth-return-url';

export async function initLoginPage(): Promise<void> {
  const form = document.getElementById('login-form') as HTMLFormElement | null;
  const passwordInput = document.getElementById('password') as HTMLInputElement | null;
  const errorDiv = document.getElementById('error-message');
  const loginBtn = document.getElementById('login-btn') as HTMLButtonElement | null;

  if (!form || !passwordInput || !errorDiv || !loginBtn) return;

  // Initialize i18n for login page (await so translations are applied before binding events)
  await initI18n();

  // Load version and insider info
  void loadVersionAndPaths();

  form.addEventListener('submit', (e) => {
    void handleLoginSubmit(e, passwordInput, errorDiv, loginBtn);
  });

  // Certificate TOFU display
  const certInfoDiv = document.getElementById('cert-info');
  const certHideBtn = document.getElementById('cert-hide-btn');

  if (certInfoDiv && certHideBtn) {
    if (localStorage.getItem(CERT_HIDDEN_KEY) !== 'true') {
      void loadCertificateInfo(certInfoDiv);
    }

    certHideBtn.addEventListener('click', () => {
      localStorage.setItem(CERT_HIDDEN_KEY, 'true');
      certInfoDiv.classList.add('hidden');
    });
  }
}

async function handleLoginSubmit(
  e: Event,
  passwordInput: HTMLInputElement,
  errorDiv: HTMLElement,
  loginBtn: HTMLButtonElement,
): Promise<void> {
  e.preventDefault();

  const password = passwordInput.value;
  if (!password) {
    showError(errorDiv, t('auth.passwordRequired'));
    return;
  }

  loginBtn.disabled = true;
  loginBtn.textContent = t('auth.loggingIn');
  errorDiv.classList.add('hidden');

  try {
    const { data, response } = await login(password);

    if (response.ok && data?.success) {
      window.location.href = consumeAuthReturnUrl();
    } else {
      showError(errorDiv, data?.error ?? t('auth.loginFailed'));
      passwordInput.value = '';
      passwordInput.focus();
    }
  } catch {
    showError(errorDiv, t('auth.connectionError'));
  } finally {
    loginBtn.disabled = false;
    loginBtn.textContent = t('auth.login');
  }
}

function consumeAuthReturnUrl(): string {
  try {
    const value = window.sessionStorage.getItem(AUTH_RETURN_URL_KEY);
    window.sessionStorage.removeItem(AUTH_RETURN_URL_KEY);
    if (value?.startsWith('/') && !value.startsWith('//') && !value.startsWith('/login')) {
      return value;
    }
  } catch {
    // Session storage can be unavailable in hardened/private browser contexts.
  }

  return '/';
}

function showError(errorDiv: HTMLElement, msg: string): void {
  errorDiv.textContent = msg;
  errorDiv.classList.remove('hidden');
}

async function loadCertificateInfo(certInfoDiv: HTMLElement): Promise<void> {
  try {
    const { data } = await getBootstrapLogin();
    if (!data?.certificate?.fingerprint) return;

    // Format fingerprint with colons every 2 chars
    const fp = data.certificate.fingerprint.match(/.{1,2}/g)?.join(':') ?? '';
    const fpEl = document.getElementById('cert-fingerprint');
    if (fpEl) fpEl.textContent = fp;

    // Format dates
    const formatDate = (iso: string | null): string => {
      if (!iso) return '';
      const d = new Date(iso);
      return d.toLocaleDateString(undefined, { year: 'numeric', month: 'short', day: 'numeric' });
    };

    const validFromEl = document.getElementById('cert-valid-from');
    const validUntilEl = document.getElementById('cert-valid-until');
    if (validFromEl)
      validFromEl.textContent = t('auth.certFrom') + ': ' + formatDate(data.certificate.notBefore);
    if (validUntilEl)
      validUntilEl.textContent = t('auth.certUntil') + ': ' + formatDate(data.certificate.notAfter);

    certInfoDiv.classList.remove('hidden');
  } catch {
    // Silently fail - this is optional info
  }
}

async function loadVersionAndPaths(): Promise<void> {
  const versionEl = document.getElementById('login-version');
  const insiderEl = document.getElementById('login-insider');

  // Fetch version (public endpoint)
  try {
    const versionRes = await fetch('/api/version');
    if (versionRes.ok) {
      const version = await versionRes.text();
      if (versionEl) versionEl.textContent = `v${version}`;
    }
  } catch {
    // Silently fail
  }

  // Fetch paths for insider info (may require auth, that's ok)
  try {
    const pathsRes = await fetch('/api/paths');
    if (pathsRes.ok && insiderEl) {
      const paths = (await pathsRes.json()) as { settingsFile?: string; logDirectory?: string };
      const lines = [
        `settings: ${paths.settingsFile || 'n/a'}`,
        `logs: ${paths.logDirectory || 'n/a'}`,
      ];
      insiderEl.textContent = lines.join('\n');
    }
  } catch {
    // Silently fail - insider info is optional
  }
}

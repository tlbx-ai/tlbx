/**
 * Trust Page Module
 *
 * Handles platform detection, certificate download, and UI interactions.
 */

import { t, initI18n } from './i18n';

export async function initTrustPage(): Promise<void> {
  // Platform detection
  const ua = navigator.userAgent.toLowerCase();
  const isIOS =
    /iphone|ipad|ipod/.test(ua) ||
    // eslint-disable-next-line @typescript-eslint/no-deprecated
    (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1);
  const isAndroid = /android/.test(ua);
  const isMac = /macintosh|mac os x/.test(ua) && !isIOS;
  const isLinux = /linux/.test(ua) && !isAndroid;

  const detectedPlatformEl = document.getElementById('detected-platform');

  if (!detectedPlatformEl) return;

  // Initialize i18n for trust page (await so translations apply before building DOM content)
  await initI18n();

  // Show detected platform text and badge
  if (isIOS) {
    detectedPlatformEl.textContent = 'iOS / iPadOS';
    document.getElementById('ios-detected')?.classList.remove('hidden');
  } else if (isAndroid) {
    detectedPlatformEl.textContent = 'Android';
    document.getElementById('android-detected')?.classList.remove('hidden');
  } else {
    document.getElementById('desktop-detected')?.classList.remove('hidden');
    if (isMac) {
      detectedPlatformEl.textContent = 'macOS';
      showDesktopTab('macos');
    } else if (isLinux) {
      detectedPlatformEl.textContent = 'Linux';
      showDesktopTab('linux');
    } else {
      detectedPlatformEl.textContent = 'Windows';
      showDesktopTab('windows');
    }
  }

  // Desktop OS tabs
  document.querySelectorAll('.os-tab').forEach((tab) => {
    tab.addEventListener('click', () => {
      const os = (tab as HTMLElement).dataset.os;
      if (os) showDesktopTab(os);
    });
  });

  // Download buttons
  bindDownload('btn-install-ios', '/api/certificate/download/mobileconfig');
  bindDownload('btn-download-pem-desktop', '/api/certificate/download/pem');
  bindDownload('btn-download-pem-macos', '/api/certificate/download/pem');
  bindDownload('btn-download-pem-linux', '/api/certificate/download/pem');

  // Copy fingerprint
  const copyBtn = document.getElementById('copy-fingerprint');
  if (copyBtn) {
    copyBtn.addEventListener('click', () => {
      const fpEl = document.getElementById('fingerprint');
      if (!fpEl) return;
      const fp = fpEl.textContent;
      void navigator.clipboard
        .writeText(fp)
        .then(() => {
          copyBtn.textContent = t('trust.copied');
          setTimeout(() => (copyBtn.textContent = t('trust.copy')), 2000);
        })
        .catch(() => {
          const textarea = document.createElement('textarea');
          textarea.value = fp;
          document.body.appendChild(textarea);
          textarea.select();
          // eslint-disable-next-line @typescript-eslint/no-deprecated
          document.execCommand('copy');
          document.body.removeChild(textarea);
        });
    });
  }

  // Load certificate info
  void loadCertificateInfo();
}

function showDesktopTab(os: string): void {
  document.querySelectorAll('.os-tab').forEach((t) => {
    t.classList.remove('active');
  });
  document.querySelector(`.os-tab[data-os="${os}"]`)?.classList.add('active');

  document.querySelectorAll('.os-instructions').forEach((el) => {
    el.classList.add('hidden');
  });
  document.getElementById(`${os}-instructions`)?.classList.remove('hidden');
}

function bindDownload(id: string, url: string): void {
  const btn = document.getElementById(id);
  if (btn) {
    btn.addEventListener('click', () => {
      window.location.href = url;
    });
  }
}

async function loadCertificateInfo(): Promise<void> {
  try {
    const { getCertificateInfo } = await import('../api/client');
    const { data, response } = await getCertificateInfo();
    if (!response.ok || !data?.fingerprint) throw new Error('Certificate unavailable');

    const info = data;

    // Display fingerprint
    const fpEl = document.getElementById('fingerprint');
    if (fpEl) fpEl.textContent = data.fingerprint.match(/.{1,2}/g)?.join(':') ?? '';

    // Display validity
    const validUntil = info.notAfter ? new Date(info.notAfter) : null;
    const validEl = document.getElementById('cert-valid-until');
    if (validEl && validUntil) {
      validEl.textContent =
        'Certificate valid until: ' +
        validUntil.toLocaleDateString(undefined, {
          year: 'numeric',
          month: 'long',
          day: 'numeric',
        });
    }
  } catch {
    const fpEl = document.getElementById('fingerprint');
    if (fpEl) fpEl.textContent = t('trust.errorLoading');
  }
}

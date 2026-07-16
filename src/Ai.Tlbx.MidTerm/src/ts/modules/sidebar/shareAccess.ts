/**
 * Share Access Module
 *
 * Handles the "Share Access" button that opens email client
 * with connection info for sharing terminal access with others.
 */

import { createLogger } from '../logging';
import { t } from '../i18n';
import { getSharePacket, type SharePacketInfo } from '../../api/client';
import { showAlert } from '../../utils/dialog';

const log = createLogger('shareAccess');

export function initShareAccessButton(): void {
  const el = document.getElementById('btn-share-access');
  log.info(() => `initShareAccessButton: element found = ${!!el}`);
  if (el) {
    el.addEventListener('click', () => {
      log.info(() => 'Share Access button clicked');
      void openShareEmail();
    });
  }
}

async function openShareEmail(): Promise<void> {
  try {
    const { data, response } = await getSharePacket();
    if (!response.ok || !data) {
      log.error(() => 'Failed to fetch share packet');
      showFallbackMessage(t('share.failedToLoad'));
      return;
    }

    const info = data;
    const subject = `tlbx access — ${location.hostname}`;
    const body = generateEmailBody(info);
    const mailtoUrl = `mailto:?subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`;

    log.info(() => `Opening mailto link (${mailtoUrl.length} chars)`);

    // Try to open email client
    tryOpenMailto(mailtoUrl);

    // Give the email client a moment to open, then show fallback if still here
    setTimeout(() => {
      if (document.hasFocus()) {
        log.info(() => 'Page still has focus - email client may not have opened');
        showCopyFallback(subject, body, info.trustPageUrl || location.href);
      }
    }, 1000);
  } catch (e) {
    log.error(() => `Failed to open share email: ${String(e)}`);
    showFallbackMessage(t('share.failedToGenerate'));
  }
}

function tryOpenMailto(url: string): boolean {
  // Try window.open first (works better in some browsers)
  const win = window.open(url, '_self');
  return win !== null;
}

function showFallbackMessage(message: string): void {
  void showAlert(message);
}

function showCopyFallback(subject: string, body: string, trustPageUrl: string): void {
  const copyText = `${subject}\n\n${body}`;

  // Try to copy to clipboard
  void navigator.clipboard
    .writeText(copyText)
    .then(() => {
      void showAlert(
        t('share.noEmailClient') +
          '\n\n' +
          t('share.copiedToClipboard') +
          '\n\n' +
          t('share.visitTrustPage') +
          ':\n' +
          trustPageUrl,
      );
    })
    .catch(() => {
      // Clipboard failed, show the trust page URL at least
      void showAlert(
        t('share.noEmailClient') + '\n\n' + t('share.visitTrustPage') + ':\n' + trustPageUrl,
      );
    });
}

function generateEmailBody(info: SharePacketInfo): string {
  const endpointsList = info.endpoints.map((ep) => `• ${ep.name}: ${ep.url}`).join('\n');

  const validUntil = new Date(info.certificate.notAfter).toLocaleDateString(undefined, {
    year: 'numeric',
    month: 'long',
    day: 'numeric',
  });

  const thumbprint = info.certificate.fingerprint;

  return `tlbx access
===========

SECURITY: VERIFY FINGERPRINT FIRST
----------------------------------
SHA-256: ${thumbprint}

Compare this with your browser's certificate fingerprint before entering any passwords.
Click the padlock icon in your browser's address bar > Certificate > SHA-256 fingerprint.

CONNECTION ENDPOINTS
--------------------
${endpointsList}

INSTALL CERTIFICATE
-------------------
Visit: ${info.trustPageUrl}

This page will detect your device and guide you through installation.

Certificate valid until: ${validUntil}

TIP: Send this email to yourself, your work email, and family members
who may need terminal access from their phones or tablets.

---
tlbx — browser control station
`;
}

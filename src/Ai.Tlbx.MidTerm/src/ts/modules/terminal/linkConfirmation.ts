import { t } from '../i18n';
import { registerBackButtonLayer } from '../navigation/backButtonGuard';
import { escapeHtml } from '../../utils/dom';
import { openTerminalWebLinkInNewTab } from './webLinks';

const STORAGE_KEY = 'tlbx-confirm-terminal-links';
let closeActiveDialog: (() => void) | null = null;

export function shouldConfirmTerminalLinks(): boolean {
  try {
    return localStorage.getItem(STORAGE_KEY) !== 'false';
  } catch {
    return true;
  }
}

export function setConfirmTerminalLinks(enabled: boolean): void {
  try {
    localStorage.setItem(STORAGE_KEY, String(enabled));
  } catch {
    // Storage may be unavailable in a restricted browser context.
  }
}

export function bindTerminalLinkSecuritySetting(): void {
  const input = document.getElementById('terminal-link-confirmation') as HTMLInputElement | null;
  if (!input) return;
  input.checked = shouldConfirmTerminalLinks();
  input.onchange = () => {
    setConfirmTerminalLinks(input.checked);
  };
}

/** Shared activation path for plain URLs and embedded OSC 8 hyperlinks. */
export function activateTerminalLink(event: MouseEvent, uri: string): void {
  let url: URL;
  try {
    url = new URL(uri);
  } catch {
    return;
  }
  if (url.protocol !== 'http:' && url.protocol !== 'https:') return;

  if (!shouldConfirmTerminalLinks()) {
    openTerminalWebLinkInNewTab(event, url.href);
    return;
  }
  event.preventDefault();
  event.stopPropagation();
  closeActiveDialog?.();
  showTerminalLinkDialog(url.href);
}

function showTerminalLinkDialog(uri: string): void {
  const previousFocus = document.activeElement as HTMLElement | null;
  const overlay = document.createElement('div');
  overlay.className = 'modal-overlay terminal-link-overlay';
  overlay.innerHTML = `
    <div class="modal dialog-modal" role="dialog" aria-modal="true" aria-labelledby="terminal-link-title" aria-describedby="terminal-link-message">
      <div class="modal-content dialog-content terminal-link-content">
        <div class="modal-header"><h3 id="terminal-link-title">${escapeHtml(t('terminalLinks.title'))}</h3></div>
        <div class="modal-body">
          <p id="terminal-link-message" class="dialog-message">${escapeHtml(t('terminalLinks.message'))}</p>
          <p class="terminal-link-destination" dir="ltr">${escapeHtml(uri)}</p>
          <p class="dialog-message">${escapeHtml(t('terminalLinks.rememberHint'))}</p>
        </div>
        <div class="modal-footer">
          <button class="btn-secondary" data-action="cancel">${escapeHtml(t('dialog.cancel'))}</button>
          <button class="btn-secondary" data-action="remember">${escapeHtml(t('terminalLinks.neverAgain'))}</button>
          <a class="btn-primary" data-action="open" href="${escapeHtml(uri)}" target="_blank" rel="noopener noreferrer">${escapeHtml(t('terminalLinks.open'))}</a>
        </div>
      </div>
    </div>`;
  const close = (): void => {
    document.removeEventListener('keydown', onKey, true);
    releaseBack();
    overlay.remove();
    closeActiveDialog = null;
    if (previousFocus?.isConnected) previousFocus.focus();
  };
  const onKey = (event: KeyboardEvent): void => {
    if (event.key === 'Escape') {
      event.preventDefault();
      event.stopPropagation();
      close();
    } else if (event.key === 'Tab') {
      const controls = Array.from(overlay.querySelectorAll<HTMLElement>('button, a'));
      const index = controls.indexOf(document.activeElement as HTMLElement);
      const next = (index + (event.shiftKey ? -1 : 1) + controls.length) % controls.length;
      event.preventDefault();
      controls[next]?.focus();
    }
  };
  overlay.addEventListener('click', (event) => {
    const action = (event.target as HTMLElement).closest<HTMLElement>('[data-action]')?.dataset
      .action;
    if (action === 'remember') {
      setConfirmTerminalLinks(false);
      bindTerminalLinkSecuritySetting();
      // Open synchronously inside the user gesture, avoiding popup blockers.
      openTerminalWebLinkInNewTab(event, uri);
      close();
    } else if (action === 'open') {
      // Keep the real anchor's native new-tab behavior.
      close();
    } else if (action === 'cancel' || event.target === overlay) {
      close();
    }
  });
  document.body.append(overlay);
  document.addEventListener('keydown', onKey, true);
  const releaseBack = registerBackButtonLayer(close);
  closeActiveDialog = close;
  overlay.querySelector<HTMLElement>('[data-action="open"]')?.focus();
}

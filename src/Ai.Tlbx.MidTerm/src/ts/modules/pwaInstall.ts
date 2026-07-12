import { t } from './i18n';
import { showAlert } from '../utils/dialog';

interface BeforeInstallPromptEvent extends Event {
  prompt(): Promise<void>;
  userChoice: Promise<{ outcome: 'accepted' | 'dismissed'; platform: string }>;
}

interface NavigatorWithStandalone extends Navigator {
  standalone?: boolean;
}

export function initPwaInstall(): void {
  let deferredPrompt: BeforeInstallPromptEvent | null = null;
  const row = document.getElementById('pwa-install-row');
  const btn = document.getElementById('btn-install-pwa') as HTMLButtonElement | null;
  if (!row || !btn) return;

  const rowEl = row;
  const btnEl = btn;
  const isIos = isIosInstallableDevice();
  const isAndroid = isAndroidInstallableDevice();

  function showRow(): void {
    rowEl.classList.remove('hidden');
  }

  function hideRow(): void {
    rowEl.classList.add('hidden');
  }

  function setButtonLabel(key: string): void {
    btnEl.dataset.i18n = key;
    btnEl.textContent = t(key);
  }

  if (isRunningAsInstalledPwa()) {
    hideRow();
    return;
  }

  if (isIos || isAndroid) {
    showRow();
    setButtonLabel('settings.behavior.showInstallSteps');
  }

  window.addEventListener('beforeinstallprompt', (event) => {
    event.preventDefault();
    deferredPrompt = event as BeforeInstallPromptEvent;
    setButtonLabel('settings.behavior.install');
    showRow();
  });

  btn.addEventListener('click', () => {
    void handleInstallClick();
  });

  async function handleInstallClick(): Promise<void> {
    const promptEvent = deferredPrompt;
    if (promptEvent) {
      deferredPrompt = null;
      btnEl.disabled = true;
      try {
        await promptEvent.prompt();
        const choice = await promptEvent.userChoice;
        if (choice.outcome === 'accepted') {
          hideRow();
        } else {
          setButtonLabel('settings.behavior.showInstallSteps');
          showRow();
        }
        return;
      } catch {
        setButtonLabel('settings.behavior.showInstallSteps');
        showRow();
      } finally {
        btnEl.disabled = false;
      }
    }

    await showManualInstallSteps();
  }

  async function showManualInstallSteps(): Promise<void> {
    await showAlert(
      t(isIos ? 'settings.behavior.installIosMessage' : 'settings.behavior.installManualMessage'),
      {
        title: t(isIos ? 'settings.behavior.installIosTitle' : 'settings.behavior.installAsApp'),
      },
    );
  }

  window.addEventListener('appinstalled', () => {
    hideRow();
    deferredPrompt = null;
    syncAppModeClasses();
  });
}

export function isIosInstallableDevice(): boolean {
  const ua = navigator.userAgent.toLowerCase();
  return (
    /iphone|ipad|ipod/.test(ua) ||
    // eslint-disable-next-line @typescript-eslint/no-deprecated
    (navigator.platform === 'MacIntel' && navigator.maxTouchPoints > 1)
  );
}

export function isAndroidInstallableDevice(): boolean {
  return /android/i.test(navigator.userAgent);
}

export function isRunningAsInstalledPwa(): boolean {
  const standaloneNavigator = navigator as NavigatorWithStandalone;
  return (
    window.matchMedia('(display-mode: standalone)').matches ||
    window.matchMedia('(display-mode: fullscreen)').matches ||
    window.matchMedia('(display-mode: window-controls-overlay)').matches ||
    standaloneNavigator.standalone === true
  );
}

export function syncAppModeClasses(): void {
  document.body.classList.toggle('installed-pwa', isRunningAsInstalledPwa());
  document.body.classList.toggle('ios-installable-device', isIosInstallableDevice());
}

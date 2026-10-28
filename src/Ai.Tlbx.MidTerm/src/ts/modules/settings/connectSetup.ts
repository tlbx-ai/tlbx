/** Loads the localhost-only Connect controls into the existing Security settings. */
export function isLocalConnectSetup(): boolean {
  return (
    ['localhost', '127.0.0.1', '[::1]'].includes(window.location.hostname) &&
    !window.__tlbxConnectPreview &&
    window.top === window
  );
}

let loading: Promise<void> | undefined;

export function initConnectSetup(): Promise<void> {
  const container = document.getElementById('connect-settings');
  if (!container) return Promise.resolve();
  container.hidden = !isLocalConnectSetup();
  if (container.hidden) return Promise.resolve();
  if (loading) {
    return loading.then(() => {
      window.dispatchEvent(new Event('tlbx-connect-setup-open'));
    });
  }
  loading ??= load(container).catch(() => {
    container.className = 'setting-hint';
    container.textContent = 'Connect setup could not be loaded. Open Settings again to retry.';
    loading = undefined;
  });
  return loading;
}

async function load(container: HTMLElement): Promise<void> {
  const response = await fetch('/connect-setup.html', { cache: 'no-store' });
  if (!response.ok || response.redirected) throw new Error('Local Connect setup is unavailable.');
  container.className = '';
  container.innerHTML = await response.text();
  const script = document.createElement('script');
  script.src = '/connect-setup.js';
  await new Promise<void>((resolve, reject) => {
    script.onload = () => {
      resolve();
    };
    script.onerror = () => {
      container.replaceChildren();
      script.remove();
      reject(new Error('Local Connect controls are unavailable.'));
    };
    document.head.appendChild(script);
  });
}

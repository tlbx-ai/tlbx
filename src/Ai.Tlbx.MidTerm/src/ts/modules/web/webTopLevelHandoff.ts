const TOP_LEVEL_POPUP_FEATURES =
  'popup,width=980,height=900,menubar=no,toolbar=no,location=yes,status=no';

let reloadTimer: number | null = null;

export interface TopLevelHandoffOptions {
  getProxyUrl: () => string | null;
  reload: () => void;
  setMessage: (severity: 'info' | 'error', message: string | null) => void;
}

export interface TopLevelProxyUrlOptions<TClient> {
  frameSrc?: string | null | undefined;
  activeClient: TClient | null | undefined;
  currentUrl: string | null | undefined;
  targetRevision: number;
  origin: string;
  buildProxyUrl: (url: string, client: TClient, targetRevision: number, origin: string) => string;
}

export function resolveTopLevelProxyUrl<TClient>(
  options: TopLevelProxyUrlOptions<TClient>,
): string | null {
  if (options.frameSrc && options.frameSrc !== 'about:blank') return options.frameSrc;
  if (!options.activeClient || !options.currentUrl) return null;
  return options.buildProxyUrl(
    options.currentUrl,
    options.activeClient,
    options.targetRevision,
    options.origin,
  );
}

function scheduleReload(reload: () => void): void {
  if (reloadTimer !== null) {
    window.clearTimeout(reloadTimer);
  }

  reloadTimer = window.setTimeout(() => {
    reloadTimer = null;
    reload();
  }, 250);
}

export function openTopLevelPreview(options: TopLevelHandoffOptions): void {
  const proxyUrl = options.getProxyUrl();
  if (!proxyUrl) {
    options.setMessage('error', 'Open top-level failed: there is no active browser preview.');
    return;
  }

  const popup = window.open(proxyUrl, 'midterm-web-preview-top-level', TOP_LEVEL_POPUP_FEATURES);
  if (!popup) {
    options.setMessage('error', 'Open top-level failed: the browser blocked the popup.');
    return;
  }

  try {
    popup.focus();
  } catch {
    // Ignore focus hand-off failures.
  }

  window.setTimeout(() => {
    try {
      if (!popup.closed) popup.location.reload();
    } catch {
      // Ignore cross-window reload failures.
    }
  }, 1800);

  options.setMessage(
    'info',
    'Top-level preview opened. After accepting consent there, return here to reload the dock.',
  );

  const handleFocus = (): void => {
    window.removeEventListener('focus', handleFocus);
    scheduleReload(options.reload);
  };
  window.addEventListener('focus', handleFocus);

  const poll = window.setInterval(() => {
    if (!popup.closed) {
      return;
    }
    window.clearInterval(poll);
    window.removeEventListener('focus', handleFocus);
    scheduleReload(options.reload);
  }, 600);
}

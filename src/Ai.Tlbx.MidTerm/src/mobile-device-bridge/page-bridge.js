(() => {
  if (globalThis.__midtermMobileDeviceBridgeInstalled) return;
  globalThis.__midtermMobileDeviceBridgeInstalled = true;

  const PAGE_SOURCE = 'midterm-mobile-device-page';
  const EXTENSION_SOURCE = 'midterm-mobile-device-extension';

  window.addEventListener('message', (event) => {
    if (event.source !== window || event.data?.source !== PAGE_SOURCE) return;
    const request = event.data;
    if (request.protocol !== 1 || typeof request.requestId !== 'string') return;

    chrome.runtime
      .sendMessage({
        type: 'mobile-device-command',
        requestId: request.requestId,
        command: request.command,
        payload: request.payload ?? {},
      })
      .then((response) => {
        window.postMessage(
          {
            source: EXTENSION_SOURCE,
            protocol: 1,
            type: 'result',
            requestId: request.requestId,
            success: response?.success === true,
            result: response?.result,
            error: response?.error,
          },
          window.location.origin,
        );
      })
      .catch((error) => {
        window.postMessage(
          {
            source: EXTENSION_SOURCE,
            protocol: 1,
            type: 'result',
            requestId: request.requestId,
            success: false,
            error: error instanceof Error ? error.message : String(error),
          },
          window.location.origin,
        );
      });
  });

  window.postMessage(
    { source: EXTENSION_SOURCE, protocol: 1, type: 'ready', version: '1.0.0' },
    window.location.origin,
  );
})();

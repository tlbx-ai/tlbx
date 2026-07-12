(function () {
  var params = new URLSearchParams(window.location.search);
  var sessionId = params.get('session') || '';
  var initialPreviewName = params.get('preview') || 'default';
  var initialViewportWidth = parseInt(params.get('viewportWidth') || '0', 10) || 0;
  var initialViewportHeight = parseInt(params.get('viewportHeight') || '0', 10) || 0;
  var initialRouteKey = params.get('routeKey') || '';
  var initialPreviewId = params.get('previewId') || '';
  var initialPreviewToken = params.get('previewToken') || '';
  var initialPreviewOrigin = params.get('origin') || window.location.origin;
  var initialTargetUrl = params.get('url');
  var sandboxEnabled = params.get('sandbox') === '1';
  var initialMobileMode = params.get('mobile') === '1';
  var channelName = sessionId
    ? 'midterm-web-preview-' + sessionId + '-' + initialPreviewName
    : 'midterm-web-preview';
  var channel = new BroadcastChannel(channelName);
  var MOBILE_VIEWPORT_WIDTH = 390;
  var MOBILE_VIEWPORT_HEIGHT = 844;

  var sandboxBaseFlags = [
    'allow-scripts',
    'allow-forms',
    'allow-popups',
    'allow-modals',
    'allow-downloads',
  ];

  var activePreviewName = initialPreviewName;
  var currentUrl = null;
  var currentTargetRevision = 0;
  var previewRouteKey = initialRouteKey;
  var previewOrigin = initialPreviewOrigin;
  var previewContext =
    initialPreviewId && initialPreviewToken
      ? {
          sessionId: sessionId,
          previewName: initialPreviewName,
          routeKey: initialRouteKey,
          previewId: initialPreviewId,
          previewToken: initialPreviewToken,
        }
      : null;
  var previewSessions = [];
  var statusRefreshTimer = null;
  var mobileMode = initialMobileMode;

  var tabsHost = document.getElementById('web-preview-tabs');
  var urlInput = document.getElementById('web-preview-url-input');
  var statusIndicator = document.getElementById('web-preview-status-indicator');
  var deviceStatusNode = document.getElementById('web-preview-device-status');
  var actionMessage = document.getElementById('web-preview-action-message');
  var previewHost = document.getElementById('preview-host');
  var iframeHost = document.getElementById('web-preview-iframe-host');
  var emptyState = document.getElementById('web-preview-empty-state');
  var titleNode = document.querySelector('.web-preview-dock-title-text');
  var sessionSubtitleNode = document.getElementById('web-preview-session-subtitle');
  var screenshotButton = document.getElementById('web-preview-screenshot');
  var frame = createPreviewFrame();
  var owningSession = null;
  var screenshotInFlight = false;

  function getOrCreateTabId() {
    var name = 'mt-tab-id';
    try {
      var existing = window.sessionStorage ? window.sessionStorage.getItem(name) : null;
      if (existing) {
        return existing;
      }

      var id =
        window.crypto && typeof window.crypto.randomUUID === 'function'
          ? window.crypto.randomUUID()
          : String(Date.now()) + '-' + Math.random().toString(16).slice(2);
      if (window.sessionStorage) {
        window.sessionStorage.setItem(name, id);
      }
      return id;
    } catch (_) {
      return String(Date.now()) + '-' + Math.random().toString(16).slice(2);
    }
  }

  function createPreviewFrame() {
    var iframe = document.createElement('iframe');
    iframe.id = 'preview-frame';
    iframe.className = 'web-preview-iframe';
    iframe.src = 'about:blank';
    iframe.setAttribute(
      'allow',
      'camera *; microphone *; geolocation *; fullscreen *; autoplay *; clipboard-read *; clipboard-write *; display-capture *',
    );
    iframeHost.appendChild(iframe);
    return iframe;
  }

  function syncThemeFromOpener() {
    if (!window.opener || window.opener.closed) {
      return;
    }

    try {
      document.documentElement.style.cssText = window.opener.document.documentElement.style.cssText;
    } catch (_) {}
  }

  function buildPreviewQuery(targetSessionId, previewName) {
    var query = new URLSearchParams();
    query.set('sessionId', targetSessionId);
    if (previewName) {
      query.set('previewName', previewName);
    }
    return query.toString();
  }

  function normalizeUrl(raw) {
    if (!raw) {
      return '';
    }

    if (raw.indexOf('://') < 0) {
      var isLocal =
        raw.indexOf('localhost') === 0 ||
        raw.indexOf('127.0.0.1') === 0 ||
        raw.indexOf('[::1]') === 0;
      return (isLocal ? 'http://' : 'https://') + raw;
    }

    return raw;
  }

  function buildPreviewTabLabel(url) {
    var trimmed = typeof url === 'string' ? url.trim() : '';
    if (!trimmed) {
      return 'New Tab';
    }

    try {
      var parsed = new URL(trimmed);
      return parsed.host || parsed.hostname || trimmed;
    } catch (_) {
      return trimmed;
    }
  }

  function getProxyPrefix() {
    return '/webpreview/' + encodeURIComponent(previewRouteKey);
  }

  function buildProxyUrl(targetUrl, reloadToken) {
    var parsed = new URL(targetUrl);
    var path = parsed.pathname || '/';
    var prefix = getProxyPrefix();
    var proxyUrl = new URL(path === '/' ? prefix + '/' : prefix + path, previewOrigin);
    proxyUrl.search = parsed.search;
    proxyUrl.hash = parsed.hash;
    if (reloadToken) {
      proxyUrl.searchParams.set('__mtReloadToken', reloadToken);
    }
    if (currentTargetRevision > 0) {
      proxyUrl.searchParams.set('__mtTargetRevision', String(currentTargetRevision));
    }
    return proxyUrl.toString();
  }

  function createForceReloadToken() {
    if (window.crypto && typeof window.crypto.randomUUID === 'function') {
      return window.crypto.randomUUID();
    }

    return Date.now().toString(36) + '-' + Math.random().toString(36).slice(2, 10);
  }

  function getSandboxFlags() {
    var flags = sandboxBaseFlags.slice();
    try {
      if (new URL(previewOrigin, window.location.origin).origin !== window.location.origin) {
        flags.push('allow-same-origin');
      }
    } catch (_) {}
    return flags.join(' ');
  }

  function setCurrentUrl(url, updateInput) {
    currentUrl = url || null;
    upsertRenderedPreviewState(activePreviewName, currentUrl, currentTargetRevision);
    if (updateInput !== false) {
      urlInput.value = currentUrl || '';
    }
    updateTitle();
    renderTabs();
    renderEmptyState();
  }

  function updateTitle() {
    if (!titleNode) {
      return;
    }

    var sessionDisplay = getOwningSessionDisplayInfo();
    titleNode.textContent = sessionDisplay.primary;
    if (sessionSubtitleNode) {
      sessionSubtitleNode.textContent = sessionDisplay.secondary || '';
      sessionSubtitleNode.classList.toggle('hidden', !sessionDisplay.secondary);
    }
    document.title = sessionDisplay.primary + ' - MidTerm';
  }

  function setPreviewContext(client) {
    previewRouteKey = client.routeKey || '';
    previewOrigin = client.origin || window.location.origin;
    previewContext = {
      sessionId: sessionId,
      previewName: activePreviewName,
      routeKey: client.routeKey,
      previewId: client.previewId,
      previewToken: client.previewToken,
    };
  }

  function matchesPreviewMessage(data) {
    return (
      !!previewContext &&
      data.previewId === previewContext.previewId &&
      data.previewToken === previewContext.previewToken
    );
  }

  function resetViewport() {
    previewHost.classList.remove('viewport-constrained');
    frame.style.flex = '';
    frame.style.alignSelf = '';
    frame.style.width = '';
    frame.style.height = '';
    frame.style.maxWidth = '';
    frame.style.maxHeight = '';
  }

  function syncPopupViewportSize(targetWidth, targetHeight, attempt) {
    if (attempt > 4) {
      return;
    }

    var widthDelta = Math.round(targetWidth - frame.clientWidth);
    var heightDelta = Math.round(targetHeight - frame.clientHeight);
    if (Math.abs(widthDelta) <= 1 && Math.abs(heightDelta) <= 1) {
      return;
    }

    try {
      window.resizeBy(widthDelta, heightDelta);
    } catch (_) {
      return;
    }

    window.setTimeout(function () {
      syncPopupViewportSize(targetWidth, targetHeight, attempt + 1);
    }, 40);
  }

  function applyViewport(width, height) {
    if (width <= 0 && height <= 0) {
      resetViewport();
      return;
    }

    var targetWidth = width > 0 ? width : Math.max(frame.clientWidth, 1);
    var targetHeight = height > 0 ? height : Math.max(frame.clientHeight, 1);

    previewHost.classList.add('viewport-constrained');
    frame.style.flex = 'none';
    frame.style.alignSelf = 'center';
    frame.style.width = targetWidth + 'px';
    frame.style.height = targetHeight + 'px';
    frame.style.maxWidth = targetWidth + 'px';
    frame.style.maxHeight = targetHeight + 'px';

    window.requestAnimationFrame(function () {
      syncPopupViewportSize(targetWidth, targetHeight, 0);
    });
  }

  function getVisualViewportSize() {
    var visual = window.visualViewport;
    return {
      width: Math.round((visual && visual.width) || window.innerWidth || 0),
      height: Math.round((visual && visual.height) || window.innerHeight || 0),
    };
  }

  function readMobileClientProbe() {
    var viewport = getVisualViewportSize();
    var maxTouchPoints = navigator.maxTouchPoints || 0;
    var coarsePointer =
      typeof window.matchMedia === 'function' && window.matchMedia('(pointer: coarse)').matches;
    var hoverNone =
      typeof window.matchMedia === 'function' && window.matchMedia('(hover: none)').matches;
    var standalone =
      typeof window.matchMedia === 'function' &&
      window.matchMedia('(display-mode: standalone)').matches;
    var iosStandalone = navigator.standalone === true;
    var shortestSide = Math.min(
      viewport.width || window.innerWidth || 0,
      viewport.height || window.innerHeight || 0,
    );
    var likelyMobile =
      shortestSide > 0 &&
      shortestSide <= 820 &&
      (maxTouchPoints > 0 || coarsePointer || hoverNone);

    return {
      viewport: viewport,
      dpr: window.devicePixelRatio || 1,
      maxTouchPoints: maxTouchPoints,
      coarsePointer: coarsePointer,
      hoverNone: hoverNone,
      standalone: standalone || iosStandalone,
      likelyMobile: likelyMobile,
    };
  }

  function updateDeviceStatus() {
    if (!deviceStatusNode) {
      return;
    }

    if (!mobileMode) {
      deviceStatusNode.textContent = '';
      deviceStatusNode.title = '';
      deviceStatusNode.classList.add('hidden');
      return;
    }

    var probe = readMobileClientProbe();
    var label = probe.likelyMobile ? 'Real mobile' : 'Desktop mobile size';
    var title =
      label +
      ' - viewport ' +
      probe.viewport.width +
      'x' +
      probe.viewport.height +
      ', DPR ' +
      probe.dpr +
      ', touch ' +
      probe.maxTouchPoints +
      ', pointer ' +
      (probe.coarsePointer ? 'coarse' : 'fine') +
      (probe.standalone ? ', PWA standalone' : '');

    deviceStatusNode.textContent = label;
    deviceStatusNode.title = title;
    deviceStatusNode.classList.remove('hidden');
  }

  function applyMobileMode(enabled, reloadFrame) {
    mobileMode = enabled === true;
    document.body.classList.toggle('web-preview-popup-mobile-mode', mobileMode);

    var probe = readMobileClientProbe();
    document.body.classList.toggle(
      'web-preview-popup-real-mobile',
      mobileMode && probe.likelyMobile,
    );
    document.body.classList.toggle(
      'web-preview-popup-desktop-mobile-size',
      mobileMode && !probe.likelyMobile,
    );
    updateDeviceStatus();

    if (mobileMode) {
      if (probe.likelyMobile) {
        resetViewport();
      } else {
        applyViewport(MOBILE_VIEWPORT_WIDTH, MOBILE_VIEWPORT_HEIGHT);
      }
    } else if (initialViewportWidth > 0 || initialViewportHeight > 0) {
      applyViewport(initialViewportWidth, initialViewportHeight);
    } else {
      resetViewport();
    }

    if (reloadFrame === true && currentUrl) {
      loadFrame(currentUrl, createForceReloadToken());
    }
  }

  function decodeIframeNavigationUrl(iframeUrl, targetOrigin) {
    var parsed = new URL(iframeUrl, window.location.origin);
    var prefix = getProxyPrefix();

    if (parsed.pathname === prefix + '/_ext') {
      return parsed.searchParams.get('u');
    }

    var path = parsed.pathname;
    if (path.indexOf(prefix + '/') === 0) {
      path = path.substring(prefix.length);
    } else if (path === prefix) {
      path = '/';
    } else {
      return parsed.toString();
    }

    var baseOrigin = targetOrigin;
    if (!baseOrigin && currentUrl) {
      baseOrigin = new URL(currentUrl).origin;
    }

    if (!baseOrigin) {
      return null;
    }

    return baseOrigin + path + parsed.search + parsed.hash;
  }

  function renderEmptyState() {
    if (!emptyState) {
      return;
    }

    var hasTarget = !!(typeof currentUrl === 'string' && currentUrl.trim());
    emptyState.classList.toggle('hidden', hasTarget);
    iframeHost.classList.toggle('hidden', !hasTarget);
  }

  function renderTabs() {
    if (!tabsHost) {
      return;
    }

    tabsHost.replaceChildren();

    var sessions = getRenderedPreviewSessions();

    sessions.forEach(function (preview) {
      var button = document.createElement('button');
      button.type = 'button';
      button.className = 'web-preview-tab';
      if (preview.previewName === activePreviewName) {
        button.classList.add('active');
      }
      if (!preview.url) {
        button.classList.add('empty');
      }
      button.textContent = buildPreviewTabLabel(preview.url);
      button.title = preview.url ? preview.url.trim() : 'New Tab';
      button.dataset.previewName = preview.previewName;
      button.addEventListener('click', function () {
        void selectPreview(preview.previewName);
      });
      tabsHost.appendChild(button);
    });
  }

  function getPreviewSession(previewName) {
    for (var i = 0; i < previewSessions.length; i++) {
      if (previewSessions[i].previewName === previewName) {
        return previewSessions[i];
      }
    }
    return null;
  }

  function upsertRenderedPreviewState(previewName, url, targetRevision) {
    if (!previewName) {
      return;
    }

    for (var i = 0; i < previewSessions.length; i++) {
      if (previewSessions[i].previewName === previewName) {
        previewSessions[i].url = url || null;
        if (typeof targetRevision === 'number') {
          previewSessions[i].targetRevision = targetRevision;
        }
        return;
      }
    }

    previewSessions.push({
      previewName: previewName,
      url: url || null,
      targetRevision: typeof targetRevision === 'number' ? targetRevision : 0,
    });
  }

  function getRenderedPreviewSessions() {
    if (!previewSessions.length) {
      return [
        {
          previewName: activePreviewName,
          url: currentUrl,
          targetRevision: currentTargetRevision,
        },
      ];
    }

    var hasActive = false;
    var sessions = previewSessions.slice();
    for (var i = 0; i < sessions.length; i++) {
      if (sessions[i].previewName === activePreviewName) {
        hasActive = true;
        break;
      }
    }

    if (!hasActive) {
      sessions.push({
        previewName: activePreviewName,
        url: currentUrl,
        targetRevision: currentTargetRevision,
      });
    }

    sessions.sort(function (a, b) {
      if (a.previewName === b.previewName) {
        return 0;
      }
      if (a.previewName === 'default') {
        return -1;
      }
      if (b.previewName === 'default') {
        return 1;
      }
      return a.previewName.localeCompare(b.previewName);
    });

    return sessions;
  }

  function normalizeExecutableName(value) {
    var trimmed = typeof value === 'string' ? value.trim() : '';
    if (!trimmed) {
      return '';
    }

    var candidate = trimmed;
    var firstChar = candidate.charAt(0);
    if (firstChar === '"' || firstChar === "'") {
      var closingQuote = candidate.indexOf(firstChar, 1);
      if (closingQuote > 1) {
        candidate = candidate.slice(1, closingQuote);
      }
    }

    var basename = candidate.replace(/\\/g, '/').split('/').pop() || candidate;
    var token = (basename.trim().split(/\s+/)[0] || basename.trim()).toLowerCase();
    return token.replace(/\.exe$/i, '');
  }

  function isShellProcess(processName) {
    if (!owningSession || !owningSession.shellType || !processName) {
      return false;
    }

    var normalizedProcess = normalizeExecutableName(processName);
    var normalizedShell = normalizeExecutableName(owningSession.shellType);
    return normalizedProcess !== '' && normalizedProcess === normalizedShell;
  }

  function getSessionSurfaceLabel(session) {
    if (!session) {
      return 'Terminal';
    }

    if (session.appServerControlOnly) {
      return 'AppServerControl';
    }

    if (typeof session.terminalTitle === 'string' && session.terminalTitle.trim()) {
      return session.terminalTitle;
    }

    if (typeof session.shellType === 'string' && session.shellType.trim()) {
      return session.shellType;
    }

    return 'Terminal';
  }

  function getOwningSessionDisplayInfo() {
    var session = owningSession;
    if (!session) {
      return {
        primary: 'Web Preview',
        secondary: sessionId || null,
      };
    }

    var termTitle = getSessionSurfaceLabel(session);
    if (session.name) {
      return {
        primary: session.name,
        secondary: termTitle,
      };
    }

    if (session.terminalTitle && !isShellProcess(session.terminalTitle)) {
      return {
        primary: session.terminalTitle,
        secondary: null,
      };
    }

    return {
      primary: termTitle,
      secondary: null,
    };
  }

  async function loadOwningSession() {
    if (!sessionId) {
      owningSession = null;
      updateTitle();
      return;
    }

    try {
      var response = await fetch('/api/sessions');
      if (!response.ok) {
        updateTitle();
        return;
      }

      var data = await response.json();
      var sessions = data && Array.isArray(data.sessions) ? data.sessions : [];
      owningSession = null;
      for (var i = 0; i < sessions.length; i++) {
        if (sessions[i] && sessions[i].id === sessionId) {
          owningSession = sessions[i];
          break;
        }
      }
    } catch (_) {
      owningSession = null;
    }

    updateTitle();
  }

  async function listPreviewSessions() {
    if (!sessionId) {
      previewSessions = [];
      return [];
    }

    try {
      var response = await fetch('/api/webpreview/previews?' + buildPreviewQuery(sessionId));
      if (!response.ok) {
        return previewSessions;
      }

      var data = await response.json();
      previewSessions = Array.isArray(data.previews) ? data.previews : [];
      return previewSessions;
    } catch (_) {
      return previewSessions;
    }
  }

  async function ensurePreviewClient(previewName) {
    if (!sessionId) {
      return null;
    }

    try {
      var response = await fetch('/api/browser/preview-client', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          sessionId: sessionId,
          previewName: previewName,
          tabId: getOrCreateTabId(),
        }),
      });

      if (!response.ok) {
        return null;
      }

      return await response.json();
    } catch (_) {
      return null;
    }
  }

  async function setPreviewTarget(previewName, url) {
    if (!sessionId) {
      return null;
    }

    try {
      var response = await fetch('/api/webpreview/target', {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          sessionId: sessionId,
          previewName: previewName,
          url: url,
        }),
      });

      if (!response.ok) {
        return null;
      }

      return await response.json();
    } catch (_) {
      return null;
    }
  }

  async function clearPreviewCookies(previewName) {
    if (!sessionId) {
      return false;
    }

    try {
      var response = await fetch(
        '/api/webpreview/cookies/clear?' + buildPreviewQuery(sessionId, previewName),
        {
          method: 'POST',
        },
      );
      return response.ok;
    } catch (_) {
      return false;
    }
  }

  async function clearPreviewState(previewName) {
    if (!sessionId) {
      return null;
    }

    try {
      var response = await fetch(
        '/api/webpreview/state/clear?' + buildPreviewQuery(sessionId, previewName),
        {
          method: 'POST',
        },
      );

      if (!response.ok) {
        return null;
      }

      return await response.json();
    } catch (_) {
      return null;
    }
  }

  async function reloadPreview(previewName, mode) {
    if (!sessionId) {
      return false;
    }

    try {
      var response = await fetch('/api/webpreview/reload', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          sessionId: sessionId,
          previewName: previewName,
          mode: mode,
        }),
      });

      return response.ok;
    } catch (_) {
      return false;
    }
  }

  async function getBrowserStatus(previewName) {
    if (!sessionId) {
      return null;
    }

    var query = new URLSearchParams();
    query.set('sessionId', sessionId);
    query.set('previewName', previewName);
    if (previewContext && previewContext.previewId) {
      query.set('previewId', previewContext.previewId);
    }

    try {
      var response = await fetch('/api/browser/status?' + query.toString());
      if (!response.ok) {
        return null;
      }

      return await response.json();
    } catch (_) {
      return null;
    }
  }

  async function runBrowserCommand(command, previewName) {
    if (!sessionId) {
      return null;
    }

    try {
      var response = await fetch('/api/browser/command', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          command: command,
          sessionId: sessionId,
          previewName: previewName,
          previewId: previewContext ? previewContext.previewId : undefined,
        }),
      });

      return await response.json();
    } catch (_) {
      return null;
    }
  }

  async function captureBrowserScreenshotRaw(previewName) {
    if (!sessionId) {
      return null;
    }

    try {
      var response = await fetch('/api/browser/screenshot-raw', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          sessionId: sessionId,
          previewName: previewName,
          previewId: previewContext ? previewContext.previewId : undefined,
        }),
      });

      if (!response.ok) {
        return null;
      }

      var data = await response.json();
      return data && data.success && typeof data.result === 'string' ? data.result : null;
    } catch (_) {
      return null;
    }
  }

  async function sendSessionText(text, appendNewline) {
    if (!sessionId) {
      return false;
    }

    try {
      var response = await fetch('/api/sessions/' + encodeURIComponent(sessionId) + '/input/text', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          text: text,
          appendNewline: appendNewline === true,
        }),
      });

      return response.ok;
    } catch (_) {
      return false;
    }
  }

  async function uploadFile(blob, fileName) {
    if (!sessionId) {
      return null;
    }

    var file = new File([blob], fileName, { type: blob.type || 'application/octet-stream' });
    var formData = new FormData();
    formData.append('file', file);

    try {
      var response = await fetch('/api/sessions/' + encodeURIComponent(sessionId) + '/upload', {
        method: 'POST',
        body: formData,
      });

      if (!response.ok) {
        return null;
      }

      return await response.json();
    } catch (_) {
      return null;
    }
  }

  function decodeScreenshotDataUrl(dataUrl) {
    var commaIndex = dataUrl.indexOf(',');
    if (commaIndex < 0) {
      return null;
    }

    var meta = dataUrl.slice(0, commaIndex);
    var mimeMatch = /^data:([^;]+)/.exec(meta);
    var mime = mimeMatch ? mimeMatch[1] : 'image/png';
    try {
      var binary = atob(dataUrl.slice(commaIndex + 1));
      var bytes = new Uint8Array(binary.length);
      for (var i = 0; i < binary.length; i++) {
        bytes[i] = binary.charCodeAt(i);
      }
      return new Blob([bytes], { type: mime });
    } catch (_) {
      return null;
    }
  }

  function setStatusIndicator(severity, message) {
    if (!statusIndicator) {
      return;
    }

    if (!message) {
      statusIndicator.textContent = '!';
      statusIndicator.title = '';
      statusIndicator.classList.add('hidden');
      statusIndicator.dataset.severity = 'info';
      statusIndicator.setAttribute('aria-hidden', 'true');
      statusIndicator.removeAttribute('aria-label');
      return;
    }

    statusIndicator.textContent = '!';
    statusIndicator.title = message;
    statusIndicator.dataset.severity = severity;
    statusIndicator.classList.remove('hidden');
    statusIndicator.setAttribute('aria-hidden', 'false');
    statusIndicator.setAttribute('aria-label', message);
  }

  function setActionMessage(severity, message) {
    if (!actionMessage) {
      return;
    }

    if (!message) {
      actionMessage.textContent = '';
      actionMessage.dataset.severity = '';
      actionMessage.classList.add('hidden');
      return;
    }

    actionMessage.textContent = message;
    actionMessage.dataset.severity = severity;
    actionMessage.classList.remove('hidden');
  }

  function setScreenshotButtonBusy(active) {
    if (!screenshotButton) {
      return;
    }

    var idleGlyph = screenshotButton.dataset.idleGlyph || screenshotButton.innerHTML;
    screenshotButton.dataset.idleGlyph = idleGlyph;
    var idleTitle = screenshotButton.dataset.idleTitle || screenshotButton.title || 'Screenshot to terminal';
    screenshotButton.dataset.idleTitle = idleTitle;

    if (active) {
      screenshotButton.disabled = true;
      screenshotButton.setAttribute('aria-busy', 'true');
      screenshotButton.classList.add('web-preview-action-working');
      screenshotButton.innerHTML = '&#x21bb;';
      screenshotButton.title = 'Capturing screenshot...';
      return;
    }

    screenshotButton.disabled = false;
    screenshotButton.setAttribute('aria-busy', 'false');
    screenshotButton.classList.remove('web-preview-action-working');
    screenshotButton.innerHTML = idleGlyph;
    screenshotButton.title = idleTitle;
  }

  function describeBrowserStatus(status) {
    if (!status) {
      return {
        severity: 'warn',
        message:
          'Browser status is currently unavailable, so the dev browser state cannot be verified honestly.',
      };
    }

    if (!status.hasUiClient) {
      return {
        severity: 'error',
        message:
          'No MidTerm browser tab is connected to /ws/state. The dev browser cannot work until a live MidTerm tab is open.',
      };
    }

    if (!status.controllable) {
      return {
        severity: 'warn',
        message:
          typeof status.statusMessage === 'string' && status.statusMessage
            ? status.statusMessage
            : 'The detached browser preview is not controllable yet.',
      };
    }

    var client = status.defaultClient;
    if (client && (!client.isVisible || !client.hasFocus)) {
      return {
        severity: 'info',
        message:
          'The attached browser preview is currently in a background tab or window. Automation may be slower or throttled there.',
      };
    }

    return null;
  }

  async function refreshStatusIndicator() {
    if (!currentUrl && !(previewContext && previewContext.previewId)) {
      setStatusIndicator('info', null);
      return;
    }

    var status = await getBrowserStatus(activePreviewName);
    var indicator = describeBrowserStatus(status);
    if (!indicator) {
      setStatusIndicator('info', null);
      return;
    }

    setStatusIndicator(indicator.severity, indicator.message);
  }

  function postCookieBridgeResponse(target, message) {
    if (!target) {
      return;
    }
    target.postMessage(message, '*');
  }

  function handleCookieBridgeRequest(event, data) {
    if (!previewRouteKey) {
      postCookieBridgeResponse(event.source, {
        type: 'mt-cookie-response',
        requestId: data.requestId,
        previewId: data.previewId,
        previewToken: data.previewToken,
        sessionId: data.sessionId,
        previewName: data.previewName,
        error: 'No preview route',
      });
      return;
    }

    var target = event.source;
    var url = new URL(getProxyPrefix() + '/_cookies', window.location.origin);
    var upstreamUrl =
      typeof data.upstreamUrl === 'string' && data.upstreamUrl ? data.upstreamUrl : currentUrl;
    if (upstreamUrl) {
      url.searchParams.set('u', upstreamUrl);
    }

    var responseMessage = {
      type: 'mt-cookie-response',
      requestId: data.requestId,
      previewId: data.previewId,
      previewToken: data.previewToken,
      sessionId: data.sessionId,
      previewName: data.previewName,
    };

    var request =
      data.action === 'set'
        ? fetch(url.toString(), {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ raw: typeof data.raw === 'string' ? data.raw : '' }),
          })
        : fetch(url.toString(), { method: 'GET' });

    request
      .then(function (response) {
        if (!response.ok) {
          responseMessage.error = 'Cookie bridge failed: ' + response.status;
          postCookieBridgeResponse(target, responseMessage);
          return null;
        }
        return response.json();
      })
      .then(function (json) {
        if (!json) {
          return;
        }
        responseMessage.header = typeof json.header === 'string' ? json.header : '';
        postCookieBridgeResponse(target, responseMessage);
      })
      .catch(function (error) {
        responseMessage.error = String(error);
        postCookieBridgeResponse(target, responseMessage);
      });
  }

  function loadFrame(url, reloadToken) {
    if (!url || !previewRouteKey) {
      frame.removeAttribute('sandbox');
      frame.name = '';
      frame.src = 'about:blank';
      setCurrentUrl(null);
      return;
    }

    setCurrentUrl(url);

    try {
      if (sandboxEnabled) {
        frame.setAttribute('sandbox', getSandboxFlags());
      } else {
        frame.removeAttribute('sandbox');
      }
      frame.name = previewContext ? JSON.stringify(previewContext) : '';
      frame.src = buildProxyUrl(url, reloadToken);
    } catch (_) {
      frame.removeAttribute('sandbox');
      frame.name = '';
      frame.src = 'about:blank';
    }
  }

  async function selectPreview(previewName) {
    activePreviewName = previewName || 'default';
    var selectedPreview = getPreviewSession(activePreviewName);
    if (!selectedPreview) {
      await listPreviewSessions();
      selectedPreview = getPreviewSession(activePreviewName);
    }

    currentTargetRevision = selectedPreview && selectedPreview.targetRevision ? selectedPreview.targetRevision : 0;
    var client = await ensurePreviewClient(activePreviewName);
    if (!client) {
      return;
    }

    setPreviewContext(client);
    loadFrame(selectedPreview && selectedPreview.url ? selectedPreview.url : null);
    renderTabs();
    await refreshStatusIndicator();
  }

  async function handleGo() {
    var url = normalizeUrl((urlInput.value || '').trim());
    if (!url) {
      return;
    }

    urlInput.value = url;
    var result = await setPreviewTarget(activePreviewName, url);
    if (!result) {
      return;
    }

    currentTargetRevision = result.targetRevision || 0;
    await listPreviewSessions();
    var client = await ensurePreviewClient(activePreviewName);
    if (!client) {
      return;
    }

    setPreviewContext(client);
    loadFrame(url);
    await refreshStatusIndicator();
  }

  async function handleRefresh(event) {
    var mode = event && (event.shiftKey || event.ctrlKey || event.altKey) ? 'hard' : 'force';
    if (currentUrl) {
      var result = await setPreviewTarget(activePreviewName, currentUrl);
      if (result) {
        currentTargetRevision = result.targetRevision || currentTargetRevision;
      }
    }
    if (mode === 'hard') {
      await reloadPreview(activePreviewName, mode);
    }
    loadFrame(currentUrl, createForceReloadToken());
    await refreshStatusIndicator();
  }

  async function handleClearCookies() {
    if (!(await clearPreviewCookies(activePreviewName))) {
      return;
    }

    loadFrame(currentUrl, createForceReloadToken());
    await refreshStatusIndicator();
  }

  async function handleClearState() {
    var result = await clearPreviewState(activePreviewName);
    if (!result) {
      return;
    }

    currentTargetRevision = result.targetRevision || 0;
    await listPreviewSessions();
    setCurrentUrl(result.url || null);
    await runBrowserCommand('clearstate', activePreviewName);
    loadFrame(result.url || null, createForceReloadToken());
    await refreshStatusIndicator();
  }

  async function handleScreenshot(event) {
    if (screenshotInFlight) {
      return;
    }

    if (!currentUrl || frame.src === 'about:blank') {
      setActionMessage('error', 'Screenshot failed: there is no active browser preview to capture.');
      return;
    }

    screenshotInFlight = true;
    setActionMessage('info', null);
    setScreenshotButtonBusy(true);

    var dataUrl = await captureBrowserScreenshotRaw(activePreviewName);
    try {
      if (!dataUrl) {
        setActionMessage(
          'error',
          'Screenshot failed: MidTerm did not receive image data back from the dev browser.',
        );
        return;
      }

      var blob = decodeScreenshotDataUrl(dataUrl);
      if (!blob) {
        setActionMessage(
          'error',
          'Screenshot failed: the returned image data could not be decoded.',
        );
        return;
      }

      var fileName = 'screenshot_' + new Date().toISOString().replace(/[:.]/g, '-') + '.png';
      if (event && event.ctrlKey) {
        var downloadUrl = URL.createObjectURL(blob);
        var link = document.createElement('a');
        link.href = downloadUrl;
        link.download = fileName;
        link.click();
        URL.revokeObjectURL(downloadUrl);
        setActionMessage('info', null);
        return;
      }

      var upload = await uploadFile(blob, fileName);
      if (!upload) {
        setActionMessage(
          'error',
          'Screenshot failed: MidTerm could not upload it to the owning session.',
        );
        return;
      }

      if (typeof upload.path === 'string' && upload.path) {
        var pasted = await sendSessionText('"' + upload.path + '"', false);
        if (!pasted) {
          setActionMessage(
            'error',
            'Screenshot failed: the file was uploaded, but MidTerm could not paste its path into the owning terminal.',
          );
          return;
        }
        setActionMessage('info', null);
        return;
      }

      setActionMessage(
        'error',
        'Screenshot failed: the upload completed but MidTerm did not return a usable file path.',
      );
    } finally {
      screenshotInFlight = false;
      setScreenshotButtonBusy(false);
    }
  }

  async function handleAgentHint() {
    var guidanceFile = '.midterm/AGENTS.md';

    if (owningSession && typeof owningSession.foregroundName === 'string') {
      guidanceFile =
        owningSession.foregroundName.toLowerCase() === 'claude'
          ? '.midterm/CLAUDE.md'
          : '.midterm/AGENTS.md';
    }

    await sendSessionText(
      'Read the file ' + guidanceFile + ' for instructions on how to interact with this browser preview.\n',
      false,
    );
  }

  function handleDockBack() {
    channel.postMessage({
      type: 'dock-back',
      sessionId: sessionId,
      previewName: activePreviewName,
    });
    window.close();
  }

  function bindEvents() {
    document.getElementById('web-preview-go').addEventListener('click', function () {
      void handleGo();
    });
    urlInput.addEventListener('keydown', function (event) {
      if (event.key === 'Enter') {
        event.preventDefault();
        void handleGo();
      }
    });
    document.getElementById('web-preview-refresh').addEventListener('click', function (event) {
      void handleRefresh(event);
    });
    document.getElementById('web-preview-clear-cookies').addEventListener('click', function () {
      void handleClearCookies();
    });
    document.getElementById('web-preview-clear-state').addEventListener('click', function () {
      void handleClearState();
    });
    document.getElementById('web-preview-screenshot').addEventListener('click', function (event) {
      void handleScreenshot(event);
    });
    document.getElementById('web-preview-agent-hint').addEventListener('click', function () {
      void handleAgentHint();
    });
    document.getElementById('web-preview-dock-back').addEventListener('click', handleDockBack);
    document.getElementById('web-preview-close').addEventListener('click', function () {
      window.close();
    });
  }

  channel.onmessage = function (event) {
    if (!event || !event.data) {
      return;
    }

    if (event.data.type === 'set-url' && typeof event.data.url === 'string') {
      setCurrentUrl(event.data.url);
      loadFrame(event.data.url);
      return;
    }

    if (event.data.type === 'refresh') {
      loadFrame(currentUrl, createForceReloadToken());
      return;
    }

    if (event.data.type === 'viewport') {
      applyViewport(Number(event.data.width) || 0, Number(event.data.height) || 0);
      return;
    }

    if (event.data.type === 'mobile-mode') {
      applyMobileMode(event.data.enabled === true, true);
    }
  };

  window.addEventListener('message', function (event) {
    if (event.source !== frame.contentWindow || !event.data || typeof event.data.type !== 'string') {
      return;
    }

    if (event.data.type === 'mt-navigation') {
      if (typeof event.data.url !== 'string' || !matchesPreviewMessage(event.data)) {
        return;
      }

      try {
        var displayUrl =
          typeof event.data.upstreamUrl === 'string' && event.data.upstreamUrl
            ? event.data.upstreamUrl
            : decodeIframeNavigationUrl(
                event.data.url,
                typeof event.data.targetOrigin === 'string' ? event.data.targetOrigin : '',
              );
        if (!displayUrl) {
          return;
        }

        setCurrentUrl(displayUrl);
        channel.postMessage({
          type: 'navigation',
          sessionId: sessionId,
          previewName: activePreviewName,
          url: displayUrl,
        });
      } catch (_) {}
      return;
    }

    if (event.data.type === 'mt-cookie-request' && matchesPreviewMessage(event.data)) {
      handleCookieBridgeRequest(event, event.data);
    }
  });

  window.addEventListener('beforeunload', function () {
    channel.postMessage({
      type: 'popup-closed',
      sessionId: sessionId,
      previewName: activePreviewName,
    });
  });

  document.addEventListener('visibilitychange', function () {
    void refreshStatusIndicator();
  });
  window.addEventListener('focus', function () {
    syncThemeFromOpener();
    void refreshStatusIndicator();
  });
  window.addEventListener('blur', function () {
    void refreshStatusIndicator();
  });
  window.addEventListener('resize', function () {
    if (!mobileMode) {
      return;
    }
    updateDeviceStatus();
    if (readMobileClientProbe().likelyMobile) {
      document.body.classList.add('web-preview-popup-real-mobile');
      document.body.classList.remove('web-preview-popup-desktop-mobile-size');
      resetViewport();
    }
  });

  async function initialize() {
    syncThemeFromOpener();
    bindEvents();
    await loadOwningSession();

    await listPreviewSessions();
    renderTabs();

    var initialPreview = getPreviewSession(activePreviewName);
    if (initialPreview) {
      currentTargetRevision = initialPreview.targetRevision || 0;
    }

    if (!previewContext) {
      var client = await ensurePreviewClient(activePreviewName);
      if (client) {
        setPreviewContext(client);
      }
    }

    var initialUrl = initialPreview && initialPreview.url ? initialPreview.url : initialTargetUrl;
    applyMobileMode(initialMobileMode, false);
    loadFrame(initialUrl);

    if (!initialMobileMode && (initialViewportWidth > 0 || initialViewportHeight > 0)) {
      applyViewport(initialViewportWidth, initialViewportHeight);
    }

    if (statusRefreshTimer === null) {
      statusRefreshTimer = window.setInterval(function () {
        void refreshStatusIndicator();
      }, 4000);
    }

    await refreshStatusIndicator();
  }

  void initialize();
})();

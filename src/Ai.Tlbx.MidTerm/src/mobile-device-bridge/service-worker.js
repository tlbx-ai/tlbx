const APPROVED_TABS_KEY = 'midtermApprovedTabs';
const DEVICES_KEY = 'midtermDevices';

const PROFILES = {
  'pixel-8': {
    id: 'pixel-8',
    label: 'Pixel 8',
    width: 412,
    height: 915,
    deviceScaleFactor: 2.625,
    maxTouchPoints: 5,
    platformVersion: '14.0.0',
    model: 'Pixel 8',
    safeArea: { top: 0, right: 0, bottom: 0, left: 0 },
  },
};

const ALLOWED_COMMANDS = new Set([
  'ping',
  'status',
  'open',
  'rotate',
  'keyboard',
  'background',
  'foreground',
  'reload',
  'screenshot',
  'close',
]);

async function getSessionValue(key, fallback) {
  const values = await chrome.storage.session.get(key);
  return values[key] ?? fallback;
}

async function setSessionValue(key, value) {
  await chrome.storage.session.set({ [key]: value });
}

async function getDevices() {
  return getSessionValue(DEVICES_KEY, {});
}

async function saveDevices(devices) {
  await setSessionValue(DEVICES_KEY, devices);
}

async function injectPageBridge(tabId) {
  await chrome.scripting.executeScript({ target: { tabId }, files: ['page-bridge.js'] });
  await chrome.action.setBadgeBackgroundColor({ tabId, color: '#18885f' });
  await chrome.action.setBadgeText({ tabId, text: 'ON' });
}

chrome.action.onClicked.addListener(async (tab) => {
  if (!tab.id) return;
  const approvedTabs = await getSessionValue(APPROVED_TABS_KEY, []);
  if (!approvedTabs.includes(tab.id)) {
    approvedTabs.push(tab.id);
    await setSessionValue(APPROVED_TABS_KEY, approvedTabs);
  }
  try {
    await injectPageBridge(tab.id);
  } catch (error) {
    await chrome.action.setBadgeBackgroundColor({ tabId: tab.id, color: '#b33a3a' });
    await chrome.action.setBadgeText({ tabId: tab.id, text: 'ERR' });
    console.error('MidTerm bridge injection failed', error);
  }
});

chrome.tabs.onUpdated.addListener(async (tabId, changeInfo) => {
  if (changeInfo.status !== 'complete') return;
  const approvedTabs = await getSessionValue(APPROVED_TABS_KEY, []);
  if (!approvedTabs.includes(tabId)) return;
  try {
    await injectPageBridge(tabId);
  } catch {
    // The approved tab may have navigated away from an injectable page.
  }
});

chrome.tabs.onRemoved.addListener(async (tabId) => {
  const approvedTabs = await getSessionValue(APPROVED_TABS_KEY, []);
  if (approvedTabs.includes(tabId)) {
    await setSessionValue(
      APPROVED_TABS_KEY,
      approvedTabs.filter((id) => id !== tabId),
    );
  }

  const devices = await getDevices();
  let changed = false;
  for (const [deviceKey, state] of Object.entries(devices)) {
    if (state.tabId === tabId) {
      delete devices[deviceKey];
      changed = true;
    }
  }
  if (changed) await saveDevices(devices);
});

chrome.debugger.onDetach.addListener(async (source) => {
  if (!source.tabId) return;
  const devices = await getDevices();
  let changed = false;
  for (const state of Object.values(devices)) {
    if (state.tabId === source.tabId) {
      state.attached = false;
      changed = true;
    }
  }
  if (changed) await saveDevices(devices);
});

async function sendCdp(tabId, method, params = {}) {
  return chrome.debugger.sendCommand({ tabId }, method, params);
}

async function ensureAttached(state) {
  try {
    await sendCdp(state.tabId, 'Runtime.evaluate', { expression: '1' });
    state.attached = true;
    return;
  } catch {
    // Reattach after extension worker suspension or explicit DevTools detach.
  }

  await chrome.debugger.attach({ tabId: state.tabId }, '1.3');
  state.attached = true;
}

function getProfile(profileId) {
  return PROFILES[profileId] ?? PROFILES['pixel-8'];
}

function getDimensions(state, profile) {
  const landscape = state.orientation === 'landscape';
  const fullWidth = landscape ? profile.height : profile.width;
  const fullHeight = landscape ? profile.width : profile.height;
  const keyboardHeight = state.keyboard ? Math.min(360, Math.floor(fullHeight * 0.44)) : 0;
  return {
    width: fullWidth,
    height: Math.max(320, fullHeight - keyboardHeight),
    screenWidth: fullWidth,
    screenHeight: fullHeight,
  };
}

function getSafeArea(state, profile) {
  if (state.orientation === 'landscape') {
    return { top: 0, right: profile.safeArea.top, bottom: 0, left: profile.safeArea.top };
  }
  return profile.safeArea;
}

function getChromeVersion() {
  const match = navigator.userAgent.match(/Chrome\/(\S+)/);
  return match?.[1] ?? '120.0.0.0';
}

async function tryCdp(tabId, method, params) {
  try {
    await sendCdp(tabId, method, params);
  } catch {
    // Experimental commands are best-effort across Chrome versions.
  }
}

async function applyDeviceState(state) {
  const profile = getProfile(state.profileId);
  const size = getDimensions(state, profile);
  const safeArea = getSafeArea(state, profile);
  const chromeVersion = getChromeVersion();
  const majorVersion = chromeVersion.split('.')[0];
  const orientation = state.orientation === 'landscape'
    ? { type: 'landscapePrimary', angle: 90 }
    : { type: 'portraitPrimary', angle: 0 };

  await sendCdp(state.tabId, 'Emulation.setDeviceMetricsOverride', {
    width: size.width,
    height: size.height,
    screenWidth: size.screenWidth,
    screenHeight: size.screenHeight,
    deviceScaleFactor: profile.deviceScaleFactor,
    mobile: true,
    screenOrientation: orientation,
  });
  await sendCdp(state.tabId, 'Emulation.setTouchEmulationEnabled', {
    enabled: true,
    maxTouchPoints: profile.maxTouchPoints,
  });
  await tryCdp(state.tabId, 'Emulation.setEmitTouchEventsForMouse', {
    enabled: true,
    configuration: 'mobile',
  });
  await sendCdp(state.tabId, 'Emulation.setUserAgentOverride', {
    userAgent: `Mozilla/5.0 (Linux; Android 14; ${profile.model}) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/${chromeVersion} Mobile Safari/537.36`,
    acceptLanguage: state.acceptLanguage || 'en-US',
    platform: 'Android',
    userAgentMetadata: {
      brands: [
        { brand: 'Chromium', version: majorVersion },
        { brand: 'Google Chrome', version: majorVersion },
        { brand: 'Not_A Brand', version: '99' },
      ],
      fullVersionList: [
        { brand: 'Chromium', version: chromeVersion },
        { brand: 'Google Chrome', version: chromeVersion },
        { brand: 'Not_A Brand', version: '99.0.0.0' },
      ],
      fullVersion: chromeVersion,
      platform: 'Android',
      platformVersion: profile.platformVersion,
      architecture: '',
      model: profile.model,
      mobile: true,
      bitness: '',
      wow64: false,
    },
  });
  await tryCdp(state.tabId, 'Emulation.setSafeAreaInsetsOverride', {
    insets: {
      top: safeArea.top,
      topMax: safeArea.top,
      right: safeArea.right,
      rightMax: safeArea.right,
      bottom: safeArea.bottom,
      bottomMax: safeArea.bottom,
      left: safeArea.left,
      leftMax: safeArea.left,
    },
  });
  await tryCdp(state.tabId, 'Emulation.setSmallViewportHeightDifferenceOverride', {
    difference: state.keyboard ? size.screenHeight - size.height : 84,
  });
  await tryCdp(state.tabId, 'Emulation.setEmulatedMedia', {
    media: '',
    features: [{ name: 'display-mode', value: 'browser' }],
  });
  await tryCdp(state.tabId, 'Emulation.setScrollbarsHidden', { hidden: true });

  if (state.windowId) {
    await chrome.windows.update(state.windowId, {
      width: size.screenWidth + 36,
      height: size.screenHeight + 126,
    });
  }
}

function publicState(state) {
  if (!state) return { connected: true, open: false };
  const profile = getProfile(state.profileId);
  const size = getDimensions(state, profile);
  return {
    connected: true,
    open: true,
    profileId: profile.id,
    profileLabel: profile.label,
    orientation: state.orientation,
    keyboard: state.keyboard,
    background: state.background,
    width: size.width,
    height: size.height,
    deviceScaleFactor: profile.deviceScaleFactor,
  };
}

async function requireDevice(payload) {
  const deviceKey = typeof payload.deviceKey === 'string' ? payload.deviceKey : '';
  if (!deviceKey) throw new Error('deviceKey is required');
  const devices = await getDevices();
  const state = devices[deviceKey];
  if (!state) throw new Error('No mobile device is open for this preview.');
  try {
    await chrome.tabs.get(state.tabId);
  } catch {
    delete devices[deviceKey];
    await saveDevices(devices);
    throw new Error('The mobile device window is no longer open.');
  }
  return { deviceKey, devices, state };
}

async function openDevice(payload, controllerTabId) {
  const deviceKey = typeof payload.deviceKey === 'string' ? payload.deviceKey : '';
  const url = typeof payload.url === 'string' ? payload.url : '';
  if (!deviceKey || !url) throw new Error('deviceKey and url are required');
  const parsed = new URL(url);
  if (!['http:', 'https:'].includes(parsed.protocol)) {
    throw new Error('Only HTTP and HTTPS preview URLs can be opened.');
  }

  const devices = await getDevices();
  let state = devices[deviceKey];
  if (state) {
    try {
      await chrome.tabs.get(state.tabId);
    } catch {
      state = null;
    }
  }

  if (!state) {
    const profile = getProfile(payload.profileId);
    const createdWindow = await chrome.windows.create({
      url: 'about:blank',
      type: 'normal',
      width: profile.width + 36,
      height: profile.height + 126,
      focused: true,
    });
    const tab = createdWindow.tabs?.[0];
    if (!createdWindow.id || !tab?.id) throw new Error('Chrome did not create a device tab.');
    state = {
      deviceKey,
      controllerTabId,
      tabId: tab.id,
      windowId: createdWindow.id,
      profileId: getProfile(payload.profileId).id,
      orientation: 'portrait',
      keyboard: false,
      background: false,
      attached: false,
      acceptLanguage: typeof payload.language === 'string' ? payload.language : 'en-US',
    };
  } else {
    state.controllerTabId = controllerTabId;
    state.profileId = getProfile(payload.profileId ?? state.profileId).id;
  }

  await ensureAttached(state);
  await applyDeviceState(state);
  await sendCdp(state.tabId, 'Page.setWebLifecycleState', { state: 'active' });
  await tryCdp(state.tabId, 'Emulation.setFocusEmulationEnabled', { enabled: true });
  state.background = false;
  await sendCdp(state.tabId, 'Page.navigate', { url });
  await sendCdp(state.tabId, 'Page.bringToFront');
  await chrome.windows.update(state.windowId, { focused: true });
  devices[deviceKey] = state;
  await saveDevices(devices);
  return publicState(state);
}

async function handleCommand(command, payload, controllerTabId) {
  if (!ALLOWED_COMMANDS.has(command)) throw new Error(`Unsupported command: ${command}`);
  if (command === 'ping') return { connected: true, version: '1.0.0' };
  if (command === 'open') return openDevice(payload, controllerTabId);

  if (command === 'status') {
    const deviceKey = typeof payload.deviceKey === 'string' ? payload.deviceKey : '';
    const devices = await getDevices();
    const state = devices[deviceKey];
    if (!state) return publicState(null);
    try {
      await chrome.tabs.get(state.tabId);
      return publicState(state);
    } catch {
      delete devices[deviceKey];
      await saveDevices(devices);
      return publicState(null);
    }
  }

  const { deviceKey, devices, state } = await requireDevice(payload);
  await ensureAttached(state);

  switch (command) {
    case 'rotate':
      state.orientation = state.orientation === 'portrait' ? 'landscape' : 'portrait';
      await applyDeviceState(state);
      break;
    case 'keyboard':
      state.keyboard = !state.keyboard;
      await applyDeviceState(state);
      break;
    case 'background':
      await tryCdp(state.tabId, 'Emulation.setFocusEmulationEnabled', { enabled: false });
      await sendCdp(state.tabId, 'Page.setWebLifecycleState', { state: 'frozen' });
      state.background = true;
      break;
    case 'foreground':
      await sendCdp(state.tabId, 'Page.setWebLifecycleState', { state: 'active' });
      await tryCdp(state.tabId, 'Emulation.setFocusEmulationEnabled', { enabled: true });
      await sendCdp(state.tabId, 'Page.bringToFront');
      await chrome.windows.update(state.windowId, { focused: true });
      state.background = false;
      break;
    case 'reload':
      if (state.background) {
        await sendCdp(state.tabId, 'Page.setWebLifecycleState', { state: 'active' });
        state.background = false;
      }
      await sendCdp(state.tabId, 'Page.reload', { ignoreCache: Boolean(payload.hard) });
      break;
    case 'screenshot': {
      const capture = await sendCdp(state.tabId, 'Page.captureScreenshot', {
        format: 'png',
        fromSurface: true,
        captureBeyondViewport: false,
      });
      return { ...publicState(state), dataUrl: `data:image/png;base64,${capture.data}` };
    }
    case 'close':
      delete devices[deviceKey];
      await saveDevices(devices);
      try {
        await chrome.debugger.detach({ tabId: state.tabId });
      } catch {
        // Already detached.
      }
      await chrome.windows.remove(state.windowId);
      return publicState(null);
  }

  devices[deviceKey] = state;
  await saveDevices(devices);
  return publicState(state);
}

chrome.runtime.onMessage.addListener((message, sender, sendResponse) => {
  if (message?.type !== 'mobile-device-command' || sender.id !== chrome.runtime.id) return false;
  handleCommand(message.command, message.payload ?? {}, sender.tab?.id ?? 0)
    .then((result) => sendResponse({ success: true, result }))
    .catch((error) =>
      sendResponse({
        success: false,
        error: error instanceof Error ? error.message : String(error),
      }),
    );
  return true;
});

// Exposes a worker-local diagnostics hook for repeatable Chrome/CDP smoke tests.
globalThis.__midtermMobileDeviceBridge = Object.freeze({
  run: (command, payload = {}) => handleCommand(command, payload, 0),
});

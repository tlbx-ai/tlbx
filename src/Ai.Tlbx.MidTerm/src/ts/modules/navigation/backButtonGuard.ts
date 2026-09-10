type BackButtonCloseHandler = () => void;

interface BackButtonLayer {
  id: number;
  close: BackButtonCloseHandler;
}

const HISTORY_STATE_KEY = '__midtermBackGuard';
const HISTORY_STATE_VALUE = 'guard';
const HANDLER_WINDOW_KEY = '__mtBackButtonGuardHandler';

let initialized = false;
let nextLayerId = 1;
const layers: BackButtonLayer[] = [];

type WindowWithHandler = Window &
  typeof globalThis & {
    [HANDLER_WINDOW_KEY]?: () => void;
  };

function getWindowWithHandler(): WindowWithHandler {
  return window;
}

function readHistoryState(): Record<string, unknown> {
  const state: unknown = window.history.state;
  if (typeof state === 'object' && state !== null && !Array.isArray(state)) {
    return { ...(state as Record<string, unknown>) };
  }

  return {};
}

function ensureGuardEntry(): void {
  const currentState = readHistoryState();
  if (currentState[HISTORY_STATE_KEY] === HISTORY_STATE_VALUE) {
    return;
  }

  const baseState = {
    ...currentState,
    [HISTORY_STATE_KEY]: 'base',
  };

  window.history.replaceState(baseState, document.title, window.location.href);
  window.history.pushState(
    {
      ...baseState,
      [HISTORY_STATE_KEY]: HISTORY_STATE_VALUE,
    },
    document.title,
    window.location.href,
  );
}

function handlePopState(): void {
  const topLayer = layers[layers.length - 1];

  try {
    topLayer?.close();
  } finally {
    window.setTimeout(() => {
      ensureGuardEntry();
    }, 0);
  }
}

export function initBackButtonGuard(): void {
  if (initialized || typeof window === 'undefined') {
    return;
  }

  if (typeof window.history.pushState !== 'function') {
    return;
  }

  const windowWithHandler = getWindowWithHandler();
  const existingHandler = windowWithHandler[HANDLER_WINDOW_KEY];
  if (existingHandler) {
    window.removeEventListener('popstate', existingHandler);
  }

  initialized = true;
  ensureGuardEntry();
  window.addEventListener('popstate', handlePopState);
  windowWithHandler[HANDLER_WINDOW_KEY] = handlePopState;
}

export function registerBackButtonLayer(close: BackButtonCloseHandler): () => void {
  initBackButtonGuard();

  const layer: BackButtonLayer = {
    id: nextLayerId++,
    close,
  };

  layers.push(layer);

  return () => {
    const index = layers.findIndex((entry) => entry.id === layer.id);
    if (index >= 0) {
      layers.splice(index, 1);
    }
  };
}

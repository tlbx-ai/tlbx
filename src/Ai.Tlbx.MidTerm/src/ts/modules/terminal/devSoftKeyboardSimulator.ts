import { autoResizeAllTerminalsImmediate } from './scaling';
import { setMobileVerticalStability } from './mobileVerticalStability';

const DEFAULT_KEYBOARD_RATIO = 0.42;
const MIN_KEYBOARD_HEIGHT_PX = 220;
const MAX_KEYBOARD_HEIGHT_PX = 360;
const PREVIEW_TAB_CHANGED_EVENT = 'midterm:web-preview-active-tab-changed';

let active = false;
let keyboardHeight = 0;
const fallbackPreviewState = new Map<string, { active: boolean; height: number }>();

declare global {
  interface Window {
    mtDevSoftKeyboard?: {
      show: (height?: number) => void;
      hide: () => void;
      toggle: () => void;
      isActive: () => boolean;
    };
  }
}

export function initDevSoftKeyboardSimulator(): void {
  const button = document.getElementById('dev-soft-keyboard-toggle') as HTMLButtonElement | null;
  const keyboard = document.getElementById('dev-soft-keyboard');
  if (!button || !keyboard) return;

  button.addEventListener('click', () => {
    const activePreview = getActivePreview();
    const previewKeyboard = activePreview?.keyboard ?? null;
    if (previewKeyboard) {
      previewKeyboard.toggle();
      button.setAttribute('aria-pressed', String(previewKeyboard.isActive()));
      return;
    }

    const frameKey = activePreview?.frameKey;
    if (frameKey) {
      const state = fallbackPreviewState.get(frameKey);
      if (state?.active) {
        hideContainedPreviewKeyboard(frameKey);
      } else {
        showContainedPreviewKeyboard(frameKey);
      }
      return;
    }

    if (active && document.body.classList.contains('dev-soft-keyboard-active')) {
      hideDevSoftKeyboard();
    } else {
      showDevSoftKeyboard();
    }
  });

  window.addEventListener(PREVIEW_TAB_CHANGED_EVENT, syncToolbarButtonToActivePreview);

  window.addEventListener('resize', () => {
    const activeFallbackFrameKey = getActiveFallbackFrameKey();
    if (activeFallbackFrameKey) {
      showContainedPreviewKeyboard(activeFallbackFrameKey, keyboardHeight || undefined);
      return;
    }

    if (active && document.body.classList.contains('dev-soft-keyboard-active')) {
      showDevSoftKeyboard(keyboardHeight);
    }
  });

  window.mtDevSoftKeyboard = {
    show: showDevSoftKeyboard,
    hide: hideDevSoftKeyboard,
    toggle: () => {
      if (active) {
        hideDevSoftKeyboard();
      } else {
        showDevSoftKeyboard();
      }
    },
    isActive: () => active,
  };
}

function getActivePreview(): {
  frameKey: string | null;
  keyboard: Window['mtDevSoftKeyboard'] | null;
} | null {
  const iframe = document.querySelector<HTMLIFrameElement>('.web-preview-iframe:not(.hidden)');
  if (!iframe) {
    return null;
  }

  const frameKey = iframe.dataset.previewFrameKey || null;
  if (!iframe.contentWindow) {
    return { frameKey, keyboard: null };
  }

  try {
    return { frameKey, keyboard: iframe.contentWindow.mtDevSoftKeyboard ?? null };
  } catch {
    return { frameKey, keyboard: null };
  }
}

function showContainedPreviewKeyboard(frameKey: string, height = calculateKeyboardHeight()): void {
  const keyboard = document.getElementById('dev-soft-keyboard');
  const button = document.getElementById('dev-soft-keyboard-toggle') as HTMLButtonElement | null;
  const previewBody = document.querySelector<HTMLElement>('.web-preview-dock-body');

  if (!keyboard || !previewBody) {
    showDevSoftKeyboard(height);
    return;
  }

  active = true;
  keyboardHeight = height;
  fallbackPreviewState.set(frameKey, { active: true, height });
  document.documentElement.style.setProperty('--midterm-dev-soft-keyboard-height', `${height}px`);
  document.body.classList.add('dev-soft-keyboard-preview-fallback');
  previewBody.appendChild(keyboard);
  keyboard.hidden = false;
  keyboard.setAttribute('aria-hidden', 'false');
  if (button) {
    button.setAttribute('aria-pressed', 'true');
  }
}

function hideContainedPreviewKeyboard(frameKey: string): void {
  const button = document.getElementById('dev-soft-keyboard-toggle') as HTMLButtonElement | null;
  const keyboard = document.getElementById('dev-soft-keyboard');

  fallbackPreviewState.set(frameKey, { active: false, height: 0 });
  active = hasActiveFallbackPreview();
  keyboardHeight = 0;
  document.documentElement.style.removeProperty('--midterm-dev-soft-keyboard-height');
  document.body.classList.remove('dev-soft-keyboard-preview-fallback');
  if (keyboard) {
    keyboard.hidden = true;
    keyboard.setAttribute('aria-hidden', 'true');
  }
  if (button) {
    button.setAttribute('aria-pressed', 'false');
  }
}

function syncToolbarButtonToActivePreview(): void {
  const button = document.getElementById('dev-soft-keyboard-toggle') as HTMLButtonElement | null;
  const activePreview = getActivePreview();
  const previewKeyboardActive = activePreview?.keyboard?.isActive() ?? false;
  const fallbackActive = syncContainedKeyboardToActivePreview(activePreview, previewKeyboardActive);

  if (button) {
    button.setAttribute('aria-pressed', String(previewKeyboardActive || fallbackActive));
  }
}

function syncContainedKeyboardToActivePreview(
  activePreview: ReturnType<typeof getActivePreview>,
  previewKeyboardActive: boolean,
): boolean {
  const fallbackState = activePreview?.frameKey
    ? fallbackPreviewState.get(activePreview.frameKey)
    : null;
  const fallbackActive = fallbackState?.active ?? false;

  if (fallbackActive && activePreview?.frameKey) {
    showContainedPreviewKeyboard(activePreview.frameKey, fallbackState?.height || undefined);
    return true;
  }

  if (!previewKeyboardActive) {
    hideInactiveContainedKeyboard();
  }

  return false;
}

function hideInactiveContainedKeyboard(): void {
  const keyboard = document.getElementById('dev-soft-keyboard');
  document.body.classList.remove('dev-soft-keyboard-preview-fallback');
  document.documentElement.style.removeProperty('--midterm-dev-soft-keyboard-height');
  if (keyboard?.parentElement?.classList.contains('web-preview-dock-body')) {
    keyboard.hidden = true;
    keyboard.setAttribute('aria-hidden', 'true');
  }
}

function getActiveFallbackFrameKey(): string | null {
  const activePreview = getActivePreview();
  if (!activePreview?.frameKey) {
    return null;
  }

  return fallbackPreviewState.get(activePreview.frameKey)?.active ? activePreview.frameKey : null;
}

function hasActiveFallbackPreview(): boolean {
  for (const state of fallbackPreviewState.values()) {
    if (state.active) {
      return true;
    }
  }
  return false;
}

export function showDevSoftKeyboard(height = calculateKeyboardHeight()): void {
  const appEl = document.querySelector<HTMLElement>('.terminal-page');
  const button = document.getElementById('dev-soft-keyboard-toggle') as HTMLButtonElement | null;
  const keyboard = document.getElementById('dev-soft-keyboard');
  const viewportHeight = Math.max(240, window.innerHeight - height);

  active = true;
  keyboardHeight = height;
  document.documentElement.style.setProperty('--midterm-dev-soft-keyboard-height', `${height}px`);
  document.documentElement.style.setProperty(
    '--midterm-visual-viewport-height',
    `${viewportHeight}px`,
  );
  document.documentElement.style.setProperty('--midterm-visual-viewport-offset-top', '0px');
  document.documentElement.style.setProperty('--midterm-soft-keyboard-height', `${height}px`);
  document.documentElement.style.height = `${viewportHeight}px`;
  document.documentElement.style.maxHeight = `${viewportHeight}px`;
  document.body.style.height = `${viewportHeight}px`;
  document.body.style.maxHeight = `${viewportHeight}px`;
  document.body.classList.add(
    'keyboard-visible',
    'mobile-terminal-vertical-stable',
    'dev-soft-keyboard-active',
  );

  if (appEl) {
    appEl.style.top = '0px';
    appEl.style.bottom = 'auto';
    appEl.style.height = `${viewportHeight}px`;
    appEl.style.maxHeight = `${viewportHeight}px`;
  }
  if (keyboard) {
    keyboard.hidden = false;
    keyboard.setAttribute('aria-hidden', 'false');
  }
  if (button) {
    button.setAttribute('aria-pressed', 'true');
  }

  setMobileVerticalStability(true);
  dispatchViewportSimulationChange();
}

export function hideDevSoftKeyboard(): void {
  const appEl = document.querySelector<HTMLElement>('.terminal-page');
  const button = document.getElementById('dev-soft-keyboard-toggle') as HTMLButtonElement | null;
  const keyboard = document.getElementById('dev-soft-keyboard');

  active = false;
  keyboardHeight = 0;
  for (const [frameKey, state] of fallbackPreviewState) {
    if (state.active) {
      fallbackPreviewState.set(frameKey, { active: false, height: 0 });
    }
  }
  document.documentElement.style.removeProperty('--midterm-dev-soft-keyboard-height');
  document.documentElement.style.removeProperty('--midterm-visual-viewport-height');
  document.documentElement.style.removeProperty('--midterm-visual-viewport-offset-top');
  document.documentElement.style.removeProperty('--midterm-soft-keyboard-height');
  document.documentElement.style.height = '';
  document.documentElement.style.maxHeight = '';
  document.body.style.height = '';
  document.body.style.maxHeight = '';
  document.body.classList.remove(
    'keyboard-visible',
    'mobile-terminal-vertical-stable',
    'dev-soft-keyboard-active',
    'dev-soft-keyboard-preview-fallback',
  );

  if (appEl) {
    appEl.style.top = '';
    appEl.style.bottom = '';
    appEl.style.height = '';
    appEl.style.maxHeight = '';
  }
  if (keyboard) {
    keyboard.hidden = true;
    keyboard.setAttribute('aria-hidden', 'true');
  }
  if (button) {
    button.setAttribute('aria-pressed', 'false');
  }

  setMobileVerticalStability(false);
  dispatchViewportSimulationChange();
}

function calculateKeyboardHeight(): number {
  return Math.round(
    Math.min(
      MAX_KEYBOARD_HEIGHT_PX,
      Math.max(MIN_KEYBOARD_HEIGHT_PX, window.innerHeight * DEFAULT_KEYBOARD_RATIO),
    ),
  );
}

function dispatchViewportSimulationChange(): void {
  window.dispatchEvent(new Event('midterm:visual-viewport-changed'));
  autoResizeAllTerminalsImmediate();
}

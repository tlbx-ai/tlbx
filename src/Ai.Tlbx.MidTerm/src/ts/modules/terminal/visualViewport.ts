import { autoResizeAllTerminalsImmediate } from './scaling';
import {
  isMobileTerminalViewport,
  observeMobileVerticalViewportChange,
  syncMobileVerticalStableTerminals,
} from './mobileVerticalStability';
import { isMobilePresentationContext } from '../theming/backgroundVisibility';

const KEYBOARD_RATIO_THRESHOLD = 0.88;
const KEYBOARD_PIXEL_THRESHOLD = 120;
const KEYBOARD_BOTTOM_GUARD_MIN_PX = 10;
const KEYBOARD_BOTTOM_GUARD_MAX_PX = 24;
const KEYBOARD_BOTTOM_GUARD_RATIO = 0.035;
const LAYOUT_VISUAL_VIEWPORT_HEIGHT_TOLERANCE_PX = 2;

function getVisualViewportShellTop(visualViewport: VisualViewport): number {
  // Chromium with interactive-widget=resizes-content already moves the layout
  // boundary above the keyboard. Its visual viewport may still emit transient
  // offsetTop values while the focused textarea is edited; following those
  // values would move the entire app on every keystroke. Browsers that keep the
  // larger layout viewport (notably iOS) still need the visual offset fallback.
  const layoutViewportTracksVisualViewport =
    Math.abs(window.innerHeight - visualViewport.height) <=
    LAYOUT_VISUAL_VIEWPORT_HEIGHT_TOLERANCE_PX;
  return layoutViewportTracksVisualViewport ? 0 : Math.max(0, visualViewport.offsetTop);
}

function hasEditableElementFocus(): boolean {
  const activeElement = document.activeElement as {
    tagName?: string | null;
    isContentEditable?: boolean | null;
  } | null;
  if (!activeElement || typeof activeElement.tagName !== 'string') {
    return false;
  }

  const tagName = activeElement.tagName.toUpperCase();
  return (
    tagName === 'INPUT' ||
    tagName === 'TEXTAREA' ||
    tagName === 'SELECT' ||
    activeElement.isContentEditable === true
  );
}

function applyVisualViewportShellGeometry(
  visualViewport: VisualViewport,
  viewportHeight: number,
  appEl: HTMLElement | null,
): void {
  const viewportTop = getVisualViewportShellTop(visualViewport);
  if (appEl) {
    appEl.style.top = `${viewportTop}px`;
    appEl.style.bottom = 'auto';
    appEl.style.height = `${viewportHeight}px`;
    appEl.style.maxHeight = `${viewportHeight}px`;
  }

  // Lock root/body to visual viewport height to prevent dragging hidden
  // off-screen space (common when soft keyboard is open in mobile PWAs).
  document.documentElement.style.height = `${viewportHeight}px`;
  document.documentElement.style.maxHeight = `${viewportHeight}px`;
  document.documentElement.style.setProperty(
    '--midterm-visual-viewport-height',
    `${viewportHeight}px`,
  );
  document.documentElement.style.setProperty(
    '--midterm-visual-viewport-offset-top',
    `${viewportTop}px`,
  );
  document.body.style.height = `${viewportHeight}px`;
  document.body.style.maxHeight = `${viewportHeight}px`;

  if (viewportTop !== 0 && !hasEditableElementFocus()) {
    window.scrollTo(0, 0);
  }
}

function clearVisualViewportShellGeometry(appEl: HTMLElement | null): void {
  if (appEl) {
    appEl.style.removeProperty('top');
    appEl.style.removeProperty('bottom');
    appEl.style.removeProperty('height');
    appEl.style.removeProperty('max-height');
  }

  document.documentElement.style.removeProperty('height');
  document.documentElement.style.removeProperty('max-height');
  document.documentElement.style.removeProperty('--midterm-visual-viewport-height');
  document.documentElement.style.removeProperty('--midterm-visual-viewport-offset-top');
  document.documentElement.style.removeProperty('--midterm-soft-keyboard-height');
  document.documentElement.style.removeProperty('--midterm-soft-keyboard-bottom-guard');
  document.body.style.removeProperty('height');
  document.body.style.removeProperty('max-height');
  document.body.classList.toggle('keyboard-visible', false);
}

function shouldConstrainShellToVisualViewport(visualViewport: VisualViewport): boolean {
  return (
    isMobilePresentationContext() ||
    isMobileTerminalViewport(Math.round(visualViewport.width || window.innerWidth))
  );
}

function isSoftKeyboardVisible(viewportHeight: number, baselineHeight: number): boolean {
  const heightDrop = baselineHeight - viewportHeight;
  return (
    viewportHeight < baselineHeight * KEYBOARD_RATIO_THRESHOLD &&
    heightDrop >= KEYBOARD_PIXEL_THRESHOLD
  );
}

function getSoftKeyboardBottomGuard(viewportHeight: number, baselineHeight: number): number {
  if (!isSoftKeyboardVisible(viewportHeight, baselineHeight)) {
    return 0;
  }

  return Math.round(
    Math.min(
      KEYBOARD_BOTTOM_GUARD_MAX_PX,
      Math.max(KEYBOARD_BOTTOM_GUARD_MIN_PX, viewportHeight * KEYBOARD_BOTTOM_GUARD_RATIO),
    ),
  );
}

function syncSoftKeyboardState(viewportHeight: number, baselineHeight: number): boolean {
  const heightDrop = baselineHeight - viewportHeight;
  document.documentElement.style.setProperty(
    '--midterm-soft-keyboard-height',
    `${Math.max(0, heightDrop)}px`,
  );
  const kbVisible = isSoftKeyboardVisible(viewportHeight, baselineHeight);
  if (kbVisible !== document.body.classList.contains('keyboard-visible')) {
    document.body.classList.toggle('keyboard-visible', kbVisible);
  }
  return kbVisible;
}

/**
 * Set up visual viewport handling for mobile keyboard appearance.
 * Constrains the .terminal-page height to the visual viewport so the entire
 * flex layout (topbar, terminals, touch controller) fits above the keyboard.
 * Also toggles a 'keyboard-visible' class on body to hide UI chrome.
 */
export function setupVisualViewport(): void {
  if (!window.visualViewport) return;

  const vv = window.visualViewport;
  let lastHeight = 0;
  let lastTop = -1;
  let lastWidth = 0;
  let baselineHeight = Math.max(window.innerHeight, vv.height);
  const appEl = document.querySelector<HTMLElement>('.terminal-page');

  const update = () => {
    const rawViewportHeight = vv.height;
    if (rawViewportHeight > baselineHeight) {
      baselineHeight = rawViewportHeight;
    }
    const constrainShell = shouldConstrainShellToVisualViewport(vv);
    const bottomGuard = constrainShell
      ? getSoftKeyboardBottomGuard(rawViewportHeight, baselineHeight)
      : 0;
    const vh = Math.max(1, rawViewportHeight - bottomGuard);
    const viewportTop = getVisualViewportShellTop(vv);
    const viewportWidth = Math.max(1, vv.width || window.innerWidth);
    const keyboardVisible = isSoftKeyboardVisible(rawViewportHeight, baselineHeight);
    const heightAndWidthStable =
      Math.abs(vh - lastHeight) < 1 && Math.abs(viewportWidth - lastWidth) < 1;
    if (keyboardVisible && hasEditableElementFocus() && heightAndWidthStable) {
      // Mobile browsers can pan the focused xterm/prompt textarea on every
      // character. The visible boundary did not change, so following that
      // offset would move the whole shell and repeat terminal synchronization.
      return;
    }
    if (
      Math.abs(vh - lastHeight) < 1 &&
      Math.abs(viewportTop - lastTop) < 1 &&
      Math.abs(viewportWidth - lastWidth) < 1
    )
      return;
    lastHeight = vh;
    lastTop = viewportTop;
    lastWidth = viewportWidth;

    if (constrainShell) {
      applyVisualViewportShellGeometry(vv, vh, appEl);
      syncSoftKeyboardState(rawViewportHeight, baselineHeight);
      document.documentElement.style.setProperty(
        '--midterm-soft-keyboard-bottom-guard',
        `${bottomGuard}px`,
      );
    } else {
      clearVisualViewportShellGeometry(appEl);
    }

    if (typeof Reflect.get(window, 'dispatchEvent') === 'function')
      window.dispatchEvent(new Event('midterm:visual-viewport-changed'));

    const mobileVerticalOnlyChange = observeMobileVerticalViewportChange();
    if (mobileVerticalOnlyChange) {
      syncMobileVerticalStableTerminals();
      return;
    }

    autoResizeAllTerminalsImmediate();
  };

  vv.addEventListener('resize', update);
  vv.addEventListener('scroll', update);
  update();
}

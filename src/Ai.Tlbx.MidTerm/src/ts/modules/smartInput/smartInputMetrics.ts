const COLLAPSED_TEXTAREA_LINES = 1;
const MAX_TEXTAREA_OVERLAY_LINES = 7;
const MAX_VISIBLE_TEXTAREA_LINES = COLLAPSED_TEXTAREA_LINES + MAX_TEXTAREA_OVERLAY_LINES;
const MOBILE_BREAKPOINT_PX = 768;
const COLLAPSED_HEIGHT_DATASET_KEY = 'midtermCollapsedHeightPx';
const SINGLE_LINE_VERTICAL_OPTICAL_OFFSET_PX = 3;

interface ResizeSmartInputTextareaOptions {
  preserveScrollTop?: number | null;
}

// eslint-disable-next-line complexity -- autoresize must reconcile min/max height, overflow mode, and preserved internal scroll state without snapping the prompt viewport.
export function resizeSmartInputTextarea(
  textarea: HTMLTextAreaElement,
  options: ResizeSmartInputTextareaOptions = {},
): void {
  const expandedMinHeight = resolveExpandedComposerTextareaMinHeight(textarea);
  const preserveScrollTop = Number.isFinite(options.preserveScrollTop ?? Number.NaN)
    ? Math.max(0, options.preserveScrollTop ?? 0)
    : Math.max(0, textarea.scrollTop);
  textarea.style.removeProperty('--smart-input-textarea-padding-top');
  textarea.style.removeProperty('--smart-input-textarea-padding-bottom');
  textarea.style.minHeight = '';
  textarea.style.height = 'auto';

  const computedStyle = getComputedStyle(textarea);
  const lineHeight = Number.parseFloat(computedStyle.lineHeight);
  const fallbackFontSize = Number.parseFloat(computedStyle.fontSize) || 16;
  const effectiveLineHeight = Number.isFinite(lineHeight) ? lineHeight : fallbackFontSize * 1.2;
  const paddingTop = Number.parseFloat(computedStyle.paddingTop) || 0;
  const paddingBottom = Number.parseFloat(computedStyle.paddingBottom) || 0;
  const basePaddingY = (paddingTop + paddingBottom) / 2;
  const borderTop = Number.parseFloat(computedStyle.borderTopWidth) || 0;
  const borderBottom = Number.parseFloat(computedStyle.borderBottomWidth) || 0;
  const minHeight = Math.max(Number.parseFloat(computedStyle.minHeight) || 0, expandedMinHeight);
  const maxHeight =
    effectiveLineHeight * MAX_VISIBLE_TEXTAREA_LINES +
    paddingTop +
    paddingBottom +
    borderTop +
    borderBottom;
  const contentHeight = textarea.scrollHeight + borderTop + borderBottom;
  const nextHeight = Math.max(minHeight, Math.min(contentHeight, maxHeight));

  if (!(COLLAPSED_HEIGHT_DATASET_KEY in textarea.dataset) && minHeight > 0) {
    textarea.dataset[COLLAPSED_HEIGHT_DATASET_KEY] = String(minHeight);
  }

  textarea.style.setProperty('--smart-input-textarea-rendered-height', `${String(nextHeight)}px`);
  const collapsedContentHeight = Math.max(0, minHeight - borderTop - borderBottom);
  const isSingleLinePrompt =
    !textarea.value.includes('\n') && textarea.scrollHeight <= collapsedContentHeight + 2;
  const opticalOffset = isSingleLinePrompt
    ? Math.min(SINGLE_LINE_VERTICAL_OPTICAL_OFFSET_PX, basePaddingY)
    : 0;
  textarea.style.setProperty(
    '--smart-input-textarea-padding-top',
    `${String(basePaddingY + opticalOffset)}px`,
  );
  textarea.style.setProperty(
    '--smart-input-textarea-padding-bottom',
    `${String(Math.max(0, basePaddingY - opticalOffset))}px`,
  );
  textarea.style.height = `${String(nextHeight)}px`;
  textarea.style.minHeight = `${String(nextHeight)}px`;
  const isOverflowing = textarea.scrollHeight > maxHeight;
  textarea.style.overflowY = isOverflowing ? 'auto' : 'hidden';

  if (!isOverflowing) {
    textarea.scrollTop = 0;
    return;
  }

  const viewportHeight =
    Number.isFinite(textarea.clientHeight) && textarea.clientHeight > 0
      ? textarea.clientHeight
      : nextHeight;
  const maxScrollTop = Math.max(0, textarea.scrollHeight - viewportHeight);
  textarea.scrollTop = Math.min(preserveScrollTop, maxScrollTop);
}

function resolveExpandedComposerTextareaMinHeight(textarea: HTMLTextAreaElement): number {
  if (typeof textarea.closest !== 'function') {
    return 0;
  }

  const expandedDock = textarea.closest('.adaptive-footer-dock[data-composer-expanded="true"]');
  if (!(expandedDock instanceof HTMLElement)) {
    return 0;
  }

  const shell = textarea.parentElement;
  if (!(shell instanceof HTMLElement)) {
    return 0;
  }

  return Math.max(0, Math.round(shell.getBoundingClientRect().height));
}

export function getCollapsedSmartInputTextareaHeight(textarea: HTMLTextAreaElement): number {
  const cachedCollapsedHeight = Number.parseFloat(
    textarea.dataset[COLLAPSED_HEIGHT_DATASET_KEY] ?? '',
  );
  if (Number.isFinite(cachedCollapsedHeight) && cachedCollapsedHeight > 0) {
    return cachedCollapsedHeight;
  }

  const computedStyle = getComputedStyle(textarea);
  const configuredMinHeight = Number.parseFloat(computedStyle.minHeight);
  if (Number.isFinite(configuredMinHeight) && configuredMinHeight > 0) {
    return configuredMinHeight;
  }

  const lineHeight = Number.parseFloat(computedStyle.lineHeight);
  const fallbackFontSize = Number.parseFloat(computedStyle.fontSize) || 16;
  const effectiveLineHeight = Number.isFinite(lineHeight) ? lineHeight : fallbackFontSize * 1.2;
  const paddingTop = Number.parseFloat(computedStyle.paddingTop) || 0;
  const paddingBottom = Number.parseFloat(computedStyle.paddingBottom) || 0;
  const borderTop = Number.parseFloat(computedStyle.borderTopWidth) || 0;
  const borderBottom = Number.parseFloat(computedStyle.borderBottomWidth) || 0;

  return effectiveLineHeight + paddingTop + paddingBottom + borderTop + borderBottom;
}

export function isMobileViewport(): boolean {
  return window.matchMedia(`(max-width: ${String(MOBILE_BREAKPOINT_PX)}px)`).matches;
}

export function isTouchPrimaryDevice(): boolean {
  return !matchMedia('(hover: hover) and (pointer: fine)').matches;
}

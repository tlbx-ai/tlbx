import type { MidTermSettingsPublic } from '../../types';
import { MOBILE_BREAKPOINT } from '../../constants';

export function isMobileBackgroundSuppressed(
  _settings: Pick<MidTermSettingsPublic, 'hideBackgroundImageOnMobile'> | null | undefined,
): boolean {
  return isMobilePresentationContext();
}

export function shouldRenderBackgroundImage(
  settings:
    | Pick<
        MidTermSettingsPublic,
        | 'backgroundImageEnabled'
        | 'backgroundImageFileName'
        | 'backgroundImageRevision'
        | 'hideBackgroundImageOnMobile'
      >
    | null
    | undefined,
): boolean {
  return Boolean(
    settings?.backgroundImageEnabled &&
    settings.backgroundImageFileName &&
    settings.backgroundImageRevision > 0 &&
    !isMobileBackgroundSuppressed(settings),
  );
}

export function isMobilePresentationContext(): boolean {
  if (typeof window === 'undefined') {
    return false;
  }

  const hasTouchScreen = typeof navigator !== 'undefined' && navigator.maxTouchPoints > 0;
  const hasCoarsePointer =
    typeof window.matchMedia === 'function' && window.matchMedia('(pointer: coarse)').matches;
  const compactTouchViewport =
    hasTouchScreen &&
    typeof window.matchMedia === 'function' &&
    window.matchMedia(`(max-width: ${MOBILE_BREAKPOINT}px)`).matches;

  return hasCoarsePointer || compactTouchViewport;
}

import type { Session } from '../../types';

const PIN_SUCCESS_ANIMATION_MS = 560;

export function getBookmarkSurfaceType(
  session: Session,
  profile: 'codex' | 'claude' | 'grok' | null,
): 'trm' | 'cdx' | 'cld' | 'grk' {
  if (session.appServerControlOnly && profile === 'claude') {
    return 'cld';
  }

  if (session.appServerControlOnly && profile === 'codex') {
    return 'cdx';
  }

  if (session.appServerControlOnly && profile === 'grok') {
    return 'grk';
  }

  return 'trm';
}

export function animateBookmarkSaveSuccess(sessionId: string): void {
  const pinButtons = document.querySelectorAll<HTMLButtonElement>(
    `.session-item[data-session-id="${sessionId}"] .session-pin`,
  );
  for (const pinButton of pinButtons) {
    pinButton.classList.remove('save-success');
    void pinButton.offsetWidth;
    pinButton.classList.add('save-success');
    window.setTimeout(() => {
      pinButton.classList.remove('save-success');
    }, PIN_SUCCESS_ANIMATION_MS);
  }
}

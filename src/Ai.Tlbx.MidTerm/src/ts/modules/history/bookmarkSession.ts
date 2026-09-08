import type { Session } from '../../types';

const PIN_SUCCESS_ANIMATION_MS = 560;

export function getBookmarkSurfaceType(
  session: Session,
  profile: string | null,
): 'trm' | 'cdx' | 'acp' {
  if (session.appServerControlOnly && profile === 'codex') {
    return 'cdx';
  }

  if (session.appServerControlOnly && profile) {
    return 'acp';
  }

  return 'trm';
}

export function animateBookmarkSaveSuccess(sessionId: string): void {
  const pinButtons = document.querySelectorAll<HTMLButtonElement>(
    `.session-item[data-session-id="${sessionId}"] .session-pin`,
  );
  for (const pinButton of pinButtons) {
    pinButton.classList.remove('save-success');
    // eslint-disable-next-line @typescript-eslint/no-meaningless-void-operator -- Reading the layout property intentionally forces synchronous layout.
    void pinButton.offsetWidth;
    pinButton.classList.add('save-success');
    window.setTimeout(() => {
      pinButton.classList.remove('save-success');
    }, PIN_SUCCESS_ANIMATION_MS);
  }
}

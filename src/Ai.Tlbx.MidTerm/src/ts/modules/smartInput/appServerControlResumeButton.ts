import { getSession } from '../../stores';
import { isHubSessionId } from '../hub/runtime';
import { t } from '../i18n';
import type { ResumeProvider } from '../providerResume';

type AppServerControlResumeConversationHandler = (args: {
  sessionId: string;
  provider: ResumeProvider;
  workingDirectory: string;
}) => void | Promise<void>;

export function createAppServerControlResumeButton(
  sessionId: string,
  appServerControlResumeConversationHandler: AppServerControlResumeConversationHandler | null,
): HTMLButtonElement | null {
  const session = getSession(sessionId);
  const provider = normalizeResumeProvider(session?.profileHint);
  const workingDirectory = session?.workspacePath?.trim() || session?.currentDirectory?.trim();
  if (
    isHubSessionId(sessionId) ||
    !provider ||
    !workingDirectory ||
    !session?.appServerControlOnly ||
    !appServerControlResumeConversationHandler
  ) {
    return null;
  }

  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'smart-input-appServerControl-action smart-input-appServerControl-resume';
  button.textContent = t('smartInput.resume');
  button.title = `${t('smartInput.resumeConversation')} ${
    provider === 'claude' ? t('sessionLauncher.claudeTitle') : t('sessionLauncher.codexTitle')
  }`;
  button.setAttribute('aria-label', button.title);
  button.addEventListener('click', () => {
    void appServerControlResumeConversationHandler({
      sessionId,
      provider,
      workingDirectory,
    });
  });
  return button;
}

function normalizeResumeProvider(profile: string | null | undefined): ResumeProvider | null {
  if (profile === 'claude') {
    return 'claude';
  }

  if (profile === 'codex') {
    return 'codex';
  }

  return null;
}

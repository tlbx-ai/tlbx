import { getSession } from '../../stores';
import { isHubSessionId } from '../hub/runtime';
import { t } from '../i18n';
import type { ResumeProvider } from '../providerResume';

export interface AppServerControlResumeConversationArgs {
  sessionId: string;
  provider: ResumeProvider;
  workingDirectory: string;
}

type AppServerControlResumeConversationHandler = (
  args: AppServerControlResumeConversationArgs,
) => void | Promise<void>;

export function getAppServerControlResumeArgs(
  sessionId: string,
): AppServerControlResumeConversationArgs | null {
  const session = getSession(sessionId);
  const provider = normalizeResumeProvider(session?.profileHint);
  const workingDirectory = session?.workspacePath?.trim() || session?.currentDirectory?.trim();
  return !isHubSessionId(sessionId) && provider && workingDirectory && session?.appServerControlOnly
    ? { sessionId, provider, workingDirectory }
    : null;
}

export function createAppServerControlResumeButton(
  args: AppServerControlResumeConversationArgs,
  handler: AppServerControlResumeConversationHandler,
): HTMLButtonElement {
  const button = document.createElement('button');
  button.type = 'button';
  button.className = 'smart-input-appServerControl-action smart-input-appServerControl-resume';
  button.textContent = t('smartInput.resume');
  button.title = `${t('smartInput.resumeConversation')} ${
    args.provider === 'claude' ? t('sessionLauncher.claudeTitle') : t('sessionLauncher.codexTitle')
  }`;
  button.setAttribute('aria-label', button.title);
  button.addEventListener('click', () => {
    void handler(args);
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

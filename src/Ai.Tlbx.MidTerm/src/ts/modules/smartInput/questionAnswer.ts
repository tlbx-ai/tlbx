import { t } from '../i18n';

/** A question answer uses the real prompt box, including its upload/paste pipeline. */
export interface QuestionAnswerTarget {
  requestId: string;
  questionId: string;
  question: string;
  isCurrent: () => boolean;
  answer: (text: string) => Promise<void>;
}
const targets = new Map<string, QuestionAnswerTarget>();
const changed = 'tlbx:question-answer-changed';

export function beginQuestionAnswer(sessionId: string, target: QuestionAnswerTarget): void {
  targets.set(sessionId, target);
  window.dispatchEvent(new CustomEvent(changed, { detail: sessionId }));
}
export function getQuestionAnswer(sessionId: string): QuestionAnswerTarget | undefined {
  return targets.get(sessionId);
}
export function endQuestionAnswer(sessionId: string): void {
  if (targets.delete(sessionId))
    window.dispatchEvent(new CustomEvent(changed, { detail: sessionId }));
}
export function syncQuestionAnswer(sessionId: string): void {
  const target = targets.get(sessionId);
  if (target) window.dispatchEvent(new CustomEvent(changed));
}
export function initializeQuestionAnswerBox(args: {
  host: HTMLElement;
  activeSession: () => string | null;
  focus: () => void;
}): () => void {
  const banner = document.createElement('div');
  banner.className = 'smart-input-question-answer';
  banner.hidden = true;
  const label = document.createElement('span');
  label.setAttribute('role', 'status');
  const cancel = document.createElement('button');
  cancel.type = 'button';
  cancel.className = 'agent-view-btn';
  cancel.textContent = t('dialog.cancel');
  cancel.addEventListener('click', () => {
    const id = args.activeSession();
    if (id) endQuestionAnswer(id);
  });
  banner.append(label, cancel);
  args.host.prepend(banner);
  const sync = (): void => {
    const id = args.activeSession();
    const target = id ? targets.get(id) : undefined;
    banner.hidden = !target;
    for (const form of document.querySelectorAll<HTMLElement>('[data-question-request-id]'))
      form.dataset.questionAnswerActive = String(
        form.dataset.questionRequestId === target?.requestId,
      );
    label.textContent = target
      ? `${target.isCurrent() ? t('appServerControl.request.answerInPrompt') + ': ' : t('appServerControl.request.noLongerOpen') + ' '}${target.question}`
      : '';
  };
  window.addEventListener(changed, (event) => {
    sync();
    if ((event as CustomEvent<string>).detail === args.activeSession()) args.focus();
  });
  sync();
  return sync;
}

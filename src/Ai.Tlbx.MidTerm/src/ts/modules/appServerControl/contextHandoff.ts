import { escapeHtml } from '../../utils/dom';
import { t } from '../i18n';
import { registerBackButtonLayer } from '../navigation/backButtonGuard';

const providers = [
  { id: 'codex', label: 'Codex' },
  { id: 'antigravity', label: 'Antigravity' },
  { id: 'claude', label: 'Claude' },
] as const;

type HandoffProvider = (typeof providers)[number]['id'];
export type HandoffTarget = { provider: HandoffProvider; terminal: boolean; draft: string };

let activeDialog: HTMLDialogElement | null = null;

export function openContextHandoff(
  sessionId: string,
  sourceProvider: string | null | undefined,
  launch: (target: HandoffTarget) => Promise<void>,
): void {
  if (activeDialog) {
    activeDialog.focus();
    return;
  }

  const dialog = document.createElement('dialog');
  dialog.className = 'agent-session-details context-handoff-dialog';
  dialog.setAttribute('aria-labelledby', 'context-handoff-title');
  const copy = t('contextHandoff.copy');
  dialog.innerHTML = `
    <header>
      <h2 id="context-handoff-title">${escapeHtml(t('contextHandoff.title'))}</h2>
      <button type="button" data-cancel class="agent-session-close" aria-label="${escapeHtml(t('dialog.cancel'))}">×</button>
    </header>
    <div class="agent-session-content context-handoff-fields">
      <div class="context-handoff-target">
        <label>${escapeHtml(t('contextHandoff.harness'))}
          <select data-provider autofocus>
            ${providers.map(({ id, label }) => `<option value="${id}">${label}</option>`).join('')}
          </select>
        </label>
        <label>${escapeHtml(t('contextHandoff.surface'))}
          <select data-surface><option value="acp">ACP</option><option value="terminal">Terminal</option></select>
        </label>
      </div>
      <label>${escapeHtml(t('contextHandoff.preview'))}
        <textarea data-draft rows="10" spellcheck="false" disabled></textarea>
      </label>
      <p data-hint></p>
      <p role="status" data-status></p>
    </div>
    <footer>
      <button type="button" data-copy hidden>${escapeHtml(copy)}</button>
      <button type="button" data-launch disabled>${escapeHtml(t('contextHandoff.openAcp'))}</button>
    </footer>
  `;

  const providerSelect = dialog.querySelector<HTMLSelectElement>('[data-provider]')!;
  const surface = dialog.querySelector<HTMLSelectElement>('[data-surface]')!;
  const draft = dialog.querySelector<HTMLTextAreaElement>('[data-draft]')!;
  const status = dialog.querySelector<HTMLParagraphElement>('[data-status]')!;
  const hint = dialog.querySelector<HTMLParagraphElement>('[data-hint]')!;
  const launchButton = dialog.querySelector<HTMLButtonElement>('[data-launch]')!;
  const copyButton = dialog.querySelector<HTMLButtonElement>('[data-copy]')!;
  const cancelButton = dialog.querySelector<HTMLButtonElement>('[data-cancel]')!;
  const abort = new AbortController();
  let launching = false;
  let copiedDraft: string | null = null;
  providerSelect.value = providers.find(({ id }) => id !== sourceProvider)?.id ?? 'codex';

  function sync(): void {
    const terminal = surface.value === 'terminal';
    const text = draft.value.trim();
    const copied = terminal && text !== '' && draft.value === copiedDraft;
    copyButton.hidden = !terminal;
    copyButton.disabled = launching || !text;
    launchButton.disabled = launching || !text;
    providerSelect.disabled = launching;
    surface.disabled = launching;
    draft.readOnly = launching;
    cancelButton.disabled = launching;
    launchButton.textContent = t(terminal ? 'contextHandoff.openTerminal' : 'contextHandoff.openAcp');
    hint.textContent = t(terminal ? 'contextHandoff.terminalHint' : 'contextHandoff.acpHint');
    copyButton.textContent = copied ? t('contextHandoff.copied') : copy;
  }

  const release = registerBackButtonLayer(() => {
    if (!launching) dialog.close();
  });
  dialog.addEventListener('close', () => {
    abort.abort();
    release();
    dialog.remove();
    activeDialog = null;
  }, { once: true });
  dialog.addEventListener('cancel', (event) => {
    if (launching) event.preventDefault();
  });
  cancelButton.addEventListener('click', () => dialog.close());
  for (const element of [draft, surface, providerSelect])
    element.addEventListener('input', sync);

  copyButton.addEventListener('click', () => {
    void copyContextDraft(draft).then((copied) => {
      copiedDraft = copied;
      status.textContent = copied === null ? t('contextHandoff.copyFallback') : '';
      sync();
    });
  });

  launchButton.addEventListener('click', () => {
    const provider = providers.find((item) => item.id === providerSelect.value);
    if (!provider || launching || !draft.value.trim()) return;
    launching = true;
    status.textContent = t('contextHandoff.opening');
    sync();
    void launch({ provider: provider.id, terminal: surface.value === 'terminal', draft: draft.value })
      .then(() => dialog.close())
      .catch((error: unknown) => {
        status.textContent = t('contextHandoff.openFailed').replace('{error}', () => String(error));
      })
      .finally(() => {
        launching = false;
        sync();
      });
  });

  activeDialog = dialog;
  document.body.append(dialog);
  dialog.showModal();
  sync();
  status.textContent = t('contextHandoff.loading');
  void fetch(`/api/sessions/${encodeURIComponent(sessionId)}/agent-control/handoff`, {
    signal: abort.signal,
  }).then(async (response) => {
    if (!response.ok) throw new Error(await readHandoffError(response));
    draft.value = await response.text();
    draft.disabled = false;
    status.textContent = '';
    sync();
  }).catch((error: unknown) => {
    if (!abort.signal.aborted)
      status.textContent = t('contextHandoff.loadFailed').replace('{error}', () => String(error));
  });
}

async function readHandoffError(response: Response): Promise<string> {
  const body = await response.text();
  if (!response.headers.get('content-type')?.includes('json')) return body || response.statusText;
  const payload: unknown = JSON.parse(body);
  return typeof payload === 'string' ? payload : response.statusText;
}

async function copyContextDraft(draft: HTMLTextAreaElement): Promise<string | null> {
  const text = draft.value;
  try {
    await navigator.clipboard.writeText(text);
    return text;
  } catch {
    draft.focus();
    draft.select();
    return null;
  }
}

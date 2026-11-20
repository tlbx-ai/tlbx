import { bootstrapWorker, waitForApiReachability } from '../../api/client';
import type { ShellType } from '../../api/types';
import { $currentSettings } from '../../stores';
import type { Session } from '../../types';
import { resolveLaunchDimensions } from '../terminal';
import { setSmartInputDraftText } from '../smartInput/smartInput';
import { escapeHtml } from '../../utils/dom';
import { t } from '../i18n';
import { registerBackButtonLayer } from '../navigation/backButtonGuard';

const providers = [
  { id: 'codex', label: 'Codex' },
  { id: 'antigravity', label: 'Antigravity' },
  { id: 'claude', label: 'Claude' },
] as const;

type HandoffProvider = (typeof providers)[number]['id'];
type HandoffTarget = { provider: HandoffProvider; terminal: boolean; draft: string };

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

  const providerSelect = getHandoffControl(dialog, '[data-provider]', HTMLSelectElement);
  const surface = getHandoffControl(dialog, '[data-surface]', HTMLSelectElement);
  const draft = getHandoffControl(dialog, '[data-draft]', HTMLTextAreaElement);
  const status = getHandoffControl(dialog, '[data-status]', HTMLParagraphElement);
  const hint = getHandoffControl(dialog, '[data-hint]', HTMLParagraphElement);
  const launchButton = getHandoffControl(dialog, '[data-launch]', HTMLButtonElement);
  const copyButton = getHandoffControl(dialog, '[data-copy]', HTMLButtonElement);
  const cancelButton = getHandoffControl(dialog, '[data-cancel]', HTMLButtonElement);
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
    launchButton.textContent = t(
      terminal ? 'contextHandoff.openTerminal' : 'contextHandoff.openAcp',
    );
    hint.textContent = t(terminal ? 'contextHandoff.terminalHint' : 'contextHandoff.acpHint');
    copyButton.textContent = copied ? t('contextHandoff.copied') : copy;
  }

  const release = registerBackButtonLayer(requestClose);
  function close(): void {
    abort.abort();
    release();
    dialog.close();
    dialog.remove();
    activeDialog = null;
  }

  function requestClose(): void {
    if (!launching) close();
  }

  dialog.addEventListener('cancel', (event) => {
    event.preventDefault();
    requestClose();
  });
  cancelButton.addEventListener('click', requestClose);
  for (const element of [draft, surface, providerSelect]) element.addEventListener('input', sync);

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
    void launch({
      provider: provider.id,
      terminal: surface.value === 'terminal',
      draft: draft.value,
    })
      .then(close)
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
  })
    .then(async (response) => {
      if (!response.ok) throw new Error(await readHandoffError(response));
      draft.value = await response.text();
      draft.disabled = false;
      status.textContent = '';
      sync();
    })
    .catch((error: unknown) => {
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

function getHandoffControl<T extends HTMLElement>(
  dialog: HTMLDialogElement,
  selector: string,
  tag: new () => T,
): T {
  const element = dialog.querySelector(selector);
  if (!(element instanceof tag)) throw new Error('Missing handoff control: ' + selector);
  return element;
}

export async function createContextHandoffSession(
  source: Session,
  target: HandoffTarget,
  shell: ShellType | null,
): Promise<Session> {
  const directory = source.currentDirectory;
  if (!directory) throw new Error(t('contextHandoff.missingDirectory'));

  const [dimensions] = await Promise.all([
    resolveLaunchDimensions($currentSettings.get(), 'launcher'),
    waitForApiReachability(),
  ]);
  const { data } = await bootstrapWorker({
    ...dimensions,
    shell,
    workingDirectory: directory,
    agentControlled: false,
    injectGuidance: false,
    profile: target.provider,
    appServerControlOnly: !target.terminal,
    launchCommand: target.terminal ? getHandoffTerminalCommand(target.provider, shell) : null,
    launchDelayMs: 0,
    slashCommands: [],
    slashCommandDelayMs: 0,
    spaceId: source.spaceId ?? null,
    workspacePath: source.workspacePath ?? null,
  });

  const session = data?.session;
  if (!session) throw new Error(t('sessionLauncher.createFailed'));
  if (!target.terminal) setSmartInputDraftText(session.id, target.draft);
  return session;
}

function getHandoffTerminalCommand(provider: HandoffProvider, shell: ShellType | null): string {
  if (provider !== 'antigravity') return provider;
  if (shell === 'Pwsh' || shell === 'PowerShell')
    return "if (Get-Command agy -ErrorAction SilentlyContinue) { agy } else { & (Join-Path $env:LOCALAPPDATA 'agy/bin/agy.exe') }";
  return 'agy';
}

import { bootstrapWorker } from '../../api/client';
import type { ShellType } from '../../api/types';
import { newlyCreatedSessions } from '../../state';
import { $currentSettings, setSession } from '../../stores';
import type { Session } from '../../types';
import { resolveLaunchDimensions } from '../terminal';
import { setSmartInputHandoffDraft } from '../smartInput/smartInput';
import { escapeHtml } from '../../utils/dom';
import { t } from '../i18n';
import { registerBackButtonLayer } from '../navigation/backButtonGuard';

const providers = [
  { id: 'codex', label: 'Codex' },
  { id: 'antigravity', label: 'Antigravity' },
  { id: 'claude', label: 'Claude' },
] as const;

type HandoffProvider = (typeof providers)[number]['id'];
type HandoffTarget = { provider: HandoffProvider; terminal: boolean };

let activeDialog: HTMLDialogElement | null = null;

export function openContextHandoff(
  source: Session,
  launch: (target: HandoffTarget) => Promise<string>,
): void {
  if (activeDialog) {
    activeDialog.focus();
    return;
  }

  const dialog = document.createElement('dialog');
  dialog.className = 'agent-session-details context-handoff-dialog';
  dialog.setAttribute('aria-labelledby', 'context-handoff-title');
  const sourceIsTerminal = !source.appServerControlOnly;
  const fileName = sourceIsTerminal ? 'terminal-output.txt' : 'chat-history.jsonl';
  dialog.innerHTML = `
    <header>
      <h2 id="context-handoff-title">${escapeHtml(t('contextHandoff.title'))}</h2>
      <button type="button" data-cancel class="agent-session-close" aria-label="${escapeHtml(t('dialog.cancel'))}">×</button>
    </header>
    <form>
      <div class="agent-session-content context-handoff-fields">
        <div class="context-handoff-target">
          <label>${escapeHtml(t('contextHandoff.harness'))}
            <select name="provider" autofocus>
              ${providers.map(({ id, label }) => `<option value="${id}">${label}</option>`).join('')}
            </select>
          </label>
          <label ${sourceIsTerminal ? 'hidden' : ''}>${escapeHtml(t('contextHandoff.surface'))}
            <select name="surface"><option value="acp">ACP</option><option value="terminal">Terminal</option></select>
          </label>
        </div>
        <p><code>${fileName}</code></p>
        <p>${escapeHtml(t(sourceIsTerminal ? 'contextHandoff.terminalFile' : 'contextHandoff.chatFile'))}</p>
        <label data-reference hidden>${escapeHtml(t('contextHandoff.fileReference'))}
          <textarea readonly rows="3"></textarea>
        </label>
        <p role="status" data-status></p>
      </div>
      <footer>
        <button type="button" data-copy hidden>${escapeHtml(t('contextHandoff.copyReference'))}</button>
        <button type="submit" data-launch>${escapeHtml(t('contextHandoff.openAcp'))}</button>
      </footer>
    </form>
  `;

  const form = getControl(dialog, 'form', HTMLFormElement);
  const select = getControl(dialog, 'select[name="provider"]', HTMLSelectElement);
  const surface = getControl(dialog, 'select[name="surface"]', HTMLSelectElement);
  const launchButton = getControl(dialog, '[data-launch]', HTMLButtonElement);
  const cancel = getControl(dialog, '[data-cancel]', HTMLButtonElement);
  const status = getControl(dialog, '[data-status]', HTMLElement);
  const copyButton = getControl(dialog, '[data-copy]', HTMLButtonElement);
  const reference = getControl(dialog, '[data-reference]', HTMLLabelElement);
  const referenceText = getControl(dialog, 'textarea', HTMLTextAreaElement);
  let launching = false;

  const sourceProvider = source.profileHint ?? source.foregroundProcessIdentity;
  select.value = providers.find(({ id }) => id !== sourceProvider)?.id ?? 'codex';
  const release = registerBackButtonLayer(requestClose);

  function close(): void {
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
  cancel.addEventListener('click', requestClose);
  surface.addEventListener('change', () => {
    launchButton.textContent = t(
      surface.value === 'terminal' ? 'contextHandoff.openTerminal' : 'contextHandoff.openAcp',
    );
  });
  copyButton.addEventListener('click', () => {
    void copyFileReference(referenceText, status);
  });
  form.addEventListener('submit', (event) => {
    event.preventDefault();
    const target = providers.find(({ id }) => id === select.value);
    if (!target || launchButton.disabled) return;
    const terminal = !sourceIsTerminal && surface.value === 'terminal';
    launching = true;
    for (const control of [select, surface, launchButton, cancel]) control.disabled = true;
    status.textContent = t('contextHandoff.opening');
    void launch({ provider: target.id, terminal })
      .then((filePath) => {
        if (!terminal) {
          close();
          return;
        }
        referenceText.value =
          'Read the chat history file, starting at its first (newest) entry. Use it as background for the next request. ' +
          filePath;
        reference.hidden = copyButton.hidden = false;
        launchButton.hidden = true;
        cancel.disabled = false;
        status.textContent = t('contextHandoff.pasteReference');
        copyButton.focus();
      })
      .catch((error: unknown) => {
        status.textContent = t('contextHandoff.openFailed').replace('{error}', () => String(error));
        for (const control of [select, surface, launchButton, cancel]) control.disabled = false;
      })
      .finally(() => {
        launching = false;
      });
  });
  activeDialog = dialog;
  document.body.append(dialog);
  dialog.showModal();
}

async function copyFileReference(input: HTMLTextAreaElement, status: HTMLElement): Promise<void> {
  try {
    await navigator.clipboard.writeText(input.value);
    status.textContent = t('contextHandoff.referenceCopied');
  } catch {
    input.focus();
    input.select();
    status.textContent = t('contextHandoff.copyFallback');
  }
}

function getControl<T extends HTMLElement>(
  dialog: HTMLDialogElement,
  selector: string,
  type: new () => T,
): T {
  const element = dialog.querySelector(selector);
  if (!(element instanceof type)) throw new Error('Handoff dialog is missing control ' + selector);
  return element;
}

export function activateTerminalHandoffSession(
  session: Session,
  select: (id: string) => void,
): void {
  setSession(session);
  newlyCreatedSessions.add(session.id);
  select(session.id);
}

export async function createContextHandoffSession(
  source: Session,
  target: HandoffTarget,
  shell: ShellType | null,
): Promise<{ session: Session; filePath: string }> {
  const dimensions = await resolveLaunchDimensions($currentSettings.get(), 'launcher');
  const { data } = await bootstrapWorker({
    ...dimensions,
    shell,
    handoffSourceSessionId: source.id,
    agentControlled: false,
    injectGuidance: false,
    profile: target.provider,
    appServerControlOnly: !target.terminal,
    launchCommand: target.terminal ? getHandoffTerminalCommand(target.provider, shell) : null,
    launchDelayMs: 0,
    slashCommands: [],
    slashCommandDelayMs: 0,
  });

  if (!data?.session || !data.handoffFilePath) throw new Error(t('sessionLauncher.createFailed'));
  setSmartInputHandoffDraft(data.session.id, data.handoffFilePath);
  return { session: data.session, filePath: data.handoffFilePath };
}

function getHandoffTerminalCommand(provider: HandoffProvider, shell: ShellType | null): string {
  if (provider !== 'antigravity') return provider;
  if (shell === 'Pwsh' || shell === 'PowerShell')
    return "if (Get-Command agy -ErrorAction SilentlyContinue) { agy } else { & (Join-Path $env:LOCALAPPDATA 'agy/bin/agy.exe') }";
  return 'agy';
}

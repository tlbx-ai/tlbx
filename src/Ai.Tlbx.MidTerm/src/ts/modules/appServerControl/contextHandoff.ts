import { bootstrapWorker } from '../../api/client';
import type { ShellType } from '../../api/types';
import { $currentSettings } from '../../stores';
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
  launch: (target: HandoffTarget) => Promise<void>,
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
        <p role="status" data-status></p>
      </div>
      <footer>
        <button type="submit" data-launch>${escapeHtml(t('contextHandoff.openAcp'))}</button>
      </footer>
    </form>
  `;

  const form = dialog.querySelector('form');
  const select = dialog.querySelector<HTMLSelectElement>('select[name="provider"]');
  const surface = dialog.querySelector<HTMLSelectElement>('select[name="surface"]');
  const launchButton = dialog.querySelector<HTMLButtonElement>('[data-launch]');
  const cancel = dialog.querySelector<HTMLButtonElement>('[data-cancel]');
  const status = dialog.querySelector<HTMLElement>('[data-status]');
  if (!form || !select || !surface || !launchButton || !cancel || !status)
    throw new Error('Handoff dialog is missing a control.');

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
    if (!launchButton?.disabled) close();
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
  form.addEventListener('submit', (event) => {
    event.preventDefault();
    const target = providers.find(({ id }) => id === select.value);
    if (!target || launchButton.disabled) return;
    for (const control of [select, surface, launchButton, cancel]) control.disabled = true;
    status.textContent = t('contextHandoff.opening');
    void launch({
      provider: target.id,
      terminal: !sourceIsTerminal && surface.value === 'terminal',
    })
      .then(close)
      .catch((error: unknown) => {
        status.textContent = t('contextHandoff.openFailed').replace('{error}', () => String(error));
        for (const control of [select, surface, launchButton, cancel]) control.disabled = false;
      });
  });
  activeDialog = dialog;
  document.body.append(dialog);
  dialog.showModal();
}

export async function createContextHandoffSession(
  source: Session,
  target: HandoffTarget,
  shell: ShellType | null,
): Promise<Session> {
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
  return data.session;
}

function getHandoffTerminalCommand(provider: HandoffProvider, shell: ShellType | null): string {
  if (provider !== 'antigravity') return provider;
  if (shell === 'Pwsh' || shell === 'PowerShell')
    return "if (Get-Command agy -ErrorAction SilentlyContinue) { agy } else { & (Join-Path $env:LOCALAPPDATA 'agy/bin/agy.exe') }";
  return 'agy';
}

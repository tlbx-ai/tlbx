import type { AppServerControlCommand, AppServerControlHistorySnapshot } from '../../api/types';
import { submitAppServerControlTurn } from '../appServerControl/input';
import { createAppServerControlTurnRequestWithQuickSettings } from '../appServerControl/quickSettings';
import { t } from '../i18n';
import { showAgentTechnicalDetails } from './historyVisibility';

export function syncNativeCommands(
  panel: HTMLElement,
  snapshot: AppServerControlHistorySnapshot,
  canSubmit: () => boolean,
): void {
  const host = panel.querySelector<HTMLElement>('[data-agent-field="native-controls"]');
  if (!host) return;
  const commands = snapshot.session.availableCommands ?? [];
  host.hidden =
    !showAgentTechnicalDetails() || (!snapshot.session.agentName && commands.length === 0);
  if (host.hidden) {
    host.replaceChildren();
    delete host.dataset.signature;
    return;
  }
  const signature = JSON.stringify([snapshot.session.agentName, commands]);
  if (host.dataset.signature !== signature) {
    const selected = host.querySelector('select')?.value;
    const argumentsText = host.querySelector('input')?.value ?? '';
    const open = host.querySelector('details')?.open ?? false;
    host.dataset.signature = signature;
    host.replaceChildren();
    const name = document.createElement('span');
    name.className = 'agent-native-name';
    name.textContent = snapshot.session.agentName ?? '';
    host.appendChild(name);
    if (commands.length) {
      const details = createCommandMenu(
        snapshot.sessionId,
        commands,
        canSubmit,
        selected,
        argumentsText,
      );
      details.open = open;
      host.appendChild(details);
    }
  }
  const submit = host.querySelector<HTMLButtonElement>('button[type="submit"]');
  if (submit) submit.disabled = !canSubmit();
}

function createCommandMenu(
  sessionId: string,
  commands: readonly AppServerControlCommand[],
  canSubmit: () => boolean,
  selected: string | undefined,
  argumentsText: string,
): HTMLDetailsElement {
  const details = document.createElement('details');
  const summary = document.createElement('summary');
  summary.textContent = `${t('appServerControl.commands.title')} (${commands.length})`;
  const form = document.createElement('form');
  form.className = 'agent-native-command-form';
  const select = document.createElement('select');
  select.setAttribute('aria-label', t('appServerControl.commands.title'));
  for (const command of commands) {
    const option = document.createElement('option');
    option.value = command.name;
    option.textContent = `/${command.name}`;
    select.appendChild(option);
  }
  if (selected && commands.some((command) => command.name === selected)) select.value = selected;
  const description = document.createElement('p');
  const input = document.createElement('input');
  input.type = 'text';
  input.value = argumentsText;
  const syncSelection = (): void => {
    const command = commands.find((entry) => entry.name === select.value);
    description.textContent = command?.description ?? '';
    input.hidden = command?.inputHint == null;
    input.placeholder = command?.inputHint ?? '';
    input.setAttribute(
      'aria-label',
      command?.inputHint || t('appServerControl.commands.arguments'),
    );
  };
  select.addEventListener('change', syncSelection);
  syncSelection();
  const submit = document.createElement('button');
  submit.type = 'submit';
  submit.className = 'agent-view-btn agent-view-btn-primary';
  submit.textContent = t('appServerControl.commands.run');
  const error = document.createElement('p');
  error.className = 'agent-native-command-error';
  error.setAttribute('role', 'alert');
  form.addEventListener('submit', (event) => {
    event.preventDefault();
    if (!canSubmit()) return;
    const command = commands.find((entry) => entry.name === select.value);
    if (!command) return;
    submit.disabled = true;
    error.textContent = '';
    const args = input.hidden ? '' : input.value.trim();
    const request = createAppServerControlTurnRequestWithQuickSettings(
      sessionId,
      `/${command.name}${args ? ` ${args}` : ''}`,
    );
    void submitAppServerControlTurn(sessionId, request)
      .then(() => {
        details.open = false;
      })
      .catch((reason: unknown) => {
        error.textContent = reason instanceof Error ? reason.message : String(reason);
      })
      .finally(() => {
        submit.disabled = !canSubmit();
      });
  });
  form.append(select, description, input, submit, error);
  details.append(summary, form);
  return details;
}

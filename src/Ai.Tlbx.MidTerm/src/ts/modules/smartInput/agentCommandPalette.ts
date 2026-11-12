import {
  getAgentSessionCommands,
  sessionToolText,
  type AgentCommand,
} from '../agentView/sessionTools';
import { reconcileKeyedChildren } from '../../utils/domReconcile';
import { positionComposerPopup } from './richComposerMenu';

/** Completion stays in the rich editor; arrows/Enter select, Escape only closes. */
export class AgentCommandPalette {
  private readonly element = document.createElement('div');
  private commands: AgentCommand[] = [];
  private selected = 0;
  private sessionId: string | null = null;
  private signature = '';
  private dismissed = '';

  constructor(private readonly choose: (sessionId: string, command: AgentCommand) => void) {
    this.element.className = 'agent-command-palette';
    this.element.id = 'agent-command-palette';
    this.element.setAttribute('role', 'listbox');
    this.element.setAttribute('aria-label', sessionToolText('commands'));
    this.element.hidden = true;
    this.element.addEventListener('pointerdown', (event) => {
      event.preventDefault();
    });
    document.body.append(this.element);
    document.addEventListener(
      'pointerdown',
      (event) => {
        if (
          event.target instanceof Element &&
          !event.target.closest('.agent-command-palette, .smart-input-rich-composer')
        )
          this.close();
      },
      true,
    );
  }

  update(sessionId: string, text: string, anchor: HTMLElement): void {
    const current = `${sessionId}:${text}`;
    if (this.signature !== current) this.dismissed = '';
    const match = /^\/([\w-]*)$/.exec(text);
    if (!match || !anchor.contains(document.activeElement)) {
      this.signature = current;
      this.close();
      return;
    }
    const signature = `${sessionId}:${text}`;
    if (this.dismissed === signature) return;
    this.sessionId = sessionId;
    this.commands = getAgentSessionCommands(sessionId).filter((command) =>
      command.name.toLowerCase().includes((match[1] ?? '').toLowerCase()),
    );
    if (this.signature !== signature) this.selected = 0;
    this.signature = signature;
    if (!this.commands.length) {
      this.close();
      return;
    }
    this.element.hidden = false;
    this.render();
    const bounds = anchor.getBoundingClientRect();
    positionComposerPopup(
      this.element,
      bounds.left,
      Math.max(6, bounds.top - this.element.offsetHeight - 6),
    );
    document.activeElement?.setAttribute('aria-controls', this.element.id);
  }

  keydown(event: KeyboardEvent): boolean {
    if (this.element.hidden || event.isComposing) return false;
    if (event.key === 'Escape') {
      event.preventDefault();
      this.dismissed = this.signature;
      this.close();
      return true;
    }
    if (event.key === 'ArrowDown' || event.key === 'ArrowUp') {
      event.preventDefault();
      this.selected =
        (this.selected + (event.key === 'ArrowDown' ? 1 : this.commands.length - 1)) %
        this.commands.length;
      this.render();
      return true;
    }
    if (
      (event.key === 'Enter' || event.key === 'Tab') &&
      !event.shiftKey &&
      !event.ctrlKey &&
      !event.metaKey &&
      !event.altKey
    ) {
      event.preventDefault();
      const command = this.commands[this.selected];
      if (command) this.accept(command);
      return true;
    }
    return false;
  }

  close(): void {
    this.element.hidden = true;
    document
      .querySelector(`[aria-controls="${this.element.id}"]`)
      ?.removeAttribute('aria-controls');
    document
      .querySelector('[aria-activedescendant^="agent-command-option-"]')
      ?.removeAttribute('aria-activedescendant');
  }

  private accept(command: AgentCommand): void {
    const sessionId = this.sessionId;
    this.close();
    if (sessionId) this.choose(sessionId, command);
  }

  private render(): void {
    reconcileKeyedChildren(this.element, this.commands, {
      key: (command) => command.name,
      create: (command) => {
        const button = document.createElement('button');
        button.type = 'button';
        button.setAttribute('role', 'option');
        button.tabIndex = -1;
        button.append(document.createElement('strong'), document.createElement('span'));
        button.addEventListener('click', () => {
          this.accept(command);
        });
        return button;
      },
      patch: (button, command, index) => {
        button.id = `agent-command-option-${index}`;
        button.setAttribute('aria-selected', String(index === this.selected));
        const name = button.querySelector('strong');
        const description = button.querySelector('span');
        if (name) name.textContent = `/${command.name}`;
        if (description) description.textContent = command.description;
      },
    });
    document.activeElement?.setAttribute(
      'aria-activedescendant',
      `agent-command-option-${this.selected}`,
    );
    const selected = this.element.children[this.selected] as HTMLElement | undefined;
    if (selected) {
      const top = selected.offsetTop;
      if (top < this.element.scrollTop) this.element.scrollTop = top;
      else if (top + selected.offsetHeight > this.element.scrollTop + this.element.clientHeight)
        this.element.scrollTop = top + selected.offsetHeight - this.element.clientHeight;
    }
  }
}

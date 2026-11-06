export interface BoardMenuAction {
  label: string;
  id: string;
  run: () => void;
}

/** Canvas and header menus share dismissal, keyboard navigation and viewport bounds. */
export class BoardMenu {
  private readonly element = document.createElement('div');
  private returnFocus: HTMLElement | null = null;

  constructor(
    private readonly host: HTMLElement,
    className: string,
  ) {
    this.element.className = `boards-menu ${className}`;
    this.element.setAttribute('role', 'menu');
    this.element.hidden = true;
    host.append(this.element);
    document.addEventListener(
      'pointerdown',
      (event) => {
        const target = event.target as Node;
        if (this.returnFocus?.hasAttribute('aria-haspopup') && this.returnFocus.contains(target))
          return;
        if (!this.element.contains(target)) this.close(false);
      },
      { capture: true },
    );
    this.element.addEventListener('keydown', (event) => {
      event.stopPropagation();
      this.keydown(event);
    });
  }

  get isOpen(): boolean {
    return !this.element.hidden;
  }

  open(actions: BoardMenuAction[], x: number, y: number, returnFocus: HTMLElement): void {
    this.returnFocus = returnFocus;
    if (returnFocus.hasAttribute('aria-haspopup'))
      returnFocus.setAttribute('aria-expanded', 'true');
    this.element.replaceChildren(
      ...actions.map((action) => {
        const button = document.createElement('button');
        button.type = 'button';
        button.tabIndex = -1;
        button.setAttribute('role', 'menuitem');
        button.dataset.boardAction = action.id;
        button.textContent = action.label;
        button.addEventListener('click', () => {
          this.close();
          action.run();
        });
        return button;
      }),
    );
    this.element.hidden = false;
    const host = this.host.getBoundingClientRect();
    this.element.style.left = `${Math.max(4, Math.min(x - host.left, host.width - this.element.offsetWidth - 4))}px`;
    this.element.style.top = `${Math.max(4, Math.min(y - host.top, host.height - this.element.offsetHeight - 4))}px`;
    this.element.querySelector('button')?.focus();
  }

  close(restoreFocus = true): void {
    if (this.element.hidden) return;
    this.element.hidden = true;
    if (this.returnFocus?.hasAttribute('aria-haspopup'))
      this.returnFocus.setAttribute('aria-expanded', 'false');
    if (restoreFocus) this.returnFocus?.focus({ preventScroll: true });
  }

  private keydown(event: KeyboardEvent): void {
    if (event.key === 'Escape' || event.key === 'Tab') {
      if (event.key === 'Escape') event.preventDefault();
      this.close();
      return;
    }
    const buttons = [...this.element.querySelectorAll('button')];
    const index = buttons.findIndex((button) => button === document.activeElement);
    const next = {
      ArrowDown: (index + 1) % buttons.length,
      ArrowUp: (index + buttons.length - 1) % buttons.length,
      Home: 0,
      End: buttons.length - 1,
    }[event.key];
    if (next === undefined) return;
    event.preventDefault();
    buttons[next]?.focus();
  }
}

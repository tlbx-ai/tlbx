export interface ComposerMenuAction {
  label: string;
  run: () => void;
  disabled?: boolean;
}

export function positionComposerPopup(element: HTMLElement, x: number, y: number): void {
  const viewport = window.visualViewport;
  const left = viewport?.offsetLeft ?? 0;
  const top = viewport?.offsetTop ?? 0;
  const width = viewport?.width ?? window.innerWidth;
  const height = viewport?.height ?? window.innerHeight;
  element.style.maxWidth = `${Math.max(0, width - 12)}px`;
  element.style.maxHeight = `${Math.max(0, height - 12)}px`;
  element.style.left = `${Math.max(left + 6, Math.min(x, left + width - element.offsetWidth - 6))}px`;
  element.style.top = `${Math.max(top + 6, Math.min(y, top + height - element.offsetHeight - 6))}px`;
}

/** Menu focus is separate from the editor's retained ProseMirror selection. */
export class RichComposerMenu {
  readonly element = document.createElement('div');

  constructor(private readonly returnFocus: () => void) {
    this.element.className = 'rich-composer-menu';
    this.element.setAttribute('role', 'menu');
    this.element.hidden = true;
    document.body.append(this.element);
    document.addEventListener(
      'pointerdown',
      (event) => {
        if (event.target instanceof Node && !this.element.contains(event.target)) this.close();
      },
      true,
    );
    this.element.addEventListener('keydown', (event) => {
      event.stopPropagation();
      if (event.key === 'Escape' || event.key === 'Tab') {
        if (event.key === 'Escape') event.preventDefault();
        this.close();
        this.returnFocus();
        return;
      }
      const buttons = [
        ...this.element.querySelectorAll<HTMLButtonElement>('button:not(:disabled)'),
      ];
      const index = buttons.indexOf(document.activeElement as HTMLButtonElement);
      const next = {
        ArrowDown: (index + 1) % buttons.length,
        ArrowUp: (index + buttons.length - 1) % buttons.length,
        Home: 0,
        End: buttons.length - 1,
      }[event.key];
      if (next === undefined) return;
      event.preventDefault();
      buttons[next]?.focus();
    });
  }

  open(x: number, y: number, label: string, actions: ComposerMenuAction[]): void {
    this.element.setAttribute('aria-label', label);
    this.element.replaceChildren(
      ...actions.map((action) => {
        const button = document.createElement('button');
        button.type = 'button';
        button.setAttribute('role', 'menuitem');
        button.tabIndex = -1;
        button.textContent = action.label;
        button.disabled = action.disabled ?? false;
        button.addEventListener('click', () => {
          this.close();
          this.returnFocus();
          action.run();
        });
        return button;
      }),
    );
    this.element.hidden = false;
    positionComposerPopup(this.element, x, y);
    this.element.querySelector<HTMLButtonElement>('button:not(:disabled)')?.focus();
  }

  close(): void {
    this.element.hidden = true;
  }

  reposition(): void {
    if (this.element.hidden) return;
    const bounds = this.element.getBoundingClientRect();
    positionComposerPopup(this.element, bounds.left, bounds.top);
  }
}

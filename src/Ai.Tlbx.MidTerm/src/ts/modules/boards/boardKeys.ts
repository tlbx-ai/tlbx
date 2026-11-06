export function trapBoardFocus(root: HTMLElement, event: KeyboardEvent): void {
  if (event.key !== 'Tab' || (event.target as HTMLElement).closest('[role="alertdialog"]')) return;
  const elements = [
    ...root.querySelectorAll<HTMLElement>('button,input,textarea,select,a[href],[tabindex="0"]'),
  ].filter((element) => element.offsetParent !== null && !element.matches(':disabled'));
  const first = elements[0];
  const last = elements[elements.length - 1];
  if (event.shiftKey && document.activeElement === first) {
    event.preventDefault();
    last?.focus();
  } else if (!event.shiftKey && document.activeElement === last) {
    event.preventDefault();
    first?.focus();
  }
}

export function isBoardEditing(event: KeyboardEvent): boolean {
  return !!(event.target as HTMLElement).closest(
    'input,textarea,select,[contenteditable="true"],[role="alertdialog"]',
  );
}

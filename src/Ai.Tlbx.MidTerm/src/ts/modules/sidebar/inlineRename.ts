/** Inline editing commits only on Enter; focus changes never save a partial name. */
export function openInlineSessionRename(
  item: Element,
  currentName: string,
  save: (name: string) => Promise<void>,
): void {
  const renameAnchor =
    item.querySelector('.session-title') ||
    item.querySelector('.process-title') ||
    item.querySelector('.session-title-row');
  if (!renameAnchor) return;

  const rect = renameAnchor.getBoundingClientRect();

  const input = document.createElement('input');
  input.type = 'text';
  input.className = 'session-rename-input';
  input.value = currentName;
  input.style.position = 'fixed';
  input.style.left = `${rect.left}px`;
  input.style.top = `${rect.top}px`;
  input.style.width = `${rect.width + 20}px`;
  input.style.height = `${rect.height}px`;
  input.style.zIndex = '10000';
  document.body.appendChild(input);

  let pending = false;
  let finished = false;
  const close = (): void => {
    finished = true;
    document.removeEventListener('pointerdown', onOutsideClick, true);
    input.remove();
  };
  const onOutsideClick = (event: PointerEvent): void => {
    if (!pending && event.target !== input) close();
  };
  const finishRename = async (): Promise<void> => {
    if (pending || finished) return;
    pending = true;
    input.disabled = true;
    try {
      await save(input.value);
      close();
      if (item instanceof HTMLElement && item.isConnected) item.focus({ preventScroll: true });
    } catch (error) {
      input.disabled = false;
      input.setCustomValidity(String(error));
      input.focus();
      input.reportValidity();
    } finally {
      pending = false;
    }
  };
  document.addEventListener('pointerdown', onOutsideClick, true);
  input.addEventListener('input', () => {
    input.setCustomValidity('');
  });
  input.addEventListener('keydown', (event) => {
    if (event.isComposing || event.repeat) return;
    if (event.key === 'Enter') {
      event.preventDefault();
      event.stopPropagation();
      void finishRename();
    } else if (event.key === 'Escape' && !pending) {
      event.preventDefault();
      event.stopPropagation();
      close();
      if (item instanceof HTMLElement && item.isConnected) item.focus({ preventScroll: true });
    }
  });

  input.focus();
  input.select();
}

export function isVisible(element: Element): boolean {
  return (
    element.getClientRects().length > 0 &&
    !element.closest('[inert], [hidden], .hidden') &&
    getComputedStyle(element).visibility === 'visible'
  );
}

export function hasTransientUi(): boolean {
  return Array.from(
    document.querySelectorAll(
      'dialog[open], [role="dialog"], .modal-overlay, .session-launcher-overlay, .session-rename-input, [role="menu"]:not(.session-actions), .session-item.menu-open .session-actions',
    ),
  ).some(isVisible);
}

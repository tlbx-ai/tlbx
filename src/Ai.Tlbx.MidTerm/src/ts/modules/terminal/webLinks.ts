/**
 * Opens an xterm-detected web link without replacing the MidTerm application.
 *
 * An actual anchor carries the browser-native new-tab semantics more reliably
 * than window.open(), particularly from installed mobile web apps.
 */
export function openTerminalWebLinkInNewTab(
  event: MouseEvent,
  uri: string,
  ownerDocument: Document = document,
): boolean {
  let url: URL;
  try {
    url = new URL(uri);
  } catch {
    return false;
  }

  if (url.protocol !== 'http:' && url.protocol !== 'https:') {
    return false;
  }

  event.preventDefault();
  event.stopPropagation();

  const link = ownerDocument.createElement('a');
  link.href = url.href;
  link.target = '_blank';
  link.rel = 'noopener noreferrer';
  link.hidden = true;
  ownerDocument.body.append(link);
  try {
    link.click();
  } finally {
    link.remove();
  }

  return true;
}

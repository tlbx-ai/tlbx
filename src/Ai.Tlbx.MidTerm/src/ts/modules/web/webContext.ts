/**
 * Web Preview Context
 *
 * Helpers for detecting when tlbx itself is running inside the dev browser.
 */

/** True when the current page is itself loaded through `/webpreview/`. */
export function isEmbeddedWebPreviewContext(): boolean {
  const path = window.location.pathname;
  return path === '/webpreview' || path.startsWith('/webpreview/');
}

/**
 * DOM Utilities
 *
 * Helper functions for DOM manipulation and event handling.
 */

/**
 * Escape HTML special characters to prevent XSS
 */
const HTML_ENTITIES: Record<string, string> = {
  '&': '&amp;',
  '<': '&lt;',
  '>': '&gt;',
  '"': '&quot;',
  "'": '&#39;',
};

export function escapeHtml(text: string): string {
  return text.replace(/[&<>"']/g, (character) => HTML_ENTITIES[character] ?? character);
}

/**
 * Bind a click event to an element by ID
 */
export function bindClick(id: string, handler: (e: MouseEvent) => void): void {
  const el = document.getElementById(id);
  if (el) {
    el.addEventListener('click', handler);
  }
}

/**
 * Create a throttled version of a function that fires immediately,
 * then at most once per interval, with a trailing call to capture final state.
 */
export function throttle<T extends (...args: unknown[]) => void>(
  fn: T,
  interval: number,
): (...args: Parameters<T>) => void {
  let lastCall = 0;
  let trailingTimeoutId: number | undefined;
  return (...args: Parameters<T>) => {
    const now = performance.now();
    const elapsed = now - lastCall;

    if (trailingTimeoutId !== undefined) {
      clearTimeout(trailingTimeoutId);
    }

    if (elapsed >= interval) {
      lastCall = now;
      fn(...args);
    }

    trailingTimeoutId = window.setTimeout(() => {
      lastCall = performance.now();
      fn(...args);
    }, interval);
  };
}

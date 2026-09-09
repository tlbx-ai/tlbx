import { reconcileKeyedChildren } from '../../utils/domReconcile';

const renderedMarkup = new WeakMap<HTMLElement, string>();

function key(element: HTMLElement, index: number): string {
  const data = element.dataset;
  return JSON.stringify([
    element.tagName,
    data.action ?? element.classList.item(0),
    data.path ?? data.hash ?? data.section ?? index,
    data.scope,
  ]);
}

function patchElement(current: HTMLElement, next: HTMLElement): void {
  for (const attribute of Array.from(current.attributes)) {
    if (attribute.name !== 'data-reconcile-key' && !next.hasAttribute(attribute.name)) {
      current.removeAttribute(attribute.name);
    }
  }
  for (const attribute of Array.from(next.attributes)) {
    if (current.getAttribute(attribute.name) !== attribute.value) {
      current.setAttribute(attribute.name, attribute.value);
    }
  }
  // The inspector has an independent lifetime: status changes must not parse,
  // rebuild or walk a potentially large, unchanged diff.
  if (next.classList.contains('git-panel-inspector')) return;
  if (next.children.length === 0) {
    if (current.textContent !== next.textContent) current.textContent = next.textContent;
    return;
  }
  // Most git containers contain only elements and formatting whitespace. Mixed
  // text (e.g. section labels and diff spans) needs its original node order.
  const mixedText = Array.from(next.childNodes).some(
    (node) => node.nodeType === Node.TEXT_NODE && node.textContent?.trim(),
  );
  if (mixedText) {
    if (current.innerHTML !== next.innerHTML) current.innerHTML = next.innerHTML;
    return;
  }
  for (const node of Array.from(current.childNodes)) {
    if (node.nodeType === Node.TEXT_NODE) node.remove();
  }
  patchChildren(current, next);
}

function patchChildren(current: HTMLElement, next: HTMLElement): void {
  reconcileKeyedChildren(current, Array.from(next.children) as HTMLElement[], {
    key,
    create: (element) => element.cloneNode(false) as HTMLElement,
    patch: patchElement,
  });
}

export function syncGitPanelDom(container: HTMLElement, html: string): void {
  if (renderedMarkup.get(container) === html && container.childElementCount > 0) return;
  const template = document.createElement('template');
  template.innerHTML = html;
  patchChildren(container, template.content as unknown as HTMLElement);
  renderedMarkup.set(container, html);
}

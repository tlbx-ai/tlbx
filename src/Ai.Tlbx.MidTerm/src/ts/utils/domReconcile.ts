/**
 * Minimal keyed DOM reconciliation for tlbx-owned UI lists and trees.
 *
 * This is intentionally not a UI framework, virtual DOM, templating layer, or
 * state system. It is a small ownership primitive for places where we already
 * build DOM by hand but need to preserve node identity on hot updates.
 *
 * Why this exists:
 * - `replaceChildren()` is simple, but it destroys hover/focus/selection state,
 *   unregisters browser optimizations, and can make large trees flicker.
 * - UI lists usually have stable domain keys (`session.id`, `workspace.key`,
 *   `machine.id`). Those keys are enough to patch existing elements in place.
 * - A tiny shared helper gives humans and AI coding agents an obvious default
 *   before reaching for whole-tree rebuilds.
 *
 * Use this when:
 * - the parent owns a direct list of keyed child elements;
 * - existing children can be patched from the latest data;
 * - add/remove/reorder should be handled without recreating unchanged nodes.
 *
 * Do not use this when:
 * - the parent contains unrelated children this view does not own;
 * - child order is controlled by CSS or a third-party component;
 * - the update is a rare, deliberate full reset where losing DOM identity is OK.
 *
 * Contract:
 * - `key(item)` must be stable and unique among siblings.
 * - `create(item)` returns the element for a brand-new key and should bind
 *   long-lived event handlers once.
 * - `patch(element, item)` updates text, attributes, classes, child lists, and
 *   any WeakMap-backed current context. It must not replace `element`.
 * - `destroy(element)` is for cleanup tied to removed DOM: canvas registries,
 *   observers, timers, external listeners, or nested owned children.
 *
 * Important ownership rule:
 * This reconciles direct children only. Nested lists should call
 * `reconcileKeyedChildren` again from their parent element's `patch` function.
 * That keeps ownership local and prevents accidental removal of unrelated DOM.
 */

export interface KeyedDomView<T, TElement extends HTMLElement = HTMLElement> {
  key: (item: T, index: number) => string;
  create: (item: T, index: number) => TElement;
  patch: (element: TElement, item: T, index: number) => void;
  destroy?: (element: TElement) => void;
}

const RECONCILE_KEY_DATASET_NAME = 'reconcileKey';

function getElementKey(element: HTMLElement): string | undefined {
  return element.dataset[RECONCILE_KEY_DATASET_NAME];
}

function setElementKey(element: HTMLElement, key: string): void {
  element.dataset[RECONCILE_KEY_DATASET_NAME] = key;
}

function removeElement<TElement extends HTMLElement>(
  element: TElement,
  destroy: ((element: TElement) => void) | undefined,
): void {
  destroy?.(element);
  element.remove();
}

/**
 * Reconcile `parent`'s direct children against `items`.
 *
 * Existing child elements with matching keys are patched and moved into the new
 * order. New keys call `create`. Removed keys call `destroy` before removal.
 * Unkeyed direct children are treated as stale owned children and removed.
 */
export function reconcileKeyedChildren<T, TElement extends HTMLElement = HTMLElement>(
  parent: HTMLElement,
  items: readonly T[],
  view: KeyedDomView<T, TElement>,
): void {
  const existingChildren = Array.from(parent.children) as TElement[];
  const existingByKey = new Map<string, TElement>();
  const staleChildren: TElement[] = [];

  for (const child of existingChildren) {
    const key = getElementKey(child);
    if (!key || existingByKey.has(key)) {
      staleChildren.push(child);
      continue;
    }
    existingByKey.set(key, child);
  }

  const desiredKeys = new Set<string>();
  const desiredElements: TElement[] = [];

  items.forEach((item, index) => {
    const key = view.key(item, index);
    if (desiredKeys.has(key)) {
      throw new Error(`Duplicate keyed DOM child: ${key}`);
    }
    desiredKeys.add(key);

    let element = existingByKey.get(key);
    if (!element) {
      element = view.create(item, index);
    }

    setElementKey(element, key);
    view.patch(element, item, index);
    desiredElements.push(element);
  });

  desiredElements.forEach((element, index) => {
    const current = parent.children[index] ?? null;
    if (current !== element) {
      parent.insertBefore(element, current);
    }
  });

  for (const child of Array.from(parent.children) as TElement[]) {
    const key = getElementKey(child);
    if (!key || !desiredKeys.has(key) || !desiredElements.includes(child)) {
      removeElement(child, view.destroy);
    }
  }
}

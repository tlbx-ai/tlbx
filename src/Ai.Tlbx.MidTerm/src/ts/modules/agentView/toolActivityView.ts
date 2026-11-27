import { t } from '../i18n';
import { reconcileKeyedChildren } from '../../utils/domReconcile';
import { resolveToolCommandScript, summarizeToolActivityGroup } from './toolRowPresentation';
import type { AppServerControlHistoryEntry } from './types';

export { resolveToolCommandScript };

function text(key: string, fallback: string): string {
  const value = t(`appServerControl.activity.${key}`);
  return value && value !== `appServerControl.activity.${key}` ? value : fallback;
}

const KNOWN_TOOL_LABELS: Record<string, [string, string]> = {
  'ran command': ['ranCommand', 'Ran command'],
  command: ['ranCommand', 'Ran command'],
  'read file': ['readFile', 'Read file'],
  'edited file': ['editedFile', 'Edited file'],
  'edit file': ['editedFile', 'Edited file'],
  'file edit': ['editedFile', 'Edited file'],
  searched: ['search', 'Search'],
  search: ['search', 'Search'],
  'used tool': ['usedTool', 'Used tool'],
  tool: ['usedTool', 'Used tool'],
};

function categoryLabel(category: string): string {
  const labels: Record<string, string> = {
    command: 'Commands',
    edit: 'File edit',
    read: 'File read',
    search: 'Search',
  };
  const fallback = labels[category];
  return fallback ? text(category, fallback) : text('tools', 'Tools');
}

export function localizeToolPresentationLabel(
  entry: AppServerControlHistoryEntry,
  fallback: string,
): string {
  const presentation = entry.toolPresentation;
  const rawLabel = presentation?.label.trim();
  if (rawLabel) {
    const mapped = KNOWN_TOOL_LABELS[rawLabel.toLowerCase()];
    return mapped ? text(mapped[0], mapped[1]) : rawLabel;
  }
  return presentation?.category ? categoryLabel(presentation.category) : fallback;
}

const updates = new WeakMap<HTMLDetailsElement, (entry: AppServerControlHistoryEntry) => void>();

export function syncToolActivityDisclosure(
  details: HTMLDetailsElement,
  entry: AppServerControlHistoryEntry,
): boolean {
  const update = updates.get(details);
  if (!update) return false;
  update(entry);
  return true;
}

export function createToolActivityDisclosure(
  initialEntry: AppServerControlHistoryEntry,
  createEntry: (entry: AppServerControlHistoryEntry) => HTMLElement,
  syncEntry: (element: HTMLElement, entry: AppServerControlHistoryEntry) => void,
  expanded: Set<string> | undefined,
  onToggle?: (
    details: HTMLDetailsElement,
    summary: HTMLElement,
    performMutation: () => void,
  ) => void,
): HTMLDetailsElement {
  let entry = initialEntry;
  const details = document.createElement('details');
  details.className = 'agent-tool-activity';
  const summary = document.createElement('summary');
  const label = document.createElement('span');
  label.className = 'agent-tool-activity-label';
  const countBadge = document.createElement('span');
  countBadge.className = 'agent-tool-activity-count';
  const tools = document.createElement('span');
  tools.className = 'agent-tool-activity-names';
  summary.append(label, countBadge, tools);
  details.append(summary);
  let list: HTMLElement | null = null;
  const updateList = () => {
    if (!list) return;
    reconcileKeyedChildren(list, entry.toolActivityEntries ?? [], {
      key: (item) => `${item.sourceTurnId ?? ''}:${item.sourceItemId || item.id}`,
      create: (item) => createEntry(item),
      patch: (element, item) => {
        syncEntry(element, item);
      },
    });
  };
  const update = (next: AppServerControlHistoryEntry) => {
    entry = next;
    const entries = entry.toolActivityEntries ?? [];
    label.textContent = text('label', 'Tool calls:').replace(/:\s*$/, '');
    countBadge.textContent = String(entries.length);
    summary.title = text(entries.length === 1 ? 'one' : 'many', '{count} tool calls').replace(
      '{count}',
      String(entries.length),
    );
    tools.textContent = summarizeToolActivityGroup(entries);
    updateList();
  };
  updates.set(details, update);
  update(entry);
  const mount = () => {
    if (list) return;
    list = document.createElement('div');
    list.className = 'agent-tool-activity-list';
    details.append(list);
    updateList();
  };
  details.open = expanded?.has(entry.id) === true;
  if (details.open) mount();
  details.addEventListener('toggle', () => {
    const apply = () => {
      if (details.open) {
        expanded?.add(entry.id);
        mount();
      } else {
        expanded?.delete(entry.id);
      }
    };
    if (onToggle) {
      onToggle(details, summary, apply);
    } else {
      apply();
    }
  });
  return details;
}

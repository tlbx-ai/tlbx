import { t } from '../i18n';
import { reconcileKeyedChildren } from '../../utils/domReconcile';
import { commandPreview } from './commandHighlight';
import type { AppServerControlHistoryEntry } from './types';

function text(key: string, fallback: string): string {
  const value = t(`appServerControl.activity.${key}`);
  return value && value !== `appServerControl.activity.${key}` ? value : fallback;
}

function commandLabel(command: string): string {
  const preview = commandPreview(command).replace(/^["']/, '');
  if (/^gh(?:\.exe)?(?:\s|$)/i.test(preview)) return 'gh CLI';
  if (/^(?:"[^"\n]*[\\/])?(?:pwsh|powershell)(?:\.exe)?["\s]/i.test(command)) return 'PowerShell';
  if (/^(?:"[^"\n]*[\\/])?bash(?:\.exe)?["\s]/i.test(command)) return 'Bash';
  return text('command', 'Commands');
}

function toolLabel(entry: AppServerControlHistoryEntry): string {
  const presentation = entry.toolPresentation;
  const name = presentation?.toolName?.trim();
  if (name && !/^(?:exec_command|shell|command)$/i.test(name)) return name;
  if (presentation?.category === 'command' || entry.commandText) {
    return commandLabel(entry.commandText || presentation?.subject || '');
  }
  return categoryLabel(presentation?.category ?? 'other');
}

function categoryLabel(category: string): string {
  const labels: Record<string, string> = { edit: 'File edit', read: 'File read', search: 'Search' };
  const fallback = labels[category];
  return fallback ? text(category, fallback) : text('tools', 'Tools');
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
): HTMLDetailsElement {
  let entry = initialEntry;
  const details = document.createElement('details');
  details.className = 'agent-tool-activity';
  const summary = document.createElement('summary');
  const label = document.createElement('span');
  label.className = 'agent-tool-activity-label';
  label.textContent = `${text('label', 'Tool calls:')} `;
  const tools = document.createElement('span');
  tools.className = 'agent-tool-activity-names';
  summary.append(label, tools);
  details.append(summary);
  let list: HTMLElement | null = null;
  const updateList = () => {
    if (!list) return;
    reconcileKeyedChildren(list, entry.toolActivityEntries ?? [], {
      key: (item) => `${item.sourceTurnId ?? ''}:${item.sourceItemId || item.id}`,
      create: (item) => createEntry({ ...item, title: toolLabel(item) }),
      patch: (element, item) => {
        syncEntry(element, { ...item, title: toolLabel(item) });
      },
    });
  };
  const update = (next: AppServerControlHistoryEntry) => {
    entry = next;
    const entries = entry.toolActivityEntries ?? [];
    const names = new Map<string, number>();
    for (const item of entries) {
      const name = toolLabel(item);
      names.set(name, (names.get(name) ?? 0) + 1);
    }
    const labels = [...names].map(([name, count]) => (count > 1 ? `${name} ×${count}` : name));
    summary.title = text(entries.length === 1 ? 'one' : 'many', '{count} tool calls').replace(
      '{count}',
      String(entries.length),
    );
    tools.textContent =
      labels.slice(0, 4).join(', ') + (labels.length > 4 ? `, +${labels.length - 4}` : '');
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
    if (details.open) {
      expanded?.add(entry.id);
      mount();
    } else expanded?.delete(entry.id);
  });
  return details;
}

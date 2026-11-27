import { t } from '../i18n';
import { reconcileKeyedChildren } from '../../utils/domReconcile';
import { commandPreview } from './commandHighlight';
import type { AppServerControlHistoryEntry } from './types';

function text(key: string, fallback: string): string {
  const value = t(`appServerControl.activity.${key}`);
  return value && value !== `appServerControl.activity.${key}` ? value : fallback;
}

const GENERIC_COMMAND_TOOL_NAMES = /^(?:exec_command|shell|command|ran command|run_command)$/i;

export function resolveToolCommandScript(entry: AppServerControlHistoryEntry): string {
  const direct = entry.commandText?.trim();
  if (direct) return direct;
  const presentation = entry.toolPresentation;
  const subject = presentation?.subject?.trim();
  if (subject) return subject;
  if (presentation?.category === 'command') {
    const rawToolName = presentation.toolName?.trim();
    if (rawToolName && !GENERIC_COMMAND_TOOL_NAMES.test(rawToolName)) {
      return rawToolName;
    }
  }
  return '';
}

function commandLabel(command: string): string {
  const preview = commandPreview(command).replace(/^["']/, '');
  if (/^gh(?:\.exe)?(?:\s|$)/i.test(preview)) return 'gh CLI';
  if (/^(?:"[^"\n]*[\\/])?(?:pwsh|powershell)(?:\.exe)?["\s]/i.test(command)) return 'PowerShell';
  if (/^(?:"[^"\n]*[\\/])?bash(?:\.exe)?["\s]/i.test(command)) return 'Bash';
  if (/^git(?:\.exe)?(?:\s|$)/i.test(preview)) return 'git';
  if (/^(?:dotnet|npm|npx|node|python|py)(?:\.exe)?(?:\s|$)/i.test(preview)) {
    return preview.split(/\s+/)[0] ?? text('command', 'Commands');
  }
  return text('command', 'Commands');
}

function normalizeDisplayToolName(rawName: string): string {
  const trimmed = rawName.trim().replace(/^Run\s+/i, '');
  if (!trimmed) return '';
  if (trimmed.includes('\n')) return '';
  if (/^File\s+/i.test(trimmed) || /^[A-Za-z]:[\\/]|^\/[^\s]+\//.test(trimmed)) {
    return '';
  }
  return trimmed.length > 48 ? `${trimmed.slice(0, 45)}…` : trimmed;
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

function toolLabel(entry: AppServerControlHistoryEntry): string {
  const presentation = entry.toolPresentation;
  if (presentation?.category === 'command' || entry.commandText) {
    return commandLabel(resolveToolCommandScript(entry));
  }
  const normalizedName = presentation?.toolName
    ? normalizeDisplayToolName(presentation.toolName)
    : '';
  if (normalizedName && !GENERIC_COMMAND_TOOL_NAMES.test(normalizedName)) {
    return normalizedName;
  }
  return categoryLabel(presentation?.category ?? 'other');
}

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
  const resolveItemTitle = (item: AppServerControlHistoryEntry): string => {
    const presentation = item.toolPresentation;
    if (presentation?.category === 'command' || item.commandText) {
      return localizeToolPresentationLabel(item, text('ranCommand', 'Ran command'));
    }
    const rawName = presentation?.toolName?.trim();
    if (rawName) {
      const cleaned = rawName.replace(/^Run\s+/i, '').trim();
      if (/^File\s+/i.test(cleaned)) {
        return `${localizeToolPresentationLabel(item, categoryLabel(presentation?.category ?? 'read'))}: ${cleaned.replace(/^File\s+/i, '')}`;
      }
      return cleaned;
    }
    return localizeToolPresentationLabel(item, toolLabel(item));
  };
  const updateList = () => {
    if (!list) return;
    reconcileKeyedChildren(list, entry.toolActivityEntries ?? [], {
      key: (item) => `${item.sourceTurnId ?? ''}:${item.sourceItemId || item.id}`,
      create: (item) => createEntry({ ...item, title: resolveItemTitle(item) }),
      patch: (element, item) => {
        syncEntry(element, { ...item, title: resolveItemTitle(item) });
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
    label.textContent = text('label', 'Tool calls:').replace(/:\s*$/, '');
    countBadge.textContent = String(entries.length);
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

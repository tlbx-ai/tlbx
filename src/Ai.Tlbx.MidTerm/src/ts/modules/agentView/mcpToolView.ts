import { t } from '../i18n';
import { hasMcpParameters, isHistoryError } from './historyVisibility';
import type { AppServerControlHistoryEntry } from './types';

function text(key: string): string {
  return t(`appServerControl.mcp.${key}`);
}

const updates = new WeakMap<HTMLDetailsElement, (entry: AppServerControlHistoryEntry) => void>();

export function isMcpToolEntry(entry: AppServerControlHistoryEntry): boolean {
  return !!entry.toolPresentation?.mcp;
}

export function syncMcpToolDisclosure(
  details: HTMLDetailsElement,
  entry: AppServerControlHistoryEntry,
): boolean {
  const update = updates.get(details);
  if (!update) return false;
  update(entry);
  return true;
}

export function createMcpToolDisclosure(
  initialEntry: AppServerControlHistoryEntry,
  expanded: Set<string> | undefined,
  outputLines: (entry: AppServerControlHistoryEntry) => string[],
  onToggle?: (
    details: HTMLDetailsElement,
    summary: HTMLElement,
    performMutation: () => void,
  ) => void,
): HTMLDetailsElement {
  let entry = initialEntry;
  const details = document.createElement('details');
  details.className = 'agent-mcp-tool';
  const summary = document.createElement('summary');
  const heading = document.createElement('span');
  heading.className = 'agent-mcp-heading';
  const protocol = document.createElement('span');
  protocol.className = 'agent-mcp-protocol';
  protocol.textContent = 'MCP';
  const name = document.createElement('strong');
  name.className = 'agent-mcp-name';
  const address = document.createElement('span');
  address.className = 'agent-mcp-address';
  const status = document.createElement('span');
  status.className = 'agent-mcp-status';
  heading.append(protocol, name, address, status);
  const preview = document.createElement('span');
  preview.className = 'agent-mcp-preview';
  summary.append(heading, preview);
  details.append(summary);
  let body: ReturnType<typeof createBody> | null = null;
  let lastParameters: string | null | undefined;
  const updateBody = () => {
    if (!details.open || !body) return;
    const mcp = entry.toolPresentation?.mcp;
    body.requestSection.hidden = !hasMcpParameters(mcp?.parameters) && !mcp?.parametersTruncated;
    if (lastParameters !== mcp?.parameters || !body.parameters.textContent) {
      lastParameters = mcp?.parameters;
      let value = lastParameters ?? text('parametersUnavailable');
      try {
        value = JSON.stringify(JSON.parse(value), null, 2);
      } catch {
        /* Partial streaming JSON. */
      }
      body.parameters.textContent = value;
    }
    body.truncated.hidden = mcp?.parametersTruncated !== true;
    const evidence = outputLines(entry).join('\n');
    body.output.hidden = !evidence;
    body.output.textContent = evidence;
    body.emptyOutput.hidden = !!evidence;
  };
  const update = (next: AppServerControlHistoryEntry) => {
    entry = next;
    const presentation = entry.toolPresentation;
    const mcp = presentation?.mcp;
    if (!presentation || !mcp) return;
    const toolName = presentation.toolName || presentation.label || 'MCP';
    name.textContent = mcp.serverName ? `${mcp.serverName}.${toolName}` : toolName;
    name.title = name.textContent;
    address.textContent = mcp.serverAddress || text('addressUnavailable');
    address.title = address.textContent;
    const failed = isHistoryError(entry);
    details.dataset.error = String(failed);
    status.textContent = failed ? presentation.outcome || entry.sourceStatus || '' : '';
    preview.textContent =
      mcp.parameters?.slice(0, 512).replace(/\s+/g, ' ') || text('parametersUnavailable');
    preview.hidden = !hasMcpParameters(mcp.parameters);
    updateBody();
  };
  const mount = () => {
    if (!body) {
      body = createBody();
      details.append(body.root);
    }
    updateBody();
  };
  updates.set(details, update);
  update(entry);
  details.open = expanded?.has(entry.id) === true;
  if (details.open) mount();
  details.addEventListener('toggle', () => {
    const apply = () => {
      if (details.open) {
        expanded?.add(entry.id);
        mount();
      } else expanded?.delete(entry.id);
    };
    if (onToggle) {
      onToggle(details, summary, apply);
    } else {
      apply();
    }
  });
  return details;
}

function createBody() {
  const root = document.createElement('div');
  root.className = 'agent-mcp-body';
  const requestSection = createSection(text('parameters'));
  const parameters = document.createElement('pre');
  parameters.setAttribute('aria-label', text('parameters'));
  const truncated = document.createElement('p');
  truncated.className = 'agent-mcp-truncated';
  truncated.textContent = text('parametersTruncated');
  requestSection.append(parameters, truncated);
  const responseSection = createSection(text('output'));
  const output = document.createElement('pre');
  output.className = 'agent-mcp-output';
  output.setAttribute('aria-label', text('output'));
  const emptyOutput = document.createElement('p');
  emptyOutput.className = 'agent-mcp-empty-output';
  emptyOutput.textContent = text('outputUnavailable');
  responseSection.append(output, emptyOutput);
  root.append(requestSection, responseSection);
  return { root, requestSection, parameters, truncated, output, emptyOutput };
}

function createSection(label: string): HTMLElement {
  const section = document.createElement('section');
  section.className = 'agent-mcp-section';
  const heading = document.createElement('h4');
  heading.className = 'agent-mcp-section-label';
  heading.textContent = label;
  section.append(heading);
  return section;
}

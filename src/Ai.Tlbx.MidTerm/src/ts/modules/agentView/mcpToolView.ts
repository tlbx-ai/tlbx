import { t } from '../i18n';
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
  const server = document.createElement('span');
  server.className = 'agent-mcp-server';
  const address = document.createElement('span');
  address.className = 'agent-mcp-address';
  const status = document.createElement('span');
  status.className = 'agent-mcp-status';
  heading.append(protocol, name, server, address, status);
  const preview = document.createElement('span');
  preview.className = 'agent-mcp-preview';
  summary.append(heading, preview);
  details.append(summary);
  let body: HTMLElement | null = null;
  let parameters: HTMLPreElement | null = null;
  let output: HTMLPreElement | null = null;
  let truncated: HTMLElement | null = null;
  let lastParameters: string | null | undefined;
  const updateBody = () => {
    if (!details.open || !body || !parameters || !output || !truncated) return;
    const mcp = entry.toolPresentation?.mcp;
    if (lastParameters !== mcp?.parameters || !parameters.textContent) {
      lastParameters = mcp?.parameters;
      let value = lastParameters ?? text('parametersUnavailable');
      try {
        value = JSON.stringify(JSON.parse(value), null, 2);
      } catch {
        /* Partial streaming JSON. */
      }
      parameters.textContent = value;
    }
    truncated.hidden = mcp?.parametersTruncated !== true;
    const evidence = outputLines(entry).join('\n');
    output.hidden = !evidence;
    output.textContent = evidence;
  };
  const update = (next: AppServerControlHistoryEntry) => {
    entry = next;
    const presentation = entry.toolPresentation;
    const mcp = presentation?.mcp;
    if (!presentation || !mcp) return;
    name.textContent = presentation.toolName || presentation.label || 'MCP';
    name.title = name.textContent;
    server.textContent = mcp.serverName || text('serverUnavailable');
    server.title = server.textContent;
    address.textContent = mcp.serverAddress || text('addressUnavailable');
    address.title = address.textContent;
    status.textContent = presentation.outcome || entry.sourceStatus || '';
    preview.textContent = mcp.parameters?.replace(/\s+/g, ' ') || text('parametersUnavailable');
    updateBody();
  };
  const mount = () => {
    if (!body) {
      body = document.createElement('div');
      body.className = 'agent-mcp-body';
      parameters = document.createElement('pre');
      parameters.setAttribute('aria-label', text('parameters'));
      output = document.createElement('pre');
      output.className = 'agent-mcp-output';
      output.setAttribute('aria-label', text('output'));
      truncated = document.createElement('p');
      truncated.className = 'agent-mcp-truncated';
      truncated.textContent = text('parametersTruncated');
      body.append(parameters, truncated, output);
      details.append(body);
    }
    updateBody();
  };
  updates.set(details, update);
  update(entry);
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

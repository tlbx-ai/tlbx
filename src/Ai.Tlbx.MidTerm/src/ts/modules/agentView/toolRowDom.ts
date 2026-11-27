import { t } from '../i18n';
import {
  enrichInteractiveTextContent,
  wireAssistantInteractiveContent,
} from './assistantEnrichment';
import { appendHighlightedCommand } from './commandHighlight';
import {
  normalizeDisplayPath,
  parseCompactToolRow,
  resolveToolCommandScript,
  type ParsedToolRowSummary,
} from './toolRowPresentation';
import { clampToolCallOutputLines, selectProjectedToolEvidenceLines } from './toolOutput';
import type { AppServerControlHistoryEntry } from './types';

function activityText(key: string, fallback: string): string {
  const value = t(`appServerControl.activity.${key}`);
  return value && value !== `appServerControl.activity.${key}` ? value : fallback;
}

function createStatusIndicator(
  statusKind: 'done' | 'error' | 'running',
  statusText: string,
): HTMLElement {
  const status = document.createElement('span');
  status.className = 'agent-tool-status';
  status.dataset.status = statusKind;
  status.title = statusText;
  status.setAttribute('aria-label', statusText);
  if (statusKind === 'done') {
    status.textContent = '✓';
  } else if (statusKind === 'error') {
    status.textContent = `✕ ${statusText}`;
  } else {
    status.textContent = '…';
  }
  return status;
}

function createFileLink(
  displayText: string,
  fullPath: string,
  className: string,
): HTMLAnchorElement {
  const link = document.createElement('a');
  link.href = '#';
  link.className = `agent-history-inline-link agent-history-inline-file ${className}`;
  link.dataset.filePath = fullPath;
  link.dataset.fileKind = /^[a-z]:[\\/]|^\\\\|^\//i.test(fullPath) ? 'absolute' : 'relative';
  link.textContent = displayText;
  link.title = fullPath;
  return link;
}

function buildCompactRowContent(
  parsed: ParsedToolRowSummary,
  entry: AppServerControlHistoryEntry,
  sessionId: string,
): HTMLElement {
  const row = document.createElement('div');
  row.className = 'agent-tool-row';
  row.dataset.badgeTone = parsed.badgeTone;
  row.dataset.status = parsed.statusKind;

  const badge = document.createElement('span');
  badge.className = `agent-tool-badge agent-tool-badge-${parsed.badgeTone}`;
  badge.textContent = parsed.badgeText;
  row.appendChild(badge);

  if (parsed.serverName) {
    const server = document.createElement('span');
    server.className = 'agent-tool-server';
    server.textContent = parsed.serverName;
    row.appendChild(server);
  }

  if (parsed.badgeTone === 'read' && parsed.secondaryPathRaw) {
    row.appendChild(
      createFileLink(parsed.primaryText, parsed.secondaryPathRaw, 'agent-tool-primary-link'),
    );
    if (parsed.secondaryPath) {
      const dir = document.createElement('span');
      dir.className = 'agent-tool-secondary';
      dir.textContent = parsed.secondaryPath;
      dir.title = parsed.secondaryPathRaw;
      row.appendChild(dir);
    }
  } else {
    const primary = document.createElement('span');
    primary.className = 'agent-tool-primary';
    primary.textContent = parsed.primaryText;
    primary.title = parsed.primaryText;
    row.appendChild(primary);

    if (parsed.secondaryPath) {
      const secondary = document.createElement('span');
      secondary.className = 'agent-tool-secondary';
      secondary.textContent = parsed.secondaryPath;
      secondary.title = parsed.secondaryPathRaw ?? parsed.secondaryPath;
      enrichInteractiveTextContent(
        secondary,
        (entry.fileMentions ?? []).filter((mention) => mention.field === 'body'),
      );
      row.appendChild(secondary);
    }
  }

  row.appendChild(createStatusIndicator(parsed.statusKind, parsed.statusText));
  wireAssistantInteractiveContent(row, sessionId);
  return row;
}

export function isCompactProjectedToolEntry(entry: AppServerControlHistoryEntry): boolean {
  const presentation = entry.toolPresentation;
  if (!presentation || entry.kind !== 'tool') return false;
  if (presentation.category === 'command' || presentation.category === 'edit') return false;
  if (presentation.mcp) return false;
  return true;
}

export function createCompactToolRowElement(args: {
  entry: AppServerControlHistoryEntry;
  sessionId: string;
  expanded: Set<string> | undefined;
  onToggle: (details: HTMLDetailsElement, summary: HTMLElement, mutate: () => void) => void;
}): HTMLElement {
  const { entry, sessionId, expanded, onToggle } = args;
  const presentation = entry.toolPresentation;
  const parsed = parseCompactToolRow(entry);
  const extraPaths = (presentation?.paths ?? [])
    .map(normalizeDisplayPath)
    .filter((path) => path && path !== parsed.secondaryPathRaw && path !== parsed.secondaryPath);
  const evidenceLines = selectProjectedToolEvidenceLines(entry, { expanded: true });
  const hasDetails = evidenceLines.length > 0 || extraPaths.length > 0;

  if (!hasDetails) {
    return buildCompactRowContent(parsed, entry, sessionId);
  }

  const details = document.createElement('details');
  details.className = 'agent-history-disclosure agent-tool-row-disclosure';
  details.open = expanded?.has(entry.id) === true;

  const summary = document.createElement('summary');
  summary.className = 'agent-history-disclosure-summary agent-tool-row-summary';
  summary.appendChild(buildCompactRowContent(parsed, entry, sessionId));
  details.appendChild(summary);

  let mounted = false;
  const mountBody = () => {
    if (mounted) return;
    mounted = true;
    const body = document.createElement('div');
    body.className = 'agent-tool-row-body';
    if (extraPaths.length > 0) {
      const pathsEl = document.createElement('div');
      pathsEl.className = 'agent-tool-row-paths';
      pathsEl.textContent = extraPaths.join('\n');
      enrichInteractiveTextContent(pathsEl, []);
      wireAssistantInteractiveContent(pathsEl, sessionId);
      body.appendChild(pathsEl);
    }
    if (evidenceLines.length > 0) {
      const omitted = presentation?.omittedLineCount ?? 0;
      if (omitted > 0) {
        const notice = document.createElement('div');
        notice.className = 'agent-command-omitted-banner';
        notice.textContent = activityText('linesOmitted', '... {count} lines omitted ...').replace(
          '{count}',
          String(omitted),
        );
        body.appendChild(notice);
      }
      const pre = document.createElement('pre');
      pre.className = 'agent-history-command-output-tail';
      pre.textContent = evidenceLines.join('\n');
      body.appendChild(pre);
    }
    details.appendChild(body);
  };

  if (details.open) mountBody();
  details.addEventListener('toggle', () => {
    onToggle(details, summary, () => {
      if (details.open) {
        expanded?.add(entry.id);
        mountBody();
      } else {
        expanded?.delete(entry.id);
      }
    });
  });

  return details;
}

export function createCopyCommandButton(command: string): HTMLButtonElement {
  const btn = document.createElement('button');
  btn.type = 'button';
  btn.className = 'agent-command-copy-btn';
  const copyLabel = activityText('copyCommand', 'Copy');
  const copiedLabel = activityText('copied', 'Copied');
  btn.textContent = copyLabel;
  btn.title = copyLabel;
  btn.setAttribute('aria-label', copyLabel);
  btn.addEventListener('click', (event) => {
    event.preventDefault();
    event.stopPropagation();
    const clipboard = typeof navigator !== 'undefined' ? navigator.clipboard : undefined;
    if (!clipboard) return;
    void clipboard.writeText(command).then(() => {
      btn.textContent = copiedLabel;
      btn.dataset.copied = 'true';
      window.setTimeout(() => {
        btn.textContent = copyLabel;
        delete btn.dataset.copied;
      }, 1400);
    });
  });
  return btn;
}

export function createCommandHistoryBodyElement(
  entry: AppServerControlHistoryEntry,
  command = resolveToolCommandScript(entry) || entry.body,
  outputLines = clampToolCallOutputLines(entry.commandOutputTail ?? [], true),
  omittedLinesCount = 0,
): HTMLElement {
  const body = document.createElement('div');
  body.className = 'agent-history-body agent-history-command-body';
  if (command) {
    const script = Boolean(entry.commandText || entry.toolPresentation?.category === 'command');
    const commandWrap = document.createElement('div');
    commandWrap.className = 'agent-command-block';
    const commandLine = document.createElement('pre');
    commandLine.className = script
      ? 'agent-history-command-script'
      : 'agent-history-command-output-tail';
    commandLine.setAttribute(
      'aria-label',
      t(script ? 'appServerControl.commands.title' : 'appServerControl.panel.details'),
    );
    if (script) appendHighlightedCommand(commandLine, command);
    else commandLine.textContent = command;
    commandWrap.appendChild(commandLine);
    if (script) {
      commandWrap.appendChild(createCopyCommandButton(command));
    }
    body.appendChild(commandWrap);
  }
  if (outputLines.length > 0) {
    const outputWrap = document.createElement('div');
    outputWrap.className = 'agent-command-output-wrap';
    if (omittedLinesCount > 0) {
      const notice = document.createElement('div');
      notice.className = 'agent-command-omitted-banner';
      notice.textContent = activityText('linesOmitted', '... {count} lines omitted ...').replace(
        '{count}',
        String(omittedLinesCount),
      );
      outputWrap.appendChild(notice);
    }
    const output = document.createElement('pre');
    output.className = 'agent-history-command-output-tail';
    output.setAttribute('aria-label', t('appServerControl.title.commandOutput'));
    output.textContent = outputLines.join('\n');
    outputWrap.appendChild(output);
    body.appendChild(outputWrap);
  }

  return body;
}

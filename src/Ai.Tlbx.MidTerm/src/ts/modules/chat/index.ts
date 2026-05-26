/**
 * Chat Module
 *
 * Handles the voice chat panel display, message rendering,
 * and panel visibility state. Tool calls within a turn are
 * consolidated into collapsible group bubbles.
 */

import { createLogger } from '../logging';
import type { ChatMessage, VoiceToolName, InteractiveOp } from '../../types';
import { escapeHtml } from '../../utils';
import { t } from '../i18n';

const log = createLogger('chat');
const STORAGE_KEY = 'midterm.chatPanelOpen';
const MAX_CHAT_ELEMENTS = 69;
const MAX_CHAT_MESSAGES = 200;

const chatMessages: ChatMessage[] = [];
let autoAcceptEnabled = false;

interface ToolEntry {
  toolName: string;
  request: string;
  response: string;
  rowEl: HTMLElement;
}

interface ActiveToolGroup {
  element: HTMLElement;
  entriesContainer: HTMLElement;
  timeEl: HTMLElement;
  entries: ToolEntry[];
}

let activeToolGroup: ActiveToolGroup | null = null;

/**
 * Initialize the chat panel and event handlers
 */
export function initChatPanel(): void {
  const collapseBtn = document.getElementById('btn-collapse-chat');
  if (collapseBtn) {
    collapseBtn.addEventListener('click', hideChatPanel);
  }

  log.info(() => 'Chat panel initialized');
}

/**
 * Show the chat panel
 */
export function showChatPanel(): void {
  const panel = document.getElementById('chat-panel');
  if (panel) {
    panel.classList.remove('hidden');
    localStorage.setItem(STORAGE_KEY, 'true');
    log.info(() => 'Chat panel shown');
  }
}

/**
 * Hide the chat panel
 */
function hideChatPanel(): void {
  const panel = document.getElementById('chat-panel');
  if (panel) {
    panel.classList.add('hidden');
    localStorage.setItem(STORAGE_KEY, 'false');
    log.info(() => 'Chat panel hidden');
  }
}

/**
 * Toggle the chat panel visibility
 */
export function toggleChatPanel(): void {
  const panel = document.getElementById('chat-panel');
  if (panel?.classList.contains('hidden')) {
    showChatPanel();
  } else {
    hideChatPanel();
  }
}

/**
 * Add a chat message and render it.
 * Tool call requests and responses are consolidated into groups.
 */
export function addChatMessage(message: ChatMessage): void {
  chatMessages.push(message);
  trimChatHistory();

  if (message.role === 'assistant' && isToolCallMessage(message.content)) {
    const toolName = extractToolName(message.content);
    const lines = message.content.split('\n');
    const args = lines.slice(1).join('\n').trim();

    if (!activeToolGroup) {
      const group = createToolGroup(message.timestamp);
      const container = document.getElementById('chat-messages');
      if (container) {
        container.appendChild(group.element);
      }
      activeToolGroup = group;
    }

    addToolEntry(activeToolGroup, toolName, args, message.timestamp);
  } else if (message.role === 'tool' && activeToolGroup) {
    const lastEntry = activeToolGroup.entries[activeToolGroup.entries.length - 1];
    if (lastEntry) {
      updateToolEntryResponse(lastEntry, message.content);
    }
  } else {
    finalizeToolGroup();
    renderMessage(message);
  }

  trimChatDom();
  scrollToBottom();
  log.info(() => `Chat message added: ${message.role}`);
}

function trimChatHistory(): void {
  const overflow = chatMessages.length - MAX_CHAT_MESSAGES;
  if (overflow > 0) {
    chatMessages.splice(0, overflow);
  }
}

/**
 * Check if a message is a tool call request from the assistant
 */
function isToolCallMessage(content: string): boolean {
  return content.toLowerCase().startsWith('calling tool:');
}

/**
 * Extract tool name from a tool call message
 */
function extractToolName(content: string): string {
  const match = content.match(/calling tool:\s*(\w+)/i);
  return match ? (match[1] ?? 'unknown') : 'unknown';
}

/**
 * Format JSON content for display
 */
function formatJsonContent(content: string): string {
  try {
    const trimmed = content.trim();
    if (
      (trimmed.startsWith('{') && trimmed.endsWith('}')) ||
      (trimmed.startsWith('[') && trimmed.endsWith(']'))
    ) {
      const parsed: unknown = JSON.parse(trimmed);
      return JSON.stringify(parsed, null, 2);
    }
  } catch {
    // Not valid JSON, return as-is
  }
  return content;
}

/**
 * Condense text to first N lines, adding ellipsis if truncated
 */
function condensed(text: string, maxLines: number): string {
  const lines = text.split('\n');
  if (lines.length <= maxLines) return text;
  return lines.slice(0, maxLines).join('\n') + '\n…';
}

/**
 * Create a tool group bubble element
 */
function createToolGroup(timestamp: string): ActiveToolGroup {
  const el = document.createElement('div');
  el.className = 'chat-msg chat-msg-tool-group';

  const entriesContainer = document.createElement('div');
  entriesContainer.className = 'tool-group-entries';
  el.appendChild(entriesContainer);

  const timeEl = document.createElement('div');
  timeEl.className = 'chat-msg-time';
  timeEl.textContent = formatTime(timestamp);
  el.appendChild(timeEl);

  return { element: el, entriesContainer, timeEl, entries: [] };
}

/**
 * Add a collapsible tool entry row to a group
 */
function addToolEntry(
  group: ActiveToolGroup,
  toolName: string,
  args: string,
  timestamp: string,
): void {
  const entry: ToolEntry = {
    toolName,
    request: args,
    response: '',
    rowEl: document.createElement('div'),
  };
  const row = entry.rowEl;
  row.className = 'tool-group-entry-row';

  const headerRow = document.createElement('div');
  headerRow.className = 'tool-group-entry-header';

  const arrow = document.createElement('span');
  arrow.className = 'tool-group-expand';
  arrow.textContent = '▸';

  const nameEl = document.createElement('span');
  nameEl.className = 'tool-group-entry-name';
  nameEl.textContent = toolName;

  headerRow.appendChild(arrow);
  headerRow.appendChild(nameEl);

  const summaryEl = document.createElement('div');
  summaryEl.className = 'tool-group-entry-summary';

  const detailEl = document.createElement('div');
  detailEl.className = 'tool-group-entry-detail';

  row.appendChild(headerRow);
  row.appendChild(summaryEl);
  row.appendChild(detailEl);

  headerRow.addEventListener('click', () => {
    const isExpanded = row.classList.contains('expanded');
    if (isExpanded) {
      row.classList.remove('expanded', 'fully-expanded');
      arrow.textContent = '▸';
    } else {
      row.classList.add('expanded');
      arrow.textContent = '▾';
      rebuildSummary(entry, summaryEl);
    }
  });

  summaryEl.addEventListener('click', (e) => {
    e.stopPropagation();
    if (!row.classList.contains('fully-expanded')) {
      row.classList.add('fully-expanded');
      rebuildDetail(entry, detailEl);
    } else {
      row.classList.remove('fully-expanded');
    }
  });

  group.entriesContainer.appendChild(row);
  group.entries.push(entry);
  group.timeEl.textContent = formatTime(timestamp);
}

/**
 * Rebuild the summary section (condensed request + response)
 */
function rebuildSummary(entry: ToolEntry, el: HTMLElement): void {
  const reqText = entry.request ? condensed(entry.request, 2) : '';
  const resText = entry.response ? condensed(entry.response, 2) : '';
  let html = '';
  if (reqText) html += `<span class="tool-group-summary-text">${escapeHtml(reqText)}</span>`;
  if (resText) html += `<span class="tool-group-summary-response">${escapeHtml(resText)}</span>`;
  el.innerHTML = html;
}

/**
 * Rebuild the detail section (full pre blocks)
 */
function rebuildDetail(entry: ToolEntry, el: HTMLElement): void {
  const reqFormatted = entry.request ? formatJsonContent(entry.request) : '';
  const resFormatted = entry.response ? formatJsonContent(entry.response) : '';
  let html = '';
  if (reqFormatted) {
    html += `<div class="tool-group-detail-label">${escapeHtml(t('chat.toolRequest'))}</div>`;
    html += `<pre class="chat-msg-tool-args">${escapeHtml(reqFormatted)}</pre>`;
  }
  if (resFormatted) {
    html += `<div class="tool-group-detail-label">${escapeHtml(t('chat.toolResponse'))}</div>`;
    html += `<pre class="chat-msg-tool-result">${escapeHtml(resFormatted)}</pre>`;
  }
  el.innerHTML = html;
}

/**
 * Attach a tool response to an existing entry
 */
function updateToolEntryResponse(entry: ToolEntry, content: string): void {
  entry.response = content;
  const summaryEl = entry.rowEl.querySelector<HTMLElement>('.tool-group-entry-summary');
  if (summaryEl && entry.rowEl.classList.contains('expanded')) {
    rebuildSummary(entry, summaryEl);
  }
  const detailEl = entry.rowEl.querySelector<HTMLElement>('.tool-group-entry-detail');
  if (detailEl && entry.rowEl.classList.contains('fully-expanded')) {
    rebuildDetail(entry, detailEl);
  }
}

/**
 * Close the active tool group so subsequent messages render normally
 */
export function finalizeToolGroup(): void {
  if (activeToolGroup) {
    log.info(() => `Finalized tool group with ${activeToolGroup?.entries.length ?? 0} entries`);
    activeToolGroup = null;
  }
}

/**
 * Remove oldest children from #chat-messages until <= MAX_CHAT_ELEMENTS
 */
function trimChatDom(): void {
  const container = document.getElementById('chat-messages');
  if (!container) return;
  while (container.children.length > MAX_CHAT_ELEMENTS) {
    container.removeChild(container.children[0] as Node);
  }
}

/**
 * Render a single message to the chat panel (non-tool or orphan tool)
 */
function renderMessage(message: ChatMessage): void {
  const container = document.getElementById('chat-messages');
  if (!container) return;

  const msgEl = document.createElement('div');
  const time = formatTime(message.timestamp);

  if (message.role === 'tool') {
    msgEl.className = 'chat-msg chat-msg-tool-response';
    const formattedContent = formatJsonContent(message.content);
    msgEl.innerHTML = `
      <div class="chat-msg-tool-header">
        <span class="chat-msg-tool-icon">🔧</span>
        <span class="chat-msg-tool-name">${escapeHtml(message.toolName || 'tool')}</span>
        <span class="chat-msg-tool-label">${escapeHtml(t('chat.toolResponse'))}</span>
      </div>
      <pre class="chat-msg-tool-result">${escapeHtml(formattedContent)}</pre>
      <div class="chat-msg-time">${time}</div>
    `;
  } else {
    msgEl.className = `chat-msg chat-msg-${message.role}`;
    msgEl.innerHTML = `
      <div class="chat-msg-content">${escapeHtml(message.content)}</div>
      <div class="chat-msg-time">${time}</div>
    `;
  }

  container.appendChild(msgEl);
}

/**
 * Scroll the chat messages to the bottom
 */
function scrollToBottom(): void {
  const container = document.getElementById('chat-messages');
  if (container) {
    container.scrollTop = container.scrollHeight;
  }
}

/**
 * Format ISO timestamp to time string (HH:MM)
 */
function formatTime(timestamp: string): string {
  try {
    const date = new Date(timestamp);
    return date.toLocaleTimeString([], { hour: '2-digit', minute: '2-digit' });
  } catch {
    return '';
  }
}

/**
 * Format a tool request for display
 */
function formatSendPromptToolDisplay(args: Record<string, unknown>): string {
  const sessionId = (args.sessionId as string) || '';
  const text = (args.text as string) || '';
  return `Send prompt to ${sessionId}:\n${text}`;
}

function formatDevBrowserToolDisplay(args: Record<string, unknown>): string {
  const command = (args.command as string) || '';
  const selector = (args.selector as string) || '';
  const value = (args.value as string) || '';
  const target = [args.sessionId as string | undefined, args.previewName as string | undefined]
    .filter(Boolean)
    .join('/');
  const lines = [`Run Dev Browser command${target ? ` on ${target}` : ''}: ${command}`];
  if (selector) lines.push(`Selector: ${selector}`);
  if (value) lines.push(`Value: ${value}`);
  return lines.join('\n');
}

function formatDevBrowserScreenshotToolDisplay(args: Record<string, unknown>): string {
  const target = [args.sessionId as string | undefined, args.previewName as string | undefined]
    .filter(Boolean)
    .join('/');
  return `Capture Dev Browser screenshot${target ? ` on ${target}` : ''}`;
}

function formatRepoMonitorToolDisplay(args: Record<string, unknown>): string {
  const action = (args.action as string) || 'list';
  const sessionId = (args.sessionId as string) || 'active session';
  const path = (args.path as string) || '';
  const repoRoot = (args.repoRoot as string) || '';
  const lines = [`Repo monitor ${action} on ${sessionId}`];
  if (path) lines.push(`Path: ${path}`);
  if (repoRoot) lines.push(`Repo root: ${repoRoot}`);
  return lines.join('\n');
}

function formatLayoutControlToolDisplay(args: Record<string, unknown>): string {
  const action = (args.action as string) || 'status';
  const sessionId = (args.sessionId as string) || '';
  const targetSessionId = (args.targetSessionId as string) || '';
  const otherSessionId = (args.otherSessionId as string) || '';
  const position = (args.position as string) || '';
  const lines = [`Layout control: ${action}`];
  if (sessionId) lines.push(`Session: ${sessionId}`);
  if (targetSessionId) lines.push(`Target: ${targetSessionId}`);
  if (otherSessionId) lines.push(`Other: ${otherSessionId}`);
  if (position) lines.push(`Position: ${position}`);
  return lines.join('\n');
}

function formatMakeInputToolDisplay(args: Record<string, unknown>): string {
  const text = (args.text as string) || '';
  const formatted = formatInputText(text);
  return `Send to terminal:\n${formatted}`;
}

function formatSessionOverviewToolDisplay(args: Record<string, unknown>): string {
  const includeBrowserStatus = args.includeBrowserStatus !== false;
  const includeRepoStatus = args.includeRepoStatus !== false;
  const lines = ['Inspect sessions'];
  lines.push(`Browser: ${includeBrowserStatus ? 'yes' : 'no'}`);
  lines.push(`Repos: ${includeRepoStatus ? 'yes' : 'no'}`);
  return lines.join('\n');
}

function formatInteractiveReadToolDisplay(args: Record<string, unknown>): string {
  const ops = (args.operations as InteractiveOp[] | undefined) ?? [];
  const lines = ops.map((op, i) => {
    if (op.type === 'input') {
      return `${i + 1}. Input: ${formatInputText(op.data || '')}`;
    } else if (op.type === 'delay') {
      return `${i + 1}. Wait ${op.delayMs || 100}ms`;
    }
    return `${i + 1}. Screenshot`;
  });
  return `Interactive sequence:\n${lines.join('\n')}`;
}

const toolDisplayFormatters: Partial<
  Record<VoiceToolName, (args: Record<string, unknown>) => string>
> = {
  send_prompt: formatSendPromptToolDisplay,
  session_overview: formatSessionOverviewToolDisplay,
  dev_browser_command: formatDevBrowserToolDisplay,
  dev_browser_screenshot: formatDevBrowserScreenshotToolDisplay,
  repo_monitor: formatRepoMonitorToolDisplay,
  layout_control: formatLayoutControlToolDisplay,
  make_input: formatMakeInputToolDisplay,
  interactive_read: formatInteractiveReadToolDisplay,
};

function formatToolDisplay(tool: VoiceToolName, args: Record<string, unknown>): string {
  return toolDisplayFormatters[tool]?.(args) ?? `Tool: ${tool}`;
}

/**
 * Format input text with escape sequence visualization
 */
function formatInputText(text: string): string {
  let result = text;
  result = result.split('\r').join('⏎');
  result = result.split('\n').join('↵');
  result = result.split('\t').join('⇥');
  result = result.split(String.fromCharCode(3)).join('^C'); // Ctrl+C
  result = result.split(String.fromCharCode(27) + '[A').join('↑'); // Arrow Up
  result = result.split(String.fromCharCode(27) + '[B').join('↓'); // Arrow Down
  result = result.split(String.fromCharCode(27) + '[C').join('→'); // Arrow Right
  result = result.split(String.fromCharCode(27) + '[D').join('←'); // Arrow Left
  result = result.split(String.fromCharCode(27) + '[5~').join('⇞'); // Page Up
  result = result.split(String.fromCharCode(27) + '[6~').join('⇟'); // Page Down
  result = result.split(String.fromCharCode(27)).join('ESC'); // Remaining ESC
  return result;
}

/**
 * Show a tool confirmation dialog in the chat panel
 * Returns true if approved, false if declined
 */
export function showToolConfirmation(
  tool: VoiceToolName,
  args: Record<string, unknown>,
  justification: string | undefined,
): Promise<boolean> {
  if (autoAcceptEnabled) {
    log.info(() => `Auto-accepting tool: ${tool}`);
    return Promise.resolve(true);
  }

  return new Promise((resolve) => {
    const container = document.getElementById('chat-messages');
    if (!container) {
      log.warn(() => 'Chat container not found, auto-declining');
      resolve(false);
      return;
    }

    finalizeToolGroup();

    const displayText = formatToolDisplay(tool, args);

    const msgEl = document.createElement('div');
    msgEl.className = 'chat-msg chat-msg-tool-confirm';

    msgEl.innerHTML = `
      <div class="chat-msg-tool-header">
        <span class="chat-msg-tool-icon">⚠️</span>
        <span class="chat-msg-tool-title">Action requires approval</span>
      </div>
      <div class="chat-msg-tool-name">${escapeHtml(tool)}</div>
      <pre class="chat-msg-tool-command">${escapeHtml(displayText)}</pre>
      ${justification ? `<div class="chat-msg-tool-justification">${escapeHtml(justification)}</div>` : ''}
      <div class="chat-msg-tool-actions">
        <button class="btn-tool-accept">Accept</button>
        <button class="btn-tool-decline">Decline</button>
        <label class="tool-auto-accept">
          <input type="checkbox" class="tool-auto-accept-check" />
          Auto-accept this session
        </label>
      </div>
    `;

    container.appendChild(msgEl);
    trimChatDom();
    scrollToBottom();

    const acceptBtn = msgEl.querySelector('.btn-tool-accept') as HTMLButtonElement;
    const declineBtn = msgEl.querySelector('.btn-tool-decline') as HTMLButtonElement;
    const autoAcceptCheck = msgEl.querySelector('.tool-auto-accept-check') as HTMLInputElement;

    const handleResponse = (approved: boolean): void => {
      if (autoAcceptCheck.checked) {
        autoAcceptEnabled = true;
        log.info(() => 'Auto-accept enabled for this session');
      }

      msgEl.classList.add(approved ? 'confirmed' : 'declined');
      const actionsEl = msgEl.querySelector('.chat-msg-tool-actions');
      if (actionsEl) {
        actionsEl.innerHTML = approved
          ? '<span class="tool-status accepted">✓ Accepted</span>'
          : '<span class="tool-status declined">✗ Declined</span>';
      }

      log.info(() => `Tool ${tool} ${approved ? 'accepted' : 'declined'}`);
      resolve(approved);
    };

    acceptBtn.addEventListener('click', () => {
      handleResponse(true);
    });
    declineBtn.addEventListener('click', () => {
      handleResponse(false);
    });
  });
}

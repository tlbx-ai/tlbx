/**
 * Command Output Panel
 *
 * Floating resizable/draggable overlay with xterm.js terminal
 * connected to a hidden mthost session via the mux protocol.
 */

import { Terminal } from '@xterm/xterm';
import { FitAddon } from '@xterm/addon-fit';
import { createLogger } from '../logging';
import { t } from '../i18n';
import { sessionTerminals, hiddenSessionIds } from '../../state';
import { forgetMuxSession, sendInput, sendResize } from '../comms/muxChannel';
import { getTerminalOptions } from '../terminal/terminalOptions';
import { syncTerminalWebglState } from '../terminal/manager';
import { syncTerminalLigatureState } from '../terminal/ligatures';
import { shouldUseWebglRenderer } from '../terminal/webglSupport';
import { escapeHtml } from '../../utils';
import { $currentSettings, $isMainBrowser } from '../../stores';

export { hiddenSessionIds } from '../../state';

const log = createLogger('commandOutput');

const OVERLAY_STORAGE_KEY = 'mt-command-overlay-rect';

interface OverlayState {
  sessionId: string;
  overlay: HTMLElement;
  terminal: Terminal;
  fitAddon: FitAddon;
  resizeObserver: ResizeObserver;
}

const activeOverlays = new Map<string, OverlayState>();

function loadOverlayRect(): { x: number; y: number; w: number; h: number } | null {
  try {
    const raw = localStorage.getItem(OVERLAY_STORAGE_KEY);
    if (!raw) return null;
    return JSON.parse(raw) as { x: number; y: number; w: number; h: number };
  } catch {
    return null;
  }
}

function saveOverlayRect(x: number, y: number, w: number, h: number): void {
  try {
    localStorage.setItem(OVERLAY_STORAGE_KEY, JSON.stringify({ x, y, w, h }));
  } catch {
    // localStorage full or unavailable
  }
}

export function showOutputOverlay(hiddenSessionId: string, scriptName: string): void {
  if (activeOverlays.has(hiddenSessionId)) return;

  hiddenSessionIds.add(hiddenSessionId);

  const overlay = document.createElement('div');
  overlay.className = 'command-overlay';

  const saved = loadOverlayRect();
  const w = saved?.w ?? Math.min(600, window.innerWidth - 20);
  const h = saved?.h ?? Math.min(400, window.innerHeight - 20);
  const x = saved?.x ?? Math.max(10, window.innerWidth - w - 50);
  const y = saved?.y ?? 80;

  overlay.style.left = `${x}px`;
  overlay.style.top = `${y}px`;
  overlay.style.width = `${w}px`;
  overlay.style.height = `${h}px`;

  overlay.innerHTML = `
    <div class="command-overlay-header">
      <span class="command-overlay-title">${escapeHtml(scriptName)}</span>
      <button class="command-overlay-close" title="${t('commands.close')}">&times;</button>
    </div>
    <div class="command-overlay-body"></div>`;

  document.body.appendChild(overlay);

  const bodyEl = overlay.querySelector('.command-overlay-body') as HTMLDivElement;

  const terminal = new Terminal({
    ...getTerminalOptions(),
    cols: 120,
    rows: 30,
  });
  const fitAddon = new FitAddon();
  terminal.loadAddon(fitAddon);
  terminal.open(bodyEl);

  const termState = {
    terminal,
    fitAddon,
    container: bodyEl,
    serverCols: 120,
    serverRows: 30,
    opened: true,
    hasWebgl: false,
    webglAddon: null,
    ligatureJoinerId: null,
  };
  sessionTerminals.set(hiddenSessionId, termState);

  const settings = $currentSettings.get();
  if (settings) {
    syncTerminalWebglState(hiddenSessionId, termState, shouldUseWebglRenderer(settings));
    syncTerminalLigatureState(termState, settings.terminalLigaturesEnabled);
  }

  terminal.onData((data: string) => {
    sendInput(hiddenSessionId, data);
  });

  requestAnimationFrame(() => {
    try {
      fitAddon.fit();
      const dims = fitAddon.proposeDimensions();
      if ($isMainBrowser.get() && dims && dims.cols > 0 && dims.rows > 0) {
        sendResize(hiddenSessionId, dims.cols, dims.rows);
        termState.serverCols = dims.cols;
        termState.serverRows = dims.rows;
      }
    } catch {
      // fitAddon may fail if terminal not fully rendered
    }
  });

  const resizeObserver = new ResizeObserver(() => {
    try {
      fitAddon.fit();
      const dims = fitAddon.proposeDimensions();
      if ($isMainBrowser.get() && dims && dims.cols > 0 && dims.rows > 0) {
        sendResize(hiddenSessionId, dims.cols, dims.rows);
        termState.serverCols = dims.cols;
        termState.serverRows = dims.rows;
      }
    } catch {
      // ignore
    }
  });
  resizeObserver.observe(bodyEl);

  const state: OverlayState = {
    sessionId: hiddenSessionId,
    overlay,
    terminal,
    fitAddon,
    resizeObserver,
  };
  activeOverlays.set(hiddenSessionId, state);

  // Close button
  overlay.querySelector('.command-overlay-close')?.addEventListener('click', () => {
    closeOverlay(hiddenSessionId);
  });

  // Drag-to-move via header
  setupDrag(overlay, overlay.querySelector('.command-overlay-header') as HTMLElement);

  // Save position on move/resize
  const mutationSave = () => {
    const rect = overlay.getBoundingClientRect();
    saveOverlayRect(rect.left, rect.top, rect.width, rect.height);
  };
  overlay.addEventListener('mouseup', mutationSave);

  log.info(() => `Opened output overlay for hidden session ${hiddenSessionId}`);
}

export function closeOverlay(hiddenSessionId: string): void {
  const state = activeOverlays.get(hiddenSessionId);
  if (!state) return;

  // Save position before removing
  const rect = state.overlay.getBoundingClientRect();
  saveOverlayRect(rect.left, rect.top, rect.width, rect.height);

  state.resizeObserver.disconnect();
  const terminalState = sessionTerminals.get(hiddenSessionId);
  if (terminalState) {
    syncTerminalWebglState(hiddenSessionId, terminalState, false);
  }
  state.terminal.dispose();
  sessionTerminals.delete(hiddenSessionId);
  forgetMuxSession(hiddenSessionId);
  hiddenSessionIds.delete(hiddenSessionId);
  state.overlay.remove();
  activeOverlays.delete(hiddenSessionId);

  // Close the hidden session on the server
  fetch(`/api/sessions/${encodeURIComponent(hiddenSessionId)}`, { method: 'DELETE' }).catch(
    () => {},
  );

  log.info(() => `Closed output overlay for hidden session ${hiddenSessionId}`);
}

export function closeAllOverlays(): void {
  for (const sessionId of [...activeOverlays.keys()]) {
    closeOverlay(sessionId);
  }
}

function setupDrag(overlay: HTMLElement, handle: HTMLElement): void {
  let startX = 0;
  let startY = 0;
  let startLeft = 0;
  let startTop = 0;
  let dragging = false;

  const onMouseMove = (e: MouseEvent) => {
    if (!dragging) return;
    const dx = e.clientX - startX;
    const dy = e.clientY - startY;
    overlay.style.left = `${startLeft + dx}px`;
    overlay.style.top = `${startTop + dy}px`;
  };

  const onMouseUp = () => {
    dragging = false;
    document.removeEventListener('mousemove', onMouseMove);
    document.removeEventListener('mouseup', onMouseUp);
  };

  handle.addEventListener('mousedown', (e: MouseEvent) => {
    if ((e.target as HTMLElement).tagName === 'BUTTON') return;
    dragging = true;
    startX = e.clientX;
    startY = e.clientY;
    const rect = overlay.getBoundingClientRect();
    startLeft = rect.left;
    startTop = rect.top;
    document.addEventListener('mousemove', onMouseMove);
    document.addEventListener('mouseup', onMouseUp);
    e.preventDefault();
  });
}

/** Browser audio and UI bridge for the tlbx-owned voice orchestrator. */
import { setVoiceStatus, setToggleRecording } from './sidebar/voiceSection';
import {
  addChatMessage,
  showChatPanel,
  toggleChatPanel,
  showToolConfirmation,
  finalizeToolGroup,
} from './chat';
import { processToolRequest } from './voiceTools';
import { createLogger } from './logging';
import { $voiceAvailable } from '../stores';
import type { VoiceToolName } from '../types';

const log = createLogger('voice');

let ws: WebSocket | null = null;
let starting = false;
let generation = 0;
let transcriptRole = '';
let transcriptText = '';
let transcriptTimer: ReturnType<typeof setTimeout> | null = null;
let toolQueue: Promise<void> = Promise.resolve();
const orchestrationTools = new Set<VoiceToolName>([
  'session_overview',
  'session_activity',
  'read_scrollback',
  'select_session',
  'create_session',
  'send_prompt',
  'make_input',
  'close_session',
]);

export async function refreshVoiceAvailability(): Promise<boolean> {
  try {
    const response = await fetch('/api/ai/settings');
    if (!response.ok) throw new Error('Could not read AI settings');
    const data = (await response.json()) as { providers: { id: string; configured: boolean }[] };
    const available = data.providers.some((p) => p.id === 'openai' && p.configured);
    $voiceAvailable.set(available);
    if (!ws)
      setVoiceStatus(available ? 'Ready · GPT-Live 1' : 'Add an OpenAI key in AI & Voice settings');
    return available;
  } catch {
    $voiceAvailable.set(false);
    setVoiceStatus('AI settings unavailable');
    return false;
  }
}

export async function populateMicDropdownPassive(): Promise<void> {
  const select = document.getElementById('mic-select') as HTMLSelectElement | null;
  if (!select) return;
  const selected = select.value;
  const devices = await navigator.mediaDevices.enumerateDevices().catch(() => []);
  select.replaceChildren(new Option('Default microphone', ''));
  for (const device of devices.filter((d) => d.kind === 'audioinput' && d.label))
    select.add(new Option(device.label, device.deviceId));
  select.value = selected;
}

function flushTranscript(): void {
  if (transcriptTimer) clearTimeout(transcriptTimer);
  transcriptTimer = null;
  if (transcriptText && (transcriptRole === 'user' || transcriptRole === 'assistant')) {
    addChatMessage({
      role: transcriptRole,
      content: transcriptText,
      timestamp: new Date().toISOString(),
    });
  }
  transcriptText = '';
  transcriptRole = '';
}

interface VoiceEvent {
  type: string;
  status?: string;
  message?: string;
  role?: 'user' | 'assistant' | 'tool';
  content?: string;
  toolName?: string;
  requestId?: string;
  tool?: VoiceToolName;
  args?: Record<string, unknown>;
  requiresConfirmation?: boolean;
}

function isCurrentSocket(socket: WebSocket): boolean {
  return socket === ws && socket.readyState === WebSocket.OPEN;
}

async function handleTool(event: VoiceEvent, socket: WebSocket): Promise<void> {
  if (!event.tool || !event.requestId || socket !== ws || socket.readyState !== WebSocket.OPEN)
    return;
  if (!orchestrationTools.has(event.tool)) {
    socket.send(
      JSON.stringify({
        type: 'tool_response',
        requestId: event.requestId,
        result: null,
        error: 'Unsupported orchestration tool.',
      }),
    );
    return;
  }
  const args = event.args ?? {};
  const approved =
    !event.requiresConfirmation ||
    (await showToolConfirmation(event.tool, args, args.justification as string | undefined));
  // Confirmation can outlive its conversation. Never execute after that connection has ended.
  if (!isCurrentSocket(socket)) return;
  addChatMessage({
    role: 'assistant',
    content: `Calling tool: ${event.tool}\n${JSON.stringify(args, null, 2)}`,
    toolRequestId: event.requestId,
    timestamp: new Date().toISOString(),
  });
  const response = approved
    ? await processToolRequest({
        type: 'tool_request',
        requestId: event.requestId,
        tool: event.tool,
        args,
      })
    : { type: 'tool_response', requestId: event.requestId, result: null, declined: true };
  addChatMessage({
    role: 'tool',
    content: JSON.stringify(response),
    toolName: event.tool,
    toolRequestId: event.requestId,
    timestamp: new Date().toISOString(),
  });
  if (isCurrentSocket(socket)) socket.send(JSON.stringify(response));
}

function renderVoiceMessage(event: VoiceEvent): void {
  if (!event.role || !event.content) return;
  if (event.type === 'transcript') {
    if (transcriptRole !== event.role) flushTranscript();
    transcriptRole = event.role;
    transcriptText += event.content;
    if (transcriptTimer) clearTimeout(transcriptTimer);
    transcriptTimer = setTimeout(flushTranscript, 900);
  } else {
    flushTranscript();
    addChatMessage({
      role: event.role,
      content: event.content,
      ...(event.toolName ? { toolName: event.toolName } : {}),
      timestamp: new Date().toISOString(),
    });
  }
}

function onMessage(event: VoiceEvent, socket: WebSocket): void {
  if (event.type === 'ready') void beginCapture(socket);
  else if (event.type === 'status') setVoiceStatus(event.status ?? 'Working');
  else if (event.type === 'transcript' || event.type === 'chat') renderVoiceMessage(event);
  else if (event.type === 'clear_audio') void window.stopAudioPlayback?.();
  else if (event.type === 'tool_request') {
    toolQueue = toolQueue
      .then(() => handleTool(event, socket))
      .catch(() => {
        if (socket === ws) void stopVoiceSession('Tool execution failed');
      });
  } else if (event.type === 'error') void stopVoiceSession(event.message ?? 'Voice error');
}

async function beginCapture(socket: WebSocket): Promise<void> {
  const microphone =
    (document.getElementById('mic-select') as HTMLSelectElement | null)?.value || null;
  try {
    const success = await window.startRecording?.(
      (audio: string) => {
        if (socket === ws && socket.readyState === WebSocket.OPEN) {
          if (socket.bufferedAmount > 1024 * 1024) {
            void stopVoiceSession('Audio connection is too slow');
            return;
          }
          socket.send(Uint8Array.from(atob(audio), (c) => c.charCodeAt(0)));
        }
      },
      100,
      microphone,
      24000,
    );
    if (socket !== ws) {
      await window.stopRecording?.();
      return;
    }
    if (!success) throw new Error('Microphone could not be started');
    starting = false;
    setToggleRecording(true);
    setVoiceStatus('Listening · GPT-Live 1');
    await populateMicDropdownPassive();
  } catch {
    await stopVoiceSession('Microphone could not be started');
  }
}

export async function startVoiceSession(): Promise<void> {
  if (ws || starting) return;
  starting = true;
  const current = ++generation;
  setVoiceStatus('Connecting…');
  try {
    if (!(await window.initAudioWithUserInteraction?.()))
      throw new Error('Audio initialization failed');
    if (current !== generation) return;
    if (!(await refreshVoiceAvailability()))
      throw new Error('Add an OpenAI key in AI & Voice settings');
    if (current !== generation) return;
    const socket = new WebSocket(
      `${location.protocol === 'https:' ? 'wss:' : 'ws:'}//${location.host}/ws/voice`,
    );
    socket.binaryType = 'arraybuffer';
    ws = socket;
    toolQueue = Promise.resolve();
    const timeout = setTimeout(() => {
      if (starting && socket === ws) void stopVoiceSession('Voice connection timed out');
    }, 30000);
    socket.onopen = () => {
      if (socket !== ws) return;
      showChatPanel();
      const voice =
        (document.getElementById('voice-select') as HTMLSelectElement | null)?.value || 'marin';
      socket.send(JSON.stringify({ type: 'start', voice }));
    };
    socket.onmessage = (message: MessageEvent<string | ArrayBuffer>) => {
      if (socket !== ws) return;
      if (message.data instanceof ArrayBuffer) {
        let binary = '';
        for (const byte of new Uint8Array(message.data)) binary += String.fromCharCode(byte);
        void window.playAudio?.(btoa(binary), 24000);
      } else {
        try {
          onMessage(JSON.parse(message.data) as VoiceEvent, socket);
        } catch {
          void stopVoiceSession('Invalid voice response');
        }
      }
    };
    socket.onclose = () => {
      clearTimeout(timeout);
      if (socket === ws) void stopVoiceSession('Disconnected');
    };
    socket.onerror = () => {
      clearTimeout(timeout);
      if (socket === ws) void stopVoiceSession('Connection failed · check AI & Voice settings');
    };
  } catch (error) {
    if (current === generation)
      await stopVoiceSession(error instanceof Error ? error.message : 'Connection failed');
  }
}

export async function stopVoiceSession(status = 'Ready'): Promise<void> {
  generation++;
  starting = false;
  const socket = ws;
  ws = null;
  if (socket?.readyState === WebSocket.OPEN) socket.send(JSON.stringify({ type: 'stop' }));
  socket?.close();
  await window.stopRecording?.();
  await window.stopAudioPlayback?.();
  flushTranscript();
  finalizeToolGroup();
  setToggleRecording(false);
  setVoiceStatus(status);
}

export function bindVoiceEvents(): void {
  document.getElementById('btn-voice-toggle')?.addEventListener('click', () => {
    if (ws || starting) void stopVoiceSession();
    else void startVoiceSession();
  });
  document.getElementById('btn-voice-chat')?.addEventListener('click', toggleChatPanel);
  window.addEventListener('pagehide', () => {
    void stopVoiceSession();
  });
}

export async function initVoiceControls(): Promise<void> {
  try {
    await refreshVoiceAvailability();
    await populateMicDropdownPassive();
  } catch (error) {
    log.warn(() => `Voice controls initialization failed: ${String(error)}`);
    setVoiceStatus('Voice controls unavailable');
  }
}

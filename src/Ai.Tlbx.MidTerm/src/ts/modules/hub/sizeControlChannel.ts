import type { TerminalSizeControlCommandResult, TerminalSizeControlStatus } from '../../types';
import type { HubMachineState } from './types';
import { createWsUrl } from '../../utils';
import {
  removeTerminalSizeControlSource,
  setTerminalSizeControl,
  setTerminalSizeControlsForSource,
} from '../../stores';

interface PendingCommand {
  resolve: (value: TerminalSizeControlCommandResult) => void;
  reject: (error: Error) => void;
  timeout: ReturnType<typeof setTimeout>;
}

interface HubSizeControlChannel {
  machineId: string;
  socket: WebSocket;
  ready: Promise<HubSizeControlChannel>;
  resolveReady: (channel: HubSizeControlChannel) => void;
  rejectReady: (error: Error) => void;
  pending: Map<string, PendingCommand>;
}

interface ParsedHubSessionId {
  machineId: string;
  remoteSessionId: string;
}

const channels = new Map<string, HubSizeControlChannel>();
const desiredMachineIds = new Set<string>();
const reconnectTimers = new Map<string, ReturnType<typeof setTimeout>>();
let commandSequence = 0;

function sourceKey(machineId: string): string {
  return `hub:${machineId}`;
}

function toCompositeId(machineId: string, remoteSessionId: string): string {
  return `hub:${machineId}:${remoteSessionId}`;
}

function parseCompositeId(sessionId: string): ParsedHubSessionId | null {
  if (!sessionId.startsWith('hub:')) return null;
  const machineEnd = sessionId.indexOf(':', 4);
  if (machineEnd < 5 || machineEnd >= sessionId.length - 1) return null;
  return {
    machineId: sessionId.slice(4, machineEnd),
    remoteSessionId: sessionId.slice(machineEnd + 1),
  };
}

function translateStatus(
  machineId: string,
  status: TerminalSizeControlStatus,
): TerminalSizeControlStatus {
  return { ...status, sessionId: toCompositeId(machineId, status.sessionId) };
}

function translateResult(
  machineId: string,
  result: TerminalSizeControlCommandResult,
): TerminalSizeControlCommandResult {
  return { ...result, status: translateStatus(machineId, result.status) };
}

function rejectPending(channel: HubSizeControlChannel, error: Error): void {
  channel.pending.forEach((pending) => {
    globalThis.clearTimeout(pending.timeout);
    pending.reject(error);
  });
  channel.pending.clear();
}

function scheduleReconnect(machineId: string): void {
  if (!desiredMachineIds.has(machineId) || reconnectTimers.has(machineId)) return;
  reconnectTimers.set(
    machineId,
    globalThis.setTimeout(() => {
      reconnectTimers.delete(machineId);
      void ensureChannel(machineId).catch(() => {
        scheduleReconnect(machineId);
      });
    }, 2000),
  );
}

function handleMessage(channel: HubSizeControlChannel, event: MessageEvent): void {
  if (typeof event.data !== 'string') return;

  let message: {
    type?: string;
    id?: string;
    success?: boolean;
    data?: TerminalSizeControlCommandResult;
    error?: string;
    terminalSizeControls?: TerminalSizeControlStatus[];
  };
  try {
    message = JSON.parse(event.data) as typeof message;
  } catch {
    return;
  }

  if (message.type === 'response' && message.id) {
    const pending = channel.pending.get(message.id);
    if (!pending) return;
    channel.pending.delete(message.id);
    globalThis.clearTimeout(pending.timeout);
    if (message.success && message.data?.status) {
      const result = translateResult(channel.machineId, message.data);
      setTerminalSizeControl(result.status);
      pending.resolve(result);
    } else {
      pending.reject(new Error(message.error || 'Remote size-control command failed.'));
    }
    return;
  }

  if (message.terminalSizeControls !== undefined) {
    setTerminalSizeControlsForSource(
      sourceKey(channel.machineId),
      message.terminalSizeControls.map((status) => translateStatus(channel.machineId, status)),
    );
  }
}

function createChannel(machineId: string): HubSizeControlChannel {
  const params = new URLSearchParams({ machineId });
  const socket = new WebSocket(createWsUrl(`/ws/hub/state?${params.toString()}`));
  let resolveReady!: (channel: HubSizeControlChannel) => void;
  let rejectReady!: (error: Error) => void;
  const ready = new Promise<HubSizeControlChannel>((resolve, reject) => {
    resolveReady = resolve;
    rejectReady = reject;
  });
  const channel: HubSizeControlChannel = {
    machineId,
    socket,
    ready,
    resolveReady,
    rejectReady,
    pending: new Map(),
  };

  socket.onopen = () => {
    channel.resolveReady(channel);
  };
  socket.onmessage = (event) => {
    handleMessage(channel, event);
  };
  socket.onerror = () => {
    if (socket.readyState !== WebSocket.OPEN) {
      channel.rejectReady(new Error('Remote size-control channel failed to connect.'));
    }
  };
  socket.onclose = () => {
    if (channels.get(machineId) === channel) channels.delete(machineId);
    const error = new Error('Remote size-control channel disconnected.');
    channel.rejectReady(error);
    rejectPending(channel, error);
    scheduleReconnect(machineId);
  };
  return channel;
}

async function ensureChannel(machineId: string): Promise<HubSizeControlChannel> {
  const existing = channels.get(machineId);
  if (existing?.socket.readyState === WebSocket.OPEN) return existing;
  if (existing?.socket.readyState === WebSocket.CONNECTING) return existing.ready;

  const channel = createChannel(machineId);
  channels.set(machineId, channel);
  return channel.ready;
}

function closeChannel(machineId: string): void {
  desiredMachineIds.delete(machineId);
  const timer = reconnectTimers.get(machineId);
  if (timer !== undefined) {
    globalThis.clearTimeout(timer);
    reconnectTimers.delete(machineId);
  }
  const channel = channels.get(machineId);
  channels.delete(machineId);
  if (channel) {
    channel.socket.onclose = null;
    channel.socket.close();
    rejectPending(channel, new Error('Remote size-control channel closed.'));
  }
  removeTerminalSizeControlSource(sourceKey(machineId));
}

export function syncHubSizeControlMachines(machines: HubMachineState[]): void {
  const next = new Set(
    machines
      .filter(
        (machine) =>
          machine.machine.enabled &&
          machine.status === 'online' &&
          !machine.requiresTrust &&
          !machine.fingerprintMismatch,
      )
      .map((machine) => machine.machine.id),
  );

  Array.from(desiredMachineIds).forEach((machineId) => {
    if (!next.has(machineId)) closeChannel(machineId);
  });
  next.forEach((machineId) => {
    desiredMachineIds.add(machineId);
    void ensureChannel(machineId).catch(() => {
      scheduleReconnect(machineId);
    });
  });
}

async function sendCommand(
  sessionId: string,
  action: 'terminal.requestSizeControl' | 'terminal.resize',
  payload: Record<string, unknown>,
): Promise<TerminalSizeControlCommandResult> {
  const parsed = parseCompositeId(sessionId);
  if (!parsed) throw new Error('Invalid Hub terminal session ID.');
  desiredMachineIds.add(parsed.machineId);
  const channel = await ensureChannel(parsed.machineId);
  const id = `hub-size-${++commandSequence}`;

  return new Promise<TerminalSizeControlCommandResult>((resolve, reject) => {
    const timeout = globalThis.setTimeout(() => {
      channel.pending.delete(id);
      reject(new Error('Remote size-control command timed out.'));
    }, 5000);
    channel.pending.set(id, { resolve, reject, timeout });
    channel.socket.send(
      JSON.stringify({
        type: 'command',
        id,
        action,
        payload: { ...payload, sessionId: parsed.remoteSessionId },
      }),
    );
  });
}

export function requestHubTerminalSizeControl(
  sessionId: string,
  force: boolean,
): Promise<TerminalSizeControlCommandResult> {
  return sendCommand(sessionId, 'terminal.requestSizeControl', { force });
}

export function resizeHubTerminalWithControl(
  sessionId: string,
  cols: number,
  rows: number,
  expectedEpoch: number,
): Promise<TerminalSizeControlCommandResult> {
  return sendCommand(sessionId, 'terminal.resize', { cols, rows, expectedEpoch });
}

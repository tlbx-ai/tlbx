import {
  MUX_HEADER_SIZE,
  MUX_TYPE_BUFFER_REQUEST,
  MUX_TYPE_COMPRESSED_OUTPUT,
  MUX_TYPE_INPUT,
  MUX_TYPE_OUTPUT,
  MUX_TYPE_RESYNC,
} from '../../constants';
import { createWsUrl, parseCompressedOutputFrame, parseOutputFrame } from '../../utils';
import { sessionTerminals } from '../../state';
import { getHubSessionRecord } from './runtime';
import { applyOutputFrameToTerminal, getBrowserTransportSnapshot } from '../comms/muxChannel';
import { getResumeSequence } from '../comms/muxResumeCursor';

let hubSocket: WebSocket | null = null;
let activeCompositeId: string | null = null;
let hubSuspendedForBrowserBackground = false;
let hubReconnectTimer: number | null = null;
const pendingHubInputs: Array<{ sessionId: string; data: string; userInput: boolean }> = [];
const MAX_PENDING_HUB_INPUTS = 100;
const HUB_RECONNECT_DELAY_MS = 1000;

function encodeSessionId(buffer: Uint8Array, offset: number, sessionId: string): void {
  for (let i = 0; i < 8; i++) {
    buffer[offset + i] = i < sessionId.length ? sessionId.charCodeAt(i) : 0;
  }
}

function closeHubSocket(): void {
  const socket = hubSocket;
  hubSocket = null;
  if (socket) {
    socket.onopen = null;
    socket.onmessage = null;
    socket.onclose = null;
    try {
      socket.close();
    } catch {
      // ignore
    }
  }
}

function cancelHubReconnect(): void {
  if (hubReconnectTimer === null) return;
  window.clearTimeout(hubReconnectTimer);
  hubReconnectTimer = null;
}

function scheduleHubReconnect(): void {
  if (
    hubSuspendedForBrowserBackground ||
    activeCompositeId === null ||
    hubReconnectTimer !== null
  ) {
    return;
  }

  hubReconnectTimer = window.setTimeout(() => {
    hubReconnectTimer = null;
    if (activeCompositeId !== null) attachHubChannel(activeCompositeId);
  }, HUB_RECONNECT_DELAY_MS);
}

async function handleOutputFrame(compositeId: string, data: Uint8Array): Promise<void> {
  if (activeCompositeId !== compositeId) {
    return;
  }

  const state = sessionTerminals.get(compositeId);
  if (!state) {
    return;
  }

  const type = data[0];
  if (type === 0xff) {
    return;
  }

  if (type !== MUX_TYPE_OUTPUT && type !== MUX_TYPE_COMPRESSED_OUTPUT && type !== MUX_TYPE_RESYNC) {
    return;
  }

  if (type === MUX_TYPE_RESYNC) {
    state.terminal.clear();
    return;
  }

  const payload = data.subarray(MUX_HEADER_SIZE);
  if (type === MUX_TYPE_COMPRESSED_OUTPUT) {
    const frame = await parseCompressedOutputFrame(payload);
    if (activeCompositeId !== compositeId || sessionTerminals.get(compositeId) !== state) return;
    applyOutputFrameToTerminal(
      compositeId,
      state,
      frame.sequenceEnd,
      frame.cols,
      frame.rows,
      frame.data,
    );
    return;
  }

  const frame = parseOutputFrame(payload);
  applyOutputFrameToTerminal(
    compositeId,
    state,
    frame.sequenceEnd,
    frame.cols,
    frame.rows,
    frame.data,
  );
}

export function detachHubChannel(sessionId?: string): void {
  if (!sessionId || activeCompositeId === sessionId) {
    activeCompositeId = null;
    pendingHubInputs.length = 0;
    cancelHubReconnect();
    closeHubSocket();
  }
}

export function attachHubChannel(compositeId: string): void {
  const record = getHubSessionRecord(compositeId);
  if (!record) {
    return;
  }

  if (
    activeCompositeId === compositeId &&
    (hubSocket?.readyState === WebSocket.OPEN || hubSocket?.readyState === WebSocket.CONNECTING)
  ) {
    return;
  }

  if (activeCompositeId !== compositeId) pendingHubInputs.length = 0;
  activeCompositeId = compositeId;
  cancelHubReconnect();
  closeHubSocket();
  if (hubSuspendedForBrowserBackground) return;

  const params = new URLSearchParams({
    machineId: record.machineId,
    sessionId: record.remoteSessionId,
  });
  const resumeSequence = getResumeSequence(getBrowserTransportSnapshot(compositeId) ?? undefined);
  if (resumeSequence !== null && resumeSequence > 0n) {
    params.set('resumeSequence', resumeSequence.toString());
  }
  const ws = new WebSocket(createWsUrl(`/ws/hub/mux?${params.toString()}`));
  ws.binaryType = 'arraybuffer';
  ws.onopen = () => {
    if (hubSocket !== ws || activeCompositeId !== compositeId) return;
    const queued = pendingHubInputs.splice(0);
    queued.forEach((input) => {
      if (input.sessionId === compositeId)
        sendHubInputFrame(input.sessionId, input.data, input.userInput);
    });
  };
  ws.onmessage = (event) => {
    if (hubSocket !== ws || activeCompositeId !== compositeId) return;
    if (!(event.data instanceof ArrayBuffer)) {
      return;
    }

    void handleOutputFrame(compositeId, new Uint8Array(event.data));
  };
  ws.onclose = () => {
    if (hubSocket !== ws || activeCompositeId !== compositeId) return;
    hubSocket = null;
    scheduleHubReconnect();
  };
  hubSocket = ws;
}

function sendFrame(frame: Uint8Array): void {
  if (!hubSocket || hubSocket.readyState !== WebSocket.OPEN) {
    return;
  }

  hubSocket.send(frame);
}

function sendHubInputFrame(sessionId: string, data: string, userInput = true): void {
  const record = getHubSessionRecord(sessionId);
  if (!record) {
    return;
  }

  const payload = new TextEncoder().encode(data);
  const frame = new Uint8Array(MUX_HEADER_SIZE + payload.length);
  frame[0] = userInput ? MUX_TYPE_INPUT : 0x02;
  encodeSessionId(frame, 1, record.remoteSessionId);
  frame.set(payload, MUX_HEADER_SIZE);
  sendFrame(frame);
}

export function sendHubInput(sessionId: string, data: string, userInput = true): void {
  if (hubSocket?.readyState === WebSocket.OPEN && activeCompositeId === sessionId) {
    sendHubInputFrame(sessionId, data, userInput);
    return;
  }

  if (pendingHubInputs.length >= MAX_PENDING_HUB_INPUTS) pendingHubInputs.shift();
  pendingHubInputs.push({ sessionId, data, userInput });
  if (!hubSuspendedForBrowserBackground) attachHubChannel(sessionId);
}

export function suspendHubChannelForBrowserBackground(): void {
  if (hubSuspendedForBrowserBackground) return;
  hubSuspendedForBrowserBackground = true;
  cancelHubReconnect();
  closeHubSocket();
}

export function recoverHubChannelAfterBrowserResume(): void {
  hubSuspendedForBrowserBackground = false;
  if (activeCompositeId !== null) attachHubChannel(activeCompositeId);
}

export function requestHubBufferRefresh(sessionId: string): void {
  const record = getHubSessionRecord(sessionId);
  if (!record) {
    return;
  }

  const frame = new Uint8Array(MUX_HEADER_SIZE);
  frame[0] = MUX_TYPE_BUFFER_REQUEST;
  encodeSessionId(frame, 1, record.remoteSessionId);
  sendFrame(frame);
}

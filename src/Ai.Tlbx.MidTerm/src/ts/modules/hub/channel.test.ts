import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

const mocks = vi.hoisted(() => ({
  applyOutputFrameToTerminal: vi.fn(),
  sessionTerminals: new Map<string, unknown>(),
}));

vi.mock('../../state', () => ({
  sessionTerminals: mocks.sessionTerminals,
}));

vi.mock('./runtime', () => ({
  getHubSessionRecord: (sessionId: string) =>
    sessionId === 'hub:machine-a:session-a'
      ? { machineId: 'machine-a', remoteSessionId: 'session-a' }
      : null,
}));

vi.mock('../comms/muxChannel', () => ({
  applyOutputFrameToTerminal: mocks.applyOutputFrameToTerminal,
  getBrowserTransportSnapshot: () => ({ receivedSeq: 45n, submittedSeq: 42n, renderedSeq: 40n }),
}));

vi.mock('../../utils', () => ({
  createWsUrl: (path: string) => path,
  parseCompressedOutputFrame: vi.fn(),
  parseOutputFrame: vi.fn(),
}));

class FakeWebSocket {
  static readonly CONNECTING = 0;
  static readonly OPEN = 1;
  static readonly CLOSED = 3;

  readonly sent: Uint8Array[] = [];
  readyState = FakeWebSocket.CONNECTING;
  binaryType = '';
  onopen: (() => void) | null = null;
  onmessage: ((event: MessageEvent) => void) | null = null;
  onclose: (() => void) | null = null;

  constructor(readonly url: string) {
    sockets.push(this);
  }

  open(version = 3): void {
    this.readyState = FakeWebSocket.OPEN;
    this.onopen?.();
    if (version) this.initialize(version);
  }

  initialize(version: number): void {
    const frame = new Uint8Array(11);
    frame[0] = 0xff;
    new DataView(frame.buffer).setUint16(9, version, true);
    this.onmessage?.({ data: frame.buffer } as MessageEvent);
  }

  send(frame: Uint8Array): void {
    this.sent.push(frame);
  }

  close(): void {
    this.readyState = FakeWebSocket.CLOSED;
    this.onclose?.();
  }

  disconnect(): void {
    this.close();
  }
}

const sockets: FakeWebSocket[] = [];

import {
  attachHubChannel,
  detachHubChannel,
  recoverHubChannelAfterBrowserResume,
  sendHubInput,
  suspendHubChannelForBrowserBackground,
} from './channel';

describe('Hub terminal browser lifecycle', () => {
  beforeEach(() => {
    vi.useFakeTimers();
    sockets.length = 0;
    mocks.applyOutputFrameToTerminal.mockClear();
    vi.stubGlobal('WebSocket', FakeWebSocket);
    vi.stubGlobal('window', {
      setTimeout: globalThis.setTimeout.bind(globalThis),
      clearTimeout: globalThis.clearTimeout.bind(globalThis),
    });
  });

  afterEach(() => {
    detachHubChannel();
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('suspends hidden output, reconnects on resume, and flushes early input', () => {
    attachHubChannel('hub:machine-a:session-a');
    expect(sockets).toHaveLength(1);
    expect(sockets[0]!.url).toContain('resumeSequence=42');
    sockets[0]!.open();

    suspendHubChannelForBrowserBackground();
    expect(sockets[0]!.readyState).toBe(FakeWebSocket.CLOSED);

    sendHubInput('hub:machine-a:session-a', 'queued');
    expect(sockets).toHaveLength(1);

    recoverHubChannelAfterBrowserResume();
    expect(sockets).toHaveLength(2);
    expect(sockets[1]!.url).toContain('machineId=machine-a');
    sockets[1]!.open();

    expect(sockets[1]!.sent).toHaveLength(1);
    expect(sockets[1]!.sent[0]![0]).toBe(0x15);
    expect(new TextDecoder().decode(sockets[1]!.sent[0]!.subarray(9))).toBe('queued');
  });

  it('waits for capabilities and separates replay, parser replies and current typing', () => {
    attachHubChannel('hub:machine-a:session-a');
    sockets[0]!.open(0);
    sendHubInput('hub:machine-a:session-a', 'old Ä');
    expect(sockets[0]!.sent).toHaveLength(0);
    sockets[0]!.initialize(3);
    expect(sockets[0]!.sent[0]![0]).toBe(0x15);
    expect(new TextDecoder().decode(sockets[0]!.sent[0]!.subarray(9))).toBe('old Ä');
    sendHubInput('hub:machine-a:session-a', '\x1b[1;1R', false);
    expect(sockets[0]!.sent[1]![0]).toBe(0x02);
    sendHubInput('hub:machine-a:session-a', 'now');
    expect(sockets[0]!.sent[2]![0]).toBe(0x14);
  });

  it('preserves queued bytes for a remote host advertising the old protocol', () => {
    attachHubChannel('hub:machine-a:session-a');
    sendHubInput('hub:machine-a:session-a', 'legacy');
    sockets[0]!.open(2);
    expect(sockets[0]!.sent[0]![0]).toBe(0x14);
    expect(new TextDecoder().decode(sockets[0]!.sent[0]!.subarray(9))).toBe('legacy');
  });

  it('reconnects an unexpectedly closed foreground channel once', () => {
    attachHubChannel('hub:machine-a:session-a');
    sockets[0]!.open();
    sockets[0]!.disconnect();

    vi.advanceTimersByTime(999);
    expect(sockets).toHaveLength(1);
    vi.advanceTimersByTime(1);
    expect(sockets).toHaveLength(2);
  });
});

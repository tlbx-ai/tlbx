import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import * as constants from '../../constants';
import * as state from '../../state';
import * as stores from '../../stores';
import {
  probeMuxWebSocket,
  connectMuxWebSocket,
  decodeSessionId,
  encodeSessionId,
  forgetMuxSession,
  getBrowserTransportSnapshot,
  isBracketedPasteEnabled,
  recoverVisibleTerminalsAfterBrowserResume,
  requestBufferRefresh,
  restartStalledSessionRecovery,
  resetMuxChannelRuntimeForTests,
  sendInput,
  setInputLatencyTracingEnabled,
  setTerminalParseStallHandler,
  suspendMuxForBrowserBackground,
  updateTerminalVisibility,
} from './muxChannel';

vi.mock('../logging', () => ({
  createLogger: () => ({
    info: vi.fn(),
    warn: vi.fn(),
    error: vi.fn(),
    verbose: vi.fn(),
  }),
}));

vi.mock('../process', () => ({
  handleForegroundChange: vi.fn(),
}));

vi.mock('../terminal/fileLinks', () => ({
  scanOutputForPaths: vi.fn(),
}));

vi.mock('../terminal/scaling', () => ({
  applyTerminalScaling: vi.fn(),
}));

vi.mock('../share', () => ({
  isSharedSessionRoute: () => false,
}));

vi.mock('./stateChannel', () => ({
  handleStateUpdate: vi.fn(),
  reportTerminalSizeInteraction: vi.fn(),
  resizeTerminalWithControl: vi.fn(),
}));

vi.mock('../../api/client', () => ({
  getSessions: vi.fn(),
}));

vi.mock('../../utils', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../utils')>();
  return {
    ...actual,
    checkVersionAndReload: vi.fn().mockResolvedValue(undefined),
    closeWebSocket: actual.closeWebSocket,
    createWsUrl: (path: string) => `ws://midterm.test${path}`,
  };
});

class MockWebSocket extends EventTarget {
  public static readonly CONNECTING = 0;
  public static readonly OPEN = 1;
  public static readonly CLOSING = 2;
  public static readonly CLOSED = 3;
  public static instances: MockWebSocket[] = [];

  public readonly url: string;
  public binaryType = 'blob';
  public readyState = MockWebSocket.OPEN;
  public onopen: ((event: Event) => void) | null = null;
  public onmessage: ((event: MessageEvent<ArrayBuffer>) => void) | null = null;
  public onclose: ((event: CloseEvent) => void) | null = null;
  public onerror: ((event: Event) => void) | null = null;
  public send = vi.fn();
  public close = vi.fn(() => {
    this.readyState = MockWebSocket.CLOSED;
  });

  public constructor(url: string) {
    super();
    this.url = url;
    MockWebSocket.instances.push(this);
  }
}

interface Harness {
  encodeSessionId: typeof encodeSessionId;
  decodeSessionId: typeof decodeSessionId;
  updateTerminalVisibility: typeof updateTerminalVisibility;
  recoverVisibleTerminalsAfterBrowserResume: typeof recoverVisibleTerminalsAfterBrowserResume;
  sessionTerminals: (typeof import('../../state'))['sessionTerminals'];
  stores: typeof stores;
  constants: typeof constants;
  ws: MockWebSocket;
}

interface FakeTerminalHarness {
  pendingCallbacks: Array<() => void>;
  writeMock: ReturnType<typeof vi.fn>;
}

function buildOutputMessage(
  encodeSessionId: (buffer: Uint8Array, offset: number, sessionId: string) => void,
  outputType: number,
  headerSize: number,
  sessionId: string,
  text: string,
  cols = 80,
  rows = 24,
): ArrayBuffer {
  const payload = new TextEncoder().encode(text);
  return buildSequencedOutputMessage(
    encodeSessionId,
    outputType,
    headerSize,
    sessionId,
    BigInt(payload.length),
    text,
    cols,
    rows,
  );
}

function buildSequencedOutputMessage(
  encodeSessionId: (buffer: Uint8Array, offset: number, sessionId: string) => void,
  outputType: number,
  headerSize: number,
  sessionId: string,
  sequenceEnd: bigint,
  text: string,
  cols = 80,
  rows = 24,
): ArrayBuffer {
  const payload = new TextEncoder().encode(text);
  const frame = new Uint8Array(headerSize + 12 + payload.length);
  const view = new DataView(frame.buffer);
  frame[0] = outputType;
  encodeSessionId(frame, 1, sessionId);
  view.setBigUint64(headerSize, sequenceEnd, true);
  frame[headerSize + 8] = cols & 0xff;
  frame[headerSize + 9] = (cols >> 8) & 0xff;
  frame[headerSize + 10] = rows & 0xff;
  frame[headerSize + 11] = (rows >> 8) & 0xff;
  frame.set(payload, headerSize + 12);
  return frame.buffer;
}

function buildDataLossMessage(
  encodeSessionId: (buffer: Uint8Array, offset: number, sessionId: string) => void,
  dataLossType: number,
  headerSize: number,
  sessionId: string,
  droppedBytes: number,
): ArrayBuffer {
  const frame = new Uint8Array(headerSize + 5);
  const view = new DataView(frame.buffer);
  frame[0] = dataLossType;
  encodeSessionId(frame, 1, sessionId);
  frame[headerSize] = 0;
  view.setUint32(headerSize + 1, droppedBytes, true);
  return frame.buffer;
}

function buildRecoveryBeginMessage(
  encodeSessionId: (buffer: Uint8Array, offset: number, sessionId: string) => void,
  recoveryBeginType: number,
  headerSize: number,
  sessionId: string,
  generation: number,
  sequenceStart: bigint,
  sourceSequenceEndExclusive: bigint,
  resetTerminal = false,
  alternateScreenMode = 0,
): ArrayBuffer {
  const frame = new Uint8Array(headerSize + 24);
  const view = new DataView(frame.buffer);
  frame[0] = recoveryBeginType;
  encodeSessionId(frame, 1, sessionId);
  view.setUint32(headerSize, generation, true);
  frame[headerSize + 4] = resetTerminal ? 1 : 0;
  frame[headerSize + 5] = 7;
  view.setBigUint64(headerSize + 6, sequenceStart, true);
  view.setBigUint64(headerSize + 14, sourceSequenceEndExclusive, true);
  view.setUint16(headerSize + 22, alternateScreenMode, true);
  return frame.buffer;
}

function buildRecoveryEndMessage(
  encodeSessionId: (buffer: Uint8Array, offset: number, sessionId: string) => void,
  recoveryEndType: number,
  headerSize: number,
  sessionId: string,
  generation: number,
  sourceSequenceEndExclusive: bigint,
  replayBytes: number,
): ArrayBuffer {
  const frame = new Uint8Array(headerSize + 16);
  const view = new DataView(frame.buffer);
  frame[0] = recoveryEndType;
  encodeSessionId(frame, 1, sessionId);
  view.setUint32(headerSize, generation, true);
  view.setBigUint64(headerSize + 4, sourceSequenceEndExclusive, true);
  view.setInt32(headerSize + 12, replayBytes, true);
  return frame.buffer;
}

function attachFakeTerminal(
  sessionTerminals: (typeof import('../../state'))['sessionTerminals'],
  sessionId: string,
  rows = 24,
  hidden = false,
  deferEmptyWrites = false,
): FakeTerminalHarness {
  const pendingCallbacks: Array<() => void> = [];
  const writeMock = vi.fn((_data: Uint8Array | string, callback?: () => void) => {
    if (callback) {
      if (
        !deferEmptyWrites &&
        ((typeof _data === 'string' && _data.length === 0) || _data.length === 0)
      ) {
        callback();
        return;
      }
      pendingCallbacks.push(callback);
    }
  });

  const container = {
    classList: {
      contains: (className: string) => hidden && className === 'hidden',
    },
    getBoundingClientRect: () => ({ width: 640, height: 480 }),
    appendChild: vi.fn(),
    querySelector: vi.fn(() => null),
  } as unknown as HTMLDivElement;

  sessionTerminals.set(sessionId, {
    terminal: {
      cols: 80,
      rows,
      modes: { synchronizedOutputMode: false },
      write: writeMock,
      resize: vi.fn(),
      clear: vi.fn(),
      reset: vi.fn(),
    },
    fitAddon: {} as never,
    container,
    serverCols: 80,
    serverRows: rows,
    opened: true,
  } as never);

  return { pendingCallbacks, writeMock };
}

async function loadHarness(nowValues: number[]): Promise<Harness> {
  MockWebSocket.instances = [];
  vi.spyOn(performance, 'now').mockImplementation(() => {
    const value = nowValues[0] ?? 0;
    if (nowValues.length > 1) {
      nowValues.shift();
    }
    return value;
  });
  vi.stubGlobal('WebSocket', MockWebSocket);

  resetMuxChannelRuntimeForTests();
  state.sessionTerminals.clear();
  state.pendingOutputFrames.clear();
  state.sessionsNeedingResync.clear();
  stores.$activeSessionId.set('sess1234');
  stores.$currentSettings.set(null);
  stores.$dataLossDetected.set(null);
  stores.$muxHasConnected.set(false);
  stores.$muxWsConnected.set(false);
  stores.$stateWsConnected.set(false);

  connectMuxWebSocket();

  const ws = MockWebSocket.instances[0];
  if (!ws) {
    throw new Error('Mock WebSocket was not created');
  }

  return {
    decodeSessionId,
    encodeSessionId,
    updateTerminalVisibility,
    recoverVisibleTerminalsAfterBrowserResume,
    sessionTerminals: state.sessionTerminals,
    stores,
    constants,
    ws,
  };
}

describe('muxChannel', () => {
  it('probes a healthy mux without replacing it and removes the listener', async () => {
    const { ws: socket } = await loadHarness([0]);
    const remove = vi.spyOn(socket, 'removeEventListener');
    const result = probeMuxWebSocket();
    socket.dispatchEvent(new MessageEvent('message', { data: new ArrayBuffer(0) }));
    expect(await result).toBe(true);
    expect(socket.close).not.toHaveBeenCalled();
    expect(remove).toHaveBeenCalledTimes(1);
  });

  it('detects a silent stale-open mux and ignores results for replaced sockets', async () => {
    vi.useFakeTimers();
    await loadHarness([0]);
    const result = probeMuxWebSocket();
    await vi.advanceTimersByTimeAsync(2000);
    expect(await result).toBe(false);
    const retired = probeMuxWebSocket();
    connectMuxWebSocket();
    await vi.advanceTimersByTimeAsync(2000);
    expect(await retired).toBe(true);
    vi.useRealTimers();
  });

  it('resumes only bytes handed to xterm when a reconnect interrupts a batched drain', async () => {
    const harness = await loadHarness(new Array(128).fill(0));
    attachFakeTerminal(harness.sessionTerminals, 'sess1234');
    const chunk = 'x'.repeat(32 * 1024);
    for (let i = 0; i < 18; i += 1) {
      harness.ws.onmessage?.({
        data: buildSequencedOutputMessage(
          encodeSessionId,
          constants.MUX_TYPE_OUTPUT,
          constants.MUX_HEADER_SIZE,
          'sess1234',
          BigInt((i + 1) * chunk.length),
          chunk,
          80 + (i % 2),
        ),
      } as MessageEvent<ArrayBuffer>);
    }
    await Promise.resolve();
    expect(getBrowserTransportSnapshot('sess1234')?.receivedSeq).toBe(BigInt(17 * chunk.length));
    connectMuxWebSocket();
    const replacement = MockWebSocket.instances.at(-1)!;
    expect(new URL(replacement.url).searchParams.get('resumeCursors')).toBe(
      `sess1234:${16 * chunk.length}`,
    );
  });

  it('retires pending recovery requests when explicitly replacing the socket', async () => {
    const harness = await loadHarness([0]);
    attachFakeTerminal(harness.sessionTerminals, 'sess1234');
    harness.ws.onopen?.(new Event('open'));
    requestBufferRefresh('sess1234');
    suspendMuxForBrowserBackground();
    recoverVisibleTerminalsAfterBrowserResume('sess1234', ['sess1234']);
    const replacement = MockWebSocket.instances.at(-1)!;
    replacement.onopen?.(new Event('open'));
    requestBufferRefresh('sess1234');

    expect(
      replacement.send.mock.calls.filter(
        ([frame]) => frame[0] === constants.MUX_TYPE_BUFFER_REQUEST,
      ),
    ).toHaveLength(1);
  });

  it('ignores a retired alternate-screen preparation callback after a new recovery', async () => {
    const harness = await loadHarness([0]);
    const terminal = attachFakeTerminal(harness.sessionTerminals, 'sess1234');
    const begin = (generation: number, start: bigint, mode: number) => {
      harness.ws.onmessage?.({
        data: buildRecoveryBeginMessage(
          encodeSessionId,
          constants.MUX_TYPE_RECOVERY_BEGIN,
          constants.MUX_HEADER_SIZE,
          'sess1234',
          generation,
          start,
          start,
          true,
          mode,
        ),
      } as MessageEvent<ArrayBuffer>);
    };
    begin(1, 100n, 1049);
    const retiredPrefixCallback = terminal.pendingCallbacks.shift()!;
    begin(2, 200n, 0);
    expect(getBrowserTransportSnapshot('sess1234')?.receivedSeq).toBe(200n);
    retiredPrefixCallback();
    expect(getBrowserTransportSnapshot('sess1234')?.receivedSeq).toBe(200n);
  });

  it('discards output waiting behind a retired recovery barrier before it changes the new cursor', async () => {
    const harness = await loadHarness([0]);
    attachFakeTerminal(harness.sessionTerminals, 'sess1234', 24, false, true);
    harness.ws.onmessage?.({
      data: buildRecoveryBeginMessage(
        encodeSessionId,
        constants.MUX_TYPE_RECOVERY_BEGIN,
        constants.MUX_HEADER_SIZE,
        'sess1234',
        1,
        100n,
        103n,
      ),
    } as MessageEvent<ArrayBuffer>);
    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        encodeSessionId,
        constants.MUX_TYPE_OUTPUT,
        constants.MUX_HEADER_SIZE,
        'sess1234',
        103n,
        'old',
      ),
    } as MessageEvent<ArrayBuffer>);
    await Promise.resolve();
    harness.ws.onclose?.({ code: 1000 } as CloseEvent);
    await Promise.resolve();
    await Promise.resolve();

    expect(getBrowserTransportSnapshot('sess1234')?.dataLossCount).toBe(0);
    expect(getBrowserTransportSnapshot('sess1234')?.receivedSeq).toBe(0n);
  });

  beforeEach(() => {
    vi.useRealTimers();
    vi.stubGlobal('window', globalThis);
    vi.stubGlobal('getComputedStyle', () => ({
      backgroundColor: 'rgb(0, 0, 0)',
    }));
    vi.stubGlobal('document', {
      createElement: () => ({
        className: '',
        style: {},
        setAttribute: vi.fn(),
        remove: vi.fn(),
      }),
    });
  });

  afterEach(() => {
    vi.restoreAllMocks();
    vi.unstubAllGlobals();
  });

  it('keeps draining queued output without waiting for prior xterm callbacks', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    const sessionId = 'sess1234';
    const terminal = attachFakeTerminal(harness.sessionTerminals, sessionId);

    harness.ws.onmessage?.({
      data: buildOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        'first',
      ),
    } as MessageEvent<ArrayBuffer>);

    harness.ws.onmessage?.({
      data: buildOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        'second',
      ),
    } as MessageEvent<ArrayBuffer>);

    await Promise.resolve();

    expect(terminal.writeMock).toHaveBeenCalledTimes(1);
    expect(terminal.pendingCallbacks).toHaveLength(1);
    expect(harness.stores.$dataLossDetected.get()).toBeNull();
  });

  it('bounds bytes handed to xterm until its parse callbacks catch up', async () => {
    const harness = await loadHarness(new Array(64).fill(0));
    const sessionId = 'sess1234';
    const terminal = attachFakeTerminal(harness.sessionTerminals, sessionId);
    const chunk = 'x'.repeat(32 * 1024);

    for (let i = 0; i < 18; i += 1) {
      harness.ws.onmessage?.({
        data: buildSequencedOutputMessage(
          harness.encodeSessionId,
          harness.constants.MUX_TYPE_OUTPUT,
          harness.constants.MUX_HEADER_SIZE,
          sessionId,
          BigInt((i + 1) * chunk.length),
          chunk,
        ),
      } as MessageEvent<ArrayBuffer>);
    }

    await Promise.resolve();
    await Promise.resolve();
    expect(terminal.writeMock).toHaveBeenCalledTimes(8);
    expect(terminal.pendingCallbacks).toHaveLength(8);

    terminal.pendingCallbacks.splice(0).forEach((callback) => callback());
    await Promise.resolve();
    await Promise.resolve();
    expect(terminal.writeMock).toHaveBeenCalledTimes(9);
    expect(harness.stores.$dataLossDetected.get()).toBeNull();
  });

  it('recreates a stalled parser without granting fake byte credit or accepting retired callbacks', async () => {
    vi.useFakeTimers();
    const harness = await loadHarness(new Array(64).fill(0));
    const sessionId = 'sess1234';
    const terminal = attachFakeTerminal(harness.sessionTerminals, sessionId);
    const stalled = vi.fn();
    setTerminalParseStallHandler(stalled);
    const chunk = 'x'.repeat(32 * 1024);

    for (let i = 0; i < 18; i += 1) {
      harness.ws.onmessage?.({
        data: buildSequencedOutputMessage(
          harness.encodeSessionId,
          harness.constants.MUX_TYPE_OUTPUT,
          harness.constants.MUX_HEADER_SIZE,
          sessionId,
          BigInt((i + 1) * chunk.length),
          chunk,
        ),
      } as MessageEvent<ArrayBuffer>);
    }

    await Promise.resolve();
    await Promise.resolve();
    expect(terminal.writeMock).toHaveBeenCalledTimes(8);

    await vi.advanceTimersByTimeAsync(5_000);
    expect(terminal.writeMock).toHaveBeenCalledTimes(8);
    expect(stalled).toHaveBeenCalledTimes(1);
    const cursorBeforeRetiredCallbacks = getBrowserTransportSnapshot(sessionId)?.renderedSeq;
    terminal.pendingCallbacks.splice(0).forEach((callback) => callback());
    expect(getBrowserTransportSnapshot(sessionId)?.renderedSeq).toBe(cursorBeforeRetiredCallbacks);
    expect(harness.stores.$dataLossDetected.get()).toBeNull();
  });

  it('keeps parser debt across separate small drains and releases only acknowledged bytes', async () => {
    const harness = await loadHarness(new Array(200).fill(0));
    const terminal = attachFakeTerminal(harness.sessionTerminals, 'sess1234');
    const chunk = 'x'.repeat(32 * 1024);
    for (let i = 0; i < 20; i++) {
      harness.ws.onmessage?.({
        data: buildSequencedOutputMessage(
          encodeSessionId,
          constants.MUX_TYPE_OUTPUT,
          constants.MUX_HEADER_SIZE,
          'sess1234',
          BigInt((i + 1) * chunk.length),
          chunk,
        ),
      } as MessageEvent<ArrayBuffer>);
      await Promise.resolve();
      await Promise.resolve();
    }
    const bytesWritten = () =>
      terminal.writeMock.mock.calls.reduce((sum, [data]) => sum + data.length, 0);
    expect(bytesWritten()).toBe(512 * 1024);
    terminal.pendingCallbacks.shift()!();
    await Promise.resolve();
    await Promise.resolve();
    // Only the acknowledged 32 KiB become available, not the entire budget.
    expect(bytesWritten()).toBe(544 * 1024);
    terminal.pendingCallbacks.shift()!();
    await Promise.resolve();
    await Promise.resolve();
    expect(bytesWritten()).toBe(576 * 1024);
  });

  it('gives a replacement parser its own byte budget and ignores retired acknowledgements', async () => {
    const harness = await loadHarness(new Array(200).fill(0));
    const sessionId = 'sess1234';
    const old = attachFakeTerminal(harness.sessionTerminals, sessionId);
    const send = (sequence: bigint, text: string) =>
      harness.ws.onmessage?.({
        data: buildSequencedOutputMessage(
          encodeSessionId,
          constants.MUX_TYPE_OUTPUT,
          constants.MUX_HEADER_SIZE,
          sessionId,
          sequence,
          text,
        ),
      } as MessageEvent<ArrayBuffer>);
    send(512n * 1024n, 'x'.repeat(512 * 1024));
    await Promise.resolve();
    await Promise.resolve();
    forgetMuxSession(sessionId);
    const replacement = attachFakeTerminal(harness.sessionTerminals, sessionId);
    send(5n, 'fresh');
    await Promise.resolve();
    await Promise.resolve();
    expect(replacement.writeMock).toHaveBeenCalledTimes(1);
    const before = getBrowserTransportSnapshot(sessionId)?.renderedSeq;
    old.pendingCallbacks.splice(0).forEach((callback) => callback());
    expect(getBrowserTransportSnapshot(sessionId)?.renderedSeq).toBe(before);
    replacement.pendingCallbacks.splice(0).forEach((callback) => callback());
    expect(getBrowserTransportSnapshot(sessionId)?.renderedSeq).toBe(5n);
  });

  it('yields between drain slices so flood output does not monopolize the main thread', async () => {
    vi.useFakeTimers();
    vi.stubGlobal('MessageChannel', undefined);

    const harness = await loadHarness([0, 9, 9, 9, 9]);
    const sessionId = 'sess1234';
    const terminal = attachFakeTerminal(harness.sessionTerminals, sessionId);

    harness.ws.onmessage?.({
      data: buildOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        'first',
      ),
    } as MessageEvent<ArrayBuffer>);

    harness.ws.onmessage?.({
      data: buildOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        'second',
      ),
    } as MessageEvent<ArrayBuffer>);

    await Promise.resolve();

    expect(
      terminal.writeMock.mock.calls.filter((call) => call[0] instanceof Uint8Array),
    ).toHaveLength(1);

    await vi.runOnlyPendingTimersAsync();

    expect(
      terminal.writeMock.mock.calls.filter((call) => call[0] instanceof Uint8Array),
    ).toHaveLength(2);
  });

  it('preserves open scrollback on reconnect and ignores duplicate tail replay frames', async () => {
    const harness = await loadHarness([0, 0, 0, 0, 0]);
    const sessionId = 'sess1234';
    const terminal = attachFakeTerminal(harness.sessionTerminals, sessionId);
    const state = harness.sessionTerminals.get(sessionId);
    if (!state) {
      throw new Error('missing terminal state');
    }

    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        5n,
        'first',
      ),
    } as MessageEvent<ArrayBuffer>);

    await Promise.resolve();

    expect(terminal.writeMock).toHaveBeenCalledTimes(1);

    harness.stores.$muxHasConnected.set(true);
    harness.ws.onopen?.(new Event('open'));

    expect(state.terminal.clear).not.toHaveBeenCalled();

    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        5n,
        'first',
      ),
    } as MessageEvent<ArrayBuffer>);

    await Promise.resolve();

    expect(terminal.writeMock).toHaveBeenCalledTimes(1);
  });

  it("continues an overlapping replay at xterm's exact control-sequence cursor", async () => {
    const harness = await loadHarness([0, 0, 0, 0, 0]);
    const sessionId = 'sess1234';
    const terminal = attachFakeTerminal(harness.sessionTerminals, sessionId);

    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        3n,
        '\x1b[3',
      ),
    } as MessageEvent<ArrayBuffer>);

    await Promise.resolve();
    expect(terminal.writeMock).toHaveBeenCalledTimes(1);

    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        8n,
        '\x1b[31mXYZ',
      ),
    } as MessageEvent<ArrayBuffer>);

    await Promise.resolve();

    expect(terminal.writeMock).toHaveBeenCalledTimes(2);
    const replayData = terminal.writeMock.mock.calls[1]?.[0] as Uint8Array;
    expect(new TextDecoder().decode(replayData)).toBe('1mXYZ');
  });

  it('does not send replay rows on full-replay mux reconnect', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    attachFakeTerminal(harness.sessionTerminals, 'sess1234', 37);

    connectMuxWebSocket();

    const ws = MockWebSocket.instances.at(-1);
    expect(ws).toBeDefined();
    const url = new URL(ws!.url);
    expect(url.searchParams.get('activeSessionId')).toBe('sess1234');
    expect(url.searchParams.get('replayRows')).toBeNull();
  });

  it('sends local resume cursors on mux reconnect', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    attachFakeTerminal(harness.sessionTerminals, 'sess1234', 37);

    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        'sess1234',
        6n,
        'abcdef',
      ),
    } as MessageEvent<ArrayBuffer>);
    await Promise.resolve();

    connectMuxWebSocket();

    const ws = MockWebSocket.instances.at(-1);
    expect(ws).toBeDefined();
    const url = new URL(ws!.url);
    expect(url.searchParams.get('resumeCursors')).toBe('sess1234:6');
  });

  it('sends local replay rows on quick-resume mux reconnect', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    harness.stores.$currentSettings.set({ resumeMode: 'quickResume' } as never);
    attachFakeTerminal(harness.sessionTerminals, 'sess1234', 37);

    connectMuxWebSocket();

    const ws = MockWebSocket.instances.at(-1);
    expect(ws).toBeDefined();
    const url = new URL(ws!.url);
    expect(url.searchParams.get('activeSessionId')).toBe('sess1234');
    expect(url.searchParams.get('replayRows')).toBe('37');
  });

  it('does not request full replay when hot sessions become streamable', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    harness.stores.$currentSettings.set({ resumeMode: 'fullReplay' } as never);

    harness.ws.send.mockClear();
    harness.updateTerminalVisibility('sess1234', ['sess5678']);

    expect(harness.ws.send).toHaveBeenCalledTimes(1);
    const frames = harness.ws.send.mock.calls.map((call) => call[0] as Uint8Array);
    expect(frames[0]?.[0]).toBe(harness.constants.MUX_TYPE_VISIBLE_SESSIONS_HINT);
  });

  it('subscribes hidden mounted terminals through the background ingest hint', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);

    harness.ws.send.mockClear();
    harness.updateTerminalVisibility('sess1234', [], ['sess5678', 'sess9999']);

    expect(harness.ws.send).toHaveBeenCalledTimes(1);
    const frame = harness.ws.send.mock.calls[0]?.[0] as Uint8Array;
    expect(frame[0]).toBe(harness.constants.MUX_TYPE_BACKGROUND_SESSIONS_HINT);
    expect(harness.decodeSessionId(frame, harness.constants.MUX_HEADER_SIZE)).toBe('sess5678');
    expect(harness.decodeSessionId(frame, harness.constants.MUX_HEADER_SIZE + 8)).toBe('sess9999');
  });

  it('carries background subscriptions into mux reconnects', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    harness.updateTerminalVisibility('sess1234', [], ['sess5678']);

    connectMuxWebSocket();

    const ws = MockWebSocket.instances.at(-1);
    expect(ws).toBeDefined();
    const url = new URL(ws!.url);
    expect(url.searchParams.get('backgroundSessionIds')).toBe('sess5678');
  });

  it('does not include local replay rows in full buffer refresh requests', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    attachFakeTerminal(harness.sessionTerminals, 'sess1234', 41);

    harness.ws.send.mockClear();
    requestBufferRefresh('sess1234', 'fullReplay');

    const frame = harness.ws.send.mock.calls
      .map((call) => call[0] as Uint8Array)
      .find((candidate) => candidate[0] === harness.constants.MUX_TYPE_BUFFER_REQUEST);
    expect(frame).toBeDefined();
    expect(frame?.byteLength).toBe(harness.constants.MUX_HEADER_SIZE + 1);
    expect(frame?.[harness.constants.MUX_HEADER_SIZE]).toBe(0);
  });

  it('flushes a startup replay request that was made before the mux opened', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    harness.ws.readyState = MockWebSocket.CONNECTING;
    harness.ws.send.mockClear();

    requestBufferRefresh('sess1234', 'fullReplay', 'terminal_open_without_rendered_output');
    expect(harness.ws.send).not.toHaveBeenCalled();

    harness.ws.readyState = MockWebSocket.OPEN;
    harness.ws.onopen?.(new Event('open'));

    const bufferRequests = harness.ws.send.mock.calls
      .map((call) => call[0] as Uint8Array)
      .filter((frame) => frame[0] === harness.constants.MUX_TYPE_BUFFER_REQUEST);
    expect(bufferRequests).toHaveLength(1);
    expect(harness.decodeSessionId(bufferRequests[0]!, 1)).toBe('sess1234');
  });

  it('includes local replay rows in quick-resume buffer refresh requests', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    attachFakeTerminal(harness.sessionTerminals, 'sess1234', 41);

    harness.ws.send.mockClear();
    requestBufferRefresh('sess1234', 'quickResume');

    const frame = harness.ws.send.mock.calls
      .map((call) => call[0] as Uint8Array)
      .find((candidate) => candidate[0] === harness.constants.MUX_TYPE_BUFFER_REQUEST);
    expect(frame).toBeDefined();
    expect(frame?.byteLength).toBe(harness.constants.MUX_HEADER_SIZE + 11);
    expect(frame?.[harness.constants.MUX_HEADER_SIZE]).toBe(1);
    expect(
      new DataView(frame!.buffer, frame!.byteOffset, frame!.byteLength).getUint16(
        harness.constants.MUX_HEADER_SIZE + 1,
        true,
      ),
    ).toBe(41);
    expect(
      new DataView(frame!.buffer, frame!.byteOffset, frame!.byteLength).getBigUint64(
        harness.constants.MUX_HEADER_SIZE + 3,
        true,
      ),
    ).toBe(0n);
  });

  it('does not include local resume cursor in full buffer refresh requests', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    attachFakeTerminal(harness.sessionTerminals, 'sess1234', 41);

    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        'sess1234',
        9n,
        'processed',
      ),
    } as MessageEvent<ArrayBuffer>);
    await Promise.resolve();
    harness.ws.send.mockClear();
    requestBufferRefresh('sess1234', 'fullReplay');

    const frame = harness.ws.send.mock.calls
      .map((call) => call[0] as Uint8Array)
      .find((candidate) => candidate[0] === harness.constants.MUX_TYPE_BUFFER_REQUEST);
    expect(frame).toBeDefined();
    expect(frame?.byteLength).toBe(harness.constants.MUX_HEADER_SIZE + 1);
    expect(frame?.[harness.constants.MUX_HEADER_SIZE]).toBe(0);
  });

  it('coalesces repeated transport-loss recovery requests', async () => {
    const harness = await loadHarness([0, 0, 0, 0, 0, 0]);

    harness.ws.send.mockClear();
    const dataLossMessage = buildDataLossMessage(
      harness.encodeSessionId,
      harness.constants.MUX_TYPE_DATA_LOSS,
      harness.constants.MUX_HEADER_SIZE,
      'sess5678',
      128,
    );

    harness.ws.onmessage?.({ data: dataLossMessage } as MessageEvent<ArrayBuffer>);
    harness.ws.onmessage?.({ data: dataLossMessage } as MessageEvent<ArrayBuffer>);

    const bufferRequestFrames = harness.ws.send.mock.calls
      .map((call) => call[0] as Uint8Array)
      .filter((frame) => frame[0] === harness.constants.MUX_TYPE_BUFFER_REQUEST);

    expect(bufferRequestFrames).toHaveLength(1);
    resetMuxChannelRuntimeForTests();
  });

  it('restarts a recovery whose xterm preparation callback never completed', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    const sessionId = 'sess1234';
    const terminal = attachFakeTerminal(harness.sessionTerminals, sessionId, 24, false, true);

    harness.ws.onmessage?.({
      data: buildRecoveryBeginMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_RECOVERY_BEGIN,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        1,
        0n,
        0n,
        true,
      ),
    } as MessageEvent<ArrayBuffer>);
    expect(terminal.pendingCallbacks).toHaveLength(1);

    harness.ws.send.mockClear();
    requestBufferRefresh(sessionId, 'fullReplay', 'startup_framebuffer_blank');
    expect(harness.ws.send).not.toHaveBeenCalled();
    expect(getBrowserTransportSnapshot(sessionId)?.recoveryCoalesced).toBe(1);

    restartStalledSessionRecovery(sessionId, 'startup_framebuffer_blank');

    const bufferRequests = harness.ws.send.mock.calls
      .map((call) => call[0] as Uint8Array)
      .filter((frame) => frame[0] === harness.constants.MUX_TYPE_BUFFER_REQUEST);
    expect(bufferRequests).toHaveLength(1);
    expect(bufferRequests[0]?.[harness.constants.MUX_HEADER_SIZE]).toBe(0);
    expect(getBrowserTransportSnapshot(sessionId)).toMatchObject({
      receivedSeq: 0n,
      renderedSeq: 0n,
      recoveryRequested: 1,
      lastRecoveryCause: 'startup_framebuffer_blank',
    });
  });

  it('bypasses the stale xterm barrier after the terminal instance was recreated', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    const sessionId = 'sess1234';
    const terminal = attachFakeTerminal(harness.sessionTerminals, sessionId, 24, false, true);

    restartStalledSessionRecovery(sessionId, 'startup_terminal_recreated', true);
    harness.ws.onmessage?.({
      data: buildRecoveryBeginMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_RECOVERY_BEGIN,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        2,
        0n,
        0n,
        true,
      ),
    } as MessageEvent<ArrayBuffer>);

    expect(terminal.pendingCallbacks).toHaveLength(0);
    expect(terminal.writeMock).not.toHaveBeenCalledWith('', expect.any(Function));
    expect(getBrowserTransportSnapshot(sessionId)).toMatchObject({
      receivedSeq: 0n,
      renderedSeq: 0n,
      recoveryRequested: 1,
      lastRecoveryCause: 'startup_terminal_recreated',
    });
  });

  it('fully resets the terminal parser state on resync frames', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    const sessionId = 'sess1234';
    attachFakeTerminal(harness.sessionTerminals, sessionId);
    const state = harness.sessionTerminals.get(sessionId);

    const frame = new Uint8Array(harness.constants.MUX_HEADER_SIZE);
    frame[0] = harness.constants.MUX_TYPE_RESYNC;
    harness.encodeSessionId(frame, 1, sessionId);
    harness.ws.onmessage?.({ data: frame.buffer } as MessageEvent<ArrayBuffer>);

    expect(state?.terminal.reset).toHaveBeenCalledOnce();
    expect(state?.terminal.clear).not.toHaveBeenCalled();
    resetMuxChannelRuntimeForTests();
  });

  it('includes local resume cursor in quick-resume buffer refresh requests', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    attachFakeTerminal(harness.sessionTerminals, 'sess1234', 41);

    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        'sess1234',
        9n,
        'processed',
      ),
    } as MessageEvent<ArrayBuffer>);
    await Promise.resolve();

    harness.ws.send.mockClear();
    requestBufferRefresh('sess1234', 'quickResume');

    const frame = harness.ws.send.mock.calls
      .map((call) => call[0] as Uint8Array)
      .find((candidate) => candidate[0] === harness.constants.MUX_TYPE_BUFFER_REQUEST);
    expect(frame).toBeDefined();
    expect(frame?.byteLength).toBe(harness.constants.MUX_HEADER_SIZE + 11);
    expect(frame?.[harness.constants.MUX_HEADER_SIZE]).toBe(1);
    expect(
      new DataView(frame!.buffer, frame!.byteOffset, frame!.byteLength).getUint16(
        harness.constants.MUX_HEADER_SIZE + 1,
        true,
      ),
    ).toBe(41);
    expect(
      new DataView(frame!.buffer, frame!.byteOffset, frame!.byteLength).getBigUint64(
        harness.constants.MUX_HEADER_SIZE + 3,
        true,
      ),
    ).toBe(9n);
  });

  it('keeps hidden background xterm sessions live without requesting replay on visibility', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    const backgroundSessionId = 'sess5678';
    const terminal = attachFakeTerminal(harness.sessionTerminals, backgroundSessionId, 24, true);

    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        backgroundSessionId,
        5n,
        'first',
      ),
    } as MessageEvent<ArrayBuffer>);

    await Promise.resolve();

    expect(terminal.writeMock).toHaveBeenCalledTimes(1);
    expect(new TextDecoder().decode(terminal.writeMock.mock.calls[0]?.[0] as Uint8Array)).toBe(
      'first',
    );
    expect(getBrowserTransportSnapshot(backgroundSessionId)?.receivedSeq).toBe(5n);
    expect(harness.sessionTerminals.get(backgroundSessionId)?.serverCols).toBe(80);
    expect(harness.sessionTerminals.get(backgroundSessionId)?.serverRows).toBe(24);

    harness.ws.send.mockClear();
    harness.updateTerminalVisibility('sess1234', [backgroundSessionId]);

    const frames = harness.ws.send.mock.calls.map((call) => call[0] as Uint8Array);
    expect(
      frames.some((frame) => frame[0] === harness.constants.MUX_TYPE_VISIBLE_SESSIONS_HINT),
    ).toBe(true);
    const replayRequest = frames.find(
      (frame) =>
        frame[0] === harness.constants.MUX_TYPE_BUFFER_REQUEST &&
        harness.decodeSessionId(frame, 1) === backgroundSessionId,
    );
    expect(replayRequest).toBeUndefined();
  });

  it('keeps active terminal live while the browser tab is hidden', async () => {
    Object.defineProperty(document, 'hidden', {
      value: true,
      configurable: true,
    });
    Object.defineProperty(document, 'visibilityState', {
      value: 'hidden',
      configurable: true,
    });

    const harness = await loadHarness([0, 0, 0, 0]);
    const sessionId = 'sess1234';
    const terminal = attachFakeTerminal(harness.sessionTerminals, sessionId);

    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        7n,
        'hidden-active-output',
      ),
    } as MessageEvent<ArrayBuffer>);

    await Promise.resolve();

    expect(terminal.writeMock).toHaveBeenCalledTimes(1);
    expect(new TextDecoder().decode(terminal.writeMock.mock.calls[0]?.[0] as Uint8Array)).toBe(
      'hidden-active-output',
    );
    expect(getBrowserTransportSnapshot(sessionId)?.receivedSeq).toBe(7n);

    Object.defineProperty(document, 'hidden', {
      value: false,
      configurable: true,
    });
    Object.defineProperty(document, 'visibilityState', {
      value: 'visible',
      configurable: true,
    });

    harness.ws.send.mockClear();
    harness.updateTerminalVisibility(sessionId, []);

    const frames = harness.ws.send.mock.calls.map((call) => call[0] as Uint8Array);
    const replayRequest = frames.find(
      (frame) =>
        frame[0] === harness.constants.MUX_TYPE_BUFFER_REQUEST &&
        harness.decodeSessionId(frame, 1) === sessionId,
    );
    expect(replayRequest).toBeUndefined();
  });

  it('advances the browser receive cursor for hidden output that was rendered', async () => {
    Object.defineProperty(document, 'hidden', {
      value: true,
      configurable: true,
    });
    Object.defineProperty(document, 'visibilityState', {
      value: 'hidden',
      configurable: true,
    });

    const harness = await loadHarness([2501, 2501, 2501, 2501, 2501]);
    const sessionId = 'sess1234';
    const terminal = attachFakeTerminal(harness.sessionTerminals, sessionId);

    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        11n,
        'hidden-idle-output',
      ),
    } as MessageEvent<ArrayBuffer>);

    await Promise.resolve();

    expect(terminal.writeMock).toHaveBeenCalledTimes(1);
    expect(getBrowserTransportSnapshot(sessionId)?.receivedSeq).toBe(11n);
    expect(getBrowserTransportSnapshot(sessionId)?.renderedSeq ?? 0n).toBe(0n);
  });

  it('does not request foreground replay after hidden output stayed live', async () => {
    vi.useFakeTimers();
    vi.stubGlobal('MessageChannel', undefined);
    Object.defineProperty(document, 'hidden', {
      value: true,
      configurable: true,
    });
    Object.defineProperty(document, 'visibilityState', {
      value: 'hidden',
      configurable: true,
    });

    const harness = await loadHarness([2501, 2501, 2501, 2501, 2501, 2501]);
    const sessionId = 'sess1234';
    attachFakeTerminal(harness.sessionTerminals, sessionId);

    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        11n,
        'hidden-idle-output',
      ),
    } as MessageEvent<ArrayBuffer>);
    await Promise.resolve();

    expect(getBrowserTransportSnapshot(sessionId)?.receivedSeq).toBe(11n);

    harness.ws.send.mockClear();
    await vi.advanceTimersByTimeAsync(2000);

    const backgroundDeltaRequest = harness.ws.send.mock.calls
      .map((call) => call[0] as Uint8Array)
      .find(
        (frame) =>
          frame[0] === harness.constants.MUX_TYPE_BUFFER_REQUEST &&
          harness.decodeSessionId(frame, 1) === sessionId,
      );
    expect(backgroundDeltaRequest).toBeUndefined();

    Object.defineProperty(document, 'hidden', {
      value: false,
      configurable: true,
    });
    Object.defineProperty(document, 'visibilityState', {
      value: 'visible',
      configurable: true,
    });

    harness.ws.send.mockClear();
    harness.updateTerminalVisibility(sessionId, []);

    const foregroundReplayRequest = harness.ws.send.mock.calls
      .map((call) => call[0] as Uint8Array)
      .find(
        (frame) =>
          frame[0] === harness.constants.MUX_TYPE_BUFFER_REQUEST &&
          harness.decodeSessionId(frame, 1) === sessionId,
      );
    expect(foregroundReplayRequest).toBeUndefined();
  });

  it('buffers unopened background frames without requesting replay on visibility', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    const backgroundSessionId = 'sess5678';

    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        backgroundSessionId,
        5n,
        'first',
      ),
    } as MessageEvent<ArrayBuffer>);

    await Promise.resolve();
    expect(getBrowserTransportSnapshot(backgroundSessionId)?.receivedSeq).toBe(5n);
    expect(state.pendingOutputFrames.get(backgroundSessionId)).toHaveLength(1);

    attachFakeTerminal(harness.sessionTerminals, backgroundSessionId, 24, true);
    harness.ws.send.mockClear();
    harness.updateTerminalVisibility('sess1234', [backgroundSessionId]);

    const backgroundReplayRequest = harness.ws.send.mock.calls
      .map((call) => call[0] as Uint8Array)
      .find(
        (frame) =>
          frame[0] === harness.constants.MUX_TYPE_BUFFER_REQUEST &&
          harness.decodeSessionId(frame, 1) === backgroundSessionId,
      );
    expect(backgroundReplayRequest).toBeUndefined();
    expect(state.pendingOutputFrames.get(backgroundSessionId)).toHaveLength(1);
  });

  it('requests cursor recovery instead of rendering partial frames after browser queue overflow', async () => {
    vi.useFakeTimers();
    vi.stubGlobal('MessageChannel', undefined);

    const harness = await loadHarness([0, 9, 9, 9, 9]);
    const sessionId = 'sess1234';
    attachFakeTerminal(harness.sessionTerminals, sessionId);

    const chunk = 'x'.repeat(32 * 1024);
    harness.ws.onmessage?.({
      data: buildOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        'first',
      ),
    } as MessageEvent<ArrayBuffer>);

    await Promise.resolve();
    harness.ws.send.mockClear();

    for (let i = 0; i < 140; i += 1) {
      harness.ws.onmessage?.({
        data: buildSequencedOutputMessage(
          harness.encodeSessionId,
          harness.constants.MUX_TYPE_OUTPUT,
          harness.constants.MUX_HEADER_SIZE,
          sessionId,
          BigInt((i + 2) * chunk.length),
          chunk,
        ),
      } as MessageEvent<ArrayBuffer>);
    }

    expect(harness.stores.$dataLossDetected.get()?.sessionId).toBe(sessionId);
    const bufferRequest = harness.ws.send.mock.calls
      .map((call) => call[0] as Uint8Array)
      .find((frame) => frame[0] === harness.constants.MUX_TYPE_BUFFER_REQUEST);
    expect(bufferRequest).toBeDefined();
  });

  it('coalesces thousands of tiny contiguous browser frames before the item-count limit', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    const sessionId = 'sess1234';
    const terminal = attachFakeTerminal(harness.sessionTerminals, sessionId);

    for (let i = 0; i < 2_500; i += 1) {
      harness.ws.onmessage?.({
        data: buildSequencedOutputMessage(
          harness.encodeSessionId,
          harness.constants.MUX_TYPE_OUTPUT,
          harness.constants.MUX_HEADER_SIZE,
          sessionId,
          BigInt(i + 1),
          'x',
        ),
      } as MessageEvent<ArrayBuffer>);
    }

    expect(harness.stores.$dataLossDetected.get()).toBeNull();
    await Promise.resolve();
    await Promise.resolve();
    expect(terminal.writeMock).toHaveBeenCalledTimes(1);
    expect((terminal.writeMock.mock.calls[0]?.[0] as Uint8Array).byteLength).toBe(2_500);
  });

  it('detects a forward gap before writing and coalesces recovery requests', async () => {
    const harness = await loadHarness([0, 0, 0, 0, 0, 0]);
    const sessionId = 'sess1234';
    const terminal = attachFakeTerminal(harness.sessionTerminals, sessionId);

    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        3n,
        'abc',
      ),
    } as MessageEvent<ArrayBuffer>);
    await Promise.resolve();
    harness.ws.send.mockClear();

    const gapFrame = buildSequencedOutputMessage(
      harness.encodeSessionId,
      harness.constants.MUX_TYPE_OUTPUT,
      harness.constants.MUX_HEADER_SIZE,
      sessionId,
      8n,
      'xy',
    );
    harness.ws.onmessage?.({ data: gapFrame } as MessageEvent<ArrayBuffer>);
    await Promise.resolve();
    harness.ws.onmessage?.({ data: gapFrame } as MessageEvent<ArrayBuffer>);
    await Promise.resolve();

    const recoveryRequests = harness.ws.send.mock.calls
      .map((call) => call[0] as Uint8Array)
      .filter((frame) => frame[0] === harness.constants.MUX_TYPE_BUFFER_REQUEST);
    expect(recoveryRequests).toHaveLength(1);
    expect(recoveryRequests[0]?.[harness.constants.MUX_HEADER_SIZE]).toBe(1);
    expect(terminal.writeMock).toHaveBeenCalledTimes(1);
    expect(getBrowserTransportSnapshot(sessionId)).toMatchObject({
      receivedSeq: 3n,
      recoveryRequested: 1,
      recoveryCoalesced: 1,
      recoveryGapCount: 2,
    });

    harness.ws.onmessage?.({
      data: buildRecoveryBeginMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_RECOVERY_BEGIN,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        1,
        3n,
        5n,
      ),
    } as MessageEvent<ArrayBuffer>);
    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        5n,
        'de',
      ),
    } as MessageEvent<ArrayBuffer>);
    harness.ws.onmessage?.({
      data: buildRecoveryEndMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_RECOVERY_END,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        1,
        5n,
        2,
      ),
    } as MessageEvent<ArrayBuffer>);

    await vi.waitFor(() => {
      expect(getBrowserTransportSnapshot(sessionId)?.recoveryCompleted).toBe(1);
    });
    expect(terminal.writeMock).toHaveBeenCalledTimes(3);
    expect(getBrowserTransportSnapshot(sessionId)).toMatchObject({
      receivedSeq: 5n,
      recoveryReplayBytes: 2,
    });
  });

  it('invalidates only the recovered session output generation', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    const recoveringSessionId = 'sess1234';
    const unaffectedSessionId = 'sess5678';
    const recoveringTerminal = attachFakeTerminal(harness.sessionTerminals, recoveringSessionId);
    const unaffectedTerminal = attachFakeTerminal(harness.sessionTerminals, unaffectedSessionId);

    for (const sessionId of [recoveringSessionId, unaffectedSessionId]) {
      harness.ws.onmessage?.({
        data: buildSequencedOutputMessage(
          harness.encodeSessionId,
          harness.constants.MUX_TYPE_OUTPUT,
          harness.constants.MUX_HEADER_SIZE,
          sessionId,
          3n,
          'abc',
        ),
      } as MessageEvent<ArrayBuffer>);
    }
    await Promise.resolve();

    harness.ws.onmessage?.({
      data: buildRecoveryBeginMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_RECOVERY_BEGIN,
        harness.constants.MUX_HEADER_SIZE,
        recoveringSessionId,
        4,
        3n,
        3n,
        true,
      ),
    } as MessageEvent<ArrayBuffer>);
    harness.ws.onmessage?.({
      data: buildRecoveryEndMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_RECOVERY_END,
        harness.constants.MUX_HEADER_SIZE,
        recoveringSessionId,
        4,
        3n,
        0,
      ),
    } as MessageEvent<ArrayBuffer>);

    unaffectedTerminal.pendingCallbacks[0]?.();
    expect(getBrowserTransportSnapshot(unaffectedSessionId)?.renderedSeq).toBe(3n);
    expect(recoveringTerminal.writeMock).toHaveBeenCalledTimes(2);
    expect(
      harness.sessionTerminals.get(recoveringSessionId)?.terminal.reset,
    ).toHaveBeenCalledOnce();
    expect(
      harness.sessionTerminals.get(unaffectedSessionId)?.terminal.reset,
    ).not.toHaveBeenCalled();
    expect(getBrowserTransportSnapshot(recoveringSessionId)).toMatchObject({
      recoveryCompleted: 1,
      recoveryResetCount: 1,
    });
  });

  it('restores alternate screen mode before replaying a truncated snapshot', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    const sessionId = 'sess1234';
    const terminal = attachFakeTerminal(harness.sessionTerminals, sessionId);

    harness.ws.onmessage?.({
      data: buildRecoveryBeginMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_RECOVERY_BEGIN,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        5,
        100n,
        102n,
        true,
        1049,
      ),
    } as MessageEvent<ArrayBuffer>);
    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        102n,
        'de',
      ),
    } as MessageEvent<ArrayBuffer>);
    harness.ws.onmessage?.({
      data: buildRecoveryEndMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_RECOVERY_END,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        5,
        102n,
        2,
      ),
    } as MessageEvent<ArrayBuffer>);

    expect(harness.sessionTerminals.get(sessionId)?.terminal.reset).toHaveBeenCalledOnce();
    expect(terminal.writeMock).toHaveBeenCalledTimes(2);
    expect(terminal.writeMock.mock.calls[1]?.[0]).toBe('\x1b[?1049h');
    expect(getBrowserTransportSnapshot(sessionId)?.recoveryCompleted).toBe(0);

    terminal.pendingCallbacks[0]?.();
    await vi.waitFor(() => {
      expect(getBrowserTransportSnapshot(sessionId)?.recoveryCompleted).toBe(1);
    });
    expect(terminal.writeMock).toHaveBeenCalledTimes(3);
  });

  it('keeps replay mode with output that arrives before the terminal opens', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    const sessionId = 'sess1234';

    harness.ws.onmessage?.({
      data: buildRecoveryBeginMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_RECOVERY_BEGIN,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        6,
        100n,
        102n,
        true,
        1049,
      ),
    } as MessageEvent<ArrayBuffer>);
    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        102n,
        'de',
      ),
    } as MessageEvent<ArrayBuffer>);
    harness.ws.onmessage?.({
      data: buildRecoveryEndMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_RECOVERY_END,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        6,
        102n,
        2,
      ),
    } as MessageEvent<ArrayBuffer>);

    await vi.waitFor(() => {
      expect(getBrowserTransportSnapshot(sessionId)?.recoveryCompleted).toBe(1);
    });
    expect(harness.sessionTerminals.has(sessionId)).toBe(false);
    expect(state.pendingTerminalReplayModes.get(sessionId)).toBe(1049);
    expect(state.pendingOutputFrames.get(sessionId)).toHaveLength(1);
  });

  it('requests one follow-up when data loss arrives inside a recovery transaction', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    const sessionId = 'sess1234';
    attachFakeTerminal(harness.sessionTerminals, sessionId);
    harness.ws.send.mockClear();

    harness.ws.onmessage?.({
      data: buildRecoveryBeginMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_RECOVERY_BEGIN,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        7,
        3n,
        3n,
      ),
    } as MessageEvent<ArrayBuffer>);
    harness.ws.onmessage?.({
      data: buildDataLossMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_DATA_LOSS,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        2,
      ),
    } as MessageEvent<ArrayBuffer>);
    expect(harness.ws.send).not.toHaveBeenCalled();

    harness.ws.onmessage?.({
      data: buildRecoveryEndMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_RECOVERY_END,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        7,
        3n,
        0,
      ),
    } as MessageEvent<ArrayBuffer>);

    const recoveryRequests = harness.ws.send.mock.calls
      .map((call) => call[0] as Uint8Array)
      .filter((frame) => frame[0] === harness.constants.MUX_TYPE_BUFFER_REQUEST);
    expect(recoveryRequests).toHaveLength(1);
    expect(getBrowserTransportSnapshot(sessionId)).toMatchObject({
      recoveryCoalesced: 1,
      recoveryCompleted: 1,
      recoveryRequested: 1,
    });
  });

  it('releases per-session recovery telemetry when a terminal is destroyed', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    const sessionId = 'sess1234';
    attachFakeTerminal(harness.sessionTerminals, sessionId);
    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        3n,
        'abc',
      ),
    } as MessageEvent<ArrayBuffer>);
    await Promise.resolve();
    expect(getBrowserTransportSnapshot(sessionId)).not.toBeNull();

    forgetMuxSession(sessionId);

    expect(getBrowserTransportSnapshot(sessionId)).toBeNull();
    expect(state.pendingOutputFrames.has(sessionId)).toBe(false);
    expect(state.sessionsNeedingResync.has(sessionId)).toBe(false);
  });

  it('does not request quick-resume bursts when hot sessions become streamable', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    harness.stores.$currentSettings.set({ resumeMode: 'quickResume' } as never);

    harness.ws.send.mockClear();
    harness.updateTerminalVisibility('sess1234', ['sess5678']);

    expect(harness.ws.send).toHaveBeenCalledTimes(1);
    const frames = harness.ws.send.mock.calls.map((call) => call[0] as Uint8Array);
    expect(frames[0]?.[0]).toBe(harness.constants.MUX_TYPE_VISIBLE_SESSIONS_HINT);
  });

  it('sends priority hints without speculative replay after mobile browser resume', async () => {
    const harness = await loadHarness([5000, 5000, 5000, 5000, 5000, 5000]);
    attachFakeTerminal(harness.sessionTerminals, 'sess1234', 41);
    attachFakeTerminal(harness.sessionTerminals, 'sess5678', 29);
    attachFakeTerminal(harness.sessionTerminals, 'sess9999', 33);

    harness.ws.send.mockClear();
    harness.recoverVisibleTerminalsAfterBrowserResume('sess1234', ['sess5678']);

    const frames = harness.ws.send.mock.calls.map((call) => call[0] as Uint8Array);
    expect(
      frames.some((frame) => frame[0] === harness.constants.MUX_TYPE_VISIBLE_SESSIONS_HINT),
    ).toBe(true);
    expect(
      frames.some(
        (frame) =>
          frame[0] === harness.constants.MUX_TYPE_ACTIVE_HINT &&
          harness.decodeSessionId(frame, 1) === 'sess1234',
      ),
    ).toBe(true);

    expect(frames.some((frame) => frame[0] === harness.constants.MUX_TYPE_BUFFER_REQUEST)).toBe(
      false,
    );
  });

  it('does not quick-resume visible terminals on ordinary focus recovery', async () => {
    const harness = await loadHarness([5000, 5000, 5000, 5000]);
    attachFakeTerminal(harness.sessionTerminals, 'sess1234', 41);

    harness.ws.send.mockClear();
    harness.recoverVisibleTerminalsAfterBrowserResume('sess1234', ['sess1234']);

    const frames = harness.ws.send.mock.calls.map((call) => call[0] as Uint8Array);
    expect(
      frames.some((frame) => frame[0] === harness.constants.MUX_TYPE_VISIBLE_SESSIONS_HINT),
    ).toBe(true);
    expect(frames.some((frame) => frame[0] === harness.constants.MUX_TYPE_BUFFER_REQUEST)).toBe(
      false,
    );
  });

  it('suspends hidden-browser transport and reconnects with the terminal cursor', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    attachFakeTerminal(harness.sessionTerminals, 'sess1234');
    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        'sess1234',
        7n,
        'foreground-output',
      ),
    } as MessageEvent<ArrayBuffer>);
    await Promise.resolve();

    suspendMuxForBrowserBackground();
    suspendMuxForBrowserBackground();

    expect(stores.$muxWsConnected.get()).toBe(false);
    expect(MockWebSocket.instances).toHaveLength(1);

    harness.recoverVisibleTerminalsAfterBrowserResume('sess1234', ['sess1234']);

    expect(MockWebSocket.instances).toHaveLength(2);
    const reconnectUrl = new URL(MockWebSocket.instances[1]!.url);
    expect(reconnectUrl.searchParams.get('activeSessionId')).toBe('sess1234');
    expect(reconnectUrl.searchParams.get('visibleSessionIds')).toBe('sess1234');
    expect(reconnectUrl.searchParams.get('resumeCursors')).toContain('sess1234');

    harness.recoverVisibleTerminalsAfterBrowserResume('sess1234', ['sess1234']);
    expect(MockWebSocket.instances).toHaveLength(2);
  });

  it('reconnects mux with visible sessions after mobile browser resume closes the socket', async () => {
    const harness = await loadHarness([5000, 5000, 5000, 5000]);
    harness.ws.readyState = MockWebSocket.CLOSED;

    harness.recoverVisibleTerminalsAfterBrowserResume('sess1234', ['sess5678']);

    const reconnectWs = MockWebSocket.instances.at(-1);
    expect(MockWebSocket.instances).toHaveLength(2);
    expect(reconnectWs).toBeDefined();
    const url = new URL(reconnectWs!.url);
    expect(url.searchParams.get('activeSessionId')).toBe('sess1234');
    expect(url.searchParams.get('visibleSessionIds')).toBe('sess5678');
  });

  it('replaces a stale-open mux generation after a long browser resume', async () => {
    const harness = await loadHarness([5000, 5000, 5000, 5000]);
    expect(harness.ws.readyState).toBe(MockWebSocket.OPEN);

    harness.recoverVisibleTerminalsAfterBrowserResume('sess1234', ['sess5678'], {
      forceReconnect: true,
    });

    expect(MockWebSocket.instances).toHaveLength(2);
    const reconnectUrl = new URL(MockWebSocket.instances[1]!.url);
    expect(reconnectUrl.searchParams.get('activeSessionId')).toBe('sess1234');
    expect(reconnectUrl.searchParams.get('visibleSessionIds')).toBe('sess5678');
  });

  it('sends sampled input trace markers before normal input when tracing is enabled', async () => {
    const harness = await loadHarness([10, 10, 10, 10]);

    setInputLatencyTracingEnabled(true);
    harness.ws.send.mockClear();

    sendInput('sess1234', 'a');

    expect(harness.ws.send).toHaveBeenCalledTimes(3);
    const frames = harness.ws.send.mock.calls.map((call) => call[0] as Uint8Array);
    expect(frames[0]?.[0]).toBe(harness.constants.MUX_TYPE_INPUT_TRACE_MARKER);
    expect(frames[1]?.[0]).toBe(harness.constants.MUX_TYPE_INPUT);
    expect(frames[1]?.[harness.constants.MUX_HEADER_SIZE]).toBe('a'.charCodeAt(0));
    expect(frames[2]?.[0]).toBe(harness.constants.MUX_TYPE_ACTIVE_HINT);

    const markerView = new DataView(
      frames[0]!.buffer,
      frames[0]!.byteOffset,
      frames[0]!.byteLength,
    );
    expect(markerView.getUint32(harness.constants.MUX_HEADER_SIZE, true)).not.toBe(0);
  });

  it('tracks bracketed paste mode when control sequences are split across output frames', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    const sessionId = 'sess1234';
    attachFakeTerminal(harness.sessionTerminals, sessionId);

    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        5n,
        '\x1b[?20',
      ),
    } as MessageEvent<ArrayBuffer>);
    await Promise.resolve();
    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        8n,
        '04h',
      ),
    } as MessageEvent<ArrayBuffer>);
    await Promise.resolve();

    expect(isBracketedPasteEnabled(sessionId)).toBe(true);

    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        14n,
        '\x1b[?200',
      ),
    } as MessageEvent<ArrayBuffer>);
    await Promise.resolve();
    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        16n,
        '4l',
      ),
    } as MessageEvent<ArrayBuffer>);
    await Promise.resolve();

    expect(isBracketedPasteEnabled(sessionId)).toBe(false);
  });
});

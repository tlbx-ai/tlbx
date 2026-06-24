import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import * as constants from '../../constants';
import * as state from '../../state';
import * as stores from '../../stores';
import {
  connectMuxWebSocket,
  decodeSessionId,
  encodeSessionId,
  getBrowserTransportSnapshot,
  isBracketedPasteEnabled,
  recoverVisibleTerminalsAfterBrowserResume,
  requestBufferRefresh,
  resetMuxChannelRuntimeForTests,
  sendInput,
  setInputLatencyTracingEnabled,
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
}));

vi.mock('../../api/client', () => ({
  getSessions: vi.fn(),
}));

vi.mock('../../utils', async (importOriginal) => {
  const actual = await importOriginal<typeof import('../../utils')>();
  return {
    ...actual,
    checkVersionAndReload: vi.fn().mockResolvedValue(undefined),
    closeWebSocket: vi.fn(),
    createWsUrl: (path: string) => `ws://midterm.test${path}`,
  };
});

class MockWebSocket {
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

function attachFakeTerminal(
  sessionTerminals: (typeof import('../../state'))['sessionTerminals'],
  sessionId: string,
  rows = 24,
  hidden = false,
): FakeTerminalHarness {
  const pendingCallbacks: Array<() => void> = [];
  const writeMock = vi.fn((_data: Uint8Array | string, callback?: () => void) => {
    if (callback) {
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

  it('does not trim replay frames through terminal control sequences', async () => {
    const harness = await loadHarness([0, 0, 0, 0, 0]);
    const sessionId = 'sess1234';
    const terminal = attachFakeTerminal(harness.sessionTerminals, sessionId);

    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        sessionId,
        5n,
        'abcde',
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
        10n,
        '\x1b[31mXYZ',
      ),
    } as MessageEvent<ArrayBuffer>);

    await Promise.resolve();

    expect(terminal.writeMock).toHaveBeenCalledTimes(2);
    const replayData = terminal.writeMock.mock.calls[1]?.[0] as Uint8Array;
    expect(new TextDecoder().decode(replayData)).toBe('\x1b[31mXYZ');
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

  it('backs off repeated transport-loss buffer refresh requests', async () => {
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

  it('requests resync instead of dropping partial terminal frames when browser output queue exceeds byte budget', async () => {
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

  it('does not request quick-resume bursts when hot sessions become streamable', async () => {
    const harness = await loadHarness([0, 0, 0, 0]);
    harness.stores.$currentSettings.set({ resumeMode: 'quickResume' } as never);

    harness.ws.send.mockClear();
    harness.updateTerminalVisibility('sess1234', ['sess5678']);

    expect(harness.ws.send).toHaveBeenCalledTimes(1);
    const frames = harness.ws.send.mock.calls.map((call) => call[0] as Uint8Array);
    expect(frames[0]?.[0]).toBe(harness.constants.MUX_TYPE_VISIBLE_SESSIONS_HINT);
  });

  it('quick-resumes active and visible terminals after mobile browser resume', async () => {
    const harness = await loadHarness([5000, 5000, 5000, 5000, 5000, 5000]);
    attachFakeTerminal(harness.sessionTerminals, 'sess1234', 41);
    attachFakeTerminal(harness.sessionTerminals, 'sess5678', 29);
    attachFakeTerminal(harness.sessionTerminals, 'sess9999', 33);

    harness.ws.send.mockClear();
    harness.recoverVisibleTerminalsAfterBrowserResume('sess1234', ['sess5678'], {
      quickRefresh: true,
    });

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

    const bufferRequests = frames.filter(
      (frame) => frame[0] === harness.constants.MUX_TYPE_BUFFER_REQUEST,
    );
    expect(bufferRequests.map((frame) => harness.decodeSessionId(frame, 1)).sort()).toEqual([
      'sess1234',
      'sess5678',
    ]);
    expect(bufferRequests.every((frame) => frame[harness.constants.MUX_HEADER_SIZE] === 1)).toBe(
      true,
    );
  });

  it('does not quick-resume visible terminals on ordinary focus recovery', async () => {
    const harness = await loadHarness([5000, 5000, 5000, 5000]);
    attachFakeTerminal(harness.sessionTerminals, 'sess1234', 41);

    harness.ws.send.mockClear();
    harness.recoverVisibleTerminalsAfterBrowserResume('sess1234', ['sess1234'], {
      quickRefresh: false,
    });

    const frames = harness.ws.send.mock.calls.map((call) => call[0] as Uint8Array);
    expect(
      frames.some((frame) => frame[0] === harness.constants.MUX_TYPE_VISIBLE_SESSIONS_HINT),
    ).toBe(true);
    expect(frames.some((frame) => frame[0] === harness.constants.MUX_TYPE_BUFFER_REQUEST)).toBe(
      false,
    );
  });

  it('reconnects mux with visible sessions after mobile browser resume closes the socket', async () => {
    const harness = await loadHarness([5000, 5000, 5000, 5000]);
    harness.ws.readyState = MockWebSocket.CLOSED;

    harness.recoverVisibleTerminalsAfterBrowserResume('sess1234', ['sess5678'], {
      quickRefresh: true,
    });

    const reconnectWs = MockWebSocket.instances.at(-1);
    expect(MockWebSocket.instances).toHaveLength(2);
    expect(reconnectWs).toBeDefined();
    const url = new URL(reconnectWs!.url);
    expect(url.searchParams.get('activeSessionId')).toBe('sess1234');
    expect(url.searchParams.get('visibleSessionIds')).toBe('sess5678');
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

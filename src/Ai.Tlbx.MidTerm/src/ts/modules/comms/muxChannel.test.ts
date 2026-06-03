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
    expect(frame?.byteLength).toBe(harness.constants.MUX_HEADER_SIZE + 3);
    expect(frame?.[harness.constants.MUX_HEADER_SIZE]).toBe(1);
    expect(
      new DataView(frame!.buffer, frame!.byteOffset, frame!.byteLength).getUint16(
        harness.constants.MUX_HEADER_SIZE + 1,
        true,
      ),
    ).toBe(41);
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

  it('keeps hidden background xterm sessions buffer-only and requests replay on visibility', async () => {
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

    expect(terminal.writeMock).not.toHaveBeenCalled();
    expect(getBrowserTransportSnapshot(backgroundSessionId)?.receivedSeq ?? 0n).toBe(0n);
    expect(harness.sessionTerminals.get(backgroundSessionId)?.serverCols).toBe(80);
    expect(harness.sessionTerminals.get(backgroundSessionId)?.serverRows).toBe(24);

    harness.ws.send.mockClear();
    harness.updateTerminalVisibility('sess1234', [backgroundSessionId]);

    const frames = harness.ws.send.mock.calls.map((call) => call[0] as Uint8Array);
    expect(frames.some((frame) => frame[0] === harness.constants.MUX_TYPE_VISIBLE_SESSIONS_HINT)).toBe(
      true,
    );
    expect(
      frames.some(
        (frame) =>
          frame[0] === harness.constants.MUX_TYPE_BUFFER_REQUEST &&
          harness.decodeSessionId(frame, 1) === backgroundSessionId,
      ),
    ).toBe(true);
  });

  it('requests replay before accepting live output after unopened background frames were skipped', async () => {
    const harness = await loadHarness([0, 0, 0, 0, 0, 0]);
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
    expect(getBrowserTransportSnapshot(backgroundSessionId)?.receivedSeq ?? 0n).toBe(0n);

    const terminal = attachFakeTerminal(harness.sessionTerminals, backgroundSessionId, 24, true);
    harness.ws.send.mockClear();
    harness.updateTerminalVisibility('sess1234', [backgroundSessionId]);

    const backgroundReplayRequest = harness.ws.send.mock.calls
      .map((call) => call[0] as Uint8Array)
      .find(
        (frame) =>
          frame[0] === harness.constants.MUX_TYPE_BUFFER_REQUEST &&
          harness.decodeSessionId(frame, 1) === backgroundSessionId,
      );
    expect(backgroundReplayRequest).toBeDefined();

    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        backgroundSessionId,
        9n,
        'live',
      ),
    } as MessageEvent<ArrayBuffer>);

    await Promise.resolve();
    expect(terminal.writeMock).not.toHaveBeenCalled();
    expect(getBrowserTransportSnapshot(backgroundSessionId)?.receivedSeq ?? 0n).toBe(0n);

    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        backgroundSessionId,
        0n,
        '\x1b[0m',
      ),
    } as MessageEvent<ArrayBuffer>);
    harness.ws.onmessage?.({
      data: buildSequencedOutputMessage(
        harness.encodeSessionId,
        harness.constants.MUX_TYPE_OUTPUT,
        harness.constants.MUX_HEADER_SIZE,
        backgroundSessionId,
        9n,
        'firstlive',
      ),
    } as MessageEvent<ArrayBuffer>);

    await Promise.resolve();

    expect(terminal.writeMock).toHaveBeenCalledTimes(1);
    expect(new TextDecoder().decode(terminal.writeMock.mock.calls[0]?.[0] as Uint8Array)).toBe(
      '\x1b[0mfirstlive',
    );
    expect(getBrowserTransportSnapshot(backgroundSessionId)?.receivedSeq).toBe(9n);
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

  it('sends sampled input trace markers before normal input when tracing is enabled', async () => {
    const harness = await loadHarness([10, 10, 10, 10]);

    setInputLatencyTracingEnabled(true);
    harness.ws.send.mockClear();

    sendInput('sess1234', 'a');

    expect(harness.ws.send).toHaveBeenCalledTimes(3);
    const frames = harness.ws.send.mock.calls.map((call) => call[0] as Uint8Array);
    expect(frames[0]?.[0]).toBe(harness.constants.MUX_TYPE_ACTIVE_HINT);
    expect(frames[1]?.[0]).toBe(harness.constants.MUX_TYPE_INPUT_TRACE_MARKER);
    expect(frames[2]?.[0]).toBe(harness.constants.MUX_TYPE_INPUT);
    expect(frames[2]?.[harness.constants.MUX_HEADER_SIZE]).toBe('a'.charCodeAt(0));

    const markerView = new DataView(
      frames[1]!.buffer,
      frames[1]!.byteOffset,
      frames[1]!.byteLength,
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

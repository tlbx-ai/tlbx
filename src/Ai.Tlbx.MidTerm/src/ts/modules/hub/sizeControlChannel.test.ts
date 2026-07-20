import { describe, expect, it, vi } from 'vitest';

const storeMocks = vi.hoisted(() => ({
  removeTerminalSizeControlSource: vi.fn(),
  setTerminalSizeControl: vi.fn(),
  setTerminalSizeControlsForSource: vi.fn(),
}));

vi.mock('../../stores', () => storeMocks);
vi.mock('../../utils', () => ({
  createWsUrl: (path: string) => `ws://midterm.test${path}`,
}));

class MockWebSocket {
  public static readonly CONNECTING = 0;
  public static readonly OPEN = 1;
  public static readonly CLOSING = 2;
  public static readonly CLOSED = 3;
  public static instances: MockWebSocket[] = [];

  public readyState = MockWebSocket.CONNECTING;
  public onopen: (() => void) | null = null;
  public onmessage: ((event: MessageEvent<string>) => void) | null = null;
  public onclose: (() => void) | null = null;
  public onerror: (() => void) | null = null;
  public send = vi.fn();
  public close = vi.fn();

  public constructor(public readonly url: string) {
    MockWebSocket.instances.push(this);
  }

  public open(): void {
    this.readyState = MockWebSocket.OPEN;
    this.onopen?.();
  }

  public receive(data: unknown): void {
    this.onmessage?.({ data: JSON.stringify(data) } as MessageEvent<string>);
  }
}

describe('Hub size-control channel', () => {
  it('projects remote ownership and sends commands to the terminal host', async () => {
    vi.stubGlobal('WebSocket', MockWebSocket);
    const { requestHubTerminalSizeControl, syncHubSizeControlMachines } = await import(
      './sizeControlChannel'
    );

    syncHubSizeControlMachines([
      {
        machine: { id: 'home', enabled: true },
        status: 'online',
        requiresTrust: false,
        fingerprintMismatch: false,
      } as never,
    ]);
    const socket = MockWebSocket.instances[0];
    expect(socket.url).toBe('ws://midterm.test/ws/hub/state?machineId=home');
    socket.open();
    socket.receive({
      terminalSizeControls: [
        {
          sessionId: 'abc12345',
          isOwner: false,
          hasOwner: true,
          ownerOnline: true,
          canTakeOverAutomatically: false,
          epoch: 3,
        },
      ],
    });

    expect(storeMocks.setTerminalSizeControlsForSource).toHaveBeenCalledWith('hub:home', [
      expect.objectContaining({ sessionId: 'hub:home:abc12345', epoch: 3 }),
    ]);

    const resultPromise = requestHubTerminalSizeControl('hub:home:abc12345', true);
    await vi.waitFor(() => expect(socket.send).toHaveBeenCalledOnce());
    const command = JSON.parse(socket.send.mock.calls[0][0] as string) as {
      id: string;
      action: string;
      payload: { sessionId: string; force: boolean };
    };
    expect(command).toMatchObject({
      action: 'terminal.requestSizeControl',
      payload: { sessionId: 'abc12345', force: true },
    });

    socket.receive({
      type: 'response',
      id: command.id,
      success: true,
      data: {
        status: {
          sessionId: 'abc12345',
          isOwner: true,
          hasOwner: true,
          ownerOnline: true,
          canTakeOverAutomatically: true,
          epoch: 4,
        },
        ownershipChanged: true,
        resizeApplied: false,
        cols: 0,
        rows: 0,
      },
    });

    await expect(resultPromise).resolves.toMatchObject({
      status: { sessionId: 'hub:home:abc12345', isOwner: true, epoch: 4 },
      ownershipChanged: true,
    });
    expect(storeMocks.setTerminalSizeControl).toHaveBeenCalledWith(
      expect.objectContaining({ sessionId: 'hub:home:abc12345', isOwner: true }),
    );
  });
});

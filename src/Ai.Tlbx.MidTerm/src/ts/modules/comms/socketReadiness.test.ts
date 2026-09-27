import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';
import { SOCKET_PROGRESS_TIMEOUT_MS, SocketReadiness } from './socketReadiness';

describe('connection readiness deadline', () => {
  let page: EventTarget & { visibilityState: string };
  beforeEach(() => {
    vi.useFakeTimers();
    page = Object.assign(new EventTarget(), { visibilityState: 'visible' });
    vi.stubGlobal('document', page);
  });
  afterEach(() => {
    vi.useRealTimers();
    vi.unstubAllGlobals();
  });

  it('replaces a connection that never becomes ready without waiting for a close event', () => {
    const socket = new EventTarget() as WebSocket;
    const reconnect = vi.fn();
    new SocketReadiness().watch(
      socket,
      () => true,
      () => false,
      reconnect,
    );
    vi.advanceTimersByTime(SOCKET_PROGRESS_TIMEOUT_MS);
    expect(reconnect).toHaveBeenCalledOnce();
    vi.advanceTimersByTime(SOCKET_PROGRESS_TIMEOUT_MS * 2);
    expect(reconnect).toHaveBeenCalledOnce();
  });

  it('allows progressing replay and stops once parsing completes', () => {
    const socket = new EventTarget() as WebSocket;
    const reconnect = vi.fn();
    let ready = false;
    new SocketReadiness().watch(
      socket,
      () => true,
      () => ready,
      reconnect,
    );
    vi.advanceTimersByTime(SOCKET_PROGRESS_TIMEOUT_MS - 1);
    socket.dispatchEvent(new Event('message'));
    vi.advanceTimersByTime(SOCKET_PROGRESS_TIMEOUT_MS - 1);
    ready = true;
    vi.advanceTimersByTime(SOCKET_PROGRESS_TIMEOUT_MS);
    expect(reconnect).not.toHaveBeenCalled();
  });

  it('allows a slower replacement to finish instead of repeatedly aborting its handshake', () => {
    const readiness = new SocketReadiness();
    const reconnect = vi.fn();
    readiness.watch(
      new EventTarget() as WebSocket,
      () => true,
      () => false,
      reconnect,
    );
    vi.advanceTimersByTime(SOCKET_PROGRESS_TIMEOUT_MS);
    expect(reconnect).toHaveBeenCalledOnce();
    const replacement = new EventTarget() as WebSocket;
    let ready = false;
    readiness.watch(
      replacement,
      () => true,
      () => ready,
      reconnect,
    );
    vi.advanceTimersByTime(2500);
    expect(reconnect).toHaveBeenCalledOnce();
    ready = true;
    replacement.dispatchEvent(new Event('message'));
    vi.advanceTimersByTime(10000);
    expect(reconnect).toHaveBeenCalledOnce();
  });

  it('gives a visible page a fresh deadline and ignores retired sockets', () => {
    const socket = new EventTarget() as WebSocket;
    const reconnect = vi.fn();
    let current = true;
    new SocketReadiness().watch(
      socket,
      () => current,
      () => false,
      reconnect,
    );
    page.visibilityState = 'hidden';
    page.dispatchEvent(new Event('visibilitychange'));
    vi.advanceTimersByTime(60000);
    expect(reconnect).not.toHaveBeenCalled();
    page.visibilityState = 'visible';
    page.dispatchEvent(new Event('visibilitychange'));
    vi.advanceTimersByTime(SOCKET_PROGRESS_TIMEOUT_MS - 1);
    expect(reconnect).not.toHaveBeenCalled();
    current = false;
    vi.advanceTimersByTime(1);
    expect(reconnect).not.toHaveBeenCalled();
  });
});

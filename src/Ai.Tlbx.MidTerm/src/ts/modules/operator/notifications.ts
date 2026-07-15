import { getHubMachines } from '../hub/runtime';
import { fetchControlPlaneEvents, type ControlPlaneEvent } from './controlPlaneApi';

const cursors = new Map<string, number>();
const activeNotifications = new Map<string, Notification>();
let timer: number | null = null;
let abortController: AbortController | null = null;
let unreadCount = 0;
let openOperator: (() => void) | null = null;
let stopped = false;

export function initControlPlaneNotifications(onOpenOperator: () => void): void {
  if (openOperator) return;
  openOperator = onOpenOperator;
  stopped = false;
  schedule(500);
  window.addEventListener('beforeunload', stop);
}

export function clearControlPlaneNotificationBadge(): void {
  unreadCount = 0;
  syncBadge();
}

async function poll(): Promise<void> {
  abortController?.abort();
  const controller = new AbortController();
  abortController = controller;
  const machines = getHubMachines().filter(
    (machine) => machine.machine.enabled && machine.status === 'online' && !machine.requiresTrust,
  );
  const origins = [
    { key: 'local', machineId: null, name: 'tlbx' },
    ...machines.map((machine) => ({
      key: `hub:${machine.machine.id}`,
      machineId: machine.machine.id,
      name: machine.machine.name,
    })),
  ];
  const activeKeys = new Set(origins.map((origin) => origin.key));
  for (const key of cursors.keys()) {
    if (!activeKeys.has(key)) cursors.delete(key);
  }

  await Promise.allSettled(
    origins.map(async (origin) => {
      const knownCursor = cursors.get(origin.key);
      const response = await fetchControlPlaneEvents(
        knownCursor ?? 0,
        100,
        origin.machineId,
        controller.signal,
      );
      if (knownCursor === undefined) {
        cursors.set(origin.key, response.latestSequence);
        return;
      }

      for (const event of response.events) {
        handleEvent(origin.key, origin.name, event);
        cursors.set(origin.key, event.sequence);
      }
      if (response.events.length === 0) cursors.set(origin.key, response.latestSequence);
    }),
  );
  if (abortController === controller) abortController = null;
  schedule(document.hidden ? 30000 : 10000);
}

function handleEvent(originKey: string, originName: string, event: ControlPlaneEvent): void {
  const notify =
    (event.type === 'sessionStatusPublished' &&
      (event.state === 'needsInput' || event.state === 'blocked' || event.state === 'done')) ||
    (event.type === 'workItemCreated' && event.priority === 'urgent');
  if (!notify) return;

  unreadCount++;
  syncBadge();
  if (!('Notification' in window) || Notification.permission !== 'granted') return;

  const key = `${originKey}:${event.sessionId ?? event.entityId}`;
  activeNotifications.get(key)?.close();
  const title = `${originName} · ${event.state ?? event.priority ?? event.type}`;
  const notification = new Notification(title, {
    body: event.summary,
    tag: `midterm-control-plane-${key}`,
  });
  activeNotifications.set(key, notification);
  notification.onclick = () => {
    window.focus();
    openOperator?.();
    notification.close();
  };
  notification.onclose = () => {
    activeNotifications.delete(key);
  };
  window.setTimeout(() => {
    notification.close();
  }, 15000);
}

function syncBadge(): void {
  const badge = document.getElementById('operator-notification-badge');
  if (!badge) return;
  badge.hidden = unreadCount === 0;
  badge.textContent = unreadCount > 99 ? '99+' : unreadCount.toString();
}

function schedule(delay: number): void {
  if (stopped) return;
  if (timer !== null) window.clearTimeout(timer);
  timer = window.setTimeout(() => {
    timer = null;
    void poll();
  }, delay);
}

function stop(): void {
  stopped = true;
  if (timer !== null) window.clearTimeout(timer);
  timer = null;
  abortController?.abort();
  abortController = null;
  for (const notification of activeNotifications.values()) notification.close();
  activeNotifications.clear();
}

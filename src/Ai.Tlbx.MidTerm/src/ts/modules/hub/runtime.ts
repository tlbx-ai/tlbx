import type { Session } from '../../api/types';
import type { HubMachineState, HubSessionRecord } from './types';
import { getHubState } from './api';
import { syncHubSizeControlMachines } from './sizeControlChannel';

let machines: HubMachineState[] = [];
const sessionRecords = new Map<string, HubSessionRecord>();
const listeners = new Set<() => void>();
let refreshTimer: number | null = null;

function notify(): void {
  listeners.forEach((listener) => {
    listener();
  });
}

function toCompositeId(machineId: string, sessionId: string): string {
  return `hub:${machineId}:${sessionId}`;
}

function isMachineLaunchable(machine: HubMachineState): boolean {
  return (
    machine.machine.enabled &&
    machine.status === 'online' &&
    !machine.requiresTrust &&
    !machine.fingerprintMismatch
  );
}

function toSession(session: HubSessionRecord['session']): Session {
  return {
    ...session,
    _order: session.order,
  };
}

function rebuildSessionRecords(): void {
  sessionRecords.clear();
  for (const machine of machines) {
    for (const session of machine.sessions) {
      const compositeId = toCompositeId(machine.machine.id, session.id);
      sessionRecords.set(compositeId, {
        compositeId,
        machineId: machine.machine.id,
        machineName: machine.machine.name,
        remoteSessionId: session.id,
        session,
      });
    }
  }
}

export async function refreshHubState(): Promise<void> {
  const state = await getHubState();
  machines = state.machines;
  rebuildSessionRecords();
  syncHubSizeControlMachines(machines);
  notify();
}

export function initHubRuntime(): void {
  if (refreshTimer !== null) {
    return;
  }

  void refreshHubState().catch(() => {});
  refreshTimer = window.setInterval(() => {
    void refreshHubState().catch(() => {});
  }, 10000);
}

export function subscribeHubState(listener: () => void): () => void {
  listeners.add(listener);
  return () => listeners.delete(listener);
}

export function isHubSessionId(sessionId: string | null | undefined): boolean {
  return !!sessionId?.startsWith('hub:');
}

export function getHubMachines(): HubMachineState[] {
  return machines;
}

export function getLaunchableHubMachines(): HubMachineState[] {
  return machines.filter(isMachineLaunchable);
}

export function toHubCompositeId(machineId: string, sessionId: string): string {
  return toCompositeId(machineId, sessionId);
}

export function getHubSessionRecord(sessionId: string): HubSessionRecord | null {
  return sessionRecords.get(sessionId) ?? null;
}

export function getHubSession(sessionId: string): Session | null {
  const record = getHubSessionRecord(sessionId);
  return record ? toSession(record.session) : null;
}

export function getHubSidebarSections(): Array<{
  machine: HubMachineState;
  sessions: Array<Session & { id: string }>;
}> {
  return machines
    .filter((machine) => machine.machine.enabled)
    .map((machine) => ({
      machine,
      sessions: machine.sessions.map((session) => ({
        ...toSession(session),
        id: toCompositeId(machine.machine.id, session.id),
      })),
    }));
}

export function getFirstHubSessionId(): string | null {
  for (const machine of getHubSidebarSections()) {
    const first = machine.sessions[0];
    if (first?.id) {
      return first.id;
    }
  }

  return null;
}

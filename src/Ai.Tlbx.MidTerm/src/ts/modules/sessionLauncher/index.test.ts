import { describe, expect, it } from 'vitest';
import type { HubMachineState } from '../hub/types';
import type { HubSessionLauncherTarget } from './index';

describe('session launcher target selection', () => {
  it('includes local plus launchable remote machines', async () => {
    const { buildSessionLauncherTargets } = await import('./index');

    const targets = buildSessionLauncherTargets([
      {
        machine: {
          id: 'm1',
          name: 'Build Box',
          baseUrl: 'https://build-box:2000',
          enabled: true,
          hasApiKey: true,
          hasPassword: false,
          lastFingerprint: null,
          pinnedFingerprint: null,
        },
        status: 'online',
        error: null,
        fingerprintMismatch: false,
        requiresTrust: false,
        currentVersion: '1.0.0',
        latestVersion: '1.0.0',
        updateAvailable: false,
        sessions: [],
      },
    ]);

    expect(targets).toEqual([
      {
        id: 'local',
        kind: 'local',
      },
      {
        id: 'hub:m1',
        kind: 'hub',
        machineId: 'm1',
        machineName: 'Build Box',
        baseUrl: 'https://build-box:2000',
        currentVersion: '1.0.0',
      },
    ]);
  });

  it('allows AppServerControl providers locally but only Terminal remotely', async () => {
    const { isProviderSupportedOnTarget } = await import('./index');

    const remoteTarget: HubSessionLauncherTarget = {
      id: 'hub:m1',
      kind: 'hub',
      machineId: 'm1',
      machineName: 'Build Box',
      baseUrl: 'https://build-box:2000',
      currentVersion: '1.0.0',
    };

    expect(isProviderSupportedOnTarget('terminal', { id: 'local', kind: 'local' })).toBe(true);
    expect(isProviderSupportedOnTarget('codex', { id: 'local', kind: 'local' })).toBe(true);
    expect(isProviderSupportedOnTarget('claude', { id: 'local', kind: 'local' })).toBe(true);
    expect(isProviderSupportedOnTarget('grok', { id: 'local', kind: 'local' })).toBe(true);

    expect(isProviderSupportedOnTarget('terminal', remoteTarget)).toBe(true);
    expect(isProviderSupportedOnTarget('codex', remoteTarget)).toBe(false);
    expect(isProviderSupportedOnTarget('claude', remoteTarget)).toBe(false);
    expect(isProviderSupportedOnTarget('grok', remoteTarget)).toBe(false);
  });

  it('only warns when the remote target differs on major and minor version', async () => {
    const { hasMatchingMajorMinorVersion } = await import('./index');

    expect(hasMatchingMajorMinorVersion('9.1.23-dev', '9.1.0')).toBe(true);
    expect(hasMatchingMajorMinorVersion('9.1.23-dev', '9.2.0')).toBe(false);
    expect(hasMatchingMajorMinorVersion('9.1.23-dev', '10.1.0')).toBe(false);
    expect(hasMatchingMajorMinorVersion('9.1.23-dev', null)).toBe(true);
  });
});

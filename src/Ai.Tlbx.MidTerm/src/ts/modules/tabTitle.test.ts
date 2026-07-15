import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

import type { MidTermSettingsPublic } from '../api/types';
import { $activeSessionId, $currentSettings, $serverHostname, $sessions } from '../stores';

vi.mock('./process', () => ({
  addProcessStateListener: vi.fn(() => () => {}),
}));

import { cleanupTabTitle, initTabTitle } from './tabTitle';

const originalDocument = globalThis.document;

function setSettings(partial: Partial<Pick<MidTermSettingsPublic, 'tabTitleMode'>>): void {
  $currentSettings.set(partial as MidTermSettingsPublic);
}

beforeEach(() => {
  Object.defineProperty(globalThis, 'document', {
    value: { title: '' },
    configurable: true,
    writable: true,
  });

  $sessions.set({});
  $activeSessionId.set(null);
  $currentSettings.set(null);
  $serverHostname.set('');
  cleanupTabTitle();
});

afterEach(() => {
  cleanupTabTitle();
  Object.defineProperty(globalThis, 'document', {
    value: originalDocument,
    configurable: true,
    writable: true,
  });
});

describe('tabTitle', () => {
  it('updates immediately when tab title mode changes', () => {
    $serverHostname.set('alpha');
    setSettings({ tabTitleMode: 'hostname' });

    initTabTitle();
    expect(document.title).toBe('tlbx — alpha');

    setSettings({ tabTitleMode: 'static' });
    expect(document.title).toBe('tlbx');
  });

  it('updates immediately when the hostname changes in hostname mode', () => {
    setSettings({ tabTitleMode: 'hostname' });
    $serverHostname.set('alpha');

    initTabTitle();
    expect(document.title).toBe('tlbx — alpha');

    $serverHostname.set('beta');
    expect(document.title).toBe('tlbx — beta');
  });
});

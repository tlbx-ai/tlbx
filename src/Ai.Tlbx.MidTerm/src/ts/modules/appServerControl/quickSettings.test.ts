import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../../api/client', () => ({
  updateSettings: vi.fn().mockResolvedValue({ response: { ok: true } }),
}));

import { updateSettings } from '../../api/client';
import type { MidTermSettingsPublic } from '../../api/types';
import { $currentSettings, $sessions } from '../../stores';
import {
  createAppServerControlTurnRequestWithQuickSettings,
  getAppServerControlQuickSettingsDraft,
  removeAppServerControlQuickSettingsSessionState,
  setAppServerControlQuickSettingsDraft,
} from './quickSettings';

function createMemoryStorage(): Storage {
  const values = new Map<string, string>();
  return {
    get length() {
      return values.size;
    },
    clear() {
      values.clear();
    },
    getItem(key) {
      return values.has(key) ? values.get(key)! : null;
    },
    key(index) {
      return Array.from(values.keys())[index] ?? null;
    },
    removeItem(key) {
      values.delete(key);
    },
    setItem(key, value) {
      values.set(key, value);
    },
  };
}

function createSettings(patch: Partial<MidTermSettingsPublic> = {}): MidTermSettingsPublic {
  return {
    codexYoloDefault: false,
    codexDefaultAppServerControlModel: '',
    codexEnvironmentVariables: '',
    ...patch,
  } as MidTermSettingsPublic;
}

describe('appServerControl quick settings', () => {
  beforeEach(() => {
    vi.stubGlobal('localStorage', createMemoryStorage());
    globalThis.localStorage.clear();
    $sessions.set({});
    $currentSettings.set(createSettings());
    vi.mocked(updateSettings).mockClear();
  });

  afterEach(() => {
    removeAppServerControlQuickSettingsSessionState('codex-default');
    removeAppServerControlQuickSettingsSessionState('codex-save');
    removeAppServerControlQuickSettingsSessionState('grok-stale');
    $sessions.set({});
    globalThis.localStorage.clear();
    vi.unstubAllGlobals();
  });

  it('persists Codex fast mode and includes it in the next turn request', () => {
    $sessions.set({
      'codex-default': {
        id: 'codex-default',
        profileHint: 'codex',
      } as never,
    });

    setAppServerControlQuickSettingsDraft('codex-default', { fastMode: 'on' });

    expect(getAppServerControlQuickSettingsDraft('codex-default').fastMode).toBe('on');
    expect(
      createAppServerControlTurnRequestWithQuickSettings(
        'codex-default',
        'Use the fast service tier.',
      ).fastMode,
    ).toBe('on');
  });

  it('preserves ACP agent model ids without provider-specific alias rewriting', () => {
    globalThis.localStorage.setItem(
      'midterm:appServerControl-quick-settings:provider:grok',
      JSON.stringify({ model: 'grok-build' }),
    );
    $sessions.set({
      'grok-stale': {
        id: 'grok-stale',
        profileHint: 'grok',
      } as never,
    });

    expect(getAppServerControlQuickSettingsDraft('grok-stale').model).toBe('grok-build');
  });
});

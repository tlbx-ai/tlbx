import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

vi.mock('../../api/client', () => ({
  updateSettings: vi.fn().mockResolvedValue({ response: { ok: true } }),
}));

import type { MidTermSettingsPublic } from '../../api/types';
import { updateSettings } from '../../api/client';
import { $currentSettings, $sessions } from '../../stores';
import {
  getAppServerControlResolvedProviderModel,
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
    claudeDangerouslySkipPermissionsDefault: false,
    claudeDefaultAppServerControlModel: '',
    claudeEnvironmentVariables: '',
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
    $sessions.set({});
    globalThis.localStorage.clear();
    vi.unstubAllGlobals();
  });

  it('defaults new codex drafts to gpt-5.5 when no stored model exists', () => {
    $sessions.set({
      'codex-default': {
        id: 'codex-default',
        profileHint: 'codex',
      } as never,
    });

    expect(getAppServerControlQuickSettingsDraft('codex-default').model).toBe('gpt-5.5');
  });

  it('resolves the concrete provider model for default codex AppServerControl sessions', () => {
    expect(getAppServerControlResolvedProviderModel('codex')).toBe('gpt-5.5');
  });

  it('persists the selected provider model into MidTerm settings', () => {
    $sessions.set({
      'codex-save': {
        id: 'codex-save',
        profileHint: 'codex',
      } as never,
    });

    setAppServerControlQuickSettingsDraft('codex-save', { model: 'gpt-5.4-codex' });

    expect($currentSettings.get()?.codexDefaultAppServerControlModel).toBe('gpt-5.4-codex');
    expect(vi.mocked(updateSettings)).toHaveBeenCalledTimes(1);
    expect(vi.mocked(updateSettings).mock.calls[0]?.[0]).toMatchObject({
      codexDefaultAppServerControlModel: 'gpt-5.4-codex',
    });
  });
});

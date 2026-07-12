import { describe, expect, it, vi } from 'vitest';

vi.mock('../diagnostics', () => ({
  startLatencyMeasurement: vi.fn(),
  stopLatencyMeasurement: vi.fn(),
}));

import { normalizeStoredSettingsTab } from './tabs';

describe('settings tab migration', () => {
  it('maps legacy tabs to their current destinations', () => {
    const migrations = new Map([
      ['general', 'updates'],
      ['hub', 'connected-hosts'],
      ['command-bay', 'workflow'],
      ['agent', 'ai-agents'],
      ['diagnostics', 'advanced'],
      ['behavior', 'workflow'],
      ['agent-ui', 'ai-agents'],
    ]);

    for (const [legacy, expected] of migrations) {
      expect(normalizeStoredSettingsTab(legacy)).toBe(expected);
    }
  });

  it('accepts current tabs unchanged', () => {
    for (const tab of [
      'updates',
      'sessions',
      'appearance',
      'workflow',
      'terminal',
      'ai-agents',
      'security',
      'connected-hosts',
      'advanced',
    ]) {
      expect(normalizeStoredSettingsTab(tab)).toBe(tab);
    }
  });

  it('rejects missing and unknown tabs', () => {
    expect(normalizeStoredSettingsTab(null)).toBeNull();
    expect(normalizeStoredSettingsTab('obsolete')).toBeNull();
  });
});

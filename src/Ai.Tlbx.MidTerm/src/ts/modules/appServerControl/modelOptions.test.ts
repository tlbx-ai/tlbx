import { describe, expect, it } from 'vitest';

import { getAppServerControlModelOptions } from './modelOptions';

describe('appServerControl model options', () => {
  it('uses a live catalog before the static Codex fallback', () => {
    expect(
      getAppServerControlModelOptions({
        provider: 'codex',
        catalogOptions: [
          { value: 'GPT-5.5', label: 'GPT-5.5' },
          { value: ' gpt-5.4 ', label: 'GPT-5.4' },
          { value: 'gpt-5.6-sol', label: 'GPT-5.6 Sol' },
          { value: 'gpt-live', label: 'GPT Live' },
          { value: 'gpt-live', label: 'duplicate' },
          { value: 'gpt-5.5', label: 'duplicate-case' },
        ],
      }),
    ).toEqual([
      { value: '', label: 'Default Codex model' },
      { value: 'gpt-5.5', label: 'GPT-5.5', description: null },
      { value: 'gpt-5.4', label: 'GPT-5.4', description: null },
      { value: 'gpt-5.6-sol', label: 'GPT-5.6 Sol', description: null },
      { value: 'gpt-live', label: 'GPT Live', description: null },
    ]);
  });

  it('normalizes active custom model casing instead of duplicating a catalog model', () => {
    expect(
      getAppServerControlModelOptions({
        provider: 'codex',
        catalogOptions: [{ value: 'gpt-5.5', label: 'GPT-5.5' }],
        currentValues: ['GPT-5.5'],
      }),
    ).toEqual([
      { value: '', label: 'Default Codex model' },
      { value: 'gpt-5.5', label: 'GPT-5.5', description: null },
    ]);
  });

  it('preserves active custom models that are not in the preset list', () => {
    expect(
      getAppServerControlModelOptions({
        provider: 'claude',
        currentValues: [' claude-custom-experimental ', 'claude-opus-4-6'],
      }).map((option) => option.value),
    ).toEqual([
      '',
      'sonnet',
      'opus',
      'claude-sonnet-4-6',
      'claude-opus-4-6',
      'claude-custom-experimental',
    ]);
  });
});

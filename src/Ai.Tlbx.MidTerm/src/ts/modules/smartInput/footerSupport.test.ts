import { describe, expect, it, vi } from 'vitest';

vi.mock('../i18n', () => ({ t: (key: string) => key }));
vi.mock('../appServerControl/input', () => ({
  hasInterruptibleAppServerControlTurnWork: () => true,
}));
vi.mock('../appServerControl/quickSettings', () => ({
  getAppServerControlQuickSettingsDraft: () => ({
    model: 'B',
    effort: 'high',
    planMode: 'on',
    permissionMode: 'manual',
    fastMode: 'on',
  }),
  getAppServerControlQuickSettingsEffective: () => ({
    model: 'A',
    effort: 'medium',
    planMode: 'off',
    permissionMode: 'manual',
  }),
  getAppServerControlQuickSettingsProvider: () => 'codex',
  getAppServerControlResolvedProviderModel: () => 'A',
  getAppServerControlRuntimeSettingStatus: (_id: string, field: string) =>
    field === 'model' ? { state: 'pending', scope: 'subsequentTurns' } : undefined,
}));
vi.mock('./nativeConfigControls', () => ({ syncNativeConfigControls: vi.fn() }));
vi.mock('./smartInputView', () => ({
  formatAppServerControlQuickSettingsSummary: () => 'B / high',
  setAppServerControlQuickSettingsDropdownOptions: vi.fn(),
  setAppServerControlQuickSettingsDropdownDisabled: (
    select: { disabled: boolean },
    value: boolean,
  ) => {
    select.disabled = value;
  },
}));

import {
  formatRuntimeSettingStatus,
  syncAppServerControlQuickSettingsControls,
} from './footerSupport';

function control(value: string) {
  const trigger = { setAttribute: vi.fn(), removeAttribute: vi.fn() };
  const field = {
    title: '',
    dataset: {},
    hidden: false,
    setAttribute: vi.fn(),
    querySelector: () => trigger,
  };
  return {
    value,
    options: [{}, {}],
    disabled: true,
    textContent: '',
    closest: () => field,
    dispatchEvent: vi.fn(),
    setAttribute: vi.fn(),
    removeAttribute: vi.fn(),
    field,
  };
}

describe('runtime quick-settings controls', () => {
  it('keeps model effort plan permissions and fast editable while busy and exposes pending status on its control', () => {
    const model = control('B');
    const effort = control('high');
    const plan = control('on');
    const permission = control('manual');
    const fast = control('on');
    syncAppServerControlQuickSettingsControls({
      appServerControlQuickSettingsRow: { dataset: {} },
      appServerControlQuickSettingsActions: {},
      appServerControlModelSelect: model,
      appServerControlEffortSelect: effort,
      appServerControlPlanSelect: plan,
      appServerControlPermissionSelect: permission,
      appServerControlFastToggle: fast,
      appServerControlSettingsSummaryBtn: null,
      dockedBar: { dataset: {} },
      getVisibilityState: () => ({
        activeSessionId: 's',
        appServerControlActive: true,
        inputMode: 'smartinput',
      }),
      setAppServerControlQuickSettingsSheetOpen: vi.fn(),
    } as any);
    for (const element of [model, effort, plan, permission, fast])
      expect(element.disabled).toBe(false);
    expect(model.field.title).toBe('smartInput.settingPending');
    expect(model.field.setAttribute).toHaveBeenCalledWith('aria-busy', 'true');
    expect(model.field.dataset).toMatchObject({ configureState: 'pending' });
  });

  it('distinguishes accepted defaults from effective readback and retains partial scope', () => {
    expect(
      formatRuntimeSettingStatus({ state: 'acknowledged', scope: 'subsequentResponses' }),
    ).toContain('smartInput.settingAcknowledged');
    expect(formatRuntimeSettingStatus({ state: 'partial', scope: 'subsequentTurns' })).toContain(
      'smartInput.settingScopeTurns',
    );
    expect(formatRuntimeSettingStatus({ state: 'unconfirmed', scope: 'subsequentTurns' })).toBe(
      'smartInput.settingUnconfirmed',
    );
  });
});

/**
 * Settings Registry Module
 *
 * Central metadata registry for MidTerm settings. This is the authoritative
 * frontend map for editability, ownership, validation shape, and apply mode.
 */

import type { MidTermSettingsPublic } from '../../api/types';

export type SettingApplyMode = 'immediate' | 'lazy' | 'new-session' | 'server-only';
export type SettingSaveStrategy = 'control' | 'preserve' | 'readonly';
export type SettingControlType =
  | 'text'
  | 'textarea'
  | 'nullable-string'
  | 'int'
  | 'float'
  | 'select'
  | 'boolean-select'
  | 'shell-select'
  | 'checkbox';

export interface SettingsRegistryEntry {
  key: keyof MidTermSettingsPublic;
  editable: boolean;
  storage: string;
  validation: string;
  applyMode: SettingApplyMode;
  saveStrategy: SettingSaveStrategy;
  controlId?: string;
  controlType?: SettingControlType;
  fallbackValue?: unknown;
  specialWriter?: string;
}

export const VALID_SETTING_SHELLS = ['Pwsh', 'PowerShell', 'Cmd', 'Bash', 'Zsh'] as const;

function controlEntry(
  key: keyof MidTermSettingsPublic,
  controlId: string,
  controlType: SettingControlType,
  fallbackValue: unknown,
  metadata: Omit<
    SettingsRegistryEntry,
    'key' | 'controlId' | 'controlType' | 'fallbackValue' | 'saveStrategy'
  >,
): SettingsRegistryEntry {
  return {
    key,
    controlId,
    controlType,
    fallbackValue,
    saveStrategy: 'control',
    ...metadata,
  };
}

function preserveEntry(
  key: keyof MidTermSettingsPublic,
  fallbackValue: unknown,
  metadata: Omit<SettingsRegistryEntry, 'key' | 'fallbackValue' | 'saveStrategy'>,
): SettingsRegistryEntry {
  return {
    key,
    fallbackValue,
    saveStrategy: 'preserve',
    ...metadata,
  };
}

function readonlyEntry(
  key: keyof MidTermSettingsPublic,
  metadata: Omit<SettingsRegistryEntry, 'key' | 'editable' | 'saveStrategy'>,
): SettingsRegistryEntry {
  return {
    key,
    editable: false,
    saveStrategy: 'readonly',
    ...metadata,
  };
}

export const SETTINGS_REGISTRY: readonly SettingsRegistryEntry[] = [
  controlEntry('defaultShell', 'setting-default-shell', 'shell-select', 'Pwsh', {
    editable: true,
    storage: 'settings.json',
    validation: `one of: ${VALID_SETTING_SHELLS.join(', ')}`,
    applyMode: 'new-session',
  }),
  preserveEntry('defaultCols', 120, {
    editable: true,
    storage: 'settings.json',
    validation: 'positive integer',
    applyMode: 'new-session',
  }),
  preserveEntry('defaultRows', 30, {
    editable: true,
    storage: 'settings.json',
    validation: 'positive integer',
    applyMode: 'new-session',
  }),
  controlEntry('defaultWorkingDirectory', 'setting-working-dir', 'text', '', {
    editable: true,
    storage: 'settings.json',
    validation: 'string path, empty allowed',
    applyMode: 'new-session',
  }),
  controlEntry('worktreeRootDirectory', 'setting-worktree-root-dir', 'text', '', {
    editable: true,
    storage: 'settings.json',
    validation: 'string path, empty allowed',
    applyMode: 'new-session',
  }),
  controlEntry('terminalEnvironmentVariables', 'setting-terminal-env', 'textarea', '', {
    editable: true,
    storage: 'settings.json',
    validation: 'newline-delimited KEY=VALUE env vars',
    applyMode: 'new-session',
  }),
  controlEntry('codexYoloDefault', 'setting-codex-yolo-default', 'checkbox', false, {
    editable: true,
    storage: 'settings.json',
    validation: 'boolean',
    applyMode: 'new-session',
  }),
  preserveEntry('codexDefaultAppServerControlModel', '', {
    editable: true,
    storage: 'settings.json',
    validation: 'string model id, empty allowed',
    applyMode: 'new-session',
  }),
  controlEntry('codexEnvironmentVariables', 'setting-codex-env', 'textarea', '', {
    editable: true,
    storage: 'settings.json',
    validation: 'newline-delimited KEY=VALUE env vars',
    applyMode: 'new-session',
  }),
  controlEntry(
    'claudeDangerouslySkipPermissionsDefault',
    'setting-claude-dangerously-skip-permissions-default',
    'checkbox',
    false,
    {
      editable: true,
      storage: 'settings.json',
      validation: 'boolean',
      applyMode: 'new-session',
    },
  ),
  preserveEntry('claudeDefaultAppServerControlModel', '', {
    editable: true,
    storage: 'settings.json',
    validation: 'string model id, empty allowed',
    applyMode: 'new-session',
  }),
  controlEntry('claudeEnvironmentVariables', 'setting-claude-env', 'textarea', '', {
    editable: true,
    storage: 'settings.json',
    validation: 'newline-delimited KEY=VALUE env vars',
    applyMode: 'new-session',
  }),
  controlEntry('agentMessageFontFamily', 'setting-agent-message-font-family', 'select', 'default', {
    editable: true,
    storage: 'settings.json',
    validation: 'known agent message font family',
    applyMode: 'immediate',
  }),
  controlEntry(
    'showAgentMessageTimestamps',
    'setting-show-agent-message-timestamps',
    'checkbox',
    false,
    {
      editable: true,
      storage: 'settings.json',
      validation: 'boolean',
      applyMode: 'immediate',
    },
  ),
  controlEntry(
    'showUnknownAgentMessages',
    'setting-show-unknown-agent-messages',
    'checkbox',
    true,
    {
      editable: true,
      storage: 'settings.json',
      validation: 'boolean',
      applyMode: 'immediate',
    },
  ),
  controlEntry('toolCallOutputLines', 'setting-tool-call-output-lines', 'int', 5, {
    editable: true,
    storage: 'settings.json',
    validation: 'integer, UI clamps to 0-20',
    applyMode: 'immediate',
  }),
  controlEntry('fontSize', 'setting-font-size', 'int', 14, {
    editable: true,
    storage: 'settings.json',
    validation: 'integer, UI clamps to 8-24',
    applyMode: 'immediate',
  }),
  controlEntry('fontFamily', 'setting-font-family', 'select', 'Cascadia Code', {
    editable: true,
    storage: 'settings.json',
    validation: 'bundled font name',
    applyMode: 'immediate',
  }),
  controlEntry('terminalLigaturesEnabled', 'setting-terminal-ligatures-enabled', 'checkbox', true, {
    editable: true,
    storage: 'settings.json',
    validation: 'boolean',
    applyMode: 'immediate',
  }),
  controlEntry('boxDrawingStyle', 'setting-box-drawing-style', 'select', 'classic', {
    editable: true,
    storage: 'settings.json',
    validation: 'classic or rounded',
    applyMode: 'immediate',
  }),
  controlEntry('boxDrawingScale', 'setting-box-drawing-scale', 'float', 1, {
    editable: true,
    storage: 'settings.json',
    validation: 'float, clamped to 0.5-2.0',
    applyMode: 'immediate',
  }),
  controlEntry('lineHeight', 'setting-line-height', 'float', 1, {
    editable: true,
    storage: 'settings.json',
    validation: 'float, UI clamps to 0.8-3.0',
    applyMode: 'immediate',
  }),
  controlEntry('letterSpacing', 'setting-letter-spacing', 'float', 0, {
    editable: true,
    storage: 'settings.json',
    validation: 'float, clamped to -2-10',
    applyMode: 'immediate',
  }),
  controlEntry('fontWeight', 'setting-font-weight', 'select', 'normal', {
    editable: true,
    storage: 'settings.json',
    validation: 'normal, bold, or numeric weight',
    applyMode: 'immediate',
  }),
  controlEntry('fontWeightBold', 'setting-font-weight-bold', 'select', 'bold', {
    editable: true,
    storage: 'settings.json',
    validation: 'normal, bold, or numeric weight',
    applyMode: 'immediate',
  }),
  controlEntry('customGlyphs', 'setting-custom-glyphs', 'boolean-select', true, {
    editable: true,
    storage: 'settings.json',
    validation: 'boolean, rendered as custom or font box drawing',
    applyMode: 'immediate',
  }),
  controlEntry('cursorStyle', 'setting-cursor-style', 'select', 'block', {
    editable: true,
    storage: 'settings.json',
    validation: 'known cursor style',
    applyMode: 'immediate',
  }),
  controlEntry('cursorBlink', 'setting-cursor-blink', 'checkbox', true, {
    editable: true,
    storage: 'settings.json',
    validation: 'boolean',
    applyMode: 'immediate',
  }),
  controlEntry('cursorInactiveStyle', 'setting-cursor-inactive', 'select', 'none', {
    editable: true,
    storage: 'settings.json',
    validation: 'known inactive cursor style',
    applyMode: 'immediate',
  }),
  controlEntry('theme', 'setting-theme', 'select', 'dark', {
    editable: true,
    storage: 'settings.json',
    validation: 'known UI theme',
    applyMode: 'immediate',
  }),
  controlEntry('terminalColorScheme', 'setting-terminal-color-scheme', 'select', 'auto', {
    editable: true,
    storage: 'settings.json',
    validation: 'known terminal color scheme',
    applyMode: 'immediate',
  }),
  preserveEntry('terminalColorSchemes', [], {
    editable: true,
    storage: 'settings.json',
    validation: 'array of custom terminal color scheme definitions',
    applyMode: 'immediate',
    specialWriter: 'terminal color scheme editor',
  }),
  controlEntry('backgroundImageEnabled', 'setting-background-image-enabled', 'checkbox', false, {
    editable: true,
    storage: 'settings.json',
    validation: 'boolean',
    applyMode: 'immediate',
  }),
  controlEntry(
    'hideBackgroundImageOnMobile',
    'setting-hide-background-image-on-mobile',
    'checkbox',
    true,
    {
      editable: true,
      storage: 'settings.json',
      validation: 'boolean',
      applyMode: 'immediate',
    },
  ),
  readonlyEntry('backgroundImageFileName', {
    storage: 'settings.json',
    validation: 'endpoint-managed metadata',
    applyMode: 'server-only',
    specialWriter: 'background image upload/delete endpoints',
  }),
  readonlyEntry('backgroundImageRevision', {
    storage: 'settings.json',
    validation: 'endpoint-managed revision counter',
    applyMode: 'server-only',
    specialWriter: 'background image upload/delete endpoints',
  }),
  controlEntry(
    'backgroundKenBurnsEnabled',
    'setting-background-ken-burns-enabled',
    'checkbox',
    false,
    {
      editable: true,
      storage: 'settings.json',
      validation: 'boolean',
      applyMode: 'immediate',
    },
  ),
  controlEntry(
    'backgroundKenBurnsZoomPercent',
    'setting-background-ken-burns-zoom-percent',
    'int',
    150,
    {
      editable: true,
      storage: 'settings.json',
      validation: 'integer, clamped to 150-300',
      applyMode: 'immediate',
    },
  ),
  controlEntry(
    'backgroundKenBurnsSpeedPxPerSecond',
    'setting-background-ken-burns-speed',
    'int',
    12,
    {
      editable: true,
      storage: 'settings.json',
      validation: 'integer, clamped to 0-120',
      applyMode: 'immediate',
    },
  ),
  controlEntry('uiTransparency', 'setting-ui-transparency', 'int', 0, {
    editable: true,
    storage: 'settings.json',
    validation: 'integer, clamped to 0-100',
    applyMode: 'immediate',
  }),
  controlEntry('terminalTransparency', 'setting-terminal-transparency', 'int', 0, {
    editable: true,
    storage: 'settings.json',
    validation: 'integer, clamped to 0-100',
    applyMode: 'immediate',
  }),
  controlEntry(
    'terminalCellBackgroundTransparency',
    'setting-terminal-cell-background-transparency',
    'int',
    0,
    {
      editable: true,
      storage: 'settings.json',
      validation: 'integer, clamped to 0-100',
      applyMode: 'immediate',
    },
  ),
  preserveEntry('minimumContrastRatio', 1, {
    editable: true,
    storage: 'settings.json',
    validation: 'double, preserved until a dedicated control writes it',
    applyMode: 'immediate',
  }),
  controlEntry('tabTitleMode', 'setting-tab-title', 'select', 'hostname', {
    editable: true,
    storage: 'settings.json',
    validation: 'known tab-title mode',
    applyMode: 'immediate',
  }),
  controlEntry('smoothScrolling', 'setting-smooth-scrolling', 'checkbox', false, {
    editable: true,
    storage: 'settings.json',
    validation: 'boolean',
    applyMode: 'immediate',
  }),
  controlEntry('scrollbarStyle', 'setting-scrollbar-style', 'select', 'off', {
    editable: true,
    storage: 'settings.json',
    validation: 'known scrollbar style',
    applyMode: 'immediate',
  }),
  controlEntry('useWebGL', 'setting-webgl', 'checkbox', true, {
    editable: true,
    storage: 'settings.json',
    validation: 'boolean',
    applyMode: 'immediate',
  }),
  controlEntry('scrollbackLines', 'setting-scrollback', 'int', 2000, {
    editable: true,
    storage: 'settings.json',
    validation: 'integer, UI clamps to 500-10000',
    applyMode: 'immediate',
  }),
  controlEntry('scrollbackBytes', 'setting-scrollback-bytes', 'int', 2 * 1024 * 1024, {
    editable: true,
    storage: 'settings.json',
    validation: 'integer bytes, UI clamps to 65536-10485760',
    applyMode: 'immediate',
  }),
  controlEntry('bellStyle', 'setting-bell-style', 'select', 'notification', {
    editable: true,
    storage: 'settings.json',
    validation: 'known bell style',
    applyMode: 'lazy',
  }),
  controlEntry('copyOnSelect', 'setting-copy-on-select', 'checkbox', false, {
    editable: true,
    storage: 'settings.json',
    validation: 'boolean',
    applyMode: 'lazy',
  }),
  controlEntry('rightClickPaste', 'setting-right-click-paste', 'checkbox', true, {
    editable: true,
    storage: 'settings.json',
    validation: 'boolean',
    applyMode: 'lazy',
  }),
  controlEntry('clipboardShortcuts', 'setting-clipboard-shortcuts', 'select', 'auto', {
    editable: true,
    storage: 'settings.json',
    validation: 'known clipboard shortcut mode',
    applyMode: 'lazy',
  }),
  controlEntry('terminalEnterMode', 'setting-terminal-enter-mode', 'select', 'shiftEnterLineFeed', {
    editable: true,
    storage: 'settings.json',
    validation: 'off, or remap modified Enter to newline-compatible bytes in terminal sessions',
    applyMode: 'immediate',
  }),
  controlEntry('scrollbackProtection', 'setting-scrollback-protection', 'checkbox', false, {
    editable: true,
    storage: 'settings.json',
    validation: 'boolean',
    applyMode: 'lazy',
  }),
  controlEntry(
    'disableAutoMainBrowserPromotion',
    'setting-disable-auto-main-browser-promotion',
    'checkbox',
    true,
    {
      editable: true,
      storage: 'settings.json',
      validation: 'boolean',
      applyMode: 'lazy',
    },
  ),
  controlEntry(
    'keepSystemAwakeWithActiveSessions',
    'setting-keep-system-awake',
    'checkbox',
    false,
    {
      editable: true,
      storage: 'settings.json',
      validation: 'boolean',
      applyMode: 'server-only',
    },
  ),
  controlEntry('resumeMode', 'setting-resume-mode', 'select', 'fullReplay', {
    editable: true,
    storage: 'settings.json',
    validation: 'fullReplay or quickResume',
    applyMode: 'server-only',
  }),
  controlEntry('inputMode', 'setting-input-mode', 'select', 'keyboard', {
    editable: true,
    storage: 'settings.json',
    validation: 'keyboard, smartinput, or both',
    applyMode: 'immediate',
  }),
  controlEntry('fileRadar', 'setting-file-radar', 'checkbox', true, {
    editable: true,
    storage: 'settings.json',
    validation: 'boolean',
    applyMode: 'lazy',
  }),
  controlEntry('showBookmarks', 'setting-show-bookmarks', 'checkbox', true, {
    editable: true,
    storage: 'settings.json',
    validation: 'boolean',
    applyMode: 'immediate',
  }),
  controlEntry(
    'allowAdHocSessionBookmarks',
    'setting-allow-adhoc-session-bookmarks',
    'checkbox',
    true,
    {
      editable: true,
      storage: 'settings.json',
      validation: 'boolean',
      applyMode: 'immediate',
    },
  ),
  controlEntry(
    'showSidebarSessionFilter',
    'setting-show-sidebar-session-filter',
    'checkbox',
    false,
    {
      editable: true,
      storage: 'settings.json',
      validation: 'boolean',
      applyMode: 'immediate',
    },
  ),
  controlEntry('tmuxCompatibility', 'setting-tmux-compatibility', 'checkbox', true, {
    editable: true,
    storage: 'settings.json',
    validation: 'boolean',
    applyMode: 'server-only',
  }),
  controlEntry('managerBarEnabled', 'setting-manager-bar', 'checkbox', true, {
    editable: true,
    storage: 'settings.json',
    validation: 'boolean',
    applyMode: 'immediate',
  }),
  controlEntry(
    'commandBayLigaturesEnabled',
    'setting-command-bay-ligatures-enabled',
    'checkbox',
    true,
    {
      editable: true,
      storage: 'settings.json',
      validation: 'boolean',
      applyMode: 'immediate',
    },
  ),
  preserveEntry('devMode', false, {
    editable: true,
    storage: 'settings.json',
    validation: 'boolean',
    applyMode: 'server-only',
    specialWriter: 'hidden version-click toggle',
  }),
  controlEntry('showChangelogAfterUpdate', 'setting-changelog-after-update', 'checkbox', true, {
    editable: true,
    storage: 'settings.json',
    validation: 'boolean',
    applyMode: 'lazy',
    specialWriter: 'modules/updating/changelog.ts',
  }),
  controlEntry('showUpdateNotification', 'setting-show-update-notification', 'checkbox', true, {
    editable: true,
    storage: 'settings.json',
    validation: 'boolean',
    applyMode: 'immediate',
  }),
  controlEntry('updateChannel', 'setting-update-channel', 'select', 'stable', {
    editable: true,
    storage: 'settings.json',
    validation: 'stable or dev',
    applyMode: 'server-only',
  }),
  controlEntry('language', 'setting-language', 'select', 'auto', {
    editable: true,
    storage: 'settings.json',
    validation: 'known language code or auto',
    applyMode: 'immediate',
  }),
  preserveEntry('managerBarButtons', [], {
    editable: true,
    storage: 'settings.json',
    validation: 'array of manager bar button objects',
    applyMode: 'immediate',
    specialWriter: 'modules/managerBar/managerBar.ts',
  }),
  controlEntry('runAsUser', 'setting-run-as-user', 'nullable-string', '', {
    editable: true,
    storage: 'settings.json',
    validation: 'nullable username',
    applyMode: 'server-only',
  }),
  readonlyEntry('runAsUserSid', {
    storage: 'settings.json',
    validation: 'server-derived SID metadata',
    applyMode: 'server-only',
    specialWriter: 'installer / server-side identity lookup',
  }),
  readonlyEntry('authenticationEnabled', {
    storage: 'settings.json',
    validation: 'auth-endpoint managed boolean',
    applyMode: 'server-only',
    specialWriter: 'auth endpoints',
  }),
  readonlyEntry('certificatePath', {
    storage: 'settings.json',
    validation: 'installer / certificate setup managed path',
    applyMode: 'server-only',
    specialWriter: 'installer / certificate generation',
  }),
  readonlyEntry('hubMachines', {
    storage: 'settings.json',
    validation: 'hub machine records with secrets managed by hub endpoints',
    applyMode: 'server-only',
    specialWriter: 'hub endpoints',
  }),
] as const;

export function getSettingsRegistryControlEntries(): readonly SettingsRegistryEntry[] {
  return SETTINGS_REGISTRY.filter((entry) => entry.controlId);
}

export function getSettingsRegistryWritableEntries(): readonly SettingsRegistryEntry[] {
  return SETTINGS_REGISTRY.filter((entry) => entry.saveStrategy !== 'readonly');
}

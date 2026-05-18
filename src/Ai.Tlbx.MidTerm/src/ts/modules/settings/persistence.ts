/**
 * Settings Persistence Module
 *
 * Handles loading, saving, and form binding for application settings.
 * Communicates with the server API to persist settings changes.
 */

import type { TerminalState } from '../../types';
import type { MidTermSettingsPublic, MidTermSettingsUpdate, UserInfo } from '../../api/types';
import type { ITerminalOptions } from '@xterm/xterm';
import { JS_BUILD_VERSION } from '../../constants';
import { applyCssTheme } from '../theming/cssThemes';
import { applyBackgroundAppearance, getBackgroundImageUrl } from '../theming/backgroundAppearance';
import {
  getEffectiveXtermThemeForSettings,
  syncEffectiveXtermThemeDomOverrides,
} from '../theming/themes';
import { dom, sessionTerminals } from '../../state';
import { $settingsOpen, $currentSettings } from '../../stores';
import { setCookie } from '../../utils';
import { showAlert } from '../../utils/dialog';
import {
  getSettings,
  getUsers,
  getVersion,
  getHealth,
  updateSettings,
  uploadBackgroundImage,
  deleteBackgroundImage,
} from '../../api/client';
import { updateTabTitle } from '../tabTitle';
import { getEffectiveTerminalFontSize } from '../terminal/fontSize';
import {
  buildTerminalFontStack,
  ensureTerminalFontLoaded,
  DEFAULT_TERMINAL_FONT_WEIGHT,
  DEFAULT_TERMINAL_FONT_WEIGHT_BOLD,
  normalizeTerminalFontWeight,
  normalizeTerminalLetterSpacing,
} from '../terminal/fontConfig';
import { refreshTerminalPresentation } from '../terminal/scaling';
import { syncTerminalLigatureState } from '../terminal/ligatures';
import { syncTerminalRgbBackgroundTransparency } from '../terminal/rgbBackgroundTransparency';
import { syncWebglTerminalCellBackgroundAlpha } from '../terminal/webglCellBackgroundAlpha';
import {
  applyTerminalScrollbarStyleClass,
  normalizeScrollbarStyle,
} from '../terminal/scrollbarStyle';
import { syncTerminalWebglState } from '../terminal/manager';
import { shouldUseWebglRenderer } from '../terminal/webglSupport';
import { setLocale, t } from '../i18n';
import { renderUpdatePanel } from '../updating/checker';
import { createLogger } from '../logging';
import { setDevMode } from '../sidebar/voiceSection';
import { buildAgentMessageFontStack } from '../agentView/fontConfig';
import { syncInlineTextInputWrappers, updateInlineTextInputWrapperState } from './inlineInputState';
import {
  bindTerminalColorSchemeEditor,
  syncTerminalColorSchemeOptions,
} from './terminalColorSchemeEditor';
import {
  getSettingsRegistryControlEntries,
  getSettingsRegistryWritableEntries,
  type SettingsRegistryEntry,
  VALID_SETTING_SHELLS,
} from './registry';

const log = createLogger('settings');

// AbortController for settings event listeners cleanup
let settingsAbortController: AbortController | null = null;
let settingsSaveVersion = 0;
let terminalFontSettingsSaveTimer: number | null = null;
let settingsFormHydrated = false;
let settingsSaveArmed = false;
type TerminalFontWeight = NonNullable<ITerminalOptions['fontWeight']>;

const MAX_BACKGROUND_IMAGE_UPLOAD_BYTES = 10 * 1024 * 1024;
const ALLOWED_BACKGROUND_IMAGE_EXTENSIONS = new Set(['.png', '.jpg', '.jpeg']);
const MIN_BACKGROUND_IMAGE_UPLOAD_TRANSPARENCY = 50;
const DEFAULT_BOX_DRAWING_SCALE = 1;
const DEFAULT_BOX_DRAWING_STYLE = 'classic';

function normalizeBoxDrawingScale(value: number | null | undefined): number {
  const numericValue =
    typeof value === 'number' && Number.isFinite(value) ? value : DEFAULT_BOX_DRAWING_SCALE;
  return Math.min(2, Math.max(0.5, Math.round(numericValue * 100) / 100));
}

function normalizeBoxDrawingStyle(value: string | null | undefined): string {
  return value === 'rounded' ? 'rounded' : DEFAULT_BOX_DRAWING_STYLE;
}

function syncBoxDrawingScale(settingValue: number | null | undefined): void {
  window.__MIDTERM_XTERM_BOX_DRAWING_STROKE_SCALE__ = normalizeBoxDrawingScale(settingValue);
}

function syncBoxDrawingStyle(settingValue: string | null | undefined): void {
  window.__MIDTERM_XTERM_BOX_DRAWING_STYLE__ = normalizeBoxDrawingStyle(settingValue);
}

function applySettingsLocally(settings: MidTermSettingsPublic): void {
  $currentSettings.set(settings);
  applyCssTheme(settings.theme);
  syncBoxDrawingStyle(settings.boxDrawingStyle);
  syncBoxDrawingScale(settings.boxDrawingScale);
  syncWebglTerminalCellBackgroundAlpha(settings);
  applySettingsToTerminals();
  updateTabTitle();
  void setLocale(settings.language);
  renderUpdatePanel();

  if ($settingsOpen.get() && dom.settingsView) {
    syncInlineTextInputWrappers(dom.settingsView);
  }
}

function hasTerminalTypographyChanges(
  state: TerminalState,
  fontFamily: string,
  fontSize: number,
  lineHeight: number,
  letterSpacing: number,
  fontWeight: TerminalFontWeight,
  fontWeightBold: TerminalFontWeight,
): boolean {
  return (
    state.terminal.options.fontFamily !== fontFamily ||
    state.terminal.options.fontSize !== fontSize ||
    state.terminal.options.lineHeight !== lineHeight ||
    state.terminal.options.letterSpacing !== letterSpacing ||
    String(state.terminal.options.fontWeight ?? DEFAULT_TERMINAL_FONT_WEIGHT) !== fontWeight ||
    String(state.terminal.options.fontWeightBold ?? DEFAULT_TERMINAL_FONT_WEIGHT_BOLD) !==
      fontWeightBold
  );
}

function applyTerminalSettingsToState(args: {
  sessionId: string;
  state: TerminalState;
  settings: MidTermSettingsPublic;
  theme: ITerminalOptions['theme'] | undefined;
  fontFamily: string;
  fontSize: number;
  lineHeight: number;
  letterSpacing: number;
  fontWeight: TerminalFontWeight;
  fontWeightBold: TerminalFontWeight;
  customGlyphs: boolean;
  contrastRatio: number;
  scrollbarStyle: ReturnType<typeof normalizeScrollbarStyle>;
}): boolean {
  const {
    sessionId,
    state,
    settings,
    theme,
    fontFamily,
    fontSize,
    lineHeight,
    letterSpacing,
    fontWeight,
    fontWeightBold,
    customGlyphs,
    contrastRatio,
    scrollbarStyle,
  } = args;

  const hasTypographyChanges = hasTerminalTypographyChanges(
    state,
    fontFamily,
    fontSize,
    lineHeight,
    letterSpacing,
    fontWeight,
    fontWeightBold,
  );

  state.terminal.options.cursorBlink = settings.cursorBlink;
  state.terminal.options.cursorStyle = settings.cursorStyle;
  state.terminal.options.cursorInactiveStyle = settings.cursorInactiveStyle;
  state.terminal.options.fontFamily = fontFamily;
  state.terminal.options.fontSize = fontSize;
  state.terminal.options.lineHeight = lineHeight;
  state.terminal.options.letterSpacing = letterSpacing;
  state.terminal.options.fontWeight = fontWeight;
  state.terminal.options.fontWeightBold = fontWeightBold;
  state.terminal.options.customGlyphs = customGlyphs;
  if (theme) {
    state.terminal.options.theme = theme;
  }
  state.terminal.options.minimumContrastRatio = contrastRatio;
  state.terminal.options.smoothScrollDuration = settings.smoothScrolling ? 150 : 0;
  state.terminal.options.scrollback = settings.scrollbackLines;
  syncTerminalWebglState(sessionId, state, shouldUseWebglRenderer(settings));
  syncTerminalLigatureState(state, settings.terminalLigaturesEnabled);
  syncTerminalRgbBackgroundTransparency(state, settings);

  applyTerminalScrollbarStyleClass(state.container, scrollbarStyle);
  refreshTerminalPresentation(sessionId, state);

  return hasTypographyChanges;
}

/**
 * Set the value of a form element by ID
 */
export function setElementValue(id: string, value: string | number): void {
  const el = document.getElementById(id) as
    | HTMLInputElement
    | HTMLSelectElement
    | HTMLTextAreaElement
    | null;
  if (!el) {
    return;
  }

  const nextValue = String(value);
  if (el instanceof HTMLSelectElement) {
    Array.from(el.options)
      .filter((option) => option.dataset.preservedValue === 'true' && option.value !== nextValue)
      .forEach((option) => {
        option.remove();
      });

    if (
      nextValue.length > 0 &&
      !Array.from(el.options).some((option) => option.value === nextValue)
    ) {
      const option = document.createElement('option');
      option.value = nextValue;
      option.textContent = nextValue;
      option.dataset.preservedValue = 'true';
      el.appendChild(option);
    }
  }

  el.value = nextValue;
}

/**
 * Set the checked state of a checkbox by ID
 */
export function setElementChecked(id: string, checked: boolean): void {
  const el = document.getElementById(id) as HTMLInputElement | null;
  if (el) el.checked = checked;
}

/**
 * Get the value of a form element by ID
 */
export function getElementValue(id: string, defaultValue: string): string {
  const el = document.getElementById(id) as
    | HTMLInputElement
    | HTMLSelectElement
    | HTMLTextAreaElement
    | null;
  return el ? el.value : defaultValue;
}

/**
 * Get the checked state of a checkbox by ID
 */
export function getElementChecked(id: string): boolean {
  const el = document.getElementById(id) as HTMLInputElement | null;
  return el ? el.checked : false;
}

function getRegistryFallbackValue(entry: SettingsRegistryEntry): string | number | boolean {
  if (typeof entry.fallbackValue === 'number') {
    return entry.fallbackValue;
  }

  if (typeof entry.fallbackValue === 'boolean') {
    return entry.fallbackValue;
  }

  if (typeof entry.fallbackValue === 'string') {
    return entry.fallbackValue;
  }

  return '';
}

function setRegistryControlValue(
  entry: SettingsRegistryEntry,
  value: MidTermSettingsPublic[keyof MidTermSettingsPublic],
): void {
  if (!entry.controlId || !entry.controlType) {
    return;
  }

  if (entry.controlType === 'checkbox') {
    setElementChecked(entry.controlId, Boolean(value ?? entry.fallbackValue));
    return;
  }

  if (entry.controlType === 'boolean-select') {
    setElementValue(entry.controlId, (value ?? entry.fallbackValue) ? 'custom' : 'font');
    return;
  }

  const fallback = getRegistryFallbackValue(entry);
  setElementValue(entry.controlId, (value ?? fallback) as string | number);
}

function readNumericRegistryControlValue(
  rawValue: string,
  entry: SettingsRegistryEntry,
  parser: (value: string) => number,
): number | string | boolean | null {
  const parsed = parser(rawValue);
  if (Number.isFinite(parsed)) {
    return parsed;
  }

  const fallback = getRegistryFallbackValue(entry);
  return typeof fallback === 'number' ? fallback : null;
}

function readTypedRegistryControlValue(entry: SettingsRegistryEntry, rawValue: string): unknown {
  switch (entry.controlType) {
    case undefined:
      return rawValue;
    case 'checkbox':
      return rawValue === 'true';
    case 'nullable-string':
      return rawValue || null;
    case 'int':
      return readNumericRegistryControlValue(rawValue, entry, (value) =>
        Number.parseInt(value, 10),
      );
    case 'float':
      return readNumericRegistryControlValue(rawValue, entry, Number.parseFloat);
    case 'shell-select':
      return VALID_SETTING_SHELLS.includes(rawValue as (typeof VALID_SETTING_SHELLS)[number])
        ? rawValue
        : null;
    case 'boolean-select':
      return rawValue === 'custom';
    case 'textarea':
    case 'text':
    case 'select':
      return rawValue;
  }

  const unexpectedControlType: never = entry.controlType;
  return unexpectedControlType;
}

function readRegistryControlValue(
  entry: SettingsRegistryEntry,
  prevSettings: MidTermSettingsPublic | null,
): unknown {
  if (entry.saveStrategy === 'preserve') {
    return prevSettings?.[entry.key] ?? entry.fallbackValue;
  }

  if (!entry.controlId || !entry.controlType) {
    return prevSettings?.[entry.key] ?? entry.fallbackValue;
  }

  if (!document.getElementById(entry.controlId)) {
    return prevSettings?.[entry.key] ?? entry.fallbackValue;
  }

  if (entry.controlType === 'checkbox') {
    return getElementChecked(entry.controlId);
  }

  const rawValue = getElementValue(entry.controlId, String(getRegistryFallbackValue(entry)));
  return readTypedRegistryControlValue(entry, rawValue);
}

function buildSettingsUpdateFromRegistry(
  prevSettings: MidTermSettingsPublic | null,
): MidTermSettingsUpdate {
  const result: Partial<MidTermSettingsUpdate> = {};

  getSettingsRegistryWritableEntries().forEach((entry) => {
    (result as Record<string, unknown>)[entry.key] = readRegistryControlValue(entry, prevSettings);
  });

  result.letterSpacing = normalizeTerminalLetterSpacing(result.letterSpacing);
  result.fontWeight = normalizeTerminalFontWeight(result.fontWeight, DEFAULT_TERMINAL_FONT_WEIGHT);
  result.fontWeightBold = normalizeTerminalFontWeight(
    result.fontWeightBold,
    DEFAULT_TERMINAL_FONT_WEIGHT_BOLD,
  );
  const toolCallOutputLines = result.toolCallOutputLines;
  result.toolCallOutputLines = Math.max(
    0,
    Math.min(20, Number.isFinite(toolCallOutputLines) ? (toolCallOutputLines as number) : 5),
  );
  result.boxDrawingStyle = normalizeBoxDrawingStyle(result.boxDrawingStyle);
  result.boxDrawingScale = normalizeBoxDrawingScale(result.boxDrawingScale);

  return result as MidTermSettingsUpdate;
}

function areSettingValuesEqual(a: unknown, b: unknown): boolean {
  if (a === b) {
    return true;
  }

  if (typeof a === 'object' && a !== null && typeof b === 'object' && b !== null) {
    return JSON.stringify(a) === JSON.stringify(b);
  }

  return false;
}

function hasPendingSettingsChanges(): boolean {
  if (!settingsFormHydrated || !settingsSaveArmed) {
    return false;
  }

  const current = $currentSettings.get();
  if (!current) {
    return false;
  }

  const pending = buildSettingsUpdateFromRegistry(current);
  const pendingValues = pending as Record<string, unknown>;
  const currentValues = current as Record<string, unknown>;
  return getSettingsRegistryWritableEntries().some((entry) => {
    const key = entry.key;
    return !areSettingValuesEqual(pendingValues[key], currentValues[key]);
  });
}

function flushPendingSettingsChanges(): void {
  if (terminalFontSettingsSaveTimer !== null) {
    window.clearTimeout(terminalFontSettingsSaveTimer);
    terminalFontSettingsSaveTimer = null;
  }

  if (hasPendingSettingsChanges()) {
    saveAllSettings();
  }
}

/**
 * Populate version info in the about section
 */
export function populateVersionInfo(
  serverVersion: string | null,
  hostVersion: string | null,
  frontendVersion: string,
  devMode?: boolean,
  codeSigned?: boolean,
): void {
  // Strip git hash suffix but preserve [LOCAL] indicator
  const formatVersion = (v: string) => 'v' + v.replace(/[+-][a-f0-9]+$/i, '');

  const serverEl = document.getElementById('version-server');
  if (serverEl && serverVersion) {
    serverEl.textContent = formatVersion(serverVersion);
  }

  const frontendEl = document.getElementById('version-frontend');
  if (frontendEl) {
    frontendEl.textContent = frontendVersion === 'dev' ? 'dev' : formatVersion(frontendVersion);
  }

  const hostEl = document.getElementById('version-host');
  if (hostEl) {
    hostEl.textContent = hostVersion ? formatVersion(hostVersion) : '-';
  }

  const envRow = document.getElementById('dev-environment-row');
  const envEl = document.getElementById('dev-environment-name');
  if (envRow && envEl) {
    if (devMode) {
      envRow.style.display = '';
      envEl.textContent = 'DEV';
    } else {
      envRow.style.display = 'none';
    }
  }

  const sigEl = document.getElementById('code-signing-value');
  if (sigEl) {
    if (codeSigned) {
      sigEl.textContent = t('settings.general.signed');
      sigEl.className = 'version-value signed-badge';
    } else {
      sigEl.textContent = t('settings.general.unsigned');
      sigEl.className = 'version-value unsigned-badge';
    }
  }
}

/**
 * Populate user dropdown for run-as-user selection
 */
export function populateUserDropdown(
  users: Array<{ username: string }>,
  selectedUser: string | null,
): void {
  const select = document.getElementById('setting-run-as-user') as HTMLSelectElement | null;
  if (!select) return;

  select.innerHTML = '<option value="">Process Owner (default)</option>';

  users.forEach((user) => {
    const option = document.createElement('option');
    option.value = user.username;
    option.textContent = user.username;
    if (user.username === selectedUser) {
      option.selected = true;
    }
    select.appendChild(option);
  });

  if (
    selectedUser &&
    !users.some(
      (user) =>
        user.username.localeCompare(selectedUser, undefined, { sensitivity: 'accent' }) === 0,
    )
  ) {
    const option = document.createElement('option');
    option.value = selectedUser;
    option.textContent = selectedUser;
    option.selected = true;
    select.appendChild(option);
  }
}

/**
 * Populate the settings form with current settings
 */
export function populateSettingsForm(settings: MidTermSettingsPublic): void {
  settingsFormHydrated = false;
  settingsSaveArmed = false;
  syncTerminalColorSchemeOptions(settings);
  getSettingsRegistryControlEntries().forEach((entry) => {
    setRegistryControlValue(entry, settings[entry.key]);
  });
  updateTransparencyValue('setting-ui-transparency-value', settings.uiTransparency);
  updateTransparencyValue(
    'setting-terminal-transparency-value',
    settings.terminalTransparency ?? settings.uiTransparency,
  );
  updateTransparencyValue(
    'setting-terminal-cell-background-transparency-value',
    settings.terminalCellBackgroundTransparency ??
      settings.terminalTransparency ??
      settings.uiTransparency,
  );
  updatePercentageValue(
    'setting-background-ken-burns-zoom-percent-value',
    settings.backgroundKenBurnsZoomPercent,
  );
  updatePixelSpeedValue(
    'setting-background-ken-burns-speed-value',
    settings.backgroundKenBurnsSpeedPxPerSecond,
  );
  updateBackgroundImageUi(settings);
  if (dom.settingsView) {
    syncInlineTextInputWrappers(dom.settingsView);
  }

  settingsFormHydrated = true;
}

/**
 * Fetch settings, users, version, and health from server and populate the form
 */
export async function fetchSettings(): Promise<void> {
  const cachedSettings = $currentSettings.get();
  if (cachedSettings) {
    populateSettingsForm(cachedSettings);
    bindSettingsAutoSave();
  }

  try {
    let settingsData = cachedSettings;
    if (!settingsData) {
      const { data, response } = await getSettings();
      if (!data || !response.ok) {
        log.error(() => `Error fetching settings: ${response.status}`);
        return;
      }

      settingsData = data;
      $currentSettings.set(settingsData);
      populateSettingsForm(settingsData);
      bindSettingsAutoSave();
    }

    const [usersRes, versionRes, healthRes] = await Promise.all([
      getUsers(),
      getVersion(),
      getHealth(),
    ]);

    const users = (usersRes.data ?? []).map((u: UserInfo) => ({
      username: u.username,
      displayName: u.username,
    }));
    const version = versionRes.data ?? null;
    const health = healthRes.data;

    populateUserDropdown(users, settingsData.runAsUser ?? null);
    populateVersionInfo(
      version,
      health?.ttyHostVersion ?? null,
      JS_BUILD_VERSION,
      settingsData.devMode,
    );

    applySettingsToTerminals();
  } catch (e) {
    log.error(() => `Error fetching settings: ${String(e)}`);
  }
}

/**
 * Apply current settings to all open terminals
 */
export function applySettingsToTerminals(settingsOverride?: MidTermSettingsPublic): void {
  const settings = settingsOverride ?? $currentSettings.get();
  if (!settings) return;

  syncWebglTerminalCellBackgroundAlpha(settings);
  applyBackgroundAppearance(settings);
  syncEffectiveXtermThemeDomOverrides(settings);
  const theme = getEffectiveXtermThemeForSettings(settings);
  const fontFamily = buildTerminalFontStack(settings.fontFamily);
  const fontSize = getEffectiveTerminalFontSize(settings.fontSize);
  const lineHeight = settings.lineHeight;
  const letterSpacing = normalizeTerminalLetterSpacing(settings.letterSpacing);
  const boxDrawingStyle = normalizeBoxDrawingStyle(settings.boxDrawingStyle);
  const boxDrawingScale = normalizeBoxDrawingScale(settings.boxDrawingScale);
  const fontWeight = normalizeTerminalFontWeight(
    settings.fontWeight,
    DEFAULT_TERMINAL_FONT_WEIGHT,
  ) as TerminalFontWeight;
  const fontWeightBold = normalizeTerminalFontWeight(
    settings.fontWeightBold,
    DEFAULT_TERMINAL_FONT_WEIGHT_BOLD,
  ) as TerminalFontWeight;
  const customGlyphs = settings.customGlyphs;
  const contrastRatio = settings.minimumContrastRatio;
  const fontLoadPromise = ensureTerminalFontLoaded(settings.fontFamily, fontSize);
  document.documentElement.style.setProperty('--terminal-font-size', `${fontSize}px`);
  document.documentElement.style.setProperty('--terminal-font-family', fontFamily);
  document.documentElement.style.setProperty('--terminal-line-height', String(lineHeight));
  document.documentElement.style.setProperty('--terminal-letter-spacing', `${letterSpacing}px`);
  document.documentElement.style.setProperty('--terminal-font-weight', String(fontWeight));
  document.documentElement.style.setProperty(
    '--agent-ui-font-family',
    buildAgentMessageFontStack(settings.agentMessageFontFamily),
  );
  document.documentElement.dataset.commandBayLigatures = settings.commandBayLigaturesEnabled
    ? 'true'
    : 'false';
  document.documentElement.dataset.agentShowMessageTimestamps = settings.showAgentMessageTimestamps
    ? 'true'
    : 'false';
  window.dispatchEvent(new CustomEvent('midterm:agent-view-settings-changed'));
  let hasFontChanges = false;
  syncBoxDrawingStyle(boxDrawingStyle);
  syncBoxDrawingScale(boxDrawingScale);

  const scrollbarStyle = normalizeScrollbarStyle(settings.scrollbarStyle);

  for (const [sessionId, state] of sessionTerminals.entries()) {
    if (
      applyTerminalSettingsToState({
        sessionId,
        state,
        settings,
        theme,
        fontFamily,
        fontSize,
        lineHeight,
        letterSpacing,
        fontWeight,
        fontWeightBold,
        customGlyphs,
        contrastRatio,
        scrollbarStyle,
      })
    ) {
      hasFontChanges = true;
    }
  }

  if (hasFontChanges) {
    void fontLoadPromise.then(() => {
      sessionTerminals.forEach((state: TerminalState, sessionId: string) => {
        refreshTerminalPresentation(sessionId, state);
      });
    });
  }
}

/**
 * Apply settings received from WebSocket sync.
 * Updates the form if settings panel is open, applies to terminals, and updates theme.
 */
export function applyReceivedSettings(settings: MidTermSettingsPublic): void {
  $currentSettings.set(settings);
  if ($settingsOpen.get()) {
    populateSettingsForm(settings);
  }

  applyCssTheme(settings.theme);
  setCookie('mm-theme', settings.theme);

  setCookie('mm-language', settings.language);
  void setLocale(settings.language);

  applySettingsToTerminals();
  updateTabTitle();
  renderUpdatePanel();

  const envRow = document.getElementById('dev-environment-row');
  const envEl = document.getElementById('dev-environment-name');
  if (envRow && envEl) {
    if (settings.devMode) {
      envRow.style.display = '';
      envEl.textContent = 'DEV';
    } else {
      envRow.style.display = 'none';
    }
  }
}

/**
 * Save all settings to the server
 */
export function saveAllSettings(): void {
  if (!settingsFormHydrated || !settingsSaveArmed) {
    return;
  }

  if (!validateAgentEnvironmentInputs()) {
    return;
  }

  const prevSettings = $currentSettings.get();
  const settings = buildSettingsUpdateFromRegistry(prevSettings);
  const nextSettings = prevSettings ? { ...prevSettings, ...settings } : null;

  persistSettingsSnapshot(prevSettings, nextSettings, settings);
}

function persistSettingsSnapshot(
  prevSettings: MidTermSettingsPublic | null,
  nextSettings: MidTermSettingsPublic | null,
  payload: MidTermSettingsUpdate,
): void {
  setCookie('mm-theme', payload.theme);

  setCookie('mm-language', payload.language);

  const saveVersion = ++settingsSaveVersion;

  if (nextSettings) {
    applySettingsLocally(nextSettings);
  }

  updateSettings(payload)
    .then(({ response, error }) => {
      if (response.ok) {
        if (!nextSettings && prevSettings) {
          applySettingsLocally({ ...prevSettings, ...payload });
        }
      } else {
        log.error(() => `Settings save failed: ${response.status} ${String(error)}`);
        if (prevSettings && settingsSaveVersion === saveVersion) {
          applySettingsLocally(prevSettings);
          if ($settingsOpen.get()) {
            populateSettingsForm(prevSettings);
          }
        }
      }
    })
    .catch((e: unknown) => {
      log.error(() => `Error saving settings: ${String(e)}`);
      if (prevSettings && settingsSaveVersion === saveVersion) {
        applySettingsLocally(prevSettings);
        if ($settingsOpen.get()) {
          populateSettingsForm(prevSettings);
        }
      }
    });
}

/**
 * Bind auto-save behavior to settings form elements.
 * Uses AbortController for cleanup when settings panel closes.
 */
export function bindSettingsAutoSave(): void {
  // Clean up previous listeners first
  unbindSettingsAutoSave(false);

  const settingsView = dom.settingsView;
  if (!settingsView) return;

  settingsAbortController = new AbortController();
  const { signal } = settingsAbortController;

  const armSettingsSave = (): void => {
    if (settingsFormHydrated) {
      settingsSaveArmed = true;
    }
  };

  settingsView.addEventListener('pointerdown', armSettingsSave, { capture: true, signal });
  settingsView.addEventListener('keydown', armSettingsSave, { capture: true, signal });

  settingsView
    .querySelectorAll('select[id^="setting-"], input[type="checkbox"][id^="setting-"]')
    .forEach((el) => {
      el.addEventListener('change', saveAllSettings, { signal });
    });

  settingsView.querySelectorAll('input[type="range"][id^="setting-"]').forEach((el) => {
    el.addEventListener('change', saveAllSettings, { signal });
  });

  settingsView
    .querySelectorAll('input[id^="setting-"][type="text"], input[id^="setting-"][type="number"]')
    .forEach((el) => {
      if (!(el instanceof HTMLInputElement)) {
        return;
      }

      el.addEventListener(
        'change',
        () => {
          saveAllSettings();
          syncInlineTextInputWrappers(settingsView);
        },
        { signal },
      );
    });

  settingsView.querySelectorAll('textarea').forEach((el) => {
    if (!(el instanceof HTMLTextAreaElement)) {
      return;
    }

    el.addEventListener(
      'change',
      () => {
        saveAllSettings();
      },
      { signal },
    );

    el.addEventListener(
      'input',
      () => {
        el.setCustomValidity('');
      },
      { signal },
    );
  });

  const uiTransparencySlider = document.getElementById(
    'setting-ui-transparency',
  ) as HTMLInputElement | null;
  bindTransparencyPreview(uiTransparencySlider, 'setting-ui-transparency-value', signal);

  const terminalTransparencySlider = document.getElementById(
    'setting-terminal-transparency',
  ) as HTMLInputElement | null;
  bindTransparencyPreview(
    terminalTransparencySlider,
    'setting-terminal-transparency-value',
    signal,
  );
  const terminalCellBackgroundTransparencySlider = document.getElementById(
    'setting-terminal-cell-background-transparency',
  ) as HTMLInputElement | null;
  bindTransparencyPreview(
    terminalCellBackgroundTransparencySlider,
    'setting-terminal-cell-background-transparency-value',
    signal,
  );
  bindBackgroundKenBurnsPreview(signal);

  const fontSizeInput = document.getElementById('setting-font-size') as HTMLInputElement | null;
  bindTerminalFontPreview(
    fontSizeInput,
    (current, fontSize) => ({ ...current, fontSize }),
    (value) => Number.parseInt(value, 10),
    signal,
  );

  const lineHeightInput = document.getElementById('setting-line-height') as HTMLInputElement | null;
  bindTerminalFontPreview(
    lineHeightInput,
    (current, lineHeight) => ({ ...current, lineHeight }),
    (value) => Number.parseFloat(value),
    signal,
  );

  const boxDrawingScaleInput = document.getElementById(
    'setting-box-drawing-scale',
  ) as HTMLInputElement | null;
  bindTerminalFontPreview(
    boxDrawingScaleInput,
    (current, boxDrawingScale) => ({ ...current, boxDrawingScale }),
    (value) => normalizeBoxDrawingScale(Number.parseFloat(value)),
    signal,
  );

  const letterSpacingInput = document.getElementById(
    'setting-letter-spacing',
  ) as HTMLInputElement | null;
  bindTerminalFontPreview(
    letterSpacingInput,
    (current, letterSpacing) => ({ ...current, letterSpacing }),
    (value) => normalizeTerminalLetterSpacing(Number.parseFloat(value)),
    signal,
  );

  const uploadInput = document.getElementById(
    'setting-background-upload',
  ) as HTMLInputElement | null;
  const removeBtn = document.getElementById('btn-background-remove') as HTMLButtonElement | null;

  uploadInput?.addEventListener(
    'change',
    () => {
      const file = uploadInput.files?.[0];
      if (!file) return;
      void handleBackgroundImageUpload(file);
      uploadInput.value = '';
    },
    { signal },
  );

  removeBtn?.addEventListener(
    'click',
    () => {
      void handleBackgroundImageDelete();
    },
    { signal },
  );

  bindTerminalColorSchemeEditor(signal, persistSettingsSnapshot);

  settingsView.querySelectorAll('.text-input-wrapper').forEach((wrapper) => {
    const input = wrapper.querySelector('input[type="text"], input[type="number"]');
    const saveBtn = wrapper.querySelector('.inline-save-btn');
    if (!(wrapper instanceof HTMLElement) || !(input instanceof HTMLInputElement) || !saveBtn) {
      return;
    }

    input.addEventListener(
      'input',
      () => {
        updateInlineTextInputWrapperState(input);
      },
      { signal },
    );

    saveBtn.addEventListener(
      'mousedown',
      (event) => {
        event.preventDefault();
      },
      { signal },
    );

    saveBtn.addEventListener(
      'click',
      () => {
        saveAllSettings();
        syncInlineTextInputWrappers(settingsView);
      },
      { signal },
    );

    input.addEventListener(
      'keydown',
      (e) => {
        if (e.key === 'Enter') {
          e.preventDefault();
          saveAllSettings();
          syncInlineTextInputWrappers(settingsView);
        }
      },
      { signal },
    );
  });
}

/**
 * Clean up settings event listeners
 */
export function unbindSettingsAutoSave(resetHydrationState = true): void {
  flushPendingSettingsChanges();

  if (settingsAbortController) {
    settingsAbortController.abort();
    settingsAbortController = null;
  }

  if (resetHydrationState) {
    settingsFormHydrated = false;
    settingsSaveArmed = false;
  }
}

function bindTransparencyPreview(
  slider: HTMLInputElement | null,
  labelId: string,
  signal: AbortSignal,
): void {
  if (!slider) {
    return;
  }

  slider.addEventListener(
    'input',
    () => {
      const value = Number.parseInt(slider.value, 10) || 0;
      updateTransparencyValue(labelId, value);
      const current = $currentSettings.get();
      if (!current) {
        return;
      }

      previewTransparencySettings(resolvePreviewTransparencySettings(current));
    },
    { signal },
  );
}

function scheduleTerminalFontSettingsSave(): void {
  if (terminalFontSettingsSaveTimer !== null) {
    window.clearTimeout(terminalFontSettingsSaveTimer);
  }

  terminalFontSettingsSaveTimer = window.setTimeout(() => {
    terminalFontSettingsSaveTimer = null;
    saveAllSettings();
  }, 150);
}

function bindTerminalFontPreview(
  input: HTMLInputElement | null,
  applyPatch: (current: MidTermSettingsPublic, value: number) => MidTermSettingsPublic,
  parse: (value: string) => number,
  signal: AbortSignal,
): void {
  if (!input) {
    return;
  }

  input.addEventListener(
    'input',
    () => {
      if (!input.validity.valid) {
        return;
      }

      const current = $currentSettings.get();
      const nextValue = parse(input.value);
      if (!current || !Number.isFinite(nextValue)) {
        return;
      }

      applySettingsToTerminals(applyPatch(current, nextValue));
      scheduleTerminalFontSettingsSave();
    },
    { signal },
  );
}

function resolvePreviewTransparencySettings(current: MidTermSettingsPublic): MidTermSettingsPublic {
  const uiSlider = document.getElementById('setting-ui-transparency') as HTMLInputElement | null;
  const terminalSlider = document.getElementById(
    'setting-terminal-transparency',
  ) as HTMLInputElement | null;
  const terminalCellBackgroundSlider = document.getElementById(
    'setting-terminal-cell-background-transparency',
  ) as HTMLInputElement | null;

  const uiTransparency = Number.parseInt(uiSlider?.value ?? '', 10);
  const terminalTransparency = Number.parseInt(terminalSlider?.value ?? '', 10);
  const terminalCellBackgroundTransparency = Number.parseInt(
    terminalCellBackgroundSlider?.value ?? '',
    10,
  );

  return {
    ...current,
    uiTransparency: Number.isFinite(uiTransparency) ? uiTransparency : current.uiTransparency,
    terminalTransparency: Number.isFinite(terminalTransparency)
      ? terminalTransparency
      : (current.terminalTransparency ?? current.uiTransparency),
    terminalCellBackgroundTransparency: Number.isFinite(terminalCellBackgroundTransparency)
      ? terminalCellBackgroundTransparency
      : (current.terminalCellBackgroundTransparency ??
        current.terminalTransparency ??
        current.uiTransparency),
  };
}

function resolvePreviewBackgroundKenBurnsSettings(
  current: MidTermSettingsPublic,
): MidTermSettingsPublic {
  const enabled = document.getElementById(
    'setting-background-ken-burns-enabled',
  ) as HTMLInputElement | null;
  const zoomSlider = document.getElementById(
    'setting-background-ken-burns-zoom-percent',
  ) as HTMLInputElement | null;
  const speedSlider = document.getElementById(
    'setting-background-ken-burns-speed',
  ) as HTMLInputElement | null;

  const zoomPercent = Number.parseInt(zoomSlider?.value ?? '', 10);
  const speedPxPerSecond = Number.parseInt(speedSlider?.value ?? '', 10);

  return {
    ...current,
    backgroundKenBurnsEnabled: enabled?.checked ?? current.backgroundKenBurnsEnabled,
    backgroundKenBurnsZoomPercent: Number.isFinite(zoomPercent)
      ? zoomPercent
      : current.backgroundKenBurnsZoomPercent,
    backgroundKenBurnsSpeedPxPerSecond: Number.isFinite(speedPxPerSecond)
      ? speedPxPerSecond
      : current.backgroundKenBurnsSpeedPxPerSecond,
  };
}

function previewTransparencySettings(settings: MidTermSettingsPublic): void {
  applyBackgroundAppearance(settings);
  syncEffectiveXtermThemeDomOverrides(settings);
  const theme = getEffectiveXtermThemeForSettings(settings);

  for (const [sessionId, state] of sessionTerminals.entries()) {
    state.terminal.options.theme = theme;
    refreshTerminalPresentation(sessionId, state);
  }
}

function updateTransparencyValue(labelId: string, value: number): void {
  const label = document.getElementById(labelId);
  if (label) {
    label.textContent = `${String(value)}%`;
  }
}

function syncTransparencyControl(controlId: string, labelId: string, value: number): void {
  const control = document.getElementById(controlId) as HTMLInputElement | null;
  if (control) {
    control.value = String(value);
  }

  updateTransparencyValue(labelId, value);
}

function updatePercentageValue(labelId: string, value: number): void {
  const label = document.getElementById(labelId);
  if (label) {
    label.textContent = `${String(value)}%`;
  }
}

function updatePixelSpeedValue(labelId: string, value: number): void {
  const label = document.getElementById(labelId);
  if (label) {
    label.textContent = `${String(value)} px/s`;
  }
}

function bindBackgroundKenBurnsPreview(signal: AbortSignal): void {
  const enabledCheckbox = document.getElementById(
    'setting-background-ken-burns-enabled',
  ) as HTMLInputElement | null;
  const zoomSlider = document.getElementById(
    'setting-background-ken-burns-zoom-percent',
  ) as HTMLInputElement | null;
  const speedSlider = document.getElementById(
    'setting-background-ken-burns-speed',
  ) as HTMLInputElement | null;

  enabledCheckbox?.addEventListener(
    'change',
    () => {
      const current = $currentSettings.get();
      if (!current) {
        return;
      }

      applyBackgroundAppearance(resolvePreviewBackgroundKenBurnsSettings(current));
    },
    { signal },
  );

  zoomSlider?.addEventListener(
    'input',
    () => {
      const nextZoomPercent = Number.parseInt(zoomSlider.value, 10);
      updatePercentageValue(
        'setting-background-ken-burns-zoom-percent-value',
        Number.isFinite(nextZoomPercent) ? nextZoomPercent : 150,
      );

      const current = $currentSettings.get();
      if (!current) {
        return;
      }

      applyBackgroundAppearance(resolvePreviewBackgroundKenBurnsSettings(current));
    },
    { signal },
  );

  speedSlider?.addEventListener(
    'input',
    () => {
      const nextSpeed = Number.parseInt(speedSlider.value, 10);
      updatePixelSpeedValue(
        'setting-background-ken-burns-speed-value',
        Number.isFinite(nextSpeed) ? nextSpeed : 12,
      );

      const current = $currentSettings.get();
      if (!current) {
        return;
      }

      applyBackgroundAppearance(resolvePreviewBackgroundKenBurnsSettings(current));
    },
    { signal },
  );
}

function updateBackgroundImageUi(settings: MidTermSettingsPublic): void {
  const preview = document.getElementById('background-image-preview') as HTMLImageElement | null;
  const empty = document.getElementById('background-image-empty');
  const name = document.getElementById('background-image-name');
  const removeBtn = document.getElementById('btn-background-remove') as HTMLButtonElement | null;
  const enabledCheckbox = document.getElementById(
    'setting-background-image-enabled',
  ) as HTMLInputElement | null;

  const hasImage = Boolean(
    settings.backgroundImageFileName && settings.backgroundImageRevision > 0,
  );

  if (preview) {
    if (hasImage) {
      preview.src = getBackgroundImageUrl(settings.backgroundImageRevision);
      preview.alt = settings.backgroundImageFileName ?? 'Background image';
      preview.classList.remove('hidden');
    } else {
      preview.removeAttribute('src');
      preview.alt = '';
      preview.classList.add('hidden');
    }
  }

  empty?.classList.toggle('hidden', hasImage);
  if (name) {
    name.textContent = hasImage ? (settings.backgroundImageFileName ?? '') : '';
  }
  if (removeBtn) {
    removeBtn.disabled = !hasImage;
  }
  if (enabledCheckbox) {
    enabledCheckbox.disabled = !hasImage;
    enabledCheckbox.checked = hasImage && settings.backgroundImageEnabled;
  }
}

const ENVIRONMENT_VARIABLE_LINE_PATTERN = /^[A-Za-z_][A-Za-z0-9_]*=.*$/;

function validateAgentEnvironmentInputs(): boolean {
  const textareas = [
    document.getElementById('setting-terminal-env') as HTMLTextAreaElement | null,
    document.getElementById('setting-codex-env') as HTMLTextAreaElement | null,
    document.getElementById('setting-claude-env') as HTMLTextAreaElement | null,
  ];

  for (const textarea of textareas) {
    if (!textarea) {
      continue;
    }

    const invalidLine = textarea.value
      .split(/\r?\n/)
      .map((line) => line.trim())
      .find((line) => line.length > 0 && !ENVIRONMENT_VARIABLE_LINE_PATTERN.test(line));

    if (invalidLine) {
      textarea.setCustomValidity(t('settings.agentUi.agentEnvInvalid'));
      textarea.reportValidity();
      textarea.focus();
      return false;
    }

    textarea.setCustomValidity('');
  }

  return true;
}

function validateBackgroundImageFile(file: File): string | null {
  const extension = file.name.slice(Math.max(0, file.name.lastIndexOf('.'))).toLowerCase();
  if (!ALLOWED_BACKGROUND_IMAGE_EXTENSIONS.has(extension)) {
    return 'Only PNG and JPG images are supported.';
  }

  if (file.size > MAX_BACKGROUND_IMAGE_UPLOAD_BYTES) {
    return 'Background image is too large. Maximum size is 10 MB.';
  }

  return null;
}

function getErrorMessage(error: unknown): string {
  return error instanceof Error ? error.message : String(error);
}

async function handleBackgroundImageUpload(file: File): Promise<void> {
  const validationError = validateBackgroundImageFile(file);
  if (validationError) {
    await showAlert(validationError, { title: t('settings.appearance.backgroundTitle') });
    return;
  }

  try {
    const info = await uploadBackgroundImage(file);
    const current = $currentSettings.get();
    if (!current) return;

    const nextSettings = {
      ...current,
      backgroundImageEnabled: true,
      backgroundImageFileName: info.fileName ?? null,
      backgroundImageRevision: info.revision,
      uiTransparency: Math.max(current.uiTransparency, MIN_BACKGROUND_IMAGE_UPLOAD_TRANSPARENCY),
      terminalTransparency: Math.max(
        current.terminalTransparency ?? current.uiTransparency,
        MIN_BACKGROUND_IMAGE_UPLOAD_TRANSPARENCY,
      ),
    };

    $currentSettings.set(nextSettings);
    updateBackgroundImageUi(nextSettings);
    syncTransparencyControl(
      'setting-ui-transparency',
      'setting-ui-transparency-value',
      nextSettings.uiTransparency,
    );
    syncTransparencyControl(
      'setting-terminal-transparency',
      'setting-terminal-transparency-value',
      nextSettings.terminalTransparency,
    );
    applySettingsToTerminals();
  } catch (e) {
    log.error(() => `Background image upload failed: ${String(e)}`);
    await showAlert(getErrorMessage(e), { title: t('settings.appearance.backgroundTitle') });
  }
}

async function handleBackgroundImageDelete(): Promise<void> {
  try {
    const info = await deleteBackgroundImage();
    const current = $currentSettings.get();
    if (!current) return;

    const nextSettings = {
      ...current,
      backgroundImageEnabled: false,
      backgroundImageFileName: info.fileName ?? null,
      backgroundImageRevision: info.revision,
    };

    $currentSettings.set(nextSettings);
    updateBackgroundImageUi(nextSettings);
    applySettingsToTerminals();
  } catch (e) {
    log.error(() => `Background image delete failed: ${String(e)}`);
    await showAlert(getErrorMessage(e), { title: t('settings.appearance.backgroundTitle') });
  }
}

/**
 * Bind the secret dev mode toggle to the server version value.
 * Click 7 times to toggle dev mode on/off.
 */
export function bindDevModeToggle(): void {
  const el = document.getElementById('version-server');
  if (!el) return;

  let clicks = 0;
  let timer: ReturnType<typeof setTimeout> | undefined;

  el.style.cursor = 'default';
  el.addEventListener('click', () => {
    clicks++;
    clearTimeout(timer);

    if (clicks >= 7) {
      clicks = 0;
      const settings = $currentSettings.get();
      if (!settings) return;
      const newDevMode = !settings.devMode;
      const updated = { ...settings, devMode: newDevMode };
      $currentSettings.set(updated);
      setDevMode(newDevMode);
      updateSettings(updated as Parameters<typeof updateSettings>[0]).catch(() => {});
      const envRow = document.getElementById('dev-environment-row');
      const envEl = document.getElementById('dev-environment-name');
      if (envRow && envEl) {
        envRow.style.display = newDevMode ? '' : 'none';
        envEl.textContent = 'DEV';
      }
    }

    timer = setTimeout(() => {
      clicks = 0;
    }, 2000);
  });
}

import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

import { getSettingsRegistryControlEntries, SETTINGS_REGISTRY } from './registry';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const projectRoot = path.resolve(__dirname, '../../../..');
const html = readFileSync(path.join(projectRoot, 'src/static/index.html'), 'utf8');
const settingsModelSource = readFileSync(
  path.join(projectRoot, 'Settings/MidTermSettingsPublic.cs'),
  'utf8',
);
const persistenceSource = readFileSync(
  path.join(projectRoot, 'src/ts/modules/settings/persistence.ts'),
  'utf8',
);
const terminalColorSchemeEditorSource = readFileSync(
  path.join(projectRoot, 'src/ts/modules/settings/terminalColorSchemeEditor.ts'),
  'utf8',
);
const cssSource = readFileSync(path.join(projectRoot, 'src/static/css/app.css'), 'utf8');
const xtermCssSource = readFileSync(path.join(projectRoot, 'src/static/css/xterm.css'), 'utf8');

const NON_PERSISTED_SETTING_IDS = new Set([
  'setting-background-upload',
  'setting-background-ken-burns-speed-value',
  'setting-background-ken-burns-zoom-percent-value',
  'setting-ui-transparency-value',
  'setting-terminal-transparency-value',
  'setting-terminal-cell-background-transparency-value',
]);

function getPersistedSettingIds(): string[] {
  const ids = new Set<string>();

  for (const match of html.matchAll(/id="(setting-[^"]+)"/g)) {
    const id = match[1];
    if (!id || NON_PERSISTED_SETTING_IDS.has(id)) {
      continue;
    }

    ids.add(id);
  }

  return [...ids].sort();
}

function toCamelCase(name: string): string {
  return name.charAt(0).toLowerCase() + name.slice(1);
}

function getPublicSettingKeys(): string[] {
  const keys = new Set<string>();

  for (const match of settingsModelSource.matchAll(/public\s+[^\s]+\s+(\w+)\s*\{/g)) {
    const key = match[1];
    if (!key) {
      continue;
    }

    keys.add(toCamelCase(key));
  }

  return [...keys].sort();
}

describe('settings persistence wiring', () => {
  it('covers every public setting in the registry', () => {
    const registeredKeys = SETTINGS_REGISTRY.map((entry) => entry.key).sort();
    expect(registeredKeys).toEqual(getPublicSettingKeys());
  });

  it('covers every persisted settings control from index.html in the registry', () => {
    const registeredIds = getSettingsRegistryControlEntries()
      .map((entry) => entry.controlId)
      .filter((id): id is string => Boolean(id))
      .sort();

    expect(registeredIds).toEqual(getPersistedSettingIds());
  });

  it('marks non-form writers explicitly in the registry', () => {
    const specialWriters = new Map(
      SETTINGS_REGISTRY.filter((entry) => entry.specialWriter).map((entry) => [
        entry.key,
        entry.specialWriter,
      ]),
    );

    expect(specialWriters.get('managerBarButtons')).toContain('managerBar');
    expect(specialWriters.get('showChangelogAfterUpdate')).toContain('changelog');
    expect(specialWriters.get('devMode')).toContain('version-click');
    expect(specialWriters.get('terminalColorSchemes')).toContain('terminal color scheme editor');
  });

  it('previews and saves font size on input', () => {
    expect(persistenceSource).toContain(
      "const fontSizeInput = document.getElementById('setting-font-size')",
    );
    expect(persistenceSource).toContain('const boxDrawingScaleInput = document.getElementById(');
    expect(persistenceSource).toContain("'setting-box-drawing-scale'");
    expect(persistenceSource).toContain(
      "const lineHeightInput = document.getElementById('setting-line-height')",
    );
    expect(persistenceSource).toContain('const letterSpacingInput = document.getElementById(');
    expect(persistenceSource).toContain("'setting-letter-spacing'");
    expect(persistenceSource).toContain('bindTerminalFontPreview(');
  });

  it('limits bundled terminal font controls to distinct supported values', () => {
    expect(html).toContain('id="setting-agent-message-font-family"');
    expect(html).toContain(
      '<option value="default" data-i18n="settings.options.agentMessageFontDefault">',
    );
    expect(html).toContain(
      '<option value="sans" data-i18n="settings.options.agentMessageFontSans">',
    );
    expect(html).toContain(
      '<option value="serif" data-i18n="settings.options.agentMessageFontSerif">',
    );
    expect(html).toContain('<option value="Helvetica Neue">Helvetica Neue</option>');
    expect(html).toContain('<option value="Trebuchet MS">Trebuchet MS</option>');
    expect(html).toContain(
      '<option value="classic" data-i18n="settings.options.boxDrawingStyleClassic">',
    );
    expect(html).toContain(
      '<option value="rounded" data-i18n="settings.options.boxDrawingStyleRounded">',
    );
    expect(html).toMatch(/id="setting-box-drawing-scale"[\s\S]*?min="0.5"/);
    expect(html).toMatch(/id="setting-box-drawing-scale"[\s\S]*?max="2"/);
    expect(html).toMatch(/id="setting-box-drawing-scale"[\s\S]*?step="0.05"/);
    expect(html).toMatch(/id="setting-letter-spacing"[\s\S]*?step="0.05"/);
    expect(html).toContain('<option value="custom" data-i18n="settings.options.boxDrawingCustom">');
    expect(html).toContain('<option value="font" data-i18n="settings.options.boxDrawingFont">');
    expect(html).toContain('<option value="normal" data-i18n="settings.options.fontWeightNormal">');
    expect(html).toContain('<option value="bold" data-i18n="settings.options.fontWeightBold">');
    expect(html).not.toContain('<option value="100">100</option>');
    expect(html).not.toContain('<option value="900">900</option>');
    expect(SETTINGS_REGISTRY.find((entry) => entry.key === 'letterSpacing')?.validation).toBe(
      'float, clamped to -2-10',
    );
    expect(SETTINGS_REGISTRY.find((entry) => entry.key === 'fontWeight')?.validation).toBe(
      'normal, bold, or numeric weight',
    );
    expect(SETTINGS_REGISTRY.find((entry) => entry.key === 'fontWeightBold')?.validation).toBe(
      'normal, bold, or numeric weight',
    );
    expect(
      SETTINGS_REGISTRY.find((entry) => entry.key === 'agentMessageFontFamily')?.validation,
    ).toBe('known agent message font family');
    expect(
      SETTINGS_REGISTRY.find((entry) => entry.key === 'showAgentMessageTimestamps')?.validation,
    ).toBe('boolean');
    expect(
      SETTINGS_REGISTRY.find((entry) => entry.key === 'showUnknownAgentMessages')?.validation,
    ).toBe('boolean');
    expect(html).toMatch(/id="setting-tool-call-output-lines"[\s\S]*?min="0"/);
    expect(html).toMatch(/id="setting-tool-call-output-lines"[\s\S]*?max="20"/);
    expect(
      SETTINGS_REGISTRY.find((entry) => entry.key === 'toolCallOutputLines')?.validation,
    ).toBe('integer, UI clamps to 0-20');
    expect(SETTINGS_REGISTRY.find((entry) => entry.key === 'customGlyphs')?.validation).toBe(
      'boolean, rendered as custom or font box drawing',
    );
    expect(SETTINGS_REGISTRY.find((entry) => entry.key === 'boxDrawingStyle')?.validation).toBe(
      'classic or rounded',
    );
    expect(SETTINGS_REGISTRY.find((entry) => entry.key === 'boxDrawingScale')?.validation).toBe(
      'float, clamped to 0.5-2.0',
    );
  });

  it('flushes pending settings before detaching handlers', () => {
    expect(persistenceSource).toContain('function flushPendingSettingsChanges(): void');
    expect(persistenceSource).toContain('flushPendingSettingsChanges();');
  });

  it('blocks autosave until the settings form is hydrated and user interaction arms it', () => {
    expect(persistenceSource).toContain('let settingsFormHydrated = false;');
    expect(persistenceSource).toContain('let settingsSaveArmed = false;');
    expect(persistenceSource).toContain('if (!settingsFormHydrated || !settingsSaveArmed) {');
    expect(persistenceSource).toContain(
      "settingsView.addEventListener('pointerdown', armSettingsSave",
    );
    expect(persistenceSource).toContain("settingsView.addEventListener('keydown', armSettingsSave");
  });

  it('applies the same env var validation rules to terminal and agent env textareas', () => {
    expect(persistenceSource).toContain("document.getElementById('setting-terminal-env')");
    expect(persistenceSource).toContain("document.getElementById('setting-codex-env')");
    expect(persistenceSource).toContain("document.getElementById('setting-claude-env')");
    expect(persistenceSource).toContain(
      'const fontFamily = buildTerminalFontStack(settings.fontFamily);',
    );
    expect(persistenceSource).toContain("'--terminal-letter-spacing'");
    expect(persistenceSource).toContain("'--terminal-line-height'");
    expect(persistenceSource).toContain("'--terminal-font-weight'");
    expect(persistenceSource).toContain("'--agent-ui-font-family'");
    expect(persistenceSource).not.toContain('buildTerminalFontStack(fontFamily)');
    expect(persistenceSource).toContain('document.documentElement.dataset.commandBayLigatures =');
    expect(persistenceSource).toContain(
      'document.documentElement.dataset.agentShowMessageTimestamps =',
    );
    expect(persistenceSource).toContain(
      "textarea.setCustomValidity(t('settings.agentUi.agentEnvInvalid'));",
    );
  });

  it('wires ligature toggles into the command bay and terminal appearance panels', () => {
    expect(html).toContain('id="setting-command-bay-ligatures-enabled"');
    expect(html).toContain('id="setting-terminal-ligatures-enabled"');
    expect(cssSource).toContain(
      ":root:not([data-command-bay-ligatures='false']) .smart-input-textarea",
    );
    expect(cssSource).toContain(":root[data-command-bay-ligatures='false'] .smart-input-textarea");
    expect(
      SETTINGS_REGISTRY.find((entry) => entry.key === 'commandBayLigaturesEnabled')?.validation,
    ).toBe('boolean');
    expect(
      SETTINGS_REGISTRY.find((entry) => entry.key === 'terminalLigaturesEnabled')?.validation,
    ).toBe('boolean');
  });

  it('preserves hydration state when rebinding autosave listeners', () => {
    expect(persistenceSource).toContain('unbindSettingsAutoSave(false);');
    expect(persistenceSource).toContain(
      'export function unbindSettingsAutoSave(resetHydrationState = true): void {',
    );
    expect(persistenceSource).toContain('if (resetHydrationState) {');
  });

  it('preserves non-default select values when hydrating the settings form', () => {
    expect(persistenceSource).toContain("option.dataset.preservedValue = 'true';");
    expect(persistenceSource).toContain('option.textContent = nextValue;');
  });

  it('keeps the saved run-as user selectable even if discovery misses it', () => {
    expect(persistenceSource).toContain('selectedUser &&');
    expect(persistenceSource).toContain('!users.some(');
    expect(persistenceSource).toContain('option.value = selectedUser;');
    expect(persistenceSource).toContain('option.textContent = selectedUser;');
    expect(persistenceSource).toContain('option.selected = true;');
  });

  it('uses non-submit inline save buttons for text and number settings', () => {
    const inlineSaveButtons = [
      ...html.matchAll(/<button\s+type="button"\s+class="inline-save-btn"/g),
    ];
    expect(inlineSaveButtons).toHaveLength(9);
  });

  it('keeps the background upload preview clean when an image exists', () => {
    expect(cssSource).toContain('.background-image-preview.hidden');
    expect(cssSource).toContain('.background-image-empty.hidden');
  });

  it('keeps background image enablement synchronized after upload and delete flows', () => {
    expect(persistenceSource).toContain(
      'enabledCheckbox.checked = hasImage && settings.backgroundImageEnabled;',
    );
  });

  it('raises app and terminal transparency after a background image upload', () => {
    expect(persistenceSource).toContain('const MIN_BACKGROUND_IMAGE_UPLOAD_TRANSPARENCY = 50;');
    expect(persistenceSource).toContain(
      'uiTransparency: Math.max(current.uiTransparency, MIN_BACKGROUND_IMAGE_UPLOAD_TRANSPARENCY),',
    );
    expect(persistenceSource).toContain(
      'terminalTransparency: Math.max(\n        current.terminalTransparency ?? current.uiTransparency,\n        MIN_BACKGROUND_IMAGE_UPLOAD_TRANSPARENCY,\n      ),',
    );
    expect(persistenceSource).toContain("'setting-ui-transparency-value'");
    expect(persistenceSource).toContain("'setting-terminal-transparency-value'");
  });

  it('keeps settings surfaces opaque under UI transparency', () => {
    expect(cssSource).toContain('background-color: var(--bg-settings-opaque, var(--bg-settings));');
    expect(cssSource).toContain('background: var(--bg-elevated-opaque, var(--bg-elevated));');
    expect(cssSource).toContain('background: var(--bg-active-opaque, var(--bg-active));');
  });

  it('keeps terminal transparency out of non-xterm chrome', () => {
    expect(cssSource).not.toContain('--bg-terminal-pane');
    expect(cssSource).not.toContain('--terminal-pane-bg');
    expect(cssSource).toContain('background: var(--bg-terminal);');
    expect(cssSource).toContain('background: var(--bg-primary);');
    expect(cssSource).toContain('background: var(--terminal-ui-background, var(--terminal-bg));');
    expect(cssSource).toContain('--workspace-pane-background: var(--bg-primary);');
    expect(cssSource).toContain('--workspace-pane-chrome-background: var(--bg-elevated);');
    expect(cssSource).toContain('background-color: transparent;');
    expect(xtermCssSource).toContain(
      'background-color: var(--terminal-canvas-background, var(--bg-terminal));',
    );
  });

  it('applies reduced UI transparency to text inputs and sidebar items', () => {
    expect(cssSource).toContain('background: var(--text-input-background, var(--bg-input));');
    expect(cssSource).toContain(
      'background-color: var(--sidebar-item-hover-background, var(--bg-session-hover));',
    );
    expect(cssSource).toContain('.session-item:not(.active):hover {');
    expect(cssSource).toContain(
      'var(--sidebar-item-hover-background, var(--bg-session-hover)) 70%,',
    );
    expect(cssSource).toContain(
      'background-color: var(--sidebar-item-active-background, var(--bg-session-active));',
    );
    expect(cssSource).toContain('--sidebar-readable-text-shadow:');
    expect(cssSource).toContain('--sidebar-readable-icon-shadow:');
    expect(cssSource).toContain('--sidebar-readable-text-color:');
    expect(cssSource).toContain('--sidebar-readable-muted-text-color:');
    expect(cssSource).toContain('--sidebar-readable-shadow-wide:');
    expect(cssSource).toContain('--sidebar-readable-ellipsis-pad-x:');
    expect(cssSource).toContain('body.has-app-background:not(.opaque-terminal-surfaces)');
    expect(cssSource).toContain('.sidebar-nav-btn > .icon');
    expect(cssSource).toContain('.session-group-toggle {');
    expect(cssSource).toContain('.session-group-label {');
    expect(cssSource).toContain('.spaces-tree-target-header {');
    expect(cssSource).toContain('.spaces-tree-adhoc-list');
    expect(cssSource).toContain('.spaces-tree-adhoc');
    expect(cssSource).toContain('isolation: isolate;');
    expect(cssSource).toContain('z-index: 1;');
    expect(cssSource).toContain('z-index: 2;');
    expect(cssSource).toContain('.spaces-tree-workspace-name');
    expect(cssSource).toContain('.spaces-tree-workspace-branch');
    expect(cssSource).toContain('.spaces-tree-workspace-badge');
    expect(cssSource).toContain('.sidebar-title {');
    expect(cssSource).toContain('.sidebar-brand,');
    expect(cssSource).toContain('color: var(--sidebar-readable-text-color);');
    expect(cssSource).toContain('color: var(--sidebar-readable-muted-text-color);');
    expect(cssSource).toContain('text-shadow: var(--sidebar-readable-text-shadow);');
    expect(cssSource).toContain('filter: var(--sidebar-readable-icon-shadow);');
    expect(cssSource).not.toContain('--sidebar-readable-filter-shadow:');
    expect(cssSource).not.toContain('filter: var(--sidebar-readable-filter-shadow);');
    expect(cssSource).toContain('.footer-update-hint:not(.hidden)');
    expect(cssSource).not.toMatch(/footer-update-hint[\s\S]{0,220}display:\s*inline-block/);
    expect(cssSource).toContain(
      '--sidebar-session-secondary-text: var(--sidebar-readable-muted-text-color);',
    );
    expect(cssSource).toContain('filter: none;');
  });

  it('keeps manager bar buttons readable under UI transparency', () => {
    expect(cssSource).toContain('background: var(--bg-elevated-opaque, var(--bg-elevated));');
    expect(cssSource).toContain('background: var(--bg-active-opaque, var(--bg-active));');
  });

  it('keeps manager bar hover actions layout-stable', () => {
    expect(cssSource).toContain('.adaptive-footer-dock .manager-btn {');
    expect(cssSource).toContain('padding: 0 14px;');
    expect(cssSource).toContain('.adaptive-footer-dock .manager-bar-buttons {');
    expect(cssSource).toContain('width: fit-content;');
    expect(cssSource).toContain('justify-content: flex-start;');
    expect(cssSource).toContain('margin-inline: auto;');
    expect(cssSource).toContain('.manager-btn:hover .manager-btn-menu');
    expect(cssSource).toContain('visibility: visible;');
    expect(cssSource).toContain('pointer-events: auto;');
    expect(cssSource).toContain('.manager-bar-action-popover {');
    expect(cssSource).toContain('position: fixed;');
    expect(cssSource).toContain('min-width: 148px;');
    expect(cssSource).toContain('.manager-bar-action-popover.hidden {');
    expect(cssSource).toContain('display: none !important;');
  });

  it('allows all transparency sliders to reach 100 percent', () => {
    expect(html).toMatch(/id="setting-ui-transparency"[\s\S]*?max="100"/);
    expect(html).toMatch(/id="setting-terminal-transparency"[\s\S]*?max="100"/);
    expect(html).toMatch(/id="setting-terminal-cell-background-transparency"[\s\S]*?max="100"/);
    expect(SETTINGS_REGISTRY.find((entry) => entry.key === 'uiTransparency')?.validation).toBe(
      'integer, clamped to 0-100',
    );
    expect(
      SETTINGS_REGISTRY.find((entry) => entry.key === 'terminalTransparency')?.validation,
    ).toBe('integer, clamped to 0-100');
    expect(
      SETTINGS_REGISTRY.find((entry) => entry.key === 'terminalCellBackgroundTransparency')
        ?.validation,
    ).toBe('integer, clamped to 0-100');
  });

  it('wires Ken Burns controls with the requested zoom and speed ranges', () => {
    expect(html).toMatch(/id="setting-background-ken-burns-zoom-percent"[\s\S]*?min="150"/);
    expect(html).toMatch(/id="setting-background-ken-burns-zoom-percent"[\s\S]*?max="300"/);
    expect(html).toMatch(/id="setting-background-ken-burns-speed"[\s\S]*?min="0"/);
    expect(html).toMatch(/id="setting-background-ken-burns-speed"[\s\S]*?max="120"/);
    expect(
      SETTINGS_REGISTRY.find((entry) => entry.key === 'backgroundKenBurnsZoomPercent')?.validation,
    ).toBe('integer, clamped to 150-300');
    expect(
      SETTINGS_REGISTRY.find((entry) => entry.key === 'backgroundKenBurnsSpeedPxPerSecond')
        ?.validation,
    ).toBe('integer, clamped to 0-120');
  });

  it('renders a dedicated custom terminal scheme editor', () => {
    expect(html).toContain('id="terminal-color-scheme-editor"');
    expect(html).toContain('id="terminal-color-scheme-editor-name"');
    expect(html).toContain('id="terminal-color-scheme-editor-source"');
    expect(html).toContain('id="terminal-color-scheme-save"');
    expect(cssSource).toContain('.terminal-color-scheme-editor');
  });

  it('rebuilds the terminal color scheme select with custom entries at runtime', () => {
    expect(persistenceSource).toContain('syncTerminalColorSchemeOptions(settings);');
    expect(persistenceSource).toContain('syncTerminalColorSchemeOptions,');
    expect(terminalColorSchemeEditorSource).toContain('appendTranslatedOption(');
    expect(terminalColorSchemeEditorSource).toContain("group.label = 'Custom Schemes'");
    expect(terminalColorSchemeEditorSource).toContain('terminalColorSchemes.length');
    expect(terminalColorSchemeEditorSource).toContain(
      "document.getElementById('terminal-color-scheme-save')",
    );
  });
});

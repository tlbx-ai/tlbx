import type {
  MidTermSettingsPublic,
  MidTermSettingsUpdate,
  TerminalColorSchemeDefinition,
} from '../../api/types';
import { $currentSettings } from '../../stores';
import { t } from '../i18n';
import {
  BUILT_IN_TERMINAL_COLOR_SCHEME_OPTIONS,
  DEFAULT_TERMINAL_COLOR_SCHEME_FALLBACKS,
  TERMINAL_COLOR_SCHEME_FIELDS,
  TERMINAL_COLOR_SCHEME_TEXT_PLACEHOLDERS,
  type TerminalColorSchemeFieldKey,
  findCustomTerminalColorScheme,
  getBuiltInTerminalTheme,
  isBuiltInTerminalColorSchemeName,
  suggestCustomTerminalColorSchemeName,
  themeToTerminalColorSchemeDefinition,
} from '../theming/terminalColorSchemes';

type PersistSettingsSnapshot = (
  prevSettings: MidTermSettingsPublic | null,
  nextSettings: MidTermSettingsPublic | null,
  payload: MidTermSettingsUpdate,
) => void;

type TerminalColorSchemeEditorGroup = 'Core' | 'Standard ANSI' | 'Bright ANSI' | 'Advanced';

const TERMINAL_COLOR_SCHEME_EDITOR_GROUPS: readonly TerminalColorSchemeEditorGroup[] = [
  'Core',
  'Standard ANSI',
  'Bright ANSI',
  'Advanced',
];

function getTerminalColorSchemeEditorFieldInput(
  key: TerminalColorSchemeFieldKey,
): HTMLInputElement | null {
  const input = document.getElementById(`terminal-color-scheme-field-${key}`);
  return input instanceof HTMLInputElement ? input : null;
}

function getTerminalColorSchemeEditorNameInput(): HTMLInputElement | null {
  const input = document.getElementById('terminal-color-scheme-editor-name');
  return input instanceof HTMLInputElement ? input : null;
}

function getTerminalColorSchemeEditorSourceSelect(): HTMLSelectElement | null {
  const select = document.getElementById('terminal-color-scheme-editor-source');
  return select instanceof HTMLSelectElement ? select : null;
}

function getTerminalColorSchemeEditorStatusElement(): HTMLElement | null {
  const element = document.getElementById('terminal-color-scheme-editor-status');
  return element instanceof HTMLElement ? element : null;
}

function getTerminalColorSchemeEditorRoot(): HTMLElement | null {
  const element = document.getElementById('terminal-color-scheme-editor');
  return element instanceof HTMLElement ? element : null;
}

function getTranslatedSettingLabel(key: string, fallback: string): string {
  const translated = t(key);
  return translated && translated !== key ? translated : fallback;
}

function appendTranslatedOption(
  select: HTMLSelectElement,
  value: string,
  translationKey: string,
  fallbackText: string,
): void {
  const option = document.createElement('option');
  option.value = value;
  option.setAttribute('data-i18n', translationKey);
  option.textContent = getTranslatedSettingLabel(translationKey, fallbackText);
  select.appendChild(option);
}

function getBuiltInTerminalColorSchemeLabel(value: string): string {
  const option = BUILT_IN_TERMINAL_COLOR_SCHEME_OPTIONS.find((entry) => entry.value === value);
  return option ? getTranslatedSettingLabel(option.translationKey, option.fallbackText) : value;
}

function ensureTerminalColorSchemeEditorRendered(): void {
  const host = document.getElementById('terminal-color-scheme-editor-fields');
  if (!(host instanceof HTMLElement) || host.childElementCount > 0) {
    return;
  }

  for (const groupName of TERMINAL_COLOR_SCHEME_EDITOR_GROUPS) {
    const group = document.createElement('section');
    group.className = 'terminal-color-scheme-editor-group';

    const title = document.createElement('h4');
    title.className = 'terminal-color-scheme-editor-group-title';
    title.textContent = groupName;
    group.appendChild(title);

    const grid = document.createElement('div');
    grid.className = 'terminal-color-scheme-editor-grid';

    for (const field of TERMINAL_COLOR_SCHEME_FIELDS.filter((entry) => entry.group === groupName)) {
      const item = document.createElement('label');
      item.className = 'terminal-color-scheme-editor-field';

      const text = document.createElement('span');
      text.className = 'terminal-color-scheme-editor-field-label';
      text.textContent = field.label;
      item.appendChild(text);

      const input = document.createElement('input');
      input.id = `terminal-color-scheme-field-${field.key}`;
      input.setAttribute('data-terminal-color-scheme-field', field.key);
      input.className =
        field.input === 'color'
          ? 'terminal-color-scheme-editor-input terminal-color-scheme-editor-color'
          : 'terminal-color-scheme-editor-input';
      input.type = field.input === 'color' ? 'color' : 'text';
      input.spellcheck = false;

      if (field.input === 'text') {
        input.placeholder = field.label.includes('Scrollbar')
          ? TERMINAL_COLOR_SCHEME_TEXT_PLACEHOLDERS.scrollbarColor
          : '';
      }

      item.appendChild(input);
      grid.appendChild(item);
    }

    group.appendChild(grid);
    host.appendChild(group);
  }
}

function syncTerminalColorSchemeEditorSourceOptions(
  settings: MidTermSettingsPublic | null | undefined,
  selectedValue: string,
): void {
  const select = getTerminalColorSchemeEditorSourceSelect();
  if (!select) {
    return;
  }

  const preferredValue =
    selectedValue === 'auto' ? (settings?.theme ?? 'dark') : selectedValue || 'dark';
  const existingValue = select.value;

  select.innerHTML = '';

  const presetsGroup = document.createElement('optgroup');
  presetsGroup.label = 'Presets';
  for (const definition of BUILT_IN_TERMINAL_COLOR_SCHEME_OPTIONS) {
    const option = document.createElement('option');
    option.value = definition.value;
    option.textContent = getTranslatedSettingLabel(
      definition.translationKey,
      definition.fallbackText,
    );
    presetsGroup.appendChild(option);
  }
  select.appendChild(presetsGroup);

  if ((settings?.terminalColorSchemes.length ?? 0) > 0) {
    const customGroup = document.createElement('optgroup');
    customGroup.label = 'Custom Schemes';
    for (const definition of settings?.terminalColorSchemes ?? []) {
      const option = document.createElement('option');
      option.value = definition.name;
      option.textContent = definition.name;
      customGroup.appendChild(option);
    }
    select.appendChild(customGroup);
  }

  const nextValue = Array.from(select.options).some((option) => option.value === existingValue)
    ? existingValue
    : preferredValue;

  select.value = Array.from(select.options).some((option) => option.value === nextValue)
    ? nextValue
    : 'dark';
}

function fillTerminalColorSchemeEditor(definition: TerminalColorSchemeDefinition): void {
  const nameInput = getTerminalColorSchemeEditorNameInput();
  if (nameInput) {
    nameInput.value = definition.name;
  }

  for (const field of TERMINAL_COLOR_SCHEME_FIELDS) {
    const input = getTerminalColorSchemeEditorFieldInput(field.key);
    if (input) {
      input.value = definition[field.key];
    }
  }
}

function loadTerminalColorSchemeEditorFromSource(
  settings: MidTermSettingsPublic | null | undefined,
  sourceName: string,
): void {
  const editorRoot = getTerminalColorSchemeEditorRoot();
  if (!editorRoot) {
    return;
  }

  const builtInTheme = getBuiltInTerminalTheme(sourceName);
  const customScheme = builtInTheme ? null : findCustomTerminalColorScheme(settings, sourceName);

  let definition: TerminalColorSchemeDefinition | null = null;
  if (builtInTheme) {
    definition = themeToTerminalColorSchemeDefinition(
      suggestCustomTerminalColorSchemeName(
        getBuiltInTerminalColorSchemeLabel(sourceName),
        settings,
      ),
      builtInTheme,
    );
    editorRoot.dataset.sourceKind = 'preset';
  } else if (customScheme) {
    definition = { ...customScheme };
    editorRoot.dataset.sourceKind = 'custom';
  }

  if (!definition) {
    const darkTheme = getBuiltInTerminalTheme('dark');
    if (!darkTheme) {
      return;
    }

    definition = themeToTerminalColorSchemeDefinition(
      suggestCustomTerminalColorSchemeName('Custom Scheme', settings),
      darkTheme,
    );
    editorRoot.dataset.sourceKind = 'blank';
  }

  editorRoot.dataset.initialized = 'true';
  editorRoot.dataset.sourceName = sourceName;
  fillTerminalColorSchemeEditor(definition);
  syncTerminalColorSchemeEditorActions(settings);
}

function readTerminalColorSchemeEditorDefinition(): TerminalColorSchemeDefinition | null {
  const nameInput = getTerminalColorSchemeEditorNameInput();
  if (!nameInput) {
    return null;
  }

  const definition: TerminalColorSchemeDefinition = {
    name: nameInput.value.trim(),
    ...DEFAULT_TERMINAL_COLOR_SCHEME_FALLBACKS,
  };

  for (const field of TERMINAL_COLOR_SCHEME_FIELDS) {
    const input = getTerminalColorSchemeEditorFieldInput(field.key);
    if (!input) {
      return null;
    }

    definition[field.key] = input.value.trim();
  }

  return definition;
}

function getTerminalColorSchemeEditorValidation(
  settings: MidTermSettingsPublic | null | undefined,
): { valid: boolean; message: string; canDelete: boolean } {
  const definition = readTerminalColorSchemeEditorDefinition();
  if (!definition) {
    return { valid: false, message: 'Editor is unavailable.', canDelete: false };
  }

  if (!definition.name) {
    return { valid: false, message: 'Enter a custom scheme name before saving.', canDelete: false };
  }

  if (isBuiltInTerminalColorSchemeName(definition.name)) {
    return {
      valid: false,
      message: 'Built-in presets are read-only. Save this under a new custom name.',
      canDelete: false,
    };
  }

  const missingField = TERMINAL_COLOR_SCHEME_FIELDS.find((field) => !definition[field.key]);
  if (missingField) {
    return {
      valid: false,
      message: `${missingField.label} cannot be empty.`,
      canDelete: false,
    };
  }

  const existingCustomScheme = findCustomTerminalColorScheme(settings, definition.name);
  return {
    valid: true,
    message: existingCustomScheme
      ? 'Saving will update this custom scheme.'
      : 'Saving will create a new custom scheme.',
    canDelete: existingCustomScheme !== null,
  };
}

function syncTerminalColorSchemeEditorActions(
  settings: MidTermSettingsPublic | null | undefined,
): void {
  const status = getTerminalColorSchemeEditorStatusElement();
  const saveButton = document.getElementById(
    'terminal-color-scheme-save',
  ) as HTMLButtonElement | null;
  const deleteButton = document.getElementById(
    'terminal-color-scheme-delete',
  ) as HTMLButtonElement | null;

  const validation = getTerminalColorSchemeEditorValidation(settings);
  if (status) {
    status.textContent = validation.message;
    status.classList.toggle('is-error', !validation.valid);
  }

  if (saveButton) {
    saveButton.disabled = !validation.valid;
  }

  if (deleteButton) {
    deleteButton.disabled = !validation.canDelete;
  }
}

function saveTerminalColorSchemeEditor(persistSettingsSnapshot: PersistSettingsSnapshot): void {
  const current = $currentSettings.get();
  const definition = readTerminalColorSchemeEditorDefinition();
  if (!current || !definition) {
    return;
  }

  const validation = getTerminalColorSchemeEditorValidation(current);
  if (!validation.valid) {
    syncTerminalColorSchemeEditorActions(current);
    return;
  }

  const existingIndex = current.terminalColorSchemes.findIndex(
    (scheme) => scheme.name.trim().toLowerCase() === definition.name.trim().toLowerCase(),
  );

  const nextSchemes = [...current.terminalColorSchemes];
  if (existingIndex >= 0) {
    nextSchemes[existingIndex] = definition;
  } else {
    nextSchemes.push(definition);
  }

  const nextSettings: MidTermSettingsPublic = {
    ...current,
    terminalColorScheme: definition.name,
    terminalColorSchemes: nextSchemes,
  };

  syncTerminalColorSchemeOptions(nextSettings);
  const select = document.getElementById(
    'setting-terminal-color-scheme',
  ) as HTMLSelectElement | null;
  if (select) {
    select.value = definition.name;
  }
  loadTerminalColorSchemeEditorFromSource(nextSettings, definition.name);
  persistSettingsSnapshot(current, nextSettings, nextSettings);
}

function deleteTerminalColorSchemeEditorScheme(
  persistSettingsSnapshot: PersistSettingsSnapshot,
): void {
  const current = $currentSettings.get();
  const definition = readTerminalColorSchemeEditorDefinition();
  if (!current || !definition) {
    return;
  }

  const nextSchemes = current.terminalColorSchemes.filter(
    (scheme) => scheme.name.trim().toLowerCase() !== definition.name.trim().toLowerCase(),
  );

  const nextSettings: MidTermSettingsPublic = {
    ...current,
    terminalColorSchemes: nextSchemes,
    terminalColorScheme:
      current.terminalColorScheme.trim().toLowerCase() === definition.name.trim().toLowerCase()
        ? 'auto'
        : current.terminalColorScheme,
  };

  syncTerminalColorSchemeOptions(nextSettings);
  loadTerminalColorSchemeEditorFromSource(nextSettings, nextSettings.theme);
  persistSettingsSnapshot(current, nextSettings, nextSettings);
}

export function bindTerminalColorSchemeEditor(
  signal: AbortSignal,
  persistSettingsSnapshot: PersistSettingsSnapshot,
): void {
  ensureTerminalColorSchemeEditorRendered();

  const sourceSelect = getTerminalColorSchemeEditorSourceSelect();
  const loadButton = document.getElementById('terminal-color-scheme-load');
  const resetButton = document.getElementById('terminal-color-scheme-reset');
  const saveButton = document.getElementById('terminal-color-scheme-save');
  const deleteButton = document.getElementById('terminal-color-scheme-delete');
  const nameInput = getTerminalColorSchemeEditorNameInput();
  const mainSelect = document.getElementById(
    'setting-terminal-color-scheme',
  ) as HTMLSelectElement | null;

  loadButton?.addEventListener(
    'click',
    () => {
      loadTerminalColorSchemeEditorFromSource(
        $currentSettings.get(),
        sourceSelect?.value ?? 'dark',
      );
    },
    { signal },
  );

  resetButton?.addEventListener(
    'click',
    () => {
      loadTerminalColorSchemeEditorFromSource($currentSettings.get(), '__blank__');
    },
    { signal },
  );

  saveButton?.addEventListener(
    'click',
    () => {
      saveTerminalColorSchemeEditor(persistSettingsSnapshot);
    },
    { signal },
  );

  deleteButton?.addEventListener(
    'click',
    () => {
      deleteTerminalColorSchemeEditorScheme(persistSettingsSnapshot);
    },
    { signal },
  );

  sourceSelect?.addEventListener(
    'change',
    () => {
      syncTerminalColorSchemeEditorActions($currentSettings.get());
    },
    { signal },
  );

  nameInput?.addEventListener(
    'input',
    () => {
      syncTerminalColorSchemeEditorActions($currentSettings.get());
    },
    { signal },
  );

  for (const field of TERMINAL_COLOR_SCHEME_FIELDS) {
    getTerminalColorSchemeEditorFieldInput(field.key)?.addEventListener(
      'input',
      () => {
        syncTerminalColorSchemeEditorActions($currentSettings.get());
      },
      { signal },
    );
  }

  mainSelect?.addEventListener(
    'change',
    () => {
      syncTerminalColorSchemeEditorSourceOptions($currentSettings.get(), mainSelect.value);
      syncTerminalColorSchemeEditorActions($currentSettings.get());
    },
    { signal },
  );
}

export function syncTerminalColorSchemeOptions(
  settings: MidTermSettingsPublic | null | undefined = $currentSettings.get(),
): void {
  const select = document.getElementById(
    'setting-terminal-color-scheme',
  ) as HTMLSelectElement | null;
  if (!select) {
    return;
  }

  const requestedValue = (settings?.terminalColorScheme ?? select.value) || 'auto';
  select.innerHTML = '';

  appendTranslatedOption(
    select,
    'auto',
    'settings.options.colorSchemeAuto',
    'Auto (follows theme)',
  );

  for (const definition of BUILT_IN_TERMINAL_COLOR_SCHEME_OPTIONS) {
    appendTranslatedOption(
      select,
      definition.value,
      definition.translationKey,
      definition.fallbackText,
    );
  }

  appendCustomTerminalColorSchemeOptions(select, settings?.terminalColorSchemes ?? []);
  syncSelectedTerminalColorSchemeOption(select, requestedValue);

  ensureTerminalColorSchemeEditorRendered();
  syncTerminalColorSchemeEditorSourceOptions(settings, select.value);

  const editorRoot = getTerminalColorSchemeEditorRoot();
  if (editorRoot?.dataset.initialized !== 'true') {
    const initialSource = select.value === 'auto' ? (settings?.theme ?? 'dark') : select.value;
    loadTerminalColorSchemeEditorFromSource(settings, initialSource);
  } else {
    syncTerminalColorSchemeEditorActions(settings);
  }
}

function appendCustomTerminalColorSchemeOptions(
  select: HTMLSelectElement,
  schemes: readonly MidTermSettingsPublic['terminalColorSchemes'][number][],
): void {
  if (schemes.length === 0) {
    return;
  }

  const group = document.createElement('optgroup');
  group.label = 'Custom Schemes';
  for (const definition of schemes) {
    const option = document.createElement('option');
    option.value = definition.name;
    option.textContent = definition.name;
    group.appendChild(option);
  }
  select.appendChild(group);
}

function syncSelectedTerminalColorSchemeOption(
  select: HTMLSelectElement,
  requestedValue: string,
): void {
  select.value = Array.from(select.options).some((option) => option.value === requestedValue)
    ? requestedValue
    : 'auto';
}

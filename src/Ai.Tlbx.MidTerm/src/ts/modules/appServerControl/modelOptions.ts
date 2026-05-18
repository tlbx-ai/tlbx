export interface AppServerControlModelOption {
  value: string;
  label: string;
  description?: string | null;
}

const CODEX_MODEL_PRESETS = [
  'gpt-5.5',
  'gpt-5.4',
  'gpt-5.4-mini',
  'gpt-5.3-codex',
  'gpt-5.3-codex-spark',
  'gpt-5.2',
  'gpt-5',
  'gpt-5.4-codex',
] as const;

const CLAUDE_MODEL_PRESETS = ['sonnet', 'opus', 'claude-sonnet-4-6', 'claude-opus-4-6'] as const;

export function getAppServerControlDefaultModelLabel(provider: string | null | undefined): string {
  return provider === 'claude'
    ? 'Default Claude model'
    : provider === 'codex'
      ? 'Default Codex model'
      : 'Default model';
}

export function getAppServerControlModelOptions(args: {
  provider: string | null | undefined;
  currentValues?: readonly (string | null | undefined)[];
  defaultLabel?: string | null | undefined;
  catalogOptions?: readonly AppServerControlModelOption[] | null | undefined;
}): AppServerControlModelOption[] {
  const normalizedDefaultLabel = normalizeOptionValue(args.defaultLabel);
  const defaultOption: AppServerControlModelOption = {
    value: '',
    label: normalizedDefaultLabel ?? getAppServerControlDefaultModelLabel(args.provider),
  };
  const options: AppServerControlModelOption[] = [defaultOption];

  const isCodex = args.provider === 'codex';
  const catalogOptions = isCodex
    ? normalizeModelCatalogOptions(args.catalogOptions)
    : normalizeCatalogOptions(args.catalogOptions);
  if (catalogOptions.length > 0) {
    options.push(...catalogOptions);
  } else {
    for (const preset of getProviderModelPresets(args.provider)) {
      options.push({ value: preset, label: preset });
    }
  }

  for (const value of args.currentValues ?? []) {
    const normalized = isCodex ? normalizeModelOptionValue(value) : normalizeOptionValue(value);
    const alreadyPresent = isCodex
      ? options.some((option) => modelOptionKeysEqual(option.value, normalized ?? ''))
      : options.some((option) => option.value === normalized);
    if (!normalized || alreadyPresent) {
      continue;
    }

    options.push({ value: normalized, label: normalized });
  }

  return isCodex ? [defaultOption, ...sortModelOptions(options.slice(1))] : options;
}

export function getAppServerControlEffortOptions(args: {
  currentValues?: readonly (string | null | undefined)[];
  catalogOptions?: readonly AppServerControlModelOption[] | null | undefined;
}): AppServerControlModelOption[] {
  const options: AppServerControlModelOption[] = [{ value: '', label: 'Default' }];
  const catalogOptions = normalizeCatalogOptions(args.catalogOptions);
  if (catalogOptions.length > 0) {
    options.push(...catalogOptions);
  } else {
    for (const preset of ['none', 'minimal', 'low', 'medium', 'high', 'xhigh']) {
      options.push({ value: preset, label: humanizeEffort(preset) });
    }
  }

  for (const value of args.currentValues ?? []) {
    const normalized = normalizeOptionValue(value);
    if (!normalized || options.some((option) => option.value === normalized)) {
      continue;
    }

    options.push({ value: normalized, label: humanizeEffort(normalized) });
  }

  return options;
}

function normalizeCatalogOptions(
  catalogOptions: readonly AppServerControlModelOption[] | null | undefined,
): AppServerControlModelOption[] {
  if (!catalogOptions) {
    return [];
  }

  const seen = new Set<string>();
  const options: AppServerControlModelOption[] = [];
  for (const option of catalogOptions) {
    const value = normalizeOptionValue(option.value);
    if (!value || seen.has(value)) {
      continue;
    }

    seen.add(value);
    options.push({
      value,
      label: normalizeOptionValue(option.label) ?? value,
      description: normalizeOptionValue(option.description),
    });
  }

  return options;
}

function normalizeModelCatalogOptions(
  catalogOptions: readonly AppServerControlModelOption[] | null | undefined,
): AppServerControlModelOption[] {
  if (!catalogOptions) {
    return [];
  }

  const seen = new Set<string>();
  const options: AppServerControlModelOption[] = [];
  for (const option of catalogOptions) {
    const value = normalizeModelOptionValue(option.value);
    if (!value) {
      continue;
    }

    const key = getModelOptionKey(value);
    if (seen.has(key)) {
      continue;
    }

    seen.add(key);
    options.push({
      value,
      label: normalizeOptionValue(option.label) ?? value,
      description: normalizeOptionValue(option.description),
    });
  }

  return sortModelOptions(options);
}

function sortModelOptions(options: AppServerControlModelOption[]): AppServerControlModelOption[] {
  return [...options].sort((a, b) => {
    const byKnownOrder = getCodexModelSortKey(a.value).localeCompare(getCodexModelSortKey(b.value));
    return byKnownOrder || a.value.localeCompare(b.value);
  });
}

function getCodexModelSortKey(value: string): string {
  switch (getModelOptionKey(value)) {
    case 'gpt-5.5':
      return '000';
    case 'gpt-5.4':
      return '010';
    case 'gpt-5.4-mini':
      return '020';
    case 'gpt-5.3-codex':
      return '030';
    case 'gpt-5.3-codex-spark':
      return '040';
    case 'gpt-5.2':
      return '050';
    case 'gpt-5':
      return '060';
    case 'gpt-5.4-codex':
      return '070';
    default:
      return `900:${getModelOptionKey(value)}`;
  }
}

function modelOptionKeysEqual(left: string, right: string): boolean {
  return getModelOptionKey(left) === getModelOptionKey(right);
}

function getModelOptionKey(value: string): string {
  return value.trim().toLowerCase();
}

function normalizeModelOptionValue(value: string | null | undefined): string | null {
  return normalizeOptionValue(value)?.toLowerCase() ?? null;
}

function humanizeEffort(value: string): string {
  switch (value.trim().toLowerCase()) {
    case 'none':
      return 'None';
    case 'minimal':
      return 'Minimal';
    case 'low':
      return 'Low';
    case 'medium':
      return 'Medium';
    case 'high':
      return 'High';
    case 'xhigh':
      return 'Extra high';
    default:
      return value;
  }
}

function getProviderModelPresets(provider: string | null | undefined): readonly string[] {
  if (provider === 'claude') {
    return CLAUDE_MODEL_PRESETS;
  }

  if (provider === 'codex') {
    return CODEX_MODEL_PRESETS;
  }

  return [];
}

function normalizeOptionValue(value: string | null | undefined): string | null {
  if (typeof value !== 'string') {
    return null;
  }

  const trimmed = value.trim();
  return trimmed.length > 0 ? trimmed : null;
}

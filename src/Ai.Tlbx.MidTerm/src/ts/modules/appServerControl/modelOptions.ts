export interface AppServerControlModelOption {
  value: string;
  label: string;
  description?: string | null;
  isDefault?: boolean;
  supportsFastMode?: boolean | null;
  effortOptions?: AppServerControlModelOption[] | null;
}

const CLAUDE_MODEL_PRESETS = ['sonnet', 'opus', 'claude-sonnet-4-6', 'claude-opus-4-6'] as const;
const GROK_MODEL_PRESETS = [
  'grok-4.20-0309-non-reasoning',
  'grok-4.20-0309-reasoning',
  'grok-4.20-multi-agent-0309',
  'grok-4.3',
  'grok-build-0.1',
] as const;

export function getAppServerControlDefaultModelLabel(provider: string | null | undefined): string {
  return provider === 'claude'
    ? 'Default Claude model'
    : provider === 'grok'
      ? 'Default Grok model'
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
  } else if (args.catalogOptions === undefined || args.catalogOptions === null) {
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

  return options;
}

export function getAppServerControlEffortOptions(args: {
  currentValues?: readonly (string | null | undefined)[];
  catalogOptions?: readonly AppServerControlModelOption[] | null | undefined;
  model?: string | null | undefined;
  modelOptions?: readonly AppServerControlModelOption[] | null | undefined;
}): AppServerControlModelOption[] {
  const options: AppServerControlModelOption[] = [{ value: '', label: 'Default' }];
  const modelCatalog = resolveAppServerControlModelEffortCatalog(args.model, args.modelOptions);
  const reportedCatalog = modelCatalog ?? args.catalogOptions;
  const catalogOptions = normalizeCatalogOptions(reportedCatalog);
  if (catalogOptions.length > 0) {
    options.push(...catalogOptions);
  } else if (reportedCatalog === undefined || reportedCatalog === null) {
    for (const preset of ['none', 'minimal', 'low', 'medium', 'high', 'xhigh']) {
      options.push({ value: preset, label: humanizeEffort(preset) });
    }
  }

  for (const value of args.currentValues ?? []) {
    const normalized = normalizeOptionValue(value);
    if (modelCatalog && !modelCatalog.some((option) => option.value === normalized)) continue;
    if (!normalized || options.some((option) => option.value === normalized)) {
      continue;
    }

    options.push({ value: normalized, label: humanizeEffort(normalized) });
  }

  return options;
}

export function resolveAppServerControlModelEffortCatalog(
  model: string | null | undefined,
  modelOptions: readonly AppServerControlModelOption[] | null | undefined,
): readonly AppServerControlModelOption[] | null | undefined {
  return modelOptions?.find((option) =>
    model ? option.value === model : option.isDefault || option.value === 'default',
  )?.effortOptions;
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

  return options;
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
    return [];
  }

  if (provider === 'grok') {
    return GROK_MODEL_PRESETS;
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

export function supportsAppServerControlFastMode(
  provider: string | null | undefined,
  model: string | null | undefined,
  options: readonly AppServerControlModelOption[] | undefined,
): boolean {
  if (provider === 'codex') return true;
  if (provider !== 'claude') return false;
  return (
    options?.some(
      (option) =>
        (model ? option.value === model : option.isDefault) && option.supportsFastMode === true,
    ) ?? false
  );
}

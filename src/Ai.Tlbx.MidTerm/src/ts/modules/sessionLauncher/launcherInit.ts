export interface LauncherBrowserDefaults {
  path: string;
  homePath: string;
  startPath: string;
}

export interface LauncherBrowserPathState<TEntry> {
  homePath: string;
  startPath: string;
  currentPath: string;
  pathDraft: string;
  pathDirty: boolean;
  pathHistory: string[];
  parentPath: string | null;
  roots: TEntry[];
  entries: TEntry[];
}

/**
 * Applies the asynchronously resolved home/start defaults and returns the directory to load.
 * A path the user typed or navigated to after opening wins over both the opening preset
 * (`requestedPath`) and the late home/start default.
 */
export function applyLauncherBrowserDefaults<TEntry>(
  state: LauncherBrowserPathState<TEntry>,
  defaults: LauncherBrowserDefaults,
  roots: TEntry[],
  requestedPath?: string | null,
): string {
  const homePath = defaults.homePath || defaults.path;
  const startPath = defaults.startPath || homePath;
  state.homePath = homePath;
  state.startPath = startPath;
  state.roots = roots;
  const userPath = state.pathDirty ? state.pathDraft.trim() : '';
  if (!userPath) {
    state.currentPath = startPath;
    state.pathDraft = startPath;
    state.pathHistory = [];
    state.parentPath = null;
    state.entries = [];
  }

  return userPath || requestedPath?.trim() || startPath;
}

export function resolveLauncherProviderChoice(current: string, selectedValue: string): string {
  return selectedValue.trim() ? selectedValue : current;
}

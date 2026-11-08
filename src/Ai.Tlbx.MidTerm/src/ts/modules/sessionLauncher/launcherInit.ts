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
  if (state.pathDirty) return state.pathDraft.trim();
  state.currentPath = startPath;
  state.pathDraft = startPath;
  state.pathHistory = [];
  state.parentPath = null;
  state.entries = [];

  return requestedPath?.trim() || startPath;
}

export interface LauncherPathCommitContext {
  getPathDraft: () => string;
  getCurrentPath: () => string;
  getTargetId: () => string;
  loadDirectory: (path: string) => Promise<boolean>;
  setError: (message: string) => void;
}

/**
 * Validates the typed path before a create/resume action uses it. Returns null when the path is
 * missing or unreachable, the target changed, or the user kept typing while it was validated.
 */
export async function commitLauncherPathForAction(
  context: LauncherPathCommitContext,
  requiredMessage: string,
): Promise<string | null> {
  const targetId = context.getTargetId();
  const candidatePath = context.getPathDraft().trim();
  if (!candidatePath) {
    context.setError(requiredMessage);
    return null;
  }

  if (candidatePath !== context.getCurrentPath() && !(await context.loadDirectory(candidatePath))) {
    return null;
  }

  const committedPath = context.getCurrentPath();
  return context.getTargetId() === targetId && context.getPathDraft().trim() === committedPath
    ? committedPath
    : null;
}

export function resolveLauncherProviderChoice(current: string, selectedValue: string): string {
  return selectedValue.trim() ? selectedValue : current;
}

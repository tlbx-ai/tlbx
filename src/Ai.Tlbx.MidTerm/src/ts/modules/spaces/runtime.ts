import type { LaunchEntry, Session, ShellType, SpaceWorkspaceDto } from '../../api/types';
import { t } from '../i18n';
import { showAlert, showConfirm } from '../../utils/dialog';
import { launchHubSpaceWorkspace, launchLocalSpaceWorkspace } from './spacesApi';
import { requestHubTerminalSizeControl } from '../hub/sizeControlChannel';
import { toHubCompositeId } from '../hub/runtime';

export type SpaceSurface = 'terminal' | 'codex' | 'claude' | 'grok';

export interface SpacesRuntimeOptions {
  resolveLaunchDimensions: () => Promise<{ cols: number; rows: number }>;
  resolveShell: () => ShellType | null;
  onOpenLocalSession: (session: Session, surface: SpaceSurface) => void | Promise<void>;
  onOpenRemoteSession: (
    machineId: string,
    sessionId: string,
    surface: SpaceSurface,
  ) => void | Promise<void>;
  onLaunchRecent: (machineId: string | null, entry: LaunchEntry) => void | Promise<void>;
}

let runtimeOptions: SpacesRuntimeOptions | null = null;

export function initSpacesRuntime(options: SpacesRuntimeOptions): void {
  runtimeOptions = options;
}

export async function launchSpaceWorkspace(
  machineId: string | null,
  spaceId: string,
  workspace: SpaceWorkspaceDto,
  surface: SpaceSurface,
): Promise<boolean> {
  if (!runtimeOptions) {
    return false;
  }

  if (surface !== 'terminal' && workspace.hasActiveAiSession) {
    const confirmed = await showConfirm(t('spaces.aiCollisionWarning'), {
      title: t('spaces.aiCollisionTitle'),
    });
    if (!confirmed) {
      return false;
    }
  }

  try {
    const { cols, rows } = await runtimeOptions.resolveLaunchDimensions();
    const shell = runtimeOptions.resolveShell();
    if (machineId) {
      const session = await launchHubSpaceWorkspace(machineId, spaceId, workspace.key, {
        surface,
        cols,
        rows,
        shell,
      });
      if (surface === 'terminal') {
        await requestHubTerminalSizeControl(toHubCompositeId(machineId, session.id), true).catch(
          () => undefined,
        );
      }
      await runtimeOptions.onOpenRemoteSession(machineId, session.id, surface);
      return true;
    }

    const session = await launchLocalSpaceWorkspace(spaceId, workspace.key, {
      surface,
      cols,
      rows,
      shell,
    });
    await runtimeOptions.onOpenLocalSession(session as Session, surface);
    return true;
  } catch (error) {
    await showAlert(error instanceof Error ? error.message : String(error), {
      title: t('spaces.sessionStartFailed'),
    });
    return false;
  }
}

export async function launchRecentEntry(
  machineId: string | null,
  entry: LaunchEntry,
): Promise<void> {
  if (!runtimeOptions) {
    return;
  }

  await runtimeOptions.onLaunchRecent(machineId, entry);
}

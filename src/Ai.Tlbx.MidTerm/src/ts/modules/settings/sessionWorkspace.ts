import { showConfirm } from '../../utils/dialog';
import { t } from '../i18n';

interface WorkspaceStatus {
  savedAt: string | null;
  savedSessionCount: number;
  liveSessionCount: number;
  unsupportedSessions: string[];
}

let busy = false;

export async function initSessionWorkspace(): Promise<void> {
  const save = document.getElementById('btn-save-workspace') as HTMLButtonElement | null;
  const restore = document.getElementById('btn-restore-workspace') as HTMLButtonElement | null;
  const status = document.getElementById('workspace-status');
  if (!save || !restore || !status || busy) return;

  const render = (state: WorkspaceStatus): void => {
    save.disabled =
      busy ||
      state.liveSessionCount === 0 ||
      state.savedAt !== null ||
      state.unsupportedSessions.length > 0;
    restore.disabled = busy || state.savedAt === null;
    status.textContent = state.savedAt
      ? t('workspace.saved')
          .replace('{count}', String(state.savedSessionCount))
          .replace('{date}', new Date(state.savedAt).toLocaleString())
      : state.unsupportedSessions.length > 0
        ? t('workspace.unsupported').replace('{sessions}', state.unsupportedSessions.join(', '))
        : t('workspace.ready').replace('{count}', String(state.liveSessionCount));
  };

  const request = async (action?: 'save' | 'restore'): Promise<WorkspaceStatus> => {
    const response = await fetch(`/api/sessions/workspace${action ? `/${action}` : ''}`, {
      method: action ? 'POST' : 'GET',
    });
    if (!response.ok) {
      let message = await response.text();
      try {
        const payload: unknown = JSON.parse(message);
        if (typeof payload === 'string') message = payload;
      } catch {
        // Some server failures return plain text.
      }
      throw new Error(message);
    }
    return (await response.json()) as WorkspaceStatus;
  };

  const act = async (action: 'save' | 'restore'): Promise<void> => {
    if (busy) return;
    if (
      !(await showConfirm(t(`workspace.${action}Confirm`), {
        title: t('workspace.title'),
        confirmLabel: t(`workspace.${action}`),
        danger: action === 'save',
      }))
    )
      return;
    busy = true;
    save.disabled = true;
    restore.disabled = true;
    status.textContent = t(`workspace.${action}Progress`);
    try {
      const state = await request(action);
      busy = false;
      render(state);
    } catch (error) {
      busy = false;
      try {
        render(await request());
      } catch {
        /* Retain the operation error below. */
      }
      status.textContent = error instanceof Error ? error.message : String(error);
    }
  };

  save.onclick = () => void act('save');
  restore.onclick = () => void act('restore');
  try {
    render(await request());
  } catch (error) {
    save.disabled = true;
    restore.disabled = true;
    status.textContent = error instanceof Error ? error.message : String(error);
  }
}

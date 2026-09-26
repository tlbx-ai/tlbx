import { getOrCreateClientId, getOrCreateTabId } from '../../utils/cookies';
import { getBrowserPreviewStatus, getWebPreviewTarget } from './webApi';

interface SavedPreview {
  sessionId: string;
  previewName: string;
  url: string;
  targetRevision: number;
}

const storageKey = 'mt-preview-recovery';

function readPreviews(): SavedPreview[] {
  try {
    const value: unknown = JSON.parse(sessionStorage.getItem(storageKey) ?? '[]');
    return Array.isArray(value)
      ? value.filter(
          (p: unknown): p is SavedPreview =>
            typeof p === 'object' &&
            p !== null &&
            'sessionId' in p &&
            'previewName' in p &&
            'url' in p &&
            'targetRevision' in p &&
            typeof p.sessionId === 'string' &&
            typeof p.previewName === 'string' &&
            typeof p.url === 'string' &&
            typeof p.targetRevision === 'number' &&
            Number.isFinite(p.targetRevision),
        )
      : [];
  } catch {
    return [];
  }
}

function writePreviews(previews: SavedPreview[]): void {
  try {
    sessionStorage.setItem(storageKey, JSON.stringify(previews));
  } catch {
    // Preview operation remains available when browser storage is disabled.
  }
}

/** Remember rendered pages in this tab only; never persist preview credentials. */
export function rememberPreview(preview: SavedPreview): void {
  const previews = readPreviews().filter(
    (p) => p.sessionId !== preview.sessionId || p.previewName !== preview.previewName,
  );
  writePreviews([...previews, preview]);
}

export function forgetPreview(sessionId: string, previewName?: string): void {
  writePreviews(
    readPreviews().filter(
      (p) =>
        p.sessionId !== sessionId || (previewName !== undefined && p.previewName !== previewName),
    ),
  );
}

/** The server remains authoritative: copied tabs and handed-off previews do not restore. */
export async function restoreSessionPreviews(
  sessionId: string,
  restore: (preview: SavedPreview) => Promise<void>,
): Promise<void> {
  const browserId = `${getOrCreateClientId()}:${getOrCreateTabId()}`;
  await Promise.all(
    readPreviews()
      .filter((p) => p.sessionId === sessionId)
      .map(async (saved) => {
        const [status, target] = await Promise.all([
          getBrowserPreviewStatus(sessionId, saved.previewName),
          getWebPreviewTarget(sessionId, saved.previewName),
        ]);
        if (!status || !target) return;
        if (status.ownerBrowserId !== browserId || !target.active || !target.url) {
          forgetPreview(sessionId, saved.previewName);
          return;
        }
        await restore({
          ...saved,
          url: target.targetRevision === saved.targetRevision ? saved.url : target.url,
          targetRevision: target.targetRevision,
        });
      }),
  );
}

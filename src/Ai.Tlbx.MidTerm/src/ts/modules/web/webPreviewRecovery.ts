import { getOrCreateClientId, getOrCreateTabId } from '../../utils/cookies';
import { getBrowserPreviewStatus, getWebPreviewTarget, listWebPreviewSessions } from './webApi';

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
  const previews = await listWebPreviewSessions(sessionId);
  if (!previews) return;
  const remembered = readPreviews().filter((p) => p.sessionId === sessionId);
  // Recovery starts from the server's surviving targets, including when tab
  // storage is unavailable. UI caches may refine navigation, never create a target.
  for (const saved of remembered) {
    if (!previews.some((p) => p.previewName === saved.previewName && p.active)) {
      forgetPreview(sessionId, saved.previewName);
    }
  }
  await Promise.all(
    previews
      .filter((p) => p.active && p.url)
      .map(async (preview) => {
        const saved = remembered.find((p) => p.previewName === preview.previewName);
        const [status, target] = await Promise.all([
          getBrowserPreviewStatus(sessionId, preview.previewName),
          getWebPreviewTarget(sessionId, preview.previewName),
        ]);
        if (!status || !target) return;
        if (status.ownerBrowserId !== browserId || !target.active || !target.url) {
          forgetPreview(sessionId, preview.previewName);
          return;
        }
        await restore({
          sessionId,
          previewName: preview.previewName,
          url: saved && target.targetRevision === saved.targetRevision ? saved.url : target.url,
          targetRevision: target.targetRevision,
        });
      }),
  );
}

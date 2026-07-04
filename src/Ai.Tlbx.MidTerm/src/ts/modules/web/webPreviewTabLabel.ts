import { DEFAULT_PREVIEW_NAME } from './webSessionState';

/**
 * The default preview is always seeded locally; do not show it as an extra
 * empty tab while a named preview is in use.
 */
export function shouldRenderPreviewTab(
  preview: { previewName: string; url: string | null },
  selectedPreviewName: string,
  previewCount: number,
): boolean {
  return !(
    preview.previewName === DEFAULT_PREVIEW_NAME &&
    preview.previewName !== selectedPreviewName &&
    !preview.url &&
    previewCount > 1
  );
}

export function buildPreviewTabLabel(url: string | null | undefined): string {
  const trimmed = url?.trim();
  if (!trimmed) {
    return 'New Tab';
  }

  try {
    const parsed = new URL(trimmed);
    if (parsed.host) {
      return parsed.host;
    }
    if (parsed.hostname) {
      return parsed.hostname;
    }
  } catch {
    // Fall back to the raw value when the URL is malformed or still being edited.
  }

  return trimmed;
}

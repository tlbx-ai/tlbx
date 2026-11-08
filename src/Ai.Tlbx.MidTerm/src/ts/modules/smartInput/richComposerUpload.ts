import {
  MAX_APP_SERVER_CONTROL_IMAGE_BYTES,
  isAppServerControlComposerImageFile,
} from './appServerControlAttachments';

export interface RichComposerUploadOptions {
  sessionId: string;
  file: File;
  originalPath?: string | null;
  signal?: AbortSignal;
  uploadFile: (sessionId: string, file: File) => Promise<string | null>;
  tooLarge: string;
  failed: string;
}

function assertNotCancelled(signal: AbortSignal | undefined): void {
  if (signal?.aborted) throw new DOMException('Aborted', 'AbortError');
}

export async function uploadRichComposerFile(options: RichComposerUploadOptions): Promise<string> {
  assertNotCancelled(options.signal);
  if (
    isAppServerControlComposerImageFile(options.file) &&
    options.file.size > MAX_APP_SERVER_CONTROL_IMAGE_BYTES
  )
    throw new Error(options.tooLarge);
  const path = await options.uploadFile(options.sessionId, options.file);
  assertNotCancelled(options.signal);
  if (!path || path === options.originalPath) throw new Error(options.failed);
  return path;
}

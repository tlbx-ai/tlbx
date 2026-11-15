import type { AppServerControlAttachmentReference } from '../../api/types';
import { beginFileOpen } from '../fileViewer/loadState';
import { isImageAttachment } from './activationHelpers';

export function createAttachmentControl(
  sessionId: string,
  attachment: AppServerControlAttachmentReference,
  previewUrl: string,
  accessReady: Promise<unknown>,
): HTMLAnchorElement | HTMLButtonElement {
  if (!isImageAttachment(attachment)) {
    const link = document.createElement('a');
    void accessReady.then(() => {
      link.href = previewUrl;
    });
    link.target = '_blank';
    link.rel = 'noreferrer';
    return link;
  }
  const button = document.createElement('button');
  button.type = 'button';
  button.addEventListener('click', () => {
    const request = beginFileOpen(sessionId);
    void accessReady.then(async () => {
      const viewer = await import('../fileViewer');
      await viewer.openFile(attachment.path, null, request);
    });
  });
  return button;
}

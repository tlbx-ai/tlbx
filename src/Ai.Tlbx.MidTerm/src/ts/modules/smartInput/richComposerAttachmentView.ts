import type { Node as ProseMirrorNode } from 'prosemirror-model';
import type { NodeView } from 'prosemirror-view';
import type { AppServerControlComposerDraftAttachment } from './appServerControlAttachments';
import { t } from '../i18n';

export interface RichAttachmentActions {
  resolve: (id: string) => AppServerControlComposerDraftAttachment | undefined;
  open: (id: string) => void;
  annotate: (position: number, id: string) => void;
  remove: (position: number, id: string) => void;
  refreshers: Set<() => void>;
}

export function richAttachmentView(
  initial: ProseMirrorNode,
  getPos: () => number | undefined,
  actions: RichAttachmentActions,
): NodeView {
  let node = initial;
  const dom = document.createElement('span');
  dom.className = 'rich-composer-attachment';
  dom.contentEditable = 'false';
  const preview = document.createElement('button');
  preview.type = 'button';
  preview.className = 'rich-composer-attachment-preview';
  const image = document.createElement('img');
  image.draggable = false;
  const label = document.createElement('span');
  preview.append(image, label);
  const pencil = document.createElement('button');
  pencil.type = 'button';
  pencil.textContent = '✎';
  pencil.title = pencil.ariaLabel = t('smartInput.richAnnotate');
  const remove = document.createElement('button');
  remove.type = 'button';
  remove.textContent = '×';
  remove.title = remove.ariaLabel = t('smartInput.removeAttachment');
  dom.append(preview, pencil, remove);
  const referenceId = (): string => String(node.attrs.referenceId);
  preview.addEventListener('click', () => {
    actions.open(referenceId());
  });
  pencil.addEventListener('click', () => {
    const position = getPos();
    if (position !== undefined) actions.annotate(position, referenceId());
  });
  remove.addEventListener('click', () => {
    const position = getPos();
    if (position !== undefined) actions.remove(position, referenceId());
  });
  const refresh = (): void => {
    dom.dataset.stagingRef = referenceId();
    const attachment = actions.resolve(referenceId());
    label.textContent =
      attachment?.referenceLabel ?? attachment?.displayName ?? t('smartInput.fileBadge');
    preview.title = attachment?.displayName ?? '';
    const src = attachment?.previewUrl ?? '';
    image.hidden = !src;
    if (src && image.getAttribute('src') !== src) image.src = src;
    image.alt = attachment?.displayName ?? '';
    pencil.hidden = attachment?.kind !== 'image';
    pencil.disabled = !src;
  };
  actions.refreshers.add(refresh);
  refresh();
  return {
    dom,
    update: (next) => {
      if (next.type !== node.type) return false;
      node = next;
      refresh();
      return true;
    },
    stopEvent: (event) =>
      event.target instanceof Element && Boolean(event.target.closest('button')),
    ignoreMutation: () => true,
    destroy: () => actions.refreshers.delete(refresh),
  };
}

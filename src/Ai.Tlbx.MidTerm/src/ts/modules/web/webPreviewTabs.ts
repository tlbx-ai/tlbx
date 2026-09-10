import { reconcileKeyedChildren } from '../../utils/domReconcile';
import { buildPreviewTabLabel, shouldRenderPreviewTab } from './webPreviewTabLabel';
import { listSessionPreviews } from './webSessionState';

export function renderBrowserPreviewTabs(
  previewTabs: HTMLElement,
  sessionId: string | null,
  selectedPreviewName: string,
  onSelect: (name: string) => void,
  onClose: (name: string) => void,
): void {
  const previews = sessionId ? listSessionPreviews(sessionId) : [];
  previewTabs.setAttribute('role', 'tablist');
  previewTabs.setAttribute('aria-label', 'Browser previews');
  reconcileKeyedChildren(
    previewTabs,
    previews.filter((preview) =>
      shouldRenderPreviewTab(preview, selectedPreviewName, previews.length),
    ),
    {
      key: (preview) => `${sessionId}:${preview.previewName}`,
      create: (preview) => {
        const tab = document.createElement('div');
        tab.className = 'web-preview-tab-shell';
        tab.dataset.previewName = preview.previewName;
        const button = document.createElement('button');
        button.type = 'button';
        button.className = 'web-preview-tab';
        button.setAttribute('role', 'tab');
        button.addEventListener('click', () => {
          onSelect(preview.previewName);
        });
        button.addEventListener('keydown', (event) => {
          const buttons = Array.from(
            previewTabs.querySelectorAll<HTMLButtonElement>('[role="tab"]'),
          );
          const index = buttons.indexOf(button);
          let next: HTMLButtonElement | undefined;
          if (event.key === 'ArrowRight') next = buttons[(index + 1) % buttons.length];
          if (event.key === 'ArrowLeft')
            next = buttons[(index + buttons.length - 1) % buttons.length];
          if (event.key === 'Home') next = buttons[0];
          if (event.key === 'End') next = buttons[buttons.length - 1];
          if (!next) return;
          event.preventDefault();
          next.click();
          next.focus();
          next.scrollIntoView({ block: 'nearest', inline: 'nearest' });
        });
        tab.appendChild(button);

        const closeButton = document.createElement('button');
        closeButton.type = 'button';
        closeButton.className = 'web-preview-tab-close';
        closeButton.textContent = '×';
        closeButton.addEventListener('click', (event) => {
          event.preventDefault();
          event.stopPropagation();
          onClose(preview.previewName);
        });
        tab.appendChild(closeButton);

        return tab;
      },
      patch: (tab, preview) => {
        const selected = preview.previewName === selectedPreviewName;
        tab.classList.toggle('active', selected);
        tab.classList.toggle('detached', preview.mode === 'detached');
        tab.classList.toggle('empty', !preview.url);
        const label = buildPreviewTabLabel(preview.url);
        const button = tab.querySelector<HTMLButtonElement>('.web-preview-tab');
        if (!button) return;
        if (button.textContent !== label) button.textContent = label;
        button.title = preview.url?.trim() || label;
        button.setAttribute('aria-label', `Preview tab ${label}`);
        button.setAttribute('aria-selected', String(selected));
        button.tabIndex = selected ? 0 : -1;
        const close = tab.querySelector<HTMLButtonElement>('.web-preview-tab-close');
        if (!close) return;
        close.title = `Close ${label}`;
        close.setAttribute('aria-label', `Close preview tab ${label}`);
      },
    },
  );
}

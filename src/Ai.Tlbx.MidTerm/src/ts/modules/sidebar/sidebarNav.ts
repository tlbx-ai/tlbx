import type { MidTermSettingsPublic } from '../../api/types';
import { closeHistoryDropdown } from '../history';
import { closeSpacesDropdown } from '../spaces/spacesDropdown';
import { closeBoardsView } from '../boards';

export function syncSidebarNavButtons(settings: MidTermSettingsPublic | null | undefined): void {
  const showSpaces = settings?.showSpaces !== false;
  const showBoards = settings?.showBoards === true;
  for (const [id, visible] of [
    ['btn-spaces', showSpaces],
    ['btn-boards', showBoards],
  ] as const) {
    const button = document.getElementById(id);
    if (button) button.hidden = !visible;
  }
  if (!showSpaces) closeSpacesDropdown();
  if (!showBoards) closeBoardsView();

  const bookmarksButton = document.getElementById('btn-bookmarks');
  const showBookmarks = settings?.showBookmarks !== false;

  if (bookmarksButton instanceof HTMLElement) {
    bookmarksButton.hidden = !showBookmarks;
  }

  if (!showBookmarks) {
    closeHistoryDropdown();
  }
}

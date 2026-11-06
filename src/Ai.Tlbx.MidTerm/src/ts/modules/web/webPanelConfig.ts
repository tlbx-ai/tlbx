export const SANDBOX_BASE_FLAGS = [
  'allow-scripts',
  'allow-forms',
  'allow-popups',
  'allow-popups-to-escape-sandbox',
  'allow-modals',
  'allow-downloads',
  'allow-storage-access-by-user-activation',
];

export const PREVIEW_CONTEXT_COOKIE_NAME = 'mt-preview-ctx';
export const STATUS_REFRESH_INTERVAL_MS = 4000;
export const PREVIEW_VISIBILITY_REFRESH_DELAYS_MS = [0, 50, 200, 500] as const;
export const PREVIEW_TAB_CHANGED_EVENT = 'midterm:web-preview-active-tab-changed';
export const FRAME_ALLOW_ATTR = `
  camera *;
  microphone *;
  geolocation *;
  fullscreen *;
  autoplay *;
  clipboard-read *;
  clipboard-write *;
  display-capture *;
`;

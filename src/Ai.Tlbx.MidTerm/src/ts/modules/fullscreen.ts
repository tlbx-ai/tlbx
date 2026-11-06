/** App fullscreen control; document and board dialogs manage their own layout. */
export function initMobileFullscreen(): void {
  const button = document.getElementById('btn-fullscreen-mobile');
  if (!button) return;
  if (!document.fullscreenEnabled) {
    button.style.display = 'none';
    return;
  }
  button.addEventListener('click', () => {
    if (document.fullscreenElement) void document.exitFullscreen();
    else document.documentElement.requestFullscreen().catch(() => {});
  });
  document.addEventListener('fullscreenchange', () => {
    const icon = button.querySelector('.icon');
    if (icon) icon.textContent = document.fullscreenElement ? '\ue920' : '\ue90c';
  });
}

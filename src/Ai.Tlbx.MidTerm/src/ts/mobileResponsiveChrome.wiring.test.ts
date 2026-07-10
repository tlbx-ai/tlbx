import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const projectRoot = path.resolve(__dirname, '../..');
const html = readFileSync(path.join(projectRoot, 'src/static/index.html'), 'utf8');
const css = readFileSync(path.join(projectRoot, 'src/static/css/app.css'), 'utf8');
const mainSource = readFileSync(path.join(projectRoot, 'src/ts/main.ts'), 'utf8');
const constantsSource = readFileSync(path.join(projectRoot, 'src/ts/constants.ts'), 'utf8');
const mobileActionsSource = readFileSync(
  path.join(projectRoot, 'src/ts/modules/sessionTabs/mobileActions.ts'),
  'utf8',
);
const spacesTreeSidebarSource = readFileSync(
  path.join(projectRoot, 'src/ts/modules/sidebar/spacesTreeSidebar.ts'),
  'utf8',
);
const smartInputMetricsSource = readFileSync(
  path.join(projectRoot, 'src/ts/modules/smartInput/smartInputMetrics.ts'),
  'utf8',
);
const browserLifecycleRecoverySource = readFileSync(
  path.join(projectRoot, 'src/ts/modules/comms/browserLifecycleRecovery.ts'),
  'utf8',
);

const mobileChromeMedia =
  '@media (max-width: 768px), (hover: none) and (pointer: coarse) and (max-width: 1024px) {';

describe('mobile responsive chrome wiring', () => {
  it('nests the mobile tab strip inside the mobile topbar', () => {
    expect(html).toMatch(
      /<header class="mobile-topbar" id="mobile-topbar">[\s\S]*?<div class="topbar-title" id="mobile-title">[\s\S]*?<nav class="mobile-tab-strip" id="mobile-tab-strip"[\s\S]*?<div class="topbar-actions no-terminal" id="topbar-actions">/,
    );
  });

  it('toggles merged mobile topbar state from the active session', () => {
    expect(mainSource).toContain("from './modules/sessionTabs/mobileActions'");
    expect(mainSource).toContain('initWebPreview, syncActiveWebPreview');
    expect(mainSource).toContain("'.session-wrapper:not(.hidden) .session-tab-bar'");
    expect(mobileActionsSource).toContain(
      "title?.toggleAttribute('hidden', Boolean(activeSessionId));",
    );
    expect(mobileActionsSource).toContain(
      "topbar?.classList.toggle('has-mobile-tabs', Boolean(activeSessionId));",
    );
    expect(mobileActionsSource).toContain("resolveSessionSurfaceMode(activeSession) === 'agent'");
    expect(mobileActionsSource).toContain('activeSessionId !== null &&');
    expect(mobileActionsSource).toContain("isTabAvailable(activeSessionId, 'agent');");
    expect(mainSource).toContain('void syncActiveWebPreview().finally(() => {');
  });

  it('keeps mobile footer controls in the adaptive dock instead of hiding automation outright', () => {
    expect(css).toContain('.mobile-topbar.has-mobile-tabs .topbar-title {');
    expect(css).toContain('.mobile-topbar .mobile-tab-strip[hidden] {');
    expect(css).toContain('.mobile-tab-pill[hidden] {');
    expect(css).toContain('.mobile-topbar.has-mobile-tabs .topbar-actions {');
    expect(css).toContain('background: var(--bg-terminal);');
    expect(css).toContain('border-image: linear-gradient(');
    expect(css).toContain(mobileChromeMedia);
    expect(html).toContain('id="adaptive-footer-dock"');
    expect(css).toContain('.adaptive-footer-dock .manager-bar:not(.hidden) {');
    expect(css).toContain('.adaptive-footer-context .touch-controller.embedded {');
    expect(css).toContain('.adaptive-footer-context .touch-controller.embedded .touch-dismiss {');
    expect(css).toContain(".adaptive-footer-dock[data-device='mobile'] .manager-bar {");
    expect(css).toContain('--command-bay-control-height-mobile: 44px;');
    expect(css).toContain(
      '--command-bay-control-height: var(--command-bay-control-height-mobile);',
    );
    expect(css).toContain('min-height: 46px;');
    expect(css).toContain('grid-template-columns: repeat(auto-fit, minmax(44px, 1fr));');
    expect(css).toContain('.adaptive-footer-context .smart-input-tools-strip {');
    expect(css).toContain('bottom: calc(100% + var(--command-bay-gap));');
    expect(css).toContain("body.keyboard-visible .adaptive-footer-dock[data-device='mobile'] {");
    expect(css).toContain(
      "body.keyboard-visible .adaptive-footer-dock[data-device='mobile'] .adaptive-footer-primary {",
    );
    expect(css).toContain('order: 0;');
    expect(css).toContain(
      "body.keyboard-visible .adaptive-footer-dock[data-device='mobile'] .adaptive-footer-context {",
    );
    expect(css).toContain('order: 2;');
    expect(css).toContain(
      "body.keyboard-visible .adaptive-footer-dock[data-device='mobile'] .adaptive-footer-status {",
    );
    expect(css).toContain('order: 4;');
  });

  it('keeps the mobile follower scale claim visible instead of tiny terminal-only chrome', () => {
    expect(css).toMatch(
      /@media \(max-width: 768px\), \(hover: none\) and \(pointer: coarse\) and \(max-width: 1024px\) \{[\s\S]*?\.terminal-container\.scaled \.scaled-overlay \{[\s\S]*?inset: 50% 16px auto;[\s\S]*?min-height: 52px;[\s\S]*?transform: translateY\(-50%\);/s,
    );
  });

  it('keeps the responsive sidebar opaque with compact rows and a dropdown action menu', () => {
    expect(css).toMatch(
      /@media \(max-width: 768px\), \(hover: none\) and \(pointer: coarse\) and \(max-width: 1024px\) \{[\s\S]*?\.sidebar \{[\s\S]*?background: var\(--bg-sidebar-opaque, var\(--bg-sidebar\)\);/s,
    );
    expect(css).toMatch(
      /@media \(max-width: 768px\), \(hover: none\) and \(pointer: coarse\) and \(max-width: 1024px\) \{[\s\S]*?\.session-item \{[\s\S]*?min-height: 64px;/s,
    );
    expect(css).toMatch(
      /@media \(max-width: 768px\), \(hover: none\) and \(pointer: coarse\) and \(max-width: 1024px\) \{[\s\S]*?\.session-actions \{[\s\S]*?position: fixed;[\s\S]*?background: var\(--bg-dropdown-opaque\);/s,
    );
    expect(css).toMatch(
      /@media \(max-width: 768px\), \(hover: none\) and \(pointer: coarse\) and \(max-width: 1024px\) \{[\s\S]*?\.session-item\.menu-open \.session-actions \{[\s\S]*?opacity: 1;[\s\S]*?visibility: visible;[\s\S]*?pointer-events: auto;/s,
    );
    expect(css).toMatch(
      /@media \(max-width: 768px\), \(hover: none\) and \(pointer: coarse\) and \(max-width: 1024px\) \{[\s\S]*?\.session-actions \.session-pin,[\s\S]*?\.session-actions \.session-close \{[\s\S]*?width: 100%;[\s\S]*?min-height: 48px;/s,
    );
    expect(css).not.toMatch(
      /@media \(max-width: 768px\), \(hover: none\) and \(pointer: coarse\) and \(max-width: 1024px\) \{[\s\S]*?\.sidebar \.session-menu-btn \{[\s\S]*?display: none;/s,
    );
  });

  it('keeps the mobile close action neutral at rest with red reserved for the press state', () => {
    expect(css).toMatch(
      /\.session-actions \.session-close \.session-action-icon \{[\s\S]*?background: color-mix\(in srgb, var\(--text-secondary\) 16%, transparent\);[\s\S]*?color: var\(--text-secondary\);/s,
    );
    expect(css).toMatch(
      /\.session-actions \.session-close:active \{[\s\S]*?background-color: var\(--accent-red\);/s,
    );
  });

  it('treats coarse touch phones with a wide CSS viewport as mobile chrome', () => {
    expect(constantsSource).toContain('export const MOBILE_TOUCH_BREAKPOINT = 1024;');
    expect(css).toContain(mobileChromeMedia);
    expect(smartInputMetricsSource).toContain(
      'return isTouchPrimaryDevice() && window.innerWidth <= MOBILE_TOUCH_BREAKPOINT;',
    );
    expect(spacesTreeSidebarSource).toContain(
      "window.matchMedia('(hover: none) and (pointer: coarse)').matches",
    );
    expect(spacesTreeSidebarSource).toContain('window.innerWidth <= MOBILE_TOUCH_BREAKPOINT');
  });

  it('recovers terminal transport after mobile PWA lifecycle resume events', () => {
    expect(mainSource).toContain('setupBrowserLifecycleRecovery');
    expect(browserLifecycleRecoverySource).toContain('recoverVisibleTerminalsAfterBrowserResume');
    expect(browserLifecycleRecoverySource).toContain("window.addEventListener('pagehide'");
    expect(browserLifecycleRecoverySource).toContain("window.addEventListener('pageshow'");
    expect(browserLifecycleRecoverySource).toContain("document.addEventListener('resume'");
    expect(browserLifecycleRecoverySource).not.toContain('requestBufferRefresh');
  });
});

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
const mobileActionsSource = readFileSync(
  path.join(projectRoot, 'src/ts/modules/sessionTabs/mobileActions.ts'),
  'utf8',
);

describe('mobile responsive chrome wiring', () => {
  it('nests the mobile tab strip inside the mobile topbar', () => {
    expect(html).toMatch(
      /<header class="mobile-topbar" id="mobile-topbar">[\s\S]*?<div class="topbar-title" id="mobile-title">[\s\S]*?<nav class="mobile-tab-strip" id="mobile-tab-strip"[\s\S]*?<div class="topbar-actions no-terminal" id="topbar-actions">/,
    );
  });

  it('toggles merged mobile topbar state from the active session', () => {
    expect(mainSource).toContain("from './modules/sessionTabs/mobileActions'");
    expect(mobileActionsSource).toContain(
      "title?.toggleAttribute('hidden', Boolean(activeSessionId));",
    );
    expect(mobileActionsSource).toContain(
      "topbar?.classList.toggle('has-mobile-tabs', Boolean(activeSessionId));",
    );
    expect(mobileActionsSource).toContain("resolveSessionSurfaceMode(activeSession) === 'agent'");
    expect(mobileActionsSource).toContain('activeSessionId !== null &&');
    expect(mobileActionsSource).toContain("isTabAvailable(activeSessionId, 'agent');");
  });

  it('keeps mobile footer controls in the adaptive dock instead of hiding automation outright', () => {
    expect(css).toContain('.mobile-topbar.has-mobile-tabs .topbar-title {');
    expect(css).toContain('.mobile-topbar .mobile-tab-strip[hidden] {');
    expect(css).toContain('.mobile-tab-pill[hidden] {');
    expect(css).toContain('.mobile-topbar.has-mobile-tabs .topbar-actions {');
    expect(css).toContain('background: var(--bg-terminal);');
    expect(css).toContain('border-image: linear-gradient(');
    expect(css).toContain('@media (max-width: 768px) {');
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

  it('keeps the responsive sidebar opaque and exposes touch-sized row actions', () => {
    expect(css).toMatch(
      /@media \(max-width: 768px\) \{[\s\S]*?\.sidebar \{[\s\S]*?background: var\(--bg-sidebar-opaque, var\(--bg-sidebar\)\);/s,
    );
    expect(css).toMatch(
      /@media \(max-width: 768px\) \{[\s\S]*?\.sidebar \.session-actions \{[\s\S]*?opacity: 1;[\s\S]*?visibility: visible;[\s\S]*?pointer-events: auto;/s,
    );
    expect(css).toMatch(
      /@media \(max-width: 768px\) \{[\s\S]*?\.session-actions \.session-pin,[\s\S]*?\.session-actions \.session-close \{[\s\S]*?width: 44px;[\s\S]*?height: 44px;/s,
    );
    expect(css).toMatch(
      /@media \(max-width: 768px\) \{[\s\S]*?\.session-item \{[\s\S]*?flex-wrap: wrap;[\s\S]*?min-height: 112px;[\s\S]*?\.session-actions \{[\s\S]*?flex: 0 0 calc\(100% - 18px\);[\s\S]*?margin: 2px 0 0 18px;/s,
    );
    expect(css).toMatch(
      /@media \(max-width: 768px\) \{[\s\S]*?\.sidebar \.session-menu-btn \{[\s\S]*?display: none;/s,
    );
  });
});

import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const projectRoot = path.resolve(__dirname, '../../../..');
const source = readFileSync(path.join(__dirname, 'webDock.ts'), 'utf8');
const css = readFileSync(path.join(projectRoot, 'src/static/css/app.css'), 'utf8');

describe('web dock footer spacing wiring', () => {
  it('uses a single tab-strip chrome row with command-bay styled actions', () => {
    expect(css).toContain('.web-preview-tab-strip {');
    expect(css).toContain('min-height: 40px;');
    expect(css).toContain('border-radius: 8px 8px 0 0;');
    expect(css).toContain('border-bottom-color: transparent;');
    expect(css).toContain('.web-preview-dock-actions .btn-icon,');
    expect(css).toContain(
      'background: var(--command-bay-ui-reactive-surface, var(--btn-secondary));',
    );
    expect(css).toContain('background: var(--command-bay-surface-hover, var(--bg-hover));');
    expect(css).toContain('.web-preview-button-glyph {');
    expect(css).toContain('transform: translateY(-0.04em);');
    expect(css).toContain('.web-preview-overflow-menu {');
    expect(css).toContain('.web-preview-menu-item {');
  });

  it('pushes the adaptive footer dock left when right-side docks are visible', () => {
    expect(source).toContain("const footerDock = document.getElementById('adaptive-footer-dock');");
    expect(source).toContain("footerDock.style.right = total > 0 ? `${total}px` : '';");
    expect(source).toContain("p.closest<HTMLElement>('.session-wrapper')?.classList.toggle(");
    expect(source).toContain("'has-right-dock-reservation'");
    expect(css).toContain('.session-wrapper.has-right-dock-reservation {');
    expect(source).toContain(
      "mainContent.style.setProperty('--adaptive-footer-right-offset', `${total}px`);",
    );
    expect(css).toContain('margin-right: var(--adaptive-footer-right-offset, 0px);');
    expect(source).toContain("const managerQueue = document.getElementById('manager-bar-queue');");
    expect(source).toContain("managerQueue.style.marginRight = total > 0 ? `${total}px` : '';");
  });

  it('keeps the reserved terminal area aligned while resizing the dock', () => {
    expect(source).toContain('panel.style.width = `${newWidth}px`;');
    expect(source).toContain('function refreshDockReservations(): void {');
    expect(source).toContain(
      'const observer = new ResizeObserver(refreshAfterDockGeometryChange);',
    );
    expect(source).toContain("window.addEventListener('resize', refreshAfterDockGeometryChange);");
  });

  it('limits the docked dev browser to 80 percent of the available horizontal space', () => {
    expect(css).toContain('.web-preview-dock {');
    expect(css).toContain('max-width: 80%;');
    expect(source).toContain('const DOCK_MAX_WIDTH_RATIO = 0.8;');
    expect(source).toContain('function getDockMaxWidth(panel: HTMLElement): number {');
    expect(source).toContain('panel.parentElement?.clientWidth ?? window.innerWidth');
    expect(source).toContain('Math.floor(availableWidth * DOCK_MAX_WIDTH_RATIO)');
    expect(source).toContain('dockPanel.style.width = `${clampDockWidth(w, dockPanel)}px`;');
    expect(source).toContain('const newWidth = clampDockWidth(startWidth + delta, panel);');
    expect(source).not.toContain('const DOCK_MAX_WIDTH = 800;');
  });

  it('reserves layout space for mobile emulation and the preview keyboard fallback', () => {
    expect(css).toContain('#web-preview-mobile-emulation {');
    expect(css).toContain('#web-preview-detach-mobile svg,');
    expect(css).toContain("#web-preview-mobile-emulation[aria-pressed='true'] {");
    expect(css).toContain("#web-preview-mobile-emulation[aria-pressed='true']::after {");
    expect(css).toContain('background: var(--accent-green);');
    expect(css).toContain('box-shadow:');
    expect(css).toContain('.web-preview-dock-body.mobile-emulation {');
    expect(css).toContain('.web-preview-dock-body.mobile-emulation #web-preview-iframe-host {');
    expect(css).toContain('max-width: 430px;');
    expect(css).toContain('body.dev-soft-keyboard-preview-fallback .web-preview-dock-body {');
    expect(css).toContain('flex-direction: column;');
    expect(css).toContain(
      'body.dev-soft-keyboard-preview-fallback .web-preview-dock-body #web-preview-iframe-host {',
    );
    expect(css).toContain('flex: 1 1 auto;');
    expect(css).toContain(
      'body.dev-soft-keyboard-preview-fallback .web-preview-dock-body .dev-soft-keyboard {',
    );
    expect(css).toContain('position: relative;');
  });
});

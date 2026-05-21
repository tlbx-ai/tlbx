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
  it('pushes the adaptive footer dock left when right-side docks are visible', () => {
    expect(source).toContain("const footerDock = document.getElementById('adaptive-footer-dock');");
    expect(source).toContain("footerDock.style.right = total > 0 ? `${total}px` : '';");
    expect(source).toContain(
      "mainContent.style.setProperty('--adaptive-footer-right-offset', `${total}px`);",
    );
    expect(css).toContain('margin-right: var(--adaptive-footer-right-offset, 0px);');
    expect(source).toContain("const managerQueue = document.getElementById('manager-bar-queue');");
    expect(source).toContain("managerQueue.style.marginRight = total > 0 ? `${total}px` : '';");
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
});

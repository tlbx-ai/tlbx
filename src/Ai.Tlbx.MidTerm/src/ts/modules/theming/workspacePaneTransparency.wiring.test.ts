import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const css = readFileSync(path.join(__dirname, '../../../static/css/app.css'), 'utf8');

function getCssRule(selector: string): string {
  const start = css.indexOf(`${selector} {`);
  if (start < 0) {
    return '';
  }

  const end = css.indexOf('\n}', start);
  return end >= 0 ? css.slice(start, end + 2) : '';
}

describe('workspace pane transparency wiring', () => {
  it('keeps files, git, and web panes out of terminal-scoped background tokens', () => {
    expect(css).toContain('--workspace-pane-background: var(--bg-primary);');
    expect(css).toContain('--workspace-pane-chrome-background: var(--bg-elevated);');
    expect(css).toContain('.file-viewer-dock {');
    expect(css).toContain('.git-dock {');
    expect(css).toContain('.web-preview-dock {');
    expect(css).toContain('.file-browser-tree {');
    expect(css).toContain('.file-browser-preview {');
    expect(css).toContain('background: var(--workspace-pane-background);');
    expect(css).toContain('background: var(--workspace-pane-chrome-background);');
  });

  it('keeps terminal and AppServerControl panes as the only workspace surface over wallpaper', () => {
    expect(css).toContain('.main-content {');
    expect(css).toContain('.terminals-area {');
    expect(css).toContain('.layout-leaf {');
    expect(css).toContain('body.has-app-background {\n  background: transparent;\n}');
    expect(css).not.toContain('body.has-app-background::after');
    expect(css).not.toContain('color-mix(in srgb, var(--bg-primary) 25%, transparent)');
    expect(css).toContain(
      "body.opaque-terminal-surfaces .session-wrapper[data-active-tab='terminal'],",
    );
    expect(css).toContain('background-color: transparent;');
    expect(css).toContain('background: transparent;');
    expect(css).toContain('background-color: transparent;');
    expect(css).toContain('linear-gradient(');
    expect(css).toContain('var(--terminal-canvas-background, var(--terminal-bg));');
  });

  it('binds the sidebar header and terminal header to the same app chrome background style', () => {
    const sidebarHeaderRule = getCssRule('.sidebar-header');
    const sessionTabBarRule = getCssRule('.session-tab-bar');
    const layoutLeafSessionTabBarRule = getCssRule('.layout-leaf .session-tab-bar');
    const focusedLayoutLeafSessionTabBarRule = getCssRule('.layout-leaf.focused .session-tab-bar');

    expect(css).toContain('--app-chrome-background: var(--bg-terminal);');
    expect(css).toContain('--app-header-background:');
    expect(css).toContain('var(--bg-sidebar-opaque, var(--bg-sidebar));');
    expect(sidebarHeaderRule).toContain('background: var(--app-header-background);');
    expect(sessionTabBarRule).toContain('background: var(--app-header-background);');
    expect(layoutLeafSessionTabBarRule).toContain('background: var(--app-header-background);');
    expect(focusedLayoutLeafSessionTabBarRule).toContain(
      'background: var(--app-header-background);',
    );
    expect(sidebarHeaderRule).not.toContain(
      'background-color: var(--app-chrome-background, var(--bg-terminal));',
    );
    expect(sessionTabBarRule).not.toContain(
      'background-color: var(--app-chrome-background, var(--bg-terminal));',
    );
    expect(sessionTabBarRule).not.toContain(
      'background: var(--app-chrome-background, var(--bg-terminal));',
    );
    expect(layoutLeafSessionTabBarRule).not.toContain(
      'background: var(--app-chrome-background, var(--bg-terminal));',
    );
    expect(focusedLayoutLeafSessionTabBarRule).not.toContain(
      'background: var(--app-chrome-background, var(--bg-terminal));',
    );
    expect(css).not.toContain('color-mix(in srgb, var(--bg-dialog-chrome) 94%, transparent)');
  });
});

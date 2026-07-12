import { readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const projectRoot = path.resolve(__dirname, '../..');
const loginHtml = readFileSync(path.join(projectRoot, 'src/static/login.html'), 'utf8');
const trustHtml = readFileSync(path.join(projectRoot, 'src/static/trust.html'), 'utf8');

describe('auth page asset paths', () => {
  it('keeps auth content above the mobile on-screen keyboard', () => {
    expect(loginHtml).toContain('interactive-widget=resizes-content');
    expect(trustHtml).toContain('interactive-widget=resizes-content');
    expect(loginHtml).not.toContain('interactive-widget=overlays-content');
    expect(trustHtml).not.toContain('interactive-widget=overlays-content');
  });

  it('uses absolute asset paths so nested preview routes do not 404 app.css', () => {
    expect(loginHtml).toContain('href="/css/app.css?v=__MIDTERM_ASSET_VERSION__"');
    expect(trustHtml).toContain('href="/css/app.css?v=__MIDTERM_ASSET_VERSION__"');
  });

  it('uses absolute terminal bundle paths so nested preview routes do not 404 terminal.min.js', () => {
    expect(loginHtml).toContain('src="/js/terminal.min.js?v=__MIDTERM_ASSET_VERSION__"');
    expect(trustHtml).toContain('src="/js/terminal.min.js?v=__MIDTERM_ASSET_VERSION__"');
  });
});

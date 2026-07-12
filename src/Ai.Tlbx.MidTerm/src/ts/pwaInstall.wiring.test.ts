import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { describe, expect, it } from 'vitest';

const __filename = fileURLToPath(import.meta.url);
const __dirname = path.dirname(__filename);
const projectRoot = path.resolve(__dirname, '../..');
const staticRoot = path.join(projectRoot, 'src/static');
const mainSource = readFileSync(path.join(projectRoot, 'src/ts/main.ts'), 'utf8');
const installSource = readFileSync(path.join(projectRoot, 'src/ts/modules/pwaInstall.ts'), 'utf8');
const frontendBuildSource = readFileSync(path.join(projectRoot, 'frontend-build.ps1'), 'utf8');
const manifest = JSON.parse(
  readFileSync(path.join(staticRoot, 'site.webmanifest'), 'utf8'),
) as Record<string, unknown>;

describe('PWA installation wiring', () => {
  it('covers the URL people actually open with a root-scoped manifest and worker', () => {
    expect(manifest).toMatchObject({ id: '/index.html', start_url: '/', scope: '/' });
    expect(existsSync(path.join(staticRoot, 'sw.js'))).toBe(true);
    expect(existsSync(path.join(staticRoot, 'js/sw.js'))).toBe(false);
    expect(frontendBuildSource).toContain("'*.html', '*.css', '*.js', '*.txt'");
    expect(mainSource).toContain("register(`/sw.js?v=${encodeURIComponent(ASSET_VERSION)}`, { scope: '/' })");
  });

  it('keeps the install action usable when the native browser prompt is unavailable or dismissed', () => {
    expect(installSource).toContain('const isAndroid = isAndroidInstallableDevice();');
    expect(installSource).toContain("choice.outcome === 'accepted'");
    expect(installSource).toContain("setButtonLabel('settings.behavior.showInstallSteps');");
    expect(installSource).toContain("'settings.behavior.installManualMessage'");
    expect(installSource).toContain('await showManualInstallSteps();');
  });
});

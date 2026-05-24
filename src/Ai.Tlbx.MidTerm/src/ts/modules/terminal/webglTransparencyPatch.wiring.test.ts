import { readFileSync } from 'node:fs';
import path from 'node:path';
import { describe, expect, it } from 'vitest';

const projectRoot = path.join(__dirname, '../../../..');
const webglPatch = readFileSync(
  path.join(projectRoot, 'patches/@xterm+addon-webgl+0.19.0.patch'),
  'utf8',
);
const managerSource = readFileSync(path.join(__dirname, 'manager.ts'), 'utf8');

function getPatchSection(filePath: string): string {
  const start = webglPatch.indexOf(`diff --git a/${filePath} `);
  if (start < 0) {
    return '';
  }

  const next = webglPatch.indexOf('\ndiff --git ', start + 1);
  return next < 0 ? webglPatch.slice(start) : webglPatch.slice(start, next);
}

describe('WebGL transparency vendor patch', () => {
  it('keeps screenshot-readable WebGL while replacing stale transparent frames', () => {
    expect(managerSource).toContain('new WebglAddon(true)');
    expect(webglPatch).toContain('alpha: true');
    expect(webglPatch).toContain('premultipliedAlpha: true');
    expect(webglPatch).toContain('this._clearFrame();');
    expect(webglPatch).toContain('gl.clearColor(0, 0, 0, 0);');
    expect(webglPatch).toContain('gl.disable(gl.BLEND);');
    expect(webglPatch).toContain('old glyph pixels decay across cursor-blink redraws');
  });

  it('premultiplies transparent rectangle colors for Chrome canvas compositing', () => {
    expect(webglPatch).toContain('outColor = vec4(v_color.rgb * v_color.a, v_color.a);');
  });

  it('keeps transparent glyph atlas edges alpha-correct over transparent cells', () => {
    expect(webglPatch).toContain(
      'gl.blendFuncSeparate(gl.SRC_ALPHA, gl.ONE_MINUS_SRC_ALPHA, gl.ONE, gl.ONE_MINUS_SRC_ALPHA);',
    );
    expect(webglPatch).toContain(
      'Preserve glyph edge alpha when compositing over transparent cell backgrounds.',
    );
    expect(webglPatch).not.toContain('shouldClearOpaqueRasterBackground');
    expect(webglPatch).not.toContain('Transparent canvas text rasterization breaks font smoothing');
  });

  it('keeps configurable box drawing glyph scale and style wired into WebGL', () => {
    const customGlyphSourcePatch = getPatchSection(
      'node_modules/@xterm/addon-webgl/src/CustomGlyphs.ts',
    );
    const webglCommonJsPatch = getPatchSection(
      'node_modules/@xterm/addon-webgl/lib/addon-webgl.js',
    );
    const webglModulePatch = getPatchSection('node_modules/@xterm/addon-webgl/lib/addon-webgl.mjs');

    for (const patchSection of [customGlyphSourcePatch, webglCommonJsPatch, webglModulePatch]) {
      expect(patchSection).toContain('__MIDTERM_XTERM_BOX_DRAWING_STROKE_SCALE__');
      expect(patchSection).toContain('__MIDTERM_XTERM_BOX_DRAWING_STYLE__');
    }

    expect(customGlyphSourcePatch).toContain('function getBoxDrawingStrokeScale');
    expect(customGlyphSourcePatch).toContain('function remapBoxDrawingChar');
    expect(customGlyphSourcePatch).toContain('Number.parseInt(fontWeight) * strokeScale');
  });

  it('keeps terminal text brightness boost foreground-only in WebGL', () => {
    const cellColorPatch = getPatchSection(
      'node_modules/@xterm/addon-webgl/src/CellColorResolver.ts',
    );
    const webglCommonJsPatch = getPatchSection(
      'node_modules/@xterm/addon-webgl/lib/addon-webgl.js',
    );
    const webglModulePatch = getPatchSection('node_modules/@xterm/addon-webgl/lib/addon-webgl.mjs');

    expect(cellColorPatch).toContain('__MIDTERM_XTERM_WEBGL_FG_ANSI__');
    expect(cellColorPatch).toContain('getTerminalForegroundAnsiRgba');
    expect(cellColorPatch).toContain('!(this.result.fg & FgFlags.INVERSE)');
    expect(cellColorPatch).toContain('$hasMidTermBoostedFg = true;');
    expect(webglCommonJsPatch).toContain('__MIDTERM_XTERM_WEBGL_FG_ANSI__');
    expect(webglModulePatch).toContain('__MIDTERM_XTERM_WEBGL_FG_ANSI__');
  });
});

import { describe, expect, it } from 'vitest';
import { buildSync } from 'esbuild';
import path from 'node:path';

// Exercise the patched dependency itself, including its hot-path DOM access.
const root = path.resolve('node_modules/@xterm');
const bundle = buildSync({
  entryPoints: [path.join(root, 'addon-webgl/src/CellColorResolver.ts')],
  alias: {
    browser: path.join(root, 'xterm/src/browser'),
    common: path.join(root, 'xterm/src/common'),
  },
  bundle: true,
  write: false,
  platform: 'node',
  format: 'cjs',
}).outputFiles[0].text;
const loaded = { exports: {} as Record<string, any> };
new Function('module', 'exports', bundle)(loaded, loaded.exports);
const { CellColorResolver } = loaded.exports;

describe('WebGL color settings per frame', () => {
  it('keeps RGB, palette, inverse and live setting changes while reading the DOM only once per frame', () => {
    let reads = 0;
    let view: Record<string, unknown> = { __MIDTERM_XTERM_FG_BOOST__: 50 };
    const terminal = {
      get element() {
        reads++;
        return { ownerDocument: { defaultView: view } };
      },
    };
    const resolver = new CellColorResolver(
      terminal,
      { rawOptions: { fontSize: 14 } },
      { isCellSelected: () => false },
      { forEachDecorationAtCell: () => {} },
      { dpr: 1 },
      { colors: { foreground: { rgba: 0xffffffff } } },
    );
    const cell = { bg: 0, fg: 0x03000000 | 0x102030, extended: {}, getCode: () => 65 };
    resolver.beginFrame();
    for (let i = 0; i < 1000; i++) resolver.resolve(cell, 0, 0, 8, 16);
    expect(reads).toBe(1);
    expect(resolver.result.fg & 0xffffff).toBe(0x889098);

    view.__MIDTERM_XTERM_FG_BOOST__ = 0;
    resolver.beginFrame();
    resolver.resolve(cell, 0, 0, 8, 16);
    expect(resolver.result.fg).toBe(cell.fg);

    // A document handoff must not retain the old window or its palette.
    view = { __MIDTERM_XTERM_FG_BOOST__: 50, __MIDTERM_XTERM_WEBGL_FG_ANSI__: [0x123456ff] };
    resolver.beginFrame();
    cell.fg = 0x01000000;
    resolver.resolve(cell, 0, 0, 8, 16);
    expect(resolver.result.fg & 0xffffff).toBe(0x123456);
    cell.fg |= 0x04000000; // inverse uses the original color rules
    resolver.resolve(cell, 0, 0, 8, 16);
    expect(resolver.result.fg).toBe(cell.fg);
    expect(reads).toBe(3);
  });
});

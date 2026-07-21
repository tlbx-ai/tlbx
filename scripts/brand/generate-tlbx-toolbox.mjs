#!/usr/bin/env node

import { mkdir, writeFile } from 'node:fs/promises';
import { dirname, isAbsolute, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';

const scriptDirectory = dirname(fileURLToPath(import.meta.url));
const repositoryRoot = resolve(scriptDirectory, '..', '..');

// Image-contract proportions, expressed as three shared projection vectors.
// Every corner, lid seam, body edge, flap anchor, eye, and side glyph derives
// from these values; independent points cannot drift apart.
const defaults = {
  stroke: 8,
  compactStroke: 22,
  gStroke: 8.5,
  compactGStroke: 22,
  compactGScale: 1.35,
  compactScale: 0.03655,
  compactOffsetX: 0.31,
  compactOffsetY: 0.49,
  eyeHalfWidth: 0.1,
  compactEyeHalfWidth: 0.135,
  eyeHalfHeight: 24,
  compactEyeHalfHeight: 48,
  pupilRadius: 9.5,
  compactPupilRadius: 16,
  pupilCoreRadius: 4.6,
  pupilGlowRadius: 7.4,
  compactPupilCoreRadius: 9,
  topbarPupilCoreRadius: 16,
  topbarPupilGlowRadius: 30,
  ink: '#f7f8fb',
  background: '#05070b',
  accent: '#69b7ff',
  websiteRoot: '',
  tileRadius: 70,
  originX: 12,
  originY: 98,
  widthX: 253,
  widthY: -91,
  depthX: 120,
  depthY: 62,
  bodyDrop: 230,
  lidBandDrop: 42,
  foldPosition: 0.53,
  foldDrop: 0.54,
  eyePosition: 0.53,
  gPositionX: 0.48,
  gPositionY: 0.48,
};

const numericKeys = new Set([
  'stroke',
  'compactStroke',
  'gStroke',
  'compactGStroke',
  'compactGScale',
  'compactScale',
  'compactOffsetX',
  'compactOffsetY',
  'eyeHalfWidth',
  'compactEyeHalfWidth',
  'eyeHalfHeight',
  'compactEyeHalfHeight',
  'pupilRadius',
  'compactPupilRadius',
  'pupilCoreRadius',
  'pupilGlowRadius',
  'compactPupilCoreRadius',
  'topbarPupilCoreRadius',
  'topbarPupilGlowRadius',
  'tileRadius',
  'originX',
  'originY',
  'widthX',
  'widthY',
  'depthX',
  'depthY',
  'bodyDrop',
  'lidBandDrop',
  'foldPosition',
  'foldDrop',
  'eyePosition',
  'gPositionX',
  'gPositionY',
]);

function parseArguments(argv) {
  const config = { ...defaults };
  for (let index = 0; index < argv.length; index += 1) {
    const token = argv[index];
    if (!token.startsWith('--')) continue;
    const [rawKey, inlineValue] = token.slice(2).split('=', 2);
    const key = rawKey.replace(/-([a-z])/g, (_, letter) => letter.toUpperCase());
    if (!(key in config)) throw new Error(`Unknown parameter --${rawKey}`);
    const value = inlineValue ?? argv[++index];
    if (value === undefined) throw new Error(`Missing value for --${rawKey}`);
    config[key] = numericKeys.has(key) ? Number(value) : value;
  }
  return config;
}

const add = ([x, y], [dx, dy]) => [x + dx, y + dy];
const subtract = ([x, y], [dx, dy]) => [x - dx, y - dy];
const scale = ([x, y], amount) => [x * amount, y * amount];
const lerp = ([ax, ay], [bx, by], amount) => [
  ax + (bx - ax) * amount,
  ay + (by - ay) * amount,
];
const point = ([x, y]) => `${Number(x.toFixed(3))} ${Number(y.toFixed(3))}`;

function geometry(config) {
  const origin = [config.originX, config.originY];
  const width = [config.widthX, config.widthY];
  const depth = [config.depthX, config.depthY];
  const vertical = [0, config.bodyDrop];
  const band = [0, config.lidBandDrop];

  const back = add(origin, width);
  const front = add(origin, depth);
  const right = add(back, depth);
  const lowerOrigin = add(origin, band);
  const lowerFront = add(front, band);
  const lowerRight = add(right, band);
  const fold = add(
    lerp(lowerFront, lowerRight, config.foldPosition),
    scale(vertical, config.foldDrop),
  );

  return {
    origin,
    back,
    front,
    right,
    lowerOrigin,
    lowerFront,
    lowerRight,
    bottomOrigin: add(origin, vertical),
    bottomFront: add(front, vertical),
    bottomRight: add(right, vertical),
    fold,
    width,
    depth,
    vertical,
  };
}

function eyeMarkup(config, geometry, variant) {
  const compact = variant !== 'detail';
  const center = add(
    lerp(geometry.front, geometry.right, config.eyePosition),
    [0, config.lidBandDrop / 2],
  );
  const halfWidth = compact ? config.compactEyeHalfWidth : config.eyeHalfWidth;
  const halfHeight = compact ? config.compactEyeHalfHeight : config.eyeHalfHeight;
  const pupilRadius = compact ? config.compactPupilRadius : config.pupilRadius;
  const pupilCoreRadius =
    variant === 'favicon'
      ? config.compactPupilCoreRadius
      : variant === 'topbar'
        ? config.topbarPupilCoreRadius
        : config.pupilCoreRadius;
  const pupilGlowRadius =
    variant === 'favicon'
      ? 0
      : variant === 'topbar'
        ? config.topbarPupilGlowRadius
        : config.pupilGlowRadius;
  const eyeVector = scale(geometry.width, halfWidth);
  const left = subtract(center, eyeVector);
  const right = add(center, eyeVector);
  const top = add(center, [0, -halfHeight]);
  const bottom = add(center, [0, halfHeight]);

  const centerX = Number(center[0].toFixed(3));
  const centerY = Number(center[1].toFixed(3));
  const glowMarkup = pupilGlowRadius
    ? `\n    <circle cx="${centerX}" cy="${centerY}" r="${pupilGlowRadius}" fill="url(#tlbx-pupil-glow)"/>`
    : '';

  return `<path d="M ${point(left)} Q ${point(top)} ${point(right)} Q ${point(bottom)} ${point(left)} Z" fill="var(--tlbx-ink)"/>
    <circle cx="${centerX}" cy="${centerY}" r="${pupilRadius}" fill="var(--tlbx-background)"/>${glowMarkup}
    <circle cx="${centerX}" cy="${centerY}" r="${pupilCoreRadius}" fill="var(--tlbx-accent)"/>`;
}

function gMarkup(config, geometry, compact) {
  const center = add(
    add(geometry.origin, scale(geometry.depth, config.gPositionX)),
    scale(geometry.vertical, config.gPositionY),
  );
  const opticalScale = compact ? config.compactGScale : 1;
  const faceSlope = config.depthY / config.depthX;
  const project = ([x, y]) => [
    center[0] + x * opticalScale,
    center[1] + (x * faceSlope + y) * opticalScale,
  ];
  const p = {
    start: project([25, -10]),
    c11: project([15, -21]),
    c12: project([-4, -23]),
    p1: project([-18, -10]),
    c21: project([-31, 1]),
    c22: project([-28, 15]),
    p2: project([-16, 19]),
    c31: project([-3, 27]),
    c32: project([16, 23]),
    p3: project([25, 10]),
    upright: project([25, 0]),
    bar: project([7, 0]),
  };
  const stroke = compact ? config.compactGStroke : config.gStroke;

  return `<path d="M ${point(p.start)} C ${point(p.c11)} ${point(p.c12)} ${point(p.p1)} C ${point(p.c21)} ${point(p.c22)} ${point(p.p2)} C ${point(p.c31)} ${point(p.c32)} ${point(p.p3)} L ${point(p.upright)} L ${point(p.bar)}" fill="none" stroke="var(--tlbx-ink)" stroke-width="${stroke}" stroke-linecap="round" stroke-linejoin="round"/>`;
}

function markMarkup(config, { compact = false, pupilVariant = compact ? 'topbar' : 'detail' } = {}) {
  const g = geometry(config);
  const stroke = compact ? config.compactStroke : config.stroke;
  const outer = [g.origin, g.back, g.right, g.bottomRight, g.bottomFront, g.bottomOrigin];

  return `<g fill="none" stroke="var(--tlbx-ink)" stroke-width="${stroke}" stroke-linecap="round" stroke-linejoin="round">
      <path d="M ${outer.map(point).join(' L ')} Z"/>
      <path d="M ${point(g.origin)} L ${point(g.front)} L ${point(g.right)}"/>
      <path d="M ${point(g.lowerOrigin)} L ${point(g.lowerFront)} L ${point(g.lowerRight)}"/>
      <path d="M ${point(g.front)} L ${point(g.bottomFront)}"/>
      <path d="M ${point(g.lowerFront)} L ${point(g.fold)} L ${point(g.lowerRight)}"/>
    </g>
    ${eyeMarkup(config, g, pupilVariant)}
    ${gMarkup(config, g, compact)}`;
}

function svgDocument(config, options = {}) {
  const {
    compact = false,
    foreground = false,
    tile = false,
    pupilVariant = compact ? 'topbar' : 'detail',
    title = 'tlbx toolbox mark',
  } = options;
  const viewBox = compact ? '0 0 16 16' : foreground ? '0 0 512 512' : '0 0 400 400';
  const tileMarkup =
    tile === 'rounded'
      ? compact
        ? '  <rect width="16" height="16" rx="3" fill="var(--tlbx-background)"/>\n'
        : `  <rect width="400" height="400" rx="${config.tileRadius}" fill="var(--tlbx-background)"/>\n`
      : tile === 'full'
        ? compact
          ? '  <rect width="16" height="16" fill="var(--tlbx-background)"/>\n'
          : '  <rect width="400" height="400" fill="var(--tlbx-background)"/>\n'
        : '';
  const markTransform = foreground
    ? 'translate(96 96) scale(.8)'
    : compact
    ? `translate(${config.compactOffsetX} ${config.compactOffsetY}) scale(${config.compactScale})`
    : tile !== false && !compact
      ? 'translate(20 20) scale(.9)'
      : '';
  const transformAttribute = markTransform ? ` transform="${markTransform}"` : '';
  const description =
    'A long isometric toolbox with a closed lid, eye, front chevron, and G-shaped side handle.';
  const defsMarkup = `<radialGradient id="tlbx-pupil-glow">
      <stop offset="0" stop-color="var(--tlbx-accent)" stop-opacity="0.72"/>
      <stop offset="0.48" stop-color="var(--tlbx-accent)" stop-opacity="0.28"/>
      <stop offset="1" stop-color="var(--tlbx-accent)" stop-opacity="0"/>
    </radialGradient>`;

  return `<svg xmlns="http://www.w3.org/2000/svg" width="100%" height="100%" viewBox="${viewBox}" role="img" aria-labelledby="title desc" style="--tlbx-ink:${config.ink};--tlbx-background:${config.background};--tlbx-accent:${config.accent}">
  <title id="title">${title}</title>
  <desc id="desc">${description}</desc>
  <defs>
    ${defsMarkup}
  </defs>
${tileMarkup}  <g${transformAttribute}>
    ${markMarkup(config, { compact, pupilVariant })}
  </g>
</svg>
`;
}

const config = parseArguments(process.argv.slice(2));
const outputs = [
  ['src/Ai.Tlbx.MidTerm/src/static/img/tlbx-toolbox.svg', svgDocument(config)],
  [
    'src/Ai.Tlbx.MidTerm/src/static/img/tlbx-toolbox-foreground.svg',
    svgDocument(config, { foreground: true, title: 'tlbx adaptive icon foreground' }),
  ],
  [
    'src/Ai.Tlbx.MidTerm/src/static/img/tlbx-toolbox-compact.svg',
    svgDocument(config, { compact: true, pupilVariant: 'topbar', title: 'tlbx topbar toolbox mark' }),
  ],
  [
    'src/Ai.Tlbx.MidTerm/src/static/favicon.svg',
    svgDocument(config, {
      compact: true,
      tile: 'rounded',
      pupilVariant: 'topbar',
      title: 'tlbx favicon',
    }),
  ],
  [
    'src/Ai.Tlbx.MidTerm/src/static/favicon-large.svg',
    svgDocument(config, { tile: 'full', title: 'tlbx large app icon' }),
  ],
  [
    'src/Ai.Tlbx.MidTerm/src/static/favicon-opaque.svg',
    svgDocument(config, {
      compact: true,
      tile: 'full',
      pupilVariant: 'topbar',
      title: 'tlbx small app icon',
    }),
  ],
];

if (config.websiteRoot) {
  outputs.push(
    [
      resolve(config.websiteRoot, 'media', 'tlbx-mark.svg'),
      svgDocument(config, {
        compact: true,
        pupilVariant: 'topbar',
        title: 'tlbx navigation toolbox mark',
      }),
    ],
    [
      resolve(config.websiteRoot, 'favicon.svg'),
      svgDocument(config, {
        compact: true,
        tile: 'rounded',
        pupilVariant: 'topbar',
        title: 'tlbx favicon',
      }),
    ],
  );
}

for (const [path, contents] of outputs) {
  const outputPath = isAbsolute(path) ? path : resolve(repositoryRoot, path);
  await mkdir(dirname(outputPath), { recursive: true });
  await writeFile(outputPath, contents, 'utf8');
  console.log(outputPath);
}

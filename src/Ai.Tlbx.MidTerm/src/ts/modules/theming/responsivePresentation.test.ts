import { afterEach, describe, expect, it, vi } from 'vitest';
import { isMobileViewport } from '../smartInput/smartInputMetrics';
import { resolveEffectiveScrollbarStyle } from '../terminal/scrollbarStyle';
import { isMobilePresentationContext } from '../theming/backgroundVisibility';
import { shouldShowTouchController } from '../touchController/detection';

afterEach(() => vi.unstubAllGlobals());

describe('responsive presentation across browser capabilities', () => {
  for (const width of [768, 769]) {
    for (const touch of [false, true]) {
      for (const dpr of [3]) {
        it(`keeps presentation consistent at ${width}px, DPR ${dpr}, touch ${touch}`, () => {
          vi.stubGlobal('navigator', { maxTouchPoints: touch ? 5 : 0 });
          vi.stubGlobal('window', {
            innerWidth: width,
            devicePixelRatio: dpr,
            matchMedia: (query: string) => ({
              matches: query.includes('max-width')
                ? width <= Number(query.match(/\d+/)?.[0])
                : query.includes('min-width')
                  ? width >= Number(query.match(/\d+/)?.[0])
                  : query.includes('coarse')
                    ? touch
                    : !touch,
            }),
          });
          expect(isMobileViewport()).toBe(width <= 768);
          expect(isMobilePresentationContext()).toBe(width <= 768);
          expect(shouldShowTouchController()).toBe(width <= 768);
          expect(resolveEffectiveScrollbarStyle('hover')).toBe(width <= 768 ? 'always' : 'hover');
        });
      }
    }
  }
});

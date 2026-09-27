import { afterAll, beforeAll, describe, expect, it } from 'vitest';

import { formatBinaryDump, highlightCode } from './rendering';

const originalDocument = globalThis.document;

function escapeForTest(text: string): string {
  return text
    .replaceAll('&', '&amp;')
    .replaceAll('<', '&lt;')
    .replaceAll('>', '&gt;')
    .replaceAll('"', '&quot;')
    .replaceAll("'", '&#39;');
}

describe('fileViewer rendering', () => {
  beforeAll(() => {
    Object.assign(globalThis, {
      document: {
        createElement: () => {
          let text = '';
          return {
            set textContent(value: string) {
              text = value ?? '';
            },
            get textContent(): string {
              return text;
            },
            get innerHTML(): string {
              return escapeForTest(text);
            },
          };
        },
      },
    });
  });

  afterAll(() => {
    Object.assign(globalThis, {
      document: originalDocument,
    });
  });

  it('can render binary dumps from a non-zero byte offset', () => {
    const dump = formatBinaryDump(new Uint8Array([0xaa, 0xbb]), 0x20);

    expect(dump).toMatch(/^00000020  AA BB/);
  });

  it('does not re-highlight generated markup inside comment spans', () => {
    const highlighted = highlightCode(
      '# Wazuh - SIEM/XDR manager + dashboard\n# See: IT Notes/Projects/Wazuh Security/Overview.md',
      '.tf',
    );

    expect(highlighted).toContain(
      '<span class="hl-comment"># Wazuh - SIEM/XDR manager + dashboard</span>',
    );
    expect(highlighted).not.toContain('<span <span');
    expect(highlighted).not.toContain('hl-keyword">class</span>');
  });

  it('keeps literal html snippets escaped as text content', () => {
    const highlighted = highlightCode('<span class="hl-comment"># nope</span>', '.html');

    expect(highlighted).toContain('&lt;span');
    expect(highlighted).toContain('&lt;/span&gt;');
    expect(highlighted).not.toContain('<span class="hl-comment"># nope</span>');
  });
});

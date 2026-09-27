import { describe, expect, it } from 'vitest';

import { renderMarkdown } from './markdown';

describe('renderMarkdown', () => {
  it('only renders safe markdown links as anchors', () => {
    const safe = renderMarkdown('[OpenAI](https://openai.com)');
    const unsafe = renderMarkdown('[Oops](javascript:alert(1))');

    expect(safe).toContain('<a href="https://openai.com"');
    expect(unsafe).not.toContain('<a href=');
    expect(unsafe).toContain('Oops');
  });

  it('falls back to a code block when a csv fence is malformed', () => {
    const html = renderMarkdown('```csv\nName,Notes\nAlpha,\"unterminated\n```');

    expect(html).toContain('<pre class="agent-markdown-pre"><code data-language="csv">');
    expect(html).not.toContain('data-table-source="csv"');
  });

  it('does not treat underscores inside plain tokens as emphasis', () => {
    const html = renderMarkdown('HELLO_FROM_CODEX\n\nTOOL_DONE');

    expect(html).toContain('<p>HELLO_FROM_CODEX</p>');
    expect(html).toContain('<p>TOOL_DONE</p>');
    expect(html).not.toContain('<em>');
  });
});

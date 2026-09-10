import { describe, expect, it } from 'vitest';
import { escapeHtml } from './dom';

describe('escapeHtml without DOM allocation', () => {
  it('escapes text and quoted attributes without needing a document', () => {
    expect(escapeHtml('<tag a="x" b=\'y\'>&')).toBe(
      '&lt;tag a=&quot;x&quot; b=&#39;y&#39;&gt;&amp;',
    );
    expect(escapeHtml('plain Unicode: ä ✓')).toBe('plain Unicode: ä ✓');
    expect(escapeHtml('&amp;')).toBe('&amp;amp;');
  });
});

import { describe, expect, it, vi } from 'vitest';
import { renderMarkdown, renderMarkdownFragment } from '../../utils/markdown';
import {
  draftFromRichDocument,
  getRichComposerDocument,
  richComposerSchema,
  richNode,
} from '../smartInput/richComposerDocument';
import { prepareSmartInputOutboundPrompt } from '../smartInput/smartInputOutboundReferences';
import { resolveHistoryBodyPresentation } from './historyContent';
import { getCachedHistoryMarkdown, pruneHistoryMarkdownCache } from './historyMarkdown';
import { buildAssistantEnrichedHtml } from './assistantEnrichment';
import type { AppServerControlHistoryEntry, HistoryMarkdownCacheEntry } from './types';

vi.mock('../i18n', () => ({ t: (key: string) => key }));
vi.mock('./assistantEnrichment', () => ({
  buildAssistantEnrichedHtml: vi.fn((html: string) => ({ html: `<aside>${html}</aside>` })),
}));

function entry(
  kind: AppServerControlHistoryEntry['kind'],
  body: string,
): AppServerControlHistoryEntry {
  return { id: 'turn', kind, order: 1, tone: 'info', label: '', title: '', body, meta: '' };
}

async function outbound(doc: ReturnType<typeof getRichComposerDocument>) {
  return prepareSmartInputOutboundPrompt({
    sessionId: 'own-session',
    draft: draftFromRichDocument({ parts: [], nextOrdinalByKind: {} }, doc),
    attachments: [],
    uploadFailureMessage: 'Upload failed',
    attachmentReadFailureMessage: 'Read failed',
    uploadFile: async () => {
      throw new Error('Text projection must not upload');
    },
  });
}

function paragraph(text: string, code = false) {
  return richNode('paragraph').create(
    null,
    richComposerSchema.text(text, code ? [richComposerSchema.marks.code!.create()] : []),
  );
}

describe('rich user input to history', () => {
  it.each(['# literal', '- literal', '> literal', '1. literal'])(
    'does not promote a literal paragraph to block markup: %s',
    async (text) => {
      const doc = richNode('doc').create(null, paragraph(text));
      const html = renderMarkdownFragment((await outbound(doc)).text);
      expect(html).toBe(text.replace('>', '&gt;'));
    },
  );
  it.each([
    'a|b',
    String.raw`a\|b`,
    String.raw`a\\|b`,
    String.raw`C:\temp\file`,
    '*literal*',
    '<script>alert("x")</script>',
  ])('retains a literal one-column cell through send and history: %s', async (text) => {
    const doc = richNode('doc').create(
      null,
      richNode('table').create(null, [
        richNode('table_row').create(
          null,
          richNode('table_header').create(null, paragraph('Header')),
        ),
        richNode('table_row').create(null, richNode('table_cell').create(null, paragraph(text))),
      ]),
    );
    const result = await outbound(doc);
    const user = entry('user', result.text);
    expect(resolveHistoryBodyPresentation(user).mode).toBe('markdown');
    const html = getCachedHistoryMarkdown(undefined, user).html;
    const escaped = text
      .replaceAll('&', '&amp;')
      .replaceAll('<', '&lt;')
      .replaceAll('>', '&gt;')
      .replaceAll('"', '&quot;')
      .replaceAll("'", '&#39;');
    expect([...html.matchAll(/<td\b[^>]*>(.*?)<\/td>/g)].map((match) => match[1])).toEqual([
      escaped,
    ]);
    expect(html).not.toContain('<script>');
    expect(result.attachments).toEqual([]);
  });

  it.each([
    'a|b',
    String.raw`a\|b`,
    String.raw`a\\|b`,
    '**literal**',
    '`tick`',
    '<img src=x onerror=alert(1)>',
  ])('preserves code semantics inside a projected table cell: %s', async (text) => {
    const doc = richNode('doc').create(
      null,
      richNode('table').create(null, [
        richNode('table_row').create(
          null,
          richNode('table_header').create(null, paragraph('Code')),
        ),
        richNode('table_row').create(
          null,
          richNode('table_cell').create(null, paragraph(text, true)),
        ),
      ]),
    );
    const result = await outbound(doc);
    const html = renderMarkdownFragment(result.text);
    const escaped = text.replaceAll('<', '&lt;').replaceAll('>', '&gt;');
    expect([...html.matchAll(/<td\b[^>]*>(.*?)<\/td>/g)].map((match) => match[1])).toEqual([
      `<code>${escaped}</code>`,
    ]);
  });

  it('keeps marks, safe links and fenced code without interpreting literal HTML or code marks', async () => {
    const doc = getRichComposerDocument({
      parts: [
        {
          kind: 'text',
          text: '**bold** and *em* [link](https://example.test/)\n\n```js\nconst x = "<tag> **literal**";\n```\n\n<script>alert(1)</script>',
        },
      ],
      nextOrdinalByKind: {},
    });
    const result = await outbound(doc);
    const html = getCachedHistoryMarkdown(undefined, entry('user', result.text)).html;
    expect(html).toContain('<strong>bold</strong>');
    expect(html).toContain('<em>em</em>');
    expect(html).toContain('href="https://example.test/"');
    expect(html).toContain('const x = &quot;&lt;tag&gt; **literal**&quot;;');
    expect(html).toContain('&lt;script&gt;alert(1)&lt;/script&gt;');
    expect(html).not.toContain('<script>');
    expect(renderMarkdown('[unsafe](javascript:alert)')).not.toContain('href=');
  });

  it('retains multiple columns, alignment and the existing CSV projection', () => {
    const html = renderMarkdown('| A | B |\n| :--- | ---: |\n| x\\|y | z |');
    expect([...html.matchAll(/<td\b[^>]*>(.*?)<\/td>/g)].map((match) => match[1])).toEqual([
      'x|y',
      'z',
    ]);
    expect(html).toContain('data-align="right"');
    const csv = renderMarkdown('```csv\nName,Value\n"a|b",2\n```');
    expect(csv).toContain('data-table-source="csv"');
    expect([...csv.matchAll(/<td\b[^>]*>(.*?)<\/td>/g)].map((match) => match[1])).toEqual([
      'a|b',
      '2',
    ]);
  });

  it('projects marked cells and safe links without promoting literal header HTML', async () => {
    const doc = getRichComposerDocument({
      parts: [
        {
          kind: 'text',
          text: '| <img src=x onerror=alert(1)> |\n| --- |\n| **bold** and *em* [safe](https://example.test/) |',
        },
      ],
      nextOrdinalByKind: {},
    });
    const html = getCachedHistoryMarkdown(
      undefined,
      entry('user', (await outbound(doc)).text),
    ).html;
    expect(html).toContain('<strong>bold</strong>');
    expect(html).toContain('<em>em</em>');
    expect(html).toContain('href="https://example.test/"');
    expect(html).toContain('&lt;img src=x onerror=alert(1)&gt;');
    expect(html).not.toContain('<img');
  });
});

describe('role-scoped bounded history Markdown cache', () => {
  it('cannot reuse assistant decorations after a row changes to user', () => {
    const cache = new Map<string, HistoryMarkdownCacheEntry>();
    const assistant = entry('assistant', '**same**');
    expect(getCachedHistoryMarkdown(cache, assistant).html).toContain('<aside>');
    const calls = vi.mocked(buildAssistantEnrichedHtml).mock.calls.length;
    const user = entry('user', assistant.body);
    expect(getCachedHistoryMarkdown(cache, user).html).toBe('<strong>same</strong>');
    expect(buildAssistantEnrichedHtml).toHaveBeenCalledTimes(calls);
    expect(getCachedHistoryMarkdown(cache, user)).toBe(cache.get(user.id));
    expect(getCachedHistoryMarkdown(cache, { ...user, body: '*changed*' }).html).toBe(
      '<em>changed</em>',
    );
  });

  it('invalidates source kind and prunes every role against the retained history window', () => {
    const cache = new Map<string, HistoryMarkdownCacheEntry>();
    const user = entry('user', 'user');
    const assistant = { ...entry('assistant', 'assistant'), id: 'assistant' };
    const plan = { ...entry('plan', 'plan'), id: 'plan', sourceItemType: 'proposed_plan' };
    const first = getCachedHistoryMarkdown(cache, user);
    expect(getCachedHistoryMarkdown(cache, { ...user, sourceItemType: 'user_message' })).not.toBe(
      first,
    );
    getCachedHistoryMarkdown(cache, assistant);
    getCachedHistoryMarkdown(cache, plan);
    pruneHistoryMarkdownCache(cache, [user, plan]);
    expect([...cache.keys()]).toEqual(['turn', 'plan']);
    pruneHistoryMarkdownCache(cache, [entry('tool', 'replaced')]);
    expect([...cache.keys()]).toEqual([]);
  });
});
